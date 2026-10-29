# OpenTAP C# Scripts

> Detailed Chinese guide: [使用指南.md](使用指南.md) (document version 1.0, updated 2026-10-24).

The Script plugin compiles standard C# scripts with Roslyn and caches the compiled delegate in memory.

## Command line

```powershell
.\bin\Debug\tap.exe script .\Plugins\Script\Examples\basic.csx
```

A successful script returns exit code `0`. Compilation, assertion, and runtime failures return a non-zero exit code.

## Test plan

Add the `Script` step and either select a `.csx` file in **Script File** or enter C# in **Inline Source**. When both are set, **Script File** takes precedence.

Scripts receive these globals:

- `Log`: `Debug`, `Info`, `Warning`, and `Error` methods.
- `Assert`: `True`, `False`, `Equal`, `InRange`, and `Fail` methods.
- `CancellationToken`: cancelled when the OpenTAP run is aborted.
- `Instruments` and `Duts`: named resource catalogs with `Add` and `Get<T>`.
- `Steps`: executes OpenTAP `TestStep` instances.
- `Results`: collects script result values.

Loaded OpenTAP plugin assemblies are referenced automatically, so scripts can instantiate installed Step, Instrument, and DUT types directly.

```csharp
var step = new Tap.Plugins.Uds.CanUdsRequestStep
{
    UdsPayload = "22 F1 90",
    ExpectedPrefix = "62 F1 90"
};

Log.Info($"Request: {step.UdsPayload}");
Assert.True(step.ExpectedPrefix.StartsWith("62"));
```

In the WPF editor, use **Import Excel** so the compiled steps become the current plan and their individual results remain visible. Configure instruments named `CANFD_ECU` and `DC_Power_Supply` in **Instruments** before importing.

For CLI automation, compile one complete `TestPlan` and execute it once. OpenTAP then opens every referenced resource once and closes it when the plan ends:

```csharp
var workflow = Tap.Plugins.Workflow.ExcelWorkflowCompiler.Compile(workbookPath);
var run = await workflow.Plan.ExecuteAsync(CancellationToken);
Verdict = run.Verdict;
```

The AIUM example reads its workbook path from the `AIUM_WORKBOOK_PATH` environment variable.

Scripts run as trusted C# code and are not sandboxed.
