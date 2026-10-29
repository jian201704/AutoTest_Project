using NUnit.Framework;
using OpenTap;
using OpenTap.Plugins.BasicSteps;
using Tap.Plugins.Script;
using Tap.Plugins.Uds;
using Tap.Plugins.Workflow;

namespace Tap.Plugins.Uds.Tests;

public class ScriptCompilerTests
{
    [SetUp]
    public void SetUp() => ScriptCompiler.ClearCache();

    [Test]
    public void CompilesAndExecutesStandardCSharp()
    {
        var messages = new List<string>();
        var script = ScriptCompiler.Compile("var value = 2 + 3 * 4; Assert.Equal(14, value); Log.Info($\"value = {value}\");");

        script.Execute(CreateGlobals(messages));

        Assert.That(messages, Does.Contain("value = 14"));
    }

    [Test]
    public void SupportsAsyncCSharpScripts()
    {
        var messages = new List<string>();
        var script = ScriptCompiler.Compile("await Task.Yield(); CancellationToken.ThrowIfCancellationRequested(); Log.Info(\"async ok\");");

        script.Execute(CreateGlobals(messages));

        Assert.That(messages, Does.Contain("async ok"));
    }

    [Test]
    public void ReusesCompiledScriptFromCache()
    {
        const string source = "Log.Info(\"cached\");";

        var first = ScriptCompiler.Compile(source);
        var second = ScriptCompiler.Compile(source);

        Assert.That(second, Is.SameAs(first));
        Assert.That(ScriptCompiler.CachedCompilationCount, Is.EqualTo(1));
    }

    [Test]
    public void ReportsCSharpDiagnostics()
    {
        var exception = Assert.Throws<ScriptCompilationException>(() => ScriptCompiler.Compile("var value = ;"));

        Assert.That(exception!.Message, Does.Contain("CS1525"));
    }

    [Test]
    public void ReferencesLoadedOpenTapPlugins()
    {
        var source = "var step = new Tap.Plugins.Uds.CanUdsRequestStep(); Assert.Equal(\"10 01\", step.UdsPayload);";

        Assert.DoesNotThrow(() => ScriptCompiler.Compile(source).Execute(CreateGlobals(new List<string>())));
    }

    [Test]
    public void ResolvesConfiguredScriptResourcesByNameAndType()
    {
        var instrument = new DoipInstrument { Name = "DoIP1", VirtualMode = true };
        var globals = new ScriptGlobals(new ScriptContext(
            new ScriptLog((_, _) => { }), CancellationToken.None,
            new ScriptResourceCatalog(new[] { instrument })));
        var script = ScriptCompiler.Compile("Assert.True(ReferenceEquals(Instruments.Get<Tap.Plugins.Uds.DoipInstrument>(\"DoIP1\"), Instruments.Get<OpenTap.IInstrument>(\"DoIP1\"))); ");

        Assert.DoesNotThrow(() => script.Execute(globals));
    }

    [Test]
    public void PublishesScriptResultsThroughHostCallback()
    {
        ScriptResult? published = null;
        var globals = new ScriptGlobals(new ScriptContext(
            new ScriptLog((_, _) => { }), CancellationToken.None,
            results: new ScriptResults(result => published = result)));

        ScriptCompiler.Compile("Results.Publish(\"Measurement\", 12.5, \"V\");").Execute(globals);

        Assert.That(published?.Name, Is.EqualTo("Measurement"));
        Assert.That(published?.Values, Is.EqualTo(new object[] { 12.5, "V" }));
    }

    [Test]
    public void CliActionRunsACompleteScriptFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opentap-script-{Guid.NewGuid():N}.csx");
        try
        {
            File.WriteAllText(path, "Log.Info(\"file script ok\"); Assert.True(true);");

            var exitCode = new ScriptCliAction { ScriptFile = path }.Execute(CancellationToken.None);

            Assert.That(exitCode, Is.Zero);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void RunsVirtualDoipStepThroughScriptContext()
    {
        var source = """
            var instrument = new Tap.Plugins.Uds.DoipInstrument { Name = "DoIP1", VirtualMode = true };
            var step = new Tap.Plugins.Uds.DoipUdsRequestStep
            {
                Name = "ReadSession",
                Instrument = instrument,
                VirtualMode = true,
                UdsPayload = "10 01",
                ExpectedPrefix = "50 01",
                SimulatedResponse = "50 01"
            };
            var result = await Steps.RunAsync(step);
            Assert.Equal(OpenTap.Verdict.Pass, result.Verdict);
            Assert.Equal("50 01", step.Response);
            """;

        var script = ScriptCompiler.Compile(source);

        Assert.DoesNotThrowAsync(async () => await script.ExecuteAsync(CreateGlobals(new List<string>())));
    }

    [Test]
    public void CompilesExcelEolScriptAndReadsStepTable()
    {
        var scriptPath = Path.Combine(TestContext.CurrentContext.TestDirectory,
            "Plugins", "Script", "Examples", "AIUM_Eol.csx");
        var workbookPath = @"E:\SourceCode\EonTest\Workflows\AIUM_Eol.xlsx";

        Assert.That(File.Exists(scriptPath), Is.True, scriptPath);
        Assert.That(File.Exists(workbookPath), Is.True, workbookPath);
        var can = new CanInstrument { Name = "CANFD_ECU" };
        var power = new ScpiPowerSupplyInstrument { Name = "DC_Power_Supply" };
        var resources = new IInstrument[]
        {
            can, power,
            new SerialCommandInstrument { Name = "G96" },
            new SerialCommandInstrument { Name = "M48" },
            new SerialCommandInstrument { Name = "LEDBoard" },
            new ScpiVisaInstrument { Name = "DMM6500" },
            new ScpiVisaInstrument { Name = "DG1022" },
            new CanInstrument { Name = "LLCAN" },
            new CanInstrument { Name = "RLCAN" },
            new CanInstrument { Name = "WLCAN" },
            new CanInstrument { Name = "LACAN" },
            new CanInstrument { Name = "RACAN" },
            new DualXpuTcpInstrument { Name = "TCP" }
        };
        var workflow = ExcelWorkflowCompiler.Compile(workbookPath, new FixedWorkflowResourceResolver(resources));
        var steps = workflow.Plan.Steps.RecursivelyGetAllTestSteps(TestStepSearch.All).ToArray();
        var udsSteps = steps.OfType<CanUdsRequestStep>().ToArray();
        var powerSteps = steps.OfType<PowerSupplyOutputStep>().ToArray();

        Assert.That(workflow.EnabledStepCount, Is.GreaterThan(0));
        Assert.That(workflow.Plan.Steps, Has.Count.EqualTo(workflow.EnabledStepCount));
        Assert.That(udsSteps, Is.Not.Empty);
        Assert.That(udsSteps.All(step => ReferenceEquals(step.Instrument, can)), Is.True);
        Assert.That(powerSteps.All(step => ReferenceEquals(step.Instrument, power)), Is.True);
        Assert.That(steps.OfType<RepeatStep>(), Is.Not.Empty);
        Assert.DoesNotThrow(() => ScriptCompiler.Compile(File.ReadAllText(scriptPath)));
    }

    [Test]
    public void CompilesExcelWorkflowSequenceScript()
    {
        var scriptPath = Path.Combine(TestContext.CurrentContext.TestDirectory,
            "Plugins", "Script", "Examples", "ExcelWorkflowSequence.csx");

        Assert.That(File.Exists(scriptPath), Is.True, scriptPath);
        Assert.DoesNotThrow(() => ScriptCompiler.Compile(File.ReadAllText(scriptPath)));
    }

    [Test]
    public void CompilesXpEngAiumSequenceScript()
    {
        var scriptPath = @"D:\Projects\XPeng\XPeng_Robot\Xpeng_AIU\Scripts\Xpeng_AIUM_Sequence.csx";

        Assert.That(File.Exists(scriptPath), Is.True, scriptPath);
        Assert.DoesNotThrow(() => ScriptCompiler.Compile(File.ReadAllText(scriptPath)));
    }

    [Test]
    public void CompilesXpEngWorkbooksAndMapsDeviceActions()
    {
        var scriptsPath = @"D:\Projects\XPeng\XPeng_Robot\Xpeng_AIU\Scripts";
        if (!Directory.Exists(scriptsPath)) Assert.Ignore("XPeng source workbooks are not available on this machine.");

        var resources = new List<OpenTap.IInstrument>
        {
            new CanInstrument { Name = "CANFD_ECU" },
            new ScpiPowerSupplyInstrument { Name = "DC_Power_Supply" },
            new CanInstrument { Name = "LLCAN" },
            new CanInstrument { Name = "RLCAN" },
            new CanInstrument { Name = "WLCAN" },
            new CanInstrument { Name = "LACAN" },
            new CanInstrument { Name = "RACAN" },
            new SerialCommandInstrument { Name = "G96" },
            new SerialCommandInstrument { Name = "M48" },
            new SerialCommandInstrument { Name = "LEDBoard" },
            new ScpiVisaInstrument { Name = "DMM6500" },
            new ScpiVisaInstrument { Name = "DG1022" },
            new DualXpuTcpInstrument { Name = "TCP" }
        };
        var resolver = new FixedWorkflowResourceResolver(resources.ToArray());
        var workflows = new[]
        {
            ExcelWorkflowCompiler.Compile(Path.Combine(scriptsPath, "Xpeng_Robot_AIUM_Eol_V1.0.xlsx"), resolver),
            ExcelWorkflowCompiler.Compile(Path.Combine(scriptsPath, "Xpeng_Robot_AIUM_APPDownload_V1.0.xlsx"), resolver),
            ExcelWorkflowCompiler.Compile(Path.Combine(scriptsPath, "Xpeng_Robot_AIUM_Final_V1.0.xlsx"), resolver)
        };
        var steps = workflows.SelectMany(workflow => workflow.Plan.Steps
            .RecursivelyGetAllTestSteps(TestStepSearch.All)).ToArray();

        Assert.That(workflows.Select(workflow => workflow.EnabledStepCount), Is.EqualTo(new[] { 303, 31, 126 }));
        Assert.That(steps.OfType<DelayTestStep>().Count(), Is.EqualTo(14));
        Assert.That(steps.OfType<PingTestStep>().Count(), Is.EqualTo(5));
        Assert.That(steps.OfType<CanRawReceiveStep>().Count(), Is.EqualTo(10));
        Assert.That(steps.OfType<SocFlashStep>().Count(), Is.EqualTo(1));
        Assert.That(steps.OfType<CanReleaseStep>().Count(), Is.EqualTo(1));
        Assert.That(steps.OfType<DmmMeasurementStep>().Count(), Is.EqualTo(10));
        Assert.That(steps.OfType<Dg1022WaveformStep>().Count(), Is.EqualTo(4));
        Assert.That(steps.OfType<SerialCommandStep>().Count(), Is.EqualTo(58));
        Assert.That(steps.OfType<SerialCommandStep>().All(step => step.Instrument is SerialCommandInstrument), Is.True);
        Assert.That(steps.OfType<DmmMeasurementStep>().All(step => step.Instrument is ScpiVisaInstrument), Is.True);
        Assert.That(steps.OfType<Dg1022WaveformStep>().All(step => step.Instrument is ScpiVisaInstrument), Is.True);
        Assert.That(steps.OfType<CanUdsRequestStep>().Count(), Is.EqualTo(355));
    }

    [Test]
    public void LoadsXpEngSocFlashProfileAndCompleteHandshakeFrame()
    {
        var profilePath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Profiles", "XPeng_AIUM_SocFlash.json");
        var instrument = new DualXpuTcpInstrument { ProtocolProfileFile = profilePath };

        instrument.LoadProtocolProfile();

        Assert.That(instrument.ExpectedProject, Is.EqualTo("R02-DUAL"));
        Assert.That(instrument.ExpectedVersion, Is.EqualTo("0.4.3-r02-robot"));
        Assert.That(instrument.LoadedProtocolProfile, Is.Not.Null);
        Assert.That(instrument.LoadedProtocolProfile!.VersionQueryHex, Is.EqualTo("F4 03 00 00 00 00 00 00 00 00 00 00"));
        Assert.That(instrument.LoadedProtocolProfile.HandshakeSettleDelayMs, Is.EqualTo(100));
        Assert.That(instrument.LoadedProtocolProfile.BeforeStartDelayMs, Is.EqualTo(1000));
        Assert.That(instrument.LoadedProtocolProfile.StartDrainDelayMs, Is.EqualTo(1000));
        Assert.That(instrument.LoadedProtocolProfile.InitialFlashDelayMs, Is.EqualTo(5000));
        Assert.That(instrument.LoadedProtocolProfile.MaxPollCount, Is.EqualTo(1000));
        var handshake = Convert.FromHexString(string.Concat(instrument.HandshakeHex.Where(character => !char.IsWhiteSpace(character))));
        Assert.That(handshake, Has.Length.EqualTo(396));
    }

    private static ScriptGlobals CreateGlobals(List<string> messages)
        => new(new ScriptLog((_, message) => messages.Add(message)), CancellationToken.None);
}
