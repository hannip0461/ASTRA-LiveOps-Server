using System.Net;
using Astra.Infrastructure.Telemetry;
using Astra.Infrastructure.Orleans;
using Astra.TcpGateway;
using Orleans.Hosting;
using Orleans.Serialization;

var host = Host.CreateDefaultBuilder(args)
    .UseOrleansClient((context, client) =>
        client.UseAstraClustering(context.Configuration))
    .ConfigureServices((context, services) =>
    {
        services.AddAstraOpenTelemetry(context.Configuration, "Astra.TcpGateway");
        services.Configure<ExceptionSerializationOptions>(
            options => options.SupportedNamespacePrefixes.Add("Astra.Domain"));
        services.AddOptions<TcpGatewayOptions>()
            .Bind(context.Configuration.GetSection(TcpGatewayOptions.SectionName))
            .Validate(
                options => IPAddress.TryParse(options.ListenAddress, out _),
                "Astra:TcpGateway:ListenAddress must be an IP address.")
            .Validate(
                options => options.Port is > 0 and <= 65_535,
                "Astra:TcpGateway:Port must be between 1 and 65535.")
            .Validate(
                options => options.Backlog > 0 &&
                           options.MaxConnections > 0 &&
                           options.MaxFrameBytes is >= 1_024 and <= 1_048_576 &&
                           options.MaxRequestsPerConnection > 0 &&
                           options.IdleTimeout > TimeSpan.Zero &&
                           options.CommandTimeout > TimeSpan.Zero &&
                           options.WriteTimeout > TimeSpan.Zero,
                "Astra TCP limits and timeouts must be positive and frame size must be 1 KiB to 1 MiB.")
            .ValidateOnStart();
        services.AddOptions<TcpSessionTokenOptions>()
            .Bind(context.Configuration.GetSection(TcpSessionTokenOptions.SectionName))
            .Validate(
                options => System.Text.Encoding.UTF8.GetByteCount(options.SigningKey) >= 32,
                "Astra:TcpSessionToken:SigningKey must contain at least 32 UTF-8 bytes.")
            .Validate(
                options => options.MaxLifetime > TimeSpan.Zero,
                "Astra:TcpSessionToken:MaxLifetime must be positive.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TcpSessionTokenService>();
        services.AddSingleton<ITcpPlayerService, OrleansTcpPlayerService>();
        services.AddSingleton<TcpRequestProcessor>();
        services.AddSingleton<TcpConnectionHandler>();
        services.AddHostedService<Worker>();
    })
    .Build();

host.Run();
