# TransactionWorker — KEDA-Scaled SQS Consumer (.NET 8)

## 1. Requirements

- Build a .NET 8 application, `TransactionWorker`, in `D:\git\microservice_1\components\TransactionWorker`.
- Dependency injection throughout; every part of the application unit tested; follow .NET/.NET Core best practices.
- Consume messages from Amazon SQS; autoscale the consumer with KEDA based on queue depth (small sizing — this is a demo).
- Message body (producer-supplied): `transaction_no`, `transaction_datetime`, `amount`, `type`, `status`, `currency`.
- Main job: insert each message into the sharded `transactions` table described in `database.md`.
- Before inserting, back up the message as state data (Redis or DynamoDB) so that if one worker dies mid-processing, another live worker can read the saved state and continue.
- Ship logs, traces, and metrics to Grafana Cloud (Prometheus, Loki, Tempo).
- Provide Docker + docker-compose.
- Local testing hits real SQS on AWS, not a local queue emulator.

## 2. Decisions

| Decision | Choice | Why |
|---|---|---|
| State store | DynamoDB | AWS-native like SQS, built-in per-item TTL, nothing extra to run/manage, scales to ~zero cost when idle. |
| Recovery model | **Claim-check with lease** — DynamoDB holds the only surviving copy of the message after it's deleted from SQS | Makes DynamoDB load-bearing rather than a redundant mirror of what SQS's own visibility timeout already does. See §5. |
| K8s target | New demo EKS cluster, provisioned via Terraform; KEDA installed via Helm | Needed to actually demonstrate autoscaling — `docker-compose` alone can't show a ScaledObject reacting to queue depth. |
| Shard DB | Real Aurora MySQL clusters, provisioned via Terraform, schema from `database.md` §6/§7 | Exercises the actual sharded design end-to-end rather than a stand-in. |
| SQS queue | New demo queue + DLQ, provisioned via Terraform | Sized small for the demo; DLQ catches poison messages independent of the lease-recovery mechanism (see §5). |
| Terraform state | Remote S3 backend, one bucket per environment, region `ca-central-1` | `steven-zhang-learning/microservice1_dev`, `_stage`, `_prod` — see §8. |
| Terraform code location | Separate repo/directory: `D:\git\microservice_1_terraform` | Kept out of the application repo so infra changes and app changes have independent history/review. |
| Observability | Full OpenTelemetry .NET SDK instrumentation (metrics, tracing, logging) in code; **no real Grafana Cloud stack wired up** | Grafana Cloud/Prometheus/Loki/Tempo skipped to avoid cost for a demo, but the instrumentation code itself must exist — see §7. |
| Test framework | xUnit + Moq | .NET 8 idiomatic default; all AWS/DB dependencies mocked so business logic is fully unit-testable without live infra. |

## 3. Architecture Overview

```
                    ┌─────────────────────────┐
   producer  ─────► │   Amazon SQS             │◄────────────┐
                    │   (demo queue + DLQ)     │              │ delete after
                    └───────────┬─────────────┘              │ claim succeeds
                                │ poll                        │
                                ▼                             │
                    ┌─────────────────────────────────────────┴───┐
                    │   TransactionWorker pod (KEDA-scaled, EKS)   │
                    │   ┌───────────────────────────────────────┐ │
                    │   │ 1. Receive message                    │ │
                    │   │ 2. Conditional PutItem  ───────────┐  │ │
                    │   │ 3. Delete from SQS                 │  │ │
                    │   │ 4. Insert into Aurora shard         │  │ │
                    │   │ 5. Mark DynamoDB item COMPLETED     │  │ │
                    │   │ (bg) Scan GSI for stale CLAIMED     │  │ │
                    │   │      items, reclaim + resume        │  │ │
                    │   └─────────────────────────────────────┼──┘ │
                    └─────────────────────────────────────────┼────┘
                                                               ▼
                                                    ┌─────────────────────┐
                                                    │  DynamoDB            │
                                                    │  transaction-claims  │
                                                    │  (+ GSI, TTL)        │
                                                    └─────────────────────┘
                                │
                                ▼ shard_id = murmur3_32(transaction_no) % 3
                    ┌───────────────────────────────────────────┐
                    │  Aurora MySQL shard-0 / shard-1 / shard-2  │
                    │  (from database.md §6)                    │
                    └───────────────────────────────────────────┘

     KEDA ScaledObject watches SQS ApproximateNumberOfMessages → scales pod replicas

     OTel SDK (in-process, fully instrumented) ──► exporter stubbed out (no Grafana Cloud stack for this demo)
```

## 4. .NET 8 Application Design

Solution layout:

```
components/TransactionWorker/
├── TransactionWorker.sln
├── src/
│   ├── TransactionWorker/                  # Generic Host entry point (Program.cs), appsettings
│   ├── TransactionWorker.Domain/           # Transaction model, IShardRouter, ISnowflakeIdGenerator
│   ├── TransactionWorker.Application/      # IMessageProcessor, IClaimStore, IStaleClaimScanner (interfaces + logic)
│   └── TransactionWorker.Infrastructure/   # SqsListenerService, DynamoDbClaimStore, AuroraTransactionRepository, OTel setup
├── tests/
│   └── TransactionWorker.UnitTests/        # xUnit + Moq, mirrors src/ namespace-for-namespace
├── Dockerfile
└── docker-compose.yml
```

- **Hosting model**: generic `Host` with two `BackgroundService`s registered via DI — `SqsListenerService` (long-poll receive loop) and `StaleClaimReclaimService` (periodic GSI scan, §5). Both resolve per-message dependencies through an `IServiceScopeFactory` so scoped services (e.g. a DB connection) don't leak across messages.
- **DI registrations**: `IAmazonSQS`, `IAmazonDynamoDB` (AWS SDK clients, singleton), `IShardRouter` / `ISnowflakeIdGenerator` (singleton, pure logic from `database.md` §4–5), `IClaimStore` → `DynamoDbClaimStore`, `ITransactionRepository` → `AuroraTransactionRepository` (holds 3 shard connection factories, keyed by `shard_id`), `IMessageProcessor` orchestrating claim → insert → complete.
- **Config**: `IOptions<T>` bound from `appsettings.{Environment}.json` + environment variables (queue URL, DynamoDB table name, shard connection strings/secret ARNs, OTLP endpoint). AWS SDK default credential chain (IRSA on EKS, local profile for dev).
- **Resilience**: Polly retry/backoff around Aurora and DynamoDB calls for transient errors; a MySQL `Duplicate entry` on `uq_transaction_no` is caught explicitly and treated as "already processed" (see §5), not retried.
- **Best practices**: nullable reference types enabled, `ILogger<T>` structured logging (no string concatenation), immutable `record` DTOs for the message payload, `CancellationToken` threaded through every async call so shutdown drains cleanly.
- **Testing**: every class behind an interface; unit tests cover shard routing determinism, id generation/decoding, claim/complete/duplicate-insert branches of `IMessageProcessor`, and the stale-lease scan/reclaim logic — all against mocked `IAmazonSQS`/`IAmazonDynamoDB`/`ITransactionRepository`, no live AWS needed to run `dotnet test`.

## 5. Message Processing Flow (Claim-Check with Lease)

This is the core mechanism, and it's designed so DynamoDB is actually necessary — not a redundant copy of what SQS's visibility timeout already does.

**DynamoDB table `transaction-claims`** (billing mode `PAY_PER_REQUEST` — a demo's traffic is intermittent enough that on-demand billing is both cheaper and one less capacity setting to manage; there's no per-hour charge for the table sitting idle, unlike the EKS/Aurora pieces of this stack):

| Attribute | Type | Notes |
|---|---|---|
| `transaction_no` (PK) | String | Same key the shard router hashes on. |
| `status` | String | `CLAIMED` \| `COMPLETED`. |
| `worker_id` | String | Pod name / instance id — whoever currently owns the claim. |
| `lease_expiry` | Number (epoch seconds) | Also the sort key of the GSI used for the stale scan. |
| `payload` | String (JSON) | The full original message body — the only surviving copy once SQS deletes it. |
| `attempt_count` | Number | Incremented on each reclaim. |
| `ttl` | Number (epoch seconds) | DynamoDB TTL attribute — expires `COMPLETED` items a few minutes after completion so the demo table stays visibly clean. |

GSI `gsi_status_lease`: PK `status`, SK `lease_expiry` — supports `status = CLAIMED AND lease_expiry < now`.

**Steady-state flow:**
1. Worker receives a message from SQS.
2. **Conditional `PutItem`**: `transaction_no` as PK, condition `attribute_not_exists(transaction_no)`. This atomically claims the message — and, as a side benefit, rejects SQS's normal at-least-once duplicate deliveries immediately, independent of any crash scenario.
3. On a successful claim, the worker **deletes the message from SQS right away**. From this point, SQS no longer holds the data — DynamoDB does.
4. Worker resolves `shard_id` (§4 of `database.md`), generates the Snowflake `id`, inserts into the Aurora shard.
5. On success, the DynamoDB item is updated to `status = COMPLETED` with a short `ttl` — visible briefly in the console, then auto-expires.

**Crash recovery:** if a worker dies between steps 3 and 5, the message is gone from SQS — the only place it still exists is the `CLAIMED` DynamoDB item. Every worker instance runs a background scan (via the GSI) for items where `lease_expiry` has passed. Whichever instance finds one first performs a **conditional update** requiring the old `lease_expiry` to still match (so only one instance wins the race), takes over the claim with a fresh lease, and resumes processing directly from the stored `payload` — no re-read from SQS is possible or needed.

**Defense in depth:** even in the (rare) case two workers both believe they won the reclaim, Aurora's `UNIQUE KEY uq_transaction_no` rejects the second insert. The worker treats that specific MySQL duplicate-key error as "already done," marks its DynamoDB item `COMPLETED`, and moves on — no special coordination required.

**What the SQS DLQ is still for:** a message that repeatedly fails *before* it's ever claimed (e.g. malformed JSON that can't be parsed into a claim) keeps being redelivered by SQS itself and eventually lands in the DLQ after `maxReceiveCount`. This is a different failure mode from the lease-based recovery above — SQS handles "bad message," DynamoDB handles "worker died mid-flight."

## 6. KEDA Autoscaling

```yaml
apiVersion: keda.sh/v1alpha1
kind: ScaledObject
metadata:
  name: transaction-worker
spec:
  scaleTargetRef:
    name: transaction-worker
  minReplicaCount: 0
  maxReplicaCount: 5
  cooldownPeriod: 60
  triggers:
    - type: aws-sqs-queue
      metadata:
        queueURL: <from Terraform output>
        queueLength: "5"          # target messages per replica
        activationQueueLength: "1"
        awsRegion: ca-central-1
      authenticationRef:
        name: transaction-worker-sqs-auth
```

- `TriggerAuthentication` uses **IRSA** (IAM Roles for Service Accounts) — no static AWS keys in the cluster.
- `minReplicaCount: 0` is safe here specifically because of the design in §5: a stale `CLAIMED` item only exists while a message was in flight, and the next message's arrival (which scales a pod back up) triggers that pod's own startup-time reclaim scan, in addition to its periodic one — so scale-to-zero never strands a claim indefinitely once traffic resumes.

## 7. Observability (Instrumented, Not Shipped)

To avoid Grafana Cloud cost for a demo, **no OTLP endpoint is actually wired up** — but the full OpenTelemetry .NET SDK instrumentation must exist in code, ready to point at Prometheus/Loki/Tempo later by swapping only the exporter configuration.

- Traces: one `Activity` (via `ActivitySource`) spanning receive → claim → insert → complete, with child spans per step; failed/duplicate branches tagged.
- Metrics: counters/histograms (via `Meter`) for messages processed, claim conflicts (duplicate deliveries rejected), reclaims performed, and a per-shard insert counter (the key signal for verifying the hash routing distributes evenly, per `database.md` §10).
- Logs: `ILogger` output wired through the OTel logging provider, correlated to traces via trace/span id.
- Exporter: `AddConsoleExporter()` (or a no-op exporter) for all three signals in this demo — this is the only piece that would change to `AddOtlpExporter()` pointed at a real Grafana Cloud endpoint/API key (from a Kubernetes Secret) if/when that's turned on. No other code changes needed.
- Unit tests can assert that expected activities/metrics are recorded (e.g. via `ActivityListener` in tests) without needing any exporter or network call.

## 8. Terraform / Infrastructure

Region: `ca-central-1` for all environments.

Remote state, one separate bucket per environment, locked via Terraform's native `use_lockfile` (no DynamoDB lock table):

| Environment | State bucket |
|---|---|
| dev | `steven-zhang-learning/microservice1_dev` |
| stage | `steven-zhang-learning/microservice1_stage` |
| prod | `steven-zhang-learning/microservice1_prod` |

Lives in its own repo/directory, separate from the application code: `D:\git\microservice_1_terraform`. Full module layout, environment folder naming (`develop`/`staging`/`production`), and all other Terraform decisions are authoritative in `Terraform.md` — not re-derived here to avoid the two docs drifting apart. The pieces this doc's design depends on: a `dynamodb` module (`transaction-claims` table, `PAY_PER_REQUEST` billing per §5), an `sqs` module, an `aurora-mysql-cluster` module applied 4× (3 shards + reporting), and a dedicated `keda` module (Helm release of the KEDA core chart, kept separate from the EKS cluster/nodegroup modules).

- Aurora Serverless v2 (low min ACU) is used for all 4 clusters instead of fixed provisioned instances — flagged as a suggestion here originally, confirmed as the actual decision in `Terraform.md` §3.
- EKS node group sized minimally for a demo (e.g. 1–2 small spot instances).

## 9. Docker & Local Testing

- `Dockerfile`: multi-stage — `mcr.microsoft.com/dotnet/sdk:8.0` to build/publish, `mcr.microsoft.com/dotnet/runtime:8.0` (no ASP.NET needed — this is a worker, not a web app) to run.
- `docker-compose.yml`: runs the `TransactionWorker` container(s) locally only — it does **not** stand up SQS/DynamoDB/Aurora emulators, per the requirement that local testing hits real AWS resources (dev queue, dev table, dev Aurora clusters) using local AWS credentials (profile or SSO), matching production topology as closely as possible.
- Unit tests (`dotnet test`) never touch real AWS — everything under `TransactionWorker.UnitTests` mocks `IAmazonSQS`, `IAmazonDynamoDB`, and `ITransactionRepository`.

## 10. Implementation Plan

**Phase 1 — Terraform foundations** (in `D:\git\microservice_1_terraform`, separate from the application repo)
- Bootstrap the 3 S3 state buckets + DynamoDB lock tables (one-time, likely run manually before `terraform init` works).
- `dynamodb` module: `transaction-claims` table with `gsi_status_lease` and TTL enabled.
- `sqs` module: demo queue + DLQ + redrive policy (`maxReceiveCount`).
- `aurora-shard` module applied 3x (shards) + 1x (reporting cluster, per `database.md` §7), plus the `eks` module with KEDA installed.

**Phase 2 — Solution scaffold**
- Create the `TransactionWorker.sln` layout from §4; wire DI, config binding, and health-check endpoints.
- Stand up `TransactionWorker.UnitTests` alongside so every subsequent phase adds tests as it goes, rather than backfilling.

**Phase 3 — Shard routing & id generation**
- Port the routing/Snowflake-id logic from `database.md` §4–5 into `TransactionWorker.Domain`.
- Unit tests: routing determinism, id/shard decode round-trip (mirrors `database.md`'s own Phase 3).

**Phase 4 — Claim-check pipeline**
- Implement `DynamoDbClaimStore` (conditional put/update/complete) and `IMessageProcessor` (claim → insert → complete, including the duplicate-key-as-success branch).
- Unit tests for every branch: fresh claim, duplicate-delivery rejection, successful insert, duplicate-key-on-insert handled as already-done.

**Phase 5 — Stale-claim reclaim**
- Implement `StaleClaimReclaimService` (GSI scan + conditional reclaim + resume-from-payload).
- Unit tests: reclaim wins the race, reclaim loses the race (another worker already renewed the lease), resume produces the same DB write as the original path.

**Phase 6 — Aurora integration**
- Implement `AuroraTransactionRepository` against the 3 shard connection factories.
- Integration tests (separate test project, opt-in) against the dev Aurora clusters from Phase 1.

**Phase 7 — Observability**
- Wire OpenTelemetry traces/metrics/logs with a console (or no-op) exporter; verify via unit tests and local console output that the expected activities/metrics/logs are actually recorded. No Grafana Cloud stack stood up for this demo.

**Phase 8 — Containerization & deployment**
- `Dockerfile` + `docker-compose.yml` (§9); Helm chart or plain manifests for the `TransactionWorker` Deployment + `ScaledObject` + `TriggerAuthentication` (§6); deploy to the demo EKS cluster.

**Phase 9 — Demo validation**
- Run a load of test messages through the real dev SQS queue; confirm KEDA scales replicas up/down with queue depth.
- Chaos test: kill a worker pod mid-processing (between SQS delete and DB insert), confirm another pod's reclaim scan picks up the stale claim and completes it, and confirm the trace/metrics/logs for both the original and reclaiming pod show up correctly in the console output.
