# Adds the "Universal Windows Platform development" workload (+ recommended SDKs) to VS 2022 Community.
# Run elevated: right-click -> Run with PowerShell as admin, or launched via Start-Process -Verb RunAs.

$ErrorActionPreference = "Stop"

# Locate the newest VS 2022 instance
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found - is Visual Studio installed?" }
$installPath = & $vswhere -latest -products * -property installationPath
if (-not $installPath) { throw "No Visual Studio instance found" }
Write-Host "Target instance: $installPath"

$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\setup.exe"

# UWP workload + recommended components (Windows SDK 10.0.22621, UWP build tools)
& $installer modify `
    --installPath "$installPath" `
    --add Microsoft.VisualStudio.Workload.Universal `
    --includeRecommended `
    --norestart --nocache `
    --passive | Out-Null

Write-Host "VS Installer exit code: $LASTEXITCODE"
exit $LASTEXITCODE
