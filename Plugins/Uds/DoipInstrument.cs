using System;
using System.Threading;
using OpenTap;
using UdsCore;
using UdsEngine;
using Jabil.Uds.Core;

namespace Tap.Plugins.Uds;

[Display("DoIP UDS", Group: "UDS", Description: "DoIP instrument for UDS diagnostics.")]
public sealed class DoipInstrument : Instrument, IUdsInstrument
{
    private readonly SemaphoreSlim transactionLock = new(1, 1);
    private DoipUdsTransport? transport;
    [Display("Host", Group: "Connection", Order: 1)]
    public string Host { get; set; } = "127.0.0.1";

    [Display("Port", Group: "Connection", Order: 2)]
    public int Port { get; set; } = 13400;

    [Display("Source Address", Group: "DoIP", Order: 3)]
    public ushort SourceAddress { get; set; } = 0x102D;

    [Display("Target Address", Group: "DoIP", Order: 4)]
    public ushort TargetAddress { get; set; } = 0x1008;

    [Display("Activation Type", Group: "DoIP", Order: 5)]
    public byte ActivationType { get; set; }

    [Display("Virtual Mode", Group: "Connection", Order: 6)]
    public bool VirtualMode { get; set; }

    public string ResourceName => Name;

    [Display("Transport Mode", Group: "Connection", Order: 7, Description: "Real connects to DoIP, VirtualResponse uses the configured simulated response, and LocalVirtualEcu connects to 127.0.0.1:13400.")]
    public UdsTransportMode TransportMode { get; set; } = UdsTransportMode.Real;

    public override void Open()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("DoIP host cannot be empty.", nameof(Host));
        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), "DoIP port must be between 1 and 65535.");

        if (VirtualMode || TransportMode == UdsTransportMode.VirtualResponse)
        {
            IsConnected = true;
            Log.Info("DoIP virtual connection ready.");
            return;
        }

        try
        {
            transport = new DoipUdsTransport(CreateConfig());
            transport.Connect();
            IsConnected = true;
            Log.Info("DoIP initialized and routing activated for {0}:{1}.", Host, Port);
        }
        catch
        {
            transport?.Dispose();
            transport = null;
            throw;
        }
    }

    public override void Close()
    {
        transport?.Dispose();
        transport = null;
        IsConnected = false;
    }

    public UdsStepResult Execute(UdsStepConfig config)
    {
        transactionLock.Wait();
        try
        {
            ApplyDefaults(config);
            if (config.ConnectionOnly)
                return new UdsStepResult { Success = true, ResultText = "PASS" };
            if (config.VirtualMode)
                return UdsTransactionExecutor.Execute(config, new VirtualUdsTransport());
            if (transport == null || !IsConnected)
                throw new InvalidOperationException("DoIP instrument is not open.");
            return UdsTransactionExecutor.Execute(config, transport);
        }
        finally
        {
            transactionLock.Release();
        }
    }

    public void ApplyDefaults(UdsStepConfig config)
    {
        config.Host = Host;
        config.Port = Port;
        config.SourceAddress = SourceAddress;
        config.TargetAddress = TargetAddress;
        config.ActivationType = ActivationType;
        config.SessionKey = Name;
        if (VirtualMode) config.VirtualMode = true;
        if (TransportMode == UdsTransportMode.VirtualResponse) config.VirtualMode = true;
    }

    private UdsStepConfig CreateConfig()
    {
        return new UdsStepConfig
        {
            Host = Host,
            Port = Port,
            SourceAddress = SourceAddress,
            TargetAddress = TargetAddress,
            ActivationType = ActivationType,
            SessionKey = Name
        };
    }
}