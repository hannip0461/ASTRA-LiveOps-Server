using System.Net.Http.Json;
using Astra.Contracts;

namespace Astra.Admin.Services;

public sealed class ContentAdminApiClient(AdminApiHttpClient httpClient)
{
    public async Task<ContentSnapshotDto?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            "/api/admin/content/active",
            LiveOpsRoles.Viewer,
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ContentSnapshotDto>(cancellationToken);
    }

    public async Task<ContentPublishResult> PublishAsync(
        PublishContentCommand command,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/admin/content/publish",
            command,
            LiveOpsRoles.Operator,
            cancellationToken);
        return await ReadPublishResultAsync(response, cancellationToken);
    }

    public async Task<ContentPublishResult> RollbackAsync(
        string version,
        string reason,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/admin/content/rollback/{Uri.EscapeDataString(version)}",
            new RollbackContentCommand(reason),
            LiveOpsRoles.Supervisor,
            cancellationToken);
        return await ReadPublishResultAsync(response, cancellationToken);
    }

    private static async Task<ContentPublishResult> ReadPublishResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<ContentPublishResult>(cancellationToken)
                ?? EmptyResponse();
        }

        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemResponse>(cancellationToken);
            if (problem?.Errors.Count > 0)
            {
                return new ContentPublishResult(
                    false,
                    null,
                    problem.Errors
                        .SelectMany(pair => pair.Value.Select(message =>
                            new ContentValidationIssue(pair.Key, message)))
                        .ToArray());
            }
        }

        await AdminApiHttpClient.EnsureSuccessAsync(response, cancellationToken);
        return EmptyResponse();
    }

    private static ContentPublishResult EmptyResponse() => new(
        false,
        null,
        [new ContentValidationIssue("api.empty_response", "API returned an empty response.")]);

    private sealed class ValidationProblemResponse
    {
        public Dictionary<string, string[]> Errors { get; init; } = new(StringComparer.Ordinal);
    }
}
