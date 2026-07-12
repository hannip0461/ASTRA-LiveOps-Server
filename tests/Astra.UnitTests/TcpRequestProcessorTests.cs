using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Astra.Contracts;
using Astra.Contracts.Tcp;
using Astra.Domain;
using Astra.TcpGateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Astra.UnitTests;

public sealed class TcpRequestProcessorTests
{
    [Fact]
    public async Task ConnectionHandler_BindsSessionAndRoutesWalletOverFramedSocket()
    {
        var harness = CreateHarness();
        var options = Options.Create(new TcpGatewayOptions
        {
            MaxFrameBytes = 64 * 1_024,
            MaxRequestsPerConnection = 10,
            IdleTimeout = TimeSpan.FromSeconds(5),
            CommandTimeout = TimeSpan.FromSeconds(5)
        });
        var handler = new TcpConnectionHandler(
            options,
            harness.Processor,
            NullLogger<TcpConnectionHandler>.Instance);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverTask = Task.Run(async () =>
        {
            using var serverClient = await listener.AcceptTcpClientAsync(timeout.Token);
            await handler.HandleAsync(serverClient.GetStream(), "test-connection", timeout.Token);
        }, timeout.Token);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(endpoint.Address, endpoint.Port, timeout.Token);
            var stream = client.GetStream();
            var bindRequest = BindRequest(
                "bind-1",
                harness.PlayerId,
                harness.TokenService.Issue(harness.PlayerId, harness.Clock.GetUtcNow().AddMinutes(10)));
            await TcpFrameCodec.WriteAsync(stream, bindRequest, options.Value.MaxFrameBytes, timeout.Token);
            var bindResponse = await TcpFrameCodec.ReadAsync(
                stream,
                ResponseEnvelope.Parser,
                options.Value.MaxFrameBytes,
                timeout.Token);

            Assert.NotNull(bindResponse);
            Assert.Equal(ResponseStatus.Ok, bindResponse.Status);
            Assert.NotEmpty(bindResponse.SessionId);

            var walletRequest = new RequestEnvelope
            {
                RequestId = "wallet-1",
                SessionId = bindResponse.SessionId,
                ProtocolVersion = 1,
                GetWallet = new GetWalletRequest()
            };
            await TcpFrameCodec.WriteAsync(stream, walletRequest, options.Value.MaxFrameBytes, timeout.Token);
            var walletResponse = await TcpFrameCodec.ReadAsync(
                stream,
                ResponseEnvelope.Parser,
                options.Value.MaxFrameBytes,
                timeout.Token);

            Assert.NotNull(walletResponse);
            Assert.Equal(ResponseStatus.Ok, walletResponse.Status);
            Assert.Equal(harness.PlayerId.ToString("D"), walletResponse.Wallet.PlayerId);
            Assert.Equal(500, walletResponse.Wallet.Balances.Single().Amount);
        }

        await serverTask;
        listener.Stop();
        Assert.Equal(1, harness.PlayerService.WalletCalls);
    }

    [Fact]
    public async Task DrawGacha_AfterReconnect_UsesStableServerHashAndReplays()
    {
        var harness = CreateHarness();
        var token = harness.TokenService.Issue(harness.PlayerId, harness.Clock.GetUtcNow().AddMinutes(10));
        var firstSession = new TcpSessionContext();
        var firstBind = await harness.Processor.ProcessAsync(
            BindRequest("bind-1", harness.PlayerId, token),
            firstSession,
            CancellationToken.None);

        var first = await harness.Processor.ProcessAsync(
            DrawRequest("draw-request-1", firstBind.SessionId, "draw-idempotency-1"),
            firstSession,
            CancellationToken.None);

        var secondSession = new TcpSessionContext();
        var secondBind = await harness.Processor.ProcessAsync(
            BindRequest("bind-2", harness.PlayerId, token),
            secondSession,
            CancellationToken.None);
        var replay = await harness.Processor.ProcessAsync(
            DrawRequest("draw-request-2", secondBind.SessionId, "draw-idempotency-1"),
            secondSession,
            CancellationToken.None);

        Assert.Equal(ResponseStatus.Ok, first.Status);
        Assert.False(first.Replayed);
        Assert.Equal(ResponseStatus.Ok, replay.Status);
        Assert.True(replay.Replayed);
        Assert.Equal(first.DrawGacha, replay.DrawGacha);
        Assert.Equal(2, harness.PlayerService.DrawRequests.Count);
        Assert.Equal(
            harness.PlayerService.DrawRequests[0].RequestHash,
            harness.PlayerService.DrawRequests[1].RequestHash);
    }

    [Fact]
    public async Task GameCommand_WithSessionFromAnotherConnection_IsRejected()
    {
        var harness = CreateHarness();
        var session = new TcpSessionContext();
        var token = harness.TokenService.Issue(harness.PlayerId, harness.Clock.GetUtcNow().AddMinutes(10));
        await harness.Processor.ProcessAsync(
            BindRequest("bind-1", harness.PlayerId, token),
            session,
            CancellationToken.None);

        var response = await harness.Processor.ProcessAsync(
            new RequestEnvelope
            {
                RequestId = "wallet-1",
                SessionId = Guid.NewGuid().ToString("N"),
                ProtocolVersion = 1,
                GetWallet = new GetWalletRequest()
            },
            session,
            CancellationToken.None);

        Assert.Equal(ResponseStatus.Unauthenticated, response.Status);
        Assert.Equal("session_mismatch", response.ErrorCode);
        Assert.Equal(0, harness.PlayerService.WalletCalls);
    }

    [Fact]
    public async Task Bind_WithTamperedToken_IsRejected()
    {
        var harness = CreateHarness();
        var token = harness.TokenService.Issue(harness.PlayerId, harness.Clock.GetUtcNow().AddMinutes(10));
        var response = await harness.Processor.ProcessAsync(
            BindRequest("bind-1", harness.PlayerId, token + "x"),
            new TcpSessionContext(),
            CancellationToken.None);

        Assert.Equal(ResponseStatus.Unauthenticated, response.Status);
        Assert.Equal("access_token_invalid", response.ErrorCode);
    }

    [Fact]
    public async Task BoundSession_AfterTokenExpiry_IsRejected()
    {
        var harness = CreateHarness();
        var session = new TcpSessionContext();
        var token = harness.TokenService.Issue(harness.PlayerId, harness.Clock.GetUtcNow().AddMinutes(1));
        var bind = await harness.Processor.ProcessAsync(
            BindRequest("bind-1", harness.PlayerId, token),
            session,
            CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromMinutes(2));

        var response = await harness.Processor.ProcessAsync(
            new RequestEnvelope
            {
                RequestId = "wallet-1",
                SessionId = bind.SessionId,
                ProtocolVersion = 1,
                GetWallet = new GetWalletRequest()
            },
            session,
            CancellationToken.None);

        Assert.Equal(ResponseStatus.Unauthenticated, response.Status);
        Assert.Equal("session_expired", response.ErrorCode);
        Assert.Equal(0, harness.PlayerService.WalletCalls);
    }

    [Fact]
    public async Task SameIdempotencyKey_WithDifferentDrawBody_ReturnsConflict()
    {
        var harness = CreateHarness();
        var session = new TcpSessionContext();
        var token = harness.TokenService.Issue(harness.PlayerId, harness.Clock.GetUtcNow().AddMinutes(10));
        var bind = await harness.Processor.ProcessAsync(
            BindRequest("bind-1", harness.PlayerId, token),
            session,
            CancellationToken.None);
        await harness.Processor.ProcessAsync(
            DrawRequest("draw-1", bind.SessionId, "draw-key", drawCount: 1),
            session,
            CancellationToken.None);

        var conflict = await harness.Processor.ProcessAsync(
            DrawRequest("draw-2", bind.SessionId, "draw-key", drawCount: 2),
            session,
            CancellationToken.None);

        Assert.Equal(ResponseStatus.Conflict, conflict.Status);
        Assert.Equal("idempotency_conflict", conflict.ErrorCode);
    }

    [Fact]
    public async Task RequestId_WithControlCharacter_IsRejectedWithoutLoggableEcho()
    {
        var harness = CreateHarness();
        var response = await harness.Processor.ProcessAsync(
            new RequestEnvelope
            {
                RequestId = "bad\nrequest",
                ProtocolVersion = 1,
                GetWallet = new GetWalletRequest()
            },
            new TcpSessionContext(),
            CancellationToken.None);

        Assert.Equal(ResponseStatus.InvalidRequest, response.Status);
        Assert.Equal("request_id_invalid", response.ErrorCode);
        Assert.Empty(response.RequestId);
    }

    private static TestHarness CreateHarness()
    {
        var playerId = Guid.NewGuid();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero));
        var tokenService = new TcpSessionTokenService(
            Options.Create(new TcpSessionTokenOptions
            {
                SigningKey = "test-signing-key-at-least-32-bytes-long",
                MaxLifetime = TimeSpan.FromHours(24)
            }),
            clock);
        var playerService = new FakeTcpPlayerService(playerId);
        var processor = new TcpRequestProcessor(
            tokenService,
            playerService,
            clock,
            NullLogger<TcpRequestProcessor>.Instance);
        return new TestHarness(playerId, clock, tokenService, playerService, processor);
    }

    private static RequestEnvelope BindRequest(
        string requestId,
        Guid playerId,
        string token) =>
        new()
        {
            RequestId = requestId,
            ProtocolVersion = 1,
            BindSession = new BindSessionRequest
            {
                PlayerId = playerId.ToString("D"),
                AccessToken = token
            }
        };

    private static RequestEnvelope DrawRequest(
        string requestId,
        string sessionId,
        string idempotencyKey,
        int drawCount = 1) =>
        new()
        {
            RequestId = requestId,
            SessionId = sessionId,
            IdempotencyKey = idempotencyKey,
            ProtocolVersion = 1,
            DrawGacha = new Astra.Contracts.Tcp.DrawGachaRequest
            {
                BannerId = "pickup-a",
                DrawCount = drawCount
            }
        };

    private sealed record TestHarness(
        Guid PlayerId,
        ManualTimeProvider Clock,
        TcpSessionTokenService TokenService,
        FakeTcpPlayerService PlayerService,
        TcpRequestProcessor Processor);

    private sealed class FakeTcpPlayerService(Guid playerId) : ITcpPlayerService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, (string RequestHash, string ResponseBody, WalletSnapshotDto Snapshot)> _completed = new(StringComparer.Ordinal);

        public int WalletCalls { get; private set; }

        public List<Astra.Contracts.DrawGachaRequest> DrawRequests { get; } = [];

        public Task<WalletSnapshotDto> GetWalletAsync(Guid requestedPlayerId, CancellationToken cancellationToken)
        {
            Assert.Equal(playerId, requestedPlayerId);
            WalletCalls++;
            return Task.FromResult(Wallet());
        }

        public Task<PlayerCommandReceipt> DrawGachaAsync(
            Guid requestedPlayerId,
            Astra.Contracts.DrawGachaRequest request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(playerId, requestedPlayerId);
            DrawRequests.Add(request);
            if (_completed.TryGetValue(request.IdempotencyKey, out var completed))
            {
                if (!StringComparer.Ordinal.Equals(completed.RequestHash, request.RequestHash))
                {
                    throw new IdempotencyConflictException("Request hash changed during replay.");
                }

                return Task.FromResult(new PlayerCommandReceipt(true, completed.ResponseBody, completed.Snapshot));
            }

            var wallet = Wallet();
            var result = new GachaDrawResultDto(
                request.BannerId,
                "content-a",
                "checksum-a",
                [new GachaDrawRewardDto(GachaRewardKind.Character, "char-pickup", 1, 3, false, null)],
                0,
                wallet);
            var responseBody = JsonSerializer.Serialize(result, JsonOptions);
            _completed.Add(request.IdempotencyKey, (request.RequestHash, responseBody, wallet));
            return Task.FromResult(new PlayerCommandReceipt(false, responseBody, wallet));
        }

        private WalletSnapshotDto Wallet() =>
            new(
                playerId,
                [new CurrencyBalanceDto(CurrencyCode.Elif, 500)],
                1,
                [],
                [],
                new Dictionary<string, int>(StringComparer.Ordinal));
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
