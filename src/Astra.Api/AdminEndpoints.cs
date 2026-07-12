using Astra.Contracts;
using Astra.Domain;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Astra.Api;

public static class AdminEndpoints
{
    public static void MapDevOperatorTokenEndpoint(
        this WebApplication app,
        LiveOpsAuthOptions authOptions)
    {
        if (!app.Environment.IsDevelopment() || !authOptions.DevTokenEnabled)
        {
            return;
        }

        app.MapPost("/api/dev/auth/token", IResult (
            DevOperatorTokenRequest request,
            HttpContext httpContext,
            DevOperatorTokenService tokenService) =>
        {
            if (!IsDirectLoopbackRequest(httpContext) ||
                !HasValidDevTokenKey(httpContext, authOptions.DevTokenKey))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(tokenService.Issue(request.OperatorId));
            }
            catch (InvalidOperationException)
            {
                return Results.Unauthorized();
            }
        })
            .AllowAnonymous()
            .RequireRateLimiting(ApiRateLimitPolicies.DevAuthentication);
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

    private static bool HasValidDevTokenKey(HttpContext context, string expected)
    {
        var supplied = context.Request.Headers[DevAuthenticationHeaders.TokenKey].ToString();
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    public static void MapLiveOpsAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin")
            .RequireAuthorization(LiveOpsPolicies.Viewer);

        admin.MapGet("/content/active", async (IClusterClient clusterClient) =>
            {
                var grain = clusterClient.GetGrain<IEventConfigGrain>("global");
                var snapshot = await grain.GetActiveSnapshotAsync();
                return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
            })
            .RequireRateLimiting(ApiRateLimitPolicies.Read);

        admin.MapPost("/content/publish", PublishContentAsync)
            .RequireAuthorization(LiveOpsPolicies.Operator)
            .RequireRateLimiting(ApiRateLimitPolicies.Mutation);

        admin.MapPost("/content/rollback/{version}", RollbackContentAsync)
            .RequireAuthorization(LiveOpsPolicies.Supervisor)
            .RequireRateLimiting(ApiRateLimitPolicies.Mutation);

        admin.MapPost("/mail/incident", CreateIncidentMailAsync)
            .RequireAuthorization(LiveOpsPolicies.Operator)
            .RequireRateLimiting(ApiRateLimitPolicies.Mutation);

        admin.MapGet("/mail/{mailId}", async (
                string mailId,
                HttpContext httpContext,
                IClusterClient clusterClient) =>
            {
                var errors = EndpointValidation.Identifier("mailId", mailId);
                if (errors.Any)
                {
                    return EndpointValidation.Invalid(httpContext, errors);
                }

                var grain = clusterClient.GetGrain<IMailboxGrain>("global");
                var definition = await grain.GetDefinitionAsync(mailId.Trim());
                return definition is null ? Results.NotFound() : Results.Ok(definition);
            })
            .RequireRateLimiting(ApiRateLimitPolicies.Read);

        admin.MapGet("/mail/{mailId}/targets/{playerId:guid}", async (
            string mailId,
            Guid playerId,
            HttpContext httpContext,
            IClusterClient clusterClient) =>
        {
            var errors = EndpointValidation.Identifier("mailId", mailId);
            if (errors.Any)
            {
                return EndpointValidation.Invalid(httpContext, errors);
            }

            var grain = clusterClient.GetGrain<IMailboxGrain>("global");
            return Results.Ok(new
            {
                mailId = mailId.Trim(),
                playerId,
                targeted = await grain.IsTargetAsync(mailId.Trim(), playerId)
            });
        }).RequireRateLimiting(ApiRateLimitPolicies.Read);

        admin.MapGet("/audit", async (
            int? limit,
            HttpContext httpContext,
            IOperationAuditStore auditStore,
            CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.AuditLimit(limit);
            return errors.Any
                ? EndpointValidation.Invalid(httpContext, errors)
                : Results.Ok(await auditStore.ListRecentAsync(limit ?? 50, cancellationToken));
        }).RequireRateLimiting(ApiRateLimitPolicies.Read);

        admin.MapGet("/outbox/overview", async (
            IOutboxOperationsStore outboxStore,
            CancellationToken cancellationToken) =>
                Results.Ok(await outboxStore.GetOverviewAsync(cancellationToken)))
            .RequireRateLimiting(ApiRateLimitPolicies.Read);

        admin.MapGet("/outbox/dead-letters", async (
            int? limit,
            HttpContext httpContext,
            IOutboxOperationsStore outboxStore,
            CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.AuditLimit(limit);
            return errors.Any
                ? EndpointValidation.Invalid(httpContext, errors)
                : Results.Ok(await outboxStore.ListDeadLettersAsync(limit ?? 50, cancellationToken));
        }).RequireRateLimiting(ApiRateLimitPolicies.Read);

        admin.MapPost("/outbox/dead-letters/{eventId:guid}/replay", ReplayOutboxAsync)
            .RequireAuthorization(LiveOpsPolicies.Supervisor)
            .RequireRateLimiting(ApiRateLimitPolicies.Mutation);
    }

    private static async Task<IResult> ReplayOutboxAsync(
        Guid eventId,
        ReplayOutboxEventCommand? command,
        HttpContext httpContext,
        IOutboxOperationsStore outboxStore,
        AdminAuditExecutor audit)
    {
        var errors = EndpointValidation.ReplayOutbox(eventId, command);
        if (errors.Any)
        {
            return EndpointValidation.Invalid(httpContext, errors);
        }

        var reason = command!.Reason.Trim();
        var replayed = await audit.ExecuteAsync<OutboxReplayResultDto?>(
            httpContext,
            new AdminAuditCommand(
                "outbox.dead_letter.replay",
                "outbox-event",
                eventId.ToString(),
                reason,
                new { eventId }),
            async () =>
            {
                var result = await outboxStore.ReplayDeadLetterAsync(
                    eventId,
                    httpContext.RequestAborted);
                return new AdminAuditOutcome<OutboxReplayResultDto?>(
                    result,
                    result is null
                        ? OperationAuditStatus.Rejected
                        : OperationAuditStatus.Succeeded,
                    result is null
                        ? new { eventId, replayed = false }
                        : new { eventId, replayed = true, result.ManualReplayCount },
                    result is null ? "outbox_dead_letter_not_found" : null);
            });

        return replayed is null ? Results.NotFound() : Results.Ok(replayed);
    }

    private static async Task<IResult> PublishContentAsync(
        PublishContentCommand command,
        HttpContext httpContext,
        IClusterClient clusterClient,
        AdminAuditExecutor audit)
    {
        var errors = EndpointValidation.PublishContent(command);
        if (errors.Any)
        {
            return EndpointValidation.Invalid(httpContext, errors);
        }

        command = EndpointValidation.Normalize(command);
        var reason = command.Reason;
        var grain = clusterClient.GetGrain<IEventConfigGrain>("global");
        var result = await audit.ExecuteAsync(
            httpContext,
            new AdminAuditCommand(
                "content.publish",
                "content-version",
                command.Version.Trim(),
                reason,
                new
                {
                    version = command.Version.Trim(),
                    bannerCount = command.GachaBanners.Count,
                    bannerIds = command.GachaBanners.Select(banner => banner.BannerId).Order().ToArray()
                }),
            async () =>
            {
                var publishResult = await grain.PublishAsync(command);
                return new AdminAuditOutcome<ContentPublishResult>(
                    publishResult,
                    publishResult.Published
                        ? OperationAuditStatus.Succeeded
                        : OperationAuditStatus.Rejected,
                    new
                    {
                        publishResult.Published,
                        version = publishResult.Snapshot?.Version,
                        checksum = publishResult.Snapshot?.Checksum,
                        issueCodes = publishResult.Issues.Select(issue => issue.Code).ToArray()
                    },
                    publishResult.Published ? null : publishResult.Issues.FirstOrDefault()?.Code);
            });
        return result.Published
            ? Results.Ok(result)
            : EndpointValidation.ContentRejected(httpContext, result.Issues);
    }

    private static async Task<IResult> RollbackContentAsync(
        string version,
        RollbackContentCommand command,
        HttpContext httpContext,
        IClusterClient clusterClient,
        AdminAuditExecutor audit)
    {
        var errors = EndpointValidation.RollbackContent(version, command);
        if (errors.Any)
        {
            return EndpointValidation.Invalid(httpContext, errors);
        }

        var normalizedVersion = version.Trim();
        var reason = command.Reason.Trim();
        var grain = clusterClient.GetGrain<IEventConfigGrain>("global");
        var result = await audit.ExecuteAsync(
            httpContext,
            new AdminAuditCommand(
                "content.rollback",
                "content-version",
                normalizedVersion,
                reason,
                new { version = normalizedVersion }),
            async () =>
            {
                var rollbackResult = await grain.RollbackAsync(normalizedVersion);
                return new AdminAuditOutcome<ContentPublishResult>(
                    rollbackResult,
                    rollbackResult.Published
                        ? OperationAuditStatus.Succeeded
                        : OperationAuditStatus.Rejected,
                    new
                    {
                        rollbackResult.Published,
                        version = rollbackResult.Snapshot?.Version,
                        checksum = rollbackResult.Snapshot?.Checksum,
                        issueCodes = rollbackResult.Issues.Select(issue => issue.Code).ToArray()
                    },
                    rollbackResult.Published ? null : rollbackResult.Issues.FirstOrDefault()?.Code);
            });
        return result.Published
            ? Results.Ok(result)
            : EndpointValidation.ContentRejected(httpContext, result.Issues);
    }

    private static async Task<IResult> CreateIncidentMailAsync(
        CreateIncidentMailCommand command,
        HttpContext httpContext,
        IClusterClient clusterClient,
        AdminAuditExecutor audit)
    {
        var errors = EndpointValidation.IncidentMail(command);
        if (errors.Any)
        {
            return EndpointValidation.Invalid(httpContext, errors);
        }

        command = EndpointValidation.Normalize(command);
        var reason = command.Reason;
        var grain = clusterClient.GetGrain<IMailboxGrain>("global");
        var result = await audit.ExecuteAsync(
            httpContext,
            new AdminAuditCommand(
                "mail.incident.create",
                "mail",
                command.MailId.Trim(),
                reason,
                new
                {
                    incidentId = command.IncidentId.Trim(),
                    mailId = command.MailId.Trim(),
                    targetCount = command.TargetPlayerIds.Distinct().Count(),
                    rewards = command.Rewards.Select(reward => new { reward.Currency, reward.Amount }).ToArray()
                }),
            async () =>
            {
                var definition = await grain.CreateIncidentMailAsync(command);
                return new AdminAuditOutcome<MailDefinitionDto>(
                    definition,
                    OperationAuditStatus.Succeeded,
                    new { definition.IncidentId, definition.MailId, definition.CreatedAt });
            });
        return Results.Ok(result);
    }
}
