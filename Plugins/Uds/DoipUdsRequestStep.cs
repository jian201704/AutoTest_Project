using System;
using System.Collections.Generic;
using OpenTap;
using UdsCore;

namespace Tap.Plugins.Uds;

[Display("DoIP UDS Request", Group: "UDS", Description: "Executes a UDS request through a DoIP instrument.")]
public sealed class DoipUdsRequestStep : TestStep
{
    [Display("DoIP Instrument", Order: 1, Description: "Select a DoIP instrument. DoIP connection and routing settings are configured on the instrument.")]
    public DoipInstrument Instrument { get; set; } = null!;

    [Display("Connection Only", Group: "Connection", Order: 2, Description: "Open and initialize DoIP without sending a UDS request.")]
    public bool ConnectionOnly { get; set; }

    [Display("Request Configuration", Group: "UDS", Order: 3, Description: "Request key=value configuration.")]
    public string Config1 { get; set; } = "udsPayload=10 01,expectedPrefix=50 01";

    [Display("UDS Payload", Group: "UDS", Order: 4, Description: "Structured request bytes. When set, this overrides udsPayload in Request Configuration.")]
    public string UdsPayload { get; set; } = "10 01";

    [Display("Expected Prefix", Group: "UDS", Order: 4)]
    public string ExpectedPrefix { get; set; } = "50 01";

    [Display("Expected Response", Group: "UDS", Order: 5)]
    public string ExpectedResponse { get; set; } = "";

    [Display("Timeout (ms)", Group: "UDS", Order: 6)]
    public int TimeoutMs { get; set; } = 5000;

    [Display("Decode Configuration", Group: "UDS", Order: 7, Description: "Response decode configuration.")]
    public string DecodeConfig { get; set; } = "";

    [Display("Virtual Mode", Group: "UDS", Order: 8)]
    public bool VirtualMode { get; set; }

    [Display("Simulated Response", Group: "UDS", Order: 9)]
    [EnabledIf(nameof(VirtualMode), true)]
    public string SimulatedResponse { get; set; } = "50 01";

    [Display("Response", Group: "Results", Order: 1)]
    [Output]
    public string Response { get; private set; } = "";

    [Display("Measured Value", Group: "Results", Order: 2)]
    [Output]
    public string MeasuredValue { get; private set; } = "";

    public DoipUdsRequestStep()
    {
        Rules.Add(() => Instrument != null, "A UDS instrument must be selected.", nameof(Instrument));
        Rules.Add(() => !string.IsNullOrWhiteSpace(Config1), "Request configuration cannot be empty.", nameof(Config1));
        Rules.Add(() => TimeoutMs > 0, "Timeout must be positive.", nameof(TimeoutMs));
    }

    public override void Run()
    {
        var configText = Config1 ?? "";
        var isNewDefaultConfiguration = string.Equals(configText.Trim(), "udsPayload=10 01,expectedPrefix=50 01", StringComparison.OrdinalIgnoreCase);
        if (isNewDefaultConfiguration || string.IsNullOrWhiteSpace(configText))
        {
            if (!string.IsNullOrWhiteSpace(UdsPayload)) configText += ",udsPayload=" + UdsPayload;
            if (!string.IsNullOrWhiteSpace(ExpectedPrefix)) configText += ",expectedPrefix=" + ExpectedPrefix;
            if (!string.IsNullOrWhiteSpace(ExpectedResponse)) configText += ",expectedResponse=" + ExpectedResponse;
            configText += ",timeoutMs=" + TimeoutMs;
        }
        var config = UdsStepConfig.FromColumns(configText, DecodeConfig ?? "");
        config.ConnectionOnly = ConnectionOnly;
        if (VirtualMode)
        {
            config.VirtualMode = true;
            config.SimResponse = SimulatedResponse ?? "";
        }

        var result = UdsStepRunner.Execute(Instrument, config);
        Response = result.CombinedResponseHex ?? "";
        MeasuredValue = result.FirstValue == null ? "" : Convert.ToString(result.FirstValue, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        if (result.Success)
        {
            UpgradeVerdict(Verdict.Pass);
            Log.Info("UDS PASS: {0} -> {1}", result.Step.Request, Response);
        }
        else
        {
            UpgradeVerdict(Verdict.Fail);
            Log.Error("UDS FAIL: {0}", result.Error ?? result.ResultText ?? "Unknown error");
        }
        Results.Publish("UDS", new List<string> { "Request", "Response", "Verdict", "Measured Value", "Unit", "Error" },
            result.Step.Request ?? "", Response, result.Success ? "PASS" : "FAIL", MeasuredValue,
            result.FirstUnit ?? "", result.Error ?? "");
    }
}