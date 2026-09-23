# Element

Add Steam games in one click. Element installs any game as a clean `.lua` plus its depot `.manifest` files — the lua lands in `config\stplug-in`, manifests in `depotcache`, straight from Hubcap or Ryuu.

## Download

Grab the latest release from [GitHub Releases](https://github.com/thecloudyy/Element/releases/latest):

- `Element-Setup-vX-win-x64.exe` — per-user setup installer, no admin rights (requires .NET 8 Desktop Runtime)
- `Element-vX-win-x64.exe` — single-file portable exe, run it anywhere

## Features

- **Per-app lua + manifest downloads** — each download pulls the game's `.lua` and only that app's manifest bundle (Hubcap `/api/v1/manifest`, Ryuu `file_type=manifest`), installed straight into `depotcache`. Never a bulk fetch, never Steam's job.
- **Two sources** — Hubcap (default, quota-based) and Ryuu (key-based, incl. non-public branch zips). Switch anytime in settings.
- **Depot downloads** — raw depot content with keys from your lua, per-depot progress, resume with validation, free-space checks up front.
- **SteamStub removal** — strip DRM from game executables via built-in Steamless integration.
- **"Add with Element" store button** — the ManifestDeXCore plugin injects a button into Steam store pages so you can install without leaving Steam.
- **Library management** — visual grid with covers, search and filters, per-game launch option editor, manifest pinning per build, optional blocking of Steam auto-updates.
- **Quiet feedback** — real download queue with history, per-item progress, non-blocking toasts. UI in English, Spanish, Polish and Turkish.

## Requirements

- Windows 10/11 x64
- .NET 8 Desktop Runtime
- Steam installed

## Build from source

API keys are intentionally **not** in this repo. Bake your own at compile time (once per machine):

```powershell
[Environment]::SetEnvironmentVariable('ELEMENT_HUBCAP_KEY', '<your-hubcap-key>', 'User')
[Environment]::SetEnvironmentVariable('ELEMENT_RYUU_KEY', '<your-ryuu-key>', 'User')
```

Then publish and package (paths to `ISCC.exe` vary by install):

```powershell
dotnet publish src\ElementGui\Element.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-single
& "<path-to>\ISCC.exe" /DAppVersion=1.4.1 installer.iss
```

Without keys the app still builds and runs — downloads just report their sources as locked until you add keys (env vars at build time, or your own Hubcap key in settings).

## Translations

UI strings live in `src/ElementGui/Resources/Strings.*.resx` (the `.Designer.cs` is hand-maintained, so keep it in sync when adding keys). Spanish, Polish and Turkish are fully translated; remaining English strings are proper nouns only (Steam, Metacritic, DLL names, `App ID`, `DLC`).
