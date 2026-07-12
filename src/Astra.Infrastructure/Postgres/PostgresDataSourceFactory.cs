using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Astra.Infrastructure.Postgres;

public sealed class PostgresPoolOptions
{
    public int MinimumPoolSize { get; init; }

    public int MaximumPoolSize { get; init; }

    public TimeSpan ConnectionTimeout { get; init; }

    public TimeSpan CommandTimeout { get; init; }

    public TimeSpan ConnectionIdleLifetime { get; init; }

    public TimeSpan ConnectionPruningInterval { get; init; }

    public TimeSpan ConnectionLifetime { get; init; }

    public void Validate()
    {
        if (MinimumPoolSize < 0 || MinimumPoolSize > MaximumPoolSize)
        {
            throw new InvalidOperationException(
                "Astra:Postgres:MinimumPoolSize must be between zero and MaximumPoolSize.");
        }

        if (MaximumPoolSize is < 1 or > 200)
        {
            throw new InvalidOperationException(
                "Astra:Postgres:MaximumPoolSize must be between 1 and 200.");
        }

        ValidateWholeSeconds(ConnectionTimeout, 1, 30, nameof(ConnectionTimeout));
        ValidateWholeSeconds(CommandTimeout, 1, 120, nameof(CommandTimeout));
        ValidateWholeSeconds(ConnectionIdleLifetime, 10, 3_600, nameof(ConnectionIdleLifetime));
        ValidateWholeSeconds(ConnectionPruningInterval, 1, 60, nameof(ConnectionPruningInterval));
        ValidateWholeSeconds(ConnectionLifetime, 60, 86_400, nameof(ConnectionLifetime));

        if (ConnectionPruningInterval > ConnectionIdleLifetime)
        {
            throw new InvalidOperationException(
                "Astra:Postgres:ConnectionPruningInterval must not exceed ConnectionIdleLifetime.");
        }
    }

    private static void ValidateWholeSeconds(
        TimeSpan value,
        int minimumSeconds,
        int maximumSeconds,
        string optionName)
    {
        if (value.TotalSeconds < minimumSeconds ||
            value.TotalSeconds > maximumSeconds ||
            value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new InvalidOperationException(
                $"Astra:Postgres:{optionName} must be a whole-second value between " +
                $"{minimumSeconds} and {maximumSeconds} seconds.");
        }
    }
}

public static class PostgresDataSourceFactory
{
    public static NpgsqlDataSource Create(
        IConfiguration configuration,
        string connectionString,
        string serviceName,
        int defaultMaximumPoolSize)
    {
        var section = configuration.GetSection("Astra:Postgres");
        var options = new PostgresPoolOptions
        {
            MinimumPoolSize = section.GetValue("MinimumPoolSize", 0),
            MaximumPoolSize = section.GetValue("MaximumPoolSize", defaultMaximumPoolSize),
            ConnectionTimeout = section.GetValue("ConnectionTimeout", TimeSpan.FromSeconds(3)),
            CommandTimeout = section.GetValue("CommandTimeout", TimeSpan.FromSeconds(15)),
            ConnectionIdleLifetime = section.GetValue("ConnectionIdleLifetime", TimeSpan.FromMinutes(1)),
            ConnectionPruningInterval = section.GetValue("ConnectionPruningInterval", TimeSpan.FromSeconds(10)),
            ConnectionLifetime = section.GetValue("ConnectionLifetime", TimeSpan.FromMinutes(30))
        };

        return Create(connectionString, serviceName, options);
    }

    public static NpgsqlDataSource Create(
        string connectionString,
        string serviceName,
        PostgresPoolOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var builder = new NpgsqlDataSourceBuilder(connectionString)
        {
            Name = serviceName
        };
        var settings = builder.ConnectionStringBuilder;
        settings.ApplicationName = serviceName;
        settings.Pooling = true;
        settings.MinPoolSize = options.MinimumPoolSize;
        settings.MaxPoolSize = options.MaximumPoolSize;
        settings.Timeout = checked((int)options.ConnectionTimeout.TotalSeconds);
        settings.CommandTimeout = checked((int)options.CommandTimeout.TotalSeconds);
        settings.ConnectionIdleLifetime = checked((int)options.ConnectionIdleLifetime.TotalSeconds);
        settings.ConnectionPruningInterval = checked((int)options.ConnectionPruningInterval.TotalSeconds);
        settings.ConnectionLifetime = checked((int)options.ConnectionLifetime.TotalSeconds);

        return builder.Build();
    }
}
