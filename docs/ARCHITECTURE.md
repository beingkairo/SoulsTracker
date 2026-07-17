# Architecture

SoulsTracker is a .NET 10 WPF desktop application with a static TypeScript browser overlay.

- `src/SoulsTracker.Domain`: game catalog, state contracts, overlay configuration.
- `src/SoulsTracker.Application`: serialized state commands and transitions.
- `src/SoulsTracker.Infrastructure`: SQLite persistence and read-only Windows process access.
- `src/SoulsTracker.Overlay`: loopback-only ASP.NET Core overlay service.
- `src/SoulsTracker.Desktop`: WPF user interface, hotkeys, and desktop lifecycle.
- `web_overlay`: OBS browser renderer and Playwright tests.

The overlay is display-only. Game readers are optional and must remain read-only.
