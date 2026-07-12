using Astra.Contracts;
using Astra.Domain;
using Astra.Infrastructure;

namespace Astra.FailureTests;

public sealed class IdempotencyReplayTests
{
    [Fact]
    public async Task OneHundredRetriesAfterCommit_ReturnSameResponseBodyWithoutMutation()
    {
        var playerId = Guid.NewGuid();
        var store = new InMemoryPlayerAccountStore();
        var processor = new PlayerAccountCommandProcessor();
        var command = new GrantCurrencyCommand(CurrencyCode.Gold, 500, "operation-compensation", "idem-commit-1", "hash-commit-1");

        var first = await store.ExecuteAsync(playerId, state => processor.Grant(state, command));
        var retries = await Task.WhenAll(Enumerable.Range(0, 100).Select(
            _ => store.ExecuteAsync(playerId, state => processor.Grant(state, command))));
        var snapshot = await store.ReadSnapshotAsync(playerId);

        Assert.False(first.Replayed);
        Assert.All(retries, retry =>
        {
            Assert.True(retry.Replayed);
            Assert.Equal(first.ResponseBody, retry.ResponseBody);
        });
        Assert.Equal(500, snapshot.Balances.Single(x => x.Currency == CurrencyCode.Gold).Amount);
        Assert.Equal(1, snapshot.LedgerVersion);
    }

    [Fact]
    public async Task GachaFailureAfterDebit_DoesNotCommitPartialState()
    {
        var playerId = Guid.NewGuid();
        var store = new InMemoryPlayerAccountStore();
        var processor = new PlayerAccountCommandProcessor(gachaRandomSource: new ThrowingRandomSource());

        await store.ExecuteAsync(
            playerId,
            state => processor.Grant(
                state,
                new GrantCurrencyCommand(CurrencyCode.Elif, 500, "seed", "grant-1", "grant-hash")));

        var draw = new DrawGachaCommand(
            "pickup-a",
            "content-a",
            "checksum-a",
            CurrencyCode.Elif,
            100,
            1,
            [
                new GachaRewardPoolEntryDto(
                    GachaRewardKind.Character,
                    "char-pickup",
                    1,
                    3,
                    100,
                    true,
                    "memory-char-pickup",
                    20)
            ],
            90,
            "draw-1",
            "draw-hash");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ExecuteAsync(playerId, state => processor.DrawGacha(state, draw)));

        var snapshot = await store.ReadSnapshotAsync(playerId);
        Assert.Equal(500, snapshot.Balances.Single(balance => balance.Currency == CurrencyCode.Elif).Amount);
        Assert.Equal(1, snapshot.LedgerVersion);
        Assert.Empty(snapshot.Characters);
        Assert.Empty(snapshot.Inventory);
        Assert.Empty(snapshot.PityByBanner);
    }

    private sealed class ThrowingRandomSource : IGachaRandomSource
    {
        public int Next(int exclusiveUpperBound) =>
            throw new InvalidOperationException("Injected failure after debit.");
    }
}
