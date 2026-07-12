using System.Collections.Concurrent;
using Astra.Contracts;
using Astra.Domain;

namespace Astra.Infrastructure;

public sealed class InMemoryOperationAuditStore : IOperationAuditStore
{
    private readonly ConcurrentDictionary<Guid, OperationAuditDto> _entries = new();

    public Task StartAsync(OperationAuditStart entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var audit = new OperationAuditDto(
            entry.AuditId,
            entry.CorrelationId,
            entry.ActorId,
            entry.ActorDisplayName,
            entry.ActorRole,
            entry.Action,
            entry.TargetType,
            entry.TargetId,
            entry.Reason,
            entry.RequestSummary,
            OperationAuditStatus.Started,
            null,
            null,
            entry.SourceIp,
            entry.StartedAtUtc,
            null);
        if (!_entries.TryAdd(entry.AuditId, audit))
        {
            throw new InvalidOperationException($"Audit entry already exists: {entry.AuditId}.");
        }

        return Task.CompletedTask;
    }

    public Task CompleteAsync(
        OperationAuditCompletion completion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (completion.Status == OperationAuditStatus.Started)
        {
            throw new ArgumentOutOfRangeException(nameof(completion), "Completion status cannot be Started.");
        }

        while (_entries.TryGetValue(completion.AuditId, out var existing))
        {
            if (existing.Status != OperationAuditStatus.Started)
            {
                throw new InvalidOperationException($"Audit entry is already complete: {completion.AuditId}.");
            }

            var updated = existing with
            {
                Status = completion.Status,
                ResultSummary = completion.ResultSummary,
                ErrorCode = completion.ErrorCode,
                CompletedAtUtc = completion.CompletedAtUtc
            };
            if (_entries.TryUpdate(completion.AuditId, updated, existing))
            {
                return Task.CompletedTask;
            }
        }

        throw new InvalidOperationException($"Audit entry was not found: {completion.AuditId}.");
    }

    public Task<IReadOnlyList<OperationAuditDto>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = _entries.Values
            .OrderByDescending(entry => entry.StartedAtUtc)
            .ThenByDescending(entry => entry.AuditId)
            .Take(Math.Clamp(limit, 1, 200))
            .ToArray();
        return Task.FromResult<IReadOnlyList<OperationAuditDto>>(entries);
    }
}
