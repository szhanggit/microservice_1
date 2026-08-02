# microservice_1 Terraform Project

## 1. Requirements

- Terraform project in `D:\git\microservice_1_terraform`, using `D:\git\microservice_0\terraform` as a reference.
- Region `ca-central-1`.
- Cluster names: `microservice1-develop`, `microservice1-staging`, `microservice1-production`.
- Reuse the reference project's patterns with properly renamed variables — not a blind copy.
- Don't copy components from the reference that aren't actually needed here.
- Cover every new component implied by `database.md`, `KEDA.md`, `SQS.md`, `TransactionGateway.md`, `TransactionService.md`.
- State: `steven-zhang-learning/microservice1_dev` (dev), `_stage` (stage), `_prod` (prod) — one shared bucket (`steven-zhang-learning`), different key prefix per environment.

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
| State backend | Same shared bucket as the reference (`steven-zhang-learning`), one key prefix per environment (`microservice1_dev`/`_stage`/`_prod`) | Matches the reference's pattern exactly — the bucket names you gave are `bucket/key` pairs (S3 bucket names can't contain `/`), not three separate buckets. |
| State locking | Terraform native `use_lockfile` (no DynamoDB table) | Same as the reference — no reason to deviate. |
| DB engine & topology | **Plain RDS PostgreSQL** (`db.t3.micro`, Single-AZ, no Aurora at all) — 2 pivots from the original design, both discovered only by actually running `terraform apply` | (1) Aurora MySQL → Aurora PostgreSQL: this account's Free Tier plan rejects `aurora-mysql` entirely (`CreateDBCluster: FreeTierRestrictionError` — only `aurora-postgresql` offered). (2) Aurora PostgreSQL → plain RDS: Free Tier Aurora *clusters* require AWS's new "Express Configuration" mode, which uses its own "Internet Access Gateway" networking and explicitly rejects a custom VPC subnet group/security group (confirmed via a direct `aws rds create-db-cluster --with-express-configuration` test) — incompatible with keeping the DB private inside our VPC. `database.md` has been updated throughout (DDL, partitioning, error handling, topology) to match. |
| DB master username | `dbadmin`, not `admin` | `admin` is a reserved word for the RDS `postgres` engine (`CreateDBInstance: InvalidParameterValue`) — another Free Tier/engine surprise caught only at apply time. `dbadmin` matches `microservice_0`'s own `rds` module default, already proven to work on this account. |
| `rds` module | **Not reused directly** — new `rds-postgres-instance` module, same shape (single instance, not a cluster) | Ironic full circle: originally rejected the reference's `rds` module because Aurora's sharded-cluster design needed something Aurora-specific; after both pivots above, we ended up needing almost exactly what `rds` already does, just Postgres instead of MySQL. |
| KEDA installation | New `keda` module (Helm release) | No equivalent exists in the reference project. |
| EKS add-ons carried over | `eks-alb-controller`, `eks-cluster-autoscaler`, `eks-external-dns`, `eks-ebs-csi` | ALB Controller is required for the Gateway's `Ingress` (`TransactionGateway.md` §3/§6); cluster autoscaler complements KEDA's pod-level scaling with node-level scaling; External DNS and EBS CSI kept per your explicit choice. |
| EKS add-ons dropped | `eks-fargate-profile`, `eks-container-insights`, `eks-xray` | Fargate profile is unused filler even in the source. Container Insights/X-Ray would ship real data to CloudWatch/X-Ray, which cuts against the "no shipped observability, save cost" decision already made in `KEDA.md` §2/`TransactionGateway.md` §2. |
| External DNS zone | Reuse `ekslab.xyz` (same as microservice_0) | Your choice — one hosted zone, environment-scoped subdomains (e.g. `transactiongateway-develop.ekslab.xyz`). |
| GitHub Actions OIDC | New singleton, repo `szhanggit/microservice_1` | Confirmed via `git remote -v` in the app repo. Immutable owner/repo numeric IDs discovered the same way the reference project documents — via CloudTrail after the first real `AssumeRoleWithWebIdentity` attempt — not guessable up front. |
| Config handoff | Replicate the SSM Parameter Store + Secrets Manager pattern for all new resources | Consistency with the reference; keeps a future app-deploy pipeline decoupled from Terraform state/CLI access, same as `ssm-outputs.tf` already does there. |
| Variable naming | Every `microservice0`-flavored default renamed to `microservice1` equivalents; kept `db_*` naming (briefly renamed to `aurora_*` mid-way through the Aurora attempt, reverted back to `db_*` once plain RDS became the final answer) | Per your explicit instruction — this is a reference for patterns, not a copy-paste source. |
| CDC pipeline (new) | New `dms` + `dms-source-endpoint-task` modules for `database.md` §7's reporting instance | `TransactionService.md`'s search-by-date-range path needs the reporting instance actually fed with data — see `database.md` §7's status note. **Written but not applied/validated against real AWS** — you explicitly chose to defer live validation given this account's track record of Free Tier/API surprises (Aurora, EC2 instance types, RDS reserved username); see §6/§11 for what specifically is unverified. |
| DMS prerequisite IAM roles | **Not Terraform-managed** — a one-time manual `scripts/bootstrap-dms-roles.sh`, same pattern as `scripts/bootstrap-backend.sh` | `dms-vpc-role`/`dms-cloudwatch-logs-role` are fixed-name, account-wide singletons DMS requires to exist by exact name. Managing them inside the per-environment `dms` module (applied separately for develop/staging/production) would make the 2nd environment's apply fail with "role already exists" — the same class of problem `github-actions-oidc/` already solves by being a separate singleton state, just resolved here with a plain idempotent script instead of a second Terraform state. |
| Logical replication | `rds-postgres-instance` gains an opt-in `enable_logical_replication` flag (custom parameter group, `rds.logical_replication = 1`), applied only to the 3 shards | DMS's CDC capture from PostgreSQL requires `wal_level=logical`, which is a static parameter — RDS enforces this via a parameter group rather than a direct instance setting, and changing it requires a reboot. The reporting instance is the CDC *target*, not a source, so it doesn't need this. |
| Local dev access to VPC-private RDS (temporary) | `rds-postgres-instance` gains an opt-in `additional_ingress_cidr_blocks` list + existing `publicly_accessible` flag, driven by root var `local_dev_ip_cidr` | Neither shard-1 nor reporting is reachable from outside the VPC by default (no bastion/VPN exists in this project) — confirmed the hard way when a local `TransactionWorker` run left a DynamoDB claim stuck `CLAIMED` after its Postgres insert timed out. Restricted to the developer's own `/32` IP, not opened broadly; **ElastiCache has no equivalent** — classic (non-serverless) Redis clusters in a VPC subnet group can't be made publicly accessible at all, so Redis stays VPC-only (both submission handlers already fail open on Redis errors, so this doesn't block local testing). |

## 4. Project Layout

```
D:\git\microservice_1_terraform/
├── backend.tf                    # bucket/key/region blank, filled via -backend-config per env (one shared bucket, key differs per env)
├── main.tf
├── variables.tf
├── outputs.tf
├── ssm-outputs.tf
├── justfile                      # bootstrap-backend / init / plan / apply / destroy, per env
├── scripts/
│   ├── bootstrap-backend.sh      # creates the one shared bucket (idempotent)
│   └── bootstrap-dms-roles.sh    # NEW — one-time, creates dms-vpc-role/dms-cloudwatch-logs-role (idempotent, account-wide singleton - see §3)
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
│   ├── rds-postgres-instance/         # NEW — replaces `rds`; applied 4x
│   ├── dynamodb/                     # NEW — transaction-claims table
│   ├── sqs/                          # NEW — TransactionQueue + DLQ
│   ├── elasticache/                  # NEW — applied 2x (Gateway, Service)
│   ├── keda/                         # NEW — Helm release
│   ├── irsa-service-role/            # NEW — generic per-service IAM role for IRSA
│   ├── dms/                          # NEW — replication instance + subnet group + security group + target (reporting) endpoint
│   └── dms-source-endpoint-task/     # NEW — one shard's source endpoint + CDC task; applied 3x
└── environments/
    ├── develop/     (backend.tfvars → microservice1_dev,   develop.tfvars,    secrets.tfvars)
    ├── staging/     (backend.tfvars → microservice1_stage, staging.tfvars,    secrets.tfvars)
    └── production/  (backend.tfvars → microservice1_prod,  production.tfvars, secrets.tfvars)
```

## 5. Modules Reused From the Reference (Renamed Only)

`vpc`, `eks-cluster`, `eks-nodegroup`, `eks-alb-controller`, `eks-cluster-autoscaler`, `eks-external-dns`, `eks-ebs-csi`, `ecr` — same logic, but every `microservice0`-specific default is renamed:

- `cluster_name` default → `microservice1`; `ecr_repository_names` → `["transactiongateway", "transactionservice", "transactionworker"]`; `route53_zone_id` → the `ekslab.xyz` zone.
- The reference's `db_*`/RDS-related root variables (`db_instance_identifier`, `db_engine_version`, `db_master_username`, etc.) map directly to this project's own `db_*` variables (§6) — same shape, since both end up being plain RDS, just a different engine (Postgres vs MySQL) and applied 4x instead of once.

## 6. New Modules

| Module | Purpose | Key config (from the linked docs) |
|---|---|---|
| `rds-postgres-instance` | One plain RDS PostgreSQL instance (`db.t3.micro`, Single-AZ — not Aurora, see §3); applied 4x | Schema from `database.md` §6/§7; identifiers `microservice1-<env>-shard-0/1/2` and `microservice1-<env>-reporting`. The 3 shard instances also pass `enable_logical_replication = true` (§3) — required for DMS to capture CDC from them. |
| `dynamodb` | `transaction-claims-<env>` table | `gsi_status_lease` GSI, TTL on `ttl`, `PAY_PER_REQUEST` billing (`KEDA.md` §5) |
| `sqs` | `TransactionQueue-<env>` + `TransactionQueue-<env>-dlq` | Exact config from `SQS.md` §3 (Standard, SSE-SQS, 30s visibility, `maxReceiveCount=5`, 4-day retention, 20s long polling) |
| `elasticache` | Redis, applied 2x per environment | Cheapest single-node instance, one for `TransactionGateway`, one for `TransactionService` (`TransactionGateway.md` §6, `TransactionService.md` §6), identifiers suffixed `-<env>` like everything else |
| `dms` (NEW) | Shared CDC pipeline pieces: `dms.t3.micro` replication instance, subnet group, security group, and the one target endpoint (the reporting instance) | `database.md` §7. Requires `scripts/bootstrap-dms-roles.sh` run once first (§3). **Not yet applied/validated.** |
| `dms-source-endpoint-task` (NEW) | One shard's DMS source endpoint + a CDC-only replication task into the shared target; applied 3x (shard-0/1/2) | Table mapping renames `transactions` → `transactions_reporting` and stamps each row with a literal `shard_id` via a DMS transformation rule (`database.md` §11 Phase 5). **The exact transformation-rule JSON is unverified** — DMS's "add-column with a literal value" capability is documented but hasn't been exercised against a real replication task yet; treat the table-mapping JSON as a first draft to debug against real DMS behavior, not a proven-working config. |

All per-environment resource names follow the same `-<env>` (or `microservice1-<env>-...`) suffix convention established by the DB instances and the EKS cluster names — every environment shares one AWS account, so naming (not account separation) is what keeps them from colliding.
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

- One shared S3 bucket (`steven-zhang-learning`, the same one `microservice_0` already uses), exactly like the reference — only the state `key` differs per environment (`microservice1_dev|_stage|_prod/terraform.tfstate`).
- `scripts/bootstrap-backend.sh` creates that one bucket (idempotent, safe to re-run), same as the reference.
- `backend.tf` stays parameter-free (bucket/key/region filled at `terraform init` time via `-backend-config`), same as the reference — each environment's `backend.tfvars` differs only in `key`.
- Cluster names exactly as given: `microservice1-develop`, `microservice1-staging`, `microservice1-production`.

## 9. GitHub Actions OIDC (`github-actions-oidc/`)

- New singleton state, separate from the per-environment applies — same structure as the reference.
- `github_repo = "szhanggit/microservice_1"` (confirmed via `git remote -v`).
- `github_owner_id` / `github_repo_id`: placeholder values to start; the first real `AssumeRoleWithWebIdentity` call will fail `AccessDenied` against a name-only policy, at which point the actual immutable IDs are read from CloudTrail (`aws cloudtrail lookup-events --lookup-attributes AttributeKey=EventName,AttributeValue=AssumeRoleWithWebIdentity`) and applied — documented as an explicit manual step in §10, not something to guess.
- `role_name = "github-actions-microservice1"`.

## 10. Config Handoff (`ssm-outputs.tf`)

`ssm_prefix = "/microservice1/${var.environment}"`. Published, mirroring the reference's pattern:

- SSM: `cluster_name`, `region`, `vpc_id`, `ecr_repository_urls` (JSON map) — same as reference.
- SSM: `sqs_queue_url`, `sqs_queue_arn`, `dynamodb_table_name`, ElastiCache endpoints (2, one per service), DB instance endpoints (4 — non-secret hostnames only).
- Secrets Manager: DB master connection strings (4, one per instance) — holds credentials, same reasoning as the reference keeping the DB connection string out of plain SSM.
- Secrets Manager (new): DB **read-only** connection strings (4, one per instance) for `TransactionService`'s search path (`TransactionService.md` §2/§6) — separate secrets from the master ones above, built from a new `db_readonly_username`/`db_readonly_password` variable pair. Terraform only stores these credentials; it does not create the underlying read-only Postgres role itself (that's the migration tooling's job, `database.md` §11 Phase 2 — not yet built, same pre-existing gap as the schema migrations for the write path).

## 11. Implementation Plan

**Phase 1 — Bootstrap**
- Adapt `scripts/bootstrap-backend.sh` (creates the one shared bucket, same as the reference).
- Apply `github-actions-oidc/` once with placeholder owner/repo IDs; fix them from CloudTrail after the first real workflow run fails; re-apply.

**Phase 2 — Foundation**
- `vpc`, `eks-cluster`, `eks-nodegroup` — renamed variables/defaults (§5) — applied per environment.

**Phase 3 — EKS add-ons**
- `eks-alb-controller`, `eks-cluster-autoscaler`, `eks-external-dns` (zone `ekslab.xyz`), `eks-ebs-csi`.

**Phase 4 — ECR**
- 3 repositories: `transactiongateway`, `transactionservice`, `transactionworker`.

**Phase 5 — Data layer (new modules)**
- `rds-postgres-instance` ×4, `dynamodb`, `sqs`, `elasticache` ×2 — configs exactly matching `database.md`/`KEDA.md`/`SQS.md`/`TransactionGateway.md`/`TransactionService.md`.

**Phase 6 — KEDA**
- Helm-release the KEDA core chart into the cluster via the `keda` module.

**Phase 7 — IRSA roles**
- `irsa-service-role` applied for `TransactionService`, `TransactionWorker`, and the KEDA `TriggerAuthentication` (§7).

**Phase 8 — Config handoff**
- `ssm-outputs.tf` publishing everything in §10.

**Phase 9 — Validation**
- `terraform plan`/`apply` against `develop` end-to-end; confirm KEDA can read `ApproximateNumberOfMessages`; confirm each IRSA role can do only what it's scoped for (e.g. `TransactionService`'s role rejected on a `ReceiveMessage` attempt).

**Phase 10 — CDC pipeline (new, code written, not yet applied)**
- Run `scripts/bootstrap-dms-roles.sh` once (§3) before this phase's `terraform apply` — DMS rejects replication-instance creation if `dms-vpc-role`/`dms-cloudwatch-logs-role` don't already exist.
- Add `enable_logical_replication = true` to the 3 shard `rds-postgres-instance` module calls; apply and confirm the resulting parameter-group change actually forces (and survives) the reboot each shard needs.
- Apply `dms` (replication instance + target endpoint) and `dms-source-endpoint-task` ×3.
- Validate the table-mapping JSON actually renames into `transactions_reporting` and stamps the correct `shard_id` per shard — the transformation rule is a first draft (§6), expect to iterate on the real DMS error messages/logs.
- Confirm replication lag and row-count parity between each shard and the reporting instance under a small write load, then re-run `TransactionService`'s `SearchByDateRange` against real data.

Create a just file.