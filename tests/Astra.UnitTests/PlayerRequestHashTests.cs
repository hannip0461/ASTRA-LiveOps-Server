using Astra.Contracts;
using Astra.Domain;

namespace Astra.UnitTests;

public sealed class PlayerRequestHashTests
{
    [Fact]
    public void DrawGacha_SameSemanticCommand_ProducesStableHash()
    {
        var playerId = Guid.NewGuid();

        var first = PlayerRequestHash.DrawGacha(playerId, "pickup-a", 10);
        var second = PlayerRequestHash.DrawGacha(playerId, "pickup-a", 10);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void MutatingAnySemanticField_ChangesHash()
    {
        var playerId = Guid.NewGuid();
        var baseline = PlayerRequestHash.DrawGacha(playerId, "pickup-a", 1);

        Assert.NotEqual(baseline, PlayerRequestHash.DrawGacha(Guid.NewGuid(), "pickup-a", 1));
        Assert.NotEqual(baseline, PlayerRequestHash.DrawGacha(playerId, "pickup-b", 1));
        Assert.NotEqual(baseline, PlayerRequestHash.DrawGacha(playerId, "pickup-a", 10));
        Assert.NotEqual(
            PlayerRequestHash.GrantCurrency(playerId, CurrencyCode.Elif, 100, "seed"),
            PlayerRequestHash.GrantCurrency(playerId, CurrencyCode.Elif, 100, "compensation"));
    }
}
