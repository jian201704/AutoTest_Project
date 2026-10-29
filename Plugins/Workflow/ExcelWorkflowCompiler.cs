using System.Globalization;
using System.Text.RegularExpressions;
using OpenTap;
using OpenTap.Plugins.BasicSteps;
using Tap.Plugins.Uds;

namespace Tap.Plugins.Workflow;

public sealed record ExcelWorkflow(TestPlan Plan, CanInstrument? Can, ScpiPowerSupplyInstrument? PowerSupply, int EnabledStepCount);

public static class ExcelWorkflowCompiler
{
    public static ExcelWorkflow Compile(string workbookPath, IWorkflowResourceResolver? resourceResolver = null)
    {
        resourceResolver ??= new InstrumentSettingsWorkflowResourceResolver();
        var stepRows = OpenXmlTable.Load(workbookPath, "StepTable", "Enabled", "StepNo", "TestGroup", "TestItem",
            "Action", "RequestConfig", "DecodeConfig", "TimeoutMs", "LowerLimit", "UpperLimit", "Unit", "FailurePolicy").Rows;
        var resourceRows = OpenXmlTable.Load(workbookPath, "ResourceMap", "ResourceName", "Parameters").Rows;
        var canRow = FindResource(resourceRows, "CANFD_ECU", required: false);
        var powerRow = FindResource(resourceRows, "DC_Power_Supply", required: false);
        var powerParameters = powerRow == null ? new Dictionary<string, string>() : ParsePairs(powerRow["Parameters"]);
        var can = canRow == null ? null : resourceResolver.Resolve<CanInstrument>(canRow["ResourceName"].Trim());
        var power = powerRow == null ? null : resourceResolver.Resolve<ScpiPowerSupplyInstrument>(powerRow["ResourceName"].Trim());
        var plan = new TestPlan();
        var enabledCount = 0;
        foreach (var row in stepRows)
        {
            if (!row["Enabled"].Trim().Equals("Y", StringComparison.OrdinalIgnoreCase)) continue;
            plan.Steps.Add(ApplyFailurePolicy(CreateStep(row, resourceResolver, can, power, powerParameters), row));
            enabledCount++;
        }
        return new ExcelWorkflow(plan, can, power, enabledCount);
    }

    private static ITestStep CreateStep(IReadOnlyDictionary<string, string> row, IWorkflowResourceResolver resolver, CanInstrument? can,
        ScpiPowerSupplyInstrument? power, IReadOnlyDictionary<string, string> powerParameters)
    {
        var action = row["Action"].Trim().ToLowerInvariant();
        var resourceName = Get(row, "Resource").Trim();
        return action switch
        {
            "send_uds" => CreateUdsStep(row, ResolveCan(resolver, resourceName, can, row)),
            "power_on" => CreatePowerStep(row, resolver, power, powerParameters, true),
            "power_off" => CreatePowerStep(row, resolver, power, powerParameters, false),
            "manual_check" => CreateOperatorStep(row),
            "run_command" => CreateProcessStep(row),
            "delay" => new DelayTestStep { Name = Get(row, "TestItem"), DurationMs = ParseDelayMs(row) },
            "ping_test" => CreatePingStep(row),
            "receive_can" => CreateRawCanStep(row, resolver, resourceName),
            "canfd_off" => new CanReleaseStep { Name = Get(row, "TestItem"), Instrument = ResolveCan(resolver, resourceName, can, row) },
            "soc_flash" => new SocFlashStep { Name = Get(row, "TestItem"), Instrument = resolver.Resolve<DualXpuTcpInstrument>(RequireResource(row)) },
            "inst_dmm_dmm6500_meas" => CreateDmmStep(row, resolver, resourceName),
            "inst_sig_dg1022_on" => CreateDg1022Step(row, resolver, resourceName, true),
            "inst_sig_dg1022_off" => CreateDg1022Step(row, resolver, resourceName, false),
            "inst_dio_g96_set" => CreateCommandStep(row, resolver, resourceName, "TRUE", 500, 3, 80, 500),
            "inst_dio_m48_set" => CreateCommandStep(row, resolver, resourceName, "True", 500, 3, 80, 500),
            "inst_dio_ledboard_set" => CreateCommandStep(row, resolver, resourceName, "OK", 500, 3, 80, 500),
            _ => throw new InvalidDataException($"Step {row["StepNo"]} has unsupported action '{row["Action"]}'.")
        };
    }

    private static CanUdsRequestStep CreateUdsStep(IReadOnlyDictionary<string, string> row, CanInstrument can)
    {
        var config = Get(row, "RequestConfig").Trim();
        AppendPair(ref config, "timeoutMs", Get(row, "TimeoutMs"));
        AppendPair(ref config, "lowerLimit", Get(row, "LowerLimit"));
        AppendPair(ref config, "upperLimit", Get(row, "UpperLimit"));
        AppendPair(ref config, "unit", Get(row, "Unit"));
        return new CanUdsRequestStep
        {
            Name = Get(row, "TestItem").Trim(), Instrument = can, Config1 = config,
            DecodeConfig = Get(row, "DecodeConfig").Trim(), UdsPayload = "", ExpectedPrefix = "",
            TimeoutMs = int.TryParse(Get(row, "TimeoutMs"), out var timeout) && timeout > 0 ? timeout : 5000
        };
    }

    private static ITestStep CreatePowerStep(IReadOnlyDictionary<string, string> row, IWorkflowResourceResolver resolver,
        ScpiPowerSupplyInstrument? fallback, IReadOnlyDictionary<string, string> parameters, bool enabled)
    {
        var resourceName = Get(row, "Resource").Trim();
        var instrument = string.IsNullOrWhiteSpace(resourceName)
            ? fallback ?? throw new InvalidDataException($"Step {Get(row, "StepNo")} requires a power supply resource.")
            : resolver.Resolve<ScpiPowerSupplyInstrument>(resourceName);
        return new PowerSupplyOutputStep
        {
            Name = Get(row, "TestItem").Trim(), Instrument = instrument, OutputEnabled = enabled,
            Voltage = GetDouble(parameters, "voltage", 15), CurrentLimit = GetDouble(parameters, "current", 20)
        };
    }

    private static ITestStep CreatePingStep(IReadOnlyDictionary<string, string> row)
    {
        var config = ParsePairs(Get(row, "RequestConfig"));
        return new PingTestStep
        {
            Name = Get(row, "TestItem"),
            SourceAddress = GetValue(config, "SourceIP"),
            TargetAddress = GetValue(config, "TargetIP"),
            MaximumMeanResponseMs = 100,
            MaximumPacketLossPercent = 0
        };
    }

    private static ITestStep CreateRawCanStep(IReadOnlyDictionary<string, string> row, IWorkflowResourceResolver resolver, string resourceName)
    {
        var config = ParsePairs(Get(row, "RequestConfig"));
        if (!config.TryGetValue("canID", out var canId) || !config.TryGetValue("canMSG", out var canData))
            throw new InvalidDataException($"Step {Get(row, "StepNo")} receive_can requires canID and canMSG.");
        var identifier = canId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? canId : "0x" + canId;
        return new CanRawReceiveStep
        {
            Name = Get(row, "TestItem"), Instrument = resolver.Resolve<CanInstrument>(resourceName),
            ExpectedIdentifier = identifier, ExpectedData = canData,
            ReceiveTimeoutMs = 1000, MaximumReceiveAttempts = 10, ReceiveMode = 2
        };
    }

    private static ITestStep CreateDmmStep(IReadOnlyDictionary<string, string> row, IWorkflowResourceResolver resolver, string resourceName)
    {
        var config = ParsePairs(Get(row, "RequestConfig"));
        return new DmmMeasurementStep
        {
            Name = Get(row, "TestItem"), Instrument = resolver.Resolve<ScpiVisaInstrument>(resourceName),
            Function = GetValue(config, "func", "VOLT_DC"),
            Range = GetDouble(config, "range", 10), Nplc = GetDouble(config, "nplc", 1),
            Scale = GetDouble(config, "scale", 1), Offset = GetDouble(config, "offset", 0),
            LowerLimit = ParseLimit(Get(row, "LowerLimit"), double.NegativeInfinity),
            UpperLimit = ParseLimit(Get(row, "UpperLimit"), double.PositiveInfinity), Unit = Get(row, "Unit")
        };
    }

    private static ITestStep CreateDg1022Step(IReadOnlyDictionary<string, string> row, IWorkflowResourceResolver resolver,
        string resourceName, bool outputEnabled)
    {
        var config = ParsePairs(Get(row, "RequestConfig"));
        return new Dg1022WaveformStep
        {
            Name = Get(row, "TestItem"), Instrument = resolver.Resolve<ScpiVisaInstrument>(resourceName),
            Channel = (int)GetDouble(config, "ch", 1), Waveform = GetValue(config, "waveType", "SQU"),
            Amplitude = GetDouble(config, "volt", 10), Frequency = GetDouble(config, "freq", 1000),
            DutyCyclePercent = GetDouble(config, "duty", 50), OutputEnabled = outputEnabled
        };
    }

    private static ITestStep CreateCommandStep(IReadOnlyDictionary<string, string> row, IWorkflowResourceResolver resolver,
        string resourceName, string expectedResponse, int timeoutMs, int attempts, int retryDelayMs, int settleDelayMs)
    {
        var config = ParsePairs(Get(row, "RequestConfig"));
        if (!config.TryGetValue("cmd", out var command) || string.IsNullOrWhiteSpace(command))
            throw new InvalidDataException($"Step {Get(row, "StepNo")} requires cmd= in RequestConfig.");
        return new SerialCommandStep
        {
            Name = Get(row, "TestItem"), Instrument = resolver.Resolve<SerialCommandInstrument>(resourceName),
            Command = command, ExpectedResponse = expectedResponse, ResponseTimeoutMs = timeoutMs,
            Attempts = attempts, RetryDelayMs = retryDelayMs, SettleDelayMs = settleDelayMs
        };
    }

    private static int ParseDelayMs(IReadOnlyDictionary<string, string> row)
    {
        var config = ParsePairs(Get(row, "RequestConfig"));
        var value = config.TryGetValue("delay", out var delay) ? delay : config.TryGetValue("delayMs", out delay) ? delay : "";
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds < 0)
            throw new InvalidDataException($"Step {Get(row, "StepNo")} delay must contain a non-negative delay=<ms> value.");
        return milliseconds;
    }

    private static CanInstrument ResolveCan(IWorkflowResourceResolver resolver, string name, CanInstrument? fallback,
        IReadOnlyDictionary<string, string> row)
        => string.IsNullOrWhiteSpace(name)
            ? fallback ?? throw new InvalidDataException($"Step {Get(row, "StepNo")} requires a CAN instrument resource.")
            : resolver.Resolve<CanInstrument>(name);

    private static string RequireResource(IReadOnlyDictionary<string, string> row)
        => string.IsNullOrWhiteSpace(Get(row, "Resource"))
            ? throw new InvalidDataException($"Step {Get(row, "StepNo")} requires a Resource name.")
            : Get(row, "Resource").Trim();

    private static ITestStep CreateOperatorStep(IReadOnlyDictionary<string, string> row)
    {
        var text = ValueAfterEquals(Get(row, "RequestConfig"));
        if (TryGetDouble(Get(row, "LowerLimit"), out var lower) && TryGetDouble(Get(row, "UpperLimit"), out var upper))
            return new OperatorMeasurementStep
            {
                Name = Get(row, "TestItem").Trim(), Instruction = text,
                LowerLimit = lower, UpperLimit = upper, Unit = Get(row, "Unit").Trim()
            };
        return new DialogStep
        {
            Name = Get(row, "TestItem").Trim(), Title = Get(row, "TestGroup").Trim(), Message = text,
            Buttons = InputButtons.YesNo, PositiveAnswer = Verdict.Pass, NegativeAnswer = Verdict.Fail
        };
    }

    private static ProcessStep CreateProcessStep(IReadOnlyDictionary<string, string> row)
    {
        var match = Regex.Match(Get(row, "Remark"), @"ping\s+(?:-t\s+)?-(?:s)\s+(?<source>\d+(?:\.\d+){3})\s+(?<target>\d+(?:\.\d+){3})", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidDataException($"Step {Get(row, "StepNo")} run_command does not contain a structured ping command in Remark.");
        return new ProcessStep
        {
            Name = row["TestItem"].Trim(), Application = "ping.exe",
            Arguments = $"-n 4 -w 1000 -S {match.Groups["source"].Value} {match.Groups["target"].Value}",
            WaitForEnd = true, CheckExitCode = true, AddToLog = true
        };
    }

    private static ITestStep ApplyFailurePolicy(ITestStep step, IReadOnlyDictionary<string, string> row)
    {
        var policy = Get(row, "FailurePolicy").Trim();
        if (policy.Equals("continue", StringComparison.OrdinalIgnoreCase))
        {
            BreakConditionProperty.SetBreakCondition(step, 0);
            return step;
        }
        if (policy.Equals("fail", StringComparison.OrdinalIgnoreCase))
        {
            BreakConditionProperty.SetBreakCondition(step, BreakCondition.BreakOnFail | BreakCondition.BreakOnError);
            return step;
        }

        var retry = Regex.Match(policy, @"^retry\s+(?<count>\d+)$", RegexOptions.IgnoreCase);
        if (!retry.Success || !uint.TryParse(retry.Groups["count"].Value, out var retryCount))
            throw new InvalidDataException($"Step {Get(row, "StepNo")} has unsupported FailurePolicy '{policy}'.");

        BreakConditionProperty.SetBreakCondition(step, 0);
        var repeat = new RepeatStep
        {
            Name = step.Name,
            Action = RepeatStep.RepeatStepAction.Until,
            TargetStep = step,
            TargetVerdict = Verdict.Pass,
            ClearVerdict = true,
            MaxCount = new Enabled<uint> { IsEnabled = true, Value = retryCount + 1 }
        };
        repeat.ChildTestSteps.Add(step);
        BreakConditionProperty.SetBreakCondition(repeat, BreakCondition.BreakOnFail | BreakCondition.BreakOnError);
        return repeat;
    }

    private static IReadOnlyDictionary<string, string>? FindResource(IEnumerable<IReadOnlyDictionary<string, string>> rows, string name, bool required = true)
    {
        var resource = rows.FirstOrDefault(row => row["ResourceName"].Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        return resource ?? (required ? throw new InvalidDataException($"ResourceMap does not contain '{name}'.") : null);
    }

    private static Dictionary<string, string> ParsePairs(string text) => text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(item => item.Split('=', 2)).Where(parts => parts.Length == 2)
        .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

    private static string Get(IReadOnlyDictionary<string, string> row, string key)
        => row.TryGetValue(key, out var value) ? value : "";

    private static string GetValue(IReadOnlyDictionary<string, string> values, string key, string fallback = "")
        => values.TryGetValue(key, out var value) ? value : fallback;

    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double fallback)
        => values.TryGetValue(key, out var value) && TryGetDouble(value, out var result) ? result : fallback;

    private static double ParseLimit(string text, double fallback)
        => TryGetDouble(text, out var value) ? value : fallback;

    private static void AppendPair(ref string config, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || config.Contains(name + "=", StringComparison.OrdinalIgnoreCase)) return;
        config += (config.Length == 0 ? "" : ",") + name + "=" + value.Trim();
    }

    private static string ValueAfterEquals(string value) => value.Contains('=') ? value[(value.IndexOf('=') + 1)..].Trim() : value.Trim();
    private static bool TryGetDouble(string value, out double result) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}