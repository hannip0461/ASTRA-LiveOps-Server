using Astra.Contracts;
using Astra.Domain;
using Astra.Infrastructure;

namespace Astra.ConcurrencyTests;

public sealed class PlayerAccountConcurrencyTests
{
    [Fact]
    public async Task ConcurrentSpend_UsesSingleAccountLock_AndNeverGoesNegative()
    {
        var playerId = Guid.NewGuid();
        var store = new InMemoryPlayerAccountStore();
        var processor = new PlayerAccountCommandProcessor();

        await store.ExecuteAsync(
            playerId,
            state => processor.Grant(state, new GrantCurrencyCommand(CurrencyCode.Elif, 10, "seed", "grant-1", "grant-hash")));

        var spendTasks = Enumerable.Range(0, 50)
            .Select(i => Task.Run(async () =>
            {
                try
                {
                    await store.ExecuteAsync(
                        playerId,
                        state => processor.Spend(
                            state,
                            new SpendCurrencyCommand(CurrencyCode.Elif, 1, "draw", $"spend-{i}", $"spend-hash-{i}")));
                    return true;
                }
                catch (InsufficientCurrencyException)
                {
                    return false;
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(spendTasks);
        var snapshot = await store.ReadSnapshotAsync(playerId);
        var balance = snapshot.Balances.Single(x => x.Currency == CurrencyCode.Elif).Amount;

        Assert.Equal(10, results.Count(x => x));
        Assert.Equal(0, balance);
        Assert.Equal(11, snapshot.LedgerVersion);
    }
}
