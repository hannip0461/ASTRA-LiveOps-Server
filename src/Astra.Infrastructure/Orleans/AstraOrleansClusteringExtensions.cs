using System.Net;
using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Astra.Infrastructure.Orleans;

public static class AstraOrleansClusteringExtensions
{
    private const string LocalhostProvider = "Localhost";
    private const string AdoNetProvider = "AdoNet";

    public static IClientBuilder UseAstraClustering(
        this IClientBuilder builder,
        IConfiguration configuration)
    {
        var provider = GetProvider(configuration);
        if (StringComparer.OrdinalIgnoreCase.Equals(provider, LocalhostProvider))
        {
            return builder.UseLocalhostClustering();
        }

        ConfigureClusterIdentity(builder, configuration);
        return builder.UseAdoNetClustering(options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = GetConnectionString(configuration);
        });
    }

    public static ISiloBuilder UseAstraClustering(
        this ISiloBuilder builder,
        IConfiguration configuration)
    {
        var provider = GetProvider(configuration);
        if (StringComparer.OrdinalIgnoreCase.Equals(provider, LocalhostProvider))
        {
            return builder.UseLocalhostClustering();
        }

        ConfigureClusterIdentity(builder, configuration);
        builder.Configure<EndpointOptions>(options =>
        {
            options.SiloPort = configuration.GetValue("Astra:Orleans:SiloPort", 11111);
            options.GatewayPort = configuration.GetValue("Astra:Orleans:GatewayPort", 30000);
            var advertisedAddress = configuration.GetValue<string>("Astra:Orleans:AdvertisedIPAddress");
            if (!IPAddress.TryParse(advertisedAddress, out var address))
            {
                throw new InvalidOperationException(
                    "Astra:Orleans:AdvertisedIPAddress must be an IP address when AdoNet clustering is enabled.");
            }

            options.AdvertisedIPAddress = address;
        });
        return builder.UseAdoNetClustering(options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = GetConnectionString(configuration);
        });
    }

    private static string GetProvider(IConfiguration configuration)
    {
        var provider = configuration.GetValue("Astra:Orleans:ClusterProvider", LocalhostProvider);
        if (StringComparer.OrdinalIgnoreCase.Equals(provider, LocalhostProvider) ||
            StringComparer.OrdinalIgnoreCase.Equals(provider, AdoNetProvider))
        {
            return provider;
        }

        throw new InvalidOperationException(
            "Astra:Orleans:ClusterProvider must be Localhost or AdoNet.");
    }

    private static string GetConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString("Orleans") ??
        configuration.GetConnectionString("Postgres") ??
        throw new InvalidOperationException(
            "ConnectionStrings:Orleans or ConnectionStrings:Postgres is required for AdoNet clustering.");

    private static void ConfigureClusterIdentity(
        IClientBuilder builder,
        IConfiguration configuration) =>
        builder.Configure<ClusterOptions>(options => SetClusterIdentity(options, configuration));

    private static void ConfigureClusterIdentity(
        ISiloBuilder builder,
        IConfiguration configuration) =>
        builder.Configure<ClusterOptions>(options => SetClusterIdentity(options, configuration));

    private static void SetClusterIdentity(
        ClusterOptions options,
        IConfiguration configuration)
    {
        options.ClusterId = configuration.GetValue("Astra:Orleans:ClusterId", "astra-liveops");
        options.ServiceId = configuration.GetValue("Astra:Orleans:ServiceId", "astra-liveops");
    }
}
