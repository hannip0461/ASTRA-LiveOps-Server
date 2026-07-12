using System.Net.Http.Headers;
using System.Net.Http.Json;
using Astra.Contracts;

namespace Astra.IntegrationTests;

internal static class ApiE2E
{
    public static HttpClient Client() => new()
    {
        BaseAddress = new Uri("http://localhost:5191")
    };

    public static async Task<HttpClient> AuthenticatedClientAsync(
        string operatorId,
        CancellationToken cancellationToken)
    {
        var client = Client();
        try
        {
            await AuthenticateAsync(client, operatorId, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task AuthenticateAsync(
        HttpClient client,
        string operatorId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/dev/auth/token")
        {
            Content = JsonContent.Create(new DevOperatorTokenRequest(operatorId))
        };
        request.Headers.TryAddWithoutValidation(DevAuthenticationHeaders.TokenKey, DevTokenKey());
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<DevOperatorTokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("API returned an empty development token.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    public static string DevTokenKey() =>
        Environment.GetEnvironmentVariable("ASTRA_DEV_TOKEN_KEY")
        ?? throw new InvalidOperationException("ASTRA_DEV_TOKEN_KEY is required for API E2E tests.");
}
