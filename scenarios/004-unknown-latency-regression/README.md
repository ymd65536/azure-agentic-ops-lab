# Scenario 004: Unknown latency regression evaluated with Microsoft Foundry

## Summary

`checkout-service` shows intermittent p95 latency bursts with a stable error
rate. Three plausible causes coexist in the evidence: GC pause growth,
database connection pool exhaustion, and a noisy-neighbor batch job recently
rescheduled onto the same node pool. No known-pattern rule matches, so
deterministic routing escalates to Tier 2.

Unlike scenarios 001–003, this scenario is specifically designed to be run
with the remote model (Microsoft Foundry) path enabled, so the quality of
LLM-generated hypotheses and plans can be compared against the deterministic
baseline.

## Expected behavior

* No known pattern matches; the incident is classified as `unknown`.
* The rule evaluator recommends escalation to Tier 2.
* Tier 2 must weigh the three competing hypotheses and produce a structured plan.
* Any remediation requires human approval before execution.
* The deterministic workflow expectations are identical in every
  `AgentRuntime` mode; the Foundry model output never bypasses policy,
  approval, or the ExecutionService allow-list.

## Running with Foundry

Start with Shadow mode: the workflow still uses the deterministic result, and
the same input is sent to the Foundry endpoint for comparison. Structured
comparison records are written as JSON Lines under `results/evaluations/`.

```bash
export AgentRuntime__Mode=Shadow
export AgentRuntime__RemoteModel__Endpoint="https://<foundry-endpoint>"
export AgentRuntime__RemoteModel__ModelId="<model-or-deployment-id>"
# AuthMode defaults to DefaultAzureCredential (az login locally)

scripts/run-scenario.sh 004-unknown-latency-regression
```

To let the remote model output drive the workflow instead, set
`AgentRuntime__Mode=RemoteModel`. Invalid structured output is rejected with a
bounded repair attempt, and unknown or high-risk actions are still rejected by
policy code.

Remote model failures, timeouts, and invalid output in Shadow mode are
recorded in the evaluation records only; they never block the deterministic
workflow.

## Files

| File | Purpose |
| --- | --- |
| `incident.json` | The `Incident` contract for this scenario. |
| `evidence/logs.json` | Logs mixing GC pauses, pool waits, and normal requests. |
| `evidence/metrics.json` | Metrics showing latency bursts, GC pauses, pool usage, and CPU steal. |
| `evidence/deployment-history.json` | Deployment history ruling out a recent deployment and introducing the batch job move. |
| `expected-classification.json` | The expected rule evaluation outcome. |
| `expected-result.json` | The expected end-to-end workflow outcome. |
