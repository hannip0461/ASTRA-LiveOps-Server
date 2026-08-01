using Astra.Domain;
using Dapper;
using Npgsql;
using System.Diagnostics;

namespace Astra.Infrastructure.Postgres;

public sealed class PostgresOutboxEventStore(NpgsqlDataSource dataSource) : IOutboxEventStore
{
    public const string LeaseExpiredError = "outbox_lease_expired";

    public async Task<IReadOnlyList<OutboxEventRecord>> LeaseBatchAsync(
        string workerId,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        using var activity = AstraTelemetry.ActivitySource.StartActivity("outbox.lease");
        activity?.SetTag("outbox.worker_id", workerId);
        activity?.SetTag("outbox.batch_size", batchSize);

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);

        // 만료된 lease를 실패 횟수에 포함해 반복 장애가 dead-letter로 수렴하도록 한다.
        var reclaimed = await connection.ExecuteAsync(new CommandDefinition(
            """
            WITH expired AS (
                SELECT event_id
                FROM outbox_events
                WHERE status = 'processing'
                  AND lease_until < now()
                ORDER BY lease_until
                LIMIT @ReclaimLimit
                FOR UPDATE SKIP LOCKED
            )
            UPDATE outbox_events AS outbox
            SET attempts = outbox.attempts + 1,
                status = CASE
                    WHEN outbox.attempts + 1 >= outbox.max_attempts THEN 'dead_letter'
                    ELSE 'pending'
                END,
                available_at = CASE
                    WHEN outbox.attempts + 1 >= outbox.max_attempts THEN outbox.available_at
                    ELSE now()
                END,
                last_error = @LeaseExpiredError,
                dead_lettered_at = CASE
                    WHEN outbox.attempts + 1 >= outbox.max_attempts THEN now()
                    ELSE NULL
                END,
                leased_by = NULL,
                lease_until = NULL
            FROM expired
            WHERE outbox.event_id = expired.event_id;
            """,
            new
            {
                ReclaimLimit = Math.Max(1, batchSize),
                LeaseExpiredError = LeaseExpiredError
            },
            cancellationToken: cancellationToken));
        activity?.SetTag("outbox.reclaimed_count", reclaimed);

        var events = await connection.QueryAsync<OutboxEventRow>(new CommandDefinition(
            """
            WITH next_events AS (
                SELECT event_id
                FROM outbox_events
                WHERE status = 'pending'
                  AND available_at <= now()
                ORDER BY created_at, event_id
                LIMIT @BatchSize
                FOR UPDATE SKIP LOCKED
            )
            UPDATE outbox_events AS outbox
            SET status = 'processing',
                leased_by = @WorkerId,
                lease_until = now() + (@LeaseSeconds * interval '1 second')
            FROM next_events
            WHERE outbox.event_id = next_events.event_id
            RETURNING
                outbox.event_id AS EventId,
                outbox.event_type AS EventType,
                outbox.aggregate_id AS AggregateId,
                outbox.idempotency_key AS IdempotencyKey,
                outbox.payload AS Payload,
                outbox.attempts AS Attempts,
                outbox.max_attempts AS MaxAttempts;
            """,
            new
            {
                WorkerId = workerId,
                BatchSize = Math.Max(1, batchSize),
                LeaseSeconds = Math.Max(1, (int)leaseDuration.TotalSeconds)
            },
            cancellationToken: cancellationToken));

        var leased = events
            .Select(row => new OutboxEventRecord(
                row.EventId,
                row.EventType,
                row.AggregateId,
                row.IdempotencyKey,
                row.Payload,
                row.Attempts,
                row.MaxAttempts))
            .ToArray();

        activity?.SetTag("outbox.leased_count", leased.Length);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return leased;
    }

    public async Task MarkPublishedAsync(
        Guid eventId,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE outbox_events
            SET status = 'published',
                published_at = now(),
                leased_by = NULL,
                lease_until = NULL
            WHERE event_id = @EventId
              AND leased_by = @WorkerId
              AND status = 'processing';
            """,
            new { EventId = eventId, WorkerId = workerId },
            cancellationToken: cancellationToken));
        EnsureLeaseOwned(affected, eventId);
    }

    public async Task MarkFailedAsync(
        Guid eventId,
        string workerId,
        string error,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE outbox_events
            SET attempts = attempts + 1,
                status = CASE
                    WHEN attempts + 1 >= max_attempts THEN 'dead_letter'
                    ELSE 'pending'
                END,
                available_at = CASE
                    WHEN attempts + 1 >= max_attempts THEN available_at
                    ELSE now() + (@RetryDelaySeconds * interval '1 second')
                END,
                last_error = @Error,
                dead_lettered_at = CASE
                    WHEN attempts + 1 >= max_attempts THEN now()
                    ELSE NULL
                END,
                leased_by = NULL,
                lease_until = NULL
            WHERE event_id = @EventId
              AND leased_by = @WorkerId
              AND status = 'processing';
            """,
            new
            {
                EventId = eventId,
                WorkerId = workerId,
                Error = error.Length > 500 ? error[..500] : error,
                RetryDelaySeconds = Math.Max(0, (int)retryDelay.TotalSeconds)
            },
            cancellationToken: cancellationToken));
        EnsureLeaseOwned(affected, eventId);
    }

    private static void EnsureLeaseOwned(int affected, Guid eventId)
    {
        if (affected != 1)
        {
            throw new OutboxLeaseLostException(eventId);
        }
    }

    private sealed class OutboxEventRow
    {
        public Guid EventId { get; init; }

        public string EventType { get; init; } = "";

        public Guid AggregateId { get; init; }

        public string IdempotencyKey { get; init; } = "";

        public string Payload { get; init; } = "";

        public int Attempts { get; init; }

        public int MaxAttempts { get; init; }
    }
}
