using System.Diagnostics;
using System.Text.Json;
using Astra.Contracts;
using Astra.Contracts.Tcp;
using Astra.Domain;

namespace Astra.TcpGateway;

internal sealed class TcpRequestProcessor(
    TcpSessionTokenService tokenService,
    ITcpPlayerService playerService,
    TimeProvider timeProvider,
    ILogger<TcpRequestProcessor> logger)
{
    private const uint ProtocolVersion = 1;
    private const int MaxRequestIdLength = 64;
    private const int MaxSessionIdLength = 64;
    private const int MaxIdempotencyKeyLength = 128;
    private const int MaxBannerIdLength = 128;
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ResponseEnvelope> ProcessAsync(
        RequestEnvelope request,
        TcpSessionContext session,
        CancellationToken cancellationToken)
    {
        var requestId = IsIdentifier(request.RequestId, MaxRequestIdLength) ? request.RequestId : "";
        using var activity = StartActivity(request);
        activity?.SetTag("server.address", "astra-tcp");
        activity?.SetTag("rpc.system", "astra.protobuf");
        activity?.SetTag("rpc.method", request.CommandCase.ToString());
        activity?.SetTag("astra.request_id", requestId);

        ResponseEnvelope response;
        try
        {
            if (!IsIdentifier(request.RequestId, MaxRequestIdLength))
            {
                response = Error(requestId, session.SessionId, ResponseStatus.InvalidRequest, "request_id_invalid", "Request id contains unsupported characters or exceeds 64 characters.");
            }
            else if (request.ProtocolVersion != ProtocolVersion)
            {
                response = Error(requestId, session.SessionId, ResponseStatus.InvalidRequest, "protocol_version_unsupported", "Protocol version 1 is required.");
            }
            else
            {
                response = request.CommandCase switch
                {
                    RequestEnvelope.CommandOneofCase.BindSession => Bind(request, session),
                    RequestEnvelope.CommandOneofCase.GetWallet => await GetWalletAsync(request, session, cancellationToken),
                    RequestEnvelope.CommandOneofCase.DrawGacha => await DrawGachaAsync(request, session, cancellationToken),
                    _ => Error(requestId, session.SessionId, ResponseStatus.InvalidRequest, "command_required", "A command payload is required.")
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IdempotencyConflictException exception)
        {
            response = DomainError(
                request,
                session,
                ResponseStatus.Conflict,
                "idempotency_conflict",
                "The idempotency key was already used for a different command.",
                exception);
        }
        catch (InvalidAccountCommandException exception)
        {
            response = DomainError(
                request,
                session,
                ResponseStatus.InvalidRequest,
                "command_invalid",
                "The command violates a domain rule.",
                exception);
        }
        catch (InsufficientCurrencyException exception)
        {
            response = DomainError(
                request,
                session,
                ResponseStatus.FailedPrecondition,
                "insufficient_currency",
                "The account does not have enough currency for this command.",
                exception);
        }
        catch (ContentUnavailableException exception)
        {
            response = DomainError(
                request,
                session,
                ResponseStatus.FailedPrecondition,
                "content_unavailable",
                "No active content snapshot can serve this command.",
                exception);
        }
        catch (ContentMismatchException exception)
        {
            response = DomainError(
                request,
                session,
                ResponseStatus.FailedPrecondition,
                "content_mismatch",
                "The requested content is not active.",
                exception);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "TCP request failed. RequestId={RequestId} Command={Command}",
                request.RequestId,
                request.CommandCase);
            response = Error(request.RequestId, session.SessionId, ResponseStatus.InternalError, "internal_error", "The request could not be completed.");
        }

        activity?.SetTag("rpc.response.status_code", response.Status.ToString());
        if (!string.IsNullOrEmpty(response.ErrorCode))
        {
            activity?.SetTag("error.type", response.ErrorCode);
        }

        activity?.SetStatus(
            response.Status == ResponseStatus.InternalError
                ? ActivityStatusCode.Error
                : ActivityStatusCode.Ok,
            response.Status == ResponseStatus.InternalError ? response.ErrorCode : null);
        return response;
    }

    private ResponseEnvelope Bind(RequestEnvelope request, TcpSessionContext session)
    {
        if (session.IsBound)
        {
            return Error(request.RequestId, session.SessionId, ResponseStatus.Conflict, "session_already_bound", "This connection is already bound.");
        }

        if (!string.IsNullOrEmpty(request.SessionId) || !string.IsNullOrEmpty(request.IdempotencyKey))
        {
            return Error(request.RequestId, "", ResponseStatus.InvalidRequest, "bind_envelope_invalid", "Bind must not include session or idempotency fields.");
        }

        if (!Guid.TryParse(request.BindSession.PlayerId, out var playerId) || playerId == Guid.Empty)
        {
            return Error(request.RequestId, "", ResponseStatus.InvalidRequest, "player_id_invalid", "Player id must be a non-empty UUID.");
        }

        if (!tokenService.TryValidate(playerId, request.BindSession.AccessToken, out var expiresAt))
        {
            return Error(request.RequestId, "", ResponseStatus.Unauthenticated, "access_token_invalid", "Access token is invalid or expired.");
        }

        session.Bind(playerId, expiresAt);
        return new ResponseEnvelope
        {
            RequestId = request.RequestId,
            SessionId = session.SessionId,
            Status = ResponseStatus.Ok,
            ProtocolVersion = ProtocolVersion,
            BindSession = new BindSessionResponse
            {
                PlayerId = playerId.ToString("D"),
                TokenExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds()
            }
        };
    }

    private async Task<ResponseEnvelope> GetWalletAsync(
        RequestEnvelope request,
        TcpSessionContext session,
        CancellationToken cancellationToken)
    {
        var authorizationError = Authorize(request, session);
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        if (!string.IsNullOrEmpty(request.IdempotencyKey))
        {
            return Error(request.RequestId, session.SessionId, ResponseStatus.InvalidRequest, "idempotency_key_not_allowed", "Read requests must not include an idempotency key.");
        }

        var wallet = await playerService.GetWalletAsync(session.PlayerId, cancellationToken);
        return new ResponseEnvelope
        {
            RequestId = request.RequestId,
            SessionId = session.SessionId,
            Status = ResponseStatus.Ok,
            ProtocolVersion = ProtocolVersion,
            Wallet = TcpProtoMapper.ToProto(wallet)
        };
    }

    private async Task<ResponseEnvelope> DrawGachaAsync(
        RequestEnvelope request,
        TcpSessionContext session,
        CancellationToken cancellationToken)
    {
        var authorizationError = Authorize(request, session);
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        if (!IsIdentifier(request.IdempotencyKey, MaxIdempotencyKeyLength))
        {
            return Error(request.RequestId, session.SessionId, ResponseStatus.InvalidRequest, "idempotency_key_invalid", "Idempotency key contains unsupported characters or exceeds 128 characters.");
        }

        var bannerId = request.DrawGacha.BannerId.Trim();
        if (!IsIdentifier(bannerId, MaxBannerIdLength))
        {
            return Error(request.RequestId, session.SessionId, ResponseStatus.InvalidRequest, "banner_id_invalid", "Banner id contains unsupported characters or exceeds 128 characters.");
        }

        if (request.DrawGacha.DrawCount is <= 0 or > GachaCommandFactory.MaxDrawCount)
        {
            return Error(request.RequestId, session.SessionId, ResponseStatus.InvalidRequest, "draw_count_invalid", $"Draw count must be between 1 and {GachaCommandFactory.MaxDrawCount}.");
        }

        var domainRequest = new Astra.Contracts.DrawGachaRequest(
            bannerId,
            request.DrawGacha.DrawCount,
            request.IdempotencyKey,
            PlayerRequestHash.DrawGacha(session.PlayerId, bannerId, request.DrawGacha.DrawCount));
        var receipt = await playerService.DrawGachaAsync(session.PlayerId, domainRequest, cancellationToken);
        var result = JsonSerializer.Deserialize<GachaDrawResultDto>(receipt.ResponseBody, ResponseJsonOptions)
            ?? throw new InvalidDataException("Grain returned an invalid gacha response.");

        return new ResponseEnvelope
        {
            RequestId = request.RequestId,
            SessionId = session.SessionId,
            Status = ResponseStatus.Ok,
            Replayed = receipt.Replayed,
            ProtocolVersion = ProtocolVersion,
            DrawGacha = TcpProtoMapper.ToProto(result)
        };
    }

    private ResponseEnvelope? Authorize(RequestEnvelope request, TcpSessionContext session)
    {
        if (!session.IsBound)
        {
            return Error(request.RequestId, "", ResponseStatus.Unauthenticated, "session_required", "Bind the connection before sending game commands.");
        }

        if (request.SessionId.Length > MaxSessionIdLength || !StringComparer.Ordinal.Equals(request.SessionId, session.SessionId))
        {
            return Error(request.RequestId, session.SessionId, ResponseStatus.Unauthenticated, "session_mismatch", "Session id does not match this connection.");
        }

        if (session.TokenExpiresAt <= timeProvider.GetUtcNow())
        {
            return Error(request.RequestId, session.SessionId, ResponseStatus.Unauthenticated, "session_expired", "The bound access token has expired.");
        }

        return null;
    }

    private ResponseEnvelope DomainError(
        RequestEnvelope request,
        TcpSessionContext session,
        ResponseStatus status,
        string errorCode,
        string clientMessage,
        Exception exception)
    {
        logger.LogWarning(
            exception,
            "TCP domain request rejected. RequestId={RequestId} Command={Command} ErrorCode={ErrorCode}",
            request.RequestId,
            request.CommandCase,
            errorCode);
        return Error(request.RequestId, session.SessionId, status, errorCode, clientMessage);
    }

    private static ResponseEnvelope Error(
        string requestId,
        string sessionId,
        ResponseStatus status,
        string errorCode,
        string errorMessage) =>
        new()
        {
            RequestId = requestId,
            SessionId = sessionId,
            Status = status,
            ProtocolVersion = ProtocolVersion,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };

    private static bool IsIdentifier(string value, int maxLength)
    {
        if (value.Length == 0 || value.Length > maxLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':')
            {
                return false;
            }
        }

        return true;
    }

    private static Activity? StartActivity(RequestEnvelope request)
    {
        if (ActivityContext.TryParse(request.TraceParent, request.TraceState, true, out var parentContext))
        {
            return AstraTelemetry.ActivitySource.StartActivity("tcp.request", ActivityKind.Server, parentContext);
        }

        return AstraTelemetry.ActivitySource.StartActivity("tcp.request", ActivityKind.Server);
    }
}

internal sealed class TcpSessionContext
{
    public bool IsBound { get; private set; }

    public Guid PlayerId { get; private set; }

    public string SessionId { get; private set; } = "";

    public DateTimeOffset TokenExpiresAt { get; private set; }

    public void Bind(Guid playerId, DateTimeOffset tokenExpiresAt)
    {
        if (IsBound)
        {
            throw new InvalidOperationException("TCP session is already bound.");
        }

        PlayerId = playerId;
        SessionId = Guid.NewGuid().ToString("N");
        TokenExpiresAt = tokenExpiresAt;
        IsBound = true;
    }
}
