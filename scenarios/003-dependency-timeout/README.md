# Scenario 003: Dependency timeout

## Summary

`sample-api` fails because calls to the external payment gateway time out.
Restarting the local service does not fix the external dependency, so the
system must avoid an ineffective restart loop.

## Expected behavior

* The pattern `external-dependency-timeout` matches, but the rule proposes no
  local remediation action (`maxActionAttempts` is 0).
* The incident is escalated to Tier 2 rather than resolved by restarting.
* The system never requests unbounded retries of the same action.
* The workflow escalates or terminates safely, and the final record explains
  the remaining uncertainty.

## Files

| File | Purpose |
| --- | --- |
| `incident.json` | The `Incident` contract for this scenario. |
| `evidence/logs.json` | Logs showing upstream request timeouts to the gateway. |
| `evidence/metrics.json` | Metrics showing dependency latency unaffected by restarts. |
| `expected-classification.json` | The expected rule evaluation outcome. |
| `expected-result.json` | The expected end-to-end workflow outcome. |
