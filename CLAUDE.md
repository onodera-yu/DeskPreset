# DeskPreset

Windows 11 app that saves which apps open on each virtual desktop (and where) as presets, and restores them in one click.
WPF on .NET 10, no NuGet dependencies. User-facing strings and README are Japanese; code and comments are English.

## Commands

```
dotnet build                                   # build all projects
./build.ps1                                    # self-contained single-file zip -> dist/DeskPreset-v<ver>-win-x64.zip
src/DeskPreset.Cli/bin/Debug/net10.0-windows/deskpreset-cli.exe desktops|windows|list|capture <name>|run <name>
src/DeskPreset.App/bin/Debug/net10.0-windows/DeskPreset.exe [--run "<preset>"]
```

There is no test project. Verify changes with the CLI (`desktops`, `windows`, `capture`) and by launching the app.

**Always set `DESKPRESET_HOME` to a scratch folder when testing**, so the user's real presets in
`%APPDATA%\DeskPreset\presets.json` are never read or overwritten. `run` really launches and moves windows on the
user's desktops — use a preset with something harmless (Notepad) and don't close apps you didn't start.

## Layout

- `src/DeskPreset.Core` — everything not UI
  - `Interop/VirtualDesktopService.cs` — undocumented shell COM interfaces called through raw vtable slots.
    IIDs and slot layouts per Windows build live in `Profiles`; users can add more via `interop.json`.
  - `Interop/WindowEnumerator.cs` — top-level window listing, placement get/set
  - `PresetCapture.cs` — current windows → preset (resolves AppUserModelIDs for Store apps / PWAs)
  - `PresetLauncher.cs` — preset → reuse/launch windows, move to desktops, restore placement
  - `PathPortability.cs` — `%ENV%` + `app-*` wildcard paths so presets work on other PCs
  - `Models.cs`, `PresetRepository.cs` — observable models, JSON storage, export/import
- `src/DeskPreset.App` — WPF UI (Fluent `ThemeMode="System"`), `MainViewModel`, `AutoSaver`, `AppPickerWindow`
- `src/DeskPreset.Cli` — `deskpreset-cli.exe` for debugging/automation (not shipped in the release zip)

## Rules and gotchas

- COM objects in `VirtualDesktopService` are apartment-bound. In the WPF app keep all calls on the UI thread
  (async/await is fine, never `Task.Run`). The CLI runs MTA on purpose (no `[STAThread]`).
- Use `SyncProgress<T>` for launcher logs; `Progress<T>` posts asynchronously and loses late messages.
- Stored paths must stay portable: call `PathPortability.Generalize` when creating entries and
  `PathPortability.Expand` / `ExpandArguments` before using them.
- Window coordinates are physical pixels: the app is Per-Monitor V2 DPI aware (`app.manifest`).
- CLI and GUI exe names must differ by more than case (Windows file names are case-insensitive).
- `SwitchToDesktop` uses `null` = "don't switch"; keep `JsonIgnoreCondition.Never` on it.

## Branches and release

See CONTRIBUTING.md. In short:

- Never commit or push to `main` or `develop` directly (rulesets block it). Branch from `develop` as
  `feature/*`, `fix/*`, `chore/*` or `docs/*` and open a PR to `develop` (squash merge).
- `main` holds released code only. Releases are a PR `develop` → `main` (merge commit, never squash);
  urgent fixes are `hotfix/*` from `main`, then back-merged into `develop`.
- Every push to `main` runs `.github/workflows/release.yml`: it reads `<Version>` from `Directory.Build.props`,
  creates tag `v<Version>` and publishes the zip. Bump the version before the release PR; CI rejects a PR to
  `main` whose version is already tagged.
- `.github/rulesets/*.json` are the branch/tag protection rules (imported in GitHub settings, not applied by git).
- `.claude/` is not committed; a local `/release` skill may exist there.
