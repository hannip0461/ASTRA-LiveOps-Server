using Astra.Contracts;

namespace Astra.Domain;

public sealed record OperationAuditStart(
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
    string? SourceIp,
    DateTimeOffset StartedAtUtc);

public sealed record OperationAuditCompletion(
    Guid AuditId,
    OperationAuditStatus Status,
    string? ResultSummary,
    string? ErrorCode,
    DateTimeOffset CompletedAtUtc);

public interface IOperationAuditStore
{
    Task StartAsync(OperationAuditStart entry, CancellationToken cancellationToken = default);

    Task CompleteAsync(OperationAuditCompletion completion, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationAuditDto>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
