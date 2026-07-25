# Scenario 001: Known routing configuration error

## Summary

A routing configuration deployment removed the route for `/api/orders` on
`sample-api`, causing a spike of HTTP 404 responses. This is a known operational
pattern with a deterministic remediation.

## Expected behavior

* The rule evaluator matches the pattern `known-routing-configuration-error`.
* The incident is classified as `known` with high confidence.
* Tier 1 can handle the incident; no Tier 2 escalation occurs.
* A low/medium-risk predefined action (`RollbackDemoDeployment`) is proposed.
* Verification passes after mock execution.

## Files

| File | Purpose |
| --- | --- |
| `incident.json` | The `Incident` contract for this scenario. |
| `evidence/logs.json` | Application logs showing 404s with "no matching route". |
| `evidence/config-diff.json` | Configuration diff showing the removed route. |
| `expected-classification.json` | The expected rule evaluation outcome. |
| `expected-result.json` | The expected end-to-end workflow outcome. |
