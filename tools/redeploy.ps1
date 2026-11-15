# Quick redeploy: preserves the package LocalState (settings/match cache),
# removes the old package, installs the new bundle, restores the data.
$repo = Split-Path -Parent $PSScriptRoot
$appPackages = Join-Path $repo "GameArtManager\AppPackages"

# discover the newest bundle (any version/configuration)
$bundle = Get-ChildItem $appPackages -Recurse -Filter *.msixbundle |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $bundle) {
    Write-Host "No .msixbundle found under $appPackages. Create the package first (tools\package_store.bat or Visual Studio)."
    exit 1
}

$pkg = Get-AppxPackage GameArtManager
if (-not $pkg) { throw "GameArtManager package not found" }
$state = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalState"
$backup = Join-Path $PSScriptRoot "localstate_backup"

Get-Process GameArtManager -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# preserve user data (settings.json, matches.json, ...)
if (Test-Path $state) {
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    Copy-Item $state $backup -Recurse -Force
}

Get-AppxPackage GameArtManager | Remove-AppxPackage -ErrorAction SilentlyContinue
Add-AppxPackage -Path $bundle.FullName -ErrorAction Stop

# restore user data
if (Test-Path $backup) {
    if (Test-Path $state) { Remove-Item $state -Recurse -Force }
    Copy-Item $backup $state -Recurse -Force
    Remove-Item $backup -Recurse -Force
}
Write-Host "redeployed (LocalState preserved)"
