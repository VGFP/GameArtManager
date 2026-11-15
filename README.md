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

## Build & sideload

```bat
msbuild GameArtManager.sln -p:Configuration=Debug -p:Platform=x64 -p:AppxPackageSigningEnabled=true
powershell -File tools\sign_and_deploy.ps1 [-CertPassword <pw>]     (run elevated: imports cert + frameworks + package)
```

`sign_and_deploy.ps1` takes an optional `-CertPassword` parameter (default empty — matching
the certificate Visual Studio auto-generates) and auto-discovers the newest built
`.msixbundle` under `AppPackages\`. It creates/uses the self-signed cert `CN=GameArtManager`
(`GameArtManager_TemporaryKey.pfx`), imports it into *Trusted People*, installs the Debug
frameworks (`Microsoft.NET.CoreRuntime.2.2` etc.) and registers the package. The `.pfx`
signing key is generated locally by Visual Studio on first build and is never committed to
the repository — every contributor signs with their own local test certificate. To redeploy
after a rebuild: `tools\redeploy.ps1` (no elevation needed).

**File access (required once):** the app container needs direct access to the Xbox app's
data folder and launcher data (Steam manifests, GOG Galaxy db). Some machines don't apply
the `broadFileSystemAccess` Settings toggle correctly for sideloaded packages, so run:

```powershell
powershell -File tools\grant_access.ps1
```

It grants the package's container SID read/modify access via ACLs on: the Xbox app's
`ThirdPartyLibraries` folder, the Steam install root **and every library listed in
`libraryfolders.vdf`** (C:/D:/E:/…), `C:\ProgramData\GOG.com` and the Epic manifests
folder. Re-run it if the Xbox app updates and recreates its folder.

Also note: inside an app container the `LOCALAPPDATA`/known-folder APIs are virtualized to
the package folder — the code resolves all paths from fixed locations instead.

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
