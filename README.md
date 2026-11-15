# GameArt Manager (UWP / Xbox Game Bar widget)

Customize game artwork in the **Xbox PC app** library (and ROG Xbox Ally Full Screen
Experience) with art from [SteamGridDB](https://www.steamgriddb.com) — as an **Xbox Game Bar
widget** and as a standalone UWP app.

## Features

- **Dual game detection**: Xbox app manifests **plus** filename fallback for games whose
  manifests are missing/stale (Epic is a common case)
- **Authoritative local names**: Steam `appmanifest_*.acf`, Epic `.item` manifests,
  GOG Galaxy `galaxy-2.0.db`
- **Three-tier matching**: exact SteamGridDB platform-ID → fuzzy name search (FuzzySharp) →
  manual search override; every match cached
- **Apply best art** (highest-scored static PNG square grid, icon fallback, full paging),
  **custom local images** (png/jpg/webp/bmp/gif/ico → converted to PNG ≤1024 px) via the
  main window, **restore** originals at any time (`.bak`/`.new` backup protocol compatible
  with the original widget)
- Platforms: Steam, GOG, Epic, Ubisoft Connect, EA App, manually-added games

## Requirements

- Windows 10 19041+ / Windows 11
- Git (for cloning)
- Visual Studio 2022 with the **Universal Windows Platform development** workload and
  **Windows 11 SDK 10.0.22621.0** (or adjust `TargetPlatformVersion` in the csproj)
- NuGet packages restore automatically (`Microsoft.Gaming.XboxGameBar`, FuzzySharp,
  SkiaSharp, sqlite-net-pcl, Newtonsoft.Json)
- A GitHub Actions workflow builds the project on every push — see
  `.github/workflows/build.yml`

## Build & install locally (sideload)

The app is not on the Microsoft Store yet — until it is, build and install it yourself.
You sign it with a **local test certificate generated on your machine**; it is never
committed to the repository, and every builder signs with their own.

### Prerequisites

- The build requirements above (Visual Studio 2022 + UWP workload)
- **Admin rights**, needed once to trust the certificate during install
- The Xbox app installed, with at least one Steam/GOG/Epic/Ubisoft/EA game in its library

### 1. Build the package

```powershell
git clone https://github.com/VGFP/GameArtManager.git
cd GameArtManager
powershell -File tools\build_local.ps1
```

`build_local.ps1` locates MSBuild, creates `GameArtManager_TemporaryKey.pfx` (a
passwordless local signing certificate, generated once and gitignored), restores the
NuGet packages and builds a **signed** Debug|x64 package under
`GameArtManager\AppPackages\`.

Prefer building inside Visual Studio? Open `GameArtManager.sln`, and in the manifest
designer's *Packaging* tab choose the generated `.pfx` (or create a test certificate) —
that writes `PackageCertificateKeyFile` into the csproj, so keep that local change out
of your commits.

### 2. Install

From an **elevated** PowerShell:

```powershell
powershell -File tools\sign_and_deploy.ps1
```

It imports your local certificate into *Trusted People* (why admin is needed), installs
the Debug framework dependencies (`Microsoft.NET.CoreRuntime.2.2` etc.) and registers
the package. It auto-discovers the newest built `.msixbundle` and accepts an optional
`-CertPassword` (default empty, matching the generated certificate). Prefer clicking?
Run the `Add-AppDevPackage.ps1` inside the `AppPackages\..._Debug_Test` folder — same
result.

### 3. Grant file access (one-time)

The app container needs direct access to the Xbox app's data folder and launcher data
(Steam manifests, GOG Galaxy db). On first launch the app offers a shortcut to
**Settings → Privacy & security → File system**. Some machines don't apply the
`broadFileSystemAccess` toggle correctly for sideloaded packages — if scanning finds
nothing, run:

```powershell
powershell -File tools\grant_access.ps1
```

It grants the package's container SID read/modify access via ACLs on: the Xbox app's
`ThirdPartyLibraries` folder, the Steam install root **and every library listed in
`libraryfolders.vdf`** (C:/D:/E:/…), `C:\ProgramData\GOG.com` and the Epic manifests
folder. Re-run it if the Xbox app updates and recreates its folder.

### 4. First run

Launch **GameArt Manager** from the Start menu, or add the widget (**Win+G** → widget
menu → GameArt). Click **Match** — you'll be asked for a free
[SteamGridDB](https://www.steamgriddb.com) API key (create an account → Preferences →
API → generate). The key is stored in the Windows Credential Locker, never in a file.

### Updating / uninstalling

- After pulling new changes: rerun `tools\build_local.ps1`, then `tools\redeploy.ps1`
  (no elevation needed) — it reinstalls while keeping your settings and match cache.
- Uninstall like any app: Settings → Apps. Optionally remove the leftover test
  certificate: `certlm.msc` → *Trusted People* → delete `GameArtManager`.

> Note: inside an app container the `LOCALAPPDATA`/known-folder APIs are virtualized to
> the package folder — the code resolves all paths from fixed locations instead.

## Using the Game Bar widget

1. Press **Win+G** in a game → open the **Widget menu** → find **GameArt** → add it.
2. Scan → Auto-match (needs a free SteamGridDB API key set in the full app's Settings) →
   Apply all / per-game Apply / Restore.
3. "Full app" button opens the main window (custom images, manual search with art previews,
   settings).

## Privacy

The app keeps all data on the machine — game libraries, settings, match caches and artwork
never leave it. The only exception is SteamGridDB API queries (game names/IDs + your API
key). See [PRIVACY.md](PRIVACY.md) for the full policy.

## Store submission checklist (when ready)

1. Create a Microsoft Partner Center account and **reserve the app name**.
2. Update the manifest Identity (`Name`/`Publisher`) to the reserved name + publisher ID.
3. `tools\package_store.bat` → produces `.msixupload` (Release, .NET Native).
4. In the submission's **Restricted capabilities** section, justify
   `broadFileSystemAccess`, e.g.:
   > The app's core function is replacing cached artwork files inside the Xbox console
   > companion app's package data folder (`...\Microsoft.GamingApp_8wekyb3d8bbwe\LocalState\`),
   > which is outside the calling app's own storage. No picker flow can express writing to
   > another package's storage; broad file system access is required for the app to fulfill
   > its single purpose.
5. Provide a privacy policy URL (the app stores its settings/matches locally only) and
   complete the age-rating questionnaire.

## Limitations

- First-party Game Pass titles render art from Microsoft's catalog — not replaceable.
- Battle.net is skipped (the Xbox app stores no art for it).
- Xbox app cache refreshes can revert art; Restore / re-apply handles it.
- Ubisoft Connect ids resolve via the SteamGridDB `uplay` lookup (local Ubisoft data is
  encrypted).

## Credits

Inspired by [lirrik/steamgriddb-xbox](https://github.com/lirrik/steamgriddb-xbox) and
[tetraguy/Xbox-PC-Library-Art](https://github.com/tetraguy/Xbox-PC-Library-Art). GameArt
Manager started as a Python prototype; this C# version superseded it. Not
affiliated with Microsoft, Valve or SteamGridDB; artwork belongs to its creators.

## License

Released under the MIT License — see [LICENSE](LICENSE).
