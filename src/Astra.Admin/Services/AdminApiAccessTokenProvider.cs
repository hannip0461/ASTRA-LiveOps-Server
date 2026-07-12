using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Authorization;

namespace Astra.Admin.Services;

public sealed class AdminApiAccessTokenProvider(
    AuthenticationStateProvider authenticationStateProvider,
    AdminSessionTokenStore sessionStore)
{
    public async Task<string> GetAsync(string requiredRole, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync();
        return sessionStore.GetAccessToken(authenticationState.User, requiredRole);
    }
}

public sealed class AdminApiHttpClient(
    IHttpClientFactory httpClientFactory,
    AdminApiAccessTokenProvider tokenProvider)
{
    public Task<HttpResponseMessage> GetAsync(
        string requestUri,
        string requiredRole,
        CancellationToken cancellationToken = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, requestUri), requiredRole, cancellationToken);

    public Task<HttpResponseMessage> PostAsJsonAsync<T>(
        string requestUri,
        T value,
        string requiredRole,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = JsonContent.Create(value) },
            requiredRole,
            cancellationToken);

    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ApiProblemResponse>(cancellationToken);
            if (problem is not null)
            {
                var fieldMessages = problem.Errors
                    .SelectMany(pair => pair.Value.Select(message => $"{pair.Key}: {message}"))
                    .Take(5)
                    .ToArray();
                var message = fieldMessages.Length > 0
                    ? string.Join("; ", fieldMessages)
                    : string.IsNullOrWhiteSpace(problem.Detail)
                        ? problem.Title
                        : problem.Detail;
                throw new HttpRequestException(
                    $"{problem.Code ?? "api_error"}: {message ?? "API request failed."}",
                    null,
                    response.StatusCode);
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string requiredRole,
        CancellationToken cancellationToken)
    {
        using (request)
        {
            var token = await tokenProvider.GetAsync(requiredRole, cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var client = httpClientFactory.CreateClient("Astra.Api");
            return await client.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ApiProblemResponse
    {
        public string? Title { get; init; }

        public string? Detail { get; init; }

        public string? Code { get; init; }

        public Dictionary<string, string[]> Errors { get; init; } = new(StringComparer.Ordinal);
    }
}
