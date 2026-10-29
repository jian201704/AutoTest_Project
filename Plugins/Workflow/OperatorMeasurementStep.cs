using System.ComponentModel;
using System.Globalization;
using OpenTap;

namespace Tap.Plugins.Workflow;

[Display("Operator Measurement", Group: "Workflow", Description: "Prompts the operator for a numeric measurement and checks its limits.")]
public sealed class OperatorMeasurementStep : TestStep
{
    [Display("Instruction", Order: 1)]
    public string Instruction { get; set; } = "";

    [Display("Lower Limit", Order: 2)]
    public double LowerLimit { get; set; }

    [Display("Upper Limit", Order: 3)]
    public double UpperLimit { get; set; }

    [Display("Unit", Order: 4)]
    public string Unit { get; set; } = "";

    [Output, Display("Measured Value", Group: "Results")]
    public string MeasuredValue { get; private set; } = "";

    public OperatorMeasurementStep()
        => Rules.Add(() => LowerLimit <= UpperLimit, "Lower limit must not exceed upper limit.", nameof(LowerLimit));

    public override void Run()
    {
        var request = new OperatorMeasurementRequest
        {
            Title = Name,
            Message = Instruction,
            Unit = Unit
        };
        UserInput.Request(request, true);
        if (request.Response != OperatorMeasurementResponse.Submit)
        {
            UpgradeVerdict(Verdict.Aborted);
            return;
        }

        MeasuredValue = request.Value.ToString("G17", CultureInfo.InvariantCulture);
        var passed = request.Value >= LowerLimit && request.Value <= UpperLimit;
        Results.Publish("Operator Measurement", new List<string> { "Measured Value", "Unit", "Lower Limit", "Upper Limit", "Verdict" },
            request.Value, Unit, LowerLimit, UpperLimit, passed ? "PASS" : "FAIL");
        UpgradeVerdict(passed ? Verdict.Pass : Verdict.Fail);
    }
}

public enum OperatorMeasurementResponse
{
    Submit,
    Cancel
}

public sealed class OperatorMeasurementRequest
{
    [Browsable(false)]
    public string Title { get; set; } = "";

    [Browsable(false)]
    public string Message { get; set; } = "";

    [Display("Measured Value", Order: 1)]
    public double Value { get; set; }

    [Browsable(false)]
    public string Unit { get; set; } = "";

    [Submit, Display("Action", Order: 2)]
    public OperatorMeasurementResponse Response { get; set; } = OperatorMeasurementResponse.Submit;
}