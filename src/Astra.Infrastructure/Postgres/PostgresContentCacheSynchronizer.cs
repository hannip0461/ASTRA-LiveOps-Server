using Astra.Contracts;
using Astra.Domain;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Astra.Infrastructure.Postgres;

public sealed class PostgresContentCacheOptions
{
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed class PostgresContentCacheSynchronizer(
    NpgsqlDataSource dataSource,
    IContentSnapshotStore store,
    IActiveContentCache cache,
    IOptions<PostgresContentCacheOptions> options,
    ILogger<PostgresContentCacheSynchronizer> logger) : BackgroundService
{
    private readonly PostgresContentCacheOptions _options = options.Value;
    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        await _ready.Task.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAndReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Content cache listener disconnected; retrying.");
                await Task.Delay(_options.ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task ListenAndReconcileAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        var notificationPending = 0;
        connection.Notification += (_, args) =>
        {
            if (StringComparer.Ordinal.Equals(args.Channel, PostgresContentSnapshotStore.ContentChangedChannel))
            {
                Interlocked.Exchange(ref notificationPending, 1);
            }
        };

        await connection.ExecuteAsync(new CommandDefinition(
            $"LISTEN {PostgresContentSnapshotStore.ContentChangedChannel};",
            cancellationToken: cancellationToken));
        await ReconcileAsync(cancellationToken);
        _ready.TrySetResult(true);

        while (!cancellationToken.IsCancellationRequested)
        {
            var received = await connection.WaitAsync(_options.ReconciliationInterval, cancellationToken);
            var contentChanged = Interlocked.Exchange(ref notificationPending, 0) == 1;
            if (!received || contentChanged)
            {
                await ReconcileAsync(cancellationToken);
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var snapshot = await store.GetActiveAsync(cancellationToken);
        var current = cache.GetActiveSnapshot();
        if (SameSnapshot(current, snapshot))
        {
            return;
        }

        cache.Update(snapshot);
        logger.LogInformation(
            "Active content cache changed from {PreviousVersion} to {ActiveVersion}.",
            current?.Version,
            snapshot?.Version);
    }

    private static bool SameSnapshot(ContentSnapshotDto? left, ContentSnapshotDto? right) =>
        left is null && right is null ||
        left is not null &&
        right is not null &&
        StringComparer.Ordinal.Equals(left.Version, right.Version) &&
        StringComparer.Ordinal.Equals(left.Checksum, right.Checksum);
}
