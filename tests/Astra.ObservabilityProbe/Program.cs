using System.Diagnostics;
using System.Text.Json;
using Astra.Domain;
using Astra.ObservabilityProbe;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var endpoint = new Uri(
    Environment.GetEnvironmentVariable("ASTRA_OTLP_ENDPOINT")
    ?? "http://127.0.0.1:4317");
var simulatePoolTimeout = args.Contains("--simulate-pool-timeout", StringComparer.Ordinal);
var saturatePostgres = args.Contains("--saturate-postgres", StringComparer.Ordinal);
var exerciseOutbox = args.Contains("--exercise-outbox", StringComparer.Ordinal);
var probeId = Guid.NewGuid().ToString("N");
var resource = ResourceBuilder.CreateDefault().AddService(
    "Astra.ObservabilityProbe",
    serviceInstanceId: probeId);

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource)
    .AddSource(AstraTelemetry.ActivitySourceName)
    .AddOtlpExporter(options =>
    {
        options.Endpoint = endpoint;
        options.Protocol = OtlpExportProtocol.Grpc;
    })
    .Build();
using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter(AstraTelemetry.MeterName)
    .AddOtlpExporter((options, readerOptions) =>
    {
        options.Endpoint = endpoint;
        options.Protocol = OtlpExportProtocol.Grpc;
        readerOptions.TemporalityPreference = MetricReaderTemporalityPreference.Delta;
    })
    .Build();

var probeCounter = AstraTelemetry.Meter.CreateCounter<long>("astra.observability.probe");
var poolFailureCounter = AstraTelemetry.Meter.CreateCounter<long>(
    "astra.postgres.connection.acquire.failures",
    "{failure}");
PoolSaturationResult? poolSaturation = null;
OutboxDeliveryResult? outboxDelivery = null;

using (var activity = AstraTelemetry.ActivitySource.StartActivity(
    saturatePostgres || exerciseOutbox ? "observability.operational" : "observability.e2e",
    ActivityKind.Internal))
{
    activity?.SetTag("astra.probe_id", probeId);
    activity?.SetStatus(ActivityStatusCode.Ok);
    probeCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));

    if (simulatePoolTimeout)
    {
        poolFailureCounter.Add(
            1,
            new KeyValuePair<string, object?>("reason", "timeout"),
            new KeyValuePair<string, object?>("synthetic", true));
    }

    if (saturatePostgres || exerciseOutbox)
    {
        var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";
        if (saturatePostgres)
        {
            poolSaturation = await OperationalScenarios.SaturatePoolAsync(connectionString);
        }

        if (exerciseOutbox)
        {
            outboxDelivery = await OperationalScenarios.ExerciseOutboxAsync(
                connectionString,
                TimeSpan.FromSeconds(30));
        }
    }
}

var tracesFlushed = tracerProvider.ForceFlush(5_000);
var metricsFlushed = meterProvider.ForceFlush(5_000);
if (!tracesFlushed || !metricsFlushed)
{
    throw new InvalidOperationException("OTLP probe flush timed out.");
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    probeId,
    poolTimeout = simulatePoolTimeout,
    poolSaturation,
    outboxDelivery
}));
