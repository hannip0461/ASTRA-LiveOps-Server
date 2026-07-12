using Astra.Contracts;
using Astra.Domain;
using Dapper;
using Npgsql;

namespace Astra.Infrastructure.Postgres;

public sealed class PostgresOperationAuditStore(NpgsqlDataSource dataSource) : IOperationAuditStore
{
    public async Task StartAsync(OperationAuditStart entry, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO admin_operation_audit(
                audit_id,
                correlation_id,
                actor_id,
                actor_display_name,
                actor_role,
                action,
                target_type,
                target_id,
                reason,
                request_summary,
                status,
                source_ip,
                started_at)
            VALUES (
                @AuditId,
                @CorrelationId,
                @ActorId,
                @ActorDisplayName,
                @ActorRole,
                @Action,
                @TargetType,
                @TargetId,
                @Reason,
                CAST(@RequestSummary AS jsonb),
                'started',
                @SourceIp,
                @StartedAtUtc);
            """;

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            entry,
            cancellationToken: cancellationToken));
    }

    public async Task CompleteAsync(
        OperationAuditCompletion completion,
        CancellationToken cancellationToken = default)
    {
        if (completion.Status == OperationAuditStatus.Started)
        {
            throw new ArgumentOutOfRangeException(nameof(completion), "Completion status cannot be Started.");
        }

        const string sql = """
            UPDATE admin_operation_audit
            SET status = @Status,
                result_summary = CASE
                    WHEN @ResultSummary IS NULL THEN NULL
                    ELSE CAST(@ResultSummary AS jsonb)
                END,
                error_code = @ErrorCode,
                completed_at = @CompletedAtUtc
            WHERE audit_id = @AuditId
              AND status = 'started';
            """;

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                completion.AuditId,
                Status = ToDatabaseStatus(completion.Status),
                completion.ResultSummary,
                completion.ErrorCode,
                completion.CompletedAtUtc
            },
            cancellationToken: cancellationToken));
        if (affected != 1)
        {
            throw new InvalidOperationException($"Started audit entry was not found: {completion.AuditId}.");
        }
    }

    public async Task<IReadOnlyList<OperationAuditDto>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT audit_id AS "AuditId",
                   correlation_id AS "CorrelationId",
                   actor_id AS "ActorId",
                   actor_display_name AS "ActorDisplayName",
                   actor_role AS "ActorRole",
                   action AS "Action",
                   target_type AS "TargetType",
                   target_id AS "TargetId",
                   reason AS "Reason",
                   request_summary::text AS "RequestSummary",
                   status AS "Status",
                   result_summary::text AS "ResultSummary",
                   error_code AS "ErrorCode",
                   source_ip AS "SourceIp",
                   started_at AS "StartedAtUtc",
                   completed_at AS "CompletedAtUtc"
            FROM admin_operation_audit
            ORDER BY started_at DESC, audit_id DESC
            LIMIT @Limit;
            """;

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            sql,
            new { Limit = Math.Clamp(limit, 1, 200) },
            cancellationToken: cancellationToken));
        return rows.Select(ToDto).ToArray();
    }

    private static OperationAuditDto ToDto(AuditRow row) =>
        new(
            row.AuditId,
            row.CorrelationId,
            row.ActorId,
            row.ActorDisplayName,
            row.ActorRole,
            row.Action,
            row.TargetType,
            row.TargetId,
            row.Reason,
            row.RequestSummary,
            Enum.Parse<OperationAuditStatus>(row.Status, ignoreCase: true),
            row.ResultSummary,
            row.ErrorCode,
            row.SourceIp,
            row.StartedAtUtc,
            row.CompletedAtUtc);

    private static string ToDatabaseStatus(OperationAuditStatus status) =>
        status.ToString().ToLowerInvariant();

    private sealed class AuditRow
    {
        public Guid AuditId { get; init; }

        public string CorrelationId { get; init; } = "";

        public string ActorId { get; init; } = "";

        public string ActorDisplayName { get; init; } = "";

        public string ActorRole { get; init; } = "";

        public string Action { get; init; } = "";

        public string TargetType { get; init; } = "";

        public string TargetId { get; init; } = "";

        public string Reason { get; init; } = "";

        public string RequestSummary { get; init; } = "{}";

        public string Status { get; init; } = "";

        public string? ResultSummary { get; init; }

        public string? ErrorCode { get; init; }

        public string? SourceIp { get; init; }

        public DateTimeOffset StartedAtUtc { get; init; }

        public DateTimeOffset? CompletedAtUtc { get; init; }
    }
}
