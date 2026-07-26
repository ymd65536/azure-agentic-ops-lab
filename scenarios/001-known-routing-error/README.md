# Scenario 001: Known routing configuration error

## Summary

A routing configuration deployment removed the route for `/api/orders` on
`sample-api`, causing a spike of HTTP 404 responses. This is a known operational
pattern with a deterministic remediation.

## Expected behavior

* The rule evaluator matches the pattern `known-routing-configuration-error`.
* The incident is classified as `known` with high confidence.
* The proposed action `RollbackDemoDeployment` is medium risk, so the rule fast
  path may not execute it and the incident is shared with Tier 1 together with a
  summary of the rule-based handling.
* Tier 1 reviews that summary and proposes a remediation plan.
* The plan is shared with Tier 2, which assesses the execution risk and shares
  the assessment with the operations console.
* A human is asked to approve command execution before anything runs.
* Verification passes after the approved mock execution.

## Files

| File | Purpose |
| --- | --- |
| `incident.json` | The `Incident` contract for this scenario. |
| `evidence/logs.json` | Application logs showing 404s with "no matching route". |
| `evidence/config-diff.json` | Configuration diff showing the removed route. |
| `expected-classification.json` | The expected rule evaluation outcome. |
| `expected-result.json` | The expected end-to-end workflow outcome. |
