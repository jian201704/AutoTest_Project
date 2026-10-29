using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

const string host = "127.0.0.1";
const int port = 13400;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var listener = new TcpListener(IPAddress.Loopback, port);
try
{
    listener.Start();
    Console.WriteLine($"Virtual DoIP ECU listening on {host}:{port}");
    Console.WriteLine("Press Ctrl+C to stop.");

    while (!cancellation.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(cancellation.Token);
        _ = HandleClientAsync(client, cancellation.Token);
    }
}
catch (OperationCanceledException)
{
}
catch (SocketException exception)
{
    Console.Error.WriteLine($"Could not listen on {host}:{port}: {exception.Message}");
    return 1;
}
finally
{
    listener.Stop();
}

return 0;

static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
{
    var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    Console.WriteLine($"Client connected: {remote}");
    using (client)
    await using (var stream = client.GetStream())
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await DoipFrame.ReadAsync(stream, cancellationToken);
                Console.WriteLine($"RX 0x{frame.PayloadType:X4}: {ToHex(frame.Payload)}");

                switch (frame.PayloadType)
                {
                    case 0x0005:
                        await SendRoutingActivationAsync(stream, cancellationToken);
                        break;
                    case 0x8001:
                        if (frame.Payload.Length < 4)
                            throw new InvalidDataException("Diagnostic message payload is too short.");
                        var sourceAddress = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(0, 2));
                        var targetAddress = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(2, 2));
                        var udsRequest = frame.Payload.AsSpan(4).ToArray();
                        var udsResponse = BuildUdsResponse(udsRequest);
                        Console.WriteLine($"UDS {ToHex(udsRequest)} -> {ToHex(udsResponse)}");

                        if (udsRequest.Length >= 2 && udsRequest[0] == 0x3E && (udsRequest[1] & 0x80) != 0)
                            break;

                        var diagnosticResponse = new byte[4 + udsResponse.Length];
                        BinaryPrimitives.WriteUInt16BigEndian(diagnosticResponse.AsSpan(0, 2), targetAddress);
                        BinaryPrimitives.WriteUInt16BigEndian(diagnosticResponse.AsSpan(2, 2), sourceAddress);
                        udsResponse.CopyTo(diagnosticResponse.AsSpan(4));
                        await stream.WriteAsync(DoipFrame.Create(0x8001, diagnosticResponse), cancellationToken);
                        break;
                    default:
                        Console.WriteLine($"Ignoring unsupported DoIP payload type 0x{frame.PayloadType:X4}.");
                        break;
                }
            }
        }
        catch (EndOfStreamException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Client error: {exception.Message}");
        }
    }
    Console.WriteLine($"Client disconnected: {remote}");
}

static async Task SendRoutingActivationAsync(NetworkStream stream, CancellationToken cancellationToken)
{
    var response = new byte[11];
    response[1] = 0x01;
    response[2] = 0x10;
    response[4] = 0x10;
    await stream.WriteAsync(DoipFrame.Create(0x0006, response), cancellationToken);
    Console.WriteLine("Routing activation accepted.");
}

static byte[] BuildUdsResponse(byte[] request)
{
    if (request.Length == 0)
        return new byte[] { 0x7F, 0x00, 0x13 };

    var service = request[0];
    switch (service)
    {
        case 0x10 when request.Length >= 2:
            return new byte[] { 0x50, request[1], 0x00, 0x32, 0x01, 0xF4 };
        case 0x3E when request.Length >= 2:
            return new byte[] { 0x7E, request[1] };
        case 0x22 when request.Length >= 3 && request[1] == 0xF1 && request[2] == 0x90:
            return WithAscii(0x62, 0xF1, 0x90, "VIRTUAL-DOIP");
        case 0x22 when request.Length >= 3 && request[1] == 0xF1 && request[2] == 0x05:
            return new byte[] { 0x62, 0xF1, 0x05, 0x12, 0x34 };
        case 0x22 when request.Length >= 3:
            return new byte[] { 0x62, request[1], request[2], 0x00 };
        case 0x2E when request.Length >= 3:
            return request.Take(3).Prepend((byte)0x6E).ToArray();
        default:
            return new byte[] { 0x7F, service, 0x11 };
    }
}

static byte[] WithAscii(byte service, byte highDid, byte lowDid, string text)
{
    return new[] { service, highDid, lowDid }.Concat(Encoding.ASCII.GetBytes(text)).ToArray();
}

static string ToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", " ");

internal sealed class DoipFrame
{
    public ushort PayloadType { get; private init; }
    public byte[] Payload { get; private init; } = Array.Empty<byte>();

    public static byte[] Create(ushort payloadType, byte[] payload)
    {
        var frame = new byte[8 + payload.Length];
        frame[0] = 0x02;
        frame[1] = 0xFD;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), payloadType);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    public static async Task<DoipFrame> ReadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellationToken);
        if (header[0] != 0x02 || header[1] != 0xFD)
            throw new InvalidDataException("Invalid DoIP protocol version.");

        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (length > 4096)
            throw new InvalidDataException("DoIP payload is too large.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return new DoipFrame
        {
            PayloadType = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)),
            Payload = payload
        };
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (count == 0)
                throw new EndOfStreamException();
            offset += count;
        }
    }
}
