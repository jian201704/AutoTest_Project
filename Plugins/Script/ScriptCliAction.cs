using OpenTap;
using OpenTap.Cli;

namespace Tap.Plugins.Script;

[Display("script", Description: "Compile and run a C# script file.")]
public sealed class ScriptCliAction : ICliAction
{
    private static readonly OpenTap.TraceSource Log = OpenTap.Log.CreateSource("C# Script");

    [UnnamedCommandLineArgument("Script File", Required = true, Description = "Path to the .csx script to run.")]
    public string ScriptFile { get; set; } = string.Empty;

    public int Execute(CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.GetFullPath(ScriptFile);
            if (!File.Exists(path))
            {
                Log.Error("Script file does not exist: {0}", path);
                return 2;
            }

            var source = File.ReadAllText(path);
            var compiled = ScriptCompiler.Compile(source);
            var context = new ScriptContext(
                new ScriptLog((level, message) => Log.TraceEvent(level, 0, message)),
                cancellationToken,
                new ScriptResourceCatalog(InstrumentSettings.Current),
                new ScriptResourceCatalog(DutSettings.Current));
            var globals = new ScriptGlobals(context);

            var result = compiled.Execute(globals, cancellationToken);
            if (result is Verdict verdict && verdict >= Verdict.Fail || context.Verdict >= Verdict.Fail)
                return 1;
            return 0;
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Script execution was cancelled.");
            return 3;
        }
        catch (Exception exception)
        {
            Log.Error("{0}", exception.Message);
            Log.Debug(exception);
            return 1;
        }
    }
}
