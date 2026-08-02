# TransactionGatewayService — HTTP-to-gRPC Edge Service (.NET 8)

## 1. Requirements

- Build a .NET 8 application, `TransactionGatewayService`, in `D:\git\microservice_1\components\TransactionGateway`.
- Receives HTTP requests from an ALB, forwards a gRPC request to `TransactionService` (per `TransactionService.md`).
- Dependency injection throughout; unit tested (xUnit); Dockerfile + docker-compose; follow .NET/.NET Core best practices.
- Uses Redis as a remote cache and local memory as a local (in-process) cache.
- Search transactions: `GET` by `transaction_no` and `GET` by `transaction_datetime` range — thin HTTP-to-gRPC forwarding to `TransactionService`'s new search RPCs (`TransactionService.md` §5), same as the existing submit path; see §5.

## 2. Decisions

| Decision | Choice | Why |
|---|---|---|
| Cache purpose | **Idempotency/dedup on `transaction_no`** | `transaction_no` is already the idempotency key everywhere downstream (the DB's `UNIQUE` constraint, the DynamoDB claim in `KEDA.md` §5) — reusing it here rejects a duplicate client retry at the front door instead of letting it travel all the way to SQS first. |
| Cache implementation | `Microsoft.Extensions.Caching.Hybrid` (`HybridCache`) — local memory (L1) + Redis (L2) | Built-in two-tier lookup with cache-stampede protection, rather than hand-rolling the L1/L2 logic. |
| Redis topology | Separate Redis per service (Gateway and `TransactionService` each get their own) | They guard different retry boundaries — Gateway's cache absorbs duplicate *HTTP* retries from the external client; `TransactionService`'s cache separately absorbs duplicate *gRPC* retries from the Gateway (e.g. if the Gateway times out and retries the call without knowing whether the first attempt landed). Sharing one instance would conflate those. |
| Redis hosting | Managed ElastiCache, 2 small instances (one per service), via Terraform | More production-like than self-hosted Redis pods; provisioned/torn down alongside the rest of the demo infra in `microservice_1_terraform`. |
| Deployment target | Same EKS cluster as `TransactionWorker` (`KEDA.md`) | One cluster to provision/manage for the whole demo; the ALB is created via the AWS Load Balancer Controller from a Kubernetes `Ingress`. |
| Response model | Fire-and-forget — HTTP `202 Accepted` as soon as `TransactionService` confirms the message reached SQS | The Gateway (and `TransactionService`) can't know the eventual DynamoDB-claim/DB-insert outcome anyway — that happens later, asynchronously, inside `TransactionWorker`. |
| Idempotent-retry response | Same `202 Accepted` on a cache hit (not `409`) | A blind client retry gets an identical response whether it was the original request or a dedup hit — no special client-side handling needed. |
| Cache write timing | **Written only after the gRPC call to `TransactionService` succeeds** | If the gRPC call fails, no cache entry is written — a legitimate client retry still goes through instead of being silently swallowed by a dedup entry for a request that never actually reached `TransactionService`. Same rule applied on the `TransactionService` side of its own SQS call (`TransactionService.md` §2). |
| Cache-unavailable behavior | Fail **open** (skip the dedup check, proceed to forward) | Correctness doesn't depend on this cache — it's a latency/cost optimization layered on top of the real idempotency guarantees further downstream (the DB's unique constraint, DynamoDB claim). Redis being briefly down should degrade to "maybe a duplicate reaches SQS," not "requests fail." |
| Observability | Full OpenTelemetry instrumentation in code (traces/metrics/logs); **no real Grafana Cloud stack wired up** | Same system-wide decision as `KEDA.md` §2/§7 — skip the cost of an actual observability backend for a demo, but the instrumentation itself must exist. See §8. |
| Search caching (new) | None — every search request forwards to `TransactionService` | Unlike the submit path, a search has no duplicate-retry/idempotency concern to guard against; a read-through cache would just be staleness risk for no correctness benefit in a demo. Simpler to have one cache (the dedup guard) doing one job. |
| Search response on a miss | HTTP `404 Not Found` for `GET /api/v1/transactions/{transaction_no}` | Unlike the fire-and-forget submit path's uniform `202`, a search result is either found or not — the client needs to be able to tell the difference, so this is a real synchronous read, not fire-and-forget. |
| Pagination defaults | Gateway applies `limit` default 50 / max 500 before calling `TransactionService` | Keeps the contract's default/clamp behavior in one place at the edge, closest to the client; `TransactionService` re-validates independently anyway (defense in depth, same reasoning as its field validation on the write path). |

## 3. Architecture Overview

```
                    ┌──────────────┐
   HTTP client ───► │  ALB (EKS,   │
                    │  AWS LB      │
                    │  Controller) │
                    └──────┬───────┘
                           ▼
              ┌─────────────────────────────┐
              │  TransactionGatewayService   │
              │  (EKS pod)                   │
              │  1. Validate request         │
              │  2. HybridCache dedup check   │◄──────┐
              │     on transaction_no         │       │
              │  3. gRPC call ──────────────┐ │  ElastiCache
              │  4. 202 Accepted            │ │  (Gateway's Redis)
              └──────────────────────────────┼─┘
                                             ▼
                              ┌─────────────────────────────┐
                              │  TransactionService (gRPC)   │
                              │  (EKS pod, ClusterIP only —   │
                              │   not internet-facing)        │
                              │  own HybridCache dedup check ◄┼── ElastiCache
                              │  → SQS TransactionQueue        │  (Service's Redis)
                              └─────────────────────────────┘
```

- `TransactionService` has **no ALB/Ingress** — it's reachable only inside the cluster (ClusterIP `Service`), since only the Gateway is meant to be called externally.
- The gRPC call between Gateway and Service is plain pod-to-pod HTTP/2 over the cluster network — no ALB involved internally.

## 4. .NET 8 Application Design

```
components/TransactionGateway/
├── TransactionGateway.sln
├── src/
│   ├── TransactionGateway.Api/              # ASP.NET Core minimal API, Program.cs, HTTP contract
│   ├── TransactionGateway.Application/      # IIdempotencyGuard, ITransactionForwarder, ITransactionSearchForwarder,
│   │                                         # ITransactionSearchHandler + orchestration logic
│   └── TransactionGateway.Infrastructure/   # Generated gRPC client, HybridCache/Redis wiring
├── tests/
│   └── TransactionGateway.UnitTests/        # xUnit + mocks for IIdempotencyGuard / gRPC client
├── Dockerfile
└── docker-compose.yml
```

- **DI registrations**: `HybridCache` (configured with the local `MemoryCache` + Redis `IDistributedCache` backing), a typed gRPC client (`AddGrpcClient<TransactionService.TransactionServiceClient>`), `IIdempotencyGuard` (wraps the `HybridCache` dedup check), `ITransactionSubmissionHandler` (validate → dedup check → gRPC call → response), `ITransactionSearchForwarder` (thin gRPC wrapper for the 2 search RPCs), `ITransactionSearchHandler` (validate query params → default/clamp pagination → gRPC call → response).
- **Config**: `IOptions<T>` for the ElastiCache endpoint, `TransactionService` gRPC address, and the idempotency-entry TTL (default 15 minutes — long enough to cover a realistic client-retry window, short enough not to matter for storage).
- **Resilience**: Polly retry/backoff around the gRPC call for transient failures; the dedup cache is checked *before* forwarding, so a client-side retry after a transient gRPC error is itself deduped rather than double-submitted. Search calls go through the same gRPC client/resilience policy, but bypass the dedup cache entirely (§2) since they're reads.
- **Testing**: `ITransactionSubmissionHandler`'s branches (fresh submission, dedup hit, cache-unavailable fail-open, gRPC failure) and `ITransactionSearchHandler`'s branches (found, not-found, invalid query params rejected before calling gRPC) are all unit tested against mocked `HybridCache`/gRPC client — no live Redis or `TransactionService` needed to run `dotnet test`.

## 5. HTTP & gRPC Contracts

**HTTP** (`POST /api/v1/transactions`):

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

`amount` is a JSON **string**, not a number — same float-precision reasoning as the gRPC contract below applies at this first hop too, so it's carried as a string the whole way through rather than round-tripping through a JSON number in between.

Response: `202 Accepted` (both for a fresh submission and for a deduped retry).

**gRPC contract** — a shared `.proto` lives at `components/contracts/transaction.proto` (referenced by both `TransactionGateway` and `TransactionService` projects, since this is a monorepo — no separate package/versioning needed for a demo):

```protobuf
syntax = "proto3";

service TransactionService {
  rpc SubmitTransaction (SubmitTransactionRequest) returns (SubmitTransactionResponse);
  rpc SearchByTransactionNo (SearchByTransactionNoRequest) returns (SearchByTransactionNoResponse);
  rpc SearchByDateRange (SearchByDateRangeRequest) returns (SearchByDateRangeResponse);
}

message SubmitTransactionRequest {
  string transaction_no = 1;
  string transaction_datetime = 2; // ISO-8601
  string amount = 3;               // decimal-as-string, avoids float precision issues over the wire
  string type = 4;
  string status = 5;
  string currency = 6;
}

message SubmitTransactionResponse {
  bool accepted = 1;
}

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
  int32 limit = 3;
  int32 offset = 4;
}
message SearchByDateRangeResponse {
  repeated Transaction transactions = 1;
  bool has_more = 2;
}
```

**Search HTTP endpoints** — thin forwarding, no dedup cache involved (§2):

`GET /api/v1/transactions/{transaction_no}`
- 200 with the transaction JSON (same shape as `SubmitTransaction`'s request body, plus `id` and `system_datetime`) if found.
- 404 if not found.
- 400 if `transaction_no` is empty (shouldn't normally happen given it's a route segment, but validated anyway for consistency with the range endpoint).

`GET /api/v1/transactions?from=&to=&limit=&offset=`
- `from`/`to`: required, ISO-8601.
- `limit`: optional, defaults to 50, clamped to `[1, 500]` (§2).
- `offset`: optional, defaults to 0.
- 200 with `{ "items": [...], "has_more": bool }`.
- 400 if `from`/`to` are missing/unparseable, or `from > to`.

Both endpoints map gRPC `INVALID_ARGUMENT` to HTTP `400`, and any other gRPC failure to `502` (consistent with the existing `SubmitTransaction` → `502` mapping on a forwarding failure).

## 6. Terraform Notes (in `D:\git\microservice_1_terraform`)

- An `elasticache` module, applied twice — once for the Gateway's Redis, once for `TransactionService`'s — cheapest single-node instance (e.g. `cache.t4g.micro`), no replication, region `ca-central-1`.
- AWS Load Balancer Controller installed on the shared EKS cluster (if not already, per `KEDA.md` §8); a Kubernetes `Ingress` resource for `TransactionGateway` triggers ALB provisioning.
- Security groups: only the EKS node/pod security group may reach either ElastiCache instance.

## 7. Docker & Local Testing

- `Dockerfile`: multi-stage, `mcr.microsoft.com/dotnet/sdk:8.0` to build/publish, `mcr.microsoft.com/dotnet/aspnet:8.0` to run (this one does serve HTTP, unlike `TransactionWorker`).
- `docker-compose.yml`: the Gateway container plus a local Redis container for convenient local iteration (Redis, unlike SQS, has no meaningful "must be the real cloud thing" requirement — a local container behaves identically). `TransactionService`'s address is configurable, so it can point at either a locally run `TransactionService` container or a shared dev deployment.
- Unit tests never touch real Redis or gRPC — everything is mocked.

## 8. Observability (Instrumented, Not Shipped)

Same system-wide decision as `KEDA.md` §7: **no Grafana Cloud stack wired up** for this demo, but the full OpenTelemetry .NET SDK instrumentation must exist in code, ready to point at Prometheus/Loki/Tempo later by swapping only the exporter configuration.

- Traces: one `Activity` spanning HTTP request → dedup check → gRPC call → response, tagging dedup hits and gRPC failures distinctly.
- Metrics: counters for requests received, dedup hits, gRPC failures, and cache-unavailable fail-opens.
- Logs: `ILogger` output wired through the OTel logging provider, correlated to traces via trace/span id.
- Exporter: `AddConsoleExporter()` (or no-op) for all three signals in this demo — the only piece that would change to `AddOtlpExporter()` if a real Grafana Cloud endpoint is ever wired up.
- Unit tests can assert expected activities/metrics are recorded (via `ActivityListener`) without needing an exporter or network call.

## 9. Implementation Plan

**Phase 1 — Terraform**
- `elasticache` module (×2) in `microservice_1_terraform`; Ingress/ALB wiring on the shared EKS cluster.

**Phase 2 — Solution scaffold**
- Create the `TransactionGateway.sln` layout from §4; wire DI, config binding.
- Stand up `TransactionGateway.UnitTests` alongside.

**Phase 3 — Shared gRPC contract**
- Author `components/contracts/transaction.proto`; wire `Grpc.Tools` codegen into both `TransactionGateway.Infrastructure` (client) and (later) `TransactionService` (server).

**Phase 4 — Idempotency guard**
- Implement `IIdempotencyGuard` on `HybridCache`; unit tests: fresh key, dedup hit, cache-unavailable fail-open.

**Phase 5 — HTTP endpoint & orchestration**
- Implement `POST /api/v1/transactions` → `ITransactionSubmissionHandler` (validate → dedup check → gRPC call → `202`); unit tests for every branch, including a deduped retry returning `202` without a second gRPC call.

**Phase 6 — Containerization**
- `Dockerfile` + `docker-compose.yml` (§7); Kubernetes Deployment + `Ingress` (ALB) manifests.

**Phase 7 — Validation**
- End-to-end demo run: submit a transaction, confirm `202`; replay the identical request, confirm it's deduped (no second message on `TransactionQueue`); stop the Gateway's Redis mid-run, confirm requests still succeed (fail-open) rather than failing.

**Phase 8 — Search endpoints**
- Extend the gRPC client for `SearchByTransactionNo`/`SearchByDateRange` (new RPCs on `components/contracts/transaction.proto`); implement `ITransactionSearchForwarder` and `ITransactionSearchHandler` (validate → default/clamp pagination → gRPC call → response).
- Add `GET /api/v1/transactions/{transaction_no}` and `GET /api/v1/transactions?from=&to=&limit=&offset=` (§5).
- Unit tests: found/not-found by `transaction_no`, date-range happy path, invalid query params rejected before calling gRPC.
- Real end-to-end validation against real data is blocked on `TransactionService.md`'s search path, which is itself blocked on the DMS CDC pipeline (`database.md` §7's status note) for the date-range case — deferred along with that.

Expose Swagger