# Development runs

Source builds normally use the same local state folder as the installed app. To test a source build without reading or changing your normal SoulsTracker state, start it with a separate data root.

Close every SoulsTracker window first, then run this from the repository root:

```powershell
$developmentDataRoot = Join-Path $env:TEMP "SoulsTracker-development"
dotnet run --project .\src\SoulsTracker.Desktop\SoulsTracker.Desktop.csproj -- --data-root $developmentDataRoot
```

The override must be an absolute folder path and cannot be the normal SoulsTracker data folder. It creates a separate database and local settings. Legacy-settings import is disabled in this mode so a development run does not inspect normal legacy state either.

To reset the development instance, close it and delete the folder chosen for `$developmentDataRoot`. Do not use `--data-root` for normal use or release builds; without it, SoulsTracker keeps its normal installed-app behavior.
