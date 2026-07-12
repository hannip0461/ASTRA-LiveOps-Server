using Astra.Domain;
using Astra.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.UnitTests;

public sealed class OutboxWorkerTests
{
    [Fact]
    public async Task Worker_WhenHandlerSucceeds_MarksEventPublished()
    {
        var outboxEvent = CreateEvent();
        var store = new FakeOutboxStore(outboxEvent);
        var handler = new CapturingOutboxHandler();
        var worker = CreateWorker(store, handler);

        await RunUntilAsync(worker, store.Published);

        Assert.Equal(outboxEvent.EventId, handler.Handled.Single().EventId);
        Assert.Equal(outboxEvent.EventId, store.PublishedEventId);
    }

    [Fact]
    public async Task Worker_WhenHandlerFails_MarksEventFailedForRetry()
    {
        var outboxEvent = CreateEvent();
        var store = new FakeOutboxStore(outboxEvent);
        var handler = new CapturingOutboxHandler(throwOnHandle: true);
        var worker = CreateWorker(store, handler);

        await RunUntilAsync(worker, store.Failed);

        Assert.Equal(outboxEvent.EventId, store.FailedEventId);
        Assert.Equal("outbox_consumer_failed", store.LastError);
    }

    private static global::Astra.Worker.Worker CreateWorker(FakeOutboxStore store, IOutboxEventHandler handler) =>
        new(
            store,
            handler,
            new OutboxWorkerOptions
            {
                WorkerId = "unit-test-worker",
                BatchSize = 1,
                PollInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(5)
            },
            NullLogger<global::Astra.Worker.Worker>.Instance);

    private static async Task RunUntilAsync(global::Astra.Worker.Worker worker, Task signal)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        await signal.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);
    }

    private static OutboxEventRecord CreateEvent() =>
        new(
            Guid.NewGuid(),
            "unit.test",
            Guid.NewGuid(),
            "idem-1",
            "{}",
            0,
            5);

    private sealed class FakeOutboxStore(OutboxEventRecord nextEvent) : IOutboxEventStore
    {
        private readonly TaskCompletionSource _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _leased;

        public Task Published => _published.Task;

        public Task Failed => _failed.Task;

        public Guid PublishedEventId { get; private set; }

        public Guid FailedEventId { get; private set; }

        public string LastError { get; private set; } = "";

        public Task<IReadOnlyList<OutboxEventRecord>> LeaseBatchAsync(
            string workerId,
            int batchSize,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            if (_leased)
            {
                return Task.FromResult<IReadOnlyList<OutboxEventRecord>>([]);
            }

            _leased = true;
            return Task.FromResult<IReadOnlyList<OutboxEventRecord>>([nextEvent]);
        }

        public Task MarkPublishedAsync(
            Guid eventId,
            string workerId,
            CancellationToken cancellationToken = default)
        {
            PublishedEventId = eventId;
            _published.TrySetResult();
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(
            Guid eventId,
            string workerId,
            string error,
            TimeSpan retryDelay,
            CancellationToken cancellationToken = default)
        {
            FailedEventId = eventId;
            LastError = error;
            _failed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingOutboxHandler(bool throwOnHandle = false) : IOutboxEventHandler
    {
        public List<OutboxEventRecord> Handled { get; } = [];

        public Task HandleAsync(OutboxEventRecord outboxEvent, CancellationToken cancellationToken = default)
        {
            if (throwOnHandle)
            {
                throw new InvalidOperationException("handler failed");
            }

            Handled.Add(outboxEvent);
            return Task.CompletedTask;
        }
    }
}
