# Scenario 005: Known crash loop resolved by rule evaluation

## Summary

The disposable demo workload `worker-service` is crash-looping after an
out-of-memory kill. The signature is a known operational pattern with an
approved low-risk remediation, so the incident is resolved deterministically at
the RuleEvaluation stage: no model call, Tier 1 investigation, or human
approval is needed.

This scenario is the first step of the escalation-ladder demo:

```text
005: rule fast path       -> resolved by RuleEvaluation
004: unknown incident     -> Tier 1 investigation (Foundry) summarizes the cause
002: ambiguous incident   -> Tier 2 planning and human approval
```

## Expected behavior

* The pattern `known-demo-workload-crashloop` matches with high confidence.
* The proposed action `RestartDemoWorkload` is low risk, so policy allows
  automatic execution in the demo environment.
* The workflow transitions `RuleEvaluation -> Executing -> Verifying -> Resolved`
  without entering `Tier1Investigation` or `Tier2Investigation`.
* If execution or verification fails, the workflow escalates to Tier 1 instead
  of retrying the rule remediation blindly.

## Files

| File | Purpose |
| --- | --- |
| `incident.json` | The `Incident` contract for this scenario. |
| `evidence/logs.json` | Kubelet logs showing the `CrashLoopBackOff` state. |
| `evidence/metrics.json` | Metrics showing the growing `restartCount`. |
| `expected-classification.json` | The expected rule evaluation outcome. |
| `expected-result.json` | The expected end-to-end workflow outcome. |

## Run

```bash
scripts/run-scenario.sh 005-known-crashloop-restart
```
