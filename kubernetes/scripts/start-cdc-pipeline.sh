#!/usr/bin/env bash
# Starts (or resumes) the DMS CDC replication task(s) that feed
# transactions_reporting (database.md §7, Terraform.md §6). Idempotent -
# safe to run on every deploy:
#   - "ready"   (never started, e.g. right after a fresh terraform apply) -> start-replication
#   - "stopped" (was running, got stopped)                                -> resume-processing
#   - "failed"                                                            -> start-replication (retry)
#   - "running"/"starting"                                                -> no-op
#
# migration_type is fixed to "cdc" (CDC-only, no initial full load - see
# modules/dms-source-endpoint-task/main.tf) by deliberate design, not an
# oversight: both this directory's and helm/'s deploy-app.sh always run
# init-db.sh first, which drops and recreates
# transactions/transactions_reporting on every deploy. So by the time this
# script runs (right after deploy-app), the shard is
# already empty - starting CDC-only replication at exactly this moment means
# every row that will ever exist in this fresh environment gets captured,
# with no backfill gap. Running this at any other time (e.g. weeks into a
# session, against a shard that already has data) would still need a manual
# `aws dms reload-tables` afterward to backfill what CDC missed.
#
# Usage: ./start-cdc-pipeline.sh [env]
set -euo pipefail

ENV="${1:-develop}"
REGION="${AWS_REGION:-ca-central-1}"
INSTANCE_ID="microservice1-$ENV-cdc"

echo "Looking up DMS replication instance $INSTANCE_ID..."
INSTANCE_ARN="$(aws dms describe-replication-instances --region "$REGION" \
  --filters "Name=replication-instance-id,Values=$INSTANCE_ID" \
  --query 'ReplicationInstances[0].ReplicationInstanceArn' --output text 2>/dev/null || true)"

if [ -z "$INSTANCE_ARN" ] || [ "$INSTANCE_ARN" == "None" ]; then
  echo "No DMS replication instance named $INSTANCE_ID found - skipping (CDC pipeline not provisioned for this environment)."
  exit 0
fi

TASK_ARNS="$(aws dms describe-replication-tasks --region "$REGION" \
  --filters "Name=replication-instance-arn,Values=$INSTANCE_ARN" \
  --query 'ReplicationTasks[].ReplicationTaskArn' --output text)"

if [ -z "$TASK_ARNS" ]; then
  echo "No replication tasks found for $INSTANCE_ID - nothing to start."
  exit 0
fi

# start-replication-task fails outright ("Test connection ... should be
# successful") unless DMS already has a cached successful test for each
# (instance, endpoint) pair - always untested right after a fresh apply, so
# every source/target endpoint used by any task on this instance is
# (re)tested before attempting to start anything.
ENDPOINT_ARNS="$(aws dms describe-replication-tasks --region "$REGION" \
  --filters "Name=replication-instance-arn,Values=$INSTANCE_ARN" \
  --query 'ReplicationTasks[].[SourceEndpointArn,TargetEndpointArn]' --output text | tr '\t' '\n' | sort -u)"

for EP in $ENDPOINT_ARNS; do
  echo "Testing connection: $EP"
  aws dms test-connection --region "$REGION" \
    --replication-instance-arn "$INSTANCE_ARN" --endpoint-arn "$EP" >/dev/null 2>&1 || true
done

echo "Waiting for connection tests to complete..."
for _ in $(seq 1 30); do
  STILL_TESTING="$(aws dms describe-connections --region "$REGION" \
    --filters "Name=replication-instance-arn,Values=$INSTANCE_ARN" \
    --query "length(Connections[?Status=='testing'])" --output text)"
  if [ "$STILL_TESTING" == "0" ]; then
    break
  fi
  sleep 5
done

FAILED="$(aws dms describe-connections --region "$REGION" \
  --filters "Name=replication-instance-arn,Values=$INSTANCE_ARN" \
  --query "Connections[?Status=='failed'].EndpointIdentifier" --output text)"
if [ -n "$FAILED" ]; then
  echo "::error::DMS connection test failed for: $FAILED - not starting CDC task(s). Check endpoint ssl_mode / security groups (see modules/dms(-source-endpoint-task)'s ssl_mode comment)."
  exit 1
fi

for TASK_ARN in $TASK_ARNS; do
  TASK_INFO="$(aws dms describe-replication-tasks --region "$REGION" \
    --filters "Name=replication-task-arn,Values=$TASK_ARN" \
    --query 'ReplicationTasks[0].[ReplicationTaskIdentifier,Status]' --output text)"
  TASK_ID="$(echo "$TASK_INFO" | cut -f1)"
  STATUS="$(echo "$TASK_INFO" | cut -f2)"

  case "$STATUS" in
    running|starting)
      echo "$TASK_ID already $STATUS - nothing to do."
      ;;
    ready)
      echo "$TASK_ID is ready (never started) - starting replication."
      aws dms start-replication-task --region "$REGION" --replication-task-arn "$TASK_ARN" \
        --start-replication-task-type start-replication >/dev/null
      ;;
    stopped)
      echo "$TASK_ID is stopped - resuming from its last position."
      aws dms start-replication-task --region "$REGION" --replication-task-arn "$TASK_ARN" \
        --start-replication-task-type resume-processing >/dev/null
      ;;
    failed)
      echo "$TASK_ID previously failed - restarting from scratch."
      aws dms start-replication-task --region "$REGION" --replication-task-arn "$TASK_ARN" \
        --start-replication-task-type start-replication >/dev/null
      ;;
    *)
      echo "$TASK_ID is in unexpected status '$STATUS' - leaving it alone."
      ;;
  esac
done

echo ""
echo "CDC pipeline start requested. Check progress with:"
echo "  aws dms describe-replication-tasks --region $REGION --query 'ReplicationTasks[].{Id:ReplicationTaskIdentifier,Status:Status}' --output table"
