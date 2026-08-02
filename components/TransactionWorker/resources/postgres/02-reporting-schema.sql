-- Reporting schema (database.md §7). Applied to the reporting instance, fed
-- by the DMS CDC pipeline (Terraform.md - still unvalidated as of this
-- writing) and read by TransactionService's SearchByDateRange. Kept
-- alongside 01-shard-schema.sql for one source of truth, even though neither
-- TransactionWorker nor TransactionService "owns" this table outright.
--
-- DROP first (not IF NOT EXISTS only) - kubernetes/db-init wipes and
-- reseeds the schema on every `just deploy-all`, by design, for this demo.
DROP TABLE IF EXISTS transactions_reporting CASCADE;

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

-- Fixed months, matching database.md §7 exactly - a real deployment needs
-- the still-not-built monthly Lambda/EventBridge job (database.md §11 Phase
-- 2) to keep pre-creating the next month's partition; this file only
-- guarantees the current window exists on a fresh schema reset.
CREATE TABLE transactions_reporting_2026_07 PARTITION OF transactions_reporting
    FOR VALUES FROM ('2026-07-01') TO ('2026-08-01');
CREATE TABLE transactions_reporting_2026_08 PARTITION OF transactions_reporting
    FOR VALUES FROM ('2026-08-01') TO ('2026-09-01');
CREATE TABLE transactions_reporting_2026_09 PARTITION OF transactions_reporting
    FOR VALUES FROM ('2026-09-01') TO ('2026-10-01');
-- Safety net only - the monthly job above always creates next month's
-- partition ahead of time, so nothing should actually land here.
CREATE TABLE transactions_reporting_default PARTITION OF transactions_reporting DEFAULT;

-- Indexes on a partitioned parent propagate to every partition automatically
-- (Postgres 11+) - no need to repeat these per partition.
CREATE INDEX idx_transaction_no ON transactions_reporting (transaction_no);
CREATE INDEX idx_datetime_status ON transactions_reporting (transaction_datetime, status);
