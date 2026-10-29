using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Jabil.Uds.Can;
using OpenTap;
using Tap.Plugins.Uds;

namespace Tap.Plugins.Workflow;

[Display("Delay", Group: "Workflow", Description: "Waits for a configured duration with plan-abort support.")]
public sealed class DelayTestStep : TestStep
{
    [Display("Duration", Order: 1), Unit("ms")]
    public int DurationMs { get; set; } = 1000;

    public DelayTestStep() => Rules.Add(() => DurationMs >= 0, "Duration cannot be negative.", nameof(DurationMs));

    public override void Run()
    {
        Task.Delay(DurationMs, TapThread.Current.AbortToken).GetAwaiter().GetResult();
        UpgradeVerdict(Verdict.Pass);
    }
}

[Display("Ping Test", Group: "Network", Description: "Pings a target from a selected local IPv4 address and checks loss and mean response time.")]
public sealed class PingTestStep : TestStep
{
    private static readonly Regex ReplyPattern = new(@"(?:time|时间)\s*[=<]\s*(?<milliseconds>\d+(?:\.\d+)?)\s*ms", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Display("Source IPv4", Order: 1)]
    public string SourceAddress { get; set; } = "";

    [Display("Target IPv4", Order: 2)]
    public string TargetAddress { get; set; } = "";

    [Display("Packets Per Attempt", Order: 3)]
    public int PacketCount { get; set; } = 4;

    [Display("Attempt Timeout", Order: 4), Unit("ms")]
    public int PacketTimeoutMs { get; set; } = 1000;

    [Display("Maximum Attempts", Order: 5)]
    public int MaximumAttempts { get; set; } = 5;

    [Display("Retry Delay", Order: 6), Unit("ms")]
    public int RetryDelayMs { get; set; } = 1000;

    [Display("Maximum Packet Loss", Order: 7), Unit("%")]
    public double MaximumPacketLossPercent { get; set; } = 0;

    [Display("Maximum Mean Response", Order: 8), Unit("ms")]
    public double MaximumMeanResponseMs { get; set; } = 100;

    [Display("Packet Loss", Group: "Results", Order: 1), Output]
    public double PacketLossPercent { get; private set; }

    [Display("Mean Response", Group: "Results", Order: 2), Output]
    public double MeanResponseMs { get; private set; }

    public PingTestStep()
    {
        Rules.Add(() => IPAddress.TryParse(SourceAddress, out var source) && source.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork,
            "Source must be an IPv4 address.", nameof(SourceAddress));
        Rules.Add(() => IPAddress.TryParse(TargetAddress, out var target) && target.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork,
            "Target must be an IPv4 address.", nameof(TargetAddress));
        Rules.Add(() => PacketCount > 0 && PacketTimeoutMs > 0 && MaximumAttempts > 0 && RetryDelayMs >= 0,
            "Ping counts and timeout must be positive; retry delay cannot be negative.", nameof(PacketCount));
    }

    public override void Run()
    {
        if (!IPAddress.TryParse(SourceAddress, out var source) || source.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !IPAddress.TryParse(TargetAddress, out var target) || target.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidDataException("Ping requires valid IPv4 source and target addresses.");
        Exception? lastError = null;
        var lastReceived = 0;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            TapThread.Current.AbortToken.ThrowIfCancellationRequested();
            lastReceived = 0;
            try
            {
                var output = RunPingOnce(TapThread.Current.AbortToken);
                var measurements = ParseReplies(output);
                var received = measurements.Count;
                lastReceived = received;
                PacketLossPercent = 100.0 * (PacketCount - received) / PacketCount;
                MeanResponseMs = received == 0 ? double.PositiveInfinity : measurements.Average();
                if (received > 0 && PacketLossPercent <= MaximumPacketLossPercent && MeanResponseMs < MaximumMeanResponseMs)
                {
                    Publish(attempt, received);
                    UpgradeVerdict(Verdict.Pass);
                    return;
                }
                lastError = new InvalidOperationException($"Ping criteria not met: loss={PacketLossPercent.ToString("0.##", CultureInfo.InvariantCulture)}%, mean={MeanResponseMs.ToString("0.##", CultureInfo.InvariantCulture)} ms.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
            }

            if (attempt < MaximumAttempts && RetryDelayMs > 0)
                Task.Delay(RetryDelayMs, TapThread.Current.AbortToken).GetAwaiter().GetResult();
        }

        Publish(MaximumAttempts, lastReceived);
        Log.Error("Ping failed after {0} attempts: {1}", MaximumAttempts, lastError?.Message ?? "no replies");
        UpgradeVerdict(Verdict.Fail);
    }

    private string RunPingOnce(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ping.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add(PacketCount.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-w");
        process.StartInfo.ArgumentList.Add(PacketTimeoutMs.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-S");
        process.StartInfo.ArgumentList.Add(SourceAddress);
        process.StartInfo.ArgumentList.Add(TargetAddress);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
        var output = stdout.GetAwaiter().GetResult() + Environment.NewLine + stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0 && ParseReplies(output).Count == 0)
            throw new InvalidOperationException(output.Trim());
        return output;
    }

    private static List<double> ParseReplies(string output)
        => ReplyPattern.Matches(output).Select(match => double.Parse(match.Groups["milliseconds"].Value, CultureInfo.InvariantCulture)).ToList();

    private void Publish(int attempts, int received)
        => Results.Publish("Ping", new List<string> { "Source", "Target", "Attempts", "Packets Received", "Packet Loss (%)", "Mean (ms)" },
            SourceAddress, TargetAddress, attempts, received, PacketLossPercent, double.IsFinite(MeanResponseMs) ? MeanResponseMs : "N/A");
}

[Display("CAN Raw Frame Receive", Group: "CAN", Description: "Receives CAN/CAN-FD frames and passes when an exact identifier and payload match is observed.")]
public sealed class CanRawReceiveStep : TestStep
{
    [Display("CAN Instrument", Order: 1)]
    public CanInstrument Instrument { get; set; } = null!;

    [Display("Expected Identifier", Order: 2)]
    public string ExpectedIdentifier { get; set; } = "0x101";

    [Display("Expected Data", Order: 3)]
    public string ExpectedData { get; set; } = "01 00";

    [Display("Receive Timeout", Order: 4), Unit("ms")]
    public int ReceiveTimeoutMs { get; set; } = 1000;

    [Display("Maximum Receive Attempts", Order: 5)]
    public int MaximumReceiveAttempts { get; set; } = 10;

    [Display("Receive Mode", Order: 6, Description: "ZLG typed receive mode; XPeng CAN-FD uses 2.")]
    public int ReceiveMode { get; set; } = 2;

    [Display("Observed Frames", Group: "Results", Order: 1), Output]
    public string ObservedFrames { get; private set; } = "";

    public CanRawReceiveStep()
    {
        Rules.Add(() => Instrument != null, "A CAN instrument must be selected.", nameof(Instrument));
        Rules.Add(() => ReceiveTimeoutMs >= 0 && MaximumReceiveAttempts > 0, "Receive timeout must be non-negative and attempts must be positive.", nameof(ReceiveTimeoutMs));
    }

    public override void Run()
    {
        var expectedId = ParseIdentifier(ExpectedIdentifier);
        var expectedData = ParseBytes(ExpectedData);
        var observed = new List<string>();
        for (var attempt = 0; attempt < MaximumReceiveAttempts; attempt++)
        {
            TapThread.Current.AbortToken.ThrowIfCancellationRequested();
            foreach (var frame in Instrument.ReceiveRaw(ReceiveTimeoutMs, ReceiveMode))
            {
                var data = frame.Data ?? Array.Empty<byte>();
                var text = $"0x{frame.Identifier:X}:{FormatHex(data)}";
                if (observed.Count < 30) observed.Add(text);
                if (frame.Identifier == expectedId && data.SequenceEqual(expectedData))
                {
                    ObservedFrames = string.Join(" | ", observed);
                    Results.Publish("CAN Frame", new List<string> { "Expected ID", "Expected Data", "Observed", "Attempts" },
                        $"0x{expectedId:X}", FormatHex(expectedData), ObservedFrames, attempt + 1);
                    UpgradeVerdict(Verdict.Pass);
                    return;
                }
            }
        }

        ObservedFrames = string.Join(" | ", observed);
        Results.Publish("CAN Frame", new List<string> { "Expected ID", "Expected Data", "Observed", "Attempts" },
            $"0x{expectedId:X}", FormatHex(expectedData), ObservedFrames, MaximumReceiveAttempts);
        Log.Error("Expected CAN frame was not received. ID=0x{0:X}, data={1}.", expectedId, FormatHex(expectedData));
        UpgradeVerdict(Verdict.Fail);
    }

    private static uint ParseIdentifier(string value)
    {
        var text = value.Trim();
        return Convert.ToUInt32(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
            text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10);
    }

    private static byte[] ParseBytes(string value)
        => value.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Convert.ToByte(part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part, 16)).ToArray();

    private static string FormatHex(byte[] value) => BitConverter.ToString(value).Replace('-', ' ');
}

[Display("CAN Release", Group: "CAN", Description: "Closes a CAN instrument and releases the provider/device handle.")]
public sealed class CanReleaseStep : TestStep
{
    [Display("CAN Instrument", Order: 1)]
    public CanInstrument Instrument { get; set; } = null!;

    public CanReleaseStep() => Rules.Add(() => Instrument != null, "A CAN instrument must be selected.", nameof(Instrument));

    public override void Run()
    {
        Instrument.Close();
        UpgradeVerdict(Verdict.Pass);
    }
}