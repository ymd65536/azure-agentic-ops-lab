# Scenario 002: Ambiguous increase in HTTP 404 responses

## Summary

HTTP 404 responses on `sample-web` increase gradually with no recent
deployment. Multiple candidate causes exist (stale external links, CDN cache
degradation, incomplete content migration), so the incident cannot be resolved
by a known-pattern rule.

## Expected behavior

* No known pattern matches; the incident is classified as `unknown`.
* The rule evaluator recommends escalation to Tier 2.
* Tier 2 must compare multiple hypotheses and produce a structured plan.
* Any remediation requires human approval before execution.

## Files

| File | Purpose |
| --- | --- |
| `incident.json` | The `Incident` contract for this scenario. |
| `evidence/logs.json` | Logs showing distributed 404s across many paths. |
| `evidence/metrics.json` | Metrics showing the 404 rate and CDN cache hit ratio. |
| `evidence/deployment-history.json` | Deployment history ruling out a recent deployment. |
| `expected-classification.json` | The expected rule evaluation outcome. |
| `expected-result.json` | The expected end-to-end workflow outcome. |
