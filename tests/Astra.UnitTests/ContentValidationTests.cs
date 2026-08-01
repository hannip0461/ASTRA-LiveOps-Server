using Astra.Contracts;
using Astra.Domain;
using Astra.Infrastructure;

namespace Astra.UnitTests;

public sealed class ContentValidationTests
{
    [Fact]
    public void ValidateAndCreateSnapshot_WithValidBanner_PublishesSnapshot()
    {
        var service = new ContentValidationService();

        var result = service.ValidateAndCreateSnapshot(NewPublishCommand("content-a"));

        Assert.True(result.Published);
        Assert.NotNull(result.Snapshot);
        Assert.Equal("content-a", result.Snapshot.Version);
        Assert.NotEmpty(result.Snapshot.Checksum);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ValidateAndCreateSnapshot_WithInvalidPeriod_ReturnsIssue()
    {
        var service = new ContentValidationService();
        var now = DateTimeOffset.UtcNow;
        var command = new PublishContentCommand(
            "content-a",
            [NewBanner("pickup-a", now, now)]);

        var result = service.ValidateAndCreateSnapshot(command);

        Assert.False(result.Published);
        Assert.Contains(result.Issues, issue => issue.Code == "gacha.period.invalid");
    }

    [Fact]
    public void ValidateAndCreateSnapshot_ProducesStableChecksumForEquivalentBannerOrder()
    {
        var service = new ContentValidationService();
        var now = DateTimeOffset.UtcNow;
        var first = new PublishContentCommand(
            "content-a",
            [
                NewBanner("pickup-b", now, now.AddDays(1)),
                NewBanner("pickup-a", now, now.AddDays(1))
            ]);
        var second = new PublishContentCommand(
            "content-a",
            [
                NewBanner("pickup-a", now, now.AddDays(1)),
                NewBanner("pickup-b", now, now.AddDays(1))
            ]);

        var firstResult = service.ValidateAndCreateSnapshot(first);
        var secondResult = service.ValidateAndCreateSnapshot(second);

        Assert.Equal(firstResult.Snapshot!.Checksum, secondResult.Snapshot!.Checksum);
    }

    [Fact]
    public void ValidateAndCreateSnapshot_ProducesStableChecksumForEquivalentPoolOrder()
    {
        var now = DateTimeOffset.UtcNow;
        var pool = GachaCommandProcessorTests.RewardPool();
        var first = new PublishContentCommand(
            "content-a",
            [NewBanner("pickup-a", now, now.AddDays(1), pool)]);
        var second = new PublishContentCommand(
            "content-a",
            [NewBanner("pickup-a", now, now.AddDays(1), pool.Reverse().ToArray())]);

        var service = new ContentValidationService();
        var firstResult = service.ValidateAndCreateSnapshot(first);
        var secondResult = service.ValidateAndCreateSnapshot(second);

        Assert.Equal(firstResult.Snapshot!.Checksum, secondResult.Snapshot!.Checksum);
        Assert.Equal(
            firstResult.Snapshot.GachaBanners[0].RewardPool,
            secondResult.Snapshot.GachaBanners[0].RewardPool);
    }

    [Fact]
    public void ValidateAndCreateSnapshot_ProducesStableChecksumForEquivalentInstantsInAnotherOffset()
    {
        var startsAtUtc = new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);
        var endsAtUtc = startsAtUtc.AddDays(1);
        var startsAtKst = startsAtUtc.ToOffset(TimeSpan.FromHours(9));
        var endsAtKst = endsAtUtc.ToOffset(TimeSpan.FromHours(9));

        Assert.Equal(startsAtUtc, startsAtKst);
        Assert.Equal(endsAtUtc, endsAtKst);

        var service = new ContentValidationService();
        var asUtc = service.ValidateAndCreateSnapshot(new PublishContentCommand(
            "content-a",
            [NewBanner("pickup-a", startsAtUtc, endsAtUtc)]));
        var asKst = service.ValidateAndCreateSnapshot(new PublishContentCommand(
            "content-a",
            [NewBanner("pickup-a", startsAtKst, endsAtKst)]));

        Assert.Equal(asUtc.Snapshot!.Checksum, asKst.Snapshot!.Checksum);
    }

    [Fact]
    public void ValidateAndCreateSnapshot_WithInvalidRewardPool_ReturnsSpecificIssues()
    {
        var now = DateTimeOffset.UtcNow;
        var invalidPool =
            new[]
            {
                new GachaRewardPoolEntryDto(
                    GachaRewardKind.Character,
                    "char-a",
                    2,
                    3,
                    0,
                    false,
                    null,
                    0)
            };
        var command = new PublishContentCommand(
            "content-a",
            [NewBanner("pickup-a", now, now.AddDays(1), invalidPool)]);

        var result = new ContentValidationService().ValidateAndCreateSnapshot(command);

        Assert.False(result.Published);
        Assert.Contains(result.Issues, issue => issue.Code == "gacha.pool.weight.invalid");
        Assert.Contains(result.Issues, issue => issue.Code == "gacha.pool.pity_target.required");
        Assert.Contains(result.Issues, issue => issue.Code == "gacha.pool.character.quantity.invalid");
        Assert.Contains(result.Issues, issue => issue.Code == "gacha.pool.duplicate_conversion.required");
    }

    [Fact]
    public async Task ContentSnapshotStore_CanActivatePreviousSnapshot()
    {
        var service = new ContentValidationService();
        var store = new InMemoryContentSnapshotStore();
        var first = service.ValidateAndCreateSnapshot(NewPublishCommand("content-a")).Snapshot!;
        var second = service.ValidateAndCreateSnapshot(NewPublishCommand("content-b")).Snapshot!;

        await store.PublishAsync(first);
        Assert.Equal(first, await store.PublishAsync(first));
        await store.PublishAsync(second);
        await Assert.ThrowsAsync<ContentVersionInactiveException>(() => store.PublishAsync(first));
        Assert.Equal("content-b", (await store.GetActiveAsync())!.Version);

        var active = await store.ActivateAsync("content-a");

        Assert.Equal("content-a", active!.Version);
        Assert.Equal("content-a", (await store.GetActiveAsync())!.Version);
    }

    [Fact]
    public async Task ContentSnapshotStore_RejectsDifferentPayloadForImmutableVersion()
    {
        var service = new ContentValidationService();
        var store = new InMemoryContentSnapshotStore();
        var first = service.ValidateAndCreateSnapshot(NewPublishCommand("content-a")).Snapshot!;

        await store.PublishAsync(first);

        await Assert.ThrowsAsync<ContentVersionConflictException>(
            () => store.PublishAsync(first with { Checksum = "different-checksum" }));
        Assert.Equal(first.Checksum, (await store.GetActiveAsync())!.Checksum);
    }

    private static PublishContentCommand NewPublishCommand(string version)
    {
        var now = DateTimeOffset.UtcNow;
        return new PublishContentCommand(
            version,
            [NewBanner("pickup-a", now, now.AddDays(1))]);
    }

    private static GachaBannerConfigDto NewBanner(
        string bannerId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        IReadOnlyList<GachaRewardPoolEntryDto>? rewardPool = null) =>
        new(
            bannerId,
            CurrencyCode.Elif,
            100,
            90,
            startsAt,
            endsAt,
            rewardPool ?? GachaCommandProcessorTests.RewardPool());
}
