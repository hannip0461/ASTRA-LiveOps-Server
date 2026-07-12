using Astra.Domain;
using Dapper;
using Npgsql;

namespace Astra.Infrastructure.Postgres;

public sealed class PostgresPersistenceMaintenanceStore(NpgsqlDataSource dataSource)
    : IPersistenceMaintenanceStore
{
    public async Task<PersistenceCleanupResult> CleanupAsync(
        DateTimeOffset publishedBefore,
        DateTimeOffset orphanDeliveryBefore,
        DateTimeOffset expiredIdempotencyBefore,
        int batchSize,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH published_candidates AS MATERIALIZED (
                SELECT event_id
                FROM outbox_events
                WHERE status = 'published'
                  AND published_at < @PublishedBefore
                ORDER BY published_at, event_id
                LIMIT @BatchSize
                FOR UPDATE SKIP LOCKED
            ),
            deleted_deliveries AS (
                DELETE FROM operational_event_deliveries AS delivery
                USING published_candidates AS candidate
                WHERE delivery.event_id = candidate.event_id
                RETURNING delivery.event_id
            ),
            deleted_events AS (
                DELETE FROM outbox_events AS outbox
                USING published_candidates AS candidate
                WHERE outbox.event_id = candidate.event_id
                  AND outbox.status = 'published'
                RETURNING outbox.event_id
            ),
            orphan_candidates AS MATERIALIZED (
                SELECT delivery.consumer_name, delivery.event_id
                FROM operational_event_deliveries AS delivery
                WHERE delivery.consumed_at < @OrphanDeliveryBefore
                  AND NOT EXISTS (
                      SELECT 1
                      FROM outbox_events AS outbox
                      WHERE outbox.event_id = delivery.event_id)
                ORDER BY delivery.consumed_at, delivery.event_id
                LIMIT @BatchSize
                FOR UPDATE SKIP LOCKED
            ),
            deleted_orphans AS (
                DELETE FROM operational_event_deliveries AS delivery
                USING orphan_candidates AS candidate
                WHERE delivery.consumer_name = candidate.consumer_name
                  AND delivery.event_id = candidate.event_id
                RETURNING delivery.event_id
            ),
            idempotency_candidates AS MATERIALIZED (
                SELECT player_id, idempotency_key
                FROM idempotency_requests
                WHERE expires_at < @ExpiredIdempotencyBefore
                ORDER BY expires_at, player_id, idempotency_key
                LIMIT @BatchSize
                FOR UPDATE SKIP LOCKED
            ),
            deleted_idempotency AS (
                DELETE FROM idempotency_requests AS request
                USING idempotency_candidates AS candidate
                WHERE request.player_id = candidate.player_id
                  AND request.idempotency_key = candidate.idempotency_key
                RETURNING request.player_id
            )
            SELECT (SELECT COUNT(*) FROM deleted_events) AS "PublishedEventsDeleted",
                   (SELECT COUNT(*) FROM deleted_deliveries) +
                   (SELECT COUNT(*) FROM deleted_orphans) AS "DeliveriesDeleted",
                   (SELECT COUNT(*) FROM deleted_idempotency) AS "IdempotencyRequestsDeleted";
            """;

        var boundedBatchSize = Math.Clamp(batchSize, 1, 10_000);
        var timeoutSeconds = Math.Clamp((int)Math.Ceiling(commandTimeout.TotalSeconds), 1, 30);
        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<CleanupRow>(new CommandDefinition(
            sql,
            new
            {
                PublishedBefore = publishedBefore,
                OrphanDeliveryBefore = orphanDeliveryBefore,
                ExpiredIdempotencyBefore = expiredIdempotencyBefore,
                BatchSize = boundedBatchSize
            },
            commandTimeout: timeoutSeconds,
            cancellationToken: cancellationToken));
        return new PersistenceCleanupResult(
            row.PublishedEventsDeleted,
            row.DeliveriesDeleted,
            row.IdempotencyRequestsDeleted);
    }

    private sealed class CleanupRow
    {
        public int PublishedEventsDeleted { get; init; }

        public int DeliveriesDeleted { get; init; }

        public int IdempotencyRequestsDeleted { get; init; }
    }
}
