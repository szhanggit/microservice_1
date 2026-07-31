# TransactionQueue — Amazon SQS Provisioning

## 1. Requirements

- Provision Amazon SQS for the demo.
- Producer: `TransactionService` (`TransactionService.md`). Consumer: the KEDA-scaled `TransactionWorker` (`KEDA.md`).
- Queue name: `TransactionQueue`.
- Cheapest plan — this is a demo, not a production workload.
- Part of the Terraform project in `D:\git\microservice_1_terraform`.
- Region: `ca-central-1`.

## 2. Decisions

| Decision | Choice | Why |
|---|---|---|
| Queue type | Standard (not FIFO) | Cheaper, higher throughput, no ordering guarantee needed. At-least-once delivery is already handled elsewhere: `TransactionService`'s dedup cache before send, and `TransactionWorker`'s DynamoDB claim + Aurora `UNIQUE KEY` after receive (`KEDA.md` §5). Nothing in the design depends on message order. |
| Encryption | SSE-SQS (AWS-managed keys) | Free, encryption at rest still applied. SSE-KMS would add per-request KMS cost for no real benefit on a demo queue. |
| DLQ purpose | Manual inspection only | Messages that exhaust `maxReceiveCount` land in the DLQ for you to look at; no CloudWatch alarm/redrive automation for this demo. |
| Visibility timeout | 30s | Short is fine — SQS's own timeout isn't the primary recovery path here. `TransactionWorker` claims into DynamoDB and deletes the SQS message almost immediately (`KEDA.md` §5); the lease/reclaim mechanism there is what actually handles a worker dying mid-processing, not SQS redelivery. |
| `maxReceiveCount` (before DLQ) | 5 | A message that fails to even be claimed 5 times is almost certainly malformed, not just unlucky timing — route it to the DLQ rather than looping forever. |
| Message retention | 4 days | Comfortably under the 14-day max; no reason to hold demo messages longer. |
| Long polling | `ReceiveMessageWaitTimeSeconds = 20` | Reduces empty-receive API calls/cost — standard practice independent of demo vs. production. |

## 3. Queue Configuration

| Attribute | Value |
|---|---|
| Name | `TransactionQueue-<env>` (main), `TransactionQueue-<env>-dlq` (dead-letter) — e.g. `TransactionQueue-develop` |
| Type | Standard |
| Region | `ca-central-1` |
| Encryption | SSE-SQS |
| VisibilityTimeout | 30 |
| MessageRetentionPeriod | 345600 (4 days, seconds) |
| ReceiveMessageWaitTimeSeconds | 20 |
| RedrivePolicy | `maxReceiveCount = 5` → `TransactionQueue-dlq` |
| DLQ MessageRetentionPeriod | 1209600 (14 days — keep failed messages around longer than the main queue, since they need a human to look at them) |

## 4. Access / IAM

| Principal | Permissions | Scope |
|---|---|---|
| `TransactionService` (IRSA role) | `sqs:SendMessage` | `TransactionQueue` ARN only (`TransactionService.md` §6) |
| `TransactionWorker` (IRSA role) | `sqs:ReceiveMessage`, `sqs:DeleteMessage`, `sqs:GetQueueAttributes` | `TransactionQueue` ARN only |
| KEDA `TriggerAuthentication` (IRSA role) | `sqs:GetQueueAttributes` | `TransactionQueue` ARN only — this is what the `aws-sqs-queue` scaler polls to read `ApproximateNumberOfMessages` for scaling decisions (`KEDA.md` §6) |

No principal has access to `TransactionQueue-dlq` beyond what's needed for manual inspection (console/CLI access via your own AWS credentials, not a service role).

## 5. Terraform (`D:\git\microservice_1_terraform`)

This is the `sqs` module referenced in `KEDA.md` §8's module layout:

```
modules/sqs/
├── main.tf        # aws_sqs_queue.main, aws_sqs_queue.dlq, redrive policy
├── iam.tf          # IAM policy documents for the 3 principals in §4 (attached to existing IRSA roles)
├── variables.tf    # queue_name, visibility_timeout, max_receive_count, retention, region
└── outputs.tf      # queue_url, queue_arn, dlq_url, dlq_arn
```

- `queue_url`/`queue_arn` outputs feed: the KEDA `ScaledObject` trigger config (`KEDA.md` §6), `TransactionService`'s config (`TransactionService.md` §4), and `TransactionWorker`'s config.
- Applied per environment (`environments/develop|staging|production`, per `Terraform.md` §4) alongside the other modules. All three environments live in the same AWS account — isolation comes from environment-suffixed resource names (`TransactionQueue-develop`, `-staging`, `-production`), the same convention `Terraform.md` already uses for the Aurora cluster identifiers (`microservice1-<env>-shard-0`, etc.), not from separate accounts.

## 6. Implementation Plan

**Phase 1 — Module**
- Write the `sqs` Terraform module (§5): main queue + DLQ + redrive policy, with the config values from §3.

**Phase 2 — IAM**
- Attach the 3 scoped policies from §4 to the existing IRSA roles for `TransactionService`, `TransactionWorker`, and the KEDA `TriggerAuthentication`.

**Phase 3 — Wire up outputs**
- Feed `queue_url`/`queue_arn` into the KEDA `ScaledObject` manifest and both services' configuration (env vars / config maps).

**Phase 4 — Validation**
- Send a test message via `TransactionService` (or directly via CLI), confirm it's visible in `TransactionQueue`, confirm `TransactionWorker` claims and deletes it.
- Force a malformed message through 5 receive attempts, confirm it lands in `TransactionQueue-dlq` rather than looping indefinitely.
