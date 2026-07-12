using System.Net.Http.Json;
using Astra.Contracts;

namespace Astra.Admin.Services;

public sealed class OutboxAdminApiClient(AdminApiHttpClient httpClient)
{
    public async Task<OutboxOverviewDto> GetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            "/api/admin/outbox/overview",
            LiveOpsRoles.Viewer,
            cancellationToken);
        await AdminApiHttpClient.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<OutboxOverviewDto>(cancellationToken)
            ?? throw new InvalidOperationException("Outbox overview response was empty.");
    }

    public async Task<IReadOnlyList<OutboxDeadLetterDto>> GetDeadLettersAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/admin/outbox/dead-letters?limit={Math.Clamp(limit, 1, 200)}",
            LiveOpsRoles.Viewer,
            cancellationToken);
        await AdminApiHttpClient.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<OutboxDeadLetterDto[]>(cancellationToken) ?? [];
    }

    public async Task<OutboxReplayResultDto> ReplayAsync(
        Guid eventId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/admin/outbox/dead-letters/{eventId}/replay",
            new ReplayOutboxEventCommand(reason),
            LiveOpsRoles.Supervisor,
            cancellationToken);
        await AdminApiHttpClient.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<OutboxReplayResultDto>(cancellationToken)
            ?? throw new InvalidOperationException("Outbox replay response was empty.");
    }
}
