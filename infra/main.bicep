// Azure infrastructure for the Azure Agentic Ops Lab.
//
// Provisions the platform the local Kubernetes setup maps onto:
//   * Azure Kubernetes Service with the Dapr cluster extension and
//     Microsoft Entra Workload ID enabled
//   * Azure Container Registry for the service images
//   * Azure Service Bus (topics) backing the logical `incident-pubsub` component
//   * Azure Storage (tables) backing the logical `incident-state` component
//   * Azure Key Vault backing the logical `secret-store` component
//   * Log Analytics + Azure Monitor for containers
//   * A user-assigned managed identity federated to the application service
//     accounts so pods authenticate with DefaultAzureCredential and no keys
//
// No secrets, subscription identifiers, or tenant identifiers are stored in
// this file; everything environment-specific arrives as a parameter.
//
// Deploy with:
//   az deployment group create \
//     --resource-group <rg> \
//     --template-file infra/main.bicep \
//     --parameters namePrefix=<prefix>

@description('Prefix applied to every resource name. Lowercase letters and digits only.')
@minLength(3)
@maxLength(12)
param namePrefix string

@description('Azure region for all resources. Defaults to the resource group region.')
param location string = resourceGroup().location

@description('Kubernetes version for the AKS cluster. Empty selects the regional default.')
param kubernetesVersion string = ''

@description('Node count of the system node pool.')
@minValue(1)
@maxValue(10)
param nodeCount int = 2

@description('VM size of the system node pool.')
param nodeVmSize string = 'Standard_D2s_v5'

@description('Kubernetes namespace the workloads run in. Must match deploy/azure manifests.')
param workloadNamespace string = 'agentic-ops'

@description('Kubernetes service accounts federated to the workload identity.')
param workloadServiceAccounts array = [
  'incident-api'
  'scribe-service'
]

var suffix = uniqueString(resourceGroup().id)
var clusterName = '${namePrefix}-aks'
var acrName = toLower('${namePrefix}acr${suffix}')
var keyVaultName = toLower('${take(namePrefix, 9)}kv${take(suffix, 13)}')
var serviceBusNamespaceName = toLower('${namePrefix}-sb-${suffix}')
var storageAccountName = toLower('${take(namePrefix, 9)}st${take(suffix, 13)}')
var logAnalyticsName = '${namePrefix}-logs'
var workloadIdentityName = '${namePrefix}-workload-identity'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

resource aksCluster 'Microsoft.ContainerService/managedClusters@2024-09-01' = {
  name: clusterName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    dnsPrefix: '${namePrefix}-aks'
    kubernetesVersion: empty(kubernetesVersion) ? null : kubernetesVersion
    enableRBAC: true
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
    agentPoolProfiles: [
      {
        name: 'system'
        mode: 'System'
        count: nodeCount
        vmSize: nodeVmSize
        osType: 'Linux'
        osSKU: 'AzureLinux'
      }
    ]
    addonProfiles: {
      omsagent: {
        enabled: true
        config: {
          logAnalyticsWorkspaceResourceID: logAnalytics.id
        }
      }
    }
  }
}

// Dapr runs as an AKS cluster extension so sidecar injection and the workflow
// engine are managed by the platform instead of a manual Helm install.
resource daprExtension 'Microsoft.KubernetesConfiguration/extensions@2023-05-01' = {
  name: 'dapr'
  scope: aksCluster
  properties: {
    extensionType: 'Microsoft.Dapr'
    autoUpgradeMinorVersion: true
    scope: {
      cluster: {
        releaseNamespace: 'dapr-system'
      }
    }
  }
}

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: serviceBusNamespaceName
  location: location
  sku: {
    name: 'Standard'
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }
}

// The lifecycle topic consumed by ScribeService. Dapr creates per-consumer
// subscriptions itself when allowed, so none are pre-created here.
resource incidentLifecycleTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'incident-lifecycle'
  properties: {
    defaultMessageTimeToLive: 'P1D'
  }
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: tenant().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    sku: {
      family: 'A'
      name: 'standard'
    }
  }
}

// Workload identity used by the application pods (DefaultAzureCredential path).
resource workloadIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: workloadIdentityName
  location: location
}

resource federatedCredentials 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = [
  for serviceAccount in workloadServiceAccounts: {
    parent: workloadIdentity
    name: 'fc-${serviceAccount}'
    properties: {
      issuer: aksCluster.properties.oidcIssuerProfile.issuerURL
      subject: 'system:serviceaccount:${workloadNamespace}:${serviceAccount}'
      audiences: [
        'api://AzureADTokenExchange'
      ]
    }
  }
]

// Role assignments: least privilege per building block.
var acrPullRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
var keyVaultSecretsUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var serviceBusDataOwnerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '090c5cfd-751d-490a-894a-3ce6f1109419')
var storageTableDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')

resource kubeletAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, aksCluster.id, acrPullRoleId)
  scope: containerRegistry
  properties: {
    roleDefinitionId: acrPullRoleId
    principalId: aksCluster.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
  }
}

resource workloadKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, workloadIdentity.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: keyVaultSecretsUserRoleId
    principalId: workloadIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource workloadServiceBusAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, workloadIdentity.id, serviceBusDataOwnerRoleId)
  scope: serviceBusNamespace
  properties: {
    roleDefinitionId: serviceBusDataOwnerRoleId
    principalId: workloadIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource workloadStorageAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, workloadIdentity.id, storageTableDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: storageTableDataContributorRoleId
    principalId: workloadIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output clusterName string = aksCluster.name
output acrLoginServer string = containerRegistry.properties.loginServer
output keyVaultName string = keyVault.name
output serviceBusHostName string = '${serviceBusNamespace.name}.servicebus.windows.net'
output storageAccountName string = storageAccount.name
output workloadIdentityClientId string = workloadIdentity.properties.clientId
output oidcIssuerUrl string = aksCluster.properties.oidcIssuerProfile.issuerURL
