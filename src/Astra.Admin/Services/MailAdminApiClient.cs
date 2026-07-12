using System.Net.Http.Json;
using Astra.Contracts;

namespace Astra.Admin.Services;

public sealed class MailAdminApiClient(AdminApiHttpClient httpClient)
{
    public async Task<MailDefinitionDto> CreateIncidentMailAsync(
        CreateIncidentMailCommand command,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/admin/mail/incident",
            command,
            LiveOpsRoles.Operator,
            cancellationToken);
        await AdminApiHttpClient.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MailDefinitionDto>(cancellationToken)
            ?? throw new InvalidOperationException("API returned an empty mail definition.");
    }

    public async Task<MailDefinitionDto?> GetMailAsync(
        string mailId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/admin/mail/{Uri.EscapeDataString(mailId)}",
            LiveOpsRoles.Viewer,
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await AdminApiHttpClient.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MailDefinitionDto>(cancellationToken);
    }

    public async Task<MailTargetCheckDto> CheckTargetAsync(
        string mailId,
        Guid playerId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/admin/mail/{Uri.EscapeDataString(mailId)}/targets/{playerId}",
            LiveOpsRoles.Viewer,
            cancellationToken);
        await AdminApiHttpClient.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MailTargetCheckDto>(cancellationToken)
            ?? new MailTargetCheckDto(mailId, playerId, false);
    }

    public async Task<PlayerCommandReceipt> ClaimMailAsync(
        Guid playerId,
        ClaimMailCommand command,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/players/{playerId}/mail/claim",
            command,
            LiveOpsRoles.Operator,
            cancellationToken);
        await AdminApiHttpClient.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PlayerCommandReceipt>(cancellationToken)
            ?? throw new InvalidOperationException("API returned an empty claim receipt.");
    }
}

public sealed record MailTargetCheckDto(string MailId, Guid PlayerId, bool Targeted);
