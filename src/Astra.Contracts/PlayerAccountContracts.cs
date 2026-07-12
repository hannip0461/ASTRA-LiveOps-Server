using Orleans;

namespace Astra.Contracts;

[GenerateSerializer]
public sealed record CurrencyBalanceDto(
    [property: Id(0)] CurrencyCode Currency,
    [property: Id(1)] long Amount);

[GenerateSerializer]
public sealed record WalletSnapshotDto(
    [property: Id(0)] Guid PlayerId,
    [property: Id(1)] IReadOnlyList<CurrencyBalanceDto> Balances,
    [property: Id(2)] long LedgerVersion,
    [property: Id(3)] IReadOnlyList<InventoryItemDto> Inventory,
    [property: Id(4)] IReadOnlyList<CharacterDto> Characters,
    [property: Id(5)] IReadOnlyDictionary<string, int> PityByBanner);

[GenerateSerializer]
public sealed record GrantCurrencyCommand(
    [property: Id(0)] CurrencyCode Currency,
    [property: Id(1)] long Amount,
    [property: Id(2)] string Reason,
    [property: Id(3)] string IdempotencyKey,
    [property: Id(4)] string RequestHash);

[GenerateSerializer]
public sealed record SpendCurrencyCommand(
    [property: Id(0)] CurrencyCode Currency,
    [property: Id(1)] long Amount,
    [property: Id(2)] string Reason,
    [property: Id(3)] string IdempotencyKey,
    [property: Id(4)] string RequestHash);

[GenerateSerializer]
public sealed record PlayerCommandReceipt(
    [property: Id(0)] bool Replayed,
    [property: Id(1)] string ResponseBody,
    [property: Id(2)] WalletSnapshotDto Snapshot);

public interface IPlayerAccountGrain : IGrainWithGuidKey
{
    Task<WalletSnapshotDto> GetSnapshotAsync();

    Task<PlayerCommandReceipt> GrantCurrencyAsync(GrantCurrencyCommand command);

    Task<PlayerCommandReceipt> SpendCurrencyAsync(SpendCurrencyCommand command);

    Task<PlayerCommandReceipt> DrawGachaAsync(DrawGachaRequest request);

    Task<PlayerCommandReceipt> ClaimMailAsync(ClaimMailCommand command);
}
