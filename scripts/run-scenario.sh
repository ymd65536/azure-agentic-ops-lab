#!/usr/bin/env bash
# Runs a scenario end-to-end against a running IncidentApi instance.
#
# Usage:
#   scripts/run-scenario.sh <scenario> [options]
#
# Arguments:
#   <scenario>   A scenario directory name under scenarios/, for example
#                001-known-routing-error.
#
# Options:
#   --api-url <url>              IncidentApi base URL (default http://localhost:8080).
#   --incident-id <id>           Override the incident ID (default: from incident.json
#                                with a timestamp suffix, so reruns don't collide).
#   --approve                    Automatically approve when the workflow reaches
#                                awaitingApproval (default).
#   --reject                     Automatically reject instead of approving.
#   --no-decision                Do not deliver an approval decision; the workflow
#                                will time out and terminate safely.
#   --verification-value <v>     The value the mock verification runner reports for
#                                each affected service after remediation
#                                (default "healthy"; set to another value to make
#                                verification fail).
#   --timeout <seconds>          Maximum time to wait for completion (default 120).
set -euo pipefail

for tool in curl jq; do
  if ! command -v "${tool}" >/dev/null 2>&1; then
    echo "error: required tool '${tool}' is not installed" >&2
    exit 1
  fi
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API_URL="http://localhost:8080"
SCENARIO=""
INCIDENT_ID=""
DECISION="approved"
VERIFICATION_VALUE="healthy"
TIMEOUT=120

while [[ $# -gt 0 ]]; do
  case "$1" in
    --api-url) API_URL="$2"; shift 2 ;;
    --incident-id) INCIDENT_ID="$2"; shift 2 ;;
    --approve) DECISION="approved"; shift ;;
    --reject) DECISION="rejected"; shift ;;
    --no-decision) DECISION=""; shift ;;
    --verification-value) VERIFICATION_VALUE="$2"; shift 2 ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    -*) echo "error: unknown option '$1'" >&2; exit 1 ;;
    *) SCENARIO="$1"; shift ;;
  esac
done

SCENARIO_DIR="${REPO_ROOT}/scenarios/${SCENARIO}"
if [[ -z "${SCENARIO}" || ! -f "${SCENARIO_DIR}/incident.json" ]]; then
  echo "error: scenario '${SCENARIO}' not found under scenarios/" >&2
  exit 1
fi

if [[ -z "${INCIDENT_ID}" ]]; then
  INCIDENT_ID="$(jq -r '.incidentId' "${SCENARIO_DIR}/incident.json")-$(date +%s)"
fi

# Assemble the submission: the scenario incident (with the run-specific ID)
# plus every evidence fixture rewritten to reference the same incident.
EVIDENCE="$(jq -s -c --arg id "${INCIDENT_ID}" 'map(.incidentId = $id)' "${SCENARIO_DIR}"/evidence/*.json)"
SUBMISSION="$(jq -c --arg id "${INCIDENT_ID}" --argjson evidence "${EVIDENCE}" \
  '{incident: (.incidentId = $id), evidence: $evidence}' "${SCENARIO_DIR}/incident.json")"

# Configure the mock verification runner so post-remediation verification can
# observe the desired value for each affected service (demo-only endpoint).
for service in $(jq -r '.affectedServices[]' "${SCENARIO_DIR}/incident.json"); do
  curl --silent --show-error --fail --output /dev/null \
    --request POST "${API_URL}/demo/verification" \
    --header 'Content-Type: application/json' \
    --data "$(jq -n --arg t "demo/deployment/${service}" --arg v "${VERIFICATION_VALUE}" '{target: $t, actualValue: $v}')"
done

echo "Submitting incident '${INCIDENT_ID}' (scenario ${SCENARIO})..."
curl --silent --show-error --fail --output /dev/null \
  --request POST "${API_URL}/incidents" \
  --header 'Content-Type: application/json' \
  --data "${SUBMISSION}"

DECISION_SENT=false
DEADLINE=$((SECONDS + TIMEOUT))
while (( SECONDS < DEADLINE )); do
  STATUS="$(curl --silent --show-error --fail "${API_URL}/incidents/${INCIDENT_ID}")"
  STATE="$(echo "${STATUS}" | jq -r '.currentState')"
  echo "state: ${STATE}"

  if [[ "$(echo "${STATUS}" | jq -r '.isCompleted')" == "true" ]]; then
    echo "${STATUS}" | jq .
    FINAL="$(echo "${STATUS}" | jq -r '.result.finalState // "unknown"')"
    echo "Workflow completed with final state: ${FINAL}"
    exit 0
  fi

  if [[ "${STATE}" == "awaitingApproval" && -n "${DECISION}" && "${DECISION_SENT}" == "false" ]]; then
    echo "Delivering approval decision: ${DECISION}"
    curl --silent --show-error --fail --output /dev/null \
      --request POST "${API_URL}/incidents/${INCIDENT_ID}/approval" \
      --header 'Content-Type: application/json' \
      --data "$(jq -n --arg o "${DECISION}" '{outcome: $o, approver: "run-scenario.sh", reason: "Scripted scenario run."}')"
    DECISION_SENT=true
  fi

  sleep 2
done

echo "error: workflow did not complete within ${TIMEOUT}s" >&2
exit 1
