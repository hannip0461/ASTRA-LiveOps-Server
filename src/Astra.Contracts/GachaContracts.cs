using Orleans;

namespace Astra.Contracts;

[GenerateSerializer]
public enum GachaRewardKind
{
    Character = 1,
    Item = 2
}

[GenerateSerializer]
public sealed record GachaRewardPoolEntryDto(
    [property: Id(0)] GachaRewardKind Kind,
    [property: Id(1)] string RewardId,
    [property: Id(2)] int Quantity,
    [property: Id(3)] int Rarity,
    [property: Id(4)] int Weight,
    [property: Id(5)] bool IsPityTarget,
    [property: Id(6)] string? DuplicateItemId,
    [property: Id(7)] int DuplicateItemQuantity);

[GenerateSerializer]
public sealed record DrawGachaRequest(
    [property: Id(0)] string BannerId,
    [property: Id(1)] int DrawCount,
    [property: Id(2)] string IdempotencyKey,
    [property: Id(3)] string RequestHash);

[GenerateSerializer]
public sealed record DrawGachaCommand(
    [property: Id(0)] string BannerId,
    [property: Id(1)] string ContentVersion,
    [property: Id(2)] string ContentChecksum,
    [property: Id(3)] CurrencyCode CostCurrency,
    [property: Id(4)] long CostAmount,
    [property: Id(5)] int DrawCount,
    [property: Id(6)] IReadOnlyList<GachaRewardPoolEntryDto> RewardPool,
    [property: Id(7)] int PityThreshold,
    [property: Id(8)] string IdempotencyKey,
    [property: Id(9)] string RequestHash);

[GenerateSerializer]
public sealed record GachaDrawRewardDto(
    [property: Id(0)] GachaRewardKind Kind,
    [property: Id(1)] string RewardId,
    [property: Id(2)] int Quantity,
    [property: Id(3)] int Rarity,
    [property: Id(4)] bool WasDuplicate,
    [property: Id(5)] InventoryItemDto? DuplicateConversion);

[GenerateSerializer]
public sealed record InventoryItemDto(
    [property: Id(0)] string ItemId,
    [property: Id(1)] long Quantity);

[GenerateSerializer]
public sealed record CharacterDto(
    [property: Id(0)] string CharacterId,
    [property: Id(1)] int Rarity,
    [property: Id(2)] int DuplicateCount);

[GenerateSerializer]
public sealed record GachaDrawResultDto(
    [property: Id(0)] string BannerId,
    [property: Id(1)] string ContentVersion,
    [property: Id(2)] string ContentChecksum,
    [property: Id(3)] IReadOnlyList<GachaDrawRewardDto> Rewards,
    [property: Id(4)] int PityAfter,
    [property: Id(5)] WalletSnapshotDto Wallet);
