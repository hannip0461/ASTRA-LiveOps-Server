using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Astra.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Astra.Api;

public static class LiveOpsPolicies
{
    public const string Viewer = "LiveOps.Viewer";
    public const string Operator = "LiveOps.Operator";
    public const string Supervisor = "LiveOps.Supervisor";
}

public sealed class LiveOpsAuthOptions
{
    public string Authority { get; init; } = "";

    public string Issuer { get; init; } = "";

    public string Audience { get; init; } = "";

    public string SigningKey { get; init; } = "";

    public bool RequireHttpsMetadata { get; init; } = true;

    public string NameClaimType { get; init; } = JwtRegisteredClaimNames.Name;

    public string RoleClaimType { get; init; } = "role";

    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromMinutes(30);

    public bool DevTokenEnabled { get; init; }

    public string DevTokenKey { get; init; } = "";

    public IReadOnlyList<DevOperatorOptions> DevOperators { get; init; } = [];

    public bool UsesExternalAuthority => !string.IsNullOrWhiteSpace(Authority);
}

public sealed class DevOperatorOptions
{
    public string OperatorId { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string Role { get; init; } = "";
}

public sealed class DevOperatorTokenService(
    LiveOpsAuthOptions options,
    TimeProvider timeProvider)
{
    private readonly byte[] _signingKey = Encoding.UTF8.GetBytes(options.SigningKey);

    public DevOperatorTokenResponse Issue(string operatorId)
    {
        var configured = options.DevOperators.SingleOrDefault(
            candidate => StringComparer.Ordinal.Equals(candidate.OperatorId, operatorId));
        if (configured is null || !LiveOpsRoles.All.Contains(configured.Role))
        {
            throw new InvalidOperationException("Operator is not configured for development token issuance.");
        }

        var now = timeProvider.GetUtcNow();
        var expiresAt = now.Add(options.TokenLifetime);
        var claims = new[]
        {
            new System.Security.Claims.Claim(JwtRegisteredClaimNames.Sub, configured.OperatorId),
            new System.Security.Claims.Claim(options.NameClaimType, configured.DisplayName),
            new System.Security.Claims.Claim(options.RoleClaimType, configured.Role),
            new System.Security.Claims.Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        var token = new JwtSecurityToken(
            options.Issuer,
            options.Audience,
            claims,
            now.UtcDateTime,
            expiresAt.UtcDateTime,
            new SigningCredentials(
                new SymmetricSecurityKey(_signingKey),
                SecurityAlgorithms.HmacSha256));

        return new DevOperatorTokenResponse(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt,
            configured.OperatorId,
            configured.DisplayName,
            configured.Role);
    }
}

public static class LiveOpsAuthorizationExtensions
{
    public static LiveOpsAuthOptions AddLiveOpsAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Astra:LiveOpsAuth");
        var options = section.Get<LiveOpsAuthOptions>() ?? new LiveOpsAuthOptions();
        Validate(options);

        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        if (!options.UsesExternalAuthority)
        {
            services.AddSingleton<DevOperatorTokenService>();
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.SaveToken = false;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                if (options.UsesExternalAuthority)
                {
                    jwt.Authority = options.Authority;
                    jwt.Audience = options.Audience;
                    jwt.TokenValidationParameters = CommonValidationParameters(options);
                    return;
                }

                var validation = CommonValidationParameters(options);
                validation.ValidIssuer = options.Issuer;
                validation.IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(options.SigningKey));
                validation.ValidAlgorithms = [SecurityAlgorithms.HmacSha256];
                jwt.TokenValidationParameters = validation;
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(
                LiveOpsPolicies.Viewer,
                policy => policy.RequireRole(
                    LiveOpsRoles.Viewer,
                    LiveOpsRoles.Operator,
                    LiveOpsRoles.Supervisor))
            .AddPolicy(
                LiveOpsPolicies.Operator,
                policy => policy.RequireRole(LiveOpsRoles.Operator, LiveOpsRoles.Supervisor))
            .AddPolicy(
                LiveOpsPolicies.Supervisor,
                policy => policy.RequireRole(LiveOpsRoles.Supervisor));
        return options;
    }

    private static TokenValidationParameters CommonValidationParameters(LiveOpsAuthOptions options) => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = options.NameClaimType,
        RoleClaimType = options.RoleClaimType
    };

    private static void Validate(LiveOpsAuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Audience) ||
            string.IsNullOrWhiteSpace(options.NameClaimType) ||
            string.IsNullOrWhiteSpace(options.RoleClaimType))
        {
            throw new InvalidOperationException(
                "Astra:LiveOpsAuth audience, name claim type, and role claim type are required.");
        }

        if (options.UsesExternalAuthority)
        {
            if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) ||
                (authority.Scheme != Uri.UriSchemeHttps && authority.Scheme != Uri.UriSchemeHttp) ||
                (options.RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "Astra:LiveOpsAuth authority must be an absolute HTTPS URI.");
            }

            if (options.DevTokenEnabled)
            {
                throw new InvalidOperationException(
                    "Astra:LiveOpsAuth development token issuance cannot use an external authority.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.Issuer))
            {
                throw new InvalidOperationException("Astra:LiveOpsAuth issuer is required.");
            }

            if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
            {
                throw new InvalidOperationException(
                    "Astra:LiveOpsAuth signing key must contain at least 32 UTF-8 bytes.");
            }
        }

        if (options.TokenLifetime < TimeSpan.FromMinutes(1) ||
            options.TokenLifetime > TimeSpan.FromHours(8))
        {
            throw new InvalidOperationException("Astra:LiveOpsAuth token lifetime must be between 1 minute and 8 hours.");
        }

        if (options.DevTokenEnabled && Encoding.UTF8.GetByteCount(options.DevTokenKey) < 32)
        {
            throw new InvalidOperationException(
                "Astra:LiveOpsAuth development token key must contain at least 32 UTF-8 bytes.");
        }

        var operatorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configured in options.DevOperators)
        {
            if (string.IsNullOrWhiteSpace(configured.OperatorId) ||
                string.IsNullOrWhiteSpace(configured.DisplayName) ||
                !LiveOpsRoles.All.Contains(configured.Role) ||
                !operatorIds.Add(configured.OperatorId))
            {
                throw new InvalidOperationException("Astra:LiveOpsAuth contains an invalid development operator.");
            }
        }
    }
}
