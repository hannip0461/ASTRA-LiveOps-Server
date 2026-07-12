using Astra.Contracts;
using Astra.Domain;

namespace Astra.UnitTests;

public sealed class GachaCommandFactoryTests
{
    [Fact]
    public void Create_UsesCostAndVersionFromActiveContentSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ContentSnapshotDto(
            "content-a",
            "checksum-a",
            now,
            [new GachaBannerConfigDto(
                "pickup-a",
                CurrencyCode.Elif,
                100,
                90,
                now.AddMinutes(-1),
                now.AddMinutes(10),
                GachaCommandProcessorTests.RewardPool())]);
        var request = new DrawGachaRequest("pickup-a", 3, "draw-1", "draw-hash-1");

        var command = new GachaCommandFactory().Create(snapshot, request);

        Assert.Equal("content-a", command.ContentVersion);
        Assert.Equal("checksum-a", command.ContentChecksum);
        Assert.Equal(CurrencyCode.Elif, command.CostCurrency);
        Assert.Equal(300, command.CostAmount);
        Assert.Equal(3, command.DrawCount);
        Assert.Equal(90, command.PityThreshold);
        Assert.Equal(2, command.RewardPool.Count);
    }

    [Fact]
    public void Create_WhenBannerIsMissing_ThrowsContentMismatch()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ContentSnapshotDto("content-a", "checksum-a", now, []);

        Assert.Throws<ContentMismatchException>(() =>
            new GachaCommandFactory().Create(snapshot, new DrawGachaRequest("missing", 1, "draw-1", "draw-hash-1")));
    }

    [Fact]
    public void Create_WhenBannerIsOutsideWindow_ThrowsContentMismatch()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ContentSnapshotDto(
            "content-a",
            "checksum-a",
            now,
            [new GachaBannerConfigDto(
                "pickup-a",
                CurrencyCode.Elif,
                100,
                90,
                now.AddDays(-2),
                now.AddDays(-1),
                GachaCommandProcessorTests.RewardPool())]);

        Assert.Throws<ContentMismatchException>(() =>
            new GachaCommandFactory().Create(snapshot, new DrawGachaRequest("pickup-a", 1, "draw-1", "draw-hash-1")));
    }

    [Fact]
    public void Create_WhenDrawCountExceedsBatchLimit_ThrowsInvalidCommand()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ContentSnapshotDto(
            "content-a",
            "checksum-a",
            now,
            [new GachaBannerConfigDto(
                "pickup-a",
                CurrencyCode.Elif,
                100,
                90,
                now.AddMinutes(-1),
                now.AddMinutes(10),
                GachaCommandProcessorTests.RewardPool())]);

        Assert.Throws<InvalidAccountCommandException>(() =>
            new GachaCommandFactory().Create(
                snapshot,
                new DrawGachaRequest("pickup-a", 11, "draw-1", "draw-hash-1")));
    }
}
