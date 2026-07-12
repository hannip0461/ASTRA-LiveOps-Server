using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Astra.Contracts;
using Astra.Domain;

namespace Astra.Api;

public sealed record AdminAuditCommand(
    string Action,
    string TargetType,
    string TargetId,
    string Reason,
    object RequestSummary);

public sealed record AdminAuditOutcome<T>(
    T Value,
    OperationAuditStatus Status,
    object? ResultSummary = null,
    string? ErrorCode = null);

public sealed class AdminAuditExecutor(
    IOperationAuditStore store,
    TimeProvider timeProvider,
    ILogger<AdminAuditExecutor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<T> ExecuteAsync<T>(
        HttpContext httpContext,
        AdminAuditCommand command,
        Func<Task<AdminAuditOutcome<T>>> operation)
    {
        var actor = ReadActor(httpContext.User);
        var auditId = Guid.NewGuid();
        var startedAt = timeProvider.GetUtcNow();
        await store.StartAsync(
            new OperationAuditStart(
                auditId,
                Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier,
                actor.ActorId,
                actor.DisplayName,
                actor.Role,
                command.Action,
                command.TargetType,
                command.TargetId,
                command.Reason,
                Serialize(command.RequestSummary),
                httpContext.Connection.RemoteIpAddress?.ToString(),
                startedAt),
            httpContext.RequestAborted);

        try
        {
            var outcome = await operation();
            await store.CompleteAsync(
                new OperationAuditCompletion(
                    auditId,
                    outcome.Status,
                    outcome.ResultSummary is null ? null : Serialize(outcome.ResultSummary),
                    outcome.ErrorCode,
                    timeProvider.GetUtcNow()),
                CancellationToken.None);
            return outcome.Value;
        }
        catch (Exception exception)
        {
            try
            {
                await store.CompleteAsync(
                    new OperationAuditCompletion(
                        auditId,
                        OperationAuditStatus.Failed,
                        null,
                        ApiExceptionHandler.Describe(exception).Code,
                        timeProvider.GetUtcNow()),
                    CancellationToken.None);
            }
            catch (Exception auditException)
            {
                logger.LogError(
                    auditException,
                    "Failed to mark operation audit {AuditId} as failed.",
                    auditId);
            }

            throw;
        }
    }

    private static AuditActor ReadActor(ClaimsPrincipal principal)
    {
        var identity = principal.Identities.FirstOrDefault(candidate => candidate.IsAuthenticated);
        var actorId = identity?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var displayName = identity?.Name;
        var role = identity?.FindFirst(identity.RoleClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(displayName) ||
            string.IsNullOrWhiteSpace(role))
        {
            throw new InvalidOperationException("Authenticated LiveOps actor claims are incomplete.");
        }

        return new AuditActor(actorId, displayName, role);
    }

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, value.GetType(), JsonOptions);

    private sealed record AuditActor(string ActorId, string DisplayName, string Role);
}
