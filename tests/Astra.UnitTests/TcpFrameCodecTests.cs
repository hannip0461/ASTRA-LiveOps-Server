using System.Buffers.Binary;
using Astra.Contracts.Tcp;
using Astra.TcpGateway;

namespace Astra.UnitTests;

public sealed class TcpFrameCodecTests
{
    [Fact]
    public async Task FrameCodec_RoundTripsFragmentedProtobufFrame()
    {
        var message = new RequestEnvelope
        {
            RequestId = "request-1",
            ProtocolVersion = 1,
            DrawGacha = new Astra.Contracts.Tcp.DrawGachaRequest
            {
                BannerId = "pickup-a",
                DrawCount = 10
            }
        };
        await using var encoded = new MemoryStream();
        await TcpFrameCodec.WriteAsync(encoded, message, 64 * 1_024, CancellationToken.None);
        await using var fragmented = new FragmentedMemoryStream(encoded.ToArray(), maxReadBytes: 2);

        var decoded = await TcpFrameCodec.ReadAsync(
            fragmented,
            RequestEnvelope.Parser,
            64 * 1_024,
            CancellationToken.None);

        Assert.NotNull(decoded);
        Assert.Equal(message, decoded);
    }

    [Fact]
    public async Task FrameCodec_RejectsFrameLargerThanConfiguredLimit()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, 1_025);
        await using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<TcpProtocolException>(async () =>
            await TcpFrameCodec.ReadAsync(
                stream,
                RequestEnvelope.Parser,
                1_024,
                CancellationToken.None));
    }

    private sealed class FragmentedMemoryStream(byte[] buffer, int maxReadBytes) : MemoryStream(buffer)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(destination[..Math.Min(destination.Length, maxReadBytes)], cancellationToken);
    }
}
