using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;

namespace Astra.TcpGateway;

internal static class TcpFrameCodec
{
    private const int HeaderBytes = sizeof(int);

    public static async ValueTask<T?> ReadAsync<T>(
        Stream stream,
        MessageParser<T> parser,
        int maxFrameBytes,
        CancellationToken cancellationToken)
        where T : class, IMessage<T>
    {
        var header = ArrayPool<byte>.Shared.Rent(HeaderBytes);
        int payloadLength;
        try
        {
            var headerBytes = await stream.ReadAsync(header.AsMemory(0, HeaderBytes), cancellationToken);
            if (headerBytes == 0)
            {
                return null;
            }

            await ReadRemainingAsync(stream, header.AsMemory(headerBytes, HeaderBytes - headerBytes), cancellationToken);
            payloadLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0, HeaderBytes));
        }
        finally
        {
            header.AsSpan(0, HeaderBytes).Clear();
            ArrayPool<byte>.Shared.Return(header);
        }

        if (payloadLength <= 0 || payloadLength > maxFrameBytes)
        {
            throw new TcpProtocolException($"Frame length must be between 1 and {maxFrameBytes} bytes.");
        }

        var payload = ArrayPool<byte>.Shared.Rent(payloadLength);
        try
        {
            await ReadRemainingAsync(stream, payload.AsMemory(0, payloadLength), cancellationToken);
            try
            {
                return parser.ParseFrom(payload, 0, payloadLength);
            }
            catch (InvalidProtocolBufferException exception)
            {
                throw new TcpProtocolException("Frame payload is not valid Protobuf.", exception);
            }
        }
        finally
        {
            payload.AsSpan(0, payloadLength).Clear();
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T message,
        int maxFrameBytes,
        CancellationToken cancellationToken)
        where T : class, IMessage<T>
    {
        var payloadLength = message.CalculateSize();
        if (payloadLength <= 0 || payloadLength > maxFrameBytes)
        {
            throw new TcpProtocolException($"Frame length must be between 1 and {maxFrameBytes} bytes.");
        }

        var frameLength = HeaderBytes + payloadLength;
        var frame = ArrayPool<byte>.Shared.Rent(frameLength);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, HeaderBytes), payloadLength);
            using var payloadStream = new MemoryStream(frame, HeaderBytes, payloadLength, writable: true, publiclyVisible: true);
            message.WriteTo(payloadStream);
            await stream.WriteAsync(frame.AsMemory(0, frameLength), cancellationToken);
        }
        finally
        {
            frame.AsSpan(0, frameLength).Clear();
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private static async ValueTask ReadRemainingAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            var read = await stream.ReadAsync(destination, cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("TCP frame ended before the declared payload length.");
            }

            destination = destination[read..];
        }
    }
}

internal sealed class TcpProtocolException : IOException
{
    public TcpProtocolException(string message)
        : base(message)
    {
    }

    public TcpProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
