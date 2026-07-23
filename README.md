# SoulsTracker

SoulsTracker is a Windows desktop companion for Soulsborne streamers. It tracks Total Deaths, maintains game-specific boss lists, and provides local OBS browser overlays.

## Getting started

### Pick your game

Install SoulsTracker, then open it before you open OBS. On the **Main** tab, pick the game you are playing from the dropdown.

SoulsTracker tracks deaths automatically for Dark Souls Remastered, Dark Souls II: Scholar of the First Sin, Dark Souls III, and Sekiro.

Elden Ring uses a save file instead: after accepting the Elden Ring notice, choose your local `ER0000.sl2` file and character slot. SoulsTracker only reads the file after the game saves, so the number can take a moment to update.

Bloodborne and Demon Souls are supported too, but their death counters are manual. Hit `+1` when you die, use `-1` for a correction, or set hotkeys if you would rather not click the buttons during a stream.

Your boss list is on the Main tab as well. Check off a boss when you beat it. Death totals and boss progress save automatically, and every game keeps its own progress.

### Add it to OBS

Open SoulsTracker first, then head to the **Overlay** tab. Turn on the Total Deaths overlay, the Boss List overlay, or both. Copy the URL for the overlay you want.

In OBS, add a **Browser Source**, paste in that URL, then move and resize it however you like.

**Important: open SoulsTracker before OBS.** If OBS was already open when you started SoulsTracker, refresh each SoulsTracker Browser Source in OBS after the app says the overlay is ready.

You can change the font, colors, size, background, markers, alignment, outline, shadow, and defeated-boss style in the Overlay tab. When it looks right, use the Apply button for that overlay to update the preview and OBS source.

### Optional extras

The **Settings** tab lets you choose a death sound, adjust its volume, and save your death count or boss list to TXT files. TXT output is useful if you would rather use OBS text sources or set things up your own way in another app.

## IMPORTANT: Disclaimer

SoulsTracker does not write to game memory, edit save files, inject code, automate gameplay, or change any game values. For games with automatic tracking, it only reads the death-total information. Elden Ring reads the user-selected save file instead of game memory. Bloodborne and Demon Souls use manual counters.

Automatic tracking is version-sensitive. A game update can change enough that tracking needs a SoulsTracker update too. Use SoulsTracker at your own discretion, especially online, and follow the game’s online and anti-cheat rules.

## Features

- Read-only game Total Deaths readers for Dark Souls Remastered, Dark Souls II: Scholar of the First Sin, Dark Souls III, and Sekiro.
- Read-only Elden Ring Total Deaths from a user-selected `ER0000.sl2` save file and character slot.
- Independent manual death counters for Bloodborne and Demon Souls.
- Game-specific boss checklists with local persistence.
- Total Deaths and Boss List OBS browser overlays, hosted only on the local machine.
- Custom overlay typography, colors, effects, markers, and text-file exports.
- Configurable global hotkeys for manual profiles.

## Important safety notes

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
