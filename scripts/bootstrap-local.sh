#!/usr/bin/env bash
# Creates the local Kubernetes cluster and installs Dapr.
# Prerequisites: docker, k3d, kubectl, dapr CLI.
set -euo pipefail

CLUSTER_NAME="${CLUSTER_NAME:-agentic-ops}"

for tool in docker k3d kubectl dapr; do
  if ! command -v "${tool}" >/dev/null 2>&1; then
    echo "error: required tool '${tool}' is not installed" >&2
    exit 1
  fi
done

if k3d cluster list --output json | grep -q "\"name\":\"${CLUSTER_NAME}\""; then
  echo "k3d cluster '${CLUSTER_NAME}' already exists; skipping creation"
else
  k3d cluster create "${CLUSTER_NAME}" --wait
fi

kubectl config use-context "k3d-${CLUSTER_NAME}"

if kubectl get namespace dapr-system >/dev/null 2>&1; then
  echo "Dapr is already installed; skipping 'dapr init -k'"
else
  dapr init -k --wait
fi

echo "Local cluster '${CLUSTER_NAME}' is ready."
echo "Next: scripts/build-images.sh && scripts/deploy-local.sh"
