using Astra.Api;
using Astra.Domain;
using Astra.Infrastructure;
using Astra.Infrastructure.Postgres;
using Astra.Infrastructure.Orleans;
using Astra.Infrastructure.Telemetry;
using Orleans.Hosting;
using Orleans.Serialization;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var maxRequestBodyBytes = builder.Configuration.GetValue(
    "Astra:Api:MaxRequestBodyBytes",
    1_048_576L);
if (maxRequestBodyBytes is < 1_024 or > 10_485_760)
{
    throw new InvalidOperationException(
        "Astra:Api:MaxRequestBodyBytes must be between 1 KiB and 10 MiB.");
}

builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = maxRequestBodyBytes);

builder.Host.UseOrleansClient((context, client) =>
    client.UseAstraClustering(context.Configuration));
builder.Services.Configure<ExceptionSerializationOptions>(
    options => options.SupportedNamespacePrefixes.Add("Astra.Domain"));
builder.Services.AddOpenApi();
builder.Services.AddAstraOpenTelemetry(
    builder.Configuration,
    "Astra.Api",
    includeAspNetCore: true,
    includePostgres: true);
builder.Services.AddApiProblemDetails();
builder.Services.AddApiRateLimiting(builder.Configuration);
var authOptions = builder.Services.AddLiveOpsAuthorization(builder.Configuration);
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
builder.Services.AddSingleton(_ => PostgresDataSourceFactory.Create(
    builder.Configuration,
    connectionString,
    "Astra.Api",
    defaultMaximumPoolSize: 8));
builder.Services.AddSingleton<IOutboxOperationsStore, PostgresOutboxOperationsStore>();

var auditStoreProvider = builder.Configuration.GetValue("Astra:Audit:StoreProvider", "InMemory");
if (StringComparer.OrdinalIgnoreCase.Equals(auditStoreProvider, "PostgreSQL"))
{
    builder.Services.AddSingleton<IOperationAuditStore, PostgresOperationAuditStore>();
}
else
{
    builder.Services.AddSingleton<IOperationAuditStore, InMemoryOperationAuditStore>();
}

builder.Services.AddSingleton<AdminAuditExecutor>();

var app = builder.Build();
_ = app.Services.GetRequiredService<IOutboxOperationsStore>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseStatusCodePages(statusCodeContext =>
    ApiProblemDetails.WriteStatusCodeAsync(statusCodeContext.HttpContext));
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/health", Liveness);
app.MapGet("/health/live", Liveness);
app.MapGet("/health/ready", CheckReadinessAsync);
app.MapDevOperatorTokenEndpoint(authOptions);
app.MapLiveOpsAdminEndpoints();
app.MapPlayerEndpoints();

app.Run();

static IResult Liveness() =>
    Results.Ok(new { status = "ok", service = "Astra.Api" });

static async Task<IResult> CheckReadinessAsync(
    NpgsqlDataSource dataSource,
    CancellationToken cancellationToken)
{
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        command.CommandTimeout = 2;
        await command.ExecuteScalarAsync(cancellationToken);
        return Results.Ok(new { status = "ready", service = "Astra.Api" });
    }
    catch (Exception)
    {
        return Results.Json(
            new { status = "unavailable", service = "Astra.Api" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

public partial class Program;
