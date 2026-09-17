# Architecture

SoulsTracker is a .NET 10 WPF desktop application with a static TypeScript browser overlay.

- `src/SoulsTracker.Domain`: game catalog, state contracts, overlay configuration.
- `src/SoulsTracker.Application`: serialized state commands and transitions.
- `src/SoulsTracker.Infrastructure`: SQLite persistence, read-only Windows process access, bounded HTTPS publisher and dedicated protected pairing storage.
- `src/SoulsTracker.Overlay`: shared display snapshot projection, with no server or browser assets.
- `src/SoulsTracker.Desktop`: WPF user interface, hotkeys, and desktop lifecycle.
- `web_overlay`: OBS browser renderer and Playwright tests.
- `cloud_overlay`: Worker Static Assets and SQLite Durable Object authority with authenticated read-only WebSocket hydration.

Normal Desktop composition uses one hosted connection owner. Accepted runtime publications and committed state changes feed the existing projection; the sender has independent death/appearance slots and one HTTP operation at a time. Source identity, freshness, lower-value confirmation and effective totals stay local. Automatic startup holds death publication until accepted data exists; persisted manual totals may publish immediately.

Pairing is opt-in and validates the exact HTTPS origin https://overlay.beingkairo.com before whole-blob current-user DPAPI storage. All other origins are denied. Only explicit Copy constructs a read-only URL; no URL style overrides or local fallback are supported. Desktop does not host an HTTP or WebSocket listener.

Shutdown stops input and setup, cancels/awaits the reader, drains committed producers, then detaches and drains the hosted sender. TXT completes independently. The single-instance lease is released last; a shutdown deadline requests cancellation rather than abandoning live publication work. No synthetic closing state is sent.

The overlay is display-only. Game readers are optional and must remain read-only.
For save-file games, Infrastructure may also provide bounded local discovery: it
resolves only launcher metadata and approved save subtrees, returns safe labels
and canonical local paths, and never scans drives, writes saves, or chooses
between multiple candidates. The desktop layer owns selection and persistence.
