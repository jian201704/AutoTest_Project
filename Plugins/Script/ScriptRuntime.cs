using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using OpenTap;

namespace Tap.Plugins.Script;

public sealed class ScriptCompilationException : Exception
{
    public ScriptCompilationException(string message) : base(message) { }
}

public sealed class ScriptAssertionException : Exception
{
    public ScriptAssertionException(string message) : base(message) { }
}

public sealed class ScriptLog
{
    private readonly Action<OpenTap.LogEventType, string> write;

    public ScriptLog(Action<OpenTap.LogEventType, string> write) => this.write = write;

    public void Debug(string message, params object?[] arguments) => Write(OpenTap.LogEventType.Debug, message, arguments);
    public void Info(string message, params object?[] arguments) => Write(OpenTap.LogEventType.Information, message, arguments);
    public void Warning(string message, params object?[] arguments) => Write(OpenTap.LogEventType.Warning, message, arguments);
    public void Error(string message, params object?[] arguments) => Write(OpenTap.LogEventType.Error, message, arguments);

    private void Write(OpenTap.LogEventType level, string message, object?[] arguments)
        => write(level, arguments.Length == 0 ? message : string.Format(CultureInfo.InvariantCulture, message, arguments));
}

public sealed class ScriptAssert
{
    public void True(bool condition, string message = "Script assertion failed.")
    {
        if (!condition) throw new ScriptAssertionException(message);
    }

    public void False(bool condition, string message = "Script assertion failed.") => True(!condition, message);

    public void Equal<T>(T expected, T actual, string message = "Script values are not equal.")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new ScriptAssertionException($"{message} Expected: {expected}; actual: {actual}.");
    }

    public void InRange(double actual, double minimum, double maximum, string message = "Script value is outside the expected range.")
    {
        if (actual < minimum || actual > maximum)
            throw new ScriptAssertionException($"{message} Expected: [{minimum}, {maximum}]; actual: {actual}.");
    }

    public void Fail(string message = "Script requested failure.") => throw new ScriptAssertionException(message);
}

public sealed class ScriptResourceCatalog
{
    private readonly Dictionary<string, IResource> resources = new(StringComparer.OrdinalIgnoreCase);

    public ScriptResourceCatalog() { }

    public ScriptResourceCatalog(IEnumerable<IResource> resources)
    {
        foreach (var resource in resources)
            Add(resource);
    }

    public void Add(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (string.IsNullOrWhiteSpace(resource.Name))
            throw new ArgumentException("A script resource must have a name.", nameof(resource));

        resources[resource.Name] = resource;
    }

    public T Get<T>(string name) where T : class, IResource
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A resource name is required.", nameof(name));
        if (!resources.TryGetValue(name, out var resource))
            throw new KeyNotFoundException($"Script resource '{name}' was not found.");
        if (resource is not T typedResource)
            throw new InvalidCastException($"Script resource '{name}' is {resource.GetType().FullName}, not {typeof(T).FullName}.");

        return typedResource;
    }

    internal IReadOnlyCollection<IResource> Items => resources.Values;
}

public sealed class ScriptResults
{
    private readonly List<ScriptResult> results = new();
    private readonly Action<ScriptResult>? publish;

    public ScriptResults(Action<ScriptResult>? publish = null) => this.publish = publish;

    public IReadOnlyList<ScriptResult> Items => results;

    public void Publish(string name, params object?[] values)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A result name is required.", nameof(name));

        var result = new ScriptResult(name, values.ToArray());
        results.Add(result);
        publish?.Invoke(result);
    }
}

public sealed record ScriptResult(string Name, IReadOnlyList<object?> Values);

public sealed record ScriptStepResult(Verdict Verdict, TestStepRun Run);

public sealed class ScriptStepRunner
{
    public async Task<ScriptStepResult> RunAsync(TestStep step, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        cancellationToken.ThrowIfCancellationRequested();
        var plan = new TestPlan();
        plan.Steps.Add(step);
        var listener = new StepRunCapture(step.Id);
        await plan.ExecuteAsync(new[] { listener }, null!, null!, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var stepRun = listener.StepRun ?? throw new InvalidOperationException("OpenTAP did not return a step run.");
        return new ScriptStepResult(stepRun.Verdict, stepRun);
    }

    private sealed class StepRunCapture : ResultListener
    {
        private readonly Guid stepId;

        public StepRunCapture(Guid stepId) => this.stepId = stepId;

        public TestStepRun? StepRun { get; private set; }

        public override void OnTestStepRunCompleted(TestStepRun stepRun)
        {
            if (stepRun.TestStepId == stepId) StepRun = stepRun;
        }
    }
}

public sealed class ScriptContext
{
    public ScriptContext(ScriptLog log, CancellationToken cancellationToken,
        ScriptResourceCatalog? instruments = null, ScriptResourceCatalog? duts = null, ScriptResults? results = null)
    {
        Log = log ?? throw new ArgumentNullException(nameof(log));
        CancellationToken = cancellationToken;
        Instruments = instruments ?? new ScriptResourceCatalog();
        Duts = duts ?? new ScriptResourceCatalog();
        Results = results ?? new ScriptResults();
        Steps = new ScriptStepRunner();
    }

    public ScriptLog Log { get; }
    public ScriptAssert Assert { get; } = new();
    public CancellationToken CancellationToken { get; }
    public ScriptResourceCatalog Instruments { get; }
    public ScriptResourceCatalog Duts { get; }
    public ScriptStepRunner Steps { get; }
    public ScriptResults Results { get; }
    public Verdict Verdict { get; set; } = Verdict.NotSet;
}

public sealed class ScriptGlobals
{
    public ScriptGlobals(ScriptLog log, CancellationToken cancellationToken)
        : this(new ScriptContext(log, cancellationToken))
    {
    }

    public ScriptGlobals(ScriptContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ScriptContext Context { get; }
    public ScriptLog Log => Context.Log;
    public ScriptAssert Assert => Context.Assert;
    public CancellationToken CancellationToken => Context.CancellationToken;
    public ScriptResourceCatalog Instruments => Context.Instruments;
    public ScriptResourceCatalog Duts => Context.Duts;
    public ScriptStepRunner Steps => Context.Steps;
    public ScriptResults Results => Context.Results;
    public Verdict Verdict { get => Context.Verdict; set => Context.Verdict = value; }
}

public sealed class CompiledScript
{
    private readonly ScriptRunner<object?> runner;

    internal CompiledScript(ScriptRunner<object?> runner) => this.runner = runner;

    public Task<object?> ExecuteAsync(ScriptGlobals globals, CancellationToken cancellationToken = default)
        => runner(globals, cancellationToken);

    public object? Execute(ScriptGlobals globals, CancellationToken cancellationToken = default)
        => ExecuteAsync(globals, cancellationToken).GetAwaiter().GetResult();
}

public static class ScriptCompiler
{
    private static readonly ConcurrentDictionary<string, Lazy<CompiledScript>> Cache = new(StringComparer.Ordinal);
    private static IReadOnlyList<Assembly>? referenceAssemblies;

    public static int CachedCompilationCount => Cache.Count;

    public static CompiledScript Compile(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ScriptCompilationException("Script source is empty.");

        OpenTap.PluginManager.GetPlugins(typeof(OpenTap.ITapPlugin));
        var assemblies = GetReferenceAssemblies();
        var key = CreateCacheKey(source, assemblies);
        return Cache.GetOrAdd(key, _ => new Lazy<CompiledScript>(
            () => CompileCore(source, assemblies), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public static void ClearCache() => Cache.Clear();

    private static CompiledScript CompileCore(string source, IReadOnlyList<Assembly> assemblies)
    {
        var options = ScriptOptions.Default
            .WithOptimizationLevel(OptimizationLevel.Release)
            .AddReferences(assemblies)
            .AddImports(
                "System",
                "System.Collections.Generic",
                "System.IO",
                "System.Linq",
                "System.Threading",
                "System.Threading.Tasks",
                "OpenTap",
                "Tap.Plugins.Script");

        var script = CSharpScript.Create<object?>(source, options, typeof(ScriptGlobals));
        var errors = script.Compile().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
            throw new ScriptCompilationException(string.Join(Environment.NewLine, errors.Select(error => error.ToString())));

        return new CompiledScript(script.CreateDelegate());
    }

    private static IReadOnlyList<Assembly> GetReferenceAssemblies()
    {
        if (referenceAssemblies != null)
            return referenceAssemblies;

        var discovered = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
            .GroupBy(assembly => assembly.Location, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return Interlocked.CompareExchange(ref referenceAssemblies, discovered, null) ?? discovered;
    }

    private static string CreateCacheKey(string source, IReadOnlyList<Assembly> assemblies)
    {
        var manifest = string.Join("\n", assemblies
            .OrderBy(assembly => assembly.FullName, StringComparer.Ordinal)
            .Select(assembly => $"{assembly.FullName}|{assembly.ManifestModule.ModuleVersionId}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + manifest)));
    }
}
