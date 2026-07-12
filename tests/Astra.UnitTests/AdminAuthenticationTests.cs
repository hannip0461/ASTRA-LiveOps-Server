using Astra.Admin.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Astra.UnitTests;

public sealed class AdminAuthenticationTests
{
    [Fact]
    public void AddAdminAuthentication_WithOidc_AcceptsConfidentialCodeFlowConfiguration()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Astra:UiAuth:OpenIdConnect:Enabled"] = "true",
            ["Astra:UiAuth:OpenIdConnect:Authority"] = "https://identity.example.test/tenant",
            ["Astra:UiAuth:OpenIdConnect:ClientId"] = "astra-admin",
            ["Astra:UiAuth:OpenIdConnect:ClientSecret"] = "test-secret",
            ["Astra:UiAuth:OpenIdConnect:PublicOrigin"] = "https://admin.example.test",
            ["Astra:UiAuth:OpenIdConnect:ApiScope"] = "astra-api/.default",
            ["Astra:UiAuth:OpenIdConnect:RoleClaimType"] = "roles"
        });

        var services = new ServiceCollection();
        var options = services.AddAdminAuthentication(
            configuration,
            new TestHostEnvironment(Environments.Production));
        using var provider = services.BuildServiceProvider();
        var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AdminAuthentication.OpenIdConnectScheme);

        Assert.True(options.OpenIdConnect.Enabled);
        Assert.Equal("roles", options.OpenIdConnect.RoleClaimType);
        Assert.Equal(OpenIdConnectResponseType.Code, oidc.ResponseType);
        Assert.True(oidc.UsePkce);
        Assert.Contains("astra-api/.default", oidc.Scope);
    }

    [Fact]
    public void AddAdminAuthentication_RejectsInsecureOidcMetadataInProduction()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Astra:UiAuth:OpenIdConnect:Enabled"] = "true",
            ["Astra:UiAuth:OpenIdConnect:Authority"] = "http://identity.example.test/tenant",
            ["Astra:UiAuth:OpenIdConnect:ClientId"] = "astra-admin",
            ["Astra:UiAuth:OpenIdConnect:ClientSecret"] = "test-secret",
            ["Astra:UiAuth:OpenIdConnect:PublicOrigin"] = "https://admin.example.test",
            ["Astra:UiAuth:OpenIdConnect:ApiScope"] = "astra-api/.default",
            ["Astra:UiAuth:OpenIdConnect:RequireHttpsMetadata"] = "false"
        });

        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddAdminAuthentication(
                configuration,
                new TestHostEnvironment(Environments.Production)));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Astra.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
