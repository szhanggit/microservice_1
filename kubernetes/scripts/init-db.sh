#!/usr/bin/env bash
# Resets the schema via a Kubernetes Job (kubernetes/db-init/job.yaml): drops
# and recreates transactions (shard-1) and transactions_reporting (reporting
# instance) from components/TransactionWorker/resources/postgres/*.sql. The
# Job pulls DB credentials from the db-connection-strings Secret already in
# the cluster (populated by apply-db-secret.sh), so this script itself needs
# no AWS/Secrets Manager access.
#
# Destructive by design - every deploy-all wipes and reseeds the schema. Fine
# for this demo environment; do not wire this into a real production
# pipeline without gating it behind an explicit flag.
#
# Usage: ./init-db.sh <env>
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
K8S_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$K8S_DIR/.." && pwd)"

ENV="${1:?Usage: $0 <env>}"
NAMESPACE="microservice1-$ENV"

SHARD_SQL="$REPO_ROOT/components/TransactionWorker/resources/postgres/01-shard-schema.sql"
REPORTING_SQL="$REPO_ROOT/components/TransactionWorker/resources/postgres/02-reporting-schema.sql"

echo "Publishing db-init-scripts ConfigMap..."
kubectl create configmap db-init-scripts \
  --namespace "$NAMESPACE" \
  --from-file="01-shard-schema.sql=$SHARD_SQL" \
  --from-file="02-reporting-schema.sql=$REPORTING_SQL" \
  --dry-run=client -o yaml | kubectl apply -f -

# Job specs are immutable - delete any previous run before applying a new one.
kubectl delete job db-init --namespace "$NAMESPACE" --ignore-not-found

NAMESPACE="$NAMESPACE" envsubst '${NAMESPACE}' < "$K8S_DIR/db-init/job.yaml" | kubectl apply -f -

echo "Waiting for db-init Job to complete..."
if ! kubectl wait --for=condition=complete job/db-init --namespace "$NAMESPACE" --timeout=120s; then
  echo "db-init Job did not complete successfully - logs:"
  kubectl logs job/db-init --namespace "$NAMESPACE" || true
  exit 1
fi

kubectl logs job/db-init --namespace "$NAMESPACE"
echo "Schema reset complete."
