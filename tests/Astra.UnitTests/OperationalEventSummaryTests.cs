using System.Text.Json;
using Astra.Domain;
using Astra.Worker;

namespace Astra.UnitTests;

public sealed class OperationalEventSummaryTests
{
    [Fact]
    public void Create_WalletEvent_StoresOnlyOperationalProjection()
    {
        var outboxEvent = Event(
            "wallet.currency_granted",
            """
            {
              "schemaVersion": 1,
              "currency": 1,
              "amount": 100,
              "balanceAfter": 250,
              "ledgerVersion": 7,
              "secretToken": "must-not-be-copied"
            }
            """);

        var summary = OperationalEventSummaryFactory.Create(outboxEvent);
        using var document = JsonDocument.Parse(summary);

        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(100, document.RootElement.GetProperty("amount").GetInt64());
        Assert.Equal(250, document.RootElement.GetProperty("balanceAfter").GetInt64());
        Assert.Equal(7, document.RootElement.GetProperty("ledgerVersion").GetInt64());
        Assert.DoesNotContain("secretToken", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-copied", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_LegacyWalletResponse_RemainsReadableDuringDeployment()
    {
        var summary = OperationalEventSummaryFactory.Create(Event(
            "wallet.currency_granted",
            "{\"balances\":[{\"currency\":1,\"amount\":100}],\"ledgerVersion\":3}"));
        using var document = JsonDocument.Parse(summary);

        Assert.Equal(0, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("balanceCount").GetInt32());
    }

    [Fact]
    public void Create_RejectsUnsupportedEventType()
    {
        var exception = Assert.Throws<UnsupportedOutboxEventException>(
            () => OperationalEventSummaryFactory.Create(Event("unknown.event", "{}")));

        Assert.Equal("outbox_event_unsupported", OutboxFailureClassifier.GetCode(exception));
    }

    [Fact]
    public void Create_RejectsPayloadThatDoesNotMatchEventType()
    {
        var exception = Assert.Throws<InvalidOutboxPayloadException>(
            () => OperationalEventSummaryFactory.Create(Event("mail.claimed", "{\"mailId\":\"mail-a\"}")));

        Assert.Equal("outbox_payload_invalid", OutboxFailureClassifier.GetCode(exception));
    }

    private static OutboxEventRecord Event(string eventType, string payload) => new(
        Guid.NewGuid(),
        eventType,
        Guid.NewGuid(),
        "idem-a",
        payload,
        0,
        5);
}
