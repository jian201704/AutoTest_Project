using NUnit.Framework;
using UdsCore;
using UdsEngine;

namespace Tap.Plugins.Uds.Tests;

[TestFixture]
public sealed class UdsExecutionTests
{
    [Test]
    public void VirtualResponseMatchingPassesExpectedPrefix()
    {
        var config = UdsStepConfig.FromColumns(
            "udsPayload=10 03,expectedPrefix=50 03,virtualMode=true,simResponse=50 03 00 32 01 F4",
            "");

        var result = UdsTransactionExecutor.Execute(config, new NoopTransport());

        Assert.That(result.Success, Is.True);
        Assert.That(result.CombinedResponseHex, Is.EqualTo("50 03 00 32 01 F4"));
    }

    [Test]
    public void VirtualResponseMatchingRejectsWrongPrefix()
    {
        var config = UdsStepConfig.FromColumns(
            "udsPayload=10 03,expectedPrefix=50 03,virtualMode=true,simResponse=50 02",
            "");

        var result = UdsTransactionExecutor.Execute(config, new NoopTransport());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("prefix mismatch"));
    }

    [Test]
    public void ParsesCanTransportConfiguration()
    {
        var config = UdsStepConfig.FromColumns(
            "transport=can,canChannel=1,canRequestId=0x7E0,canResponseId=0x7E8,canFd=true,canBitRateSwitch=true",
            "");

        Assert.That(config.IsCan, Is.True);
        Assert.That(config.CanChannelIndex, Is.EqualTo(1));
        Assert.That(config.CanRequestId, Is.EqualTo(0x7E0));
        Assert.That(config.CanResponseId, Is.EqualTo(0x7E8));
        Assert.That(config.CanFd, Is.True);
        Assert.That(config.CanBitRateSwitch, Is.True);
    }

    private sealed class NoopTransport : Jabil.Uds.Core.IUdsTransport
    {
        public void Connect() { }
        public Jabil.Uds.Core.DiagnosticFrame Request(Jabil.Uds.Core.DiagnosticFrame request, int timeoutMilliseconds) => throw new System.InvalidOperationException();
        public void Dispose() { }
    }
}
