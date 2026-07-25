# Azure Agentic Ops Lab (Project Resolve)

Azure、Dapr、Kubernetes、.NET を用いた自律型システム運用（agentic ops）の実験リポジトリです。
無制限の自律復旧を目指すのではなく、インシデント対応を「決定的ワークフロー / ルール自動化 / AIエージェント / 人間の承認 / 制御された実行 / 検証」に分担させる方法を評価します。

設計原則・安全要件・実装順序は [`AGENTS.md`](AGENTS.md) を参照してください。

```text
Detect → Classify → Investigate → Escalate → Plan → Approve → Execute → Verify → Record
```

## 現在のMilestone（Milestone 1）の範囲

システム全体の土台のみを実装しています。

* 共有契約（`src/BuildingBlocks/Contracts`）— Incident、InvestigationResult、RemediationPlan などの immutable record と安定した JSON シリアライズ
* モデルクライアント抽象化（`src/BuildingBlocks/AgentRuntime`）— `IAgentModelClient`、決定的テスト用 `FakeAgentModelClient`、バージョン管理プロンプトを読み込む `FilePromptStore`
* Safety 基盤（`src/BuildingBlocks/Safety`）— ActionType 許可リスト、リスク判定、`ActionPolicyEvaluator`、IdempotencyKey 検証
* RuleEvaluator（`src/RuleEvaluator`）— LLM を使わない決定的な既知パターン判定（ルールはデータとして分離）
* Tier 1 SRE Agent（`src/Tier1SreAgent`）— 構造化出力の検証、有界の修復リトライ、信頼度しきい値による決定的エスカレーション、Insights 検索機能
* Tier 2 SRE Agent（`src/Tier2SreAgent`）— 構造化された復旧計画、リスクフロア（エージェントはリスクを下げられない）、承認要件の強制
* IncidentWorkflow（`src/IncidentWorkflow`）— 明示的ステートマシンによる決定的オーケストレーション、有界リトライ、外部承認イベント抽象、ライフサイクルイベント発行、ロールバック、安全な停止
* ExecutionService（`src/ExecutionService`）— ポリシー検証・承認ゲート・冪等性台帳を備えたモック（dry-run）実行
* VerificationService（`src/VerificationService`）— 決定的な検証チェックの集約（全チェック合格で passed、チェックなしは inconclusive）
* ScribeService（`src/ScribeService`）— 重複イベント耐性のあるタイムライン構築と、構造化イベントからの決定的なポストインシデントレコード生成。ASP.NET Core ホストとして Dapr Pub/Sub（`incident-pubsub` / `incident-lifecycle`）をプログラム型サブスクリプションで購読し、タイムライン・ポストインシデントレコードを HTTP で提供（Dapr なしでもイベントを直接 POST して検証可能）
* Observability 基盤（`src/BuildingBlocks/Observability`）— OpenTelemetry 互換の ActivitySource / Meter、相関タグ規約（incident.id、correlation.id 等）、AGENTS.md §13 の推奨メトリクス一式
* IncidentApi ホスト（`src/IncidentApi`）— インシデント受付・状態照会・承認外部イベント・ヘルスチェックを提供する ASP.NET Core 最小 API。全ライブラリを DI で結線し、決定的スタブモデルクライアントでワークフロー全体をローカル実行可能。Dapr サイドカー経由の `incident-pubsub` ライフサイクルイベント発行（無効時は no-op）。OpenTelemetry SDK によるトレース・メトリクス収集（OTLP エクスポータはオプトイン）
* OpsConsole（`src/OpsConsole`）— .NET Blazor（Interactive Server）による運用コンソール。ワークフローの状態遷移・ライフサイクルタイムライン・人間の承認・シナリオ実行を視覚的に確認できる。IncidentApi を読み取り、人間の判断を中継するだけで、復旧アクションは実行しない
* ローカル Kubernetes デプロイ資産 — IncidentApi / OpsConsole の `Dockerfile`、`deploy/local/` の Kubernetes マニフェスト（namespace `agentic-ops`、Dapr コンポーネント `incident-pubsub` / `incident-state` / `secret-store`、開発用 Redis）、`scripts/` の運用スクリプト一式
* プロンプト資産（`prompts/`）と Insights ナレッジフィクスチャ（`knowledge/`）
* 固定シナリオ（`scenarios/`）— Scenario 001〜003
* テスト（`tests/UnitTests`、`tests/ContractTests`、`tests/WorkflowTests`、`tests/IntegrationTests`）

ローカルの動作確認に Kubernetes・Azure リソース・実 LLM API は不要です（デフォルト構成では外部通信は発生しません）。Dapr Pub/Sub 発行（`Dapr:Enabled`）、Dapr Workflow ホスティング（`Workflow:Engine=Dapr`）、実 LLM 接続（`AgentRuntime:Mode`）、AKS デプロイ（[`docs/azure-deployment.md`](docs/azure-deployment.md)）はすべてオプトインです。

## 必要環境

* .NET SDK 10.0（`global.json` で固定。`rollForward: latestFeature`）

## ビルド方法

```bash
dotnet build
```

`Directory.Build.props` により nullable / implicit usings / warnings-as-errors が全プロジェクトで有効です。

## テスト方法

```bash
dotnet test
```

すべてのテストはネットワークや外部サービスなしで実行できます。
シナリオファイルはテストがリポジトリの `scenarios/` から直接読み込みます。
`tests/IntegrationTests` は IncidentApi ホストをインメモリで起動し、HTTP 経由でシナリオをエンドツーエンドに実行します。

## 再現手順（ローカル・エンドツーエンド）

Kubernetes・Azure・実 LLM なしで、インシデント受付から検証・記録までのワークフローを再現できます。
Kubernetes 上での再現は [ローカル Kubernetes での実行](#ローカル-kubernetes-での実行) を参照してください。

前提ツール: .NET SDK 10.0、`curl`、`jq`（`scripts/run-scenario.sh` が使用）

```bash
# 0. リポジトリを取得
git clone https://github.com/ymd65536/azure-agentic-ops-lab.git
cd azure-agentic-ops-lab

# 1. ビルド（warnings-as-errors）
dotnet build

# 2. テスト（Unit / Contract / Workflow / Integration。ネットワーク不要）
dotnet test

# 3. IncidentApi を起動（決定的スタブモデル、外部依存なし）
ASPNETCORE_URLS=http://localhost:8080 dotnet run --project src/IncidentApi

# 4. 別ターミナルでシナリオをエンドツーエンド実行
#    （検証値の設定 → インシデント送信 → 状態ポーリング → 承認送達 → 完了確認）
scripts/run-scenario.sh 001-known-routing-error
scripts/run-scenario.sh 002-ambiguous-404-increase
scripts/run-scenario.sh 003-dependency-timeout

# 5. 状態とライフサイクルタイムラインを確認（<incident-id> は手順4の出力に表示）
curl -s localhost:8080/incidents | jq -r '.[] | "\(.incidentId) \(.currentState)"'
curl -s localhost:8080/incidents/<incident-id> | jq .
curl -s localhost:8080/incidents/<incident-id>/timeline | jq .

# 6. 収集（ワークフロー状態を results/<timestamp>/ に保存）
API_URL=http://localhost:8080 scripts/collect-results.sh <incident-id>
```

`scripts/run-scenario.sh` は実行ごとにインシデント ID へタイムスタンプを付与するため、同じシナリオを何度でも再実行できます（`--incident-id` で固定も可能）。
API プロセスを停止すると、インメモリのワークフロー状態とタイムラインは破棄されます。

### 分岐パスの再現

| コマンド | 再現する経路 | 実行例の最終状態 |
| --- | --- | --- |
| `scripts/run-scenario.sh 001-known-routing-error` | 既知パターン一致 → Tier 1 → 承認 → 実行 → 検証成功 | `resolved` |
| `scripts/run-scenario.sh 001-known-routing-error --reject` | 人間が却下し、アクションを実行しない | `rejected` |
| `scripts/run-scenario.sh 001-known-routing-error --no-decision` | 承認イベントが届かず `awaitingApproval` のまま待機し、タイムアウトで安全に停止（スクリプト側は `--timeout` で打ち切り） | `awaitingApproval` → 停止 |
| `scripts/run-scenario.sh 002-ambiguous-404-increase` | 未知パターン → Tier 2 エスカレーション → 計画 → 実行 → 検証 | `resolved` |
| `scripts/run-scenario.sh 003-dependency-timeout --verification-value degraded` | 復旧後も検証が失敗し、無限リトライせず有界で停止 | `failed` |

各シナリオの設計上の期待値は `scenarios/<name>/expected-result.json` に固定されており、`dotnet test`（ワークフロー／統合テスト）で検証されます。
承認要否は `ActionPolicyEvaluator` のリスク判定が決定するため、Tier 2 が低リスクアクションのみを提案した場合は承認待ちにならずに実行へ進みます。

### ブラウザで再現を確認する

手順3のあと、別ターミナルで運用コンソールを起動すると、状態遷移・タイムライン・承認操作を画面から再現できます（詳細は [運用コンソール（Blazor）のローカル実行](#運用コンソールblazorのローカル実行)）。

```bash
ASPNETCORE_URLS=http://localhost:5080 dotnet run --project src/OpsConsole
# ブラウザで http://localhost:5080 を開き、/scenarios から実行・承認する
```

## IncidentApi のローカル実行

```bash
dotnet run --project src/IncidentApi
```

実 LLM の代わりに決定的スタブモデルクライアント（ルールカタログとポリシーから正当な構造化出力を合成）を使用するため、外部依存なしで全ワークフローを実行できます。

| エンドポイント | 説明 |
| --- | --- |
| `POST /incidents` | インシデントと証拠（モックデータ）を受け付け、ワークフローを開始（重複 ID は 409） |
| `GET /incidents` | すべてのワークフロー実行の状態一覧（新しい順。運用コンソール用の読み取り専用ビュー） |
| `GET /incidents/{incidentId}` | ワークフローの現在状態と最終結果を照会 |
| `GET /incidents/{incidentId}/timeline` | 記録済みライフサイクルイベントのタイムライン（到着順。上限は `IncidentTimeline` 設定で有界） |
| `POST /incidents/{incidentId}/approval` | 人間の承認/却下を外部イベントとして送達（HTTP リクエストは保持しない） |
| `POST /demo/verification` | デモ専用: モック検証ランナーが報告する実測値を設定 |
| `GET /healthz` / `GET /readyz` | liveness / readiness |

シナリオ 001 の例:

```bash
# 検証ターゲットを healthy に設定（復旧後の検証を成功させる）
curl -X POST localhost:5000/demo/verification \
  -H 'Content-Type: application/json' \
  -d '{"target":"demo/deployment/sample-api","actualValue":"healthy"}'

# インシデントを送信（evidence はシナリオの evidence/*.json の配列）
curl -X POST localhost:5000/incidents \
  -H 'Content-Type: application/json' \
  -d "{\"incident\": $(cat scenarios/001-known-routing-error/incident.json), \"evidence\": [$(cat scenarios/001-known-routing-error/evidence/*.json | paste -sd, -)]}"

# AwaitingApproval になったら承認イベントを送達
curl -X POST localhost:5000/incidents/inc-001/approval \
  -H 'Content-Type: application/json' \
  -d '{"outcome":"approved","approver":"sre-lead","reason":"known pattern"}'

# 状態を確認
curl localhost:5000/incidents/inc-001
```

Dapr サイドカーと併用する場合は `Dapr:Enabled=true` を設定すると、全ライフサイクルイベントが論理コンポーネント `incident-pubsub` のトピック `incident-lifecycle` に発行されます（サイドカー不達でも復旧パスは停止しません）。

トレース・メトリクスを OTLP で送信する場合は、環境変数 `OTEL_EXPORTER_OTLP_ENDPOINT`（または設定 `OpenTelemetry:OtlpEndpoint`）にコレクタのエンドポイントを指定します。未指定の場合はエクスポータを登録しないため、ネットワーク依存なしで動作します。

## 運用コンソール（Blazor）のローカル実行

コマンドと JSON だけでは自律型運用の進行が分かりにくいため、ワークフローを視覚的に確認できる Blazor Server アプリを同梱しています。

```bash
# 1. IncidentApi を起動（既定 http://localhost:8080）
ASPNETCORE_URLS=http://localhost:8080 dotnet run --project src/IncidentApi

# 2. 別ターミナルで運用コンソールを起動し、ブラウザで http://localhost:5080 を開く
ASPNETCORE_URLS=http://localhost:5080 dotnet run --project src/OpsConsole
```

| 画面 | 内容 |
| --- | --- |
| `/`（Incidents） | 実行中・完了済みインシデントの一覧と現在状態。承認待ちは強調表示。既定2秒間隔で自動更新 |
| `/incidents/{incidentId}` | ワークフローのステートパイプライン（通過済み・現在・終端状態）、ライフサイクルタイムライン（各イベントの component / outcome / attempt / details）、承認待ち時の承認・却下ボタン |
| `/scenarios` | バージョン管理されたシナリオの一覧と実行。モック検証値（healthy / unhealthy）を選んで成功・失敗パスを再現できる |

設定（`OpsConsole` セクション、環境変数では `OpsConsole__IncidentApiBaseUrl` など）:

| 設定 | 既定値 | 説明 |
| --- | --- | --- |
| `IncidentApiBaseUrl` | `http://localhost:8080` | 参照する IncidentApi のベース URL |
| `ScenariosRoot` | `scenarios` | シナリオフィクスチャのディレクトリ |
| `RefreshIntervalSeconds` | `2` | 画面のポーリング間隔（秒） |

コンソールは IncidentApi の読み取りエンドポイントを参照し、人間の承認決定を外部イベントとして中継するだけです。復旧アクションの実行・ポリシー判断・状態遷移はすべてワークフロー側が所有します。

## モデル実行モード

`IAgentModelClient` の実体は `AgentRuntime` 設定セクションの実行モードで選択します。デフォルトは `Deterministic` で、外部通信は一切発生しません。未知のモード名は起動時検証で拒否されます。

| モード | 動作 |
| --- | --- |
| `Deterministic`（デフォルト） | 決定的スタブモデルクライアントのみを使用。外部通信なし |
| `RemoteModel` | リモートモデルの構造化出力をワークフローで使用（`RemoteModel` セクションの `Endpoint` / `ModelId` が必須） |
| `Shadow` | 決定的結果を採用しつつ、同一入力をリモートモデルへも送信し、構造化比較を評価レコードとして `results/evaluations/` に JSON Lines で記録 |

設定例（`appsettings.json` または環境変数）:

```json
{
  "AgentRuntime": {
    "Mode": "Deterministic",
    "RemoteModel": {
      "Endpoint": "https://<foundry-endpoint>",
      "ModelId": "<model-or-deployment-id>",
      "AuthMode": "DefaultAzureCredential",
      "TimeoutSeconds": 30,
      "MaxAttempts": 2
    },
    "Shadow": {
      "TimeoutSeconds": 30,
      "EvaluationOutputDirectory": "results/evaluations"
    }
  }
}
```

* 認証は `DefaultAzureCredential`（デフォルト）または `ApiKeySecretReference`（`ApiKeySecretName` でシークレット名のみを参照）です。資格情報の生値は設定・コード・ログ・評価レコードのいずれにも置きません。
* Shadow モードのリモートモデル出力は、ワークフロー・承認判定・ExecutionService に一切渡されません。リモート側の失敗・タイムアウト・無効出力は評価レコードに記録されるだけで、決定的ワークフローを停止させません。
* ワイヤ実装は `FoundryChatCompletionTransport`（OpenAI 互換 chat-completions。Azure OpenAI 形式の `ApiVersion` クエリにも対応）です。`RemoteModel:Endpoint` と `ModelId` が設定されている場合のみ登録され、未設定時は安全に失敗するプレースホルダーのままです。認証は `DefaultAzureCredential`（`TokenScope` 設定可）、または Dapr の `secret-store` コンポーネント経由で API キーを名前解決する `ApiKeySecretReference` を使用します。429/5xx/ネットワークエラーは一時的失敗として `RemoteAgentModelClient` の有界リトライに分類され、エラー応答本文はログ・例外メッセージに含めません。

## ローカル Kubernetes での実行

前提ツール: `docker`、`k3d`、`kubectl`、`dapr` CLI、`jq`、`curl`

```bash
# 1. ローカルクラスタ（k3d）を作成し Dapr をインストール
scripts/bootstrap-local.sh

# 2. コンテナイメージをビルドしてクラスタへインポート
scripts/build-images.sh

# 3. namespace / Redis / Daprコンポーネント / IncidentApi / OpsConsole / ScribeService をデプロイ
#    （IncidentApi は Dapr サイドカー有効でライフサイクルイベントを発行し、ScribeService が購読）
scripts/deploy-local.sh

# 4. API をローカルへポートフォワード（別ターミナルで維持）
kubectl port-forward --namespace agentic-ops service/incident-api 8080:80

# 5. 運用コンソールをポートフォワードしてブラウザで確認（別ターミナルで維持）
kubectl port-forward --namespace agentic-ops service/ops-console 5080:80
# ブラウザで http://localhost:5080 を開く

# 6. シナリオを実行（送信 → 状態ポーリング → 承認送達 → 完了確認まで自動）
scripts/run-scenario.sh 001-known-routing-error
scripts/run-scenario.sh 001-known-routing-error --reject          # 却下パス
scripts/run-scenario.sh 001-known-routing-error --no-decision     # 承認タイムアウト
scripts/run-scenario.sh 003-dependency-timeout --verification-value degraded  # 検証失敗パス

# 7. 障害注入（カオステスト）
scripts/inject-failure.sh delete-api-pod   # ワークフロー実行中のPod削除
scripts/inject-failure.sh restart-redis    # 開発用Redisの再起動

# 8. ログ・ワークフロー状態・クラスタ診断を results/ に収集
API_URL=http://localhost:8080 scripts/collect-results.sh <incident-id>

# 9. Scribe のタイムラインとポストインシデントレコードを確認（別ターミナルで維持）
kubectl port-forward --namespace agentic-ops service/scribe-service 8090:80
curl -s localhost:8090/incidents/<incident-id>/timeline | jq .
curl -s localhost:8090/incidents/<incident-id>/record | jq .

# 10. ワークフロー状態の確認・承認イベントの手動送達
curl localhost:8080/incidents/<incident-id>
curl -X POST localhost:8080/incidents/<incident-id>/approval \
  -H 'Content-Type: application/json' \
  -d '{"outcome":"approved","approver":"sre-lead","reason":"known pattern"}'

# 11. ローカル環境の削除
k3d cluster delete agentic-ops
```

Dapr コンポーネントの論理名（`incident-pubsub` / `incident-state`）は環境間で不変です。ローカルでは Redis を下位実装として使用し、Azure 環境では Service Bus / Azure ステートストアに差し替えます。今回のローカル Milestone では、シークレットストア依存を避けるため `secret-store` は展開対象外です。

## シナリオの追加方法

1. `scenarios/NNN-short-name/` ディレクトリを作成する
2. 以下のファイルを配置する
   * `incident.json` — `Incident` 契約（camelCase JSON）
   * `evidence/*.json` — `IncidentEvidence` 契約（1ファイル1件）
   * `expected-classification.json` — RuleEvaluator の期待結果
   * `expected-result.json` — ワークフロー全体の期待結果
   * `README.md` — シナリオの説明
3. 既知パターンにする場合は `src/RuleEvaluator/DefaultRuleCatalog.cs` に `RuleDefinition` を追加する
4. `tests/UnitTests` に `ScenarioLoader.Load("NNN-short-name")` を使うテストを追加する

## 実装済みシナリオ

| シナリオ | 内容 | 期待結果 |
| --- | --- | --- |
| [001-known-routing-error](scenarios/001-known-routing-error/) | 既知のルーティング設定ミスによる404 | 既知パターン一致、Tier 1で解決、低〜中リスクのモック復旧 |
| [002-ambiguous-404-increase](scenarios/002-ambiguous-404-increase/) | 原因が曖昧な404増加 | unknown判定、Tier 2エスカレーション、人間の承認が必要 |
| [003-dependency-timeout](scenarios/003-dependency-timeout/) | 外部依存タイムアウト | 再起動の無限ループを回避、エスカレーションまたは安全な停止 |

## 主要な設計判断

* ルールはデータ: `RuleDefinition` は宣言的な定義オブジェクトで、`IncidentRuleEvaluator` はそれを決定的に評価するだけです。未知・複数一致のケースは推測せずエスカレーションします。
* ポリシーが最終権限: `ActionPolicyEvaluator` は許可リスト外・高リスク・不正な IdempotencyKey・許可外 namespace を拒否します。エージェントはリスク分類を下げられません。
* 任意コマンドは表現不可能: ActionType は6種類の定義済み操作のみで、シェル/CLI コマンドを表す型は存在しません。
* 安定した契約 JSON: `ContractSerialization` が camelCase、文字列 enum、null 省略を固定し、ContractTests のゴールデンテストで破壊的変更を検出します。
* 決定的なモデルテスト: `FakeAgentModelClient` は応答・遅延（`TimeProvider` ベース）・失敗・無効 JSON をテストから制御できます。

* ワークフローが遷移を所有: `WorkflowStateMachine` が宣言的な遷移表で全状態遷移を検証し、`IncidentWorkflowOrchestrator` のすべてのループは `IncidentWorkflowOptions` の最大試行回数で有界です。承認は外部イベント（承認 / 却下 / タイムアウト）として扱われ、HTTP リクエストを保持しません。

* 計装は SDK 非依存: `BuildingBlocks/Observability` は `System.Diagnostics` プリミティブ（`ActivitySource` / `Meter`）のみに依存し、OpenTelemetry SDK やエクスポータはホスト側で登録します。インシデント ID などの高カーディナリティ値はスパン・ログのみに付与し、メトリクスラベルには使用しません。

* スタブモデルも安全境界の内側: `DeterministicStubModelClient` はルールカタログとポリシー評価から構造化出力を合成するため、許可リスト外のアクションを提案できません。承認が必要なアクションを Tier 1 の高速パスに載せず、Tier 2（承認付き）へ決定的にエスカレーションします。実 LLM への差し替えは `IAgentModelClient` の実装交換のみで行えます。

## ワークフローのホスティングエンジン

`Workflow:Engine` 設定でワークフローの実行基盤を選択します。どちらのエンジンでも同じ決定的オーケストレータ（`IncidentWorkflowOrchestrator`）と状態遷移・有界リトライが使われます。

| エンジン | 動作 |
| --- | --- |
| `InProcess`（デフォルト） | IncidentApi ホスト内でインプロセス実行。サイドカー等の外部依存なし |
| `Dapr` | Dapr Workflow として永続実行。各アクティビティ（証拠収集・ルール評価・Tier 1/2・実行・検証・ライフサイクル発行）はジャーナルされたワークフローアクティビティとして実行され、リプレイ安全。人間の承認は durable な外部イベント（`approval-decision`）として送達され、プロセス再起動後も承認待ちが継続 |

`Dapr` エンジンには Workflow が有効な Dapr サイドカーが必要です（例: `dapr run --app-id incident-api --app-port 8080 -- dotnet run --project src/IncidentApi` と環境変数 `Workflow__Engine=Dapr`）。ワークフロー時刻はリプレイ安全な `WorkflowContext.CurrentUtcDateTime` から供給され、メトリクスはアクティビティ側でのみ記録されるため、リプレイで二重計上されません。

## ScribeService（Pub/Sub 購読）のローカル実行

ScribeService は `incident-pubsub` の `incident-lifecycle` トピックを購読する非同期コンシューマです。復旧のクリティカルパスには乗らず、停止しても調査・復旧・検証を妨げません。

```bash
# Dapr なしで起動し、イベントを直接 POST して検証できる
ASPNETCORE_URLS=http://localhost:8091 dotnet run --project src/ScribeService
curl -X POST localhost:8091/events/incident-lifecycle   -H 'Content-Type: application/json'   -d '{"schemaVersion":"1.0","eventId":"evt-1","incidentId":"inc-001","correlationId":"corr-1","eventType":"StateChanged","component":"IncidentWorkflow","occurredAt":"2026-07-25T12:00:00Z","outcome":"resolved"}'
curl -s localhost:8091/incidents/inc-001/timeline
curl -s localhost:8091/incidents/inc-001/record
```

| エンドポイント | 説明 |
| --- | --- |
| `GET /dapr/subscribe` | プログラム型 Dapr サブスクリプション宣言（`incident-pubsub` / `incident-lifecycle`） |
| `POST /events/incident-lifecycle` | ライフサイクルイベントの受信。CloudEvents エンベロープと生 JSON の両方を受け付け、イベント ID で重複排除。不正ペイロードは再配送されないよう `DROP` で応答 |
| `GET /incidents/{incidentId}/timeline` | 発生時刻順のタイムライン |
| `GET /incidents/{incidentId}/record` | 決定的なポストインシデントレコードのドラフト |
| `GET /healthz` / `GET /readyz` | liveness / readiness |

## Azure（AKS）へのデプロイ

`infra/main.bicep` が AKS（Dapr クラスター拡張・Workload ID 有効）、ACR、Service Bus、Table Storage、Key Vault、Log Analytics をプロビジョニングし、`deploy/azure/dapr-components/` が同じ論理名（`incident-pubsub` / `incident-state` / `secret-store`）の Azure 実装を提供します。手順は [`docs/azure-deployment.md`](docs/azure-deployment.md) を参照してください。

```bash
RESOURCE_GROUP=<resource-group> NAME_PREFIX=<prefix> scripts/deploy-azure.sh
```

## 未実装の機能（今後のMilestone）

* Dapr Service Invocation（`IIncidentWorkflowActivities` の Dapr サービス呼び出し実装。ライフサイクル Pub/Sub 発行・購読は実装済み）
* ExecutionService / VerificationService の独立 Dapr サービスホスト化（現在は IncidentApi 内でインプロセス結線。ScribeService は独立ホスト化済み）

詳細は [`docs/architecture.md`](docs/architecture.md) と [`docs/evaluation-plan.md`](docs/evaluation-plan.md) を参照してください。
