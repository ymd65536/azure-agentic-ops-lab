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
