using Astra.Contracts;
using Astra.Infrastructure;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Astra.IntegrationTests;

public sealed class RedisCachedMailStoreTests
{
    [Fact]
    public async Task RedisUnavailable_FallsBackToInnerStore()
    {
        var redisOptions = ConfigurationOptions.Parse("127.0.0.1:1");
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectTimeout = 100;
        redisOptions.AsyncTimeout = 100;
        redisOptions.SyncTimeout = 100;

        using var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions);
        var inner = new InMemoryMailStore();
        var store = new RedisCachedMailStore(
            inner,
            redis,
            Options.Create(new RedisMailCacheOptions { TargetTtl = TimeSpan.FromMinutes(5) }));
        var playerId = Guid.NewGuid();
        var mailId = $"mail-{Guid.NewGuid():N}";

        var definition = await store.CreateIncidentMailAsync(new CreateIncidentMailCommand(
            "incident-redis-down",
            mailId,
            "Compensation",
            "PostgreSQL fallback equivalent",
            [playerId],
            [new MailRewardDto(CurrencyCode.Elif, 100)],
            "redis-unavailable"));

        Assert.Equal(mailId, definition.MailId);
        Assert.True(await store.IsTargetAsync(mailId, playerId));
        Assert.False(await store.IsTargetAsync(mailId, Guid.NewGuid()));
    }

    [Fact]
    public async Task IsTargetAsync_WithRedis_UsesCachedTargetSnapshot()
    {
        if (!ShouldRunRedisTests())
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("ASTRA_REDIS_CONNECTION")
            ?? "localhost:6389";

        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var inner = new InMemoryMailStore();
        var store = new RedisCachedMailStore(
            inner,
            redis,
            Options.Create(new RedisMailCacheOptions { TargetTtl = TimeSpan.FromMinutes(5) }));

        var playerId = Guid.NewGuid();
        var mailId = $"mail-{Guid.NewGuid():N}";

        await store.CreateIncidentMailAsync(new CreateIncidentMailCommand(
            "incident-redis-cache",
            mailId,
            "Compensation",
            "Cached target snapshot",
            [playerId],
            [new MailRewardDto(CurrencyCode.Elif, 100)],
            "redis-target-cache"));

        var db = redis.GetDatabase();

        Assert.True(await db.KeyExistsAsync($"astra:mail:{mailId}:targets:ready"));
        Assert.True(await store.IsTargetAsync(mailId, playerId));
        Assert.False(await store.IsTargetAsync(mailId, Guid.NewGuid()));
    }

    private static bool ShouldRunRedisTests() =>
        string.Equals(Environment.GetEnvironmentVariable("ASTRA_RUN_REDIS_TESTS"), "1", StringComparison.Ordinal);
}
