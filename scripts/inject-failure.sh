#!/usr/bin/env bash
# Injects a failure into the local environment for chaos testing.
#
# Usage:
#   scripts/inject-failure.sh <failure>
#
# Failures:
#   delete-api-pod     Deletes the incident-api pod mid-run (workflow restart).
#   restart-redis      Restarts the development Redis instance.
#   pause-api          Scales incident-api to zero replicas.
#   resume-api         Scales incident-api back to one replica.
set -euo pipefail

NAMESPACE="agentic-ops"
FAILURE="${1:-}"

case "${FAILURE}" in
  delete-api-pod)
    kubectl delete pod --namespace "${NAMESPACE}" \
      --selector app.kubernetes.io/name=incident-api --wait=false
    ;;
  restart-redis)
    kubectl rollout restart deployment/redis --namespace "${NAMESPACE}"
    ;;
  pause-api)
    kubectl scale deployment/incident-api --namespace "${NAMESPACE}" --replicas 0
    ;;
  resume-api)
    kubectl scale deployment/incident-api --namespace "${NAMESPACE}" --replicas 1
    ;;
  *)
    echo "usage: $0 {delete-api-pod|restart-redis|pause-api|resume-api}" >&2
    exit 1
    ;;
esac
