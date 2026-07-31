# TransactionService — gRPC-to-SQS Forwarder (.NET 8)

## 1. Requirements

- Build a .NET 8 application, `TransactionService`, in `D:\git\microservice_1\components\TransactionService`.
- Receives a gRPC request from `TransactionGatewayService` (per `TransactionGateway.md`), sends a message to Amazon SQS `TransactionQueue` (per `SQS.md`).
- Dependency injection throughout; unit tested (xUnit); Dockerfile + docker-compose; follow .NET/.NET Core best practices.
- Uses Redis as a remote cache and local memory as a local (in-process) cache.

## 2. Decisions

| Decision | Choice | Why |
|---|---|---|
| Cache purpose | Idempotency/dedup on `transaction_no` | Same rationale as `TransactionGateway.md`: guards against a duplicate gRPC call from the Gateway (e.g. a Polly retry after a timeout) causing a duplicate `SendMessage`. |
| Cache write timing | **Written only after `SendMessage` succeeds** | If `SendMessage` fails, no cache entry is written — a legitimate retry from the Gateway still goes through, instead of being silently swallowed by a dedup entry for a message that never actually reached SQS. |
| Redis hosting | Its own managed ElastiCache instance, separate from the Gateway's | Decided in `TransactionGateway.md` §2 — this service's cache guards a different retry boundary (gRPC, not HTTP) and shouldn't share state with the Gateway's. |
| Id / shard-id generation | **Not done here** — `TransactionWorker` generates the Snowflake `id` and resolves `shard_id` at insert time (`database.md` §4–5, `KEDA.md` §4) | Keeps this service a thin, stateless forwarder: dedup check, validate, forward the raw fields to SQS. No routing logic to keep in sync with the shard-routing library. |
| Field validation | **Re-validates independently** — does not trust `TransactionGateway`'s validation | A `ClusterIP` Kubernetes `Service` restricts *routing*, not *authorization* — anything in the cluster can technically reach it. Defense in depth, consistent with treating Aurora's unique-key rejection as another independent safety net further downstream. |
| Local SQS | Real dev `TransactionQueue`, not a local emulator | Consistent with `TransactionWorker`'s local-testing rule in `KEDA.md` §9 — both ends of the queue are tested against the real thing. |
| Deployment target | Same EKS cluster as `TransactionGateway`/`TransactionWorker`; `ClusterIP` only, **no ALB/Ingress** | Established in `TransactionGateway.md` §3 — this service is never called from outside the cluster. |
| Observability | Full OpenTelemetry instrumentation in code (traces/metrics/logs); **no real Grafana Cloud stack wired up** | Same system-wide decision as `KEDA.md` §2/§7 and `TransactionGateway.md` §2/§8 — skip the cost of an actual observability backend for a demo, but the instrumentation itself must exist. See §8. |

## 3. Architecture Overview

```
        gRPC (from TransactionGatewayService, in-cluster only)
                       │
                       ▼
        ┌───────────────────────────────────┐
        │  TransactionService (EKS pod,      │
        │  ClusterIP — not internet-facing)  │
        │                                     │
        │  1. Validate fields                 │
        │     (currency / type / status /     │
        │      amount)                        │
        │  2. HybridCache dedup check on      │◄──── ElastiCache
        │     transaction_no                  │      (Service's own Redis)
        │  3. If dedup hit → return            │
        │     accepted=true immediately        │
        │  4. Else: SendMessage to SQS         │──────► Amazon SQS
        │  5. On success only: write cache     │        TransactionQueue
        │     entry, return accepted=true      │
        │  6. On SendMessage failure: no       │
        │     cache write, return gRPC error   │
        └───────────────────────────────────┘
```

- A **validation failure** (bad currency/type/status/amount) short-circuits before the dedup check or SQS call — returns a gRPC error (`INVALID_ARGUMENT`), which the Gateway should surface as an HTTP `4xx`, distinct from the `202`/fire-and-forget success path.
- A **cache-unavailable** condition fails open here too, same as the Gateway: skip the dedup check, proceed to `SendMessage` — correctness still holds because Aurora's `UNIQUE KEY uq_transaction_no` (`database.md` §6) is the real backstop.

## 4. .NET 8 Application Design

```
components/TransactionService/
├── TransactionService.sln
├── src/
│   ├── TransactionService.Grpc/             # Grpc.AspNetCore host, Program.cs, service implementation
│   ├── TransactionService.Application/      # IIdempotencyGuard, ITransactionValidator, ISqsForwarder + orchestration
│   └── TransactionService.Infrastructure/   # AWS SQS client wrapper, HybridCache/Redis wiring
├── tests/
│   └── TransactionService.UnitTests/        # xUnit + mocks for IAmazonSQS / HybridCache
├── Dockerfile
└── docker-compose.yml
```

- **DI registrations**: `IAmazonSQS` (singleton), `HybridCache` (local memory + this service's Redis), `ITransactionValidator`, `IIdempotencyGuard`, `ISqsMessageForwarder`, and the gRPC service implementation itself, which orchestrates validate → dedup check → forward → cache write → response.
- **Config**: `IOptions<T>` for the SQS queue URL, the ElastiCache endpoint, and the idempotency-entry TTL (same default window as the Gateway's — 15 minutes).
- **IAM**: the pod's IRSA role is scoped to `sqs:SendMessage` on the `TransactionQueue` ARN only — least privilege, no broader SQS access.
- **Testing**: every branch of the orchestration (valid + fresh, valid + dedup hit, invalid field, `SendMessage` failure leaving no cache entry, cache-unavailable fail-open) is unit tested against mocked `IAmazonSQS`/`HybridCache` — no live Redis, SQS, or Gateway needed to run `dotnet test`.

## 5. gRPC Server & SQS Message Contract

Implements the server side of the shared contract at `components/contracts/transaction.proto` (already defined in `TransactionGateway.md` §5) via `Grpc.AspNetCore`.

**Field validation rules** (enforced before anything else runs):
- `currency`: must be a recognized 3-letter ISO 4217 code (small in-process allow-list for the demo).
- `type` / `status`: must be one of an application-level allowed-value set (kept in code, not a MySQL `ENUM`, per `database.md` §6's reasoning — no `ALTER TABLE` needed to add a new value).
- `amount`: must parse as a positive decimal (received as a string over gRPC to avoid floating-point precision issues).

**SQS message body** (JSON, passed straight through — `TransactionWorker` deserializes and inserts, per `KEDA.md` §5):

```json
{
  "transaction_no": "string",
  "transaction_datetime": "2026-07-31T12:00:00Z",
  "amount": "100.00",
  "type": "string",
  "status": "string",
  "currency": "USD"
}
```

No `id` or `shard_id` is included — those are computed by `TransactionWorker` at insert time.

## 6. Terraform Notes (in `D:\git\microservice_1_terraform`)

- Second application of the `elasticache` module (the first was the Gateway's, per `TransactionGateway.md` §6) — same cheapest single-node sizing, region `ca-central-1`.
- IAM policy/role for this service's IRSA, scoped to `sqs:SendMessage` on the `TransactionQueue` ARN from `SQS.md`.
- Security group: only this service's pods may reach its ElastiCache instance.

## 7. Docker & Local Testing

- `Dockerfile`: multi-stage, `mcr.microsoft.com/dotnet/sdk:8.0` to build/publish, `mcr.microsoft.com/dotnet/aspnet:8.0` to run (Kestrel/HTTP2 is needed to host gRPC).
- `docker-compose.yml`: this service's container plus a local Redis container for convenient iteration; AWS credentials (profile/SSO) configured so it sends to the real dev `TransactionQueue`, per §2.
- Unit tests never touch real Redis or SQS — everything is mocked.

## 8. Observability (Instrumented, Not Shipped)

Same system-wide decision as `KEDA.md` §7 and `TransactionGateway.md` §8: **no Grafana Cloud stack wired up** for this demo, but the full OpenTelemetry .NET SDK instrumentation must exist in code.

- Traces: one `Activity` spanning gRPC receive → validate → dedup check → `SendMessage` → response, tagging validation failures, dedup hits, and send failures distinctly.
- Metrics: counters for requests received, validation failures, dedup hits, `SendMessage` failures, and successful forwards.
- Logs: `ILogger` output wired through the OTel logging provider, correlated to traces via trace/span id.
- Exporter: `AddConsoleExporter()` (or no-op) for all three signals in this demo — the only piece that would change to `AddOtlpExporter()` if a real Grafana Cloud endpoint is ever wired up.
- Unit tests can assert expected activities/metrics are recorded (via `ActivityListener`) without needing an exporter or network call.

## 9. Implementation Plan

**Phase 1 — Terraform**
- `elasticache` module (2nd instance) + IRSA role/policy scoped to `sqs:SendMessage` on `TransactionQueue`.

**Phase 2 — Solution scaffold**
- Create the `TransactionService.sln` layout from §4; wire DI, config binding.
- Stand up `TransactionService.UnitTests` alongside.

**Phase 3 — gRPC server**
- Implement the server side of `components/contracts/transaction.proto`; wire `Grpc.AspNetCore` hosting.

**Phase 4 — Field validation**
- Implement `ITransactionValidator` (currency/type/status/amount rules from §5); unit tests for every rejection case, confirming SQS/cache are never touched on a validation failure.

**Phase 5 — Idempotency guard**
- Implement `IIdempotencyGuard` on `HybridCache`, written only after a successful send; unit tests: fresh key, dedup hit (no second `SendMessage`), cache-unavailable fail-open, `SendMessage` failure leaves no cache entry.

**Phase 6 — SQS forwarding**
- Implement `ISqsMessageForwarder` against `IAmazonSQS`; unit tests mocking `IAmazonSQS`, including a transient failure path.

**Phase 7 — Containerization**
- `Dockerfile` + `docker-compose.yml` (§7); Kubernetes Deployment + `ClusterIP` Service manifest — deliberately no `Ingress`/ALB.

**Phase 8 — Validation**
- End-to-end demo run through `TransactionGateway` → this service → SQS → `TransactionWorker`; confirm a retried gRPC call is deduped (no second SQS message); confirm an invalid field is rejected without reaching SQS; confirm this service's Redis being down still allows requests through (fail-open).
