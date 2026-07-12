DROP INDEX IF EXISTS ix_idempotency_expires_at;

CREATE INDEX IF NOT EXISTS ix_idempotency_retention
    ON idempotency_requests(expires_at, player_id, idempotency_key);
