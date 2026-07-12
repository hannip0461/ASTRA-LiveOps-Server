using Astra.Domain;
using Astra.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.UnitTests;

public sealed class PersistenceCleanupWorkerTests
{
    [Fact]
    public async Task Worker_RunsCleanupImmediatelyWithConfiguredCutoffs()
    {
        var store = new CapturingMaintenanceStore();
        var now = DateTimeOffset.UtcNow;
        var worker = new PersistenceCleanupWorker(
            store,
            new PersistenceRetentionOptions
            {
                PublishedOutboxRetention = TimeSpan.FromDays(7),
                OrphanDeliveryRetention = TimeSpan.FromDays(30),
                ExpiredIdempotencyGrace = TimeSpan.FromHours(1),
                CleanupInterval = TimeSpan.FromMinutes(1),
                CommandTimeout = TimeSpan.FromSeconds(3),
                BatchSize = 250,
                MaxBatchesPerCycle = 1
            },
            TimeProvider.System,
            NullLogger<PersistenceCleanupWorker>.Instance);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(timeout.Token);
        await store.Called.WaitAsync(timeout.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(250, store.BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(3), store.CommandTimeout);
        Assert.InRange(store.PublishedBefore, now.AddDays(-7).AddSeconds(-2), now.AddDays(-7).AddSeconds(2));
        Assert.InRange(store.OrphanDeliveryBefore, now.AddDays(-30).AddSeconds(-2), now.AddDays(-30).AddSeconds(2));
        Assert.InRange(store.ExpiredIdempotencyBefore, now.AddHours(-1).AddSeconds(-2), now.AddHours(-1).AddSeconds(2));
    }

    [Fact]
    public void Options_RejectUnsafeQueryTimeout()
    {
        var options = new PersistenceRetentionOptions { CommandTimeout = TimeSpan.FromMinutes(1) };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private sealed class CapturingMaintenanceStore : IPersistenceMaintenanceStore
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Called => _called.Task;

        public DateTimeOffset PublishedBefore { get; private set; }

        public DateTimeOffset OrphanDeliveryBefore { get; private set; }

        public DateTimeOffset ExpiredIdempotencyBefore { get; private set; }

        public int BatchSize { get; private set; }

        public TimeSpan CommandTimeout { get; private set; }

        public Task<PersistenceCleanupResult> CleanupAsync(
            DateTimeOffset publishedBefore,
            DateTimeOffset orphanDeliveryBefore,
            DateTimeOffset expiredIdempotencyBefore,
            int batchSize,
            TimeSpan commandTimeout,
            CancellationToken cancellationToken = default)
        {
            PublishedBefore = publishedBefore;
            OrphanDeliveryBefore = orphanDeliveryBefore;
            ExpiredIdempotencyBefore = expiredIdempotencyBefore;
            BatchSize = batchSize;
            CommandTimeout = commandTimeout;
            _called.TrySetResult();
            return Task.FromResult(new PersistenceCleanupResult(0, 0, 0));
        }
    }
}
