#!/usr/bin/env bash
# helm/'s equivalent of kubernetes/scripts/deploy-app.sh - same SSM-sourced
# values (image URLs, Redis/SQS/DynamoDB endpoints), but hands them to
# `helm upgrade --install` via --set instead of envsubst + kubectl apply. See
# prompting/Helm.md §2 "Dynamic values" decision.
#
# db-connection-strings Secret creation and the destructive db-init reset are
# NOT chart-managed (prompting/Helm.md §2 "db-init Job" decision) - delegated
# straight to kubernetes/scripts/apply-db-secret.sh / init-db.sh, same as the
# raw-manifest path uses.
#
# Assumes install-alb-controller has already been run at least once against
# this cluster (see helm/justfile's deploy-all) - the Ingress applied below
# just sits unprocessed forever without that controller running.
#
# Usage: ./deploy-app.sh <env> [tag]
#   tag defaults to the current commit's short SHA - pass an explicit tag to
#   deploy a different build instead.
set -euo pipefail

# See kubernetes/scripts/apply-db-secret.sh's comment for why - Git Bash
# mangles leading-slash args into Windows paths otherwise. Harmless on
# WSL/Linux/macOS.
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HELM_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$HELM_DIR/.." && pwd)"
K8S_SCRIPTS="$REPO_ROOT/kubernetes/scripts"

ENV="${1:?Usage: $0 <env> [tag]}"
TAG="${2:-$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo latest)}"
REGION="${AWS_REGION:-ca-central-1}"
NAMESPACE="microservice1-$ENV"

REPO_URLS_JSON="$(aws ssm get-parameter --name "/microservice1/$ENV/ecr_repository_urls" --region "$REGION" --query 'Parameter.Value' --output text)"
GATEWAY_IMAGE="$(echo "$REPO_URLS_JSON" | jq -r '.transactiongateway'):$TAG"
SERVICE_IMAGE="$(echo "$REPO_URLS_JSON" | jq -r '.transactionservice'):$TAG"
WORKER_IMAGE="$(echo "$REPO_URLS_JSON" | jq -r '.transactionworker'):$TAG"

GW_REDIS_ENDPOINT="$(aws ssm get-parameter --name "/microservice1/$ENV/elasticache_gateway_endpoint" --region "$REGION" --query 'Parameter.Value' --output text)"
SVC_REDIS_ENDPOINT="$(aws ssm get-parameter --name "/microservice1/$ENV/elasticache_service_endpoint" --region "$REGION" --query 'Parameter.Value' --output text)"
SQS_QUEUE_URL="$(aws ssm get-parameter --name "/microservice1/$ENV/sqs_queue_url" --region "$REGION" --query 'Parameter.Value' --output text)"
DYNAMODB_TABLE_NAME="$(aws ssm get-parameter --name "/microservice1/$ENV/dynamodb_table_name" --region "$REGION" --query 'Parameter.Value' --output text)"

echo "namespace  -> $NAMESPACE"
echo "gateway    -> $GATEWAY_IMAGE"
echo "service    -> $SERVICE_IMAGE"
echo "worker     -> $WORKER_IMAGE"
echo "gw redis   -> $GW_REDIS_ENDPOINT"
echo "svc redis  -> $SVC_REDIS_ENDPOINT"
echo "sqs queue  -> $SQS_QUEUE_URL"
echo "dynamodb   -> $DYNAMODB_TABLE_NAME"

"$K8S_SCRIPTS/apply-db-secret.sh" "$ENV"

# Destructive by design - wipes and reseeds transactions/transactions_reporting
# on every deploy (see init-db.sh's own header). Must run after
# apply-db-secret.sh, since the db-init Job reads the same Secret.
"$K8S_SCRIPTS/init-db.sh" "$ENV"

helm upgrade --install microservice1 "$HELM_DIR" \
  --namespace "$NAMESPACE" \
  --set "gateway.image=$GATEWAY_IMAGE" \
  --set "gateway.redis.endpoint=$GW_REDIS_ENDPOINT" \
  --set "service.image=$SERVICE_IMAGE" \
  --set "service.redis.endpoint=$SVC_REDIS_ENDPOINT" \
  --set "service.sqs.queueUrl=$SQS_QUEUE_URL" \
  --set "worker.image=$WORKER_IMAGE" \
  --set "worker.sqs.queueUrl=$SQS_QUEUE_URL" \
  --set "worker.dynamodb.tableName=$DYNAMODB_TABLE_NAME"

echo ""
echo "Deployed via Helm release 'microservice1' in namespace $NAMESPACE."
echo "Check status with:"
echo "  helm status microservice1 -n $NAMESPACE"
echo "  kubectl get pods -n $NAMESPACE"
echo "Once the AWS Load Balancer Controller provisions the ALB:"
echo "  kubectl get ingress -n $NAMESPACE transaction-gateway"
