using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Astra.Api;
using Astra.Contracts;
using Astra.Domain;
using Astra.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.UnitTests;

public sealed class OperationAuditTests
{
    [Fact]
    public async Task ExecuteAsync_PersistsIntentBeforeOperation_ThenCompletes()
    {
        var store = new InMemoryOperationAuditStore();
        var executor = Executor(store);
        var context = Context();

        var result = await executor.ExecuteAsync(
            context,
            Command(),
            async () =>
            {
                var started = Assert.Single(await store.ListRecentAsync(10));
                Assert.Equal(OperationAuditStatus.Started, started.Status);
                return new AdminAuditOutcome<string>(
                    "done",
                    OperationAuditStatus.Succeeded,
                    new { version = "content-a" });
            });

        Assert.Equal("done", result);
        var completed = Assert.Single(await store.ListRecentAsync(10));
        Assert.Equal(OperationAuditStatus.Succeeded, completed.Status);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Contains("content-a", completed.ResultSummary);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOperationFails_MarksAuditFailedAndRethrows()
    {
        var store = new InMemoryOperationAuditStore();
        var executor = Executor(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync<string>(
            Context(),
            Command(),
            () => throw new InvalidOperationException("domain failure")));

        var failed = Assert.Single(await store.ListRecentAsync(10));
        Assert.Equal(OperationAuditStatus.Failed, failed.Status);
        Assert.Equal("internal_error", failed.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithCustomIdentityClaimTypes_PersistsConfiguredActor()
    {
        var store = new InMemoryOperationAuditStore();
        var context = Context("preferred_username", "roles");

        await Executor(store).ExecuteAsync(
            context,
            Command(),
            () => Task.FromResult(new AdminAuditOutcome<string>(
                "done",
                OperationAuditStatus.Succeeded)));

        var audit = Assert.Single(await store.ListRecentAsync(10));
        Assert.Equal("operator-a", audit.ActorId);
        Assert.Equal("Operator A", audit.ActorDisplayName);
        Assert.Equal(LiveOpsRoles.Operator, audit.ActorRole);
    }

    [Fact]
    public async Task CompleteAsync_CanTransitionOnlyOnce()
    {
        var store = new InMemoryOperationAuditStore();
        var auditId = Guid.NewGuid();
        await store.StartAsync(new OperationAuditStart(
            auditId,
            "trace-a",
            "operator-a",
            "Operator A",
            LiveOpsRoles.Operator,
            "content.publish",
            "content-version",
            "content-a",
            "test",
            "{}",
            "127.0.0.1",
            DateTimeOffset.UtcNow));
        var completion = new OperationAuditCompletion(
            auditId,
            OperationAuditStatus.Succeeded,
            "{}",
            null,
            DateTimeOffset.UtcNow);

        await store.CompleteAsync(completion);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(completion));
    }

    private static AdminAuditExecutor Executor(InMemoryOperationAuditStore store) =>
        new(store, TimeProvider.System, NullLogger<AdminAuditExecutor>.Instance);

    private static AdminAuditCommand Command() =>
        new("content.publish", "content-version", "content-a", "test", new { version = "content-a" });

    private static DefaultHttpContext Context(
        string nameClaimType = JwtRegisteredClaimNames.Name,
        string roleClaimType = "role")
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, "operator-a"),
            new Claim(nameClaimType, "Operator A"),
            new Claim(roleClaimType, LiveOpsRoles.Operator)
        ], "test", nameClaimType, roleClaimType));
        context.TraceIdentifier = ActivityTraceId.CreateRandom().ToString();
        return context;
    }
}
