# TransactionService — gRPC-to-SQS Forwarder (.NET 8)

## 1. Requirements

- Build a .NET 8 application, `TransactionService`, in `D:\git\microservice_1\components\TransactionService`.
- Receives a gRPC request from `TransactionGatewayService` (per `TransactionGateway.md`), sends a message to Amazon SQS `TransactionQueue` (per `SQS.md`).
- Dependency injection throughout; unit tested (xUnit); Dockerfile + docker-compose; follow .NET/.NET Core best practices.
- Uses Redis as a remote cache and local memory as a local (in-process) cache.
- Search transactions: by `transaction_no` (single-shard direct DB query) and by `transaction_datetime` range (direct query against the reporting instance) — a second, read-only path independent of the SQS-forwarding flow above; see §5.

## 2. Decisions

| Decision | Choice | Why |
|---|---|---|
| Cache purpose | Idempotency/dedup on `transaction_no` | Same rationale as `TransactionGateway.md`: guards against a duplicate gRPC call from the Gateway (e.g. a Polly retry after a timeout) causing a duplicate `SendMessage`. |
| Cache write timing | **Written only after `SendMessage` succeeds** | If `SendMessage` fails, no cache entry is written — a legitimate retry from the Gateway still goes through, instead of being silently swallowed by a dedup entry for a message that never actually reached SQS. |
| Redis hosting | Its own managed ElastiCache instance, separate from the Gateway's | Decided in `TransactionGateway.md` §2 — this service's cache guards a different retry boundary (gRPC, not HTTP) and shouldn't share state with the Gateway's. |
| Id / shard-id generation | **Not done here** — `TransactionWorker` generates the Snowflake `id` and resolves `shard_id` at insert time (`database.md` §4–5, `KEDA.md` §4) | Keeps this service a thin, stateless forwarder: dedup check, validate, forward the raw fields to SQS. No routing logic to keep in sync with the shard-routing library. |
| Field validation | **Re-validates independently** — does not trust `TransactionGateway`'s validation | A `ClusterIP` Kubernetes `Service` restricts *routing*, not *authorization* — anything in the cluster can technically reach it. Defense in depth, consistent with treating the DB's unique-constraint rejection as another independent safety net further downstream. |
| Local SQS | Real dev `TransactionQueue`, not a local emulator | Consistent with `TransactionWorker`'s local-testing rule in `KEDA.md` §9 — both ends of the queue are tested against the real thing. |
| Deployment target | Same EKS cluster as `TransactionGateway`/`TransactionWorker`; `ClusterIP` only, **no ALB/Ingress** | Established in `TransactionGateway.md` §3 — this service is never called from outside the cluster. |
| Observability | Full OpenTelemetry instrumentation in code (traces/metrics/logs); **no real Grafana Cloud stack wired up** | Same system-wide decision as `KEDA.md` §2/§7 and `TransactionGateway.md` §2/§8 — skip the cost of an actual observability backend for a demo, but the instrumentation itself must exist. See §8. |
| DB access (new) | Direct, **read-only** Postgres connections to all 3 OLTP shards + the reporting instance | The write path (`SubmitTransaction` → SQS → `TransactionWorker`) is unchanged and remains the only way data gets inserted — this is a second, independent read path added for search, not a replacement for the claim-check/insert flow in `KEDA.md` §5. See `database.md` §1's topology note: shard-0/shard-2 are currently dropped, so all 3 shard keys resolve to the same physical shard-1 instance. |
| Shard routing for reads | Reuses the exact same `components/ShardRouting` library `TransactionWorker` uses, rather than a second implementation | `database.md` §10: an independently-written second copy of the routing logic risks silently disagreeing with `TransactionWorker`'s and looking up the wrong shard for a given `transaction_no`. |
| Date-range query target | `transactions_reporting` (fed by CDC, `database.md` §7) — not scatter-gather across the 3 OLTP shards | Matches `database.md` §8's documented query pattern; keeps the OLTP shards' capacity dedicated to the write path. Requires the DMS CDC pipeline (`Terraform.md`) to actually be applied and running — see `database.md` §7's status note; until then this query path returns no rows. |
| DB credentials | A separate **read-only** Postgres role/user for this service, distinct from `TransactionWorker`'s write-capable credentials | Same least-privilege principle already applied to this service's SQS IAM policy (`sqs:SendMessage` only, §6) — a bug in the search path should not be able to write or delete data. **Not yet applied (2026-08-01):** the role has never been created via `CREATE ROLE` (no migration tooling exists for it yet — §6), so this service is currently configured with the same master (`dbadmin`) credentials `TransactionWorker` uses, purely to unblock local testing. Revert to the dedicated read-only role once it's provisioned. |
| Pagination (date-range search) | `limit` + `offset`, default 50 / max 500 | Simplest to implement and test; adequate for this demo's data volumes. One extra row beyond `limit` is fetched to compute `has_more` without a separate `COUNT(*)` query. |

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
- A **cache-unavailable** condition fails open here too, same as the Gateway: skip the dedup check, proceed to `SendMessage` — correctness still holds because the DB's `UNIQUE (transaction_no)` constraint (`database.md` §6) is the real backstop.

**Read path (search) — separate from the write path above:**

```
        gRPC (from TransactionGatewayService, in-cluster only)
                       │
          ┌────────────┴────────────┐
          ▼                          ▼
  SearchByTransactionNo      SearchByDateRange
          │                          │
          ▼                          ▼
  shard_id = murmur3_32(          transactions_reporting
  transaction_no) % 3             instance (read-only,
  (components/ShardRouting)       fed by CDC - database.md §7)
          │
          ▼
  RDS shard-0 / shard-1 / shard-2
  (database.md §6) - single-shard
  SELECT, same instances
  TransactionWorker writes to
```

No dedup cache, no SQS, no field-validation rules from above are involved — this is a plain read, independent of the fire-and-forget write flow. The only validation is on the search request shape itself (§5): a non-empty `transaction_no`, or a valid `from <= to` date range with `limit`/`offset` in range.

## 4. .NET 8 Application Design

```
components/TransactionService/
├── TransactionService.sln
├── src/
│   ├── TransactionService.Grpc/             # Grpc.AspNetCore host, Program.cs, service implementation
│   ├── TransactionService.Application/      # IIdempotencyGuard, ITransactionValidator, ISqsForwarder,
│   │                                         # ITransactionSearchRepository + orchestration (references
│   │                                         # components/ShardRouting directly - no separate Domain project)
│   └── TransactionService.Infrastructure/   # AWS SQS client wrapper, HybridCache/Redis wiring,
│                                             # PostgresTransactionSearchRepository (keyed shard +
│                                             # reporting NpgsqlDataSource connections)
├── tests/
│   └── TransactionService.UnitTests/        # xUnit + mocks for IAmazonSQS / HybridCache / ITransactionSearchRepository
├── Dockerfile
└── docker-compose.yml
```

- **DI registrations**: `IAmazonSQS` (singleton), `HybridCache` (local memory + this service's Redis), `ITransactionValidator`, `IIdempotencyGuard`, `ISqsMessageForwarder`, `IShardRouter` (from `components/ShardRouting`), `ITransactionSearchRepository` → `PostgresTransactionSearchRepository` (holds 3 keyed shard `NpgsqlDataSource`s + 1 for the reporting instance, same keyed-DI pattern as `TransactionWorker.Infrastructure.PostgresTransactionRepository`), and the gRPC service implementation, which now orchestrates both the write flow (validate → dedup check → forward → cache write → response) and the two read flows (§5).
- **Config**: `IOptions<T>` for the SQS queue URL, the ElastiCache endpoint, the idempotency-entry TTL (same default window as the Gateway's — 15 minutes), the 3 shard connection strings, and the reporting-instance connection string.
- **IAM/DB**: the pod's IRSA role is scoped to `sqs:SendMessage` on the `TransactionQueue` ARN only; DB access uses a separate read-only Postgres role's credentials (§2, §6) — neither grants write/delete on the shards or reporting instance.
- **Testing**: every branch of the write orchestration (valid + fresh, valid + dedup hit, invalid field, `SendMessage` failure leaving no cache entry, cache-unavailable fail-open) and every branch of the read orchestration (found/not-found by `transaction_no`, date-range happy path + pagination, invalid range) is unit tested against mocked `IAmazonSQS`/`HybridCache`/`ITransactionSearchRepository` — no live Redis, SQS, DB, or Gateway needed to run `dotnet test`.

## 5. gRPC Server & SQS Message Contract

Implements the server side of the shared contract at `components/contracts/transaction.proto` (already defined in `TransactionGateway.md` §5) via `Grpc.AspNetCore`.

**Field validation rules** (enforced before anything else runs):
- `currency`: must be a recognized 3-letter ISO 4217 code (small in-process allow-list for the demo).
- `type` / `status`: must be one of an application-level allowed-value set (kept in code, not a Postgres `ENUM` type, per `database.md` §6's reasoning — no DDL change needed on any cluster to add a new value).
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

**Search RPCs** (also part of the shared contract at `components/contracts/transaction.proto`):

```protobuf
rpc SearchByTransactionNo (SearchByTransactionNoRequest) returns (SearchByTransactionNoResponse);
rpc SearchByDateRange (SearchByDateRangeRequest) returns (SearchByDateRangeResponse);

message Transaction {
  int64 id = 1;
  string transaction_no = 2;
  string transaction_datetime = 3; // ISO-8601
  string amount = 4;               // decimal-as-string
  string type = 5;
  string status = 6;
  string currency = 7;
  string system_datetime = 8;      // ISO-8601
}

message SearchByTransactionNoRequest {
  string transaction_no = 1;
}
message SearchByTransactionNoResponse {
  bool found = 1;
  Transaction transaction = 2; // only set when found = true
}

message SearchByDateRangeRequest {
  string from = 1;  // ISO-8601, inclusive
  string to = 2;    // ISO-8601, inclusive
  int32 limit = 3;  // 1-500; TransactionGateway defaults/clamps before calling
  int32 offset = 4;
}
message SearchByDateRangeResponse {
  repeated Transaction transactions = 1;
  bool has_more = 2; // true if offset+limit didn't reach the end of the range
}
```

**`SearchByTransactionNo`:**
1. Reject an empty `transaction_no` with `INVALID_ARGUMENT`.
2. Resolve `shard_id = murmur3_32(transaction_no) % 3` via `components/ShardRouting` (the same library `TransactionWorker` uses to route inserts — database.md §10).
3. `SELECT ... FROM transactions WHERE transaction_no = @transaction_no` against that one shard's read-only connection.
4. `found = false` (not a gRPC error) if no row exists — a search miss is a normal outcome, not a failure.

**`SearchByDateRange`:**
1. Reject with `INVALID_ARGUMENT` if `from`/`to` don't parse as ISO-8601, if `from > to`, or if `limit`/`offset` are negative.
2. Clamp `limit` to `[1, 500]` if the caller sent `0` or something out of range (defense in depth — `TransactionGateway` already clamps before calling, §2).
3. `SELECT ... FROM transactions_reporting WHERE transaction_datetime BETWEEN @from AND @to ORDER BY transaction_datetime LIMIT @limit+1 OFFSET @offset` against the reporting instance's read-only connection — the `+1` is trimmed off the returned page and used only to compute `has_more`, avoiding a separate `COUNT(*)` query.
4. An empty result set is `has_more = false` with an empty `transactions` list, not an error.

Both RPCs bypass the `HybridCache` dedup guard and the SQS forwarder entirely — they're plain reads with no idempotency concern.

## 6. Terraform Notes (in `D:\git\microservice_1_terraform`)

- Second application of the `elasticache` module (the first was the Gateway's, per `TransactionGateway.md` §6) — same cheapest single-node sizing, region `ca-central-1`.
- IAM policy/role for this service's IRSA, scoped to `sqs:SendMessage` on the `TransactionQueue` ARN from `SQS.md`.
- Security group: only this service's pods may reach its ElastiCache instance.
- **New for search:** a read-only Postgres role (e.g. `transactionservice_reader`, `GRANT SELECT ON transactions TO ...` on each shard and `GRANT SELECT ON transactions_reporting TO ...` on the reporting instance) — created via the shard/reporting RDS instances' migration tooling, not IAM; credentials stored in Secrets Manager alongside `TransactionWorker`'s write-capable secrets (separate secret, since it's a different role). **Status (2026-08-01):** `db_readonly_username`/`db_readonly_password` exist as Terraform variables and a Secrets Manager entry (`ssm-outputs.tf`), but no migration tooling has actually run `CREATE ROLE` for it yet — this service is temporarily configured with the master `dbadmin` credentials instead (§2) until that's done.
- Security group: the 3 shard instances' and the reporting instance's security groups gain an ingress rule from this service's pod security group on `5432`, in addition to the existing rule for `TransactionWorker`'s pods.

## 7. Docker & Local Testing

- `Dockerfile`: multi-stage, `mcr.microsoft.com/dotnet/sdk:8.0` to build/publish, `mcr.microsoft.com/dotnet/aspnet:8.0` to run (Kestrel/HTTP2 is needed to host gRPC).
- `docker-compose.yml`: this service's container plus a local Redis container for convenient iteration; AWS credentials (profile/SSO) configured so it sends to the real dev `TransactionQueue`, per §2. Also needs the 3 shard connection strings and the reporting-instance connection string (env vars, same pattern as `TransactionWorker`'s `docker-compose.yml`) — search hits the real dev DB instances, no local Postgres container, consistent with this project's "local testing hits real AWS resources" rule (`KEDA.md` §9).
- Unit tests never touch real Redis, SQS, or Postgres — everything is mocked.

## 8. Observability (Instrumented, Not Shipped)

Same system-wide decision as `KEDA.md` §7 and `TransactionGateway.md` §8: **no Grafana Cloud stack wired up** for this demo, but the full OpenTelemetry .NET SDK instrumentation must exist in code.

- Traces: one `Activity` spanning gRPC receive → validate → dedup check → `SendMessage` → response, tagging validation failures, dedup hits, and send failures distinctly; a separate `Activity` per search RPC (shard resolved, row found/not-found, page size returned).
- Metrics: counters for requests received, validation failures, dedup hits, `SendMessage` failures, and successful forwards; separately, counters for search requests (by RPC), search misses (`transaction_no` not found), and a per-shard search counter for `SearchByTransactionNo` (same "is the hash routing even" signal as `TransactionWorker`'s per-shard insert counter, `KEDA.md` §7).
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

**Phase 9 — Search read path**
- Depend on `components/ShardRouting` (shared with `TransactionWorker`); implement `ITransactionSearchRepository` → `PostgresTransactionSearchRepository` (keyed shard connections + reporting-instance connection).
- Implement `SearchByTransactionNo`/`SearchByDateRange` on the gRPC service; wire the new RPCs from `components/contracts/transaction.proto`.
- Unit tests: found/not-found by `transaction_no`, date-range happy path + pagination (`has_more`), invalid range (`from > to`), limit clamping — all against a mocked `ITransactionSearchRepository`.
- Real validation against the dev shard/reporting instances is blocked on the DMS CDC pipeline (`database.md` §7's status note) actually running — deferred along with that.
