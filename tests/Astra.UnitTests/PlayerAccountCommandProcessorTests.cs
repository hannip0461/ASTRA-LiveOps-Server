using System.Text.Json;
using Astra.Contracts;
using Astra.Domain;

namespace Astra.UnitTests;

public sealed class PlayerAccountCommandProcessorTests
{
    [Fact]
    public void Grant_WithSameIdempotencyKey_ReplaysStoredResponse()
    {
        var playerId = Guid.NewGuid();
        var state = new PlayerAccountState(playerId);
        var processor = new PlayerAccountCommandProcessor();
        var command = new GrantCurrencyCommand(CurrencyCode.Elif, 100, "seed", "idem-1", "hash-1");

        var first = processor.Grant(state, command);
        var second = processor.Grant(state, command);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.ResponseBody, second.ResponseBody);
        Assert.Equal(100, state.GetBalance(CurrencyCode.Elif));
        Assert.Single(state.Ledger);
        Assert.Single(state.CompletedRequests);
        Assert.Single(state.PendingOutboxEvents);
        Assert.Equal("wallet.currency_granted", state.PendingOutboxEvents[0].EventType);
        Assert.NotEqual(first.ResponseBody, state.PendingOutboxEvents[0].Payload);
        using var payload = JsonDocument.Parse(state.PendingOutboxEvents[0].Payload);
        Assert.Equal(1, payload.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(100, payload.RootElement.GetProperty("amount").GetInt64());
        Assert.False(payload.RootElement.TryGetProperty("balances", out _));
    }

    [Fact]
    public void Grant_WithSameIdempotencyKeyAndDifferentHash_ThrowsConflict()
    {
        var state = new PlayerAccountState(Guid.NewGuid());
        var processor = new PlayerAccountCommandProcessor();

        processor.Grant(state, new GrantCurrencyCommand(CurrencyCode.Elif, 100, "seed", "idem-1", "hash-1"));

        Assert.Throws<IdempotencyConflictException>(() =>
            processor.Grant(state, new GrantCurrencyCommand(CurrencyCode.Elif, 100, "seed", "idem-1", "hash-2")));
    }

    [Fact]
    public void Grant_ReplayAfterLaterCommand_ReturnsOriginalEnvelopeSnapshot()
    {
        var state = new PlayerAccountState(Guid.NewGuid());
        var processor = new PlayerAccountCommandProcessor();
        var firstCommand = new GrantCurrencyCommand(CurrencyCode.Elif, 100, "seed", "idem-1", "hash-1");

        var first = processor.Grant(state, firstCommand);
        processor.Grant(state, new GrantCurrencyCommand(CurrencyCode.Elif, 50, "bonus", "idem-2", "hash-2"));
        var replay = processor.Grant(state, firstCommand);

        Assert.True(replay.Replayed);
        Assert.Equal(first.ResponseBody, replay.ResponseBody);
        Assert.Equal(first.Snapshot.LedgerVersion, replay.Snapshot.LedgerVersion);
        Assert.Equal(
            first.Snapshot.Balances.Single().Amount,
            replay.Snapshot.Balances.Single().Amount);
        Assert.Equal(150, state.GetBalance(CurrencyCode.Elif));
    }

    [Fact]
    public void Grant_AfterIdempotencyTtl_AllowsKeyReuseAsNewRequest()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero));
        var state = new PlayerAccountState(Guid.NewGuid());
        var processor = new PlayerAccountCommandProcessor(clock);

        processor.Grant(state, new GrantCurrencyCommand(CurrencyCode.Elif, 100, "seed", "idem-1", "hash-1"));
        clock.Advance(TimeSpan.FromHours(25));
        var reused = processor.Grant(
            state,
            new GrantCurrencyCommand(CurrencyCode.Elif, 50, "bonus", "idem-1", "hash-2"));

        Assert.False(reused.Replayed);
        Assert.Equal(150, state.GetBalance(CurrencyCode.Elif));
        Assert.Equal("hash-2", state.CompletedRequests["idem-1"].RequestHash);
    }

    [Fact]
    public void Spend_WhenBalanceIsInsufficient_DoesNotWriteLedgerOrIdempotency()
    {
        var state = new PlayerAccountState(Guid.NewGuid());
        var processor = new PlayerAccountCommandProcessor();

        Assert.Throws<InsufficientCurrencyException>(() =>
            processor.Spend(state, new SpendCurrencyCommand(CurrencyCode.Elif, 1, "draw", "idem-1", "hash-1")));

        Assert.Equal(0, state.GetBalance(CurrencyCode.Elif));
        Assert.Empty(state.Ledger);
        Assert.Empty(state.CompletedRequests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-500)]
    public void Spend_WithNonPositiveAmount_IsRejectedAndLeavesBalanceUntouched(long amount)
    {
        var state = new PlayerAccountState(Guid.NewGuid());
        var processor = new PlayerAccountCommandProcessor();
        processor.Grant(state, new GrantCurrencyCommand(CurrencyCode.Elif, 100, "seed", "idem-seed", "hash-seed"));

        Assert.Throws<InvalidAccountCommandException>(() =>
            processor.Spend(state, new SpendCurrencyCommand(CurrencyCode.Elif, amount, "draw", "idem-1", "hash-1")));

        Assert.Equal(100, state.GetBalance(CurrencyCode.Elif));
        Assert.Single(state.Ledger);
        Assert.DoesNotContain("idem-1", state.CompletedRequests.Keys);
    }

    [Fact]
    public void Spend_WithUndefinedCurrency_IsRejected()
    {
        var state = new PlayerAccountState(Guid.NewGuid());
        var processor = new PlayerAccountCommandProcessor();

        Assert.Throws<InvalidAccountCommandException>(() =>
            processor.Spend(state, new SpendCurrencyCommand((CurrencyCode)99, 10, "draw", "idem-1", "hash-1")));

        Assert.Empty(state.Ledger);
        Assert.Empty(state.CompletedRequests);
    }

    [Fact]
    public void ClaimMail_WithSameIdempotencyKey_ReplaysStoredResponse()
    {
        var state = new PlayerAccountState(Guid.NewGuid());
        var processor = new PlayerAccountCommandProcessor();
        var definition = CreateMailDefinition();
        var command = new ClaimMailCommand(definition.MailId, "mail-claim-1", "mail-hash-1");

        var first = processor.ClaimMail(state, command, definition);
        var replay = processor.ClaimMail(state, command, definition);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.ResponseBody, replay.ResponseBody);
        Assert.Equal(300, state.GetBalance(CurrencyCode.Elif));
        Assert.Single(state.Ledger);
        Assert.True(state.ClaimedMailIdempotencyKeys.ContainsKey(definition.MailId));
    }

    [Fact]
    public void ClaimMail_WithDifferentKeyAfterClaim_ThrowsAlreadyClaimed()
    {
        var state = new PlayerAccountState(Guid.NewGuid());
        var processor = new PlayerAccountCommandProcessor();
        var definition = CreateMailDefinition();

        processor.ClaimMail(state, new ClaimMailCommand(definition.MailId, "mail-claim-1", "mail-hash-1"), definition);

        Assert.Throws<MailAlreadyClaimedException>(() =>
            processor.ClaimMail(state, new ClaimMailCommand(definition.MailId, "mail-claim-2", "mail-hash-2"), definition));

        Assert.Equal(300, state.GetBalance(CurrencyCode.Elif));
        Assert.Single(state.Ledger);
    }

    private static MailDefinitionDto CreateMailDefinition() =>
        new(
            "incident-gacha-rollback",
            "mail-comp-001",
            "Compensation",
            "Incident compensation",
            [new MailRewardDto(CurrencyCode.Elif, 300)],
            "bad-gacha-table",
            DateTimeOffset.UtcNow);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
