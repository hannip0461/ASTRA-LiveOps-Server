using Astra.Contracts;
using Astra.Contracts.Tcp;

namespace Astra.TcpGateway;

internal static class TcpProtoMapper
{
    public static WalletSnapshot ToProto(WalletSnapshotDto source)
    {
        var target = new WalletSnapshot
        {
            PlayerId = source.PlayerId.ToString("D"),
            LedgerVersion = source.LedgerVersion
        };

        target.Balances.Add(source.Balances.Select(balance => new CurrencyBalance
        {
            Currency = (int)balance.Currency,
            Amount = balance.Amount
        }));
        target.Inventory.Add(source.Inventory.Select(item => new InventoryItem
        {
            ItemId = item.ItemId,
            Quantity = item.Quantity
        }));
        target.Characters.Add(source.Characters.Select(character => new Character
        {
            CharacterId = character.CharacterId,
            Rarity = character.Rarity,
            DuplicateCount = character.DuplicateCount
        }));
        target.Pity.Add(source.PityByBanner
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new PityState
            {
                BannerId = pair.Key,
                Pity = pair.Value
            }));

        return target;
    }

    public static GachaDrawResponse ToProto(GachaDrawResultDto source)
    {
        var target = new GachaDrawResponse
        {
            BannerId = source.BannerId,
            ContentVersion = source.ContentVersion,
            ContentChecksum = source.ContentChecksum,
            PityAfter = source.PityAfter,
            Wallet = ToProto(source.Wallet)
        };

        target.Rewards.Add(source.Rewards.Select(ToProto));
        return target;
    }

    private static GachaReward ToProto(GachaDrawRewardDto source)
    {
        var target = new GachaReward
        {
            Kind = (int)source.Kind,
            RewardId = source.RewardId,
            Quantity = source.Quantity,
            Rarity = source.Rarity,
            WasDuplicate = source.WasDuplicate
        };

        if (source.DuplicateConversion is not null)
        {
            target.DuplicateConversion = new DuplicateConversion
            {
                ItemId = source.DuplicateConversion.ItemId,
                Quantity = source.DuplicateConversion.Quantity
            };
        }

        return target;
    }
}
