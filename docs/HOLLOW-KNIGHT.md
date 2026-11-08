# Hollow Knight persistent tracking

Soulstracker.DeathTracker.dll records deaths inside the original Hollow Knight through the official [Hollow Knight Modding API](https://github.com/hk-modding/api). Counting continues while SoulsTracker is closed. The desktop reads saved records without independently detecting deaths.

## Installation and compatibility

Select Hollow Knight. Steam installation and library folders are searched automatically. **Change Game Folder** is always available. Close Hollow Knight, click **Install Death Tracking**, review the target folder and file changes, then confirm. Launch Hollow Knight and load a save. Tracking active requires a fresh plugin heartbeat matched to that process; DLL presence alone is insufficient.

This version targets Windows game **1.5.78.11833** with official API **v77**, [release 1.5.78.11833-77](https://github.com/hk-modding/api/releases/tag/1.5.78.11833-77). Newer builds, including **1.5.12620**, and older builds are unsupported. The installer never installs an older API over an unsupported game. Compatible non-Steam folders can be selected manually; distribution-specific gameplay validation is pending.

The installer downloads the official API over HTTPS, checks the pinned archive checksum and runtime files, and reuses an exact compatible existing API. Unknown APIs, conflicting shared components, modified plugin files and linked folders are rejected. Other mods remain untouched. Originals and a recovery journal are retained in `.soulstracker-death-tracking` inside the selected game folder. Failed installations roll back. After a process interruption, close Hollow Knight and use **Roll Back Failed Install** before retrying.

## Counts and persistence

The plugin hooks `HeroController.Die`. A latch prevents repeated calls during one death from incrementing twice; healthy respawn rearms it. Nonlethal hazards use `DieFromHazard` and do not count. Lethal hazards that enter `Die` count. Dream and Godhome defeats entering `Die` count even when the normal death/save path is skipped. Steel Soul terminal deaths are written before invoking the game coroutine, without requiring respawn.

Every save run has a random identity in the API's per-save mod settings. A new game reusing a slot gets a new identity. Copies of the same save retain its identity. Per-run records remain separate, including the final Steel Soul total. Missing known identities or existing records fail closed rather than silently resetting totals.

Records live in `%LOCALAPPDATA%/SoulsTracker/HollowKnightRecords/<installation-id>`. Each increment is flushed to a checksummed per-run record with atomic replacement, then to the current-record projection. Interrupted or corrupt projections are unavailable until the plugin reconciles them. Temporary files never supply totals. Back up these records together with the game's `.modded.json` save sidecars. Lost mod settings, corrupt records, failed storage and termination before a completed write can interrupt tracking; errors never become a fabricated zero.

Counts start when the plugin first tracks that save. Earlier deaths, disabled-plugin periods and failed writes cannot be recovered. This is a persistent plugin total, not a verified vanilla lifetime statistic. Desktop, TXT and hosted overlay share the same validated total. While the game is closed, the last tracked save remains readable; while running without a valid active-save heartbeat, tracking is unavailable.

## Updates and uninstall

Close Hollow Knight and use **Install Death Tracking** to update the owned plugin. Extra or modified files prevent replacement. **Uninstall Death Tracking** removes only the verified plugin DLL, installation identity and ownership record. Shared API files, other mods, mod settings, saved totals and installer backups remain. Reinstalling with the same saved desktop configuration reuses the installation identity. Remove the shared API separately only after checking other mods no longer require it.

## Manual validation before release

Build and fixture tests do not establish live gameplay compatibility. On a supported installation:

- Cancel installation confirmation and verify no game files change. Install, restart, load a save and confirm active status.
- With SoulsTracker closed, die twice, restart both programs and verify two saved deaths in desktop, TXT and hosted overlay.
- Verify nonlethal spikes, acid and falls do not count; lethal hazards count once. Repeat death/respawn and rapid deaths.
- Check Dream Warrior, Dream Boss, White Palace, Radiance, Hall of Gods and Pantheon defeats, including Steel Soul variants. Confirm which scenes enter `Die`.
- Die in Steel Soul and restart at title. Verify the final total survives. Create a new game in that slot and verify a separate total.
- Switch all four slots, reload and copy saves, and attach the desktop during a death. Verify unrelated runs never merge and no duplicate occurs.
- Disable the plugin and relaunch. Confirm unavailable status while running. Re-enable and verify the old total resumes without recovering disabled-period deaths.
- With backups, test read-only storage, interrupted writes and corruption. Verify unavailable status and recovery without false zero.
- Install beside another compatible mod, update and uninstall. Verify the other mod, shared API, backups and totals remain unchanged.

The API is MIT licensed. See [third-party notices](../THIRD_PARTY_NOTICES.md) and the [API license](licenses/HOLLOW-KNIGHT-MODDING-API.txt).
