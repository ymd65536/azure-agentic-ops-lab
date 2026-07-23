# Azure Agentic Ops Lab (Project Resolve)

Azure、Dapr、Kubernetes、.NET を用いた自律型システム運用（agentic ops)の実験リポジトリです。
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
* ScribeService（`src/ScribeService`）— 重複イベント耐性のあるタイムライン構築と、構造化イベントからの決定的なポストインシデントレコード生成
* Observability 基盤（`src/BuildingBlocks/Observability`）— OpenTelemetry 互換の ActivitySource / Meter、相関タグ規約（incident.id、correlation.id 等）、AGENTS.md §13 の推奨メトリクス一式
* プロンプト資産（`prompts/`）と Insights ナレッジフィクスチャ（`knowledge/`）
* 固定シナリオ（`scenarios/`）— Scenario 001〜003
* テスト（`tests/UnitTests`、`tests/ContractTests`、`tests/WorkflowTests`）

Dapr、Kubernetes、Azure リソース、実 LLM API は **このMilestoneでは使用しません**。

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

* **ルールはデータ**: `RuleDefinition` は宣言的な定義オブジェクトで、`IncidentRuleEvaluator` はそれを決定的に評価するだけです。未知・複数一致のケースは推測せずエスカレーションします。
* **ポリシーが最終権限**: `ActionPolicyEvaluator` は許可リスト外・高リスク・不正な IdempotencyKey・許可外 namespace を拒否します。エージェントはリスク分類を下げられません。
* **任意コマンドは表現不可能**: ActionType は6種類の定義済み操作のみで、シェル/CLI コマンドを表す型は存在しません。
* **安定した契約 JSON**: `ContractSerialization` が camelCase、文字列 enum、null 省略を固定し、ContractTests のゴールデンテストで破壊的変更を検出します。
* **決定的なモデルテスト**: `FakeAgentModelClient` は応答・遅延（`TimeProvider` ベース）・失敗・無効 JSON をテストから制御できます。

* **ワークフローが遷移を所有**: `WorkflowStateMachine` が宣言的な遷移表で全状態遷移を検証し、`IncidentWorkflowOrchestrator` のすべてのループは `IncidentWorkflowOptions` の最大試行回数で有界です。承認は外部イベント（承認 / 却下 / タイムアウト）として扱われ、HTTP リクエストを保持しません。

* **計装は SDK 非依存**: `BuildingBlocks/Observability` は `System.Diagnostics` プリミティブ（`ActivitySource` / `Meter`）のみに依存し、OpenTelemetry SDK やエクスポータはホスト側で登録します。インシデント ID などの高カーディナリティ値はスパン・ログのみに付与し、メトリクスラベルには使用しません。

## 未実装の機能（今後のMilestone）

* Dapr Workflow ホスティング（現在の `IncidentWorkflow` オーケストレータを Dapr Workflow 上で実行）
* Dapr Service Invocation / Pub/Sub（`ILifecycleEventPublisher` / `IIncidentWorkflowActivities` の Dapr 実装）
* IncidentApi
* ExecutionService / VerificationService / ScribeService の Dapr サービスホスト化（現在はライブラリ実装のみ）
* 実 LLM（Azure OpenAI / Microsoft Foundry）接続
* OpenTelemetry SDK / エクスポータのホスト登録（計装ライブラリは実装済み）
* Kubernetes マニフェスト、k3d/kind ブートストラップ、AKS デプロイ

詳細は [`docs/architecture.md`](docs/architecture.md) と [`docs/evaluation-plan.md`](docs/evaluation-plan.md) を参照してください。
