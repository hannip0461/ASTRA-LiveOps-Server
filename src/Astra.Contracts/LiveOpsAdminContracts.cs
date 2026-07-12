namespace Astra.Contracts;

public static class LiveOpsRoles
{
    public const string Viewer = "LiveOpsViewer";
    public const string Operator = "LiveOpsOperator";
    public const string Supervisor = "LiveOpsSupervisor";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Viewer,
        Operator,
        Supervisor
    };
}

public static class DevAuthenticationHeaders
{
    public const string TokenKey = "X-Astra-Dev-Token-Key";
}

public sealed record DevOperatorTokenRequest(string OperatorId);

public sealed record DevOperatorTokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    string OperatorId,
    string DisplayName,
    string Role);

public enum OperationAuditStatus
{
    Started = 1,
    Succeeded = 2,
    Rejected = 3,
    Failed = 4
}

public sealed record OperationAuditDto(
    Guid AuditId,
    string CorrelationId,
    string ActorId,
    string ActorDisplayName,
    string ActorRole,
    string Action,
    string TargetType,
    string TargetId,
    string Reason,
    string RequestSummary,
    OperationAuditStatus Status,
    string? ResultSummary,
    string? ErrorCode,
    string? SourceIp,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record OutboxOverviewDto(
    long PendingCount,
    long ProcessingCount,
    long PublishedCount,
    long DeadLetterCount,
    long DeliveryCount,
    DateTimeOffset? OldestPendingAtUtc);

public sealed record OutboxDeadLetterDto(
    Guid EventId,
    string EventType,
    Guid AggregateId,
    int Attempts,
    int MaxAttempts,
    string ErrorCode,
    int ManualReplayCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeadLetteredAtUtc);

public sealed record ReplayOutboxEventCommand(string Reason);

public sealed record OutboxReplayResultDto(
    Guid EventId,
    string Status,
    int ManualReplayCount,
    DateTimeOffset AvailableAtUtc);
