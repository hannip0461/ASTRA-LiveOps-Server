using Astra.Contracts;
using Astra.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Astra.Infrastructure;

public sealed class RedisCachedMailStore(
    IMailStore inner,
    IConnectionMultiplexer redis,
    IOptions<RedisMailCacheOptions> options,
    ILogger<RedisCachedMailStore>? logger = null) : IMailStore
{
    private readonly TimeSpan _targetTtl = options.Value.TargetTtl;

    public async Task<MailDefinitionDto> CreateIncidentMailAsync(
        CreateIncidentMailCommand command,
        CancellationToken cancellationToken = default)
    {
        var definition = await inner.CreateIncidentMailAsync(command, cancellationToken);
        try
        {
            await CacheTargetSnapshotAsync(command.MailId, command.TargetPlayerIds);
        }
        catch (RedisException exception)
        {
            logger?.LogWarning(exception, "Redis mail target cache write failed. MailId={MailId}", command.MailId);
        }

        return definition;
    }

    public Task<MailDefinitionDto?> GetDefinitionAsync(
        string mailId,
        CancellationToken cancellationToken = default) =>
        inner.GetDefinitionAsync(mailId, cancellationToken);

    public async Task<bool> IsTargetAsync(
        string mailId,
        Guid playerId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var db = redis.GetDatabase();
            if (await db.KeyExistsAsync(ReadyKey(mailId)))
            {
                return await db.SetContainsAsync(TargetKey(mailId), playerId.ToString("N"));
            }
        }
        catch (RedisException exception)
        {
            logger?.LogWarning(exception, "Redis mail target cache read failed. MailId={MailId}", mailId);
        }

        return await inner.IsTargetAsync(mailId, playerId, cancellationToken);
    }

    private async Task CacheTargetSnapshotAsync(string mailId, IEnumerable<Guid> playerIds)
    {
        var db = redis.GetDatabase();
        var targetKey = TargetKey(mailId);
        var readyKey = ReadyKey(mailId);
        var values = playerIds.Distinct().Select(id => (RedisValue)id.ToString("N")).ToArray();

        await db.KeyDeleteAsync(targetKey);
        if (values.Length > 0)
        {
            await db.SetAddAsync(targetKey, values);
        }

        await db.KeyExpireAsync(targetKey, _targetTtl);
        await db.StringSetAsync(readyKey, "1", _targetTtl);
    }

    private static RedisKey TargetKey(string mailId) => $"astra:mail:{mailId}:targets";

    private static RedisKey ReadyKey(string mailId) => $"astra:mail:{mailId}:targets:ready";
}
