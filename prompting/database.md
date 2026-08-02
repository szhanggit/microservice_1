# Sharded Transaction Database Design (PostgreSQL on AWS)

## 1. Requirements

- Shard database based on PostgreSQL for a microservice hosted on AWS.
- Transaction table, columns include (not limited to): `id`, `transaction_no`, `transaction_datetime`,
  `amount`, `type`, `status`, `currency`, `system_datetime`.
- Table is large and must be split ("cross") 3 PostgreSQL databases.
- Each transaction record is routed to a shard by `mod`.
- Existing records must be fetched either by time range or by `transaction_no`.

> **Engine note:** originally designed against Aurora MySQL. Switched to Aurora PostgreSQL because the target AWS account's Free Tier plan rejects `aurora-mysql` entirely (`CreateDBCluster: FreeTierRestrictionError` — only `aurora-postgresql` is offered). Then switched again, from Aurora PostgreSQL to **plain RDS PostgreSQL** (no Aurora at all): Free Tier accounts can only create Aurora *clusters* via AWS's "Express Configuration" mode, which uses its own "Internet Access Gateway" networking and explicitly rejects a custom VPC subnet group/security group (confirmed via a direct `aws rds create-db-cluster --with-express-configuration` test) — incompatible with keeping the DB private inside our own VPC. See `Terraform.md` for both account-limitation discoveries; this doc reflects the resulting topology change throughout (§2, §10, §11).

> **Topology note (permanent, as of 2026-08-01):** this account's RDS instance ceiling only allows 2 concurrent instances to actually provision (confirmed by direct testing — the account's published Service Quota is much higher, this looks like an AWS-side new-account throttle). shard-0 and shard-2's RDS instances have been dropped; only **shard-1** and the **reporting** instance exist. The `murmur3_32(transaction_no) % 3` routing logic itself is unchanged (§4) — all 3 shard keys (0, 1, 2) are simply configured to point at the same physical shard-1 instance for now, so the application-level sharding code, the routing library, and the DMS CDC source-task setup for shard-1 all continue to work unmodified. Re-split onto 3 physical instances if the account's ceiling is ever raised.

## 2. Decisions

| Decision | Choice | Why |
|---|---|---|
| Shard key | `hash(transaction_no) mod 3` | `transaction_no` is alphanumeric/UUID-like and externally generated (not sequential), so it must be hashed before the mod — a raw `mod` on a non-numeric key isn't possible, and a raw numeric id would spread rows unevenly if callers batch-generate ids. |
| Shard topology | 3 separate RDS PostgreSQL instances (not Aurora — see engine note above) | True fault isolation (one instance's outage doesn't affect the other shards) and independent scaling. Aurora (and Aurora Limitless, its native distributed-sharding option) would be architecturally nicer here, but this AWS account's Free Tier plan blocks Aurora cluster creation entirely for a VPC-private setup — plain RDS is the option that actually works on this account. |
| `id` generation | Globally unique, Snowflake-style, generated in the application | Each shard has its own local sequence (`SERIAL`/`IDENTITY`), so ids would collide across shards if left to Postgres. A Snowflake-style id also embeds the shard, so a lookup by `id` alone can go straight to the correct shard. |
| Time-range queries | Dedicated reporting RDS PostgreSQL instance, fed by CDC from the 3 shards | Scatter-gathering all 3 OLTP shards for every time-range query adds load and latency to the write path's instances. A separate instance keeps OLTP shards lean and lets the reporting table use Postgres-native declarative partitioning by month. |
| Archival | 3–6 months hot in shards + reporting instance, older data exported to S3 (Parquet) | Bounds shard/reporting-instance size at 100M+ rows/shard while keeping older data queryable via Athena/Glue for audit/compliance. |

## 3. Architecture Overview

```
                         ┌─────────────────────────┐
   write / point read    │   Shard Router (app      │
  ─────────────────────► │   layer or proxy, e.g.   │
                         │   ShardingSphere/Vitess) │
                         └───────┬───────┬───────┬──┘
                                 │       │       │
                     shard=0    │       │       │    shard=2
                    ┌───────────┘       │       └───────────┐
                    ▼                   ▼ shard=1            ▼
            ┌───────────────┐  ┌───────────────┐    ┌───────────────┐
            │ RDS            │  │ RDS            │    │ RDS            │
            │ PostgreSQL     │  │ PostgreSQL     │    │ PostgreSQL     │
            │  shard-0       │  │  shard-1       │    │  shard-2       │
            │ (txn table)    │  │ (txn table)    │    │ (txn table)    │
            └───────┬───────┘  └───────┬───────┘    └───────┬───────┘
                    │  CDC (AWS DMS)   │                    │
                    └──────────────────┼────────────────────┘
                                       ▼
                        ┌───────────────────────────┐
                        │ RDS PostgreSQL —           │
                        │ Reporting instance           │
                        │ (partitioned by month on    │
                        │ transaction_datetime)        │
                        └──────────────┬─────────────┘
                                       │ export partitions > 6mo
                                       ▼
                        ┌───────────────────────────┐
                        │  S3 (Parquet) + Glue       │
                        │  Catalog, queried via      │
                        │  Athena                    │
                        └───────────────────────────┘
```

- **3 OLTP shards** — identical schema, each an independent RDS PostgreSQL instance (Single-AZ — see §10 for why this isn't Multi-AZ/reader as originally specified). Handle writes and point lookups (`transaction_no`, `id`).
- **1 reporting instance** — receives changes from all 3 shards via CDC, used only for time-range queries and analytics.
- **S3 + Glue/Athena** — cold storage for data older than the hot window.

## 4. Shard Key & Routing

Route on a hash of `transaction_no`, not a language built-in `hash()` (those aren't guaranteed stable across processes/versions). Use a fixed, well-known 32-bit hash such as MurmurHash3 or CRC32:

```
shard_id = murmur3_32(transaction_no) % 3   // 0, 1, or 2
```

Because the hash is deterministic, a given `transaction_no` always maps to the same shard — inserts, updates, and point lookups by `transaction_no` are single-shard operations. The application's data-access layer (or a proxy such as ShardingSphere-Proxy / Vitess) owns this routing so callers never see the shard split directly.

`transaction_no` remains globally unique without cross-shard coordination: enforce a `UNIQUE` constraint on it *within* each shard — since a given value only ever routes to one shard, per-shard uniqueness is equivalent to global uniqueness.

Note on resharding: a straight `mod 3` means adding a 4th shard later requires re-routing (and physically moving) a large fraction of existing rows. If resharding-without-downtime is a real future concern, consider consistent hashing with virtual nodes instead — flagging this as a trade-off rather than building it now, since it adds complexity that isn't needed for a fixed 3-shard requirement.

## 5. Global ID Generation (Snowflake-style)

`id` is a 64-bit integer generated by the application before insert, not by a Postgres `SERIAL`/`IDENTITY` sequence (which is only unique per-instance):

```
 1 bit   41 bits              5 bits     5 bits      12 bits
┌───┬─────────────────────┬──────────┬──────────┬──────────────┐
│ 0 │ ms since custom epoch│ shard_id │ node_id  │  sequence    │
└───┴─────────────────────┴──────────┴──────────┴──────────────┘
```

- **41 bits timestamp**: milliseconds since a custom epoch (e.g. 2025-01-01), good for ~69 years.
- **5 bits shard_id**: the value computed in §4 (0–2 today; room for up to 32 shards without changing the format).
- **5 bits node_id**: identifies the app instance/generator writing to that shard, so multiple instances can mint ids concurrently without collisions (up to 32 concurrent generators per shard).
- **12 bits sequence**: counter reset every millisecond, up to 4096 ids/ms per node.

The leading bit is always `0` specifically so the value fits in a signed 64-bit integer — Postgres has no unsigned integer type, so the column is a plain `BIGINT`.

Because `shard_id` is embedded in `id`, a lookup by `id` alone can be decoded and routed to the correct shard — no different from routing by `transaction_no`.

## 6. OLTP Shard Schema

Identical DDL deployed to all 3 shard instances (via a migration tool such as Flyway/Liquibase, applied to each instance):

```sql
CREATE TABLE transactions (
    id                    BIGINT          NOT NULL,
    transaction_no        VARCHAR(64)     NOT NULL,
    transaction_datetime  TIMESTAMP(3)    NOT NULL,
    amount                NUMERIC(18,4)   NOT NULL,
    type                  VARCHAR(32)     NOT NULL,
    status                VARCHAR(32)     NOT NULL,
    currency              CHAR(3)         NOT NULL,
    system_datetime       TIMESTAMP(3)    NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (transaction_no)
);

CREATE INDEX idx_transaction_datetime ON transactions (transaction_datetime);

-- Postgres has no ON UPDATE CURRENT_TIMESTAMP equivalent - a trigger keeps
-- system_datetime current on every UPDATE, mirroring MySQL's built-in behavior.
CREATE OR REPLACE FUNCTION set_system_datetime()
RETURNS TRIGGER AS $$
BEGIN
    NEW.system_datetime := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_transactions_system_datetime
BEFORE UPDATE ON transactions
FOR EACH ROW
EXECUTE FUNCTION set_system_datetime();
```

Notes:
- `amount` uses `NUMERIC(18,4)` rather than `(18,2)` to accommodate currencies with more than 2 decimal places.
- `type`/`status` are plain `VARCHAR` rather than a Postgres `ENUM` type — adding a new value would only need `ALTER TYPE ... ADD VALUE` (lighter than MySQL's column redefinition), but it's still kept in application-level validation instead, so a new value never needs a DDL change on 3 instances at all.
- `idx_transaction_datetime` supports queries scoped to a single shard (e.g. reconciliation jobs); it is not the primary path for cross-shard time-range queries — that goes through the reporting instance (§7).

Column definitions as JSON (e.g. for an API contract or schema-validation config):

```json
{
  "transaction_no": { "type": "string", "maxLength": 64, "nullable": false },
  "transaction_datetime": { "type": "string", "format": "date-time", "precision": 3, "nullable": false },
  "amount": { "type": "number", "precision": 18, "scale": 4, "nullable": false },
  "type": { "type": "string", "maxLength": 32, "nullable": false },
  "status": { "type": "string", "maxLength": 32, "nullable": false },
  "currency": { "type": "string", "length": 3, "nullable": false }
}
```

## 7. Reporting Instance (Time-Range Queries)

Fed by CDC (AWS DMS, RDS PostgreSQL → RDS PostgreSQL) from all 3 shards. DMS is the more turnkey choice for straightforward RDS-to-RDS replication; Debezium is worth switching to only if custom transform logic is needed in the pipeline itself.

**Status:** the DMS pipeline's Terraform (replication instance, 3 source endpoints, 1 target endpoint, CDC tasks) is now being built — see `Terraform.md`'s `dms` module — but has not yet been applied/validated against real AWS (the whole stack was intentionally destroyed between work sessions to avoid idle cost, and live DMS validation was explicitly deferred). Until it's applied and confirmed replicating, this table has no data. `TransactionService`'s new `transaction_datetime` range search (`TransactionService.md` §5) queries this table directly, so that search path won't return real results until the pipeline is live.

```sql
CREATE TABLE transactions_reporting (
    id                    BIGINT          NOT NULL,
    transaction_no        VARCHAR(64)     NOT NULL,
    transaction_datetime  TIMESTAMP(3)    NOT NULL,
    amount                NUMERIC(18,4)   NOT NULL,
    type                  VARCHAR(32)     NOT NULL,
    status                VARCHAR(32)     NOT NULL,
    currency              CHAR(3)         NOT NULL,
    system_datetime       TIMESTAMP(3)    NOT NULL,
    shard_id              SMALLINT        NOT NULL,
    PRIMARY KEY (id, transaction_datetime)
) PARTITION BY RANGE (transaction_datetime);

CREATE TABLE transactions_reporting_2026_07 PARTITION OF transactions_reporting
    FOR VALUES FROM ('2026-07-01') TO ('2026-08-01');
CREATE TABLE transactions_reporting_2026_08 PARTITION OF transactions_reporting
    FOR VALUES FROM ('2026-08-01') TO ('2026-09-01');
CREATE TABLE transactions_reporting_2026_09 PARTITION OF transactions_reporting
    FOR VALUES FROM ('2026-09-01') TO ('2026-10-01');
-- Safety net only - the monthly job below always creates next month's
-- partition ahead of time, so nothing should actually land here.
CREATE TABLE transactions_reporting_default PARTITION OF transactions_reporting DEFAULT;

-- Indexes on a partitioned parent propagate to every partition automatically
-- (Postgres 11+) - no need to repeat these per partition.
CREATE INDEX idx_transaction_no ON transactions_reporting (transaction_no);
CREATE INDEX idx_datetime_status ON transactions_reporting (transaction_datetime, status);
```

- `transaction_datetime` must be part of the primary key for `PARTITION BY RANGE` to be valid alongside the unique `id` — Postgres requires every unique/primary key on a partitioned table to include the partition key.
- A monthly job (Lambda on an EventBridge schedule, or the `pg_cron` extension if enabled on the instance) creates the next month's partition ahead of time — a plain `CREATE TABLE ... PARTITION OF ... FOR VALUES FROM/TO`, not a MySQL-style "split" of a catch-all partition.
- This instance is read-only from the application's perspective — all writes arrive via CDC from the 3 shards. It can be scaled with additional RDS read replicas independent of OLTP shard capacity.

## 8. Query Patterns

| Query | Path |
|---|---|
| Insert / update a transaction | Compute `shard_id` from `transaction_no`, write to that shard only. Implemented by `TransactionWorker` (`KEDA.md` §4–5). |
| Fetch by `transaction_no` | Compute `shard_id` from `transaction_no`, single-shard `SELECT`. Implemented by `TransactionService`'s `SearchByTransactionNo` (`TransactionService.md` §5), exposed via `TransactionGateway`'s `GET /api/v1/transactions/{transaction_no}` (`TransactionGateway.md` §5). |
| Fetch by `id` | Decode `shard_id` from the id's bit layout (§5), single-shard `SELECT`. Not yet exposed by any service - no current requirement calls for looking up by the internal `id` rather than `transaction_no`. |
| Fetch by time range | Single `SELECT` against `transactions_reporting`, pruned to the relevant monthly partition(s). Implemented by `TransactionService`'s `SearchByDateRange` (`TransactionService.md` §5), exposed via `TransactionGateway`'s `GET /api/v1/transactions?from=&to=` (`TransactionGateway.md` §5) - see §7's status note on the CDC pipeline this depends on. |

No query pattern in the current requirements needs a cross-shard transaction or a scatter-gather read against the OLTP shards.

## 9. Archival

- Hot window: 3–6 months, kept in both the OLTP shards and the reporting instance.
- A scheduled job exports partitions older than the hot window to S3 as Parquet (e.g. via RDS PostgreSQL's `aws_s3.query_export_to_s3` extension, or a Glue ETL job reading the about-to-be-dropped partition), registers them in the Glue Data Catalog, then drops the partition from the reporting instance and deletes the corresponding rows from the OLTP shards.
- Archived data remains queryable via Athena for audit/compliance without keeping it in either Postgres tier.

## 10. Operational Notes

- **Demo simplification:** originally specified as Multi-AZ with at least one reader per shard/reporting instance for failover/read scaling. This account's Free Tier plan is Single-AZ only (and blocks Aurora entirely — see the engine/topology note in §1), so all 4 instances currently run as a single Single-AZ instance with no reader. Revisit Multi-AZ/read replicas if this ever runs on an unrestricted account.
- Shard routing logic (hash function, node/shard id assignment) must be a single shared library used by every service instance — a mismatch between two instances' routing logic would send writes and lookups for the same `transaction_no` to different shards. This lives at `components/ShardRouting` (a plain .NET class library, referenced directly by both `TransactionWorker.Domain` and `TransactionService.Application`) — not owned by either service.
- Because `shard_id` is fixed at 3 for the life of this ID format, revisit the routing scheme (see resharding note in §4) before provisioning a 4th shard.

## 11. Implementation Plan

The repo is currently empty aside from this doc, so this is a greenfield build — no existing data migration is required. Phases are ordered by dependency; phases 5–7 can run in parallel with each other once phase 4 is done.

**Phase 1 — Infrastructure (Terraform)** ✅ (2 of the originally-planned 4 instances — see §1's topology note)
- ✅ VPC/subnet groups, security groups — provisioned for shard-1 + reporting instance (shard-0/shard-2 dropped, §1).
- ✅ RDS PostgreSQL instances: `microservice1-develop-shard-1` + `microservice1-develop-reporting`, Single-AZ, encrypted storage — plain RDS, not Aurora, per this account's Free Tier restrictions (§1's engine/topology note; full history in `Terraform.md`).
- ✅ Credentials in Secrets Manager (`microservice1/develop/db-{shard-1,reporting}-connection-string` + `-readonly-connection-string`, `ssm-outputs.tf`).
- ⬜ Parameter groups tuned for a write-heavy OLTP workload (checkpoint/WAL settings) — only `rds.logical_replication` is currently set; write-throughput tuning not yet done.

**Phase 2 — Schema & migrations**
- Set up a migration tool (Flyway/Liquibase) with one migration set applied identically to all 3 shard instances.
- Separate migration set for the reporting instance's partitioned table (§7).
- Lambda + EventBridge monthly schedule to pre-create the next partition (`CREATE TABLE ... PARTITION OF`) on the reporting instance; test it creates a partition at least one month ahead.

**Phase 3 — Shard routing & ID generation library** ✅ (routing) / ✅ (id generation)
- ✅ Shared library (used by every service instance) implementing `shard_id = murmur3_32(transaction_no) % 3` — `components/ShardRouting`, referenced by both `TransactionWorker.Domain` and `TransactionService.Application`.
- ✅ Snowflake-style id generator/decoder from §5 — `TransactionWorker.Domain` (id generation is `TransactionWorker`-only per `TransactionService.md`'s decision table; `TransactionService` only needs routing, not generation).
- ✅ Per-instance `node_id` assignment derived from pod ordinal/hostname (`TransactionWorker.Infrastructure`'s `HostnameWorkerIdentity`, hashed via the shared routing library's `MurmurHash3`).
- ✅ Unit tests: routing determinism (`ShardRouting.Tests`), id uniqueness under concurrent generation, id → shard decode round-trip (`TransactionWorker.UnitTests`).

**Phase 4 — Application data-access layer** (insert + fetch-by-`transaction_no` done; update and fetch-by-`id` not needed by any current requirement)
- ✅ Connection pools for all 3 shard instances, selected per-call via the routing library — `TransactionWorker.Infrastructure.PostgresTransactionRepository` (insert), `TransactionService.Infrastructure`'s search repository (fetch-by-`transaction_no`).
- ✅ Repository methods: insert (`TransactionWorker`), fetch-by-`transaction_no` (`TransactionService`), both resolving to a single shard. `update` and fetch-by-`id` are not implemented - no current requirement needs them.
- ⬜ Integration tests against local/dev RDS (or PostgreSQL-compatible test containers) covering all 3 shards — only mock-based unit tests exist so far in both services.

**Phase 5 — CDC pipeline** (Terraform written, not yet applied/validated - see §7's status note)
- ✅ AWS DMS replication instance; 3 source endpoints (shard-0/1/2) and 1 target endpoint (reporting instance) — `Terraform.md`'s `dms` module.
- ✅ Replication tasks with a transform that stamps each row with its source `shard_id`.
- ⬜ Validate replication lag and row-count parity between each shard and the reporting instance under load — blocked on applying the Terraform stack, deferred intentionally.

**Phase 6 — Reporting/time-range query path** (implemented, not yet live-tested)
- ✅ Implement the time-range read API against `transactions_reporting` — `TransactionService`'s `SearchByDateRange` (`TransactionService.md` §5), limit+offset pagination (default 50/max 500).
- ⬜ Confirm query plans hit partition pruning; load-test with realistic date ranges to size reader capacity — needs Phase 5's pipeline actually running with real data first.

**Phase 7 — Archival pipeline**
- S3 bucket (with lifecycle policy) + Glue Data Catalog table for archived transactions.
- Scheduled export job: dump partitions older than the hot window (3–6 months) to Parquet, register in Glue, verify row counts against source before deleting.
- Drop the exported partition from the reporting instance and delete the corresponding rows from the OLTP shards only after export is verified.
- Athena queries validated against archived data for at least one full exported month.

**Phase 8 — Observability**
- CloudWatch alarms: DMS replication lag, shard CPU/storage/connections, reporting instance partition count, archival job failures.
- Dashboard covering all 3 shards + reporting instance side by side (per-shard write skew is the key signal that the hash routing isn't distributing evenly).

**Phase 9 — Validation & cutover**
- Failover drill on one shard instance (once Multi-AZ is restored — see §10's demo simplification); confirm the app's connection pool reconnects without cross-shard side effects.
- Consistency check: sample transactions across all 3 shards, confirm identical rows exist in the reporting instance.
- Only after phases 1–8 pass in a non-prod environment: deploy to production, enable CDC, then enable the archival job (the archival job should stay disabled until the first cohort of data has actually aged past the hot window, so there's nothing for it to act on prematurely).
