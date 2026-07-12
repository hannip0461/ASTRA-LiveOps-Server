using System.Diagnostics;
using Astra.Infrastructure.Postgres;
using Npgsql;

namespace Astra.ObservabilityProbe;

internal sealed record PoolSaturationResult(
    int PoolSize,
    int ExpectedAttempts,
    int ExpectedTimeouts,
    long TimeoutMilliseconds,
    int RecoveryProbe);

internal sealed record OutboxDeliveryResult(
    Guid PublishedEventId,
    string PublishedStatus,
    Guid DeadLetterEventId,
    string DeadLetterStatus,
    int Attempts,
    string ErrorCode);

internal static class OperationalScenarios
{
    public static async Task<PoolSaturationResult> SaturatePoolAsync(string connectionString)
    {
        const int poolSize = 2;
        await using var dataSource = PostgresDataSourceFactory.Create(
            connectionString,
            $"Astra.OperationalProbe.{Guid.NewGuid():N}",
            new PostgresPoolOptions
            {
                MinimumPoolSize = 0,
                MaximumPoolSize = poolSize,
                ConnectionTimeout = TimeSpan.FromSeconds(1),
                CommandTimeout = TimeSpan.FromSeconds(5),
                ConnectionIdleLifetime = TimeSpan.FromSeconds(30),
                ConnectionPruningInterval = TimeSpan.FromSeconds(5),
                ConnectionLifetime = TimeSpan.FromMinutes(5)
            });

        var timeoutMilliseconds = 0L;
        await using (var first = await dataSource.OpenConnectionObservedAsync())
        await using (var second = await dataSource.OpenConnectionObservedAsync())
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await using var unexpected = await dataSource.OpenConnectionObservedAsync();
                throw new InvalidOperationException("The saturated PostgreSQL pool unexpectedly accepted another connection.");
            }
            catch (NpgsqlException exception) when (ContainsTimeout(exception))
            {
                stopwatch.Stop();
                timeoutMilliseconds = stopwatch.ElapsedMilliseconds;
            }
        }

        await using var recovered = await dataSource.OpenConnectionObservedAsync();
        await using var command = recovered.CreateCommand();
        command.CommandText = "SELECT 1;";
        var recoveryProbe = Convert.ToInt32(await command.ExecuteScalarAsync());
        return new PoolSaturationResult(poolSize, 4, 1, timeoutMilliseconds, recoveryProbe);
    }

    public static async Task<OutboxDeliveryResult> ExerciseOutboxAsync(
        string connectionString,
        TimeSpan timeout)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await DeletePreviousProbeEventsAsync(dataSource);

        var publishedEventId = Guid.NewGuid();
        var deadLetterEventId = Guid.NewGuid();
        await InsertProbeEventAsync(
            dataSource,
            publishedEventId,
            """{"schemaVersion":1,"currency":1,"amount":100,"balanceAfter":100,"ledgerVersion":1}""");
        await InsertProbeEventAsync(
            dataSource,
            deadLetterEventId,
            """{"schemaVersion":999}""");

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        do
        {
            var publishedState = await ReadOutboxStateAsync(dataSource, publishedEventId);
            var deadLetterState = await ReadOutboxStateAsync(dataSource, deadLetterEventId);
            if (publishedState is { Status: "published" } &&
                deadLetterState is { Status: "dead_letter" })
            {
                return new OutboxDeliveryResult(
                    publishedEventId,
                    publishedState.Status,
                    deadLetterEventId,
                    deadLetterState.Status,
                    deadLetterState.Attempts,
                    deadLetterState.ErrorCode ?? "outbox_consumer_failed");
            }

            await Task.Delay(250);
        } while (DateTimeOffset.UtcNow < deadline);

        throw new TimeoutException(
            $"Outbox events did not reach published/dead_letter within {timeout}. " +
            $"published={publishedEventId}, deadLetter={deadLetterEventId}");
    }

    private static async Task DeletePreviousProbeEventsAsync(NpgsqlDataSource dataSource)
    {
        await using (var deliveries = dataSource.CreateCommand("""
            DELETE FROM operational_event_deliveries
            WHERE event_id IN (
                SELECT event_id
                FROM outbox_events
                WHERE idempotency_key LIKE 'operational-probe-%');
            """))
        {
            await deliveries.ExecuteNonQueryAsync();
        }

        await using var command = dataSource.CreateCommand("""
            DELETE FROM outbox_events
            WHERE idempotency_key LIKE 'operational-probe-%'
              AND status IN ('published', 'dead_letter');
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertProbeEventAsync(
        NpgsqlDataSource dataSource,
        Guid eventId,
        string payload)
    {
        await using var insert = dataSource.CreateCommand("""
            INSERT INTO outbox_events(
                event_id, event_type, aggregate_type, aggregate_id, idempotency_key,
                payload, status, attempts, max_attempts, available_at)
            VALUES (
                $1, 'wallet.currency_granted', 'player', $2, $3,
                $4::jsonb, 'pending', 0, 2, now());
            """);
        insert.Parameters.AddWithValue(eventId);
        insert.Parameters.AddWithValue(Guid.NewGuid());
        insert.Parameters.AddWithValue($"operational-probe-{eventId:N}");
        insert.Parameters.AddWithValue(payload);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<OutboxState?> ReadOutboxStateAsync(
        NpgsqlDataSource dataSource,
        Guid eventId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT status, attempts, last_error
            FROM outbox_events
            WHERE event_id = $1;
            """);
        command.Parameters.AddWithValue(eventId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new OutboxState(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static bool ContainsTimeout(Exception exception) =>
        exception is TimeoutException ||
        exception.InnerException is not null && ContainsTimeout(exception.InnerException);

    private sealed record OutboxState(string Status, int Attempts, string? ErrorCode);
}
