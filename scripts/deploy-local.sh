#!/usr/bin/env bash
# Deploys the system to the local Kubernetes cluster.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEPLOY_DIR="${REPO_ROOT}/deploy/local"

kubectl apply -f "${DEPLOY_DIR}/namespace.yaml"
kubectl apply -f "${DEPLOY_DIR}/redis.yaml"
for f in "${DEPLOY_DIR}/dapr-components"/*.yaml; do
  if [[ "$(basename "$f")" == "secret-store.yaml" ]]; then
    continue
  fi
  kubectl apply -f "$f"
done
kubectl apply -f "${DEPLOY_DIR}/incident-api.yaml"
kubectl apply -f "${DEPLOY_DIR}/ops-console.yaml"

kubectl rollout status deployment/redis --namespace agentic-ops --timeout 120s
kubectl rollout status deployment/incident-api --namespace agentic-ops --timeout 180s
kubectl rollout status deployment/ops-console --namespace agentic-ops --timeout 180s

echo "Deployment complete."
echo "Forward the API locally with:"
echo "  kubectl port-forward --namespace agentic-ops service/incident-api 8080:80"
echo "Open the operations console with:"
echo "  kubectl port-forward --namespace agentic-ops service/ops-console 5080:80"
echo "  # then browse to http://localhost:5080"
