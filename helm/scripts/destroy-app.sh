#!/usr/bin/env bash
# helm/'s equivalent of kubernetes/scripts/destroy-app.sh. Run this BEFORE
# `just destroy` in microservice_1_terraform, or the ALB's security
# groups/ENIs can block Terraform from deleting the VPC - same reasoning as
# the raw-manifest path's destroy-app.sh.
#
# `helm uninstall` alone removes every resource templates/ creates
# (Deployments/Services/HPAs/PDBs/Ingress/ScaledObject/TriggerAuthentication)
# atomically - simpler than the raw-manifest path's per-resource kubectl
# delete list. The db-connection-strings Secret/db-init-scripts
# ConfigMap/db-init Job are NOT chart-managed (prompting/Helm.md §2), so
# still need cleaning up explicitly here, same as before.
#
# Captures the Ingress's ExternalDNS hostname *before* uninstalling, since
# it's needed by cleanup-dns.sh afterward - ExternalDNS runs with
# policy=upsert-only and never deletes its own Route53 records itself.
#
# Usage: ./destroy-app.sh <env>
set -euo pipefail

# See kubernetes/scripts/apply-db-secret.sh's comment for why - Git Bash
# mangles leading-slash args into Windows paths otherwise. Harmless on
# WSL/Linux/macOS.
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
K8S_SCRIPTS="$REPO_ROOT/kubernetes/scripts"

ENV="${1:?Usage: $0 <env>}"
NAMESPACE="microservice1-$ENV"

HOSTNAME="$(kubectl get ingress transaction-gateway -n "$NAMESPACE" \
  -o jsonpath='{.metadata.annotations.external-dns\.alpha\.kubernetes\.io/hostname}' 2>/dev/null || true)"

helm uninstall microservice1 --namespace "$NAMESPACE" --wait --timeout=120s || true

if [ -n "$HOSTNAME" ]; then
  "$K8S_SCRIPTS/cleanup-dns.sh" "$ENV" "$HOSTNAME"
else
  echo "No Ingress hostname found (already deleted?) - skipping DNS cleanup."
fi

kubectl delete secret db-connection-strings -n "$NAMESPACE" --ignore-not-found
kubectl delete job db-init -n "$NAMESPACE" --ignore-not-found
kubectl delete configmap db-init-scripts -n "$NAMESPACE" --ignore-not-found

# Shared cluster add-ons, not owned by this release specifically - same
# uninstalls kubernetes/scripts/destroy-app.sh does. Idempotent (|| true) in
# case the other deploy path's destroy-app already tore them down.
helm uninstall aws-load-balancer-controller -n kube-system || true
helm uninstall cluster-autoscaler -n kube-system || true
helm uninstall external-dns -n default || true

echo "App layer torn down for $NAMESPACE (Helm release 'microservice1')."
