using Astra.Contracts;

namespace Astra.Domain;

public interface IPlayerAccountStore
{
    Task<WalletSnapshotDto> ReadSnapshotAsync(Guid playerId, CancellationToken cancellationToken = default);

    Task<PlayerCommandReceipt> ExecuteAsync(
        Guid playerId,
        Func<PlayerAccountState, PlayerCommandReceipt> operation,
        CancellationToken cancellationToken = default);
}
