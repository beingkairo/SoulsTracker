# SoulsTracker

SoulsTracker is a Windows app for streams: total death tracking and local OBS overlays.

## Games

Automatic tracking is available for Dark Souls Remastered, Dark Souls II: Scholar of the First Sin, Dark Souls III, Bloodborne, Sekiro, Elden Ring, Black Myth: Wukong, and Lies of P.

Demon Souls uses a manual counter.

## Start streaming

1. Open SoulsTracker, choose your game on the **Main** tab, and pick a save or character when prompted.
2. Configure the retained overlay appearance and optional local text export.
3. Open the **Overlay** tab, enable Total Deaths, then copy the URL into an OBS **Browser Source**.

The Total Deaths overlay works at any size.

Keep SoulsTracker running while OBS uses the local Browser Source.

Browse and Rescan help with save locations. Save-based counters update after the game saves.

## Make it yours

The **Overlay** tab controls fonts, colors, size, background, alignment, outlines, and shadows.

The **Settings** tab includes TXT output for OBS text sources, update checks, and global hotkeys for manual counters.

## Privacy and read-only use

Everything stays on your PC. SoulsTracker reads approved game data and save data, then leaves game files and game memory untouched.

The overlay uses `127.0.0.1` on your computer. Keep generated overlay URLs private because they include a local access token.

Game updates can change saved data. Keep SoulsTracker current and follow each game's online and anti-cheat rules.

## For contributors

- Windows 10 or later
- .NET SDK version listed in [global.json](global.json)
- Node version listed in [web_overlay/.nvmrc](web_overlay/.nvmrc)

## Build and test

```powershell
dotnet restore SoulsTracker.sln --locked-mode
npm ci --prefix web_overlay
dotnet build SoulsTracker.sln --configuration Release --no-restore
dotnet test SoulsTracker.sln --configuration Release --no-build
npm run check --prefix web_overlay
npm test --prefix web_overlay
```

## Privacy

SoulsTracker stores its settings and progress locally. See [Privacy](docs/PRIVACY.md) for more information.

## Contributing and security

Read [Contributing](CONTRIBUTING.md) before opening a pull request.

Report security issues privately using the instructions in [Security](SECURITY.md).

## Trademark notice

SoulsTracker is an independent project. Game names describe compatibility.

## License

SoulsTracker is released under the [MIT License](LICENSE).
