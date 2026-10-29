using OpenTap;
using OpenTap.Diagnostic;

namespace Tap.Plugins.Script;

[Display("Script", Group: "Flow Control", Description: "Executes a compiled OpenTAP script.")]
public sealed class ScriptStep : TestStep
{
    private const string DefaultSource = "Log.Info(\"C# ScriptStep is running\");\nvar voltage = 15.0;\nLog.Info($\"voltage = {voltage}\");\nAssert.InRange(voltage, 14.25, 15.75, \"voltage out of range\");";
    private string compiledSource = string.Empty;
    private CompiledScript? compiledScript;

    [FilePath(FilePathAttribute.BehaviorChoice.Open, "C# Script (*.csx)|*.csx")]
    [Display("Script File", Description: "Optional .csx file. When set, this file is executed instead of Inline Source.", Order: 1)]
    public string ScriptFile { get; set; } = string.Empty;

    [Display("Inline Source", Description: "C# script source used when Script File is empty.", Order: 2)]
    public string Source { get; set; } = DefaultSource;

    public override void Run()
    {
        Log.TraceEvent(LogEventType.Information, 0, "Script execution started.");

        var source = string.IsNullOrWhiteSpace(ScriptFile) ? Source : File.ReadAllText(ScriptFile);
        if (string.IsNullOrWhiteSpace(source))
        {
            Log.TraceEvent(LogEventType.Warning, 0, "Script source is empty; nothing was executed.");
            return;
        }

        if (!string.Equals(compiledSource, source, StringComparison.Ordinal) || compiledScript is null)
        {
            compiledScript = ScriptCompiler.Compile(source);
            compiledSource = source;
            Log.TraceEvent(LogEventType.Debug, 0, "Script compiled successfully.");
        }

        var cancellationToken = TapThread.Current.AbortToken;
        var context = new ScriptContext(
            new ScriptLog((level, message) => Log.TraceEvent(level, 0, message)),
            cancellationToken,
            new ScriptResourceCatalog(InstrumentSettings.Current),
            new ScriptResourceCatalog(DutSettings.Current),
            new ScriptResults(result => Results.Publish(result.Name,
                Enumerable.Range(1, result.Values.Count).Select(index => $"Value {index}").ToList(),
                result.Values.Select(value => value as IConvertible ?? value?.ToString() ?? "").ToArray())));
        var globals = new ScriptGlobals(context);
        try
        {
            var result = compiledScript.Execute(globals, cancellationToken);
            UpgradeVerdict(result is Verdict verdict ? verdict : context.Verdict == Verdict.NotSet ? Verdict.Pass : context.Verdict);
            Log.TraceEvent(LogEventType.Information, 0, "Script execution completed successfully.");
        }
        catch (ScriptAssertionException exception)
        {
            Log.TraceEvent(LogEventType.Error, 0, exception.Message);
            UpgradeVerdict(Verdict.Fail);
        }
    }
}
