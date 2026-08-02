-- Shard schema (database.md §6). Applied to shard-1 - all 3 logical shard
-- keys currently point at this one physical instance (database.md §1's
-- topology note). Kept alongside TransactionWorker since it owns the write
-- path into this table; TransactionService's search path reads from it too
-- but doesn't own the schema.
--
-- DROP first (not IF NOT EXISTS only) - kubernetes/db-init wipes and
-- reseeds the schema on every `just deploy-all`, by design, for this demo.
DROP TABLE IF EXISTS transactions CASCADE;

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
