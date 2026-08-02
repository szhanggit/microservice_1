#!/usr/bin/env bash
# Points your local kubectl at the EKS cluster microservice_1_terraform
# created, reading the cluster name from SSM Parameter Store - no dependency
# on the Terraform CLI, that project's state, or S3 backend credentials.
#
# When run from WSL, also writes the same context into the Windows-side
# kubeconfig (C:\Users\<you>\.kube\config) - so kubectl/helm on the native
# Windows side (PowerShell, Git Bash, etc.) can reach the same cluster
# without a separate manual `aws eks update-kubeconfig` over there. The
# Windows user profile path is discovered via cmd.exe interop rather than
# assumed, since the WSL and Windows usernames can differ.
#
# Usage: ./update-kubeconfig.sh <env>   (env = develop | staging | production)
set -euo pipefail

# See apply-db-secret.sh's comment for why - Git Bash mangles leading-slash
# args into Windows paths otherwise. Harmless on WSL/Linux/macOS.
export MSYS_NO_PATHCONV=1

ENV="${1:?Usage: $0 <env>}"
REGION="${AWS_REGION:-ca-central-1}"

CLUSTER_NAME="$(aws ssm get-parameter --name "/microservice1/$ENV/cluster_name" --region "$REGION" --query 'Parameter.Value' --output text)"

aws eks --region "$REGION" update-kubeconfig --name "$CLUSTER_NAME"

if grep -qi microsoft /proc/version 2>/dev/null; then
  WIN_PROFILE_RAW="$(cmd.exe /c 'echo %USERPROFILE%' 2>/dev/null | tr -d '\r')"
  if [ -n "$WIN_PROFILE_RAW" ] && command -v wslpath >/dev/null 2>&1; then
    WIN_PROFILE="$(wslpath "$WIN_PROFILE_RAW")"
    mkdir -p "$WIN_PROFILE/.kube"
    KUBECONFIG="$WIN_PROFILE/.kube/config" aws eks --region "$REGION" update-kubeconfig --name "$CLUSTER_NAME"
    echo "Also updated Windows kubeconfig at $WIN_PROFILE_RAW\\.kube\\config"
  fi
fi
