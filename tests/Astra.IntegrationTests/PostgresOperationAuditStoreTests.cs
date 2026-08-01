using Astra.Contracts;
using Astra.Domain;
using Astra.Infrastructure.Postgres;
using Npgsql;

namespace Astra.IntegrationTests;

public sealed class PostgresOperationAuditStoreTests
{
    [RequiresEnvironmentFact("ASTRA_RUN_POSTGRES_TESTS")]
    public async Task StartAndComplete_PersistAuthenticatedOperationLifecycle()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString());
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();
        var store = new PostgresOperationAuditStore(dataSource);
        var auditId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        await store.StartAsync(new OperationAuditStart(
            auditId,
            $"trace-{auditId:N}",
            "operator-a",
            "Operator A",
            LiveOpsRoles.Operator,
            "content.publish",
            "content-version",
            $"content-{auditId:N}",
            "integration-test",
            "{\"bannerCount\":1}",
            "127.0.0.1",
            startedAt));

        var started = Assert.Single(
            await store.ListRecentAsync(200),
            entry => entry.AuditId == auditId);
        Assert.Equal(OperationAuditStatus.Started, started.Status);

        await store.CompleteAsync(new OperationAuditCompletion(
            auditId,
            OperationAuditStatus.Succeeded,
            "{\"version\":\"content-a\"}",
            null,
            startedAt.AddSeconds(1)));

        var completed = Assert.Single(
            await store.ListRecentAsync(200),
            entry => entry.AuditId == auditId);
        Assert.Equal(OperationAuditStatus.Succeeded, completed.Status);
        Assert.Equal("operator-a", completed.ActorId);
        Assert.Equal(LiveOpsRoles.Operator, completed.ActorRole);
        Assert.NotNull(completed.CompletedAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(
            new OperationAuditCompletion(
                auditId,
                OperationAuditStatus.Failed,
                null,
                "late-update",
                startedAt.AddSeconds(2))));
    }

    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
        ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";
}
