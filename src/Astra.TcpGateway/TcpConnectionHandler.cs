using System.Diagnostics;
using System.Diagnostics.Metrics;
using Astra.Contracts.Tcp;
using Astra.Domain;
using Microsoft.Extensions.Options;

namespace Astra.TcpGateway;

internal sealed class TcpConnectionHandler(
    IOptions<TcpGatewayOptions> options,
    TcpRequestProcessor requestProcessor,
    ILogger<TcpConnectionHandler> logger)
{
    private readonly TcpGatewayOptions _options = options.Value;

    public async Task HandleAsync(
        Stream stream,
        string connectionId,
        CancellationToken stoppingToken)
    {
        var session = new TcpSessionContext();
        for (var requestCount = 0; requestCount < _options.MaxRequestsPerConnection; requestCount++)
        {
            RequestEnvelope? request;
            try
            {
                using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                idleTimeout.CancelAfter(_options.IdleTimeout);
                request = await TcpFrameCodec.ReadAsync(
                    stream,
                    RequestEnvelope.Parser,
                    _options.MaxFrameBytes,
                    idleTimeout.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("TCP connection idle timeout. ConnectionId={ConnectionId}", connectionId);
                return;
            }
            catch (TcpProtocolException exception)
            {
                logger.LogWarning(
                    "TCP protocol error. ConnectionId={ConnectionId} Error={Error}",
                    connectionId,
                    exception.Message);
                return;
            }
            catch (EndOfStreamException)
            {
                logger.LogDebug("TCP peer closed a partial frame. ConnectionId={ConnectionId}", connectionId);
                return;
            }

            if (request is null)
            {
                return;
            }

            var requestStartedAt = Stopwatch.GetTimestamp();
            ResponseEnvelope response;
            try
            {
                using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                commandTimeout.CancelAfter(_options.CommandTimeout);
                response = await requestProcessor.ProcessAsync(request, session, commandTimeout.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                response = new ResponseEnvelope
                {
                    RequestId = request.RequestId,
                    SessionId = session.SessionId,
                    Status = ResponseStatus.FailedPrecondition,
                    ProtocolVersion = 1,
                    ErrorCode = "request_timeout",
                    ErrorMessage = "The request exceeded the server timeout."
                };
            }

            if (response.CalculateSize() > _options.MaxFrameBytes)
            {
                logger.LogWarning(
                    "TCP response exceeds frame limit. ConnectionId={ConnectionId} RequestId={RequestId}",
                    connectionId,
                    request.RequestId);
                response = new ResponseEnvelope
                {
                    RequestId = request.RequestId,
                    SessionId = session.SessionId,
                    Status = ResponseStatus.FailedPrecondition,
                    ProtocolVersion = 1,
                    ErrorCode = "response_too_large",
                    ErrorMessage = "The response exceeds the configured frame limit."
                };
            }

            try
            {
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                writeTimeout.CancelAfter(_options.WriteTimeout);
                await TcpFrameCodec.WriteAsync(stream, response, _options.MaxFrameBytes, writeTimeout.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                TcpServerMetrics.Record(request, "transport_error", requestStartedAt);
                logger.LogInformation("TCP response write timeout. ConnectionId={ConnectionId}", connectionId);
                return;
            }
            catch
            {
                TcpServerMetrics.Record(request, "transport_error", requestStartedAt);
                throw;
            }

            TcpServerMetrics.Record(request, Classify(response), requestStartedAt);
        }

        logger.LogInformation(
            "TCP request limit reached; closing connection. ConnectionId={ConnectionId} Limit={Limit}",
            connectionId,
            _options.MaxRequestsPerConnection);
    }

    private static string Classify(ResponseEnvelope response) => response.ErrorCode switch
    {
        "request_timeout" => "timeout",
        "response_too_large" => "server_error",
        _ when response.Status == ResponseStatus.InternalError => "server_error",
        _ when response.Status == ResponseStatus.Ok => "success",
        _ => "rejected"
    };
}

internal static class TcpServerMetrics
{
    private static readonly Counter<long> Requests = AstraTelemetry.Meter.CreateCounter<long>(
        "astra.tcp.server.requests",
        "{request}");

    private static readonly Histogram<double> RequestDuration = AstraTelemetry.Meter.CreateHistogram<double>(
        "astra.tcp.server.request.duration",
        "s");

    public static void Record(RequestEnvelope request, string outcome, long startedAt)
    {
        var tags = new TagList
        {
            { "rpc.method", request.CommandCase.ToString() },
            { "outcome", outcome }
        };
        Requests.Add(1, tags);
        RequestDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds, tags);
    }
}
