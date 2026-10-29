using OpenTap;

namespace Tap.Plugins.Workflow;

public interface IWorkflowResourceResolver
{
    T Resolve<T>(string name) where T : class, IInstrument;
}

public sealed class InstrumentSettingsWorkflowResourceResolver : IWorkflowResourceResolver
{
    public T Resolve<T>(string name) where T : class, IInstrument
    {
        var matches = InstrumentSettings.Current.OfType<T>()
            .Where(resource => string.Equals(resource.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"Instrument Settings does not contain a {typeof(T).Name} named '{name}'. Configure it in Bench > Instruments before importing the workflow."),
            _ => throw new InvalidOperationException(
                $"Instrument Settings contains multiple {typeof(T).Name} resources named '{name}'. Resource names must be unique.")
        };
    }
}

public sealed class FixedWorkflowResourceResolver : IWorkflowResourceResolver
{
    private readonly IReadOnlyList<IInstrument> resources;

    public FixedWorkflowResourceResolver(params IInstrument[] resources) => this.resources = resources;

    public T Resolve<T>(string name) where T : class, IInstrument
        => resources.OfType<T>().Single(resource => string.Equals(resource.Name, name, StringComparison.OrdinalIgnoreCase));
}