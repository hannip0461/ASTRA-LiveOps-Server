using Astra.Contracts;
using Astra.Domain;

namespace Astra.Silo.Grains;

public sealed class PlayerAccountGrain(
    IPlayerAccountStore store,
    PlayerAccountCommandProcessor processor,
    IActiveContentCache activeContentCache,
    GachaCommandFactory gachaCommandFactory,
    IMailStore mailStore) : Grain, IPlayerAccountGrain
{
    public Task<WalletSnapshotDto> GetSnapshotAsync() =>
        store.ReadSnapshotAsync(this.GetPrimaryKey());

    public Task<PlayerCommandReceipt> GrantCurrencyAsync(GrantCurrencyCommand command) =>
        store.ExecuteAsync(this.GetPrimaryKey(), state => processor.Grant(state, command));

    public Task<PlayerCommandReceipt> SpendCurrencyAsync(SpendCurrencyCommand command) =>
        store.ExecuteAsync(this.GetPrimaryKey(), state => processor.Spend(state, command));

    public Task<PlayerCommandReceipt> DrawGachaAsync(DrawGachaRequest request) =>
        store.ExecuteAsync(
            this.GetPrimaryKey(),
            state =>
            {
                var replay = processor.TryReplay(state, request.IdempotencyKey, request.RequestHash);
                if (replay is not null)
                {
                    return replay;
                }

                var snapshot = activeContentCache.GetActiveSnapshot()
                    ?? throw new ContentUnavailableException("Active content snapshot is not available.");
                var command = gachaCommandFactory.Create(snapshot, request);
                return processor.DrawGacha(state, command);
            });

    public async Task<PlayerCommandReceipt> ClaimMailAsync(ClaimMailCommand command)
    {
        var playerId = this.GetPrimaryKey();
        var definition = await mailStore.GetDefinitionAsync(command.MailId)
            ?? throw new MailNotFoundException($"Mail not found: {command.MailId}.");

        if (!await mailStore.IsTargetAsync(command.MailId, playerId))
        {
            throw new MailNotEligibleException($"Player is not a target of mail: {command.MailId}.");
        }

        return await store.ExecuteAsync(playerId, state => processor.ClaimMail(state, command, definition));
    }
}
