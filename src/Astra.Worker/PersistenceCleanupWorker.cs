using System.Diagnostics.Metrics;
using Astra.Domain;

namespace Astra.Worker;

public sealed class PersistenceCleanupWorker(
    IPersistenceMaintenanceStore maintenanceStore,
    PersistenceRetentionOptions options,
    TimeProvider timeProvider,
    ILogger<PersistenceCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Persistence retention started. published={PublishedRetention} orphanDelivery={OrphanRetention} idempotencyGrace={IdempotencyGrace}",
            options.PublishedOutboxRetention,
            options.OrphanDeliveryRetention,
            options.ExpiredIdempotencyGrace);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupBatchesAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                PersistenceCleanupMetrics.Failures.Add(1);
                logger.LogError(exception, "Persistence retention cycle failed.");
            }

            await Task.Delay(options.CleanupInterval, timeProvider, stoppingToken);
        }
    }

    private async Task CleanupBatchesAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var publishedBefore = now - options.PublishedOutboxRetention;
        var orphanDeliveryBefore = now - options.OrphanDeliveryRetention;
        var expiredIdempotencyBefore = now - options.ExpiredIdempotencyGrace;
        var deletedEvents = 0;
        var deletedDeliveries = 0;
        var deletedIdempotency = 0;

        for (var batch = 0; batch < options.MaxBatchesPerCycle; batch++)
        {
            var result = await maintenanceStore.CleanupAsync(
                publishedBefore,
                orphanDeliveryBefore,
                expiredIdempotencyBefore,
                options.BatchSize,
                options.CommandTimeout,
                cancellationToken);
            deletedEvents += result.PublishedEventsDeleted;
            deletedDeliveries += result.DeliveriesDeleted;
            deletedIdempotency += result.IdempotencyRequestsDeleted;
            if (result.PublishedEventsDeleted < options.BatchSize &&
                result.DeliveriesDeleted < options.BatchSize &&
                result.IdempotencyRequestsDeleted < options.BatchSize)
            {
                break;
            }
        }

        if (deletedEvents == 0 && deletedDeliveries == 0 && deletedIdempotency == 0)
        {
            return;
        }

        PersistenceCleanupMetrics.PublishedEventsDeleted.Add(deletedEvents);
        PersistenceCleanupMetrics.DeliveriesDeleted.Add(deletedDeliveries);
        PersistenceCleanupMetrics.IdempotencyRequestsDeleted.Add(deletedIdempotency);
        logger.LogInformation(
            "Persistence retention removed {PublishedEvents} outbox events, {Deliveries} deliveries, and {IdempotencyRequests} idempotency requests.",
            deletedEvents,
            deletedDeliveries,
            deletedIdempotency);
    }
}

public sealed class PersistenceRetentionOptions
{
    public TimeSpan PublishedOutboxRetention { get; init; } = TimeSpan.FromDays(7);

    public TimeSpan OrphanDeliveryRetention { get; init; } = TimeSpan.FromDays(30);

    public TimeSpan ExpiredIdempotencyGrace { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; init; } = 500;

    public int MaxBatchesPerCycle { get; init; } = 20;

    public void Validate()
    {
        if (PublishedOutboxRetention < TimeSpan.FromHours(1) ||
            PublishedOutboxRetention > TimeSpan.FromDays(365) ||
            OrphanDeliveryRetention < TimeSpan.FromHours(1) ||
            OrphanDeliveryRetention > TimeSpan.FromDays(365) ||
            ExpiredIdempotencyGrace < TimeSpan.Zero ||
            ExpiredIdempotencyGrace > TimeSpan.FromDays(7))
        {
            throw new InvalidOperationException("Persistence retention values are outside the supported range.");
        }

        if (CleanupInterval < TimeSpan.FromMinutes(1) ||
            CleanupInterval > TimeSpan.FromDays(1) ||
            CommandTimeout < TimeSpan.FromSeconds(1) ||
            CommandTimeout > TimeSpan.FromSeconds(30) ||
            BatchSize is < 1 or > 10_000 ||
            MaxBatchesPerCycle is < 1 or > 100)
        {
            throw new InvalidOperationException("Persistence cleanup cadence or query limits are outside the supported range.");
        }
    }
}

internal static class PersistenceCleanupMetrics
{
    public static readonly Counter<long> PublishedEventsDeleted = AstraTelemetry.Meter.CreateCounter<long>(
        "astra.persistence.cleanup.published_outbox_deleted");
    public static readonly Counter<long> DeliveriesDeleted = AstraTelemetry.Meter.CreateCounter<long>(
        "astra.persistence.cleanup.deliveries_deleted");
    public static readonly Counter<long> IdempotencyRequestsDeleted = AstraTelemetry.Meter.CreateCounter<long>(
        "astra.persistence.cleanup.idempotency_deleted");
    public static readonly Counter<long> Failures = AstraTelemetry.Meter.CreateCounter<long>(
        "astra.persistence.cleanup.failures");
}
