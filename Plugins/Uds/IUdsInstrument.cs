using UdsCore;
using Jabil.Uds.Core;

namespace Tap.Plugins.Uds;

public interface IUdsInstrument
{
    string ResourceName { get; }
    UdsStepResult Execute(UdsStepConfig config);
}