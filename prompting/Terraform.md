# microservice_1 Terraform Project

## 1. Requirements

- Terraform project in `D:\git\microservice_1_terraform`, using `D:\git\microservice_0\terraform` as a reference.
- Region `ca-central-1`.
- Cluster names: `microservice1-develop`, `microservice1-staging`, `microservice1-production`.
- Reuse the reference project's patterns with properly renamed variables — not a blind copy.
- Don't copy components from the reference that aren't actually needed here.
- Cover every new component implied by `database.md`, `KEDA.md`, `SQS.md`, `TransactionGateway.md`, `TransactionService.md`.
- State buckets: `steven-zhang-learning/microservice1_dev` (dev), `_stage` (stage), `_prod` (prod).

## 2. What the Reference Project Actually Has

Explored `D:\git\microservice_0\terraform` directly rather than guessing. Relevant findings:

- **Backend**: one shared S3 bucket, only the state `key` differs per environment; locking via Terraform's native `use_lockfile` (Terraform ≥ 1.10) — no DynamoDB lock table.
- **`modules/rds`**: a single, non-Aurora, non-Multi-AZ MySQL instance for one database. Doesn't fit `database.md`'s sharded/Aurora design — not reusable as-is.
- **No KEDA equivalent** — that project scales via plain Kubernetes HPA.
- **`github-actions-oidc/`**: a separate-state singleton creating an IAM role trusted via GitHub OIDC, scoped to one repo using immutable owner/repo numeric IDs (not just names) — those IDs had to be discovered via CloudTrail after the name-only trust policy failed with `AccessDenied` on the first real `AssumeRoleWithWebIdentity` call.
- **`ssm-outputs.tf`**: publishes cluster/VPC/ECR/DB info to SSM Parameter Store (plus a Secrets Manager entry for the DB connection string) so a separate application/Kubernetes CI pipeline can read it without needing Terraform state or CLI access.
- Add-on modules present: `eks-alb-controller`, `eks-cluster-autoscaler`, `eks-container-insights`, `eks-ebs-csi`, `eks-external-dns`, `eks-fargate-profile`, `eks-xray`. The Fargate profile's own variable admits "no workload targets this namespace today" — decorative even in the source project.

## 3. Decisions

| Decision | Choice | Why |
|---|---|---|
| State buckets | 3 separate buckets (dev/stage/prod, as given), not the reference's one-shared-bucket pattern | Explicit instruction — kept as specified even though it diverges from the reference. |
| State locking | Terraform native `use_lockfile` (no DynamoDB table) | No reason to deviate from the reference's pattern here just because the bucket topology differs — native locking works per-bucket the same way. |
| Aurora sizing | Aurora Serverless v2, low min ACU, all 4 clusters (3 shards + reporting) | Matches the suggestion already flagged in `KEDA.md` §8 — scales down automatically, cheapest option for demo-level, intermittent traffic. |
| `rds` module | **Not reused** — new `aurora-mysql-cluster` module | The reference's `rds` module is a single non-Aurora MySQL instance; doesn't fit the sharded design at all. |
| KEDA installation | New `keda` module (Helm release) | No equivalent exists in the reference project. |
| EKS add-ons carried over | `eks-alb-controller`, `eks-cluster-autoscaler`, `eks-external-dns`, `eks-ebs-csi` | ALB Controller is required for the Gateway's `Ingress` (`TransactionGateway.md` §3/§6); cluster autoscaler complements KEDA's pod-level scaling with node-level scaling; External DNS and EBS CSI kept per your explicit choice. |
| EKS add-ons dropped | `eks-fargate-profile`, `eks-container-insights`, `eks-xray` | Fargate profile is unused filler even in the source. Container Insights/X-Ray would ship real data to CloudWatch/X-Ray, which cuts against the "no shipped observability, save cost" decision already made in `KEDA.md` §2/`TransactionGateway.md` §2. |
| External DNS zone | Reuse `ekslab.xyz` (same as microservice_0) | Your choice — one hosted zone, environment-scoped subdomains (e.g. `transactiongateway-develop.ekslab.xyz`). |
| GitHub Actions OIDC | New singleton, repo `szhanggit/microservice_1` | Confirmed via `git remote -v` in the app repo. Immutable owner/repo numeric IDs discovered the same way the reference project documents — via CloudTrail after the first real `AssumeRoleWithWebIdentity` attempt — not guessable up front. |
| Config handoff | Replicate the SSM Parameter Store + Secrets Manager pattern for all new resources | Consistency with the reference; keeps a future app-deploy pipeline decoupled from Terraform state/CLI access, same as `ssm-outputs.tf` already does there. |
| Variable naming | Every `microservice0`-flavored default renamed to `microservice1` equivalents; `db_*`/RDS variables dropped entirely, replaced by `aurora_*` | Per your explicit instruction — this is a reference for patterns, not a copy-paste source. |

## 4. Project Layout

```
D:\git\microservice_1_terraform/
├── backend.tf                    # bucket/key/region blank, filled via -backend-config per env
├── main.tf
├── variables.tf
├── outputs.tf
├── ssm-outputs.tf
├── justfile                      # bootstrap-backend / init / plan / apply / destroy, per env
├── scripts/
│   └── bootstrap-backend.sh      # creates all 3 (separate) buckets, one per environment
├── github-actions-oidc/          # separate state, singleton
│   ├── backend.tf
│   ├── main.tf                   # trust policy scoped to szhanggit/microservice_1
│   ├── variables.tf
│   └── outputs.tf
├── modules/
│   ├── vpc/                       # reused
│   ├── eks-cluster/                # reused
│   ├── eks-nodegroup/               # reused
│   ├── eks-alb-controller/          # reused
│   ├── eks-cluster-autoscaler/       # reused
│   ├── eks-external-dns/             # reused, route53_zone_id = ekslab.xyz's zone
│   ├── eks-ebs-csi/                  # reused
│   ├── ecr/                          # reused, repository_names = [transactiongateway, transactionservice, transactionworker]
│   ├── aurora-mysql-cluster/          # NEW — replaces `rds`; applied 4x
│   ├── dynamodb/                     # NEW — transaction-claims table
│   ├── sqs/                          # NEW — TransactionQueue + DLQ
│   ├── elasticache/                  # NEW — applied 2x (Gateway, Service)
│   ├── keda/                         # NEW — Helm release
│   └── irsa-service-role/            # NEW — generic per-service IAM role for IRSA
└── environments/
    ├── develop/     (backend.tfvars → microservice1_dev,   develop.tfvars,    secrets.tfvars)
    ├── staging/     (backend.tfvars → microservice1_stage, staging.tfvars,    secrets.tfvars)
    └── production/  (backend.tfvars → microservice1_prod,  production.tfvars, secrets.tfvars)
```

## 5. Modules Reused From the Reference (Renamed Only)

`vpc`, `eks-cluster`, `eks-nodegroup`, `eks-alb-controller`, `eks-cluster-autoscaler`, `eks-external-dns`, `eks-ebs-csi`, `ecr` — same logic, but every `microservice0`-specific default is renamed:

- `cluster_name` default → `microservice1`; `ecr_repository_names` → `["transactiongateway", "transactionservice", "transactionworker"]`; `route53_zone_id` → the `ekslab.xyz` zone.
- All `db_*`/RDS-related root variables (`db_instance_identifier`, `db_engine_version`, `db_master_username`, etc.) are **dropped entirely** — replaced by the `aurora_*` variables the new module needs (§6).

## 6. New Modules

| Module | Purpose | Key config (from the linked docs) |
|---|---|---|
| `aurora-mysql-cluster` | One Aurora MySQL Serverless v2 cluster; applied 4x | Schema from `database.md` §6/§7; identifiers `microservice1-<env>-shard-0/1/2` and `microservice1-<env>-reporting`; low min ACU |
| `dynamodb` | `transaction-claims-<env>` table | `gsi_status_lease` GSI, TTL on `ttl`, `PAY_PER_REQUEST` billing (`KEDA.md` §5) |
| `sqs` | `TransactionQueue-<env>` + `TransactionQueue-<env>-dlq` | Exact config from `SQS.md` §3 (Standard, SSE-SQS, 30s visibility, `maxReceiveCount=5`, 4-day retention, 20s long polling) |
| `elasticache` | Redis, applied 2x per environment | Cheapest single-node instance, one for `TransactionGateway`, one for `TransactionService` (`TransactionGateway.md` §6, `TransactionService.md` §6), identifiers suffixed `-<env>` like everything else |

All per-environment resource names follow the same `-<env>` (or `microservice1-<env>-...`) suffix convention established by the Aurora clusters and the EKS cluster names — every environment shares one AWS account, so naming (not account separation) is what keeps them from colliding.
| `keda` | Helm release of the KEDA core chart into the cluster | Namespace `keda`; `ScaledObject`/`TriggerAuthentication` manifests themselves live with the app, not here |
| `irsa-service-role` | Generic module: takes the cluster's `oidc_provider_arn`/`url` + a k8s namespace/service-account name + a list of IAM statements, outputs a role ARN | Applied per service needing AWS access (§7) — `TransactionGateway` needs none, since it never calls an AWS API directly |

## 7. IRSA Roles (via `irsa-service-role`)

| Service | Permissions | Scope |
|---|---|---|
| `TransactionService` | `sqs:SendMessage` | `TransactionQueue` ARN only |
| `TransactionWorker` | `sqs:ReceiveMessage`, `sqs:DeleteMessage`, `sqs:GetQueueAttributes`, `dynamodb:PutItem`/`UpdateItem`/`Query` on `transaction-claims` + its GSI | `TransactionQueue` + `transaction-claims` table/GSI ARNs only |
| KEDA `TriggerAuthentication` | `sqs:GetQueueAttributes` | `TransactionQueue` ARN only |

`TransactionGateway` gets no IRSA role — it only talks to Redis and gRPC, never an AWS API directly (pulling its container image is the node role's job, not the pod's).

## 8. Environments & Backend

- 3 separate S3 buckets as given in §1 (not the reference's shared-bucket-with-per-env-key pattern).
- `scripts/bootstrap-backend.sh` adapted to create all 3 buckets (one per environment) instead of one shared bucket.
- `backend.tf` stays parameter-free (bucket/key/region filled at `terraform init` time via `-backend-config`), same as the reference — only now each environment's `backend.tfvars` points at a wholly separate bucket, not just a different `key` in the same one.
- Cluster names exactly as given: `microservice1-develop`, `microservice1-staging`, `microservice1-production`.

## 9. GitHub Actions OIDC (`github-actions-oidc/`)

- New singleton state, separate from the per-environment applies — same structure as the reference.
- `github_repo = "szhanggit/microservice_1"` (confirmed via `git remote -v`).
- `github_owner_id` / `github_repo_id`: placeholder values to start; the first real `AssumeRoleWithWebIdentity` call will fail `AccessDenied` against a name-only policy, at which point the actual immutable IDs are read from CloudTrail (`aws cloudtrail lookup-events --lookup-attributes AttributeKey=EventName,AttributeValue=AssumeRoleWithWebIdentity`) and applied — documented as an explicit manual step in §10, not something to guess.
- `role_name = "github-actions-microservice1"`.

## 10. Config Handoff (`ssm-outputs.tf`)

`ssm_prefix = "/microservice1/${var.environment}"`. Published, mirroring the reference's pattern:

- SSM: `cluster_name`, `region`, `vpc_id`, `ecr_repository_urls` (JSON map) — same as reference.
- SSM: `sqs_queue_url`, `sqs_queue_arn`, `dynamodb_table_name`, ElastiCache endpoints (2, one per service), Aurora cluster endpoints (4 — non-secret hostnames only).
- Secrets Manager: Aurora master connection strings (4, one per cluster) — holds credentials, same reasoning as the reference keeping the DB connection string out of plain SSM.

## 11. Implementation Plan

**Phase 1 — Bootstrap**
- Adapt `scripts/bootstrap-backend.sh` for 3 separate buckets.
- Apply `github-actions-oidc/` once with placeholder owner/repo IDs; fix them from CloudTrail after the first real workflow run fails; re-apply.

**Phase 2 — Foundation**
- `vpc`, `eks-cluster`, `eks-nodegroup` — renamed variables/defaults (§5) — applied per environment.

**Phase 3 — EKS add-ons**
- `eks-alb-controller`, `eks-cluster-autoscaler`, `eks-external-dns` (zone `ekslab.xyz`), `eks-ebs-csi`.

**Phase 4 — ECR**
- 3 repositories: `transactiongateway`, `transactionservice`, `transactionworker`.

**Phase 5 — Data layer (new modules)**
- `aurora-mysql-cluster` ×4, `dynamodb`, `sqs`, `elasticache` ×2 — configs exactly matching `database.md`/`KEDA.md`/`SQS.md`/`TransactionGateway.md`/`TransactionService.md`.

**Phase 6 — KEDA**
- Helm-release the KEDA core chart into the cluster via the `keda` module.

**Phase 7 — IRSA roles**
- `irsa-service-role` applied for `TransactionService`, `TransactionWorker`, and the KEDA `TriggerAuthentication` (§7).

**Phase 8 — Config handoff**
- `ssm-outputs.tf` publishing everything in §10.

**Phase 9 — Validation**
- `terraform plan`/`apply` against `develop` end-to-end; confirm KEDA can read `ApproximateNumberOfMessages`; confirm each IRSA role can do only what it's scoped for (e.g. `TransactionService`'s role rejected on a `ReceiveMessage` attempt).

Create a just file.