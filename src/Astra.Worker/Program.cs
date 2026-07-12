using Astra.Domain;
using Astra.Infrastructure.Postgres;
using Astra.Infrastructure.Telemetry;
using Astra.Worker;

var builder = Host.CreateApplicationBuilder(args);
var migrateOnly = args.Contains("--migrate-only", StringComparer.Ordinal);
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

builder.Services.AddAstraOpenTelemetry(
    builder.Configuration,
    "Astra.Worker",
    includePostgres: true);
builder.Services.AddSingleton(_ => PostgresDataSourceFactory.Create(
    builder.Configuration,
    connectionString,
    "Astra.Worker",
    defaultMaximumPoolSize: 8));
builder.Services.AddSingleton<PostgresSchemaInitializer>();
builder.Services.AddSingleton<IOutboxEventStore, PostgresOutboxEventStore>();
builder.Services.AddSingleton<IPersistenceMaintenanceStore, PostgresPersistenceMaintenanceStore>();
builder.Services.AddSingleton<IOutboxEventHandler, PostgresOperationalEventHandler>();
builder.Services.AddSingleton(TimeProvider.System);
var workerOptions = new OutboxWorkerOptions
{
    BatchSize = builder.Configuration.GetValue("Astra:Outbox:BatchSize", 20),
    PollInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("Astra:Outbox:PollIntervalSeconds", 2)),
    LeaseDuration = TimeSpan.FromSeconds(builder.Configuration.GetValue("Astra:Outbox:LeaseSeconds", 30)),
    MaxConcurrency = builder.Configuration.GetValue("Astra:Outbox:MaxConcurrency", 4)
};
workerOptions.Validate();
builder.Services.AddSingleton(workerOptions);
var retentionOptions = new PersistenceRetentionOptions
{
    PublishedOutboxRetention = builder.Configuration.GetValue(
        "Astra:PersistenceMaintenance:PublishedOutboxRetention",
        TimeSpan.FromDays(7)),
    OrphanDeliveryRetention = builder.Configuration.GetValue(
        "Astra:PersistenceMaintenance:OrphanDeliveryRetention",
        TimeSpan.FromDays(30)),
    ExpiredIdempotencyGrace = builder.Configuration.GetValue(
        "Astra:PersistenceMaintenance:ExpiredIdempotencyGrace",
        TimeSpan.FromHours(1)),
    CleanupInterval = builder.Configuration.GetValue(
        "Astra:PersistenceMaintenance:CleanupInterval",
        TimeSpan.FromHours(1)),
    CommandTimeout = builder.Configuration.GetValue(
        "Astra:PersistenceMaintenance:CommandTimeout",
        TimeSpan.FromSeconds(5)),
    BatchSize = builder.Configuration.GetValue("Astra:PersistenceMaintenance:BatchSize", 500),
    MaxBatchesPerCycle = builder.Configuration.GetValue("Astra:PersistenceMaintenance:MaxBatchesPerCycle", 20)
};
retentionOptions.Validate();
builder.Services.AddSingleton(retentionOptions);
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<PersistenceCleanupWorker>();

var host = builder.Build();
await host.Services.GetRequiredService<PostgresSchemaInitializer>().ApplyAsync();
if (!migrateOnly)
{
    await host.RunAsync();
}
