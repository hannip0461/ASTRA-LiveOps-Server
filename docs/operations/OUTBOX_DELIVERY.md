# Outbox Delivery

## Consistency Boundary

A player command commits its domain state, ledger/history, completed idempotency response, and pending outbox row in one PostgreSQL transaction. The client response waits for that commit; it does not wait for a downstream consumer.

Delivery is **at least once**, not exactly once. Workers lease ready rows with `FOR UPDATE SKIP LOCKED`. An expired processing lease returns to pending so another worker can recover it.

## Consumer Idempotency

Each outbox row carries a versioned, event-specific payload rather than copying the API response or player snapshot. `PostgresOperationalEventHandler` is the first concrete consumer adapter. It writes a smaller operational projection under the primary key `(consumer_name, event_id)`. A repeated delivery uses `ON CONFLICT DO NOTHING`. During rolling deployment it can also read the previous response-shaped payload format, which is projected as schema version `0`.

This closes the important crash window:

1. the consumer projection commits;
2. the worker crashes before marking the outbox row published;
3. the lease expires and the event is delivered again;
4. the consumer observes the same key, performs no duplicate effect, and the worker marks it published.

The projection contains only event-specific operational fields such as content version, reward count, and ledger version. Full command responses, idempotency keys, wallet contents, and unexpected payload properties are not copied. Unknown event types and payload/schema mismatches fail closed.

The PostgreSQL projection is a portfolio-stage consumer that makes the delivery boundary executable without requiring another broker image. A production notification, analytics, or stream adapter can replace it while retaining the same event ID idempotency contract.

## Retry And Dead Letter

Handler failures use bounded exponential retry. The database stores a stable error code rather than an exception message:

- `outbox_event_unsupported`;
- `outbox_payload_invalid`;
- `outbox_consumer_store_unavailable`;
- `outbox_consumer_failed`.

After `max_attempts`, the row moves to `dead_letter` and records `dead_lettered_at`. It is no longer leased automatically.

A Supervisor can replay a dead-letter event only through the audited Admin API. Replay requires a reason, resets attempts, increments `manual_replay_count`, and returns the row to pending. Viewer endpoints expose counts and dead-letter metadata but never payload or idempotency-key contents.

## Retention

`PersistenceCleanupWorker` runs once at startup and then on a bounded interval. Defaults are seven days for published outbox rows and thirty days for orphan delivery projections. Each cycle has fixed batch, maximum-batch, and query-timeout limits. The same cycle also removes expired idempotency records; its complete policy is documented in `docs/persistence/RETENTION.md`.

Published outbox rows and their consumer delivery projections are deleted in one PostgreSQL statement. This prevents cleanup from removing the idempotency record while leaving a source event eligible for redelivery. `pending`, `processing`, and `dead_letter` rows are never removed automatically. An orphan projection is eligible only when its source outbox row no longer exists and its separate retention window has expired.

The hard-kill integration test starts a separate consumer process, waits until its projection commits, kills the process before the outbox status update, and verifies that lease recovery publishes the event without creating a second projection.

The operational observability probe adds a separate runtime demonstration. It inserts one valid versioned event and one event with an unsupported payload version. The real Worker publishes the valid event and drives the invalid event through one retry to `dead_letter`; the script then verifies the three delta metrics and the Kibana dead-letter rule. Previous probe rows are removed by their dedicated `operational-probe-` idempotency-key prefix, leaving only the latest evidence pair.

## Operations Surface

| Route | Minimum role | Purpose |
| --- | --- | --- |
| `GET /api/admin/outbox/overview` | Viewer | Backlog, delivery, and dead-letter counts |
| `GET /api/admin/outbox/dead-letters` | Viewer | Bounded terminal-failure list |
| `POST /api/admin/outbox/dead-letters/{eventId}/replay` | Supervisor | Reason-required audited replay |

The Blazor `/outbox` page uses these routes. The replay action is recorded as `outbox.dead_letter.replay` in `admin_operation_audit`.

## Telemetry

The worker emits traces from `Astra.LiveOps` and low-cardinality metrics:

- `astra.outbox.leased`;
- `astra.outbox.published`;
- `astra.outbox.retry_scheduled`;
- `astra.outbox.dead_lettered`;
- `astra.outbox.cycle_failures`;
- `astra.outbox.processing.duration` in milliseconds;
- `astra.persistence.cleanup.published_outbox_deleted`;
- `astra.persistence.cleanup.deliveries_deleted`;
- `astra.persistence.cleanup.idempotency_deleted`;
- `astra.persistence.cleanup.failures`.

Metric tags are limited to event type and outcome. Event IDs and aggregate IDs remain trace/log fields and are not metric labels.
