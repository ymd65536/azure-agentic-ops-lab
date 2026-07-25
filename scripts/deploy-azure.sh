#!/usr/bin/env bash
# Provisions the Azure environment (AKS + Dapr extension + backing services),
# pushes the container images, and deploys the workloads with the Azure Dapr
# components. Local execution (k3d / in-process) is unaffected by this script.
#
# Prerequisites: az CLI logged in, kubectl, and an existing resource group.
#
# Usage:
#   RESOURCE_GROUP=<rg> NAME_PREFIX=<prefix> scripts/deploy-azure.sh
set -euo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:?Set RESOURCE_GROUP to the target resource group}"
NAME_PREFIX="${NAME_PREFIX:?Set NAME_PREFIX to the resource name prefix (3-12 lowercase alphanumerics)}"
IMAGE_TAG="${IMAGE_TAG:-azure}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "==> Provisioning infrastructure (infra/main.bicep)"
az deployment group create \
  --resource-group "${RESOURCE_GROUP}" \
  --template-file "${REPO_ROOT}/infra/main.bicep" \
  --parameters namePrefix="${NAME_PREFIX}" \
  --output none

outputs="$(az deployment group show \
  --resource-group "${RESOURCE_GROUP}" \
  --name main \
  --query properties.outputs \
  --output json)"

CLUSTER_NAME="$(echo "${outputs}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["clusterName"]["value"])')"
ACR_LOGIN_SERVER="$(echo "${outputs}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["acrLoginServer"]["value"])')"
KEY_VAULT_NAME="$(echo "${outputs}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["keyVaultName"]["value"])')"
SERVICE_BUS_HOSTNAME="$(echo "${outputs}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["serviceBusHostName"]["value"])')"
STORAGE_ACCOUNT_NAME="$(echo "${outputs}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["storageAccountName"]["value"])')"
WORKLOAD_CLIENT_ID="$(echo "${outputs}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["workloadIdentityClientId"]["value"])')"

echo "==> Building images in ACR (${ACR_LOGIN_SERVER})"
ACR_NAME="${ACR_LOGIN_SERVER%%.*}"
az acr build --registry "${ACR_NAME}" --image "incident-api:${IMAGE_TAG}" \
  --file "${REPO_ROOT}/src/IncidentApi/Dockerfile" "${REPO_ROOT}"
az acr build --registry "${ACR_NAME}" --image "ops-console:${IMAGE_TAG}" \
  --file "${REPO_ROOT}/src/OpsConsole/Dockerfile" "${REPO_ROOT}"
az acr build --registry "${ACR_NAME}" --image "scribe-service:${IMAGE_TAG}" \
  --file "${REPO_ROOT}/src/ScribeService/Dockerfile" "${REPO_ROOT}"

echo "==> Fetching AKS credentials"
az aks get-credentials --resource-group "${RESOURCE_GROUP}" --name "${CLUSTER_NAME}" --overwrite-existing

echo "==> Applying namespace and Azure Dapr components"
kubectl apply -f "${REPO_ROOT}/deploy/local/namespace.yaml"
for f in "${REPO_ROOT}/deploy/azure/dapr-components"/*.yaml; do
  sed \
    -e "s|<SERVICE_BUS_HOSTNAME>|${SERVICE_BUS_HOSTNAME}|g" \
    -e "s|<STORAGE_ACCOUNT_NAME>|${STORAGE_ACCOUNT_NAME}|g" \
    -e "s|<KEY_VAULT_NAME>|${KEY_VAULT_NAME}|g" \
    -e "s|<WORKLOAD_CLIENT_ID>|${WORKLOAD_CLIENT_ID}|g" \
    "$f" | kubectl apply -f -
done

echo "==> Deploying workloads (ACR images, workload identity enabled)"
for manifest in incident-api ops-console scribe-service; do
  sed \
    -e "s|image: ${manifest}:local|image: ${ACR_LOGIN_SERVER}/${manifest}:${IMAGE_TAG}|" \
    -e "s|imagePullPolicy: IfNotPresent|imagePullPolicy: Always|" \
    "${REPO_ROOT}/deploy/local/${manifest}.yaml" | kubectl apply -f -
  kubectl annotate serviceaccount "${manifest}" --namespace agentic-ops --overwrite \
    "azure.workload.identity/client-id=${WORKLOAD_CLIENT_ID}"
  kubectl patch deployment "${manifest}" --namespace agentic-ops --type merge \
    --patch '{"spec":{"template":{"metadata":{"labels":{"azure.workload.identity/use":"true"}}}}}'
done

kubectl rollout status deployment/incident-api --namespace agentic-ops --timeout 300s
kubectl rollout status deployment/ops-console --namespace agentic-ops --timeout 300s
kubectl rollout status deployment/scribe-service --namespace agentic-ops --timeout 300s

echo "Azure deployment complete."
echo "Forward the API locally with:"
echo "  kubectl port-forward --namespace agentic-ops service/incident-api 8080:80"
