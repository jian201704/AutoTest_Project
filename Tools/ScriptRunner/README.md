# OpenTAP Script Runner

This is a standalone runner for `.TapPlan`, `.csx`, Excel workflow (`.xlsx`/`.xlsm`), and standalone Python (`.py`) files. It has no test-plan editor or separate build workflow; choose a file, run it, abort it, and inspect the log. Script and Excel inputs are assembled into an in-memory OpenTAP plan and do not need to be saved as `.TapPlan` first.

The `.csx` handler uses the existing Script TestStep, which compiles C# source at runtime. Excel inputs use the project's `ExcelWorkflowCompiler`. A standalone `.py` runs with `py.exe -3` when available, otherwise `python.exe` from `PATH`; set `PYTHON` to select another executable. The Python process working directory is the script's folder, and its output is captured in the run log. This is separate from OpenTAP Python plugin modules, which define TestStep types for plans.

## Build and Run

```powershell
dotnet build .\Tools\ScriptRunner\OpenTap.ScriptRunner.csproj
dotnet run --project .\Tools\ScriptRunner\OpenTap.ScriptRunner.csproj
```

Python TestSteps inside `.TapPlan` files use the official OpenTAP Python package. Configure it with `Plugins/Python/setup-python.ps1` before building. Python 3.7 through 3.13, matching the host architecture, is required.