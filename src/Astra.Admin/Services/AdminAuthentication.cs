using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Astra.Contracts;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Astra.Admin.Services;

public sealed class AdminUiAuthOptions
{
    public bool DevSignInEnabled { get; init; }

    public string DevTokenKey { get; init; } = "";

    public IReadOnlyList<string> DevOperatorIds { get; init; } = [];

    public AdminOpenIdConnectOptions OpenIdConnect { get; init; } = new();
}

public sealed class AdminOpenIdConnectOptions
{
    public bool Enabled { get; init; }

    public string Authority { get; init; } = "";

    public string ClientId { get; init; } = "";

    public string ClientSecret { get; init; } = "";

    public string PublicOrigin { get; init; } = "";

    public string ApiScope { get; init; } = "";

    public bool RequireHttpsMetadata { get; init; } = true;

    public string NameClaimType { get; init; } = "name";

    public string RoleClaimType { get; init; } = "role";
}

public sealed class AdminSessionTokenStore(TimeProvider timeProvider)
{
    public const string SessionIdClaim = "astra_admin_session_id";

    private readonly ConcurrentDictionary<string, AdminApiSessionToken> _tokens =
        new(StringComparer.Ordinal);

    public string Create(DevOperatorTokenResponse token) => Create(
        token.AccessToken,
        token.ExpiresAtUtc,
        token.OperatorId,
        token.Role);

    public string CreateExternal(
        ClaimsPrincipal principal,
        string accessToken,
        DateTimeOffset expiresAtUtc)
    {
        var operatorId = FindOperatorId(principal);
        var role = HighestRole(principal);
        if (string.IsNullOrWhiteSpace(operatorId) || role is null)
        {
            throw new UnauthorizedAccessException(
                "The identity provider did not issue a supported operator identity and role.");
        }

        return Create(accessToken, expiresAtUtc, operatorId, role);
    }

    private string Create(
        string accessToken,
        DateTimeOffset expiresAtUtc,
        string operatorId,
        string role)
    {
        if (string.IsNullOrWhiteSpace(accessToken) ||
            expiresAtUtc <= timeProvider.GetUtcNow() ||
            string.IsNullOrWhiteSpace(operatorId) ||
            !LiveOpsRoles.All.Contains(role))
        {
            throw new InvalidOperationException("Cannot create an Admin session from an invalid API token.");
        }

        RemoveExpired();
        var sessionId = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var token = new AdminApiSessionToken(accessToken, expiresAtUtc, operatorId, role);
        if (!_tokens.TryAdd(sessionId, token))
        {
            throw new InvalidOperationException("Failed to allocate a unique Admin session.");
        }

        return sessionId;
    }

    public string GetAccessToken(ClaimsPrincipal principal, string requiredRole)
    {
        if (principal.Identity?.IsAuthenticated != true || !HasRequiredRole(principal, requiredRole))
        {
            throw new UnauthorizedAccessException("The Admin session lacks the required role.");
        }

        var sessionId = principal.FindFirst(SessionIdClaim)?.Value;
        var operatorId = FindOperatorId(principal);
        var role = HighestRole(principal);
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !_tokens.TryGetValue(sessionId, out var token) ||
            token.ExpiresAtUtc <= timeProvider.GetUtcNow() ||
            !StringComparer.Ordinal.Equals(token.OperatorId, operatorId) ||
            !StringComparer.Ordinal.Equals(token.Role, role))
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _tokens.TryRemove(sessionId, out _);
            }

            throw new UnauthorizedAccessException("The Admin session is missing, expired, or inconsistent.");
        }

        return token.AccessToken;
    }

    public void Remove(ClaimsPrincipal principal)
    {
        var sessionId = principal.FindFirst(SessionIdClaim)?.Value;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _tokens.TryRemove(sessionId, out _);
        }
    }

    private static bool HasRequiredRole(ClaimsPrincipal principal, string requiredRole) => requiredRole switch
    {
        LiveOpsRoles.Viewer =>
            principal.IsInRole(LiveOpsRoles.Viewer) ||
            principal.IsInRole(LiveOpsRoles.Operator) ||
            principal.IsInRole(LiveOpsRoles.Supervisor),
        LiveOpsRoles.Operator =>
            principal.IsInRole(LiveOpsRoles.Operator) ||
            principal.IsInRole(LiveOpsRoles.Supervisor),
        LiveOpsRoles.Supervisor => principal.IsInRole(LiveOpsRoles.Supervisor),
        _ => false
    };

    private static string? HighestRole(ClaimsPrincipal principal)
    {
        if (principal.IsInRole(LiveOpsRoles.Supervisor))
        {
            return LiveOpsRoles.Supervisor;
        }

        if (principal.IsInRole(LiveOpsRoles.Operator))
        {
            return LiveOpsRoles.Operator;
        }

        return principal.IsInRole(LiveOpsRoles.Viewer) ? LiveOpsRoles.Viewer : null;
    }

    private static string? FindOperatorId(ClaimsPrincipal principal) =>
        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
        principal.FindFirst("sub")?.Value;

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var pair in _tokens)
        {
            if (pair.Value.ExpiresAtUtc <= now)
            {
                _tokens.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record AdminApiSessionToken(
        string AccessToken,
        DateTimeOffset ExpiresAtUtc,
        string OperatorId,
        string Role);
}

public sealed class DevAdminAuthenticator(
    IHttpClientFactory httpClientFactory,
    AdminUiAuthOptions options)
{
    public async Task<DevOperatorTokenResponse> AuthenticateAsync(
        string operatorId,
        CancellationToken cancellationToken)
    {
        if (!options.DevOperatorIds.Contains(operatorId, StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException("The development operator is not configured for Admin sign-in.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/dev/auth/token")
        {
            Content = JsonContent.Create(new DevOperatorTokenRequest(operatorId))
        };
        request.Headers.TryAddWithoutValidation(DevAuthenticationHeaders.TokenKey, options.DevTokenKey);

        var client = httpClientFactory.CreateClient("Astra.Api");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<DevOperatorTokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("API returned an empty development token.");
        if (!StringComparer.Ordinal.Equals(token.OperatorId, operatorId) ||
            !LiveOpsRoles.All.Contains(token.Role))
        {
            throw new InvalidOperationException("API token identity does not match the sign-in request.");
        }

        return token;
    }
}

public static class AdminAuthentication
{
    public const string OpenIdConnectScheme = "Astra.Admin.OpenIdConnect";

    public static AdminUiAuthOptions AddAdminAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration.GetSection("Astra:UiAuth").Get<AdminUiAuthOptions>()
            ?? new AdminUiAuthOptions();
        Validate(options, environment);

        services.AddSingleton(options);
        services.AddSingleton<AdminSessionTokenStore>();
        services.AddSingleton<DevAdminAuthenticator>();
        var authentication = services.AddAuthentication(authenticationOptions =>
        {
            authenticationOptions.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            authenticationOptions.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            authenticationOptions.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            authenticationOptions.DefaultChallengeScheme = options.OpenIdConnect.Enabled
                ? OpenIdConnectScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
        });
        authentication.AddCookie(cookie =>
        {
            cookie.Cookie.Name = "Astra.Admin.Session";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.SecurePolicy = environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            cookie.LoginPath = "/sign-in";
            cookie.AccessDeniedPath = "/access-denied";
            cookie.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            cookie.SlidingExpiration = false;
        });

        if (options.OpenIdConnect.Enabled)
        {
            var configured = options.OpenIdConnect;
            authentication.AddOpenIdConnect(OpenIdConnectScheme, oidc =>
            {
                oidc.Authority = configured.Authority;
                oidc.ClientId = configured.ClientId;
                oidc.ClientSecret = configured.ClientSecret;
                oidc.RequireHttpsMetadata = configured.RequireHttpsMetadata;
                oidc.ResponseType = OpenIdConnectResponseType.Code;
                oidc.UsePkce = true;
                oidc.MapInboundClaims = false;
                oidc.SaveTokens = true;
                oidc.Scope.Clear();
                oidc.Scope.Add(OpenIdConnectScope.OpenId);
                oidc.Scope.Add(OpenIdConnectScope.Profile);
                oidc.Scope.Add(configured.ApiScope);
                oidc.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = configured.NameClaimType,
                    RoleClaimType = configured.RoleClaimType
                };
                oidc.Events = new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = context =>
                    {
                        context.ProtocolMessage.RedirectUri = ExternalUri(
                            configured.PublicOrigin,
                            context.Options.CallbackPath);
                        return Task.CompletedTask;
                    },
                    OnRedirectToIdentityProviderForSignOut = context =>
                    {
                        context.ProtocolMessage.PostLogoutRedirectUri = ExternalUri(
                            configured.PublicOrigin,
                            context.Options.SignedOutCallbackPath);
                        return Task.CompletedTask;
                    },
                    OnTicketReceived = CaptureExternalSessionAsync
                };
            });
        }

        services.AddAuthorization();
        return options;
    }

    public static void MapAdminAuthenticationEndpoints(
        this WebApplication app,
        AdminUiAuthOptions options)
    {
        if (app.Environment.IsDevelopment() && options.DevSignInEnabled)
        {
            app.MapPost("/auth/dev-sign-in", SignInAsync).AllowAnonymous();
        }

        if (options.OpenIdConnect.Enabled)
        {
            app.MapGet("/auth/sign-in", ChallengeExternalIdentity).AllowAnonymous();
        }

        app.MapPost("/auth/sign-out", SignOutAsync).RequireAuthorization();
    }

    private static IResult ChallengeExternalIdentity(string? returnUrl) => Results.Challenge(
        new AuthenticationProperties
        {
            AllowRefresh = false,
            IsPersistent = false,
            RedirectUri = LocalReturnUrl(returnUrl ?? "")
        },
        [OpenIdConnectScheme]);

    private static async Task<IResult> SignInAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        DevAdminAuthenticator authenticator,
        AdminSessionTokenStore sessionStore)
    {
        if (!IsDirectLoopbackRequest(context) || !await HasValidAntiforgeryTokenAsync(context, antiforgery))
        {
            return Results.NotFound();
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var operatorId = form["operatorId"].ToString();
        var returnUrl = LocalReturnUrl(form["returnUrl"].ToString());
        try
        {
            var token = await authenticator.AuthenticateAsync(operatorId, context.RequestAborted);
            var sessionId = sessionStore.Create(token);
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, token.OperatorId),
                new Claim(ClaimTypes.Name, token.DisplayName),
                new Claim(ClaimTypes.Role, token.Role),
                new Claim(AdminSessionTokenStore.SessionIdClaim, sessionId)
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    AllowRefresh = false,
                    IsPersistent = false,
                    ExpiresUtc = token.ExpiresAtUtc,
                    RedirectUri = returnUrl
                });
            return Results.LocalRedirect(returnUrl);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> SignOutAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        AdminSessionTokenStore sessionStore,
        AdminUiAuthOptions options)
    {
        if (!await HasValidAntiforgeryTokenAsync(context, antiforgery))
        {
            return Results.BadRequest();
        }

        sessionStore.Remove(context.User);
        var schemes = options.OpenIdConnect.Enabled
            ? new[] { CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectScheme }
            : [CookieAuthenticationDefaults.AuthenticationScheme];
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/sign-in" },
            schemes);
    }

    private static Task CaptureExternalSessionAsync(TicketReceivedContext context)
    {
        var properties = context.Properties;
        if (properties is null || context.Principal?.Identity is not ClaimsIdentity identity)
        {
            context.Fail("The identity provider response did not contain a usable API token.");
            return Task.CompletedTask;
        }

        var accessToken = properties.GetTokenValue("access_token");
        var expiresAtValue = properties.GetTokenValue("expires_at");
        if (
            string.IsNullOrWhiteSpace(accessToken) ||
            !DateTimeOffset.TryParse(
                expiresAtValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAtUtc))
        {
            context.Fail("The identity provider response did not contain a usable API token.");
            return Task.CompletedTask;
        }

        try
        {
            var sessionStore = context.HttpContext.RequestServices
                .GetRequiredService<AdminSessionTokenStore>();
            var sessionId = sessionStore.CreateExternal(context.Principal, accessToken, expiresAtUtc);
            identity.AddClaim(new Claim(AdminSessionTokenStore.SessionIdClaim, sessionId));
            properties.AllowRefresh = false;
            properties.ExpiresUtc = expiresAtUtc;
            properties.IsPersistent = false;

            var idToken = properties.GetTokenValue("id_token");
            properties.StoreTokens(string.IsNullOrWhiteSpace(idToken)
                ? []
                : [new AuthenticationToken { Name = "id_token", Value = idToken }]);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or InvalidOperationException)
        {
            context.Fail(exception.Message);
        }

        return Task.CompletedTask;
    }

    private static bool IsDirectLoopbackRequest(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } address ||
            !IPAddress.IsLoopback(address))
        {
            return false;
        }

        return !context.Request.Headers.ContainsKey("Forwarded") &&
               !context.Request.Headers.ContainsKey("X-Forwarded-For") &&
               !context.Request.Headers.ContainsKey("X-Real-IP");
    }

    private static string LocalReturnUrl(string value) =>
        value.StartsWith("/", StringComparison.Ordinal) &&
        !value.StartsWith("//", StringComparison.Ordinal) &&
        !value.StartsWith("/\\", StringComparison.Ordinal)
            ? value
            : "/";

    private static string ExternalUri(string publicOrigin, PathString path) =>
        new Uri(
            new Uri(publicOrigin.TrimEnd('/') + "/"),
            path.Value?.TrimStart('/') ?? "").AbsoluteUri;

    private static async Task<bool> HasValidAntiforgeryTokenAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static void Validate(AdminUiAuthOptions options, IHostEnvironment environment)
    {
        if (options.DevSignInEnabled && options.OpenIdConnect.Enabled)
        {
            throw new InvalidOperationException(
                "Development Admin sign-in and OpenID Connect cannot be enabled together.");
        }

        if (options.OpenIdConnect.Enabled)
        {
            var configured = options.OpenIdConnect;
            if (!Uri.TryCreate(configured.Authority, UriKind.Absolute, out var authority) ||
                (authority.Scheme != Uri.UriSchemeHttps && authority.Scheme != Uri.UriSchemeHttp) ||
                (configured.RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps) ||
                (!configured.RequireHttpsMetadata && !environment.IsDevelopment()) ||
                !Uri.TryCreate(configured.PublicOrigin, UriKind.Absolute, out var publicOrigin) ||
                publicOrigin.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(publicOrigin.PathAndQuery.Trim('/')) ||
                !string.IsNullOrEmpty(publicOrigin.Fragment) ||
                string.IsNullOrWhiteSpace(configured.ClientId) ||
                string.IsNullOrWhiteSpace(configured.ClientSecret) ||
                string.IsNullOrWhiteSpace(configured.ApiScope) ||
                string.IsNullOrWhiteSpace(configured.NameClaimType) ||
                string.IsNullOrWhiteSpace(configured.RoleClaimType))
            {
                throw new InvalidOperationException(
                    "Astra:UiAuth OpenID Connect configuration is invalid.");
            }

            return;
        }

        if (!options.DevSignInEnabled)
        {
            return;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Development Admin sign-in can run only in Development.");
        }

        if (Encoding.UTF8.GetByteCount(options.DevTokenKey) < 32 ||
            options.DevOperatorIds.Count == 0 ||
            options.DevOperatorIds.Any(string.IsNullOrWhiteSpace) ||
            options.DevOperatorIds.Distinct(StringComparer.Ordinal).Count() != options.DevOperatorIds.Count)
        {
            throw new InvalidOperationException("Astra:UiAuth development sign-in configuration is invalid.");
        }
    }
}
