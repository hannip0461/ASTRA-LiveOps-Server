using System.Net.Http.Json;
using Astra.Contracts;

namespace Astra.Admin.Services;

public sealed class AuditAdminApiClient(AdminApiHttpClient httpClient)
{
    public async Task<IReadOnlyList<OperationAuditDto>> GetRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/admin/audit?limit={Math.Clamp(limit, 1, 200)}",
            LiveOpsRoles.Viewer,
            cancellationToken);
        await AdminApiHttpClient.EnsureSuccessAsync(response, cancellationToken);
        var entries = await response.Content.ReadFromJsonAsync<OperationAuditDto[]>(cancellationToken);
        return entries ?? [];
    }
}
