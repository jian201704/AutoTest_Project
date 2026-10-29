using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace OpenTap.Poc;

internal static class DoipProtocol
{
    public const ushort RoutingActivationRequest = 0x0005;
    public const ushort RoutingActivationResponse = 0x0006;
    public const ushort DiagnosticMessage = 0x8001;

    public static byte[] CreateFrame(ushort payloadType, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[8 + payload.Length];
        frame[0] = 0x02;
        frame[1] = 0xFD;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), payloadType);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    public static async Task<(ushort PayloadType, byte[] Payload)> ReadFrameAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellationToken);

        if (header[0] != 0x02 || header[1] != 0xFD)
            throw new InvalidDataException("Invalid DoIP protocol version.");

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (payloadLength > 4096)
            throw new InvalidDataException("DoIP payload is too large.");

        var payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)), payload);
    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (count == 0)
                throw new EndOfStreamException("The DoIP peer closed the connection.");
            offset += count;
        }
    }
}

internal sealed class VirtualDoipServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private Task? serverTask;

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public void Start()
    {
        listener.Start();
        serverTask = AcceptAndHandleAsync(cancellation.Token);
    }

    private async Task AcceptAndHandleAsync(CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();

        var activation = await DoipProtocol.ReadFrameAsync(stream, cancellationToken);
        if (activation.PayloadType != DoipProtocol.RoutingActivationRequest || activation.Payload.Length < 3)
            throw new InvalidDataException("Expected a DoIP routing activation request.");

        var activationResponse = new byte[11];
        activationResponse[1] = 0x01;
        activationResponse[2] = 0x10;
        activationResponse[4] = 0x10;
        activationResponse[5] = 0x00;
        activationResponse[6] = 0x00;
        await stream.WriteAsync(
            DoipProtocol.CreateFrame(DoipProtocol.RoutingActivationResponse, activationResponse),
            cancellationToken);

        var diagnostic = await DoipProtocol.ReadFrameAsync(stream, cancellationToken);
        if (diagnostic.PayloadType != DoipProtocol.DiagnosticMessage || diagnostic.Payload.Length < 6)
            throw new InvalidDataException("Expected a DoIP diagnostic message.");

        var request = diagnostic.Payload.AsSpan(4);
        if (!request.SequenceEqual(new byte[] { 0x22, 0xF1, 0x90 }))
            throw new InvalidDataException("The virtual ECU received an unexpected UDS request.");

        var responsePayload = new byte[]
        {
            0x10, 0x00, 0x0E, 0x00,
            0x62, 0xF1, 0x90, (byte)'D', (byte)'O', (byte)'I', (byte)'P'
        };
        await stream.WriteAsync(
            DoipProtocol.CreateFrame(DoipProtocol.DiagnosticMessage, responsePayload),
            cancellationToken);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        listener.Stop();
        try
        {
            serverTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        cancellation.Dispose();
    }
}

internal static class DoipClient
{
    public static async Task<string> ReadVehiclePartNumberAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        await using var stream = client.GetStream();

        var activationRequest = new byte[] { 0x0E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        await stream.WriteAsync(
            DoipProtocol.CreateFrame(DoipProtocol.RoutingActivationRequest, activationRequest),
            cancellationToken);

        var activation = await DoipProtocol.ReadFrameAsync(stream, cancellationToken);
        if (activation.PayloadType != DoipProtocol.RoutingActivationResponse || activation.Payload.Length < 3 || activation.Payload[2] != 0x10)
            throw new InvalidDataException("DoIP routing activation was rejected.");

        var diagnosticPayload = new byte[] { 0x0E, 0x00, 0x10, 0x00, 0x22, 0xF1, 0x90 };
        await stream.WriteAsync(
            DoipProtocol.CreateFrame(DoipProtocol.DiagnosticMessage, diagnosticPayload),
            cancellationToken);

        var response = await DoipProtocol.ReadFrameAsync(stream, cancellationToken);
        if (response.PayloadType != DoipProtocol.DiagnosticMessage || response.Payload.Length < 7)
            throw new InvalidDataException("DoIP diagnostic response was invalid.");

        var udsResponse = response.Payload.AsSpan(4);
        if (!udsResponse[..3].SequenceEqual(new byte[] { 0x62, 0xF1, 0x90 }))
            throw new InvalidDataException("The ECU returned an unexpected UDS response.");

        return System.Text.Encoding.ASCII.GetString(udsResponse[3..]);
    }
}