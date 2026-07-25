#!/usr/bin/env bash
# Collects logs, workflow state, and cluster diagnostics into results/.
#
# Usage:
#   scripts/collect-results.sh [incident-id ...]
set -euo pipefail

NAMESPACE="agentic-ops"
API_URL="${API_URL:-http://localhost:8080}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="${REPO_ROOT}/results/$(date -u +%Y%m%dT%H%M%SZ)"

mkdir -p "${OUTPUT_DIR}"

if command -v kubectl >/dev/null 2>&1 && kubectl get namespace "${NAMESPACE}" >/dev/null 2>&1; then
  kubectl get all --namespace "${NAMESPACE}" -o wide > "${OUTPUT_DIR}/resources.txt" 2>&1 || true
  kubectl get events --namespace "${NAMESPACE}" --sort-by .lastTimestamp > "${OUTPUT_DIR}/events.txt" 2>&1 || true
  for deployment in incident-api redis; do
    kubectl logs "deployment/${deployment}" --namespace "${NAMESPACE}" --all-containers \
      > "${OUTPUT_DIR}/${deployment}.log" 2>&1 || true
  done
else
  echo "kubectl or namespace '${NAMESPACE}' unavailable; skipping cluster diagnostics" >&2
fi

for incident_id in "$@"; do
  curl --silent --show-error "${API_URL}/incidents/${incident_id}" \
    > "${OUTPUT_DIR}/incident-${incident_id}.json" || true
done

echo "Results collected under ${OUTPUT_DIR}"
