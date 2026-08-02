#!/usr/bin/env bash
# Tears down the app layer for one environment - run this BEFORE `just
# destroy` in microservice_1_terraform, or the ALB's security groups/ENIs can
# block Terraform from deleting the VPC (same class of problem
# microservice_0/terraform/justfile's destroy warns about).
#
# The namespace/ServiceAccounts/IRSA roles themselves are NOT touched here -
# they're Terraform-managed (kubernetes_namespace.app, irsa-service-role
# modules); remove those via microservice_1_terraform's own destroy.
#
# Captures the Ingress's ExternalDNS hostname *before* deleting anything,
# since it's needed by cleanup-dns.sh afterward - ExternalDNS runs with
# policy=upsert-only and will never delete its own Route53 records itself
# (see cleanup-dns.sh), so this script does that cleanup directly instead of
# just uninstalling ExternalDNS and hoping it reconciled in time. Matches
# microservice_0/kubernetes/scripts/destroy-app.sh.
#
# Usage: ./destroy-app.sh <env>
set -euo pipefail

# See apply-db-secret.sh's comment for why - Git Bash mangles leading-slash
# args into Windows paths otherwise. Harmless on WSL/Linux/macOS.
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV="${1:?Usage: $0 <env>}"
NAMESPACE="microservice1-$ENV"

HOSTNAME="$(kubectl get ingress transaction-gateway -n "$NAMESPACE" \
  -o jsonpath='{.metadata.annotations.external-dns\.alpha\.kubernetes\.io/hostname}' 2>/dev/null || true)"

# Ingress first, and deliberately not --ignore-not-found silently swallowed -
# give the AWS Load Balancer Controller a moment to actually deprovision the
# ALB/target groups before anything else, same reasoning as
# microservice_0/kubernetes/scripts/destroy-app.sh.
kubectl delete ingress transaction-gateway -n "$NAMESPACE" --ignore-not-found --timeout=120s

if [ -n "$HOSTNAME" ]; then
  "$SCRIPT_DIR/cleanup-dns.sh" "$ENV" "$HOSTNAME"
else
  echo "No Ingress hostname found (already deleted?) - skipping DNS cleanup."
fi

kubectl delete deployment,service,hpa,pdb transaction-gateway -n "$NAMESPACE" --ignore-not-found
kubectl delete deployment,service,hpa,pdb transaction-service -n "$NAMESPACE" --ignore-not-found
kubectl delete deployment transaction-worker -n "$NAMESPACE" --ignore-not-found
kubectl delete scaledobject transaction-worker -n "$NAMESPACE" --ignore-not-found
kubectl delete triggerauthentication transaction-worker-sqs-auth -n "$NAMESPACE" --ignore-not-found
kubectl delete secret db-connection-strings -n "$NAMESPACE" --ignore-not-found
kubectl delete job db-init -n "$NAMESPACE" --ignore-not-found
kubectl delete configmap db-init-scripts -n "$NAMESPACE" --ignore-not-found

helm uninstall aws-load-balancer-controller -n kube-system || true
helm uninstall cluster-autoscaler -n kube-system || true
helm uninstall external-dns -n default || true

echo "App layer torn down for $NAMESPACE."
