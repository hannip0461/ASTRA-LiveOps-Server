using System.Diagnostics;
using System.Diagnostics.Metrics;
using Astra.Domain;
using Npgsql;

namespace Astra.Infrastructure.Postgres;

public static class PostgresConnectionExtensions
{
    private static readonly Counter<long> AcquireAttempts = AstraTelemetry.Meter.CreateCounter<long>(
        "astra.postgres.connection.acquire.attempts",
        "{attempt}",
        "PostgreSQL connection acquisition attempts.");

    private static readonly Histogram<double> AcquireDuration = AstraTelemetry.Meter.CreateHistogram<double>(
        "astra.postgres.connection.acquire.duration",
        "s",
        "Time spent acquiring a PostgreSQL connection, including pool wait time.");

    private static readonly Counter<long> AcquireFailures = AstraTelemetry.Meter.CreateCounter<long>(
        "astra.postgres.connection.acquire.failures",
        "{failure}",
        "PostgreSQL connection acquisition failures.");

    public static async ValueTask<NpgsqlConnection> OpenConnectionObservedAsync(
        this NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        AcquireAttempts.Add(1);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            return await dataSource.OpenConnectionAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AcquireFailures.Add(1, new KeyValuePair<string, object?>("reason", Classify(exception)));
            throw;
        }
        finally
        {
            AcquireDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
        }
    }

    private static string Classify(Exception exception) => exception switch
    {
        TimeoutException => "timeout",
        NpgsqlException { InnerException: TimeoutException } => "timeout",
        PostgresException => "postgres",
        NpgsqlException => "transport",
        _ => "unknown"
    };
}
