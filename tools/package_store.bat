# Builds a Store-ready package (.msixupload) for future Partner Center submission.
# Usage: run from any shell; produces files under GameArtManager\AppPackages\.
cd /d "%~dp0.."

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo ERROR: vswhere.exe not found at "%VSWHERE%". Install Visual Studio 2019 or newer.
  exit /b 1
)

set "MSBUILD="
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
if not defined MSBUILD (
  echo ERROR: MSBuild.exe not found via vswhere. Install the "Universal Windows Platform build tools" workload.
  exit /b 1
)

"%MSBUILD%" ^
  GameArtManager\GameArtManager.csproj ^
  -p:Configuration=Release ^
  -p:Platform=x64 ^
  -p:AppxBundle=Always ^
  -p:AppxBundlePlatforms="x64" ^
  -p:UapAppxPackageBuildMode=StoreUpload ^
  -p:AppxPackageSigningEnabled=false ^
  -p:GenerateAppInstallerFile=false ^
  -v:m -nologo

echo.
echo Store upload package (.msixupload) is under GameArtManager\AppPackages\
echo Upload it in Partner Center after reserving the app name.
