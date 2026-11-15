# Deploys GameArtManager: imports the signing cert and registers the package.
# Output is logged to tools\deploy_log.txt (gitignored) for inspection.
# Usage: .\sign_and_deploy.ps1 [-CertPassword <password>]  (VS auto-generated certs have no password)
param([string]$CertPassword = "")

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo "GameArtManager"
$pfx = Join-Path $project "GameArtManager_TemporaryKey.pfx"

if (-not (Test-Path $pfx)) {
    Write-Host "No signing certificate found. Build the project once in Visual Studio - it auto-generates GameArtManager_TemporaryKey.pfx - or provide your own."
    exit 1
}

# VS auto-generated certificates have an empty password, so $CertPassword = "" is valid.
$securePassword = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText

Start-Transcript -Path "$PSScriptRoot\deploy_log.txt" -Force

try
{
    $appPackages = Join-Path $project "AppPackages"

    # 1. import the self-signed cert into Trusted People (required for sideload)
    Import-PfxCertificate -FilePath $pfx -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" -Password $securePassword | Out-Null
    Write-Host "Certificate imported."

    # 2. install framework dependencies (Debug runtimes, MS-signed) from the newest Debug_Test package folder
    $testDir = Get-ChildItem $appPackages -Directory -Filter "GameArtManager_*_Debug_Test" |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $testDir) { throw "No GameArtManager_*_Debug_Test folder found under $appPackages. Create the package in Visual Studio first." }
    $deps = Join-Path $testDir.FullName "Dependencies\x64"
    Get-ChildItem $deps -Filter *.appx | ForEach-Object {
        Write-Host "Installing framework $($_.Name)..."
        Add-AppxPackage -Path $_.FullName -ErrorAction Stop
    }

    # 3. register the newest package bundle
    $bundle = Get-ChildItem $appPackages -Recurse -Filter *.msixbundle |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $bundle) { throw "No .msixbundle found under $appPackages. Create the package in Visual Studio first." }
    Add-AppxPackage -Path $bundle.FullName -ErrorAction Stop
    Write-Host "Package installed."
}
catch
{
    Write-Host "DEPLOY FAILED: $($_.Exception.Message)"
}
finally
{
    Stop-Transcript | Out-Null
}
