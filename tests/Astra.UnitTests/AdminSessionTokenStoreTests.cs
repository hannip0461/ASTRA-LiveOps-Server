using System.Security.Claims;
using Astra.Admin.Services;
using Astra.Contracts;

namespace Astra.UnitTests;

public sealed class AdminSessionTokenStoreTests
{
    [Fact]
    public void GetAccessToken_UsesMatchingSessionAndRoleHierarchy()
    {
        var now = new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var store = new AdminSessionTokenStore(clock);
        var token = Token(now.AddMinutes(10), LiveOpsRoles.Operator);
        var principal = Principal(store.Create(token), token);

        Assert.Equal(token.AccessToken, store.GetAccessToken(principal, LiveOpsRoles.Viewer));
        Assert.Equal(token.AccessToken, store.GetAccessToken(principal, LiveOpsRoles.Operator));
        Assert.Throws<UnauthorizedAccessException>(
            () => store.GetAccessToken(principal, LiveOpsRoles.Supervisor));
    }

    [Fact]
    public void GetAccessToken_RejectsExpiredOrMismatchedSession()
    {
        var now = new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var store = new AdminSessionTokenStore(clock);
        var token = Token(now.AddMinutes(1), LiveOpsRoles.Supervisor);
        var principal = Principal(store.Create(token), token);

        clock.UtcNow = now.AddMinutes(2);

        Assert.Throws<UnauthorizedAccessException>(
            () => store.GetAccessToken(principal, LiveOpsRoles.Viewer));
    }

    [Fact]
    public void CreateExternal_SupportsSubjectAndConfiguredRoleClaimType()
    {
        var now = new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);
        var store = new AdminSessionTokenStore(new ManualTimeProvider(now));
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", "oidc-operator"),
            new Claim("roles", LiveOpsRoles.Supervisor)
        ],
        "oidc",
        "name",
        "roles");
        var principal = new ClaimsPrincipal(identity);

        var sessionId = store.CreateExternal(principal, "external-access-token", now.AddMinutes(5));
        identity.AddClaim(new Claim(AdminSessionTokenStore.SessionIdClaim, sessionId));

        Assert.Equal(
            "external-access-token",
            store.GetAccessToken(principal, LiveOpsRoles.Supervisor));
    }

    private static DevOperatorTokenResponse Token(DateTimeOffset expiresAt, string role) =>
        new("signed-token", expiresAt, "operator-a", "Operator A", role);

    private static ClaimsPrincipal Principal(string sessionId, DevOperatorTokenResponse token) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, token.OperatorId),
            new Claim(ClaimTypes.Name, token.DisplayName),
            new Claim(ClaimTypes.Role, token.Role),
            new Claim(AdminSessionTokenStore.SessionIdClaim, sessionId)
        ], "test"));

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
