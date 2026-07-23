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
* Retry/repair loops for invalid model output are not implemented yet; the
  fake client records `RetryCount = 0`.
