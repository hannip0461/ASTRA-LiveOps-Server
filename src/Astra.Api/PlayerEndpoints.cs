using Astra.Contracts;
using Astra.Domain;

namespace Astra.Api;

public static class PlayerEndpoints
{
    private const string HttpSimulatorReason = "authenticated-http-simulator";

    public static void MapPlayerEndpoints(this WebApplication app)
    {
        var players = app.MapGroup("/api/players")
            .RequireAuthorization(LiveOpsPolicies.Viewer);

        players.MapGet("/{playerId:guid}/wallet", async (
            Guid playerId,
            HttpContext httpContext,
            IClusterClient clusterClient) =>
        {
            if (playerId == Guid.Empty)
            {
                var errors = new ValidationErrors();
                errors.Add("playerId", "Player ID must not be an empty GUID.");
                return EndpointValidation.Invalid(httpContext, errors);
            }

            var grain = clusterClient.GetGrain<IPlayerAccountGrain>(playerId);
            return Results.Ok(await grain.GetSnapshotAsync());
        }).RequireRateLimiting(ApiRateLimitPolicies.Read);

        players.MapPost("/{playerId:guid}/wallet/grant", GrantCurrencyAsync)
            .RequireAuthorization(LiveOpsPolicies.Operator)
            .RequireRateLimiting(ApiRateLimitPolicies.Mutation);
        players.MapPost("/{playerId:guid}/wallet/spend", SpendCurrencyAsync)
            .RequireAuthorization(LiveOpsPolicies.Operator)
            .RequireRateLimiting(ApiRateLimitPolicies.Mutation);
        players.MapPost("/{playerId:guid}/gacha/draw", DrawGachaAsync)
            .RequireAuthorization(LiveOpsPolicies.Operator)
            .RequireRateLimiting(ApiRateLimitPolicies.Mutation);
        players.MapPost("/{playerId:guid}/mail/claim", ClaimMailAsync)
            .RequireAuthorization(LiveOpsPolicies.Operator)
            .RequireRateLimiting(ApiRateLimitPolicies.Mutation);
    }

    private static async Task<IResult> GrantCurrencyAsync(
        Guid playerId,
        GrantCurrencyCommand command,
        HttpContext httpContext,
        IClusterClient clusterClient,
        AdminAuditExecutor audit)
    {
        var errors = EndpointValidation.CurrencyCommand(
            command.Currency,
            command.Amount,
            command.Reason,
            command.IdempotencyKey);
        ValidatePlayerId(playerId, errors);
        if (errors.Any)
        {
            return EndpointValidation.Invalid(httpContext, errors);
        }

        var reason = command.Reason.Trim();
        command = command with
        {
            Reason = reason,
            IdempotencyKey = command.IdempotencyKey.Trim(),
            RequestHash = PlayerRequestHash.GrantCurrency(
                playerId,
                command.Currency,
                command.Amount,
                reason)
        };
        var grain = clusterClient.GetGrain<IPlayerAccountGrain>(playerId);
        var result = await audit.ExecuteAsync(
            httpContext,
            PlayerAuditCommand(
                "wallet.grant",
                playerId,
                reason,
                new { playerId, command.Currency, command.Amount, command.IdempotencyKey }),
            async () => ReceiptOutcome(await grain.GrantCurrencyAsync(command)));
        return Results.Ok(result);
    }

    private static async Task<IResult> SpendCurrencyAsync(
        Guid playerId,
        SpendCurrencyCommand command,
        HttpContext httpContext,
        IClusterClient clusterClient,
        AdminAuditExecutor audit)
    {
        var errors = EndpointValidation.CurrencyCommand(
            command.Currency,
            command.Amount,
            command.Reason,
            command.IdempotencyKey);
        ValidatePlayerId(playerId, errors);
        if (errors.Any)
        {
            return EndpointValidation.Invalid(httpContext, errors);
        }

        var reason = command.Reason.Trim();
        command = command with
        {
            Reason = reason,
            IdempotencyKey = command.IdempotencyKey.Trim(),
            RequestHash = PlayerRequestHash.SpendCurrency(
                playerId,
                command.Currency,
                command.Amount,
                reason)
        };
        var grain = clusterClient.GetGrain<IPlayerAccountGrain>(playerId);
        var result = await audit.ExecuteAsync(
            httpContext,
            PlayerAuditCommand(
                "wallet.spend",
                playerId,
                reason,
                new { playerId, command.Currency, command.Amount, command.IdempotencyKey }),
            async () => ReceiptOutcome(await grain.SpendCurrencyAsync(command)));
        return Results.Ok(result);
    }

    private static async Task<IResult> DrawGachaAsync(
        Guid playerId,
        DrawGachaRequest request,
        HttpContext httpContext,
        IClusterClient clusterClient,
        AdminAuditExecutor audit)
    {
        var errors = EndpointValidation.DrawGacha(request);
        ValidatePlayerId(playerId, errors);
        if (errors.Any)
        {
            return EndpointValidation.Invalid(httpContext, errors);
        }

        var bannerId = request.BannerId.Trim();
        request = request with
        {
            BannerId = bannerId,
            IdempotencyKey = request.IdempotencyKey.Trim(),
            RequestHash = PlayerRequestHash.DrawGacha(playerId, bannerId, request.DrawCount)
        };
        var grain = clusterClient.GetGrain<IPlayerAccountGrain>(playerId);
        var result = await audit.ExecuteAsync(
            httpContext,
            PlayerAuditCommand(
                "gacha.draw",
                playerId,
                HttpSimulatorReason,
                new { playerId, bannerId, request.DrawCount, request.IdempotencyKey }),
            async () => ReceiptOutcome(await grain.DrawGachaAsync(request)));
        return Results.Ok(result);
    }

    private static async Task<IResult> ClaimMailAsync(
        Guid playerId,
        ClaimMailCommand command,
        HttpContext httpContext,
        IClusterClient clusterClient,
        AdminAuditExecutor audit)
    {
        var errors = EndpointValidation.ClaimMail(command);
        ValidatePlayerId(playerId, errors);
        if (errors.Any)
        {
            return EndpointValidation.Invalid(httpContext, errors);
        }

        var mailId = command.MailId.Trim();
        command = command with
        {
            MailId = mailId,
            IdempotencyKey = command.IdempotencyKey.Trim(),
            RequestHash = PlayerRequestHash.ClaimMail(playerId, mailId)
        };
        var grain = clusterClient.GetGrain<IPlayerAccountGrain>(playerId);
        var result = await audit.ExecuteAsync(
            httpContext,
            PlayerAuditCommand(
                "mail.claim",
                playerId,
                HttpSimulatorReason,
                new { playerId, mailId, command.IdempotencyKey }),
            async () => ReceiptOutcome(await grain.ClaimMailAsync(command)));
        return Results.Ok(result);
    }

    private static AdminAuditCommand PlayerAuditCommand(
        string action,
        Guid playerId,
        string reason,
        object summary) =>
        new(action, "player", playerId.ToString("D"), reason, summary);

    private static AdminAuditOutcome<PlayerCommandReceipt> ReceiptOutcome(PlayerCommandReceipt receipt) =>
        new(
            receipt,
            OperationAuditStatus.Succeeded,
            new { receipt.Replayed, receipt.Snapshot.LedgerVersion });

    private static void ValidatePlayerId(Guid playerId, ValidationErrors errors)
    {
        if (playerId == Guid.Empty)
        {
            errors.Add("playerId", "Player ID must not be an empty GUID.");
        }
    }
}
