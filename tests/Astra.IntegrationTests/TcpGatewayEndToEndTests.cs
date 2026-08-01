using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Astra.Contracts;
using Astra.Contracts.Tcp;
using Astra.TcpGateway;
using Microsoft.Extensions.Options;

namespace Astra.IntegrationTests;

[Collection(EndToEndCollection.Name)]
public sealed class TcpGatewayEndToEndTests
{
    [RequiresEnvironmentFact("ASTRA_RUN_TCP_E2E")]
    public async Task HttpAndTcpPaths_UseSameGrainState_AndReconnectReplays()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = ApiE2E.Client();
        await ApiE2E.AuthenticateAsync(http, "local-supervisor", timeout.Token);
        var playerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var publish = new PublishContentCommand(
            $"tcp-e2e-{Guid.NewGuid():N}",
            [new GachaBannerConfigDto(
                "pickup-tcp-e2e",
                CurrencyCode.Elif,
                100,
                90,
                now.AddMinutes(-1),
                now.AddMinutes(10),
                [
                    new GachaRewardPoolEntryDto(
                        GachaRewardKind.Character,
                        "char-standard",
                        1,
                        2,
                        9_700,
                        false,
                        "memory-char-standard",
                        5),
                    new GachaRewardPoolEntryDto(
                        GachaRewardKind.Character,
                        "char-pickup",
                        1,
                        3,
                        300,
                        true,
                        "memory-char-pickup",
                        20)
                ])],
            "tcp-gateway-e2e");
        var publishResponse = await http.PostAsJsonAsync(
            "/api/admin/content/publish",
            publish,
            timeout.Token);
        publishResponse.EnsureSuccessStatusCode();

        var grantResponse = await http.PostAsJsonAsync(
            $"/api/players/{playerId:D}/wallet/grant",
            new GrantCurrencyCommand(
                CurrencyCode.Elif,
                500,
                "tcp-e2e-seed",
                $"grant-{Guid.NewGuid():N}",
                $"hash-{Guid.NewGuid():N}"),
            timeout.Token);
        grantResponse.EnsureSuccessStatusCode();

        var tokenService = new TcpSessionTokenService(
            Options.Create(new TcpSessionTokenOptions
            {
                SigningKey = "astra-development-signing-key-change-me-2026",
                MaxLifetime = TimeSpan.FromHours(24)
            }),
            TimeProvider.System);
        var token = tokenService.Issue(playerId, DateTimeOffset.UtcNow.AddMinutes(10));
        var idempotencyKey = $"draw-{Guid.NewGuid():N}";

        var first = await DrawAsync(
            playerId,
            token,
            idempotencyKey,
            "request-first",
            timeout.Token);
        var replay = await DrawAsync(
            playerId,
            token,
            idempotencyKey,
            "request-reconnect",
            timeout.Token);

        Assert.Equal(ResponseStatus.Ok, first.Status);
        Assert.False(first.Replayed);
        Assert.Equal(ResponseStatus.Ok, replay.Status);
        Assert.True(replay.Replayed);
        Assert.Equal(first.DrawGacha, replay.DrawGacha);

        var crossTransportKey = $"draw-{Guid.NewGuid():N}";
        var httpDrawResponse = await http.PostAsJsonAsync(
            $"/api/players/{playerId:D}/gacha/draw",
            new Astra.Contracts.DrawGachaRequest(
                "pickup-tcp-e2e",
                1,
                crossTransportKey,
                "client-hash-is-ignored"),
            timeout.Token);
        httpDrawResponse.EnsureSuccessStatusCode();
        var httpDrawReceipt = await httpDrawResponse.Content.ReadFromJsonAsync<PlayerCommandReceipt>(timeout.Token);
        Assert.NotNull(httpDrawReceipt);
        Assert.False(httpDrawReceipt.Replayed);

        var crossTransportReplay = await DrawAsync(
            playerId,
            token,
            crossTransportKey,
            "request-cross-transport",
            timeout.Token);
        var httpDrawResult = JsonSerializer.Deserialize<GachaDrawResultDto>(
            httpDrawReceipt.ResponseBody,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(httpDrawResult);
        Assert.True(crossTransportReplay.Replayed);
        Assert.Equal(httpDrawResult.ContentVersion, crossTransportReplay.DrawGacha.ContentVersion);
        Assert.Equal(httpDrawResult.ContentChecksum, crossTransportReplay.DrawGacha.ContentChecksum);

        var tcpWallet = await GetWalletAsync(playerId, token, timeout.Token);
        var httpWallet = await http.GetFromJsonAsync<WalletSnapshotDto>(
            $"/api/players/{playerId:D}/wallet",
            timeout.Token);

        Assert.NotNull(httpWallet);
        Assert.Equal(httpWallet.LedgerVersion, tcpWallet.LedgerVersion);
        Assert.Equal(
            httpWallet.Balances.Single(balance => balance.Currency == CurrencyCode.Elif).Amount,
            tcpWallet.Balances.Single(balance => balance.Currency == (int)CurrencyCode.Elif).Amount);
        Assert.Equal(300, tcpWallet.Balances.Single(balance => balance.Currency == (int)CurrencyCode.Elif).Amount);

        var mismatch = await DrawAsync(
            playerId,
            token,
            $"draw-{Guid.NewGuid():N}",
            "request-content-mismatch",
            timeout.Token,
            "missing-banner");
        Assert.Equal(ResponseStatus.FailedPrecondition, mismatch.Status);
        Assert.Equal("content_mismatch", mismatch.ErrorCode);
    }

    private static async Task<ResponseEnvelope> DrawAsync(
        Guid playerId,
        string token,
        string idempotencyKey,
        string requestId,
        CancellationToken cancellationToken,
        string bannerId = "pickup-tcp-e2e")
    {
        using var client = await ConnectAndBindAsync(playerId, token, requestId + "-bind", cancellationToken);
        var stream = client.Client.GetStream();
        return await SendAsync(
            stream,
            new RequestEnvelope
            {
                RequestId = requestId,
                SessionId = client.SessionId,
                IdempotencyKey = idempotencyKey,
                ProtocolVersion = 1,
                DrawGacha = new Astra.Contracts.Tcp.DrawGachaRequest
                {
                    BannerId = bannerId,
                    DrawCount = 1
                }
            },
            cancellationToken);
    }

    private static async Task<WalletSnapshot> GetWalletAsync(
        Guid playerId,
        string token,
        CancellationToken cancellationToken)
    {
        using var client = await ConnectAndBindAsync(playerId, token, "wallet-bind", cancellationToken);
        var response = await SendAsync(
            client.Client.GetStream(),
            new RequestEnvelope
            {
                RequestId = "wallet-read",
                SessionId = client.SessionId,
                ProtocolVersion = 1,
                GetWallet = new GetWalletRequest()
            },
            cancellationToken);

        Assert.Equal(ResponseStatus.Ok, response.Status);
        return response.Wallet;
    }

    // Windows가 Hyper-V 또는 WSL용으로 기본 포트를 예약할 수 있다.
    private static int GatewayPort() =>
        int.TryParse(Environment.GetEnvironmentVariable("ASTRA_TCP_PORT"), out var port) && port is > 0 and <= 65535
            ? port
            : 5300;

    private static async Task<BoundClient> ConnectAndBindAsync(
        Guid playerId,
        string token,
        string requestId,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, GatewayPort(), cancellationToken);
            var response = await SendAsync(
                client.GetStream(),
                new RequestEnvelope
                {
                    RequestId = requestId,
                    ProtocolVersion = 1,
                    BindSession = new BindSessionRequest
                    {
                        PlayerId = playerId.ToString("D"),
                        AccessToken = token
                    }
                },
                cancellationToken);
            Assert.Equal(ResponseStatus.Ok, response.Status);
            return new BoundClient(client, response.SessionId);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<ResponseEnvelope> SendAsync(
        NetworkStream stream,
        RequestEnvelope request,
        CancellationToken cancellationToken)
    {
        const int maxFrameBytes = 64 * 1_024;
        await TcpFrameCodec.WriteAsync(stream, request, maxFrameBytes, cancellationToken);
        return await TcpFrameCodec.ReadAsync(
                stream,
                ResponseEnvelope.Parser,
                maxFrameBytes,
                cancellationToken)
            ?? throw new EndOfStreamException("TCP gateway closed before returning a response.");
    }

    private sealed class BoundClient(TcpClient client, string sessionId) : IDisposable
    {
        public TcpClient Client { get; } = client;

        public string SessionId { get; } = sessionId;

        public void Dispose() => Client.Dispose();
    }
}
