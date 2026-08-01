using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Astra.Contracts;

namespace Astra.IntegrationTests;

[Collection(EndToEndCollection.Name)]
public sealed class ApiBoundaryEndToEndTests
{
    [RequiresEnvironmentFact("ASTRA_RUN_API_E2E")]
    public async Task ApiBoundary_ReturnsProblemDetails_AndRateLimitsByActor()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var anonymous = ApiE2E.Client();
        using var unauthorized = await anonymous.GetAsync(
            $"/api/players/{Guid.NewGuid():D}/wallet",
            timeout.Token);
        await AssertProblemAsync(
            unauthorized,
            HttpStatusCode.Unauthorized,
            "authentication_required",
            timeout.Token);

        using var notFound = await anonymous.GetAsync("/api/does-not-exist", timeout.Token);
        await AssertProblemAsync(
            notFound,
            HttpStatusCode.NotFound,
            "resource_not_found",
            timeout.Token);

        using var viewer = await ApiE2E.AuthenticatedClientAsync("local-viewer", timeout.Token);
        using var forbidden = await viewer.PostAsJsonAsync(
            "/api/admin/content/publish",
            ValidPublishCommand(),
            timeout.Token);
        await AssertProblemAsync(
            forbidden,
            HttpStatusCode.Forbidden,
            "permission_denied",
            timeout.Token);

        using var rateActor = await ApiE2E.AuthenticatedClientAsync("local-rate-test", timeout.Token);
        var playerId = Guid.NewGuid();
        using var invalid = await rateActor.PostAsJsonAsync(
            $"/api/players/{playerId:D}/wallet/grant",
            new GrantCurrencyCommand(
                CurrencyCode.Elif,
                0,
                "boundary-validation",
                $"grant-{Guid.NewGuid():N}",
                "client-hash-is-ignored"),
            timeout.Token);
        var validation = await AssertProblemAsync(
            invalid,
            HttpStatusCode.BadRequest,
            "validation_failed",
            timeout.Token);
        Assert.True(validation.GetProperty("errors").TryGetProperty("amount", out _));

        using var insufficient = await rateActor.PostAsJsonAsync(
            $"/api/players/{playerId:D}/wallet/spend",
            new SpendCurrencyCommand(
                CurrencyCode.Elif,
                1,
                "boundary-domain-error",
                $"spend-{Guid.NewGuid():N}",
                "client-hash-is-ignored"),
            timeout.Token);
        await AssertProblemAsync(
            insufficient,
            HttpStatusCode.Conflict,
            "insufficient_currency",
            timeout.Token);

        var audits = await rateActor.GetFromJsonAsync<OperationAuditDto[]>(
            "/api/admin/audit?limit=200",
            timeout.Token) ?? [];
        var failedAudit = Assert.Single(
            audits,
            entry => entry.Action == "wallet.spend" && entry.TargetId == playerId.ToString("D"));
        Assert.Equal(OperationAuditStatus.Failed, failedAudit.Status);
        Assert.Equal("insufficient_currency", failedAudit.ErrorCode);
        Assert.DoesNotContain("Exception", failedAudit.ErrorCode, StringComparison.Ordinal);

        HttpResponseMessage? limited = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var response = await rateActor.PostAsJsonAsync(
                $"/api/players/{playerId:D}/wallet/grant",
                new GrantCurrencyCommand(
                    CurrencyCode.Elif,
                    0,
                    "boundary-rate-limit",
                    $"rate-{Guid.NewGuid():N}",
                    "client-hash-is-ignored"),
                timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
                break;
            }

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            response.Dispose();
        }

        Assert.NotNull(limited);
        using (limited)
        {
            Assert.True(limited.Headers.RetryAfter is not null);
            await AssertProblemAsync(
                limited,
                HttpStatusCode.TooManyRequests,
                "rate_limited",
                timeout.Token);
        }
    }

    private static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        CancellationToken cancellationToken)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        return root.Clone();
    }

    private static PublishContentCommand ValidPublishCommand()
    {
        var now = DateTimeOffset.UtcNow;
        return new PublishContentCommand(
            $"boundary-{Guid.NewGuid():N}",
            [new GachaBannerConfigDto(
                "boundary-banner",
                CurrencyCode.Elif,
                100,
                90,
                now,
                now.AddMinutes(10),
                [new GachaRewardPoolEntryDto(
                    GachaRewardKind.Character,
                    "boundary-character",
                    1,
                    3,
                    100,
                    true,
                    "boundary-memory",
                    20)])],
            "boundary-forbidden");
    }
}
