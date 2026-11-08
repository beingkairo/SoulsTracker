# Hollow Knight tracking

Select Hollow Knight in SoulsTracker and run the original game normally. Tracking uses read-only process memory. No game changes, mod loader, additional downloads or player-installed libraries are required.

The displayed count is deaths observed while Hollow Knight is selected and tracking is active. SoulsTracker saves this count locally across application and game restarts. It accumulates across Hollow Knight saves and modes within this SoulsTracker data folder. It starts at zero when no observed deaths have been saved. It is not a character's lifetime total. Deaths while the app is closed, another game is selected, or tracking is unavailable cannot be recovered.

## Compatibility

The reader recognizes the exact original executable names `hollow_knight` and `Hollow Knight` on Windows. It requires one matching process and a recognized 64-bit memory layout. Initial profiles use the runtime version strings `1.5.68.11808`, `1.5.78.11833`, and `1.5.12459`. These profiles are supported by source inspection and synthetic memory tests; live gameplay validation is still required before release. Storefront compatibility has not been independently established. Older 32-bit builds, unknown versions, changed layouts, ambiguous processes and unreadable memory leave tracking inactive.

## Counting behavior

The detector requires two consecutive healthy gameplay samples, followed by a coherent zero-health sample from the same loaded objects. Repeated zero-health readings count once. Loading, menus, changed objects, interrupted reads and sampling gaps break continuity. Attaching during a death or reopening a dead Steel Soul save does not count that earlier death. Respawn must establish a fresh healthy baseline before another death can count.

The reader samples at approximately 100 ms while selected. Unchanged output updates retain the usual two-second cadence. An event is committed locally before it can change the displayed or hosted count. Ordinary damage and nonlethal hazards do not count. Zero-health dream and Godhome defeats are included when observed in validated gameplay, even when the ordinary death flag remains false. Steel Soul terminal deaths can count before leaving gameplay without requiring a respawn.

Transitions that reach zero only outside gameplay, very short deaths between samples, an unobserved reload that reuses the same objects, or an interrupted terminal death may not be classified reliably. The detector avoids guessing from save timestamps, shade state or mode changes. Ordinary deaths, dream encounters, Godhome, White Palace, Radiance, lethal hazards and Steel Soul still need live validation of their exact health/state timing. A storage failure leaves the event pending for retry while this app instance remains active; an unsaved event cannot survive an app failure.

The hosted overlay and text export show the same committed observed count, including while the game is offline. The desktop status reports whether tracking is active. Existing overlay URLs and authentication remain unchanged.

## Technical references

The [LiveSplit reader](https://github.com/ShootMe/LiveSplit.HollowKnight/blob/30084da5385c58e698c4b067fe59e10acd380484/HollowKnightMemory.cs) provides the version-specific layout and singleton discovery facts; its [death split](https://github.com/ShootMe/LiveSplit.HollowKnight/blob/30084da5385c58e698c4b067fe59e10acd380484/HollowKnightComponent.cs#L1073) uses zero health. The [independent counter](https://github.com/Theuzin69/ds-death-counter/blob/430f8965c890a81e131570373425ebbc2d426d28/src/games/hollowknight.cpp) and its [technical account](https://github.com/Theuzin69/ds-death-counter/blob/430f8965c890a81e131570373425ebbc2d426d28/docs/how-it-was-made.md#hollow-knight) informed continuity and special-mode review. Both references are MIT licensed. SoulsTracker implements only bounded discovery, validated field reads and observed-event handling; no upstream runtime or generic memory library is bundled.
