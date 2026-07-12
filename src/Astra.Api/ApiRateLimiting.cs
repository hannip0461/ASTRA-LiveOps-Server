using System.Globalization;
using System.Threading.RateLimiting;
using Astra.Contracts;
using Microsoft.AspNetCore.RateLimiting;

namespace Astra.Api;

public static class ApiRateLimitPolicies
{
    public const string DevAuthentication = "Api.DevAuthentication";
    public const string Read = "Api.Read";
    public const string Mutation = "Api.Mutation";
}

public sealed class ApiRateLimitOptions
{
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);

    public int DevAuthenticationPermitLimit { get; init; } = 20;

    public int ViewerReadPermitLimit { get; init; } = 120;

    public int OperatorReadPermitLimit { get; init; } = 180;

    public int SupervisorReadPermitLimit { get; init; } = 240;

    public int OperatorMutationPermitLimit { get; init; } = 30;

    public int SupervisorMutationPermitLimit { get; init; } = 60;
}

public static class ApiRateLimitingExtensions
{
    public static void AddApiRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = configuration.GetSection("Astra:RateLimits").Get<ApiRateLimitOptions>()
            ?? new ApiRateLimitOptions();
        Validate(settings);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(
                ApiRateLimitPolicies.DevAuthentication,
                context => FixedWindow(
                    $"dev-auth:{ClientAddress(context)}",
                    settings.DevAuthenticationPermitLimit,
                    settings.Window));
            options.AddPolicy(
                ApiRateLimitPolicies.Read,
                context => FixedWindow(
                    $"read:{ActorKey(context)}",
                    ReadPermitLimit(context, settings),
                    settings.Window));
            options.AddPolicy(
                ApiRateLimitPolicies.Mutation,
                context => FixedWindow(
                    $"mutation:{ActorKey(context)}",
                    MutationPermitLimit(context, settings),
                    settings.Window));
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = Math.Max(
                        1,
                        (int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);
                }

                await ApiProblemDetails.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status429TooManyRequests,
                    "rate_limited",
                    "Too many requests",
                    "The request rate limit has been exceeded.",
                    cancellationToken);
            };
        });
    }

    private static RateLimitPartition<string> FixedWindow(
        string partitionKey,
        int permitLimit,
        TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = permitLimit,
                QueueLimit = 0,
                Window = window
            });

    private static int ReadPermitLimit(HttpContext context, ApiRateLimitOptions settings) =>
        context.User.IsInRole(LiveOpsRoles.Supervisor)
            ? settings.SupervisorReadPermitLimit
            : context.User.IsInRole(LiveOpsRoles.Operator)
                ? settings.OperatorReadPermitLimit
                : settings.ViewerReadPermitLimit;

    private static int MutationPermitLimit(HttpContext context, ApiRateLimitOptions settings) =>
        context.User.IsInRole(LiveOpsRoles.Supervisor)
            ? settings.SupervisorMutationPermitLimit
            : settings.OperatorMutationPermitLimit;

    private static string ActorKey(HttpContext context) =>
        context.User.FindFirst("sub")?.Value ?? $"anonymous:{ClientAddress(context)}";

    private static string ClientAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static void Validate(ApiRateLimitOptions settings)
    {
        var limits = new[]
        {
            settings.DevAuthenticationPermitLimit,
            settings.ViewerReadPermitLimit,
            settings.OperatorReadPermitLimit,
            settings.SupervisorReadPermitLimit,
            settings.OperatorMutationPermitLimit,
            settings.SupervisorMutationPermitLimit
        };
        if (settings.Window < TimeSpan.FromSeconds(1) ||
            settings.Window > TimeSpan.FromHours(1) ||
            limits.Any(limit => limit <= 0) ||
            settings.ViewerReadPermitLimit > settings.OperatorReadPermitLimit ||
            settings.OperatorReadPermitLimit > settings.SupervisorReadPermitLimit ||
            settings.OperatorMutationPermitLimit > settings.SupervisorMutationPermitLimit)
        {
            throw new InvalidOperationException("Astra:RateLimits configuration is invalid.");
        }
    }
}
