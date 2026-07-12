using System.Net;
using System.Net.Http.Json;
using Astra.Contracts;

namespace Astra.IntegrationTests;

[Collection(EndToEndCollection.Name)]
public sealed class AdminAuthorizationEndToEndTests
{
    [Fact]
    public async Task AdminRoutes_EnforceRoleMatrix_AndPersistAuditActor()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ASTRA_RUN_API_E2E"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var anonymous = ApiE2E.Client();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.PostAsJsonAsync(
                "/api/dev/auth/token",
                new DevOperatorTokenRequest("local-supervisor"),
                timeout.Token)).StatusCode);
        using var proxiedTokenRequest = new HttpRequestMessage(HttpMethod.Post, "/api/dev/auth/token")
        {
            Content = JsonContent.Create(new DevOperatorTokenRequest("local-supervisor"))
        };
        proxiedTokenRequest.Headers.TryAddWithoutValidation(
            DevAuthenticationHeaders.TokenKey,
            ApiE2E.DevTokenKey());
        proxiedTokenRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.SendAsync(proxiedTokenRequest, timeout.Token)).StatusCode);
        using var viewer = await ApiE2E.AuthenticatedClientAsync("local-viewer", timeout.Token);
        using var operatorClient = await ApiE2E.AuthenticatedClientAsync("local-operator", timeout.Token);
        using var supervisor = await ApiE2E.AuthenticatedClientAsync("local-supervisor", timeout.Token);
        var version = $"auth-e2e-{Guid.NewGuid():N}";
        var publish = PublishCommand(version);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/admin/content/active", timeout.Token)).StatusCode);
        Assert.NotEqual(
            HttpStatusCode.Forbidden,
            (await viewer.GetAsync("/api/admin/content/active", timeout.Token)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync("/api/admin/content/publish", publish, timeout.Token)).StatusCode);

        var publishResponse = await operatorClient.PostAsJsonAsync(
            "/api/admin/content/publish",
            publish,
            timeout.Token);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await operatorClient.PostAsJsonAsync(
                $"/api/admin/content/rollback/{version}",
                new RollbackContentCommand("operator-must-not-rollback"),
                timeout.Token)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await supervisor.PostAsJsonAsync(
                $"/api/admin/content/rollback/{version}",
                new RollbackContentCommand("supervisor-rollback-check"),
                timeout.Token)).StatusCode);

        var playerId = Guid.NewGuid();
        var grant = new GrantCurrencyCommand(
            CurrencyCode.Elif,
            100,
            "authorization-e2e",
            $"grant-{Guid.NewGuid():N}",
            "client-hash-is-ignored");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/players/{playerId:D}/wallet", timeout.Token)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await viewer.GetAsync($"/api/players/{playerId:D}/wallet", timeout.Token)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync(
                $"/api/players/{playerId:D}/wallet/grant",
                grant,
                timeout.Token)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync(
                $"/api/players/{playerId:D}/wallet/grant",
                grant,
                timeout.Token)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await operatorClient.PostAsJsonAsync(
                $"/api/players/{playerId:D}/wallet/grant",
                grant,
                timeout.Token)).StatusCode);

        var privateTarget = Guid.NewGuid();
        var privateTitle = $"private-title-{Guid.NewGuid():N}";
        var privateBody = $"private-body-{Guid.NewGuid():N}";
        var mailId = $"auth-mail-{Guid.NewGuid():N}";
        Assert.Equal(
            HttpStatusCode.OK,
            (await operatorClient.PostAsJsonAsync(
                "/api/admin/mail/incident",
                new CreateIncidentMailCommand(
                    $"incident-{Guid.NewGuid():N}",
                    mailId,
                    privateTitle,
                    privateBody,
                    [privateTarget],
                    [new MailRewardDto(CurrencyCode.Elif, 100)],
                    "authorization-redaction-e2e"),
                timeout.Token)).StatusCode);

        var audits = await supervisor.GetFromJsonAsync<OperationAuditDto[]>(
            "/api/admin/audit?limit=200",
            timeout.Token) ?? [];
        var publishAudit = Assert.Single(
            audits,
            entry => entry.Action == "content.publish" && entry.TargetId == version);
        Assert.Equal("local-operator", publishAudit.ActorId);
        Assert.Equal(LiveOpsRoles.Operator, publishAudit.ActorRole);
        Assert.Equal(OperationAuditStatus.Succeeded, publishAudit.Status);

        var rollbackAudit = Assert.Single(
            audits,
            entry => entry.Action == "content.rollback" && entry.TargetId == version);
        Assert.Equal("local-supervisor", rollbackAudit.ActorId);
        Assert.Equal(OperationAuditStatus.Succeeded, rollbackAudit.Status);

        var grantAudit = Assert.Single(
            audits,
            entry => entry.Action == "wallet.grant" && entry.TargetId == playerId.ToString("D"));
        Assert.Equal("local-operator", grantAudit.ActorId);
        Assert.Equal(OperationAuditStatus.Succeeded, grantAudit.Status);

        var mailAudit = Assert.Single(
            audits,
            entry => entry.Action == "mail.incident.create" && entry.TargetId == mailId);
        Assert.Equal("local-operator", mailAudit.ActorId);

        var auditPayload = string.Join(
            '\n',
            audits.Select(entry => $"{entry.RequestSummary}\n{entry.ResultSummary}"));
        Assert.DoesNotContain(privateTitle, auditPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(privateBody, auditPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(privateTarget.ToString("D"), auditPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("client-hash-is-ignored", auditPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(
            supervisor.DefaultRequestHeaders.Authorization!.Parameter!,
            auditPayload,
            StringComparison.Ordinal);
    }

    private static PublishContentCommand PublishCommand(string version)
    {
        var now = DateTimeOffset.UtcNow;
        return new PublishContentCommand(
            version,
            [new GachaBannerConfigDto(
                "pickup-auth-e2e",
                CurrencyCode.Elif,
                100,
                90,
                now.AddMinutes(-1),
                now.AddMinutes(10),
                [new GachaRewardPoolEntryDto(
                    GachaRewardKind.Character,
                    "char-auth-e2e",
                    1,
                    3,
                    100,
                    true,
                    "memory-char-auth-e2e",
                    20)])],
            "authorization-e2e");
    }

}
