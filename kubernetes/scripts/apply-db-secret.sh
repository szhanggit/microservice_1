#!/usr/bin/env bash
# Feeds the shard-1/reporting Postgres connection strings - written by
# microservice_1_terraform's ssm-outputs.tf to Secrets Manager - into the
# db-connection-strings Secret. No dependency on the Terraform CLI, that
# project's state, or S3 backend credentials.
#
# These are the MASTER (dbadmin) credentials, used for both
# transaction-worker AND transaction-service - the dedicated read-only
# Postgres role for transaction-service's search path
# (TransactionService.md §2/§6, db-*-readonly-connection-string in Secrets
# Manager) exists as a Terraform variable/secret but has never actually been
# created via CREATE ROLE (no migration tooling built for it yet). Switch
# transaction-service to a separate secret sourced from the *-readonly-*
# entries once that role exists.
#
# Secrets Manager stores "Server=...;User=...;" (Npgsql accepts these as
# aliases for Host/Username, but this project has only ever validated the
# "Host=...;Username=..." form) - reformatted here to that exact form rather
# than relying on untested Npgsql aliasing.
#
# The namespace must already exist first - created directly by Terraform's
# kubernetes_namespace.app resource (main.tf), not by any manifest here.
#
# Usage: ./apply-db-secret.sh <env>
set -euo pipefail

# Git Bash (MSYS2) silently rewrites any argument that looks like a Unix
# absolute path (starts with /) into a Windows path before exec'ing aws.exe -
# e.g. "/microservice1/develop/..." becomes "C:/Program Files/Git/microservice1/...",
# turning every --name/--secret-id below into a lookup for a parameter that
# doesn't exist (confirmed via CloudTrail: AWS received the mangled path, not
# the real one - nothing wrong with IAM/the parameters themselves). Harmless
# on WSL/Linux/macOS, where this variable is simply ignored.
export MSYS_NO_PATHCONV=1

ENV="${1:?Usage: $0 <env>}"
REGION="${AWS_REGION:-ca-central-1}"
NAMESPACE="microservice1-$ENV"

reformat() {
  local raw="$1"
  local host port db user pass
  host="$(grep -oP '(?<=Server=)[^;]*' <<<"$raw")"
  port="$(grep -oP '(?<=Port=)[^;]*' <<<"$raw")"
  db="$(grep -oP '(?<=Database=)[^;]*' <<<"$raw")"
  user="$(grep -oP '(?<=User=)[^;]*' <<<"$raw")"
  pass="$(grep -oP '(?<=Password=)[^;]*' <<<"$raw")"
  echo "Host=$host;Port=$port;Database=$db;Username=$user;Password=$pass"
}

SHARD_RAW="$(aws secretsmanager get-secret-value \
  --secret-id "microservice1/$ENV/db-shard-1-connection-string" \
  --region "$REGION" \
  --query SecretString --output text)"

REPORTING_RAW="$(aws secretsmanager get-secret-value \
  --secret-id "microservice1/$ENV/db-reporting-connection-string" \
  --region "$REGION" \
  --query SecretString --output text)"

kubectl create secret generic db-connection-strings \
  --namespace "$NAMESPACE" \
  --from-literal=shard-connection-string="$(reformat "$SHARD_RAW")" \
  --from-literal=reporting-connection-string="$(reformat "$REPORTING_RAW")" \
  --dry-run=client -o yaml | kubectl apply -f -
