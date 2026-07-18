# SoulsTracker

SoulsTracker is a Windows desktop companion for Soulsborne streamers. It tracks Total Deaths, maintains game-specific boss lists, and provides local OBS browser overlays.

## Features

- Read-only game Total Deaths readers for Dark Souls Remastered, Dark Souls II: Scholar of the First Sin, Dark Souls III, and Sekiro.
- Independent manual death counters for Bloodborne and Demon Souls.
- Game-specific boss checklists with local persistence.
- Total Deaths and Boss List OBS browser overlays, hosted only on the local machine.
- Custom overlay typography, colors, effects, markers, and text-file exports.
- Configurable global hotkeys for manual profiles.

## Important safety notes

Game-memory support is read-only and version-sensitive. SoulsTracker never writes game memory, injects code, automates input, edits saves, or changes gameplay. Use it at your own discretion and follow each game's online and anti-cheat rules.

The OBS overlay service binds only to `127.0.0.1`. Its generated URL includes a local access token; treat that URL as private configuration and do not post it publicly.

## Requirements

- Windows 10 or later
- .NET SDK version specified in [global.json](global.json)
- Node version specified in [web_overlay/.nvmrc](web_overlay/.nvmrc)

## Build and test

```powershell
dotnet restore SoulsTracker.sln --locked-mode
npm ci --prefix web_overlay
dotnet build SoulsTracker.sln --configuration Release --no-restore
dotnet test SoulsTracker.sln --configuration Release --no-build
npm run check --prefix web_overlay
npm test --prefix web_overlay
```

For a local release publish smoke test:

```powershell
./scripts/Build-Release.ps1 -SkipInstaller
```

## Privacy

SoulsTracker stores its settings locally. It does not include telemetry, cloud synchronization, user accounts, or remote overlay hosting. See [Privacy](docs/PRIVACY.md).

## Contributing and security

Read [Contributing](CONTRIBUTING.md) before opening a pull request. Please report vulnerabilities privately according to [Security](SECURITY.md), not through a public issue.

## Trademark notice

SoulsTracker is an independent project and is not affiliated with or endorsed by FromSoftware, Bandai Namco, Sony Interactive Entertainment, Activision, or OBS. Game names are used only for compatibility and identification.

## License

Released under the [MIT License](LICENSE).
