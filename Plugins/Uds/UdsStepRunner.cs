using UdsCore;
using Jabil.Uds.Core;

namespace Tap.Plugins.Uds;

internal static class UdsStepRunner
{
    public static UdsStepResult Execute(IUdsInstrument instrument, UdsStepConfig config)
    {
        if (instrument == null) throw new InvalidOperationException("A UDS instrument must be selected.");
        return instrument.Execute(config);
    }
}