#!/usr/bin/env bash
# Builds each service's Dockerfile and pushes it to the ECR repository
# microservice_1_terraform created for it, reading repo URLs from SSM
# Parameter Store (see ssm-outputs.tf) - no dependency on the Terraform CLI,
# that project's state, or S3 backend credentials.
# Requires: aws cli, docker, jq.
#
# Usage: ./build-and-push-images.sh <env> [tag]
#   env is develop | staging | production (must match what
#   microservice_1_terraform was applied with). tag defaults to the short git
#   SHA, falling back to "latest".
set -euo pipefail

# See apply-db-secret.sh's comment for why - Git Bash mangles leading-slash
# args into Windows paths otherwise. Harmless on WSL/Linux/macOS.
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

ENV="${1:?Usage: $0 <env> [tag]}"
TAG="${2:-$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo latest)}"
REGION="${AWS_REGION:-ca-central-1}"

echo "Reading ECR repository URLs from SSM Parameter Store..."
REPO_URLS_JSON="$(aws ssm get-parameter --name "/microservice1/$ENV/ecr_repository_urls" --region "$REGION" --query 'Parameter.Value' --output text)"

ACCOUNT_ID="$(aws sts get-caller-identity --query Account --output text)"
REGISTRY="$ACCOUNT_ID.dkr.ecr.$REGION.amazonaws.com"

echo "Logging in to $REGISTRY..."
aws ecr get-login-password --region "$REGION" | docker login --username AWS --password-stdin "$REGISTRY"

# service name -> Dockerfile path (relative to repo root), matching each
# component's docker-compose.yml `dockerfile:` entry exactly. Build context is
# always components/ (one level above each service's own directory), never
# the service directory itself - TransactionService and TransactionWorker
# both depend on sibling projects (contracts/, ShardRouting/) that live
# outside their own directory.
declare -A DOCKERFILES=(
  [transactiongateway]="components/TransactionGateway/Dockerfile"
  [transactionservice]="components/TransactionService/Dockerfile"
  [transactionworker]="components/TransactionWorker/Dockerfile"
)

for service in "${!DOCKERFILES[@]}"; do
  image_url="$(echo "$REPO_URLS_JSON" | jq -r --arg svc "$service" '.[$svc]')"
  dockerfile="$REPO_ROOT/${DOCKERFILES[$service]}"
  context="$REPO_ROOT/components"

  echo ""
  echo "=== $service -> $image_url:$TAG ==="
  docker build -f "$dockerfile" -t "$image_url:$TAG" -t "$image_url:latest" "$context"
  docker push "$image_url:$TAG"
  docker push "$image_url:latest"
done

echo ""
echo "All images pushed with tag '$TAG' (and 'latest')."
