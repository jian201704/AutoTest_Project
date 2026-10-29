using OpenTap;

namespace OpenTap.Poc;

[Display("PoC Check", Description: "Compares a measured value with an expected value.", Group: "OpenTAP PoC")]
public sealed class PocCheckStep : TestStep
{
    [Display("Measured Value", Order: 1)]
    public int MeasuredValue { get; set; }

    [Display("Expected Value", Order: 2)]
    public int ExpectedValue { get; set; }

    public override void Run()
    {
        if (MeasuredValue == ExpectedValue)
        {
            Log.Info("PoC check passed: {0} == {1}.", MeasuredValue, ExpectedValue);
            UpgradeVerdict(Verdict.Pass);
            return;
        }

        Log.Error("PoC check failed: {0} != {1}.", MeasuredValue, ExpectedValue);
        UpgradeVerdict(Verdict.Fail);
    }
}