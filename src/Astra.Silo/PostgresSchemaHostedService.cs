using Astra.Infrastructure.Postgres;

namespace Astra.Silo;

public sealed class PostgresSchemaHostedService(
    PostgresSchemaInitializer initializer,
    ILogger<PostgresSchemaHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await initializer.ApplyAsync(cancellationToken);
        logger.LogInformation("PostgreSQL schema is ready.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
