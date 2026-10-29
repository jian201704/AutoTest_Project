using OpenTap;

namespace Tap.Plugins.Workflow;

[Display("SCPI Power Supply", Group: "Workflow", Description: "Generic SCPI power supply used by Excel workflows.")]
public sealed class ScpiPowerSupplyInstrument : ScpiInstrument
{
    [Display("Output Off On Close", Group: "Safety", Description: "Turns the output off when the test plan closes the instrument.")]
    public bool OutputOffOnClose { get; set; } = true;

    public void Configure(double voltage, double currentLimit)
    {
        ScpiCommand("VOLT {0}", voltage);
        ScpiCommand("CURR {0}", currentLimit);
    }

    public void SetOutput(bool enabled) => ScpiCommand(enabled ? "OUTP ON" : "OUTP OFF");

    public override void Close()
    {
        try
        {
            if (IsConnected && OutputOffOnClose) SetOutput(false);
        }
        finally
        {
            base.Close();
        }
    }
}

[Display("Power Supply Output", Group: "Workflow", Description: "Configures voltage/current and switches a power supply output.")]
public sealed class PowerSupplyOutputStep : TestStep
{
    [Display("Instrument", Order: 1)]
    public ScpiPowerSupplyInstrument Instrument { get; set; } = null!;

    [Display("Output Enabled", Order: 2)]
    public bool OutputEnabled { get; set; }

    [Display("Voltage", Order: 3), Unit("V")]
    public double Voltage { get; set; } = 15;

    [Display("Current Limit", Order: 4), Unit("A")]
    public double CurrentLimit { get; set; } = 20;

    public PowerSupplyOutputStep() => Rules.Add(() => Instrument != null, "A power supply must be selected.", nameof(Instrument));

    public override void Run()
    {
        if (OutputEnabled) Instrument.Configure(Voltage, CurrentLimit);
        Instrument.SetOutput(OutputEnabled);
        UpgradeVerdict(Verdict.Pass);
    }
}