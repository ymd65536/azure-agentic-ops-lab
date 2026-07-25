# Azure（AKS）デプロイ

ローカル（k3d / in-process）での動作確認は Azure なしで従来どおり可能です。本書は、同じシステムを Azure Kubernetes Service 上で動かすための手順を説明します。

## 構成

`infra/main.bicep` が以下をプロビジョニングします。ローカル環境と Azure 環境で Dapr の論理コンポーネント名（`incident-pubsub` / `incident-state` / `secret-store`）は変わらず、下位実装だけが置き換わります。

| 論理名 / 役割 | ローカル実装 | Azure 実装 |
| --- | --- | --- |
| `incident-pubsub` | Redis Streams | Azure Service Bus（topics） |
| `incident-state` | Redis | Azure Table Storage |
| `secret-store` | Kubernetes Secrets / env | Azure Key Vault |
| クラスター | k3d / kind | AKS + Dapr クラスター拡張 |
| 認証 | なし（開発値） | Microsoft Entra Workload ID（キーレス） |
| 監視 | OTLP（オプトイン） | Azure Monitor / Log Analytics |

認証はすべて Microsoft Entra Workload ID（`DefaultAzureCredential` 互換）で行い、接続文字列やキーはどこにも保存しません。Service Bus と Storage はローカル認証（共有キー）を無効化しています。

## 前提

* `az` CLI（ログイン済み）、`kubectl`
* 既存のリソースグループ
* サブスクリプションで `Microsoft.KubernetesConfiguration`（Dapr 拡張用）が登録済みであること

```bash
az provider register --namespace Microsoft.KubernetesConfiguration
```

## デプロイ手順

```bash
# 1. インフラ + イメージビルド + ワークロードデプロイを一括実行
RESOURCE_GROUP=<resource-group> NAME_PREFIX=<prefix> scripts/deploy-azure.sh
```

スクリプトは次を行います。

1. `infra/main.bicep` をデプロイ（AKS、Dapr 拡張、ACR、Service Bus、Storage、Key Vault、Log Analytics、Workload Identity とロール割り当て）
2. `az acr build` で 3 つのイメージ（incident-api / ops-console / scribe-service）をビルド
3. `deploy/azure/dapr-components/` のプレースホルダーをデプロイ出力で置換して適用
4. `deploy/local/` のワークロードマニフェストを ACR イメージ + Workload Identity 付きで適用

## 実 LLM（Microsoft Foundry）を使う場合

IncidentApi の `AgentRuntime` セクションを設定します（既定は `Deterministic` のままで、リモート呼び出しは発生しません）。

```bash
kubectl set env deployment/incident-api --namespace agentic-ops \
  AgentRuntime__Mode=Shadow \
  AgentRuntime__RemoteModel__Endpoint=https://<your-endpoint> \
  AgentRuntime__RemoteModel__ModelId=<deployment-or-model-id>
```

* `AuthMode=DefaultAzureCredential`（既定）: Workload ID のトークンで認証します。
* `AuthMode=ApiKeySecretReference`: `ApiKeySecretName` に Key Vault 上のシークレット名を設定すると、Dapr の `secret-store` コンポーネント経由でキーを解決します。キー値そのものは構成に書きません。

## Dapr Workflow エンジンを使う場合

`Workflow__Engine=Dapr` を設定すると、同じ決定的オーケストレーターが Dapr Workflow として永続実行されます（承認は durable な外部イベント）。既定は `InProcess` です。

```bash
kubectl set env deployment/incident-api --namespace agentic-ops \
  Workflow__Engine=Dapr \
  Dapr__Enabled=true
```

## 後片付け

```bash
az group delete --name <resource-group>
```
