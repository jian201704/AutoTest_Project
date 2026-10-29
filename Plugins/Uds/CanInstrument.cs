using System;
using System.Collections.Generic;
using System.Threading;
using OpenTap;
using Jabil.Uds.Can;
using UdsCore;
using UdsEngine;
using Jabil.Uds.Core;
using ZLGAPI;

namespace Tap.Plugins.Uds;

[Display("CAN UDS", Group: "UDS", Description: "ZLG CAN/CAN-FD instrument for UDS diagnostics.")]
public sealed class CanInstrument : Instrument, IUdsInstrument
{
    private ZlgCanDeviceProvider? provider;
    private CanIsoTpTransport? transport;
    private readonly SemaphoreSlim transactionLock = new(1, 1);
    private Timer? keepAliveTimer;
    private byte[] keepAlivePayload = Array.Empty<byte>();

    public string ResourceName => Name;

    [Display("Device Type,Index", Group: "Connection", Order: 1)]
    public string DeviceAddress { get; set; } = "4,0";

    [Display("Serial Number", Group: "Connection", Order: 2)]
    public string DeviceSerialNumber { get; set; } = "";

    [Display("Channel", Group: "Connection", Order: 3)]
    public int ChannelIndex { get; set; }

    [Display("CAN-FD", Group: "CAN", Order: 1)]
    public bool CanFd { get; set; }

    [Display("Bit Rate Switch", Group: "CAN", Order: 2)]
    [EnabledIf(nameof(CanFd), true)]
    public bool BitRateSwitch { get; set; }

    [Display("Nominal Bitrate", Group: "CAN", Order: 3)]
    public int NominalBitrate { get; set; } = 500000;

    [Display("Data Bitrate", Group: "CAN", Order: 4)]
    [EnabledIf(nameof(CanFd), true)]
    public int DataBitrate { get; set; } = 2000000;

    [Display("Request ID", Group: "CAN", Order: 5)]
    public uint RequestId { get; set; } = 0x7E0;

    [Display("Response ID", Group: "CAN", Order: 6)]
    public uint ResponseId { get; set; } = 0x7E8;

    [Display("Frame Length", Group: "CAN", Order: 7, Description: "Optional fixed CAN frame length; zero uses the native length.")]
    public int FrameLength { get; set; }

    [Display("Padding Byte", Group: "CAN", Order: 8)]
    public byte PaddingByte { get; set; } = 0xCC;

    public override void Open()
    {
        if (ChannelIndex < 0) throw new ArgumentOutOfRangeException(nameof(ChannelIndex));
        if (NominalBitrate <= 0 || (CanFd && DataBitrate <= 0))
            throw new ArgumentOutOfRangeException(nameof(NominalBitrate), "CAN bitrates must be positive.");

        provider = new ZlgCanDeviceProvider();
        if (!string.IsNullOrWhiteSpace(DeviceSerialNumber))
            provider.OpenDeviceByIdentifier(DeviceAddress, DeviceSerialNumber);
        else
            provider.Configure(DeviceAddress, "");
        try
        {
            transport = new CanIsoTpTransport(provider, CreateOptions(), RequestId, ResponseId);
            transport.Connect();
            IsConnected = true;
        }
        catch
        {
            transport?.Dispose();
            transport = null;
            provider.Dispose();
            provider = null;
            throw;
        }
        StartKeepAliveTimer();
        Log.Info("CAN device initialized: {0}, channel {1}.", DeviceAddress, ChannelIndex);
    }

    public override void Close()
    {
        transactionLock.Wait();
        try
        {
            keepAliveTimer?.Dispose();
            keepAliveTimer = null;
            transport?.Dispose();
            transport = null;
            provider?.Dispose();
            provider = null;
            IsConnected = false;
        }
        finally
        {
            transactionLock.Release();
        }
    }

    public CanChannelOptions CreateOptions() => new()
    {
        ChannelIndex = ChannelIndex,
        NominalBitrate = NominalBitrate,
        DataBitrate = DataBitrate,
        CanFd = CanFd,
        BitRateSwitch = BitRateSwitch,
        CanFdStandard = 0,
        CanFdMode = 0,
        FrameLength = FrameLength,
        PaddingByte = PaddingByte,
        DeviceAddress = DeviceAddress,
        DeviceSerialNumber = DeviceSerialNumber
    };

    public CanIsoTpTransport CreateTransport(uint requestId, uint responseId)
    {
        if (provider == null || !IsConnected)
            throw new InvalidOperationException("CAN instrument is not open.");
        return new CanIsoTpTransport(provider, CreateOptions(), requestId, responseId);
    }

    public UdsStepResult Execute(UdsStepConfig config)
    {
        transactionLock.Wait();
        try
        {
            config.IsCan = true;
            int channelIndex = config.HasExplicitCanChannel ? config.CanChannelIndex : ChannelIndex;
            int nominalBitrate = config.HasExplicitCanNominalBitrate ? config.CanNominalBitrate : NominalBitrate;
            int dataBitrate = config.HasExplicitCanDataBitrate ? config.CanDataBitrate : DataBitrate;
            bool canFd = config.HasExplicitCanFd ? config.CanFd : CanFd;
            bool bitRateSwitch = config.HasExplicitCanBitRateSwitch ? config.CanBitRateSwitch : BitRateSwitch;
            uint requestId = config.HasExplicitCanRequestId ? config.CanRequestId : RequestId;
            uint responseId = config.HasExplicitCanResponseId ? config.CanResponseId : ResponseId;
            config.CanChannelIndex = channelIndex;
            config.CanNominalBitrate = nominalBitrate;
            config.CanDataBitrate = dataBitrate;
            config.CanFd = canFd;
            config.CanBitRateSwitch = bitRateSwitch;
            config.CanRequestId = requestId;
            config.CanResponseId = responseId;
            int fdStandard = config.CanFdStandard;
            int fdMode = config.CanFdMode;
            string customBitrateConfig = config.CanCustomBitrateConfig ?? "";
            EnsureTransport(channelIndex, nominalBitrate, dataBitrate, canFd, bitRateSwitch, fdStandard, fdMode, customBitrateConfig, requestId, responseId, config.CanFrameLength, config.CanPaddingByte);
            if (transport == null)
                throw new InvalidOperationException("CAN channel is not open.");
            if (channelIndex != ChannelIndex || nominalBitrate != NominalBitrate || dataBitrate != DataBitrate || canFd != CanFd || bitRateSwitch != BitRateSwitch ||
                (config.HasExplicitCanFdStandard && fdStandard != transport.Options.CanFdStandard) ||
                (config.HasExplicitCanFdMode && fdMode != transport.Options.CanFdMode) ||
                (config.HasExplicitCanCustomBitrateConfig && !string.Equals(customBitrateConfig, transport.Options.CustomBitrateConfig, StringComparison.Ordinal)) )
            {
                ReopenTransport(channelIndex, nominalBitrate, dataBitrate, canFd, bitRateSwitch, fdStandard, fdMode, customBitrateConfig, requestId, responseId, config.CanFrameLength, config.CanPaddingByte);
            }
            transport.SetAddresses(requestId, responseId);
            transport.Options.FrameLength = config.CanFrameLength;
            transport.Options.PaddingByte = config.CanPaddingByte;
            keepAlivePayload = ParsePayload(config.KeepAlivePayload);
            StartKeepAliveTimer(config.KeepAliveEnabled, config.KeepAliveIntervalMs);
            Log.Info("CAN UDS effective settings: channel {0}, request 0x{1:X}, response 0x{2:X}, keep-alive {3} every {4} ms, payload {5}.",
                channelIndex, requestId, responseId, config.KeepAliveEnabled, config.KeepAliveIntervalMs, config.KeepAlivePayload);
            if (config.ConnectionOnly)
                return new UdsStepResult { Success = true, ResultText = "PASS" };
            UdsStepResult result = UdsTransactionExecutor.Execute(config, transport);
            if (config.CloseConnection || string.Equals(config.ConnectionMode, "perStep", StringComparison.OrdinalIgnoreCase))
            {
                transport.Dispose();
                transport = null;
                IsConnected = false;
            }
            return result;
        }
        finally
        {
            transactionLock.Release();
        }
    }

    public IList<CanFrame> ReceiveRaw(int timeoutMilliseconds, int receiveMode)
    {
        transactionLock.Wait();
        try
        {
            if (!IsConnected || transport == null)
                throw new InvalidOperationException("CAN instrument is not open.");
            return transport.ReceiveRaw(timeoutMilliseconds, receiveMode);
        }
        finally
        {
            transactionLock.Release();
        }
    }

    private void EnsureTransport(int channelIndex, int nominalBitrate, int dataBitrate, bool canFd, bool bitRateSwitch, int fdStandard, int fdMode, string customBitrateConfig, uint requestId, uint responseId, int frameLength, byte paddingByte)
    {
        if (transport == null)
            ReopenTransport(channelIndex, nominalBitrate, dataBitrate, canFd, bitRateSwitch, fdStandard, fdMode, customBitrateConfig, requestId, responseId, frameLength, paddingByte);
    }

    private void ReopenTransport(int channelIndex, int nominalBitrate, int dataBitrate, bool canFd, bool bitRateSwitch, int fdStandard, int fdMode, string customBitrateConfig, uint requestId, uint responseId, int frameLength, byte paddingByte)
    {
        transport?.Dispose();
        transport = new CanIsoTpTransport(provider!, new CanChannelOptions
        {
            ChannelIndex = channelIndex, NominalBitrate = nominalBitrate, DataBitrate = dataBitrate,
            CanFd = canFd, BitRateSwitch = bitRateSwitch, CanFdStandard = fdStandard, CanFdMode = fdMode,
            CustomBitrateConfig = customBitrateConfig, FrameLength = frameLength, PaddingByte = paddingByte,
            DeviceAddress = DeviceAddress, DeviceSerialNumber = DeviceSerialNumber
        }, requestId, responseId);
        transport.Connect();
        IsConnected = true;
    }

    private void StartKeepAliveTimer()
    {
        StartKeepAliveTimer(false, 0);
    }

    private void StartKeepAliveTimer(bool enabled, int intervalMs)
    {
        keepAliveTimer?.Dispose();
        keepAliveTimer = null;
        if (enabled && intervalMs > 0 && keepAlivePayload.Length > 0)
            keepAliveTimer = new Timer(SendKeepAlive, null, intervalMs, intervalMs);
    }

    private void SendKeepAlive(object? state)
    {
        if (keepAlivePayload.Length == 0 || !transactionLock.Wait(0)) return;
        try { transport?.SendNoResponse(keepAlivePayload); }
        catch (Exception ex) { Log.Debug("CAN keep-alive skipped: {0}", ex.Message); }
        finally { transactionLock.Release(); }
    }

    private static byte[] ParsePayload(string value)
    {
        string[] parts = (value ?? "").Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        byte[] result = new byte[parts.Length];
        for (int index = 0; index < parts.Length; index++) result[index] = Convert.ToByte(parts[index], 16);
        return result;
    }
}