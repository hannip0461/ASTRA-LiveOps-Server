using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Astra.Api;
using Astra.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Astra.UnitTests;

public sealed class LiveOpsAuthorizationTests
{
    private const string SigningKey = "unit-test-liveops-jwt-signing-key-2026";

    [Fact]
    public void Issue_WithConfiguredOperator_CreatesSignedRoleToken()
    {
        var options = Options();
        var response = new DevOperatorTokenService(options, TimeProvider.System).Issue("operator-a");
        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(
            response.AccessToken,
            ValidationParameters(options),
            out var validatedToken);

        Assert.IsType<JwtSecurityToken>(validatedToken);
        Assert.Equal("operator-a", principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value);
        Assert.Equal("Operator A", principal.FindFirst(JwtRegisteredClaimNames.Name)?.Value);
        Assert.True(principal.IsInRole(LiveOpsRoles.Operator));
        Assert.Equal(LiveOpsRoles.Operator, response.Role);
    }

    [Fact]
    public void Issue_WithCustomClaimTypes_UsesConfiguredIdentityMapping()
    {
        var options = Options("preferred_username", "roles");
        var response = new DevOperatorTokenService(options, TimeProvider.System).Issue("operator-a");
        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(
            response.AccessToken,
            ValidationParameters(options),
            out _);

        Assert.Equal("Operator A", principal.Identity?.Name);
        Assert.Equal("Operator A", principal.FindFirst(options.NameClaimType)?.Value);
        Assert.True(principal.IsInRole(LiveOpsRoles.Operator));
    }

    [Fact]
    public void Issue_WithUnknownOperator_RejectsTokenIssuance()
    {
        var service = new DevOperatorTokenService(Options(), TimeProvider.System);

        Assert.Throws<InvalidOperationException>(() => service.Issue("unknown"));
    }

    [Fact]
    public void AddLiveOpsAuthorization_WithExternalAuthority_DoesNotRequireSigningKey()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Astra:LiveOpsAuth:Authority"] = "https://identity.example.test/tenant",
            ["Astra:LiveOpsAuth:Audience"] = "astra-api",
            ["Astra:LiveOpsAuth:RoleClaimType"] = "roles"
        });

        var services = new ServiceCollection();
        var options = services.AddLiveOpsAuthorization(configuration);
        using var provider = services.BuildServiceProvider();
        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.True(options.UsesExternalAuthority);
        Assert.Equal("roles", options.RoleClaimType);
        Assert.Equal("https://identity.example.test/tenant", jwt.Authority);
        Assert.Null(jwt.TokenValidationParameters.IssuerSigningKey);
    }

    [Fact]
    public void AddLiveOpsAuthorization_WithExternalAuthority_RejectsDevIssuer()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Astra:LiveOpsAuth:Authority"] = "https://identity.example.test/tenant",
            ["Astra:LiveOpsAuth:Audience"] = "astra-api",
            ["Astra:LiveOpsAuth:DevTokenEnabled"] = "true"
        });

        Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddLiveOpsAuthorization(configuration));
    }

    private static LiveOpsAuthOptions Options(
        string nameClaimType = JwtRegisteredClaimNames.Name,
        string roleClaimType = "role") => new()
    {
        Issuer = "unit-test-issuer",
        Audience = "unit-test-audience",
        SigningKey = SigningKey,
        TokenLifetime = TimeSpan.FromMinutes(10),
        DevTokenEnabled = true,
        DevTokenKey = "unit-test-development-token-key-2026",
        NameClaimType = nameClaimType,
        RoleClaimType = roleClaimType,
        DevOperators =
        [
            new DevOperatorOptions
            {
                OperatorId = "operator-a",
                DisplayName = "Operator A",
                Role = LiveOpsRoles.Operator
            }
        ]
    };

    private static TokenValidationParameters ValidationParameters(LiveOpsAuthOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        NameClaimType = options.NameClaimType,
        RoleClaimType = options.RoleClaimType
    };

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
