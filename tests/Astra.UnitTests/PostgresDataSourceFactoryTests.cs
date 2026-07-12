using Astra.Infrastructure.Postgres;
using Npgsql;

namespace Astra.UnitTests;

public sealed class PostgresDataSourceFactoryTests
{
    [Fact]
    public async Task Create_OverridesUnboundedConnectionStringSettings()
    {
        var options = ValidOptions(maximumPoolSize: 7);

        await using var dataSource = PostgresDataSourceFactory.Create(
            "Host=localhost;Database=astra;Username=astra;Maximum Pool Size=100;Timeout=15",
            "Astra.Test",
            options);
        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.True(settings.Pooling);
        Assert.Equal(0, settings.MinPoolSize);
        Assert.Equal(7, settings.MaxPoolSize);
        Assert.Equal(2, settings.Timeout);
        Assert.Equal(10, settings.CommandTimeout);
        Assert.Equal(60, settings.ConnectionIdleLifetime);
        Assert.Equal(10, settings.ConnectionPruningInterval);
        Assert.Equal(1_800, settings.ConnectionLifetime);
        Assert.Equal("Astra.Test", settings.ApplicationName);
    }

    [Fact]
    public void Options_RejectPoolMinimumAboveMaximum()
    {
        var options = new PostgresPoolOptions
        {
            MinimumPoolSize = 3,
            MaximumPoolSize = 2,
            ConnectionTimeout = TimeSpan.FromSeconds(2),
            CommandTimeout = TimeSpan.FromSeconds(10),
            ConnectionIdleLifetime = TimeSpan.FromMinutes(1),
            ConnectionPruningInterval = TimeSpan.FromSeconds(10),
            ConnectionLifetime = TimeSpan.FromMinutes(30)
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Options_RejectSubsecondTimeout()
    {
        var options = ValidOptions(maximumPoolSize: 2);
        options = new PostgresPoolOptions
        {
            MinimumPoolSize = options.MinimumPoolSize,
            MaximumPoolSize = options.MaximumPoolSize,
            ConnectionTimeout = TimeSpan.FromMilliseconds(1_500),
            CommandTimeout = options.CommandTimeout,
            ConnectionIdleLifetime = options.ConnectionIdleLifetime,
            ConnectionPruningInterval = options.ConnectionPruningInterval,
            ConnectionLifetime = options.ConnectionLifetime
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static PostgresPoolOptions ValidOptions(int maximumPoolSize) => new()
    {
        MinimumPoolSize = 0,
        MaximumPoolSize = maximumPoolSize,
        ConnectionTimeout = TimeSpan.FromSeconds(2),
        CommandTimeout = TimeSpan.FromSeconds(10),
        ConnectionIdleLifetime = TimeSpan.FromMinutes(1),
        ConnectionPruningInterval = TimeSpan.FromSeconds(10),
        ConnectionLifetime = TimeSpan.FromMinutes(30)
    };
}
