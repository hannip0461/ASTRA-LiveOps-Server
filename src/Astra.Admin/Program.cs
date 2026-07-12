using Astra.Admin.Components;
using Astra.Admin.Services;
using Microsoft.AspNetCore.Components.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
var apiBaseUrl = builder.Configuration.GetValue<string>("Astra:ApiBaseUrl") ?? "http://localhost:5191";
builder.Services.AddSingleton(TimeProvider.System);
var authOptions = builder.Services.AddAdminAuthentication(
    builder.Configuration,
    builder.Environment);
builder.Services.AddHttpClient("Astra.Api", client => client.BaseAddress = new Uri(apiBaseUrl));
builder.Services.AddScoped<AdminApiAccessTokenProvider>();
builder.Services.AddScoped<AdminApiHttpClient>();
builder.Services.AddScoped<ContentAdminApiClient>();
builder.Services.AddScoped<MailAdminApiClient>();
builder.Services.AddScoped<AuditAdminApiClient>();
builder.Services.AddScoped<OutboxAdminApiClient>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapAdminAuthenticationEndpoints(authOptions);
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
