using Astra.Domain;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Npgsql;

namespace Astra.Worker;

public sealed class Worker(
    IOutboxEventStore outboxStore,
    IOutboxEventHandler handler,
    OutboxWorkerOptions options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ASTRA outbox worker started with worker id {WorkerId}.", options.WorkerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                OutboxMetrics.CycleFailures.Add(1);
                logger.LogError(exception, "Outbox worker cycle failed; leased events will recover after lease expiry.");
                await Task.Delay(options.PollInterval, stoppingToken);
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var events = await outboxStore.LeaseBatchAsync(
            options.WorkerId,
            options.BatchSize,
            options.LeaseDuration,
            cancellationToken);
        OutboxMetrics.Leased.Add(events.Count);

        if (events.Count == 0)
        {
            await Task.Delay(options.PollInterval, cancellationToken);
            return;
        }

        await Parallel.ForEachAsync(
            events,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.MaxConcurrency
            },
            (outboxEvent, token) => new ValueTask(ProcessAsync(outboxEvent, token)));
    }

    private async Task ProcessAsync(OutboxEventRecord outboxEvent, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var activity = AstraTelemetry.ActivitySource.StartActivity("outbox.process");
        activity?.SetTag("outbox.event_id", outboxEvent.EventId);
        activity?.SetTag("outbox.event_type", outboxEvent.EventType);
        activity?.SetTag("outbox.aggregate_id", outboxEvent.AggregateId);
        activity?.SetTag("outbox.attempts", outboxEvent.Attempts);

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["outbox.event_id"] = outboxEvent.EventId,
            ["outbox.event_type"] = outboxEvent.EventType,
            ["outbox.aggregate_id"] = outboxEvent.AggregateId
        });

        try
        {
            await handler.HandleAsync(outboxEvent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorCode = OutboxFailureClassifier.GetCode(ex);
            activity?.SetStatus(ActivityStatusCode.Error, errorCode);
            var delay = GetRetryDelay(outboxEvent.Attempts);
            var isTerminal = outboxEvent.Attempts + 1 >= outboxEvent.MaxAttempts;
            logger.LogWarning(
                ex,
                "Outbox event {EventId} failed. code={ErrorCode} attempt={Attempt} terminal={Terminal} nextDelay={DelaySeconds}s",
                outboxEvent.EventId,
                errorCode,
                outboxEvent.Attempts + 1,
                isTerminal,
                delay.TotalSeconds);

            await outboxStore.MarkFailedAsync(
                outboxEvent.EventId,
                options.WorkerId,
                errorCode,
                delay,
                cancellationToken);
            var tags = new TagList
            {
                { "event.type", outboxEvent.EventType },
                { "outcome", isTerminal ? "dead_letter" : "retry" }
            };
            (isTerminal ? OutboxMetrics.DeadLettered : OutboxMetrics.RetryScheduled).Add(1, tags);
            OutboxMetrics.ProcessingDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                tags);
            return;
        }

        await outboxStore.MarkPublishedAsync(outboxEvent.EventId, options.WorkerId, cancellationToken);
        activity?.SetStatus(ActivityStatusCode.Ok);
        var successTags = new TagList
        {
            { "event.type", outboxEvent.EventType },
            { "outcome", "published" }
        };
        OutboxMetrics.Published.Add(1, successTags);
        OutboxMetrics.ProcessingDuration.Record(
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            successTags);
    }

    private static TimeSpan GetRetryDelay(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Max(0, attempts))));
}

public sealed class OutboxWorkerOptions
{
    public string WorkerId { get; init; } = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    public int BatchSize { get; init; } = 20;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxConcurrency { get; init; } = 4;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkerId) || WorkerId.Length > 200)
        {
            throw new InvalidOperationException("Outbox WorkerId must contain between 1 and 200 characters.");
        }

        if (BatchSize is < 1 or > 500 || MaxConcurrency is < 1 or > 64)
        {
            throw new InvalidOperationException("Outbox BatchSize or MaxConcurrency is outside the supported range.");
        }

        if (PollInterval < TimeSpan.FromMilliseconds(10) ||
            PollInterval > TimeSpan.FromMinutes(1) ||
            LeaseDuration < TimeSpan.FromSeconds(1) ||
            LeaseDuration > TimeSpan.FromMinutes(10))
        {
            throw new InvalidOperationException("Outbox polling or lease duration is outside the supported range.");
        }
    }
}

public interface IOutboxEventHandler
{
    Task HandleAsync(OutboxEventRecord outboxEvent, CancellationToken cancellationToken = default);
}

internal static class OutboxFailureClassifier
{
    public static string GetCode(Exception exception) => exception switch
    {
        UnsupportedOutboxEventException => "outbox_event_unsupported",
        InvalidOutboxPayloadException => "outbox_payload_invalid",
        NpgsqlException => "outbox_consumer_store_unavailable",
        _ => "outbox_consumer_failed"
    };
}

internal static class OutboxMetrics
{
    public static readonly Counter<long> Leased = AstraTelemetry.Meter.CreateCounter<long>("astra.outbox.leased");
    public static readonly Counter<long> Published = AstraTelemetry.Meter.CreateCounter<long>("astra.outbox.published");
    public static readonly Counter<long> RetryScheduled = AstraTelemetry.Meter.CreateCounter<long>("astra.outbox.retry_scheduled");
    public static readonly Counter<long> DeadLettered = AstraTelemetry.Meter.CreateCounter<long>("astra.outbox.dead_lettered");
    public static readonly Counter<long> CycleFailures = AstraTelemetry.Meter.CreateCounter<long>("astra.outbox.cycle_failures");
    public static readonly Histogram<double> ProcessingDuration = AstraTelemetry.Meter.CreateHistogram<double>(
        "astra.outbox.processing.duration",
        "ms");
}
