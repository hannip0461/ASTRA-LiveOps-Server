using Astra.Contracts;

namespace Astra.Domain;

public sealed class GachaCommandFactory(TimeProvider? timeProvider = null)
{
    public const int MaxDrawCount = 10;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public DrawGachaCommand Create(ContentSnapshotDto snapshot, DrawGachaRequest request)
    {
        var banner = snapshot.GachaBanners.FirstOrDefault(x => x.BannerId == request.BannerId)
            ?? throw new ContentMismatchException($"Gacha banner is not active: {request.BannerId}.");

        var now = _timeProvider.GetUtcNow();
        if (now < banner.StartsAtUtc || now >= banner.EndsAtUtc)
        {
            throw new ContentMismatchException($"Gacha banner is outside the active window: {request.BannerId}.");
        }

        if (request.DrawCount is <= 0 or > MaxDrawCount)
        {
            throw new InvalidAccountCommandException($"Draw count must be between 1 and {MaxDrawCount}.");
        }

        return new DrawGachaCommand(
            request.BannerId,
            snapshot.Version,
            snapshot.Checksum,
            banner.CostCurrency,
            checked(banner.CostAmount * request.DrawCount),
            request.DrawCount,
            banner.RewardPool,
            banner.PityThreshold,
            request.IdempotencyKey,
            request.RequestHash);
    }
}
