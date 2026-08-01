using System.Collections.Concurrent;
using Astra.Contracts;
using Astra.Domain;

namespace Astra.Infrastructure;

public sealed class InMemoryPlayerAccountStore : IPlayerAccountStore
{
    private readonly ConcurrentDictionary<Guid, AccountSlot> _accounts = new();

    public Task<WalletSnapshotDto> ReadSnapshotAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slot = _accounts.GetOrAdd(playerId, static id => new AccountSlot(new PlayerAccountState(id)));

        lock (slot.SyncRoot)
        {
            return Task.FromResult(PlayerAccountCommandProcessor.ToSnapshot(slot.State));
        }
    }

    public Task<PlayerCommandReceipt> ExecuteAsync(
        Guid playerId,
        Func<PlayerAccountState, PlayerCommandReceipt> operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slot = _accounts.GetOrAdd(playerId, static id => new AccountSlot(new PlayerAccountState(id)));

        lock (slot.SyncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workingState = slot.State.Clone();
            var receipt = operation(workingState);
            if (!receipt.Replayed)
            {
                workingState.ClearPendingGachaDraws();
                workingState.ClearPendingOutboxEvents();
                workingState.ClearPendingCompletedRequests();
                slot.State = workingState;
            }

            return Task.FromResult(receipt);
        }
    }

    private sealed class AccountSlot(PlayerAccountState state)
    {
        public PlayerAccountState State { get; set; } = state;

        public object SyncRoot { get; } = new();
    }
}
