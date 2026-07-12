using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Astra.TcpGateway;

internal sealed class Worker(
    IOptions<TcpGatewayOptions> options,
    TcpConnectionHandler connectionHandler,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly TcpGatewayOptions _options = options.Value;
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private long _nextConnectionId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var address = IPAddress.Parse(_options.ListenAddress);
        var listener = new TcpListener(address, _options.Port);
        using var connectionSlots = new SemaphoreSlim(_options.MaxConnections, _options.MaxConnections);
        listener.Start(_options.Backlog);
        logger.LogInformation(
            "ASTRA TCP gateway listening. Address={Address} Port={Port} MaxFrameBytes={MaxFrameBytes}",
            address,
            _options.Port,
            _options.MaxFrameBytes);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await connectionSlots.WaitAsync(stoppingToken);
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch
                {
                    connectionSlots.Release();
                    throw;
                }

                var connectionId = Interlocked.Increment(ref _nextConnectionId);
                var task = HandleClientAsync(client, connectionId, connectionSlots, stoppingToken);
                _connections[connectionId] = task;
                _ = task.ContinueWith(
                    completedTask => _connections.TryRemove(connectionId, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(_connections.Values);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        long connectionId,
        SemaphoreSlim connectionSlots,
        CancellationToken stoppingToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                using var scope = logger.BeginScope(new Dictionary<string, object>
                {
                    ["ConnectionId"] = connectionId,
                    ["RemoteEndpoint"] = remoteEndpoint
                });
                logger.LogInformation("TCP client connected.");
                await connectionHandler.HandleAsync(client.GetStream(), connectionId.ToString(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (TcpProtocolException exception)
            {
                logger.LogWarning(exception, "TCP response framing failed. ConnectionId={ConnectionId}", connectionId);
            }
            catch (IOException exception)
            {
                logger.LogDebug(exception, "TCP connection ended with an I/O error. ConnectionId={ConnectionId}", connectionId);
            }
            catch (SocketException exception)
            {
                logger.LogDebug(exception, "TCP connection ended with a socket error. ConnectionId={ConnectionId}", connectionId);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "TCP connection failed. ConnectionId={ConnectionId}", connectionId);
            }
            finally
            {
                connectionSlots.Release();
            }
        }
    }
}
