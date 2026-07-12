using Orleans;

namespace Astra.Contracts;

[GenerateSerializer]
public sealed record GachaBannerConfigDto(
    [property: Id(0)] string BannerId,
    [property: Id(1)] CurrencyCode CostCurrency,
    [property: Id(2)] long CostAmount,
    [property: Id(3)] int PityThreshold,
    [property: Id(4)] DateTimeOffset StartsAtUtc,
    [property: Id(5)] DateTimeOffset EndsAtUtc,
    [property: Id(6)] IReadOnlyList<GachaRewardPoolEntryDto> RewardPool);

[GenerateSerializer]
public sealed record ContentSnapshotDto(
    [property: Id(0)] string Version,
    [property: Id(1)] string Checksum,
    [property: Id(2)] DateTimeOffset PublishedAtUtc,
    [property: Id(3)] IReadOnlyList<GachaBannerConfigDto> GachaBanners);

[GenerateSerializer]
public sealed record PublishContentCommand(
    [property: Id(0)] string Version,
    [property: Id(1)] IReadOnlyList<GachaBannerConfigDto> GachaBanners,
    [property: Id(2)] string Reason = "");

public sealed record RollbackContentCommand(string Reason);

[GenerateSerializer]
public sealed record ContentValidationIssue(
    [property: Id(0)] string Code,
    [property: Id(1)] string Message);

[GenerateSerializer]
public sealed record ContentPublishResult(
    [property: Id(0)] bool Published,
    [property: Id(1)] ContentSnapshotDto? Snapshot,
    [property: Id(2)] IReadOnlyList<ContentValidationIssue> Issues);

public interface IEventConfigGrain : IGrainWithStringKey
{
    Task<ContentSnapshotDto?> GetActiveSnapshotAsync();

    Task<ContentPublishResult> PublishAsync(PublishContentCommand command);

    Task<ContentPublishResult> RollbackAsync(string version);
}
