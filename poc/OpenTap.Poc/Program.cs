using OpenTap;
using OpenTap.Plugins.BasicSteps;

namespace OpenTap.Poc;

internal static class Program
{
    private static int Main()
    {
        PluginManager.SearchAsync();
        SessionLogs.Initialize("opentap-poc.log");

        using var virtualEcu = new VirtualDoipServer();
        virtualEcu.Start();

        var plan = new TestPlan();
        plan.ChildTestSteps.Add(new LogStep
        {
            Name = "Start PoC",
            LogMessage = "OpenTAP PoC plan started.",
            Severity = LogSeverity.Info
        });
        plan.ChildTestSteps.Add(new PocCheckStep
        {
            Name = "Check Measurement",
            MeasuredValue = 42,
            ExpectedValue = 42
        });
        plan.ChildTestSteps.Add(new DoipReadStep
        {
            Name = "Read Virtual ECU DID F190",
            Host = "127.0.0.1",
            Port = virtualEcu.Port
        });

        var result = plan.Execute(new List<ResultListener> { new LogResultListener() });
        Console.WriteLine("OpenTAP PoC verdict: {0}", result.Verdict);

        return result.Verdict == Verdict.Pass ? 0 : 1;
    }
}