# Privacy

SoulsTracker is a local Windows application.

- Settings, counters, and optional local text exports remain on the user's machine.
- Hosted overlays require explicit consent and an operator-issued pairing file. Successful pairing records that opt-in across restarts in a dedicated Windows current-user DPAPI-protected file, separate from the tracker database and exports.
- Publication sends the accepted death display value/availability and applied appearance (including custom title and font name) to Cloudflare. It does not send game/save paths, source identities, character/slot names, process details, raw observations or adjustment metadata. Cloudflare does not calculate totals or synchronize cloud totals back into local counters.
- Cloudflare retains the last published display until operator deletion and processes operational connection metadata such as IP addresses and timing. Application telemetry, analytics and accounts are not added. Provider recovery/backups may outlive application deletion.
- Hosted pairing accepts only https://overlay.beingkairo.com and requires operator provisioning. Local tracking and TXT remain usable without hosted setup. There is no local overlay fallback in the composed hosted path.
- The optional update check contacts GitHub's public latest-release endpoint when you press **Check for updates**, or once when the app opens if you enable **Check for updates on startup**. Startup checks are off by default; changing the setting applies to future launches. The check sends no account, token, analytics, machine identifier, or save data, and never downloads or installs anything automatically. A verified newer version can show a dismissible notice; its **View update** button opens the SoulsTracker website only when you choose it.
- Copy OBS URL includes only read authority in a fragment, never write authority. Anyone possessing that URL can read the overlay. Keep OBS scene collections and URLs private. The connection panel displays only host and sanitized status.
- The imported JSON contains sensitive read and write capabilities. Protect the source file: import does not delete it. Write authority is used only for authenticated HTTPS publication, never in the OBS URL, browser assets, ordinary settings, database or TXT output.
- Remove pairing stops publication and removes only the dedicated protected file on this PC. It does not revoke capabilities or delete hosted state; request those actions from the operator. Replacement pairing requires explicit import.
