#!/usr/bin/env bash
# Installs ExternalDNS via Helm. main.tf's eks_external_dns module only
# provisions its IAM role and IRSA-annotated ServiceAccount (system:
# serviceaccount:default:external-dns) - the controller itself isn't a
# first-party Terraform resource, so it's installed here instead, same
# pattern as install-alb-controller.sh/install-cluster-autoscaler.sh. Reads
# cluster name from SSM Parameter Store - no dependency on the Terraform
# CLI, this project's state, or S3 backend credentials.
#
# policy=upsert-only: ExternalDNS will only create/update records it manages,
# never delete them - ekslab.xyz is a shared personal domain also used by
# microservice_0 (and possibly others), so this avoids any risk of it
# pruning records it didn't create. txtOwnerId is set to the cluster name so
# develop/staging/production don't fight over the same TXT ownership
# registry records if ever applied at once against the same hosted zone.
# Matches microservice_0/kubernetes/scripts/install-external-dns.sh exactly.
#
# Usage: ./install-external-dns.sh <env>
set -euo pipefail

# See apply-db-secret.sh's comment for why - Git Bash mangles leading-slash
# args into Windows paths otherwise. Harmless on WSL/Linux/macOS.
export MSYS_NO_PATHCONV=1

ENV="${1:?Usage: $0 <env>}"
REGION="${AWS_REGION:-ca-central-1}"
SSM_PREFIX="/microservice1/$ENV"

CLUSTER_NAME="$(aws ssm get-parameter --name "$SSM_PREFIX/cluster_name" --region "$REGION" --query 'Parameter.Value' --output text)"

helm repo add external-dns https://kubernetes-sigs.github.io/external-dns/
helm repo update external-dns

helm upgrade --install external-dns external-dns/external-dns \
  -n default \
  --set provider=aws \
  --set aws.region="$REGION" \
  --set serviceAccount.create=false \
  --set serviceAccount.name=external-dns \
  --set txtOwnerId="$CLUSTER_NAME" \
  --set policy=upsert-only \
  --set domainFilters[0]=ekslab.xyz
