#!/usr/bin/env bash
# Applies kubernetes/ to whatever cluster kubectl is currently pointed at,
# substituting image/endpoint placeholders in the manifests with real values
# from SSM Parameter Store. No dependency on the Terraform CLI, that
# project's state, or S3 backend credentials. Requires: envsubst (gettext),
# jq, aws cli, kubectl pointed at the target cluster (see
# update-kubeconfig.sh).
#
# The namespace itself is NOT created here - it's already created directly by
# Terraform's kubernetes_namespace.app resource (main.tf), same for the
# transaction-service/transaction-worker ServiceAccounts (irsa-service-role
# module creates the kubernetes_service_account directly, IRSA-annotated).
#
# Assumes install-alb-controller.sh has already been run at least once
# against this cluster (see justfile's deploy-all) - the Ingress applied
# below just sits unprocessed forever without that controller running.
#
# Usage: ./deploy-app.sh <env> [tag]
#   tag defaults to the current commit's short SHA (same default
#   build-and-push-images.sh uses) - pass an explicit tag if you want to
#   deploy a different build instead.
set -euo pipefail

# See apply-db-secret.sh's comment for why - Git Bash mangles leading-slash
# args into Windows paths otherwise. Harmless on WSL/Linux/macOS.
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
K8S_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$K8S_DIR/.." && pwd)"

ENV="${1:?Usage: $0 <env> [tag]}"
TAG="${2:-$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo latest)}"
REGION="${AWS_REGION:-ca-central-1}"

export NAMESPACE="microservice1-$ENV"

REPO_URLS_JSON="$(aws ssm get-parameter --name "/microservice1/$ENV/ecr_repository_urls" --region "$REGION" --query 'Parameter.Value' --output text)"
export GATEWAY_IMAGE="$(echo "$REPO_URLS_JSON" | jq -r '.transactiongateway'):$TAG"
export SERVICE_IMAGE="$(echo "$REPO_URLS_JSON" | jq -r '.transactionservice'):$TAG"
export WORKER_IMAGE="$(echo "$REPO_URLS_JSON" | jq -r '.transactionworker'):$TAG"

export GW_REDIS_ENDPOINT="$(aws ssm get-parameter --name "/microservice1/$ENV/elasticache_gateway_endpoint" --region "$REGION" --query 'Parameter.Value' --output text)"
export SVC_REDIS_ENDPOINT="$(aws ssm get-parameter --name "/microservice1/$ENV/elasticache_service_endpoint" --region "$REGION" --query 'Parameter.Value' --output text)"
export SQS_QUEUE_URL="$(aws ssm get-parameter --name "/microservice1/$ENV/sqs_queue_url" --region "$REGION" --query 'Parameter.Value' --output text)"
export DYNAMODB_TABLE_NAME="$(aws ssm get-parameter --name "/microservice1/$ENV/dynamodb_table_name" --region "$REGION" --query 'Parameter.Value' --output text)"

echo "namespace  -> $NAMESPACE"
echo "gateway    -> $GATEWAY_IMAGE"
echo "service    -> $SERVICE_IMAGE"
echo "worker     -> $WORKER_IMAGE"
echo "gw redis   -> $GW_REDIS_ENDPOINT"
echo "svc redis  -> $SVC_REDIS_ENDPOINT"
echo "sqs queue  -> $SQS_QUEUE_URL"
echo "dynamodb   -> $DYNAMODB_TABLE_NAME"

"$SCRIPT_DIR/apply-db-secret.sh" "$ENV"

# Destructive by design - wipes and reseeds transactions/transactions_reporting
# on every deploy (see init-db.sh's own header). Must run after
# apply-db-secret.sh, since the db-init Job reads the same Secret.
"$SCRIPT_DIR/init-db.sh" "$ENV"

envsubst '${NAMESPACE} ${GATEWAY_IMAGE} ${GW_REDIS_ENDPOINT}' < "$K8S_DIR/transaction-gateway/deployment.yaml" | kubectl apply -f -
envsubst '${NAMESPACE}' < "$K8S_DIR/transaction-gateway/service.yaml" | kubectl apply -f -
envsubst '${NAMESPACE}' < "$K8S_DIR/transaction-gateway/ingress.yaml" | kubectl apply -f -
envsubst '${NAMESPACE}' < "$K8S_DIR/transaction-gateway/hpa.yaml" | kubectl apply -f -
envsubst '${NAMESPACE}' < "$K8S_DIR/transaction-gateway/pdb.yaml" | kubectl apply -f -

envsubst '${NAMESPACE} ${SERVICE_IMAGE} ${SVC_REDIS_ENDPOINT} ${SQS_QUEUE_URL}' < "$K8S_DIR/transaction-service/deployment.yaml" | kubectl apply -f -
envsubst '${NAMESPACE}' < "$K8S_DIR/transaction-service/service.yaml" | kubectl apply -f -
envsubst '${NAMESPACE}' < "$K8S_DIR/transaction-service/hpa.yaml" | kubectl apply -f -
envsubst '${NAMESPACE}' < "$K8S_DIR/transaction-service/pdb.yaml" | kubectl apply -f -

envsubst '${NAMESPACE} ${WORKER_IMAGE} ${SQS_QUEUE_URL} ${DYNAMODB_TABLE_NAME}' < "$K8S_DIR/transaction-worker/deployment.yaml" | kubectl apply -f -
envsubst '${NAMESPACE}' < "$K8S_DIR/transaction-worker/triggerauthentication.yaml" | kubectl apply -f -
envsubst '${NAMESPACE} ${SQS_QUEUE_URL}' < "$K8S_DIR/transaction-worker/scaledobject.yaml" | kubectl apply -f -

echo ""
echo "Deployed. transaction-worker starts at 0 replicas (KEDA-scaled, see"
echo "scaledobject.yaml) - it comes up once a message lands in the queue."
echo "Check status with:"
echo "  kubectl get pods -n $NAMESPACE"
echo "Once the AWS Load Balancer Controller provisions the ALB:"
echo "  kubectl get ingress -n $NAMESPACE transaction-gateway"
