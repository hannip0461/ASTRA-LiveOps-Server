using Astra.Contracts;
using Astra.Domain;

namespace Astra.UnitTests;

public sealed class PlayerRequestHashTests
{
    private static readonly Guid GoldenPlayerId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>재시도와 rolling deployment에서 유지할 요청 hash를 고정한다.</summary>
    [Theory]
    // {"operation":"wallet.grant.v1","playerId":"11111111222233334444555555555555","currency":2,"amount":100,"reason":"seed"}
    [InlineData("grant", "501380ccd399fde5bee24fc99cfd65e12503c0d27181ccca0f4b57ec486f42ba")]
    // {"operation":"wallet.spend.v1","playerId":"11111111222233334444555555555555","currency":1,"amount":250,"reason":"gacha:pickup-a"}
    [InlineData("spend", "6e9c14a2e34e6875fa8fdd65220dfb39563d231a9402b32f632fdc2bb0220d7e")]
    // {"operation":"gacha.draw.v1","playerId":"11111111222233334444555555555555","bannerId":"pickup-a","drawCount":10}
    [InlineData("draw", "6a3b745f34415195602108e43f2bdf5933249c6121b411bee2093ae6dcd30614")]
    // {"operation":"mail.claim.v1","playerId":"11111111222233334444555555555555","mailId":"mail-001"}
    [InlineData("claim", "00a84a4e9b55114ea92248610958f21849f1c8eb04852e092a5d6e40ff52563c")]
    public void KnownRequest_MatchesGoldenHash(string operation, string expectedHash)
    {
        var actual = operation switch
        {
            "grant" => PlayerRequestHash.GrantCurrency(GoldenPlayerId, CurrencyCode.Elif, 100, "seed"),
            "spend" => PlayerRequestHash.SpendCurrency(GoldenPlayerId, CurrencyCode.Gold, 250, "gacha:pickup-a"),
            "draw" => PlayerRequestHash.DrawGacha(GoldenPlayerId, "pickup-a", 10),
            "claim" => PlayerRequestHash.ClaimMail(GoldenPlayerId, "mail-001"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown golden case.")
        };

        Assert.Equal(expectedHash, actual);
    }

    [Fact]
    public void DifferentOperations_WithIdenticalArguments_DoNotCollide()
    {
        var grant = PlayerRequestHash.GrantCurrency(GoldenPlayerId, CurrencyCode.Elif, 100, "seed");
        var spend = PlayerRequestHash.SpendCurrency(GoldenPlayerId, CurrencyCode.Elif, 100, "seed");

        Assert.NotEqual(grant, spend);
    }

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
