# Evaluation plan

This document describes how the system will be evaluated across milestones.
The goal of the lab is to measure how incident response quality changes as
responsibilities are divided among deterministic workflows, rules, AI agents,
human approval, controlled execution, and verification.

## Evaluation principles

* Scenarios are fixed and version-controlled under `scenarios/`. The same
  scenario is replayed while models, prompts, and configuration change.
* Expected outcomes are stored next to each scenario
  (`expected-classification.json`, `expected-result.json`) and asserted by
  tests, not judged by prose comparison.
* Agent evaluation asserts structured fields and measurable criteria, never
  exact text equality.
* All Milestone 1 evaluation runs offline: no network, no external services.

## Milestone 1 evaluation (implemented)

| Question | How it is evaluated |
| --- | --- |
| Are known patterns matched deterministically? | `RuleEvaluatorScenarioTests.Scenario001_MatchesKnownRoutingConfigurationError` |
| Are unknown/ambiguous incidents escalated instead of guessed? | Scenario 002 test plus the no-rules and multi-match tests |
| Are ineffective retries bounded? | Scenario 003 test asserts `MaxActionAttempts == 0` and escalation |
| Are unknown or dangerous actions rejected? | `ActionPolicyEvaluatorTests` (unknown types, arbitrary commands, bad namespaces, invalid idempotency keys, execution-count bounds) |
| Is contract JSON stable? | Golden serialization tests in `tests/ContractTests` |
| Can model behavior be reproduced deterministically? | `FakeAgentModelClientTests` (success, failure, latency via fake time, invalid JSON, cancellation) |

Run with:

```bash
dotnet test
```

## Metrics to capture in later milestones

Per scenario run (see `AGENTS.md` §13–14):

* Correct classification rate
* Correct escalation decision rate
* Root cause accuracy
* Unsupported claim count
* Dangerous action proposal count (must be 0 executed; proposals must be rejected by policy)
* Schema compliance rate of model output
* Latency per tier (`tier1_duration_seconds`, `tier2_duration_seconds`)
* Input/output token counts per invocation
* Number of tool calls
* Verification pass rate and rollback rate

Results will be written under `results/` (kept separate from `scenarios/` so
fixtures stay immutable), tagged with prompt name/version and model id from
`ModelInvocationMetadata`.

## Future evaluation stages

1. **Agent evaluation tests** (`tests/AgentEvaluationTests`): replay scenarios
   through Tier 1/Tier 2 with the fake model client and, later, recorded real
   model outputs; assert structured dispositions and safety criteria.
2. **Workflow tests** (`tests/WorkflowTests`): approval accepted/rejected/
   timeout, execution failure, verification failure, rollback, max-attempt
   termination, restart continuation.
3. **Integration tests**: local Kubernetes with Dapr enabled, driven by
   `scripts/run-scenario.sh`.
4. **Chaos tests**: pod deletion during investigation, duplicate Pub/Sub
   events, Scribe outage, Redis restart, delayed approval.

## Shadow-mode comparison metrics (implemented)

When `AgentRuntime:Mode` is `Shadow`, the deterministic result is adopted by
the workflow while the same input is also sent to the remote model. Both
structured outputs are compared field by field and each invocation is written
as one JSON Lines record (`AgentEvaluationRecord`) under `results/evaluations/`.
Free-form prose (`reasoningSummary`, summaries, observation text) is never
compared for equality.

Compared structured fields:

| Tier | Field | Comparison |
| --- | --- | --- |
| Tier 1 | `classification` | Exact enum match |
| Tier 1 | `recommendedDisposition` | Exact enum match |
| Tier 1 | `escalationRequired` | Both sides agree on whether disposition is `escalate` |
| Tier 1 | `confidenceDelta` | Absolute difference of confidence values (recorded, not pass/fail) |
| Tier 1 | `proposedActionType` | Exact action-type match (including absence) |
| Tier 1 | `missingEvidence` | Set equality of requested evidence items |
| Tier 2 | `riskLevel` | Exact enum match |
| Tier 2 | `requiresApproval` | Exact boolean match |
| Tier 2 | `actionTypes` | Ordered sequence equality of action types |
| Tier 2 | `verificationSteps` | Sequence equality of (checkType, target) pairs |
| Tier 2 | `rollbackPresence` | Both sides agree on whether rollback steps exist |

Each record also captures: incident id, agent role, execution mode, scenario
name, prompt name/version, model id, start time, duration, input/output tokens,
tool call count, knowledge retrieval count, schema validation result, repair
attempt count, classification/disposition/risk level of the shadow output,
proposed action types, and an error category (`timeout`, `invalid_output`,
`cancelled`, `shadow_failure`) when the shadow invocation failed. Shadow
failures never interrupt the deterministic workflow, and shadow output never
reaches approval decisions or the ExecutionService. Incident ids appear only
in records, traces, and logs — never as metric labels.
