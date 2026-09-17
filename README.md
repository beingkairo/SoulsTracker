# SoulsTracker

SoulsTracker is a Windows app for streams: total death tracking, optional TXT output, and opt-in hosted OBS overlays.

## Games

Automatic tracking is available for Dark Souls Remastered, Dark Souls II: Scholar of the First Sin, Dark Souls III, Bloodborne, Sekiro, Elden Ring, Black Myth: Wukong, and Lies of P.

Demon Souls uses a manual counter.

## Start streaming

1. Open SoulsTracker, choose your game on the **Main** tab, and pick a save or character when prompted.
2. Configure the retained overlay appearance and optional local text export.
3. For hosted overlays, read the disclosure on the **Overlay** tab, explicitly consent, and import an operator-issued pairing JSON file.
4. Enable **Total Deaths** for visibility, then use **Copy OBS URL** and paste into an OBS **Browser Source**. Visibility is separate from publication consent.

This development build accepts pairing only for https://overlay.beingkairo.com. The service requires operator provisioning; deployment and OBS validation are not established by a source build. Local tracking and TXT output do not require pairing or an internet connection.

The Total Deaths overlay works at any size.

With an authorized hosted service, SoulsTracker publishes accepted death display and applied appearance while running. Closing it leaves the last published state hosted. Automatic readers do not overwrite that state until a current accepted observation arrives. An already open browser retains its last display during a disconnect; a cold offline reload cannot hydrate it.

Existing local OBS URLs need one deliberate replacement. Styles embedded in old URLs are not migrated: reproduce them in Desktop appearance settings before switching. The hosted URL has no style overrides. Fonts must be installed on the OBS machine; existing title-icon Apply and number-only font-size limitations remain.

Keep the pairing file and read-only OBS URL private. The original pairing file contains write authority and is not deleted by import. **Reconnect** explicitly starts a new publisher session; close another publisher first if a conflict is reported. **Import / Replace pairing** is the recovery entry for a new operator-issued bundle. **Remove pairing** stops publication and deletes only this PC's protected pairing, not cloud state or remote capabilities. Contact the operator for revocation or deletion.

Browse and Rescan help with save locations. Save-based counters update after the game saves.

## Make it yours

The **Overlay** tab controls fonts, colors, size, background, alignment, outlines, and shadows.

The **Settings** tab includes TXT output for OBS text sources, update checks, and global hotkeys for manual counters.

## Privacy and read-only use

SoulsTracker reads approved game data and save data, then leaves game files and game memory untouched. Local settings, save selection and counters remain on your PC.

Opt-in hosted publication sends the accepted display value/availability and applied appearance, including custom title and font name, to Cloudflare. It sends no game/save paths, character/slot names or raw observations. Cloudflare processes connection metadata and retains the latest published state. Successful protected pairing records consent across restarts. No localhost fallback is used.

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
