# SoulsTracker

SoulsTracker is a Windows app I originally made for my own streams. It tracks deaths, keeps a separate boss checklist for each game, and gives you local browser overlays for OBS.

Automatic death tracking currently works for:

- Dark Souls Remastered
- Dark Souls II: Scholar of the First Sin
- Dark Souls III
- Sekiro: Shadows Die Twice
- Elden Ring
- Black Myth: Wukong
- Lies of P

Elden Ring and Black Myth: Wukong are tracked through local save files. Bloodborne and Demon's Souls use manual death counters.

Everything is stored locally. SoulsTracker does not have user accounts, telemetry, cloud syncing, or remote overlay hosting.

## Getting started

### Pick your game

Install SoulsTracker and open it before opening OBS. Go to the **Main** tab and choose the game you are playing.

For Elden Ring, SoulsTracker searches for local `ER0000.sl2` files and lets you choose the character you are using. The character list shows each character's name and level.

For Black Myth: Wukong, SoulsTracker searches supported Steam and Epic save locations. If it finds one clear save slot, it selects it automatically. If it finds more than one, choose the one you are using.

For Lies of P, SoulsTracker searches local Steam character saves and lets you choose the character you are using. Paired character saves are handled together so the selected character remains stable when the game writes both files.

Browse and Rescan are available if your saves are stored somewhere else. Save-based counters only update after the game saves, so the number may take a moment to change.

Bloodborne and Demon's Souls are manual. Use `+1` when you die, `-1` to fix a mistake, or set global hotkeys so you do not have to click the buttons during a stream.

The boss checklist is also on the Main tab. Each game keeps its own death total and boss progress, and everything saves automatically.

### Add it to OBS

Open the **Overlay** tab and enable the Total Deaths overlay, the Boss List overlay, or both. Copy the URL for the overlay you want to use.

In OBS, add a **Browser Source** and paste the URL.

For the Boss List, use a **600 x 1080** browser source. Position it in your scene and lock it. Long boss names wrap inside the widget so the list does not move around during a stream.

The Total Deaths overlay can be moved and resized however you want.

SoulsTracker should be opened before OBS. If OBS was already running, refresh each SoulsTracker Browser Source after the app says the overlay is ready.

You can change the font, colors, size, background, markers, alignment, outline, shadow, and defeated boss style from the Overlay tab. Click Apply to update the preview and OBS source.

### Other settings

The **Settings** tab includes:

- Death sounds and volume controls
- TXT output for the death total and boss list
- Global hotkeys for manual counters

TXT output is there if you would rather use a normal OBS text source or build your own overlay.

## Disclaimer

SoulsTracker is read-only. It does not edit save files, write to game memory, inject code, automate gameplay, or change anything inside the games.

The automatically tracked Souls games only read the stored death total. Elden Ring and Black Myth: Wukong read local save files instead. Bloodborne and Demon's Souls do not read the game at all and use manual counters.

Automatic tracking can be affected by game updates. If a game changes how its data is stored, SoulsTracker may also need an update. Use it at your own discretion, especially when playing online, and follow each game's online and anti-cheat rules.

## Local overlay security

The overlay server only runs on `127.0.0.1`, which means it is limited to your own computer.

Generated overlay URLs include a local access token. Treat those URLs like private OBS settings and do not post them publicly.

## Requirements

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

For a local release publish test:

```powershell
./scripts/Build-Release.ps1 -SkipInstaller
```

## Privacy

SoulsTracker stores its settings and progress locally. See [Privacy](docs/PRIVACY.md) for more information.

## Contributing and security

Read [Contributing](CONTRIBUTING.md) before opening a pull request.

Security issues should be reported privately using the instructions in [Security](SECURITY.md), not through a public issue.

## Trademark notice

SoulsTracker is an independent project. It is not affiliated with or endorsed by FromSoftware, Bandai Namco, Sony Interactive Entertainment, Activision, or OBS.

Game names are only used to describe compatibility.

## License

SoulsTracker is released under the [MIT License](LICENSE).
