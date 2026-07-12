namespace Astra.TcpGateway;

internal sealed class TcpGatewayOptions
{
    public const string SectionName = "Astra:TcpGateway";

    public string ListenAddress { get; init; } = "0.0.0.0";

    public int Port { get; init; } = 5300;

    public int Backlog { get; init; } = 512;

    public int MaxConnections { get; init; } = 1_024;

    public int MaxFrameBytes { get; init; } = 64 * 1_024;

    public int MaxRequestsPerConnection { get; init; } = 1_000;

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

internal sealed class TcpSessionTokenOptions
{
    public const string SectionName = "Astra:TcpSessionToken";

    public string SigningKey { get; init; } = "";

    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromHours(24);
}
