# GameArt Manager — Privacy Policy

**Effective date:** 2026-11-15

GameArt Manager keeps all data on your machine. The only exception is SteamGridDB API
queries (game names/IDs and your API key), described under *Network* below.

## What the app reads (local only)

- The Xbox app's third-party library cache folder
  (`%LOCALAPPDATA%\Packages\Microsoft.GamingApp_8wekyb3d8bbwe\LocalState\ThirdPartyLibraries`)
- Steam's `appmanifest_*.acf` files and `libraryfolders.vdf`
- A temporary read-only copy of the GOG Galaxy database (`galaxy-2.0.db`)
- Epic Games Launcher `.item` manifests

These are used to detect installed games and their names.

## What the app writes (local only)

- Replacement artwork `.png` files, plus `.bak`/`.new` backups, inside the Xbox app's
  cache folder
- Its own settings and match cache in the app's local data folder

## Network

The app only talks to the SteamGridDB API (https://www.steamgriddb.com). It sends game
names/IDs for matching, sends your SteamGridDB API key as a Bearer token, and downloads
artwork from SteamGridDB CDN URLs. Nothing else leaves the machine.

## What the app never does

No telemetry, no analytics, no accounts, no advertising, no data collection, no sale or
sharing of data.

## API key storage

Your SteamGridDB API key is stored locally in the Windows Credential Locker
(DPAPI-encrypted), never in plain text.

## File system permission (broadFileSystemAccess)

The app's single purpose requires reading and writing another package's cache folder (the
Xbox app's library cache), which Windows only allows via the `broadFileSystemAccess`
capability. It is used solely for the local reads and writes described above.

## Contact

**TODO(owner):** add contact email or GitHub profile URL before Store submission
