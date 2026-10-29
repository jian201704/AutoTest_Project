using System.Globalization;
using System.Diagnostics;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenTap;

namespace Tap.Plugins.Workflow;

[Display("SCPI VISA Instrument", Group: "Instruments", Description: "VISA instrument for SCPI-capable measurement and signal-generation devices.")]
public sealed class ScpiVisaInstrument : ScpiInstrument
{
    public string Query(string command) => ScpiQuery(command);
    public void Write(string command) => ScpiCommand(command);

    public string Query(string command, int timeoutMs)
    {
        var previousTimeout = IoTimeout;
        try
        {
            IoTimeout = timeoutMs;
            return ScpiQuery(command);
        }
        finally
        {
            IoTimeout = previousTimeout;
        }
    }
}

[Display("DMM Measurement", Group: "Instruments", Description: "Configures a DMM function and records a measured value with optional limits.")]
public sealed class DmmMeasurementStep : TestStep
{
    [Display("Instrument", Order: 1)]
    public ScpiVisaInstrument Instrument { get; set; } = null!;

    [Display("Function", Order: 2, Description: "VOLT_DC, VOLT_AC, CURR_DC, CURR_AC, RES, CAP, or FREQ.")]
    public string Function { get; set; } = "VOLT_DC";

    [Display("Range", Order: 3)]
    public double Range { get; set; } = 10;

    [Display("NPLC", Order: 4)]
    public double Nplc { get; set; } = 1;

    [Display("Scale", Order: 5)]
    public double Scale { get; set; } = 1;

    [Display("Offset", Order: 6)]
    public double Offset { get; set; }

    [Display("Absolute Current", Order: 7)]
    public bool AbsoluteCurrent { get; set; } = true;

    [Display("Lower Limit", Order: 8)]
    public double LowerLimit { get; set; } = double.NegativeInfinity;

    [Display("Upper Limit", Order: 9)]
    public double UpperLimit { get; set; } = double.PositiveInfinity;

    [Display("Unit", Order: 10)]
    public string Unit { get; set; } = "";

    [Display("Measured Value", Group: "Results", Order: 1), Output]
    public double MeasuredValue { get; private set; }

    public DmmMeasurementStep()
    {
        Rules.Add(() => Instrument != null, "A DMM instrument must be selected.", nameof(Instrument));
        Rules.Add(() => Range > 0 && Nplc > 0, "Range and NPLC must be positive.", nameof(Range));
        Rules.Add(() => LowerLimit <= UpperLimit, "Lower limit must not exceed upper limit.", nameof(LowerLimit));
    }

    public override void Run()
    {
        var (functionCommand, rangeCommand, nplcCommand, current) = BuildCommands(Function, Range, Nplc);
        Instrument.Write(functionCommand);
        Instrument.Write(rangeCommand);
        if (nplcCommand.Length != 0) Instrument.Write(nplcCommand);
        var response = Instrument.Query("READ?").Trim();
        if (response.Length == 0 || response.Contains("E+37", StringComparison.OrdinalIgnoreCase) || response.Contains("E37", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"DMM returned an empty or over-range value: '{response}'.");
        if (!double.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawValue))
            throw new InvalidDataException($"DMM returned a non-numeric measurement: '{response}'.");
        MeasuredValue = rawValue * Scale + Offset;
        if (current && AbsoluteCurrent) MeasuredValue = Math.Abs(MeasuredValue);
        Results.Publish("DMM", new List<string> { "Function", "Value", "Unit", "Lower", "Upper" }, Function, MeasuredValue, Unit, LowerLimit, UpperLimit);
        if (MeasuredValue < LowerLimit || MeasuredValue > UpperLimit)
        {
            Log.Error("DMM value {0} {1} is outside [{2}, {3}].", MeasuredValue, Unit, LowerLimit, UpperLimit);
            UpgradeVerdict(Verdict.Fail);
            return;
        }
        UpgradeVerdict(Verdict.Pass);
    }

    internal static (string Function, string Range, string Nplc, bool IsCurrent) BuildCommands(string function, double range, double nplc)
    {
        var normalized = function.Trim().ToUpperInvariant().Replace('-', '_');
        var (sense, rangePrefix, needsNplc, current) = normalized switch
        {
            "RES" or "RESISTANCE" => ("RES", "RES", true, false),
            "CAP" or "CAPACITANCE" => ("CAP", "CAP", false, false),
            "VOLT" or "VOLT_DC" => ("VOLT", "VOLT", true, false),
            "VOLT_AC" => ("VOLT:AC", "VOLT:AC", false, false),
            "CURR" or "CURR_DC" => ("CURR", "CURR", true, true),
            "CURR_AC" => ("CURR:AC", "CURR:AC", false, true),
            "FREQ" or "FREQUENCY" => ("FREQ", "FREQ", false, false),
            _ => throw new InvalidDataException($"Unsupported DMM function '{function}'.")
        };
        return ($"SENS:FUNC '{sense}'", $"SENS:{rangePrefix}:RANG {range.ToString(CultureInfo.InvariantCulture)}",
            needsNplc ? $"SENS:{rangePrefix}:NPLC {nplc.ToString(CultureInfo.InvariantCulture)}" : "", current);
    }
}

[Display("DG1022 Waveform", Group: "Instruments", Description: "Configures a DG1022 channel waveform and output.")]
public sealed class Dg1022WaveformStep : TestStep
{
    [Display("Instrument", Order: 1)] public ScpiVisaInstrument Instrument { get; set; } = null!;
    [Display("Channel", Order: 2)] public int Channel { get; set; } = 1;
    [Display("Waveform", Order: 3)] public string Waveform { get; set; } = "SQU";
    [Display("Amplitude", Order: 4), Unit("Vpp")] public double Amplitude { get; set; } = 10;
    [Display("Offset", Order: 5), Unit("V")] public double Offset { get; set; } = 5;
    [Display("Frequency", Order: 6), Unit("Hz")] public double Frequency { get; set; } = 1000;
    [Display("Duty Cycle", Order: 7), Unit("%")] public double DutyCyclePercent { get; set; } = 50;
    [Display("Output Enabled", Order: 8)] public bool OutputEnabled { get; set; } = true;
    [Display("Waveform Set Attempts", Order: 9)] public int WaveformSetAttempts { get; set; } = 3;
    [Display("Output State", Group: "Results", Order: 1), Output] public string OutputState { get; private set; } = "";

    public Dg1022WaveformStep()
    {
        Rules.Add(() => Instrument != null, "A DG1022 VISA instrument must be selected.", nameof(Instrument));
        Rules.Add(() => Channel is 1 or 2, "DG1022 channel must be 1 or 2.", nameof(Channel));
        Rules.Add(() => Amplitude >= 0 && Frequency > 0 && DutyCyclePercent is > 0 and < 100 && WaveformSetAttempts > 0,
            "Waveform parameters or verification attempts are outside valid ranges.", nameof(Amplitude));
    }

    public override void Run()
    {
        var ch2 = Channel == 2;
        var channel = ch2 ? ":CH2" : "";
        var outputCommand = ch2 ? "OUTP:CH2" : "OUTP";
        Instrument.Write(outputCommand + " OFF");
        if (!OutputEnabled)
        {
            OutputState = "OFF";
            Results.Publish("DG1022", new List<string> { "Channel", "Output" }, Channel, OutputState);
            UpgradeVerdict(Verdict.Pass);
            return;
        }
        Task.Delay(50, TapThread.Current.AbortToken).GetAwaiter().GetResult();
        Instrument.Write($"FUNC{channel} {Waveform}");
        Task.Delay(300, TapThread.Current.AbortToken).GetAwaiter().GetResult();
        var functionQuery = ch2 ? "FUNC:CH2?" : "FUNC?";
        var waveformVerified = false;
        for (var attempt = 0; attempt < WaveformSetAttempts; attempt++)
        {
            var actualWaveform = Instrument.Query(functionQuery).Trim();
            if (actualWaveform.Contains(Waveform, StringComparison.OrdinalIgnoreCase))
            {
                waveformVerified = true;
                break;
            }
            if (attempt + 1 < WaveformSetAttempts)
            {
                Instrument.Write($"FUNC{channel} {Waveform}");
                Task.Delay(300, TapThread.Current.AbortToken).GetAwaiter().GetResult();
            }
        }
        if (!waveformVerified) throw new InvalidOperationException($"DG1022 did not confirm waveform '{Waveform}'.");
        Instrument.Write(ch2 ? "VOLT:UNIT:CH2 VPP" : "VOLT:UNIT VPP");
        Instrument.Write(ch2
            ? $"VOLT:CH2 {Amplitude.ToString(CultureInfo.InvariantCulture)}"
            : $"VOLT {Amplitude.ToString(CultureInfo.InvariantCulture)}");
        Instrument.Write(ch2
            ? $"VOLT:OFFS:CH2 {Offset.ToString(CultureInfo.InvariantCulture)}"
            : $"VOLT:OFFS {Offset.ToString(CultureInfo.InvariantCulture)}");
        Instrument.Write(ch2
            ? $"FREQ:CH2 {Frequency.ToString(CultureInfo.InvariantCulture)}"
            : $"FREQ {Frequency.ToString(CultureInfo.InvariantCulture)}");
        if (Waveform.Equals("SQU", StringComparison.OrdinalIgnoreCase) || Waveform.Equals("SQUARE", StringComparison.OrdinalIgnoreCase))
            Instrument.Write(ch2
                ? $"FUNC:SQU:DCYC:CH2 {DutyCyclePercent.ToString(CultureInfo.InvariantCulture)}"
                : $"FUNC:SQU:DCYC {DutyCyclePercent.ToString(CultureInfo.InvariantCulture)}");
        if (OutputEnabled) Instrument.Write(outputCommand + " ON");
        Task.Delay(2000, TapThread.Current.AbortToken).GetAwaiter().GetResult();
        OutputState = OutputEnabled ? "ON" : "OFF";
        Results.Publish("DG1022", new List<string> { "Channel", "Waveform", "Amplitude (Vpp)", "Frequency (Hz)", "Output" }, Channel, Waveform, Amplitude, Frequency, OutputState);
        UpgradeVerdict(Verdict.Pass);
    }
}

[Display("Serial Command/Response", Group: "Instruments", Description: "Sends a raw serial command and validates the device response.")]
public sealed class SerialCommandStep : TestStep
{
    [Display("Instrument", Order: 1)] public SerialCommandInstrument Instrument { get; set; } = null!;
    [Display("Command", Order: 2)] public string Command { get; set; } = "";
    [Display("Expected Response Text", Order: 3)] public string ExpectedResponse { get; set; } = "";
    [Display("Response Timeout", Order: 4), Unit("ms")] public int ResponseTimeoutMs { get; set; } = 500;
    [Display("Attempts", Order: 5)] public int Attempts { get; set; } = 3;
    [Display("Retry Delay", Order: 6), Unit("ms")] public int RetryDelayMs { get; set; } = 80;
    [Display("Settle Delay", Order: 7), Unit("ms")] public int SettleDelayMs { get; set; } = 0;
    [Display("Response", Group: "Results", Order: 1), Output] public string Response { get; private set; } = "";

    public SerialCommandStep()
    {
        Rules.Add(() => Instrument != null, "A raw serial command instrument must be selected.", nameof(Instrument));
        Rules.Add(() => !string.IsNullOrWhiteSpace(Command), "Command cannot be empty.", nameof(Command));
        Rules.Add(() => ResponseTimeoutMs > 0 && Attempts > 0 && RetryDelayMs >= 0 && SettleDelayMs >= 0,
            "Timeout and attempts must be positive; delays cannot be negative.", nameof(ResponseTimeoutMs));
    }

    public override void Run()
    {
        var command = Command.EndsWith("\r\n", StringComparison.Ordinal) || Command.EndsWith("\n", StringComparison.Ordinal)
            ? Command : Command + "\r\n";
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            TapThread.Current.AbortToken.ThrowIfCancellationRequested();
            Response = Instrument.Exchange(command, ResponseTimeoutMs).Trim();
            if (Response.Contains(ExpectedResponse, StringComparison.OrdinalIgnoreCase))
            {
                if (SettleDelayMs > 0) Task.Delay(SettleDelayMs, TapThread.Current.AbortToken).GetAwaiter().GetResult();
                Results.Publish("Command Response", new List<string> { "Command", "Response", "Attempt" }, Command, Response, attempt);
                UpgradeVerdict(Verdict.Pass);
                return;
            }
            if (attempt < Attempts && RetryDelayMs > 0)
                Task.Delay(RetryDelayMs, TapThread.Current.AbortToken).GetAwaiter().GetResult();
        }
        Log.Error("Device response did not contain '{0}'. Last response: '{1}'.", ExpectedResponse, Response);
        Results.Publish("Command Response", new List<string> { "Command", "Expected", "Response", "Attempts" }, Command, ExpectedResponse, Response, Attempts);
        UpgradeVerdict(Verdict.Fail);
    }
}

[Display("Dual XPU TCP", Group: "Instruments", Description: "Maintains TCP connections to XPU A and B for paired SOC image programming.")]
public sealed class DualXpuTcpInstrument : Instrument
{
    [Display("XPU A Address", Group: "Connection", Order: 1)] public string XpuAAddress { get; set; } = "";
    [Display("XPU B Address", Group: "Connection", Order: 2)] public string XpuBAddress { get; set; } = "";
    [Display("Port", Group: "Connection", Order: 3)] public int Port { get; set; } = 7057;
    [Display("Connect Timeout", Group: "Connection", Order: 4), Unit("ms")] public int ConnectTimeoutMs { get; set; } = 2000;
    [Display("Expected Project", Group: "SOC Flash", Order: 1)] public string ExpectedProject { get; set; } = "";
    [Display("Expected Version", Group: "SOC Flash", Order: 2)] public string ExpectedVersion { get; set; } = "";
    [Display("Handshake Hex", Group: "SOC Flash", Order: 3)] public string HandshakeHex { get; set; } = "";
    [Display("Handshake Hex File", Group: "SOC Flash", Order: 3.1)] public string HandshakeHexFile { get; set; } = "";
    [FilePath(FilePathAttribute.BehaviorChoice.Open, "SOC profile (*.json)|*.json")]
    [Display("SOC Protocol Profile", Group: "SOC Flash", Order: 4)] public string ProtocolProfileFile { get; set; } = "";

    private TcpClient? xpuA;
    private TcpClient? xpuB;
    public SocFlashProtocolProfile? LoadedProtocolProfile { get; private set; }

    public void LoadProtocolProfile()
    {
        LoadedProtocolProfile = null;
        if (!string.IsNullOrWhiteSpace(ProtocolProfileFile))
        {
            var profilePath = Path.GetFullPath(ProtocolProfileFile);
            var profile = JsonSerializer.Deserialize<SocFlashProtocolProfile>(File.ReadAllText(profilePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"SOC protocol profile '{profilePath}' is empty.");
            ExpectedProject = profile.ExpectedProject;
            ExpectedVersion = profile.ExpectedVersion;
            HandshakeHex = profile.HandshakeHex;
            if (string.IsNullOrWhiteSpace(HandshakeHex) && !string.IsNullOrWhiteSpace(profile.HandshakeHexFile))
                HandshakeHex = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(profilePath)!, profile.HandshakeHexFile)));
            if (string.IsNullOrWhiteSpace(ExpectedProject) || string.IsNullOrWhiteSpace(ExpectedVersion) || string.IsNullOrWhiteSpace(HandshakeHex))
                throw new InvalidDataException($"SOC protocol profile '{profilePath}' must define project, version, and handshake data.");
            LoadedProtocolProfile = profile;
        }
    }

    public override void Open()
    {
        LoadProtocolProfile();
        if (!System.Net.IPAddress.TryParse(XpuAAddress, out _) || !System.Net.IPAddress.TryParse(XpuBAddress, out _))
            throw new InvalidDataException("XPU A and B addresses must be valid IP addresses.");
        if (Port is < 1 or > 65535 || ConnectTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(Port));
        try
        {
            xpuA = Connect(XpuAAddress);
            xpuB = Connect(XpuBAddress);
            IsConnected = true;
        }
        catch
        {
            Close();
            throw;
        }
    }

    public override void Close()
    {
        xpuA?.Dispose(); xpuA = null;
        xpuB?.Dispose(); xpuB = null;
        IsConnected = false;
    }

    public void Send(bool unitA, byte[] data) => GetStream(unitA).Write(data);

    public byte[] Receive(bool unitA, int timeoutMs)
    {
        var client = unitA ? xpuA : xpuB;
        var stream = GetStream(unitA);
        var deadline = Stopwatch.StartNew();
        var buffer = new byte[8192];
        using var result = new MemoryStream();
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            if (stream.DataAvailable)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) throw new EndOfStreamException($"XPU {(unitA ? "A" : "B")} closed the TCP connection.");
                result.Write(buffer, 0, read);
                if (client!.Available == 0) break;
            }
            else
            {
                Task.Delay(10).GetAwaiter().GetResult();
            }
        }
        return result.ToArray();
    }

    private TcpClient Connect(string address)
    {
        var client = new TcpClient();
        try
        {
            client.ConnectAsync(address, Port).Wait(ConnectTimeoutMs);
            if (!client.Connected) throw new TimeoutException($"Connection to {address}:{Port} timed out.");
            client.ReceiveTimeout = ConnectTimeoutMs;
            client.SendTimeout = ConnectTimeoutMs;
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private NetworkStream GetStream(bool unitA)
        => (unitA ? xpuA : xpuB)?.GetStream() ?? throw new InvalidOperationException("Dual XPU TCP instrument is not open.");
}

public sealed class SocFlashProtocolProfile
{
    public string ExpectedProject { get; set; } = "";
    public string ExpectedVersion { get; set; } = "";
    public string HandshakeHex { get; set; } = "";
    public string HandshakeHexFile { get; set; } = "";
    public string VersionQueryHex { get; set; } = "F4 03 00 00 00 00 00 00 00 00 00 00";
    public string StartHex { get; set; } = "38 08 00 00 00 00 00 00 00 00 00 00";
    public string StatusQueryHex { get; set; } = "EC 03 00 00 00 00 00 00 00 00 00 00";
    public string RunningSignatureHex { get; set; } = "EC 03 00 00 38 08 00 00 01 00 00 00 30";
    public string PassSignatureHex { get; set; } = "38 08 00 00 01 00 00 00 03 00 00 00 31 30 30";
    public string FailSignatureHex { get; set; } = "38 08 00 00 02 00 00 00 00 00 00 00";
    public string FreeSignatureHex { get; set; } = "EC 03 00 00 01 00 00 00 00 00 00 00";
    public int ResponseTimeoutMs { get; set; } = 1000;
    public int HandshakeSettleDelayMs { get; set; } = 100;
    public int BeforeStartDelayMs { get; set; } = 1000;
    public int StartDrainDelayMs { get; set; } = 1000;
    public int InitialFlashDelayMs { get; set; } = 5000;
    public int PollIntervalMs { get; set; } = 1000;
    public int MaxPollCount { get; set; } = 1000;
    public int OverallTimeoutMs { get; set; }
    public int PostFlashDrainDelayMs { get; set; } = 1000;
    public int PostFlashDrainTimeoutMs { get; set; } = 1000;
}

[Display("SOC Flash A/B", Group: "Programming", Description: "Checks project/version, starts paired SOC flashing, and polls each XPU until pass/fail or timeout.")]
public sealed class SocFlashStep : TestStep
{
    [Display("Instrument", Order: 1)] public DualXpuTcpInstrument Instrument { get; set; } = null!;
    [Display("Project", Order: 2)] public string ExpectedProject { get; set; } = "";
    [Display("Version", Order: 3)] public string ExpectedVersion { get; set; } = "";
    [Display("XPU A Result", Group: "Results", Order: 1), Output] public string XpuAResult { get; private set; } = "NotStarted";
    [Display("XPU B Result", Group: "Results", Order: 2), Output] public string XpuBResult { get; private set; } = "NotStarted";

    public SocFlashStep()
    {
        Rules.Add(() => Instrument != null, "A Dual XPU TCP instrument must be selected.", nameof(Instrument));
    }

    public override void Run()
    {
        var profile = Instrument.LoadedProtocolProfile ?? new SocFlashProtocolProfile();
        var expectedProject = ExpectedProject.Length == 0 ? Instrument.ExpectedProject : ExpectedProject;
        var expectedVersion = ExpectedVersion.Length == 0 ? Instrument.ExpectedVersion : ExpectedVersion;
        var handshake = ParseHex(Instrument.HandshakeHex);
        if (string.IsNullOrWhiteSpace(expectedProject) || string.IsNullOrWhiteSpace(expectedVersion) || handshake.Length == 0)
            throw new InvalidDataException("Configure a SOC protocol profile or expected project, version, and handshake on the Dual XPU TCP instrument.");
        ValidateProfile(profile);
        var versionQuery = ParseHex(profile.VersionQueryHex);
        var start = ParseHex(profile.StartHex);
        var statusQuery = ParseHex(profile.StatusQueryHex);
        var free = ParseHex(profile.FreeSignatureHex);
        var running = ParseHex(profile.RunningSignatureHex);
        var passed = ParseHex(profile.PassSignatureHex);
        var failed = ParseHex(profile.FailSignatureHex);

        ExchangeHandshake(true, handshake);
        ExchangeHandshake(false, handshake);
        Delay(profile.HandshakeSettleDelayMs);
        Instrument.Send(true, versionQuery); Instrument.Send(false, versionQuery);
        var versionA = Encoding.ASCII.GetString(Instrument.Receive(true, profile.ResponseTimeoutMs));
        var versionB = Encoding.ASCII.GetString(Instrument.Receive(false, profile.ResponseTimeoutMs));
        VerifyVersion("A", versionA, expectedProject, expectedVersion);
        VerifyVersion("B", versionB, expectedProject, expectedVersion);

        Delay(profile.BeforeStartDelayMs);
        Instrument.Send(true, start); Instrument.Send(false, start);
        XpuAResult = "Running"; XpuBResult = "Running";
        var timer = Stopwatch.StartNew();
        Delay(profile.StartDrainDelayMs);
        Instrument.Receive(true, profile.ResponseTimeoutMs);
        Instrument.Receive(false, profile.ResponseTimeoutMs);
        Delay(profile.InitialFlashDelayMs);
        var pollCount = 0;
        while (pollCount < profile.MaxPollCount &&
            (profile.OverallTimeoutMs <= 0 || timer.ElapsedMilliseconds < profile.OverallTimeoutMs) &&
            (XpuAResult == "Running" || XpuBResult == "Running"))
        {
            TapThread.Current.AbortToken.ThrowIfCancellationRequested();
            if (XpuAResult == "Running") XpuAResult = PollUnit(true, statusQuery, free, running, passed, failed, profile.ResponseTimeoutMs);
            if (XpuBResult == "Running") XpuBResult = PollUnit(false, statusQuery, free, running, passed, failed, profile.ResponseTimeoutMs);
            pollCount++;
            Delay(profile.PollIntervalMs);
        }
        if (XpuAResult == "Running") XpuAResult = "Timeout";
        if (XpuBResult == "Running") XpuBResult = "Timeout";
        Delay(profile.PostFlashDrainDelayMs);
        Instrument.Receive(true, profile.PostFlashDrainTimeoutMs);
        Instrument.Receive(false, profile.PostFlashDrainTimeoutMs);
        Results.Publish("SOC Flash", new List<string> { "Project", "Version", "XPU A", "XPU B", "Elapsed (ms)" }, expectedProject, expectedVersion, XpuAResult, XpuBResult, timer.ElapsedMilliseconds);
        if (XpuAResult == "Pass" && XpuBResult == "Pass") UpgradeVerdict(Verdict.Pass);
        else
        {
            Log.Error("SOC Flash failed: XPU A={0}, XPU B={1}.", XpuAResult, XpuBResult);
            UpgradeVerdict(Verdict.Fail);
        }
    }

    private void ExchangeHandshake(bool unitA, byte[] handshake)
    {
        Instrument.Send(unitA, handshake);
        Instrument.Receive(unitA, Instrument.LoadedProtocolProfile?.ResponseTimeoutMs ?? 1000);
    }

    private void VerifyVersion(string unit, string response, string expectedProject, string expectedVersion)
    {
        var project = ExtractLineValue(response, "project=");
        var version = ExtractLineValue(response, "version=");
        Results.Publish($"XPU {unit} Version", new List<string> { "Project", "Expected Project", "Version", "Expected Version" },
            project, expectedProject, version, expectedVersion);
        if (!string.Equals(project, expectedProject, StringComparison.Ordinal) || !string.Equals(version, expectedVersion, StringComparison.Ordinal))
            throw new InvalidOperationException($"XPU {unit} project/version mismatch: '{project}'/'{version}', expected '{expectedProject}'/'{expectedVersion}'.");
    }

    private string PollUnit(bool unitA, byte[] query, byte[] free, byte[] running, byte[] passed, byte[] failed, int responseTimeoutMs)
    {
        Instrument.Send(unitA, query);
        var response = Instrument.Receive(unitA, responseTimeoutMs);
        if (Contains(response, passed)) return "Pass";
        if (Contains(response, failed)) return "Fail";
        if (Contains(response, free)) return "Free";
        if (Contains(response, running)) return "Running";
        return "Running";
    }

    private static void Delay(int milliseconds)
    {
        if (milliseconds > 0) Task.Delay(milliseconds, TapThread.Current.AbortToken).GetAwaiter().GetResult();
    }

    private static void ValidateProfile(SocFlashProtocolProfile profile)
    {
        if (profile.ResponseTimeoutMs <= 0 || profile.MaxPollCount <= 0 || profile.PollIntervalMs < 0 ||
            profile.HandshakeSettleDelayMs < 0 || profile.BeforeStartDelayMs < 0 || profile.StartDrainDelayMs < 0 ||
            profile.InitialFlashDelayMs < 0 || profile.OverallTimeoutMs < 0 || profile.PostFlashDrainDelayMs < 0 ||
            profile.PostFlashDrainTimeoutMs <= 0)
            throw new InvalidDataException("SOC protocol profile contains invalid timeout, polling, or delay values.");
    }

    private static string ExtractLineValue(string text, string key)
    {
        var line = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith(key, StringComparison.Ordinal));
        return line == null ? "" : line[key.Length..].Trim();
    }

    private static bool Contains(byte[] source, byte[] pattern)
    {
        if (pattern.Length == 0 || pattern.Length > source.Length) return false;
        for (var offset = 0; offset <= source.Length - pattern.Length; offset++)
            if (source.AsSpan(offset, pattern.Length).SequenceEqual(pattern)) return true;
        return false;
    }

    private static byte[] ParseHex(string value)
        => value.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Convert.ToByte(part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part, 16)).ToArray();
}

[Display("Serial Command Instrument", Group: "Instruments", Description: "Raw serial-port transport for non-SCPI command/response devices.")]
public sealed class SerialCommandInstrument : Instrument
{
    [Display("COM Port", Group: "Serial", Order: 1)] public string PortName { get; set; } = "COM1";
    [Display("Baud Rate", Group: "Serial", Order: 2)] public int BaudRate { get; set; } = 115200;
    [Display("Data Bits", Group: "Serial", Order: 3)] public int DataBits { get; set; } = 8;
    [Display("Parity", Group: "Serial", Order: 4)] public Parity Parity { get; set; } = Parity.None;
    [Display("Stop Bits", Group: "Serial", Order: 5)] public StopBits StopBits { get; set; } = StopBits.One;
    [Display("Handshake", Group: "Serial", Order: 6)] public Handshake Handshake { get; set; } = Handshake.None;
    [Display("Encoding", Group: "Serial", Order: 7)] public string EncodingName { get; set; } = "us-ascii";
    [Display("Response Idle Timeout", Group: "Serial", Order: 8), Unit("ms")] public int ResponseIdleTimeoutMs { get; set; } = 30;

    private readonly object ioLock = new();
    private SerialPort? serialPort;

    public override void Open()
    {
        if (string.IsNullOrWhiteSpace(PortName) || BaudRate <= 0 || DataBits is < 5 or > 8 || ResponseIdleTimeoutMs < 0)
            throw new InvalidDataException("Serial port name, baud rate, data bits, or response idle timeout is invalid.");

        serialPort = new SerialPort(PortName, BaudRate, Parity, DataBits, StopBits)
        {
            Handshake = Handshake,
            Encoding = Encoding.GetEncoding(EncodingName),
            ReadTimeout = 50,
            WriteTimeout = 1000
        };
        serialPort.Open();
        IsConnected = true;
    }

    public override void Close()
    {
        lock (ioLock)
        {
            serialPort?.Dispose();
            serialPort = null;
            IsConnected = false;
        }
    }

    public string Exchange(string command, int timeoutMs)
    {
        var encoding = Encoding.GetEncoding(EncodingName);
        return encoding.GetString(Exchange(encoding.GetBytes(command), timeoutMs));
    }

    public void Send(string command)
    {
        var encoding = Encoding.GetEncoding(EncodingName);
        Send(encoding.GetBytes(command));
    }

    public void Send(byte[] command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (ioLock)
        {
            var port = serialPort ?? throw new InvalidOperationException("Serial command instrument is not open.");
            port.Write(command, 0, command.Length);
        }
    }

    public byte[] Exchange(byte[] command, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));

        lock (ioLock)
        {
            var port = serialPort ?? throw new InvalidOperationException("Serial command instrument is not open.");
            port.Write(command, 0, command.Length);
            var stopwatch = Stopwatch.StartNew();
            var lastByteAtMs = -1L;
            using var response = new MemoryStream();
            var buffer = new byte[256];
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                var available = port.BytesToRead;
                if (available > 0)
                {
                    var read = port.Read(buffer, 0, Math.Min(buffer.Length, available));
                    response.Write(buffer, 0, read);
                    lastByteAtMs = stopwatch.ElapsedMilliseconds;
                    continue;
                }

                if (response.Length > 0 && lastByteAtMs >= 0 &&
                    stopwatch.ElapsedMilliseconds - lastByteAtMs >= ResponseIdleTimeoutMs)
                    break;
                Thread.Sleep(2);
            }
            return response.ToArray();
        }
    }
}