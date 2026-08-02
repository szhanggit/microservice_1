#!/usr/bin/env bash
# Rolls back the microservice1 Helm release to a previous revision - the
# immediately preceding one if no revision is given (Helm's own default
# behavior for `helm rollback` with no revision argument), or a specific
# revision number if provided. See `helm history microservice1 -n
# microservice1-<env>` for available revisions before choosing one.
#
# Only reverts the Kubernetes-side manifests (image tags, replica counts,
# config) this Helm release manages - it does NOT touch the database. A
# rollback that also needs a schema/data change requires handling that
# separately; this script deliberately doesn't call
# kubernetes/scripts/apply-db-secret.sh or init-db.sh.
#
# Also doesn't protect against rolling back to a revision whose image tag
# has since been garbage-collected by ECR's lifecycle policy - if that
# happens, this command will succeed but the resulting pods will fail to
# pull the image; check `aws ecr describe-images` for the tag first if
# rolling back to an old revision.
#
# Usage: ./rollback.sh <env> [revision]
#   revision defaults to blank, meaning "the previous revision".
set -euo pipefail

ENV="${1:?Usage: $0 <env> [revision]}"
REVISION="${2:-}"
NAMESPACE="microservice1-$ENV"

echo "Release history before rollback:"
helm history microservice1 -n "$NAMESPACE"

if [ -n "$REVISION" ]; then
  echo ""
  echo "Rolling back microservice1 ($ENV) to revision $REVISION..."
  helm rollback microservice1 "$REVISION" -n "$NAMESPACE" --wait --timeout 5m
else
  echo ""
  echo "Rolling back microservice1 ($ENV) to the previous revision..."
  helm rollback microservice1 -n "$NAMESPACE" --wait --timeout 5m
fi

echo ""
echo "Release history after rollback:"
helm history microservice1 -n "$NAMESPACE"
