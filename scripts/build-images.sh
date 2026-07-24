#!/usr/bin/env bash
# Builds the service container images and imports them into the k3d cluster.
set -euo pipefail

CLUSTER_NAME="${CLUSTER_NAME:-agentic-ops}"
IMAGE_TAG="${IMAGE_TAG:-local}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

docker build \
  --file "${REPO_ROOT}/src/IncidentApi/Dockerfile" \
  --tag "incident-api:${IMAGE_TAG}" \
  "${REPO_ROOT}"

docker build \
  --file "${REPO_ROOT}/src/OpsConsole/Dockerfile" \
  --tag "ops-console:${IMAGE_TAG}" \
  "${REPO_ROOT}"

if command -v k3d >/dev/null 2>&1 && k3d cluster list --output json | grep -q "\"name\":\"${CLUSTER_NAME}\""; then
  k3d image import "incident-api:${IMAGE_TAG}" "ops-console:${IMAGE_TAG}" --cluster "${CLUSTER_NAME}"
else
  echo "k3d cluster '${CLUSTER_NAME}' not found; skipping image import" >&2
fi
