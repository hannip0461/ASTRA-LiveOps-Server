using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Astra.Domain;
using Astra.Infrastructure.Postgres;
using Npgsql;

namespace Astra.IntegrationTests;

[Collection(PostgresPoolTelemetryCollection.Name)]
public sealed class PostgresPoolSaturationTests
{
    [RequiresEnvironmentFact("ASTRA_RUN_POSTGRES_TESTS")]
    public async Task ExhaustedPool_FailsWithinTimeout_EmitsMetric_AndRecovers()
    {
        var failureReasons = new ConcurrentBag<string>();
        long attemptCount = 0;
        using var listener = ListenForAcquireMetrics(
            failureReasons,
            value => Interlocked.Add(ref attemptCount, value));
        await using var dataSource = CreateDataSource(maximumPoolSize: 2, connectionTimeoutSeconds: 1);
        var stopwatch = new Stopwatch();
        await using (var first = await dataSource.OpenConnectionObservedAsync())
        await using (var second = await dataSource.OpenConnectionObservedAsync())
        {
            stopwatch.Start();
            await Assert.ThrowsAsync<NpgsqlException>(async () =>
            {
                await using var connection = await dataSource.OpenConnectionObservedAsync();
            });
            stopwatch.Stop();
        }

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(3));
        Assert.Contains("timeout", failureReasons);

        await using var recovered = await dataSource.OpenConnectionObservedAsync();
        await using var command = recovered.CreateCommand();
        command.CommandText = "SELECT 1;";
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
        Assert.Equal(4, Volatile.Read(ref attemptCount));
    }

    [RequiresEnvironmentFact("ASTRA_RUN_POSTGRES_TESTS")]
    public async Task BoundedPool_QueuesConcurrentBurst_WithoutExceedingLimit()
    {
        const int maximumPoolSize = 4;
        var applicationName = $"Astra.PoolBurst.{Guid.NewGuid():N}";
        await using var dataSource = CreateDataSource(
            maximumPoolSize,
            connectionTimeoutSeconds: 5,
            applicationName);
        await using var probe = NpgsqlDataSource.Create(ConnectionString());

        var operations = Enumerable.Range(0, 24)
            .Select(_ => ExecuteBriefQueryAsync(dataSource))
            .ToArray();
        var burst = Task.WhenAll(operations);
        var observedMaximum = await ObserveConnectionCountAsync(
            probe,
            applicationName,
            burst);
        await burst;

        Assert.InRange(observedMaximum, 2, maximumPoolSize);
    }

    private static NpgsqlDataSource CreateDataSource(
        int maximumPoolSize,
        int connectionTimeoutSeconds,
        string? applicationName = null) =>
        PostgresDataSourceFactory.Create(
            ConnectionString(),
            applicationName ?? $"Astra.PoolSaturation.{Guid.NewGuid():N}",
            new PostgresPoolOptions
            {
                MinimumPoolSize = 0,
                MaximumPoolSize = maximumPoolSize,
                ConnectionTimeout = TimeSpan.FromSeconds(connectionTimeoutSeconds),
                CommandTimeout = TimeSpan.FromSeconds(5),
                ConnectionIdleLifetime = TimeSpan.FromSeconds(30),
                ConnectionPruningInterval = TimeSpan.FromSeconds(5),
                ConnectionLifetime = TimeSpan.FromMinutes(5)
            });

    private static async Task ExecuteBriefQueryAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionObservedAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_sleep(0.1);";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ObserveConnectionCountAsync(
        NpgsqlDataSource probe,
        string applicationName,
        Task operations)
    {
        var maximum = 0;
        while (!operations.IsCompleted)
        {
            await using var command = probe.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = $1;");
            command.Parameters.AddWithValue(applicationName);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            maximum = Math.Max(maximum, count);
            await Task.Delay(10);
        }

        return maximum;
    }

    private static MeterListener ListenForAcquireMetrics(
        ConcurrentBag<string> failureReasons,
        Action<long> onAttempt)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AstraTelemetry.MeterName &&
                instrument.Name is
                    "astra.postgres.connection.acquire.attempts" or
                    "astra.postgres.connection.acquire.failures")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "astra.postgres.connection.acquire.attempts")
            {
                onAttempt(measurement);
                return;
            }

            foreach (var tag in tags)
            {
                if (tag.Key == "reason" && tag.Value is string reason)
                {
                    failureReasons.Add(reason);
                }
            }
        });
        listener.Start();
        return listener;
    }

    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
        ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";
}

[CollectionDefinition(PostgresPoolTelemetryCollection.Name, DisableParallelization = true)]
public sealed class PostgresPoolTelemetryCollection
{
    public const string Name = "PostgreSQL pool telemetry";
}
