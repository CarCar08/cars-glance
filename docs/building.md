# Building C.A.R.S. Glance

You need Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download). No Visual Studio, no admin rights.

```powershell
dotnet build CarsGlance.slnx -c Release
dotnet test tests\Glance.Tests\Glance.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 0.9.0 -Output artifacts-mine
```

The last line writes `CarsGlance-Setup-0.9.0.exe`, `SHA256SUMS.txt` and `release-notes.md`.

## Layout

| Project | What it is |
|---|---|
| `src\Glance.Core` | Settings, view models, the smart rules, time maths, and the parsers for weather, calendars, Todoist and media. No Windows APIs, so a future macOS/iOS port can reuse it. |
| `src\Glance` | `CarsGlance.exe`: the widget. Layered windows on the desktop layer, drawn with GDI+; Windows media session, Credential Manager, self-update. |
| `src\Glance.Setup.Core` | The installer's engine (netstandard2.0): payload manifest, safe unzip, folder swap with rollback, release and checksum parsing. |
| `src\Glance.Setup` | `CarsGlance-Setup.exe` (.NET Framework 4.8 + WPF-UI): the wizard, per-user install. |
| `tools\Glance.ReleaseTool` | Packs the payload, writes checksums and release notes. |
| `tests\Glance.Tests` | xUnit: every widget check plus installer tests in a sandbox. |

## Handy switches

```
CarsGlance.exe --selftest out.txt
CarsGlance.exe --preview out.png --view clock|countdown|nowplaying|agenda|nextup [--sample] [--weather] [--at 2026-11-18T14:18:00] [--height 1440]
CarsGlance.exe --preview out.png --view countdown --transition clock      (a strip of animation frames)
CarsGlance-Setup.exe --install --silent --sandbox C:\temp\glance-sandbox     (installs into a folder, no registry)
scripts\Test-Update.ps1 -OldSetup old.exe -NewSetup new.exe                  (update test in a sandbox)
```

`GLANCE_DATA_DIR` points the app at another data folder (tests, development).

## Releasing

Releases are built by GitHub Actions: push a tag `vX.Y.Z` (or `vX.Y.Z-rc.N` for a pre-release). The workflow runs the tests, builds the installer, runs the update test from the previous release, and publishes `CarsGlance-Setup-X.Y.Z.exe` and `SHA256SUMS.txt` with that version's `CHANGELOG.md` section as the notes.

## Signing (not enabled yet)

Planned for 1.6: [SignPath Foundation](https://signpath.org) (free for open-source projects), or Azure Trusted Signing. `build-release.ps1` marks where the signing step goes (before the checksum is taken).
