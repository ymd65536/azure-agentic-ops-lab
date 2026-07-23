# Architecture

This document describes the architecture of the Azure Agentic Ops Lab
(Project Resolve) and what is implemented as of Milestone 1. The authoritative
design principles are in [`AGENTS.md`](../AGENTS.md).

## Target lifecycle

```text
Detect → Classify → Investigate → Escalate → Plan → Approve → Execute → Verify → Record
```

The full target architecture places a Dapr Workflow orchestrator in control of
all state transitions, with LLM agents performing only non-deterministic
reasoning, and deterministic policy code holding final authority over any
proposed action:

```text
Incident Source → Incident API → Dapr Workflow Orchestrator
                                   ├── Rule Evaluator      (deterministic)
                                   ├── Tier 1 SRE Agent    (LLM, fast path)
                                   ├── Tier 2 SRE Agent    (LLM, deep path)
                                   ├── Approval API        (human)
                                   ├── Execution Service   (policy-gated)
                                   └── Verification Service
All events → Dapr Pub/Sub → Scribe / Audit / Telemetry
```

## Milestone 1 components

Only the foundation is implemented. Nothing in this milestone depends on Dapr,
Kubernetes, Azure, or a specific LLM SDK.

### BuildingBlocks/Contracts

Immutable, versioned record contracts shared by all future services:
`Incident`, `IncidentEvidence`, `AgentHypothesis`, `InvestigationResult`,
`RemediationPlan`, `RemediationAction`, `ActionTarget`, `VerificationStep`,
`ExecutionResult`, `VerificationResult`, `VerificationCheckResult`,
`IncidentLifecycleEvent`, plus the enums `IncidentClassification`,
`AgentDisposition`, `RiskLevel`, `ExecutionOutcome`, and `VerificationOutcome`.

Every externally visible contract carries a `SchemaVersion` (currently `1.0`).

`ContractSerialization` pins the canonical JSON behavior:

* camelCase property names
* string enum values with explicit stable names (e.g. `request_more_evidence`)
* null properties omitted
* unknown enum values rejected instead of guessed

Golden tests in `tests/ContractTests` fail whenever the wire format changes,
so schema changes cannot happen silently.

### BuildingBlocks/AgentRuntime

`IAgentModelClient` is the single abstraction through which all future model
calls must flow. It returns `AgentModelResponse<T>` with
`ModelInvocationMetadata` (prompt name/version, model id, duration, token
usage, validation outcome, retry count) so every invocation is observable.

`FakeAgentModelClient` is the deterministic test double. Tests enqueue
behaviors in order: structured responses, raw (possibly invalid) output,
failures, and simulated latency driven by `TimeProvider` so no test sleeps.
Invalid output raises `ModelResponseValidationException`; invalid model output
is never passed downstream.

`FilePromptStore` loads version-controlled prompts from
`prompts/<name>/<version>.md` so no prompt is embedded in application source
code and every prompt version is diffable and reviewable.

No real Azure OpenAI or Microsoft Foundry client exists yet by design.

### BuildingBlocks/Safety

Deterministic policy code with final authority over any proposed action:

* `ActionTypeCatalog` — the allow-list of the six predefined action types with
  fixed risk classifications. There is no action type that can represent an
  arbitrary shell, kubectl, or Azure CLI command.
* `ActionPolicyEvaluator` — rejects unknown action types (treated as high
  risk), rejects high-risk actions, requires approval for medium-risk actions,
  enforces the target-namespace allow-list, validates idempotency keys, and
  bounds execution counts. Agents cannot downgrade a risk classification.
* `IdempotencyKeyValidator` — enforces non-empty, bounded, safe-charset keys
  so duplicate delivery can be handled idempotently later.

### RuleEvaluator

`IncidentRuleEvaluator` deterministically matches incident evidence against
declarative `RuleDefinition` data (no LLM). Decision table:

| Match count | Classification | Disposition |
| --- | --- | --- |
| exactly one rule | `known` | rule-defined (resolve or escalate) |
| more than one rule | `ambiguous` | escalate, never guess |
| zero rules | `unknown` | escalate, never guess |

`DefaultRuleCatalog` contains the Milestone 1 rules:
`known-routing-configuration-error` (Tier 1 fast path, proposes
`RollbackDemoDeployment`, one attempt max) and `external-dependency-timeout`
(known but escalates; proposes no local action so restart loops are
impossible).

### Tier1SreAgent

`Tier1SreAgent` is the fast investigation path. It searches the Insights
knowledge base, loads the `tier1-investigation` prompt, and asks the model for
a structured `InvestigationResult`. Deterministic code — not the model — has
final authority over the outcome:

* structured output is validated (schema version, incident id, confidence
  range, non-empty summary); invalid output triggers one bounded repair
  attempt and then fails safely with `ModelResponseValidationException`
* a `resolve` recommendation below the configured confidence threshold is
  escalated to Tier 2
* a proposed action whose type is not in `ActionTypeCatalog` is stripped and
  the result is escalated
* a `resolve` recommendation without a proposed deterministic action is
  escalated

`InsightsCapability` is a Tier 1 sub-capability, not an agent. It performs
deterministic keyword search over the version-controlled fixtures in
`knowledge/knowledge-base.json` (runbooks, prior incidents) and returns hits
with source identifiers. No vector database is used.

### Tier2SreAgent

`Tier2SreAgent` is the deep reasoning path. It receives the complete
structured Tier 1 handoff plus evidence, loads the `tier2-remediation` prompt,
and asks the model for a structured `RemediationPlan`. Deterministic guards
enforce the risk floor:

* plans containing action types outside `ActionTypeCatalog` are invalid
* the authoritative plan risk level is the maximum fixed catalog
  classification across all actions; the model can raise but never lower it
* medium- and high-risk plans always require approval; low-risk plans require
  approval unless automatic low-risk execution is explicitly enabled

### ExecutionService

`MockExecutionService` executes validated actions in mock (dry-run) mode only.
Every request first passes through `ActionPolicyEvaluator`; rejected actions
never execute. Approval requirements are enforced (`Rejected` when approval is
required but absent), and an in-memory idempotency ledger skips executions
beyond the action's `MaxExecutionCount`, producing `Skipped` results for
duplicate delivery.

### VerificationService

`VerificationEvaluator` runs the plan's `VerificationStep`s through an
`IVerificationCheckRunner` and aggregates deterministically: all checks must
pass for `passed`, any failure yields `failed`, and an empty step list yields
`inconclusive` because success cannot be demonstrated.
`MockVerificationCheckRunner` reports configured values per target and fails
unconfigured targets instead of guessing.

### Prompts and knowledge fixtures

`prompts/` holds the versioned prompt files (`tier1-investigation/1.0.md`,
`tier2-remediation/1.0.md`). `knowledge/knowledge-base.json` holds the
Insights fixtures. Both are plain version-controlled files loaded at runtime.

### Scenarios

`scenarios/` holds fixed, version-controlled fixtures loaded directly by
tests so the same experiment can be repeated while models, prompts, and
configuration change. Each scenario contains `incident.json`, `evidence/`,
`expected-classification.json`, `expected-result.json`, and a `README.md`.

## Boundaries preserved for later milestones

* Contracts have no Dapr/Azure/Kubernetes/LLM dependencies, so the future
  workflow, services, and agents can consume them unchanged.
* The workflow state machine (`Received … Terminated`), Pub/Sub lifecycle
  events, and approval-as-external-event model from `AGENTS.md` will be built
  on `IncidentLifecycleEvent` and the disposition/risk enums defined here.
* `ExecutionService` will consume `ActionPolicyEvaluator` decisions as-is;
  the policy is already the final authority.

## Explicit TODOs / open constraints

* `ActionPolicyOptions.AllowedNamespaces` defaults to `["demo"]`; production
  namespace policy is intentionally undefined until an environment model
  exists.
* `ScaleDemoWorkload` parameter bounds (min/max replicas) are not yet
  enforced; the range check belongs to ExecutionService schema validation in a
  later milestone.
* `MockExecutionService` and `VerificationEvaluator` are in-process library
  implementations; the Dapr service hosts arrive with the workflow milestone.
