namespace Astra.Domain;

public sealed record PersistenceCleanupResult(
    int PublishedEventsDeleted,
    int DeliveriesDeleted,
    int IdempotencyRequestsDeleted);

public interface IPersistenceMaintenanceStore
{
    Task<PersistenceCleanupResult> CleanupAsync(
        DateTimeOffset publishedBefore,
        DateTimeOffset orphanDeliveryBefore,
        DateTimeOffset expiredIdempotencyBefore,
        int batchSize,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken = default);
}
