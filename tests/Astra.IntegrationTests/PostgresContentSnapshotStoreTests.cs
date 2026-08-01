using Astra.Contracts;
using Astra.Domain;
using Astra.Infrastructure;
using Astra.Infrastructure.Postgres;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;

namespace Astra.IntegrationTests;

public sealed class PostgresContentSnapshotStoreTests
{
    [RequiresEnvironmentFact("ASTRA_RUN_POSTGRES_TESTS")]
    public async Task SchemaInitializer_SerializesConcurrentMigrationAttempts()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString());
        await Task.WhenAll(
            Enumerable.Range(0, 4)
                .Select(_ => new PostgresSchemaInitializer(dataSource).ApplyAsync()));

        await using var connection = await dataSource.OpenConnectionAsync();
        Assert.True(await connection.QuerySingleAsync<bool>(
            "SELECT to_regclass('public.content_snapshots') IS NOT NULL;"));
    }

    [RequiresEnvironmentFact("ASTRA_RUN_POSTGRES_TESTS")]
    public async Task PublishAndActivate_PersistImmutableSnapshots()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString());
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();
        await ClearContentAsync(dataSource);
        var store = new PostgresContentSnapshotStore(dataSource);
        var first = Snapshot("content-a", "checksum-a");
        var second = Snapshot("content-b", "checksum-b");

        await store.PublishAsync(first);
        var firstGeneration = await ReadGenerationAsync(dataSource);
        AssertSnapshotEqual(first, await store.PublishAsync(first));
        Assert.Equal(firstGeneration, await ReadGenerationAsync(dataSource));
        await store.PublishAsync(second);

        var restartedStore = new PostgresContentSnapshotStore(dataSource);
        AssertSnapshotEqual(second, await restartedStore.GetActiveAsync());
        await Assert.ThrowsAsync<ContentVersionInactiveException>(() => restartedStore.PublishAsync(first));
        AssertSnapshotEqual(second, await restartedStore.GetActiveAsync());
        AssertSnapshotEqual(first, await restartedStore.ActivateAsync(first.Version));
        var rollbackGeneration = await ReadGenerationAsync(dataSource);
        AssertSnapshotEqual(first, await restartedStore.GetActiveAsync());
        AssertSnapshotEqual(first, await restartedStore.ActivateAsync(first.Version));
        Assert.Equal(rollbackGeneration, await ReadGenerationAsync(dataSource));
        await Assert.ThrowsAsync<ContentVersionConflictException>(
            () => restartedStore.PublishAsync(first with { Checksum = "different" }));
    }

    [RequiresEnvironmentFact("ASTRA_RUN_POSTGRES_TESTS")]
    public async Task ConcurrentPublish_WithSameVersionAndDifferentChecksums_CommitsOneWinner()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString());
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();
        await ClearContentAsync(dataSource);
        var firstStore = new PostgresContentSnapshotStore(dataSource);
        var secondStore = new PostgresContentSnapshotStore(dataSource);
        var first = Snapshot("content-race", "checksum-a");
        var second = Snapshot("content-race", "checksum-b");

        var results = await Task.WhenAll(
            Record.ExceptionAsync(async () => await firstStore.PublishAsync(first)),
            Record.ExceptionAsync(async () => await secondStore.PublishAsync(second)));

        Assert.Single(results, exception => exception is null);
        Assert.Single(results, exception => exception is ContentVersionConflictException);
        var active = await firstStore.GetActiveAsync();
        Assert.NotNull(active);
        Assert.Contains(active.Checksum, new[] { first.Checksum, second.Checksum });
        Assert.Equal(1, await ReadGenerationAsync(dataSource));
        Assert.Equal(1, await CountSnapshotsAsync(dataSource));
    }

    [RequiresEnvironmentFact("ASTRA_RUN_POSTGRES_TESTS")]
    public async Task ConcurrentPublishAndRollback_SerializeActivePointerChanges()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString());
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();
        await ClearContentAsync(dataSource);
        var publishStore = new PostgresContentSnapshotStore(dataSource);
        var rollbackStore = new PostgresContentSnapshotStore(dataSource);
        var first = Snapshot("content-a", "checksum-a");
        var second = Snapshot("content-b", "checksum-b");
        var third = Snapshot("content-c", "checksum-c");
        await publishStore.PublishAsync(first);
        await publishStore.PublishAsync(second);

        await Task.WhenAll(
            new Task[]
            {
                publishStore.PublishAsync(third),
                rollbackStore.ActivateAsync(first.Version)
            });

        var active = await publishStore.GetActiveAsync();
        Assert.NotNull(active);
        Assert.Contains(active.Version, new[] { first.Version, third.Version });
        Assert.Equal(4, await ReadGenerationAsync(dataSource));
        Assert.Equal(3, await CountSnapshotsAsync(dataSource));
    }

    [RequiresEnvironmentFact("ASTRA_RUN_POSTGRES_TESTS")]
    public async Task Synchronizer_UpdatesIndependentSiloCaches_AndReconcilesMissedNotification()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString());
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();
        await ClearContentAsync(dataSource);
        var store = new PostgresContentSnapshotStore(dataSource);
        var first = Snapshot("content-a", "checksum-a");
        var second = Snapshot("content-b", "checksum-b");
        await store.PublishAsync(first);

        var firstCache = new InMemoryActiveContentCache();
        var secondCache = new InMemoryActiveContentCache();
        var options = Options.Create(new PostgresContentCacheOptions
        {
            ReconciliationInterval = TimeSpan.FromMilliseconds(100),
            ReconnectDelay = TimeSpan.FromMilliseconds(50)
        });
        var firstSynchronizer = CreateSynchronizer(dataSource, store, firstCache, options);
        var secondSynchronizer = CreateSynchronizer(dataSource, store, secondCache, options);

        await firstSynchronizer.StartAsync(CancellationToken.None);
        await secondSynchronizer.StartAsync(CancellationToken.None);
        try
        {
            await WaitForVersionAsync(firstCache, first.Version);
            await WaitForVersionAsync(secondCache, first.Version);

            await store.PublishAsync(second);
            await WaitForVersionAsync(firstCache, second.Version);
            await WaitForVersionAsync(secondCache, second.Version);

            await SetActiveWithoutNotificationAsync(dataSource, first.Version);
            await WaitForVersionAsync(firstCache, first.Version);
            await WaitForVersionAsync(secondCache, first.Version);
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await firstSynchronizer.StopAsync(stopTimeout.Token);
            await secondSynchronizer.StopAsync(stopTimeout.Token);
        }
    }

    private static PostgresContentCacheSynchronizer CreateSynchronizer(
        NpgsqlDataSource dataSource,
        IContentSnapshotStore store,
        IActiveContentCache cache,
        IOptions<PostgresContentCacheOptions> options) =>
        new(dataSource, store, cache, options, NullLogger<PostgresContentCacheSynchronizer>.Instance);

    private static async Task WaitForVersionAsync(IActiveContentCache cache, string version)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!StringComparer.Ordinal.Equals(cache.GetActiveSnapshot()?.Version, version))
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task ClearContentAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("TRUNCATE active_content, content_snapshots;");
    }

    private static async Task SetActiveWithoutNotificationAsync(NpgsqlDataSource dataSource, string version)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            UPDATE active_content
            SET version = @Version,
                generation = generation + 1,
                activated_at = now()
            WHERE singleton_id = 1;
            """,
            new { Version = version });
    }

    private static async Task<long> ReadGenerationAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<long>(
            "SELECT generation FROM active_content WHERE singleton_id = 1;");
    }

    private static async Task<int> CountSnapshotsAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>("SELECT count(*) FROM content_snapshots;");
    }

    private static void AssertSnapshotEqual(ContentSnapshotDto expected, ContentSnapshotDto? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    private static ContentSnapshotDto Snapshot(string version, string checksum)
    {
        var now = DateTimeOffset.UtcNow;
        return new ContentSnapshotDto(
            version,
            checksum,
            now,
            [new GachaBannerConfigDto(
                "pickup-a",
                CurrencyCode.Elif,
                100,
                90,
                now.AddMinutes(-1),
                now.AddHours(1),
                [new GachaRewardPoolEntryDto(
                    GachaRewardKind.Character,
                    "character-a",
                    1,
                    3,
                    100,
                    true,
                    "memory-character-a",
                    20)])]);
    }

    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
        ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";
}
