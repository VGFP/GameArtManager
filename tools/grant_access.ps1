# Grants the GameArt Manager app container direct access to the Xbox app's
# third-party library folder and to the launcher data used for name resolution
# (Steam manifests incl. all libraries, GOG Galaxy db, Epic manifests).
# Needed once after sideloading (some machines don't apply the
# broadFileSystemAccess consent correctly for sideloaded apps).
$ErrorActionPreference = "Continue"

$family = (Get-AppxPackage GameArtManager).PackageFamilyName
if (-not $family) { throw "GameArtManager package not found" }

# the app container SID from the AppContainer mappings
$map = "Registry::HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings"
$sid = $null
Get-ChildItem $map | ForEach-Object {
    $moniker = (Get-ItemProperty $_.PSPath -Name Moniker -ErrorAction SilentlyContinue).Moniker
    if ($moniker -eq $family) { $sid = $_.PSChildName }
}
if (-not $sid) { throw "Container SID not found for $family" }
Write-Host "Container SID: $sid"

function Grant-Access([string]$path, [string]$rights) {
    if (Test-Path $path) {
        icacls $path /grant ("*" + $sid + ":(" + $rights + ")") /T /C 2>&1 | Out-Null
        Write-Host "Granted $rights : $path"
    }
}

# 1. Xbox app library (read + write)
$xboxLib = Join-Path $env:LOCALAPPDATA "Packages\Microsoft.GamingApp_8wekyb3d8bbwe\LocalState\ThirdPartyLibraries"
Grant-Access $xboxLib "OI)(CI)M"

# 2. Steam: install root + every library from libraryfolders.vdf
$steamRoots = @("C:\Program Files (x86)\Steam", "C:\Program Files\Steam") | Where-Object { Test-Path $_ }
foreach ($root in $steamRoots) {
    Grant-Access $root "OI)(CI)RX"
    $libraryVdf = Join-Path $root "steamapps\libraryfolders.vdf"
    if (Test-Path $libraryVdf) {
        $content = Get-Content $libraryVdf -Raw
        # extract "path" values from the vdf
        $matches2 = [regex]::Matches($content, '"path"\s*"([^"]+)"')
        foreach ($m in $matches2) {
            $lib = $m.Groups[1].Value.Replace("\\", "\")
            if (Test-Path $lib) { Grant-Access $lib "OI)(CI)RX" }
        }
    }
}

# 3. GOG Galaxy db + Epic manifests
Grant-Access "C:\ProgramData\GOG.com" "OI)(CI)RX"
Grant-Access "C:\ProgramData\Epic\EpicGamesLauncher" "OI)(CI)RX"

Write-Host "Done. Restart GameArt Manager and click Scan."
