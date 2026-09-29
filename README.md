# SoulsTracker

SoulsTracker is a Windows app for streams: total death tracking, optional TXT output, and an opt-in online overlay.

## Games

Automatic tracking is available for Dark Souls Remastered, Dark Souls II: Scholar of the First Sin, Dark Souls III, Bloodborne, Sekiro, Elden Ring, Black Myth: Wukong, and Lies of P.

Demon Souls uses a manual counter.

## Start streaming

1. Open SoulsTracker, choose your game on the **Main** tab, and pick a save or character when prompted.
2. Configure the retained overlay appearance and optional local text export.
3. Open the **Overlay** tab. SoulsTracker automatically prepares one private Overlay URL the first time you use it.
4. Use the icon-only **Copy URL** control and add the URL to your streaming software.

This development build connects only to https://overlay.beingkairo.com. No account, login, or operator action is required. Local tracking and TXT output do not require the Overlay or an internet connection.

The Total Deaths overlay works at any size.

After setup, SoulsTracker sends the accepted death display and applied appearance while running. Closing it leaves the last published state online. Automatic readers do not overwrite that state until a current accepted observation arrives. An already open browser retains its last display during a disconnect; a cold offline reload cannot hydrate it.

Existing local overlay URLs need one deliberate replacement. Styles embedded in old URLs are not migrated: reproduce them in Desktop appearance settings before switching. The Overlay URL has no style overrides. Fonts must be installed on the streaming PC; existing title-icon Apply and number-only font-size limitations remain.

Keep the Overlay URL private. Anyone who has it can view the overlay. If automatic preparation is temporarily unavailable, **Try again** resumes the same protected request instead of creating another identity.

Choose Directory and Refresh help with save locations. Save-based counters update after the game saves.

## Make it yours

The **Overlay** tab controls fonts, colors, size, background, alignment, outlines, and shadows.

The **Settings** tab includes TXT output for text sources, update checks, and global hotkeys for the manual counter and Elden Ring missed deaths.

## Privacy and read-only use

SoulsTracker reads approved game data and save data, then leaves game files and game memory untouched. Local settings, save selection and counters remain on your PC.

Opening the Overlay tab prepares online publication and sends the accepted display value/availability and applied appearance, including custom title and font name, to Cloudflare. It sends no game/save paths, character/slot names or raw observations. Cloudflare processes connection metadata and retains the latest published state. No localhost fallback is used.

Game updates can change saved data. Keep SoulsTracker current and follow each game's online and anti-cheat rules.

## For contributors

- Windows 10 or later
- .NET SDK version listed in [global.json](global.json)
- Node version listed in [web_overlay/.nvmrc](web_overlay/.nvmrc)

## Build and test

```powershell
dotnet restore SoulsTracker.sln --locked-mode
npm ci --prefix web_overlay
npm ci --prefix cloud_overlay
npm run build --prefix web_overlay
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
