#!/usr/bin/env bash
# Installs Cluster Autoscaler via Helm. main.tf's eks_cluster_autoscaler
# module only provisions its IAM role and IRSA-annotated ServiceAccount - the
# autoscaler itself isn't a first-party Terraform resource, so it's installed
# here instead. Reads cluster name from SSM Parameter Store - no dependency
# on the Terraform CLI, this project's state, or S3 backend credentials.
#
# variables.tf's node_max_size comment explicitly assumes this actually runs
# ("nothing scales the node group past [node_desired_size] until that
# pipeline is running") - without this, node_max_size=8 is just an unused
# ceiling and pods can get stuck Pending once node_desired_size=4 fills up
# (e.g. TransactionWorker's KEDA burst to 5 replicas).
#
# Usage: ./install-cluster-autoscaler.sh <env>
set -euo pipefail

# See apply-db-secret.sh's comment for why - Git Bash mangles leading-slash
# args into Windows paths otherwise. Harmless on WSL/Linux/macOS.
export MSYS_NO_PATHCONV=1

ENV="${1:?Usage: $0 <env>}"
REGION="${AWS_REGION:-ca-central-1}"
SSM_PREFIX="/microservice1/$ENV"

CLUSTER_NAME="$(aws ssm get-parameter --name "$SSM_PREFIX/cluster_name" --region "$REGION" --query 'Parameter.Value' --output text)"

helm repo add autoscaler https://kubernetes.github.io/autoscaler
helm repo update autoscaler

# Cluster Autoscaler must track the EKS control plane's minor version (see
# https://github.com/kubernetes/autoscaler/blob/master/cluster-autoscaler/README.md#releases).
# Chart 9.44.0 -> CA 1.31.0, matching this cluster's Kubernetes 1.31
# (variables.tf's kubernetes_version). Installing an unpinned/newer CA
# against an older control plane silently hangs the whole autoscaling loop
# (its informer cache sync blocks waiting on APIs the older control plane
# doesn't have yet) - update this pin if kubernetes_version ever changes.
CHART_VERSION="9.44.0"

helm upgrade --install cluster-autoscaler autoscaler/cluster-autoscaler \
  -n kube-system \
  --version "$CHART_VERSION" \
  --set autoDiscovery.clusterName="$CLUSTER_NAME" \
  --set awsRegion="$REGION" \
  --set rbac.serviceAccount.create=false \
  --set rbac.serviceAccount.name=cluster-autoscaler \
  --set extraArgs.balance-similar-node-groups=true \
  --set extraArgs.skip-nodes-with-local-storage=false
