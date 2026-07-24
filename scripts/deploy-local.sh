#!/usr/bin/env bash
# Deploys the system to the local Kubernetes cluster.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEPLOY_DIR="${REPO_ROOT}/deploy/local"

kubectl apply -f "${DEPLOY_DIR}/namespace.yaml"
kubectl apply -f "${DEPLOY_DIR}/redis.yaml"
kubectl apply -f "${DEPLOY_DIR}/dapr-components/"
kubectl apply -f "${DEPLOY_DIR}/incident-api.yaml"

kubectl rollout status deployment/redis --namespace agentic-ops --timeout 120s
kubectl rollout status deployment/incident-api --namespace agentic-ops --timeout 180s

echo "Deployment complete."
echo "Forward the API locally with:"
echo "  kubectl port-forward --namespace agentic-ops service/incident-api 8080:80"
