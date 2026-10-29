param(
    [string]$OpenTapSourceRoot = (Join-Path $PSScriptRoot "..\..\..\opentap-9.34.0"),
    [string]$RuntimeDirectory = (Join-Path $PSScriptRoot "..\..\Tools\OpenTapRuntime934")
)

$ErrorActionPreference = "Stop"
$tap = Join-Path $OpenTapSourceRoot "bin\Debug\tap.exe"
if (-not (Test-Path $tap)) {
    throw "OpenTAP CLI not found at '$tap'. Build the opentap-9.34.0 source tree first."
}

& $tap package install --target $RuntimeDirectory --version 9.34 --dependencies --non-interactive OpenTAP
if ($LASTEXITCODE -ne 0) {
    throw "Could not install OpenTAP 9.34 into '$RuntimeDirectory'."
}

& $tap package install --target $RuntimeDirectory --dependencies --non-interactive Python
if ($LASTEXITCODE -ne 0) {
    throw "Could not install the official OpenTAP Python plugin into '$RuntimeDirectory'."
}

Write-Host "OpenTAP Python is ready in '$RuntimeDirectory'. Rebuild WpfEditor or ScriptRunner to discover Python TestSteps."