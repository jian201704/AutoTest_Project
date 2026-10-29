using System;
using Jabil.Uds.Core;
using UdsCore;

namespace Tap.Plugins.Uds;

internal sealed class DoipUdsTransport : IUdsTransport
{
    private readonly UdsStepConfig config;
    private readonly DoipClient client = new();
    private bool connected;

    public DoipUdsTransport(UdsStepConfig config)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public void Connect()
    {
        if (connected) return;
        if (!client.Connect(config.Host, config.Port, config.TimeoutMs))
            throw new InvalidOperationException(client.LastError);
        if (!client.ActivateRouting(config.SourceAddress, config.ActivationType, config.TimeoutMs, out var error))
        {
            client.Dispose();
            throw new InvalidOperationException(error);
        }
        connected = true;
    }

    public DiagnosticFrame Request(DiagnosticFrame request, int timeoutMilliseconds)
    {
        if (!connected) throw new InvalidOperationException("DoIP transport is not connected.");
        if (!client.SendDiagnosticMessage(
                (ushort)request.SourceAddress,
                (ushort)request.TargetAddress,
                request.Payload,
                timeoutMilliseconds,
                config.ResponsePendingTimeoutMs,
                out var response,
                out var error))
            throw new InvalidOperationException(error);
        return new DiagnosticFrame(request.SourceAddress, request.TargetAddress, response);
    }

    public void Dispose()
    {
        connected = false;
        client.Dispose();
    }
}

internal sealed class VirtualUdsTransport : IUdsTransport
{
    public void Connect() { }

    public DiagnosticFrame Request(DiagnosticFrame request, int timeoutMilliseconds) =>
        throw new InvalidOperationException("Virtual UDS transport does not send requests.");

    public void Dispose() { }
}