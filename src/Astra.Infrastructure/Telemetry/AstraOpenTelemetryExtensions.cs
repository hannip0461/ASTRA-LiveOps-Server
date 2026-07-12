using Astra.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Astra.Infrastructure.Telemetry;

public static class AstraOpenTelemetryExtensions
{
    public static IServiceCollection AddAstraOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        bool includeAspNetCore = false,
        bool includePostgres = false)
    {
        var section = configuration.GetSection("Astra:OpenTelemetry");
        if (!section.GetValue("Enabled", true))
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(AstraTelemetry.ActivitySourceName);

                if (includeAspNetCore)
                {
                    tracing.AddAspNetCoreInstrumentation();
                }

                if (includePostgres)
                {
                    tracing.AddNpgsql();
                }

                if (section.GetValue("ConsoleExporter", false))
                {
                    tracing.AddConsoleExporter();
                }

                var otlpEndpoint = section.GetValue<string>("OtlpEndpoint");
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(AstraTelemetry.MeterName);

                if (includeAspNetCore)
                {
                    metrics.AddAspNetCoreInstrumentation();
                }

                if (includePostgres)
                {
                    metrics.AddNpgsqlInstrumentation(_ => { });
                }

                if (section.GetValue("ConsoleExporter", false))
                {
                    metrics.AddConsoleExporter();
                }

                var otlpEndpoint = section.GetValue<string>("OtlpEndpoint");
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter((exporterOptions, readerOptions) =>
                    {
                        exporterOptions.Endpoint = new Uri(otlpEndpoint);
                        readerOptions.TemporalityPreference = MetricReaderTemporalityPreference.Delta;
                    });
                }
            });

        return services;
    }
}
