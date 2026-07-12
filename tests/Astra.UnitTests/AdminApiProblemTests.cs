using System.Net;
using System.Net.Http.Json;
using Astra.Admin.Services;

namespace Astra.UnitTests;

public sealed class AdminApiProblemTests
{
    [Fact]
    public async Task EnsureSuccessAsync_IncludesValidationFieldMessages()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new
            {
                title = "Request validation failed",
                detail = "One or more request fields are invalid.",
                code = "validation_failed",
                errors = new Dictionary<string, string[]>
                {
                    ["mailId"] = ["Value contains unsupported characters."]
                }
            })
        };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => AdminApiHttpClient.EnsureSuccessAsync(response));

        Assert.Contains("validation_failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("mailId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unsupported characters", exception.Message, StringComparison.Ordinal);
    }
}
