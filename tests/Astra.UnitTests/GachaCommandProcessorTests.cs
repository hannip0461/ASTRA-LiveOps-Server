using System.Text.Json;
using Astra.Contracts;
using Astra.Domain;

namespace Astra.UnitTests;

public sealed class GachaCommandProcessorTests
{
    [Fact]
    public void DrawGacha_DebitsCurrency_ConvertsDuplicate_AndRecordsHistory()
    {
        var state = new PlayerAccountState(Guid.NewGuid());
        var processor = new PlayerAccountCommandProcessor(gachaRandomSource: new SequenceRandomSource(0, 0));

        processor.Grant(state, new GrantCurrencyCommand(CurrencyCode.Elif, 1_000, "seed", "grant-1", "grant-hash"));

        var receipt = processor.DrawGacha(state, NewDrawCommand("draw-1", "draw-hash-1", drawCount: 2));
        var result = JsonSerializer.Deserialize<GachaDrawResultDto>(receipt.ResponseBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.False(receipt.Replayed);
        Assert.Equal(900, state.GetBalance(CurrencyCode.Elif));
        Assert.Equal(2, state.LedgerVersion);
        Assert.Equal(1, state.Characters["char-standard"].DuplicateCount);
        Assert.Equal(5, state.Inventory["memory-char-standard"]);
        Assert.Equal(2, state.PityByBanner["pickup-fatima"]);
        Assert.Equal(2, result.PityAfter);
        Assert.False(result.Rewards[0].WasDuplicate);
        Assert.True(result.Rewards[1].WasDuplicate);
        Assert.Equal(5, result.Rewards[1].DuplicateConversion!.Quantity);

        var history = Assert.Single(state.PendingGachaDraws);
        Assert.Equal(0, history.PityBefore);
        Assert.Equal(2, history.PityAfter);
        Assert.Equal("content-2026-07-09-a", history.ContentVersion);
    }

    [Fact]
    public void DrawGacha_AtPityThreshold_ForcesPityTargetAndResetsCounter()
    {
        var state = new PlayerAccountState(Guid.NewGuid());
        var processor = new PlayerAccountCommandProcessor(gachaRandomSource: new SequenceRandomSource(0, 0));
        processor.Grant(state, new GrantCurrencyCommand(CurrencyCode.Elif, 1_000, "seed", "grant-1", "grant-hash"));

        var receipt = processor.DrawGacha(
            state,
            NewDrawCommand("draw-1", "draw-hash-1", drawCount: 2, pityThreshold: 2));
        var result = JsonSerializer.Deserialize<GachaDrawResultDto>(receipt.ResponseBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.Equal(["char-standard", "char-pickup"], result.Rewards.Select(reward => reward.RewardId));
        Assert.Equal(0, result.PityAfter);
        Assert.Equal(0, state.GetPity("pickup-fatima"));
    }

    [Fact]
    public void DrawGacha_WithSameIdempotencyKey_ReplaysWithoutSecondDebitOrDraw()
    {
        var state = new PlayerAccountState(Guid.NewGuid());
        var random = new SequenceRandomSource(0);
        var processor = new PlayerAccountCommandProcessor(gachaRandomSource: random);
        var command = NewDrawCommand("draw-1", "draw-hash-1", drawCount: 1);

        processor.Grant(state, new GrantCurrencyCommand(CurrencyCode.Elif, 1_000, "seed", "grant-1", "grant-hash"));

        var first = processor.DrawGacha(state, command);
        var retry = processor.DrawGacha(state, command);

        Assert.False(first.Replayed);
        Assert.True(retry.Replayed);
        Assert.Equal(first.ResponseBody, retry.ResponseBody);
        Assert.Equal(900, state.GetBalance(CurrencyCode.Elif));
        Assert.Equal(2, state.LedgerVersion);
        Assert.Equal(1, random.Calls);
        Assert.Equal(2, state.CompletedRequests.Count);
        Assert.Single(state.PendingGachaDraws);
    }

    [Fact]
    public void DrawGacha_WhenBalanceIsInsufficient_DoesNotGrantRewardsOrIdempotency()
    {
        var state = new PlayerAccountState(Guid.NewGuid());
        var processor = new PlayerAccountCommandProcessor(gachaRandomSource: new SequenceRandomSource(0));

        Assert.Throws<InsufficientCurrencyException>(() =>
            processor.DrawGacha(state, NewDrawCommand("draw-1", "draw-hash-1", drawCount: 1)));

        Assert.Empty(state.Characters);
        Assert.Empty(state.Inventory);
        Assert.Empty(state.PityByBanner);
        Assert.Empty(state.Ledger);
        Assert.Empty(state.CompletedRequests);
        Assert.Empty(state.PendingGachaDraws);
    }

    internal static IReadOnlyList<GachaRewardPoolEntryDto> RewardPool() =>
    [
        new(
            GachaRewardKind.Character,
            "char-standard",
            1,
            2,
            90,
            false,
            "memory-char-standard",
            5),
        new(
            GachaRewardKind.Character,
            "char-pickup",
            1,
            3,
            10,
            true,
            "memory-char-pickup",
            20)
    ];

    private static DrawGachaCommand NewDrawCommand(
        string idempotencyKey,
        string requestHash,
        int drawCount,
        int pityThreshold = 10) =>
        new(
            "pickup-fatima",
            "content-2026-07-09-a",
            "checksum-a",
            CurrencyCode.Elif,
            100,
            drawCount,
            RewardPool(),
            pityThreshold,
            idempotencyKey,
            requestHash);

    private sealed class SequenceRandomSource(params int[] values) : IGachaRandomSource
    {
        private readonly Queue<int> _values = new(values);

        public int Calls { get; private set; }

        public int Next(int exclusiveUpperBound)
        {
            Calls++;
            return _values.Dequeue() % exclusiveUpperBound;
        }
    }
}
