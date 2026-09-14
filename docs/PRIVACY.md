# Privacy

SoulsTracker is a local Windows application.

- Settings, counters, and optional local text exports remain on the user's machine.
- OBS overlays are served only on the loopback address (`127.0.0.1`).
- The application has no account system, telemetry, analytics, cloud synchronization, or remote overlay hosting.
- The optional update check contacts GitHub's public latest-release endpoint only when you press **Check for updates**. It sends no account, token, analytics, machine identifier, or save data, and it never downloads or installs anything automatically.
- A local overlay URL contains a token. Treat it like private configuration and do not share it publicly.
