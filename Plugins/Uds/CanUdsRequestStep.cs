using System;
using System.Collections.Generic;
using OpenTap;
using UdsCore;
using UdsEngine;

namespace Tap.Plugins.Uds;

[Display("CAN UDS Request", Group: "UDS", Description: "Executes a UDS request through a CAN or CAN-FD instrument.")]
public sealed class CanUdsRequestStep : TestStep
{
    [Display("CAN Instrument", Order: 1, Description: "Select a CAN instrument. CAN channel, bitrate, frame format, and request/response IDs are configured on the instrument.")]
    public CanInstrument Instrument { get; set; } = null!;

    [Display("Connection Only", Group: "Connection", Order: 2, Description: "Initialize the CAN device without sending a UDS request.")]
    public bool ConnectionOnly { get; set; }

    [Display("Request Configuration", Group: "UDS", Order: 1, Description: "Request key=value configuration. Custom values override the structured UDS fields below.")]
    public string Config1 { get; set; } = "udsPayload=10 01,expectedPrefix=50 01";

    [Display("UDS Payload", Group: "UDS", Order: 2, Description: "Structured request bytes. Used when Request Configuration is empty or still at its default.")]
    public string UdsPayload { get; set; } = "10 01";

    [Display("Expected Prefix", Group: "UDS", Order: 3)]
    public string ExpectedPrefix { get; set; } = "50 01";

    [Display("Expected Response", Group: "UDS", Order: 4)]
    public string ExpectedResponse { get; set; } = "";

    [Display("Timeout (ms)", Group: "UDS", Order: 5)]
    public int TimeoutMs { get; set; } = 5000;

    [Display("Decode Configuration", Group: "UDS", Order: 6)]
    public string DecodeConfig { get; set; } = "";

    [Display("Response", Group: "Results", Order: 1)]
    [Output]
    public string Response { get; private set; } = "";

    [Display("Measured Value", Group: "Results", Order: 2)]
    [Output]
    public string MeasuredValue { get; private set; } = "";

    public CanUdsRequestStep()
    {
        Rules.Add(() => Instrument != null, "A UDS instrument must be selected.", nameof(Instrument));
        Rules.Add(() => !string.IsNullOrWhiteSpace(Config1), "Request configuration cannot be empty.", nameof(Config1));
        Rules.Add(() => ConnectionOnly || !string.IsNullOrWhiteSpace(UdsPayload), "UDS payload cannot be empty unless Connection Only is enabled.", nameof(UdsPayload));
        Rules.Add(() => TimeoutMs > 0, "Timeout must be positive.", nameof(TimeoutMs));
    }

    public override void Run()
    {
        var canInstrument = Instrument;
        var configText = Config1 ?? "";
        var isNewDefaultConfiguration = string.Equals(configText.Trim(), "udsPayload=10 01,expectedPrefix=50 01", StringComparison.OrdinalIgnoreCase);
        if (isNewDefaultConfiguration || string.IsNullOrWhiteSpace(configText))
        {
            configText = "";
            if (!string.IsNullOrWhiteSpace(UdsPayload)) configText += "udsPayload=" + UdsPayload;
            if (!string.IsNullOrWhiteSpace(ExpectedPrefix)) configText += (configText.Length == 0 ? "" : ",") + "expectedPrefix=" + ExpectedPrefix;
            if (!string.IsNullOrWhiteSpace(ExpectedResponse)) configText += (configText.Length == 0 ? "" : ",") + "expectedResponse=" + ExpectedResponse;
            configText += (configText.Length == 0 ? "" : ",") + "timeoutMs=" + TimeoutMs;
        }
        var config = UdsStepConfig.FromColumns(
            configText,
            DecodeConfig ?? "");
        Log.Info("CAN UDS decode configuration input: length={0}, value={1}.",
            (DecodeConfig ?? "").Length,
            string.IsNullOrWhiteSpace(DecodeConfig) ? "<empty>" : DecodeConfig.Trim());
        config.ConnectionOnly = ConnectionOnly || config.ConnectionOnly;
        var result = UdsStepRunner.Execute(Instrument, config);
        Response = result.CombinedResponseHex ?? "";
        MeasuredValue = result.FirstValue == null ? "" : Convert.ToString(result.FirstValue, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        Log.Info("CAN UDS decode: configured={0}, measurements={1}, value={2}, unit={3}.",
            config.HasDecodeConfig, result.Decode == null ? 0 : result.Decode.Measurements.Count,
            MeasuredValue, result.FirstUnit ?? "");
        if (result.Success)
        {
            UpgradeVerdict(Verdict.Pass);
            Log.Info("CAN UDS PASS: {0} -> {1}", result.Step.Request, Response);
        }
        else
        {
            UpgradeVerdict(Verdict.Fail);
            Log.Error("CAN UDS FAIL: {0}; response={1}; decode={2}",
                result.Error ?? result.ResultText ?? "Unknown error", Response,
                result.Decode == null ? "none" : result.Decode.ErrorMessage);
        }
        Results.Publish("CAN UDS", new List<string> { "Request", "Response", "Verdict", "Measured Value", "Unit", "Error" },
            result.Step.Request ?? "", Response, result.Success ? "PASS" : "FAIL", MeasuredValue,
            result.FirstUnit ?? "", result.Error ?? "");
    }
}