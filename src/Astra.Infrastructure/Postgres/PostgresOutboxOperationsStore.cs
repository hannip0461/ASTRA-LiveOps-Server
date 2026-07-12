using Astra.Contracts;
using Astra.Domain;
using Dapper;
using Npgsql;

namespace Astra.Infrastructure.Postgres;

public sealed class PostgresOutboxOperationsStore(NpgsqlDataSource dataSource) : IOutboxOperationsStore
{
    public async Task<OutboxOverviewDto> GetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT COUNT(*) FILTER (WHERE status = 'pending') AS "PendingCount",
                   COUNT(*) FILTER (WHERE status = 'processing') AS "ProcessingCount",
                   COUNT(*) FILTER (WHERE status = 'published') AS "PublishedCount",
                   COUNT(*) FILTER (WHERE status = 'dead_letter') AS "DeadLetterCount",
                   (SELECT COUNT(*) FROM operational_event_deliveries) AS "DeliveryCount",
                   MIN(created_at) FILTER (WHERE status = 'pending') AS "OldestPendingAtUtc"
            FROM outbox_events;
            """;

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<OverviewRow>(new CommandDefinition(
            sql,
            cancellationToken: cancellationToken));
        return new OutboxOverviewDto(
            row.PendingCount,
            row.ProcessingCount,
            row.PublishedCount,
            row.DeadLetterCount,
            row.DeliveryCount,
            row.OldestPendingAtUtc);
    }

    public async Task<IReadOnlyList<OutboxDeadLetterDto>> ListDeadLettersAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT event_id AS "EventId",
                   event_type AS "EventType",
                   aggregate_id AS "AggregateId",
                   attempts AS "Attempts",
                   max_attempts AS "MaxAttempts",
                   COALESCE(last_error, 'outbox_consumer_failed') AS "ErrorCode",
                   manual_replay_count AS "ManualReplayCount",
                   created_at AS "CreatedAtUtc",
                   dead_lettered_at AS "DeadLetteredAtUtc"
            FROM outbox_events
            WHERE status = 'dead_letter'
            ORDER BY dead_lettered_at DESC NULLS LAST, event_id DESC
            LIMIT @Limit;
            """;

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        var rows = await connection.QueryAsync<DeadLetterRow>(new CommandDefinition(
            sql,
            new { Limit = Math.Clamp(limit, 1, 200) },
            cancellationToken: cancellationToken));
        return rows.Select(row => new OutboxDeadLetterDto(
            row.EventId,
            row.EventType,
            row.AggregateId,
            row.Attempts,
            row.MaxAttempts,
            row.ErrorCode,
            row.ManualReplayCount,
            row.CreatedAtUtc,
            row.DeadLetteredAtUtc)).ToArray();
    }

    public async Task<OutboxReplayResultDto?> ReplayDeadLetterAsync(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE outbox_events
            SET status = 'pending',
                attempts = 0,
                available_at = now(),
                leased_by = NULL,
                lease_until = NULL,
                last_error = NULL,
                dead_lettered_at = NULL,
                manual_replay_count = manual_replay_count + 1
            WHERE event_id = @EventId
              AND status = 'dead_letter'
            RETURNING event_id AS "EventId",
                      status AS "Status",
                      manual_replay_count AS "ManualReplayCount",
                      available_at AS "AvailableAtUtc";
            """;

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ReplayRow>(new CommandDefinition(
            sql,
            new { EventId = eventId },
            cancellationToken: cancellationToken));
        return row is null
            ? null
            : new OutboxReplayResultDto(
                row.EventId,
                row.Status,
                row.ManualReplayCount,
                row.AvailableAtUtc);
    }

    private sealed class OverviewRow
    {
        public long PendingCount { get; init; }
        public long ProcessingCount { get; init; }
        public long PublishedCount { get; init; }
        public long DeadLetterCount { get; init; }
        public long DeliveryCount { get; init; }
        public DateTimeOffset? OldestPendingAtUtc { get; init; }
    }

    private sealed class DeadLetterRow
    {
        public Guid EventId { get; init; }
        public string EventType { get; init; } = "";
        public Guid AggregateId { get; init; }
        public int Attempts { get; init; }
        public int MaxAttempts { get; init; }
        public string ErrorCode { get; init; } = "";
        public int ManualReplayCount { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset? DeadLetteredAtUtc { get; init; }
    }

    private sealed class ReplayRow
    {
        public Guid EventId { get; init; }
        public string Status { get; init; } = "";
        public int ManualReplayCount { get; init; }
        public DateTimeOffset AvailableAtUtc { get; init; }
    }
}
