using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Astra.Contracts;

namespace Astra.Domain;

public sealed class ContentValidationService(TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions ChecksumJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ContentPublishResult ValidateAndCreateSnapshot(PublishContentCommand command)
    {
        var issues = Validate(command);
        if (issues.Count > 0)
        {
            return new ContentPublishResult(false, null, issues);
        }

        var snapshot = new ContentSnapshotDto(
            command.Version.Trim(),
            CreateChecksum(command),
            _timeProvider.GetUtcNow(),
            command.GachaBanners
                .OrderBy(x => x.BannerId, StringComparer.Ordinal)
                .Select(x => x with
                {
                    RewardPool = x.RewardPool
                        .OrderBy(entry => entry.Kind)
                        .ThenBy(entry => entry.RewardId, StringComparer.Ordinal)
                        .ToArray()
                })
                .ToArray());

        return new ContentPublishResult(true, snapshot, []);
    }

    private static List<ContentValidationIssue> Validate(PublishContentCommand command)
    {
        var issues = new List<ContentValidationIssue>();
        if (string.IsNullOrWhiteSpace(command.Version))
        {
            issues.Add(new ContentValidationIssue("content.version.required", "Content version is required."));
        }

        var bannerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var banner in command.GachaBanners)
        {
            ValidateBanner(banner, bannerIds, issues);
        }

        return issues;
    }

    private static void ValidateBanner(
        GachaBannerConfigDto banner,
        HashSet<string> bannerIds,
        List<ContentValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(banner.BannerId))
        {
            issues.Add(new ContentValidationIssue("gacha.banner.required", "Gacha banner id is required."));
            return;
        }

        if (!bannerIds.Add(banner.BannerId))
        {
            issues.Add(new ContentValidationIssue("gacha.banner.duplicate", $"Duplicate banner id: {banner.BannerId}."));
        }

        if (!Enum.IsDefined(banner.CostCurrency))
        {
            issues.Add(new ContentValidationIssue("gacha.cost.currency.invalid", $"Invalid cost currency for {banner.BannerId}."));
        }

        if (banner.CostAmount <= 0)
        {
            issues.Add(new ContentValidationIssue("gacha.cost.amount.invalid", $"Cost amount must be positive for {banner.BannerId}."));
        }

        if (banner.PityThreshold <= 0)
        {
            issues.Add(new ContentValidationIssue("gacha.pity.invalid", $"Pity threshold must be positive for {banner.BannerId}."));
        }

        if (banner.EndsAtUtc <= banner.StartsAtUtc)
        {
            issues.Add(new ContentValidationIssue("gacha.period.invalid", $"End time must be after start time for {banner.BannerId}."));
        }

        ValidateRewardPool(banner, issues);
    }

    private static void ValidateRewardPool(
        GachaBannerConfigDto banner,
        List<ContentValidationIssue> issues)
    {
        if (banner.RewardPool.Count == 0)
        {
            issues.Add(new ContentValidationIssue(
                "gacha.pool.required",
                $"Reward pool is required for {banner.BannerId}."));
            return;
        }

        var rewardIds = new HashSet<string>(StringComparer.Ordinal);
        var totalWeight = 0L;
        var hasPityTarget = false;

        foreach (var entry in banner.RewardPool)
        {
            var rewardKey = $"{entry.Kind}:{entry.RewardId}";
            if (!rewardIds.Add(rewardKey))
            {
                issues.Add(new ContentValidationIssue(
                    "gacha.pool.reward.duplicate",
                    $"Duplicate reward in {banner.BannerId}: {rewardKey}."));
            }

            if (!Enum.IsDefined(entry.Kind))
            {
                issues.Add(new ContentValidationIssue(
                    "gacha.pool.kind.invalid",
                    $"Invalid reward kind in {banner.BannerId}: {entry.Kind}."));
            }

            if (string.IsNullOrWhiteSpace(entry.RewardId))
            {
                issues.Add(new ContentValidationIssue(
                    "gacha.pool.reward.required",
                    $"Reward id is required for {banner.BannerId}."));
            }

            if (entry.Quantity <= 0 || entry.Rarity <= 0)
            {
                issues.Add(new ContentValidationIssue(
                    "gacha.pool.reward.invalid",
                    $"Reward quantity and rarity must be positive for {banner.BannerId}:{entry.RewardId}."));
            }

            if (entry.Weight <= 0)
            {
                issues.Add(new ContentValidationIssue(
                    "gacha.pool.weight.invalid",
                    $"Reward weight must be positive for {banner.BannerId}:{entry.RewardId}."));
            }
            else
            {
                totalWeight += entry.Weight;
            }

            hasPityTarget |= entry.IsPityTarget;
            ValidateDuplicateConversion(banner.BannerId, entry, issues);
        }

        if (!hasPityTarget)
        {
            issues.Add(new ContentValidationIssue(
                "gacha.pool.pity_target.required",
                $"At least one pity target is required for {banner.BannerId}."));
        }

        if (totalWeight > int.MaxValue)
        {
            issues.Add(new ContentValidationIssue(
                "gacha.pool.weight.overflow",
                $"Total reward weight exceeds {int.MaxValue} for {banner.BannerId}."));
        }
    }

    private static void ValidateDuplicateConversion(
        string bannerId,
        GachaRewardPoolEntryDto entry,
        List<ContentValidationIssue> issues)
    {
        if (entry.Kind == GachaRewardKind.Character)
        {
            if (entry.Quantity != 1)
            {
                issues.Add(new ContentValidationIssue(
                    "gacha.pool.character.quantity.invalid",
                    $"Character reward quantity must be one for {bannerId}:{entry.RewardId}."));
            }

            if (string.IsNullOrWhiteSpace(entry.DuplicateItemId) || entry.DuplicateItemQuantity <= 0)
            {
                issues.Add(new ContentValidationIssue(
                    "gacha.pool.duplicate_conversion.required",
                    $"Character duplicate conversion is required for {bannerId}:{entry.RewardId}."));
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.DuplicateItemId) || entry.DuplicateItemQuantity != 0)
        {
            issues.Add(new ContentValidationIssue(
                "gacha.pool.duplicate_conversion.invalid",
                $"Only character rewards can define duplicate conversion for {bannerId}:{entry.RewardId}."));
        }
    }

    private static string CreateChecksum(PublishContentCommand command)
    {
        var stable = new
        {
            version = command.Version.Trim(),
            banners = command.GachaBanners
                .OrderBy(x => x.BannerId, StringComparer.Ordinal)
                .Select(x => new
                {
                    x.BannerId,
                    x.CostCurrency,
                    x.CostAmount,
                    x.PityThreshold,
                    // 같은 시각을 나타내는 UTC offset은 hash 전에 정규화한다.
                    startsAtUtc = x.StartsAtUtc.ToUnixTimeMilliseconds(),
                    endsAtUtc = x.EndsAtUtc.ToUnixTimeMilliseconds(),
                    rewardPool = x.RewardPool
                        .OrderBy(entry => entry.Kind)
                        .ThenBy(entry => entry.RewardId, StringComparer.Ordinal)
                        .Select(entry => new
                        {
                            entry.Kind,
                            entry.RewardId,
                            entry.Quantity,
                            entry.Rarity,
                            entry.Weight,
                            entry.IsPityTarget,
                            entry.DuplicateItemId,
                            entry.DuplicateItemQuantity
                        })
                })
        };

        var json = JsonSerializer.Serialize(stable, ChecksumJsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
