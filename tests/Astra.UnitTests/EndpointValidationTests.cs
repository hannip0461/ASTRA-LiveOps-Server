using Astra.Api;
using Astra.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Astra.UnitTests;

public sealed class EndpointValidationTests
{
    [Fact]
    public void PublishContent_RejectsInvalidNestedPayload()
    {
        var command = new PublishContentCommand(
            "invalid version",
            [null!],
            " ");

        var errors = EndpointValidation.PublishContent(command).ToDictionary();

        Assert.Contains("version", errors.Keys);
        Assert.Contains("reason", errors.Keys);
        Assert.Contains("gachaBanners[0]", errors.Keys);
    }

    [Fact]
    public void Normalize_CanonicalizesContentIdentifiers()
    {
        var now = DateTimeOffset.UtcNow;
        var command = new PublishContentCommand(
            " content-a ",
            [new GachaBannerConfigDto(
                " banner-a ",
                CurrencyCode.Elif,
                100,
                90,
                now,
                now.AddHours(1),
                [new GachaRewardPoolEntryDto(
                    GachaRewardKind.Character,
                    " character-a ",
                    1,
                    3,
                    100,
                    true,
                    " memory-a ",
                    20)])],
            " publish reason ");

        var normalized = EndpointValidation.Normalize(command);

        Assert.Equal("content-a", normalized.Version);
        Assert.Equal("publish reason", normalized.Reason);
        Assert.Equal("banner-a", normalized.GachaBanners[0].BannerId);
        Assert.Equal("character-a", normalized.GachaBanners[0].RewardPool[0].RewardId);
        Assert.Equal("memory-a", normalized.GachaBanners[0].RewardPool[0].DuplicateItemId);
    }

    [Fact]
    public void PlayerCommands_EnforceAmountAndIdempotencyBounds()
    {
        var errors = EndpointValidation.CurrencyCommand(
            CurrencyCode.Elif,
            long.MaxValue,
            "grant",
            "invalid key").ToDictionary();

        Assert.Contains("amount", errors.Keys);
        Assert.Contains("idempotencyKey", errors.Keys);
    }

    [Fact]
    public void ProblemDetails_UsesStableCodeAndTraceId()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-a" };
        var problem = new ProblemDetails { Status = StatusCodes.Status403Forbidden };

        ApiProblemDetails.Enrich(context, problem);

        Assert.Equal("permission_denied", problem.Extensions["code"]);
        Assert.Equal("trace-a", problem.Extensions["traceId"]);
        Assert.Equal("urn:astra:problem:permission_denied", problem.Type);
    }

    [Fact]
    public void ReplayOutbox_RequiresEventIdAndReason()
    {
        var errors = EndpointValidation.ReplayOutbox(
            Guid.Empty,
            new ReplayOutboxEventCommand(" ")).ToDictionary();

        Assert.Contains("eventId", errors.Keys);
        Assert.Contains("reason", errors.Keys);
    }
}
