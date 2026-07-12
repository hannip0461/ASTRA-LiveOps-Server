# Persistence Retention

## Idempotency Lifecycle

Successful player commands store the completed response envelope and snapshot with a 24-hour expiry. There is no request-level `PENDING` row. A retry before expiry returns the stored response; a retry after expiry is a new command.

Two cleanup paths share the same expiry rule:

1. an account command deletes that player's expired rows after taking the player lock;
2. `PersistenceCleanupWorker` removes expired rows for accounts that no longer receive traffic.

The global path defaults to a one-hour grace after expiry. The grace is operational only and does not extend replay eligibility.

## Database Guardrails

The cleanup query uses the composite `(expires_at, player_id, idempotency_key)` retention index and selects candidates in the same order with `FOR UPDATE SKIP LOCKED`. Concurrent workers therefore take disjoint batches instead of waiting on each other.

Default limits:

| Setting | Default |
| --- | ---: |
| Cleanup interval | 1 hour |
| Rows per table and batch | 500 |
| Maximum batches per cycle | 20 |
| SQL command timeout | 5 seconds |
| Expired idempotency grace | 1 hour |

Startup validation caps a batch at 10,000 rows, a cycle at 100 batches, and command timeout at 30 seconds. A timeout rolls back the statement and is recorded by `astra.persistence.cleanup.failures`; it never falls back to an unbounded delete.

The same PostgreSQL statement performs bounded published-Outbox, orphan-delivery, and expired-idempotency cleanup. `pending`, `processing`, and `dead_letter` Outbox rows are excluded.

## Disk Behavior

Regular PostgreSQL `DELETE` makes dead tuples reusable after autovacuum; it does not immediately shrink the Docker volume file. This avoids table rewrite locks. Physical file compaction such as `VACUUM FULL` is intentionally not run by the application and belongs in an explicitly scheduled maintenance window.

No cleanup path deletes ledger, gacha history, mail claims, or admin audit records. Those records require separate product/legal retention decisions rather than inheriting an idempotency TTL.
