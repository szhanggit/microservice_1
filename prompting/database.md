# Sharded Transaction Database Design (Aurora MySQL on AWS)

## 1. Requirements

- Shard database based on Aurora MySQL for a microservice hosted on AWS.
- Transaction table, columns include (not limited to): `id`, `transaction_no`, `transaction_datetime`,
  `amount`, `type`, `status`, `currency`, `system_datetime`.
- Table is large and must be split ("cross") 3 MySQL databases.
- Each transaction record is routed to a shard by `mod`.
- Existing records must be fetched either by time range or by `transaction_no`.

## 2. Decisions

| Decision | Choice | Why |
|---|---|---|
| Shard key | `hash(transaction_no) mod 3` | `transaction_no` is alphanumeric/UUID-like and externally generated (not sequential), so it must be hashed before the mod — a raw `mod` on a non-numeric key isn't possible, and a raw numeric id would spread rows unevenly if callers batch-generate ids. |
| Shard topology | 3 separate Aurora MySQL clusters | True fault isolation (one cluster's outage doesn't affect the other shards) and independent scaling. Aurora Limitless (AWS's native distributed-sharding option) is currently PostgreSQL-only, so it isn't usable for a MySQL requirement. |
| `id` generation | Globally unique, Snowflake-style, generated in the application | Each shard has its own `AUTO_INCREMENT` sequence, so ids would collide across shards if left to MySQL. A Snowflake-style id also embeds the shard, so a lookup by `id` alone can go straight to the correct shard. |
| Time-range queries | Dedicated reporting Aurora MySQL cluster, fed by CDC from the 3 shards | Scatter-gathering all 3 OLTP shards for every time-range query adds load and latency to the write path's clusters. A separate cluster keeps OLTP shards lean and lets the reporting table use MySQL-native partitioning by month. |
| Archival | 3–6 months hot in shards + reporting cluster, older data exported to S3 (Parquet) | Bounds shard/reporting-cluster size at 100M+ rows/shard while keeping older data queryable via Athena/Glue for audit/compliance. |

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
            │ Aurora MySQL  │  │ Aurora MySQL  │    │ Aurora MySQL  │
            │  shard-0      │  │  shard-1      │    │  shard-2      │
            │ (txn table)   │  │ (txn table)   │    │ (txn table)   │
            └───────┬───────┘  └───────┬───────┘    └───────┬───────┘
                    │  CDC (AWS DMS)   │                    │
                    └──────────────────┼────────────────────┘
                                       ▼
                        ┌───────────────────────────┐
                        │ Aurora MySQL — Reporting   │
                        │ cluster (partitioned by    │
                        │ month on transaction_      │
                        │ datetime)                  │
                        └──────────────┬─────────────┘
                                       │ export partitions > 6mo
                                       ▼
                        ┌───────────────────────────┐
                        │  S3 (Parquet) + Glue       │
                        │  Catalog, queried via      │
                        │  Athena                    │
                        └───────────────────────────┘
```

- **3 OLTP shards** — identical schema, each an independent Aurora MySQL cluster (writer + reader(s), Multi-AZ). Handle writes and point lookups (`transaction_no`, `id`).
- **1 reporting cluster** — receives changes from all 3 shards via CDC, used only for time-range queries and analytics.
- **S3 + Glue/Athena** — cold storage for data older than the hot window.

## 4. Shard Key & Routing

Route on a hash of `transaction_no`, not a language built-in `hash()` (those aren't guaranteed stable across processes/versions). Use a fixed, well-known 32-bit hash such as MurmurHash3 or CRC32:

```
shard_id = murmur3_32(transaction_no) % 3   // 0, 1, or 2
```

Because the hash is deterministic, a given `transaction_no` always maps to the same shard — inserts, updates, and point lookups by `transaction_no` are single-shard operations. The application's data-access layer (or a proxy such as ShardingSphere-Proxy / Vitess) owns this routing so callers never see the shard split directly.

`transaction_no` remains globally unique without cross-shard coordination: enforce `UNIQUE KEY` on it *within* each shard — since a given value only ever routes to one shard, per-shard uniqueness is equivalent to global uniqueness.

Note on resharding: a straight `mod 3` means adding a 4th shard later requires re-routing (and physically moving) a large fraction of existing rows. If resharding-without-downtime is a real future concern, consider consistent hashing with virtual nodes instead — flagging this as a trade-off rather than building it now, since it adds complexity that isn't needed for a fixed 3-shard requirement.

## 5. Global ID Generation (Snowflake-style)

`id` is a 64-bit unsigned integer generated by the application before insert, not by MySQL `AUTO_INCREMENT` (which is only unique per-cluster):

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

Because `shard_id` is embedded in `id`, a lookup by `id` alone can be decoded and routed to the correct shard — no different from routing by `transaction_no`.

## 6. OLTP Shard Schema

Identical DDL deployed to all 3 shard clusters (via a migration tool such as Flyway/Liquibase, applied to each cluster):

```sql
CREATE TABLE transactions (
    id                    BIGINT UNSIGNED NOT NULL,
    transaction_no        VARCHAR(64)     NOT NULL,
    transaction_datetime  DATETIME(3)     NOT NULL,
    amount                DECIMAL(18,4)   NOT NULL,
    type                  VARCHAR(32)     NOT NULL,
    status                VARCHAR(32)     NOT NULL,
    currency              CHAR(3)         NOT NULL,
    system_datetime       DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                           ON UPDATE CURRENT_TIMESTAMP(3),
    PRIMARY KEY (id),
    UNIQUE KEY uq_transaction_no (transaction_no),
    KEY idx_transaction_datetime (transaction_datetime)
) ENGINE=InnoDB;
```

Notes:
- `amount` uses `DECIMAL(18,4)` rather than `(18,2)` to accommodate currencies with more than 2 decimal places.
- `type`/`status` are plain `VARCHAR` rather than `ENUM` — adding a new type/status value shouldn't require an `ALTER TABLE` on 3 clusters; validate the value set at the application layer instead.
- `idx_transaction_datetime` supports queries scoped to a single shard (e.g. reconciliation jobs); it is not the primary path for cross-shard time-range queries — that goes through the reporting cluster (§7).

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

## 7. Reporting Cluster (Time-Range Queries)

Fed by CDC (AWS DMS, Aurora MySQL → Aurora MySQL) from all 3 shards. DMS is the more turnkey choice for straightforward Aurora-to-Aurora replication; Debezium is worth switching to only if custom transform logic is needed in the pipeline itself.

```sql
CREATE TABLE transactions_reporting (
    id                    BIGINT UNSIGNED NOT NULL,
    transaction_no        VARCHAR(64)     NOT NULL,
    transaction_datetime  DATETIME(3)     NOT NULL,
    amount                DECIMAL(18,4)   NOT NULL,
    type                  VARCHAR(32)     NOT NULL,
    status                VARCHAR(32)     NOT NULL,
    currency              CHAR(3)         NOT NULL,
    system_datetime       DATETIME(3)     NOT NULL,
    shard_id              TINYINT UNSIGNED NOT NULL,
    PRIMARY KEY (id, transaction_datetime),
    KEY idx_transaction_no (transaction_no),
    KEY idx_datetime_status (transaction_datetime, status)
) ENGINE=InnoDB
PARTITION BY RANGE COLUMNS(transaction_datetime) (
    PARTITION p2026_07 VALUES LESS THAN ('2026-08-01'),
    PARTITION p2026_08 VALUES LESS THAN ('2026-09-01'),
    PARTITION p2026_09 VALUES LESS THAN ('2026-10-01'),
    PARTITION pmax     VALUES LESS THAN (MAXVALUE)
);
```

- `transaction_datetime` must be part of the primary key for `PARTITION BY RANGE COLUMNS` to be valid alongside the unique `id`.
- A monthly job (Lambda on an EventBridge schedule, or a MySQL event) adds the next month's partition ahead of time and splits `pmax`.
- This cluster is read-only from the application's perspective — all writes arrive via CDC from the 3 shards. It can be scaled with additional Aurora read replicas independent of OLTP shard capacity.

## 8. Query Patterns

| Query | Path |
|---|---|
| Insert / update a transaction | Compute `shard_id` from `transaction_no`, write to that shard only. |
| Fetch by `transaction_no` | Compute `shard_id` from `transaction_no`, single-shard `SELECT`. |
| Fetch by `id` | Decode `shard_id` from the id's bit layout (§5), single-shard `SELECT`. |
| Fetch by time range | Single `SELECT` against `transactions_reporting`, pruned to the relevant monthly partition(s). |

No query pattern in the current requirements needs a cross-shard transaction or a scatter-gather read against the OLTP shards.

## 9. Archival

- Hot window: 3–6 months, kept in both the OLTP shards and the reporting cluster.
- A scheduled job exports partitions older than the hot window to S3 as Parquet (e.g. via Aurora's `SELECT ... INTO OUTFILE S3` or a Glue ETL job reading the about-to-be-dropped partition), registers them in the Glue Data Catalog, then drops the partition from the reporting cluster and deletes the corresponding rows from the OLTP shards.
- Archived data remains queryable via Athena for audit/compliance without keeping it in either Aurora tier.

## 10. Operational Notes

- Each of the 3 OLTP clusters and the reporting cluster should run Multi-AZ with at least one reader for failover/read scaling.
- Shard routing logic (hash function, node/shard id assignment) must be a single shared library used by every service instance — a mismatch between two instances' routing logic would send writes and lookups for the same `transaction_no` to different shards.
- Because `shard_id` is fixed at 3 for the life of this ID format, revisit the routing scheme (see resharding note in §4) before provisioning a 4th shard.

## 11. Implementation Plan

The repo is currently empty aside from this doc, so this is a greenfield build — no existing data migration is required. Phases are ordered by dependency; phases 5–7 can run in parallel with each other once phase 4 is done.

**Phase 1 — Infrastructure (Terraform)**
- VPC/subnet groups, security groups for the 3 shard clusters + reporting cluster.
- 3 Aurora MySQL clusters (shard-0/1/2): writer + 1 reader each, Multi-AZ, encrypted storage.
- 1 Aurora MySQL reporting cluster: writer + reader(s), Multi-AZ.
- Sized as Aurora Serverless v2 (low min ACU) rather than fixed provisioned instances — decided in `Terraform.md` §3 for demo cost; Serverless v2 still supports the writer/reader Multi-AZ topology above, just with elastic capacity instead of a fixed instance class.
- Credentials in Secrets Manager (one secret per cluster); parameter groups tuned for InnoDB write-heavy workload on the shards.

**Phase 2 — Schema & migrations**
- Set up a migration tool (Flyway/Liquibase) with one migration set applied identically to all 3 shard clusters.
- Separate migration set for the reporting cluster's partitioned table (§7).
- Lambda + EventBridge monthly schedule to pre-create the next partition and split `pmax` on the reporting cluster; test it creates a partition at least one month ahead.

**Phase 3 — Shard routing & ID generation library**
- Shared library (used by every service instance) implementing: `shard_id = murmur3_32(transaction_no) % 3`, and the Snowflake-style id generator/decoder from §5.
- Per-instance `node_id` assignment (e.g. derived from pod ordinal/hostname) to avoid id collisions across concurrently-running instances.
- Unit tests: routing determinism, id uniqueness under concurrent generation, id → shard decode round-trip.

**Phase 4 — Application data-access layer**
- Connection pools for all 3 shard clusters, selected per-call via the routing library.
- Repository methods: insert, update, fetch-by-`transaction_no`, fetch-by-`id`, all resolving to a single shard.
- Integration tests against local/dev Aurora (or MySQL-compatible test containers) covering all 3 shards.

**Phase 5 — CDC pipeline**
- AWS DMS replication instance; 3 source endpoints (shard-0/1/2) and 1 target endpoint (reporting cluster).
- Replication tasks with a transform that stamps each row with its source `shard_id`.
- Validate replication lag and row-count parity between each shard and the reporting cluster under load.

**Phase 6 — Reporting/time-range query path**
- Implement the time-range read API against `transactions_reporting`, confirming query plans hit partition pruning.
- Load-test with realistic date ranges to size reader capacity.

**Phase 7 — Archival pipeline**
- S3 bucket (with lifecycle policy) + Glue Data Catalog table for archived transactions.
- Scheduled export job: dump partitions older than the hot window (3–6 months) to Parquet, register in Glue, verify row counts against source before deleting.
- Drop the exported partition from the reporting cluster and delete the corresponding rows from the OLTP shards only after export is verified.
- Athena queries validated against archived data for at least one full exported month.

**Phase 8 — Observability**
- CloudWatch alarms: DMS replication lag, shard CPU/storage/connections, reporting cluster partition count, archival job failures.
- Dashboard covering all 3 shards + reporting cluster side by side (per-shard write skew is the key signal that the hash routing isn't distributing evenly).

**Phase 9 — Validation & cutover**
- Failover drill on one shard cluster; confirm the app's connection pool reconnects without cross-shard side effects.
- Consistency check: sample transactions across all 3 shards, confirm identical rows exist in the reporting cluster.
- Only after phases 1–8 pass in a non-prod environment: deploy to production, enable CDC, then enable the archival job (the archival job should stay disabled until the first cohort of data has actually aged past the hot window, so there's nothing for it to act on prematurely).
