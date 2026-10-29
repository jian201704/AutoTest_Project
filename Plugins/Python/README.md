# OpenTAP Python Support

Python TestStep modules belong to this plugin area and use the official OpenTAP Python package. OpenTAP embeds CPython through Python.NET; it does not launch a separate `python.exe` for every step.

## Runtime Setup

Prerequisites are a built OpenTAP 9.34 source tree and 64-bit CPython 3.7 through 3.13. From the repository root, run:

```powershell
.\Plugins\Python\setup-python.ps1
```

The script installs OpenTAP 9.34 and the official `Python` package into the shared, ignored `Tools/OpenTapRuntime934` host runtime. Rebuild and launch WpfEditor or ScriptRunner afterward. Both hosts copy the official plugin assemblies and this directory's Python modules into their OpenTAP plugin search paths.

To use another OpenTAP source location:

```powershell
.\Plugins\Python\setup-python.ps1 -OpenTapSourceRoot "D:\src\opentap-9.34.0"
```

## Smoke Test

After setup and rebuild, add **Python Smoke Step** from the Test Steps library, then run the plan in either host. The step logs its message and returns `Pass`. Its source is in `PythonModules/AutoTestPython/PythonSmokeStep.py`.

Python module source is maintained under `PythonModules`. Keep host-specific deployment behavior in the host project rather than adding WPF dependencies to the Python plugin modules.