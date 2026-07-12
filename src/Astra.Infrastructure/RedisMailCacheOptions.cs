namespace Astra.Infrastructure;

public sealed class RedisMailCacheOptions
{
    public TimeSpan TargetTtl { get; init; } = TimeSpan.FromDays(7);
}
