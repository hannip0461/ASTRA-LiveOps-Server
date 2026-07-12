using Astra.Contracts;

namespace Astra.Domain;

public sealed record PendingOutboxEvent(
    Guid EventId,
    string EventType,
    Guid AggregateId,
    string IdempotencyKey,
    string Payload,
    DateTimeOffset CreatedAt);

public sealed record OutboxEventRecord(
    Guid EventId,
    string EventType,
    Guid AggregateId,
    string IdempotencyKey,
    string Payload,
    int Attempts,
    int MaxAttempts);

public sealed record WalletCurrencyOutboxPayload(
    int SchemaVersion,
    CurrencyCode Currency,
    long Amount,
    long BalanceAfter,
    long LedgerVersion);

public sealed record GachaDrawCompletedOutboxPayload(
    int SchemaVersion,
    string BannerId,
    string ContentVersion,
    string ContentChecksum,
    int DrawCount,
    int RewardCount,
    int PityAfter,
    long LedgerVersion);

public sealed record MailClaimedOutboxPayload(
    int SchemaVersion,
    string IncidentId,
    string MailId,
    int RewardCount,
    long LedgerVersion);

public interface IOutboxEventStore
{
    Task<IReadOnlyList<OutboxEventRecord>> LeaseBatchAsync(
        string workerId,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task MarkPublishedAsync(
        Guid eventId,
        string workerId,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        Guid eventId,
        string workerId,
        string error,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default);
}

public sealed class OutboxLeaseLostException(Guid eventId) : InvalidOperationException(
    $"The lease for outbox event '{eventId}' is no longer owned by this worker.");
