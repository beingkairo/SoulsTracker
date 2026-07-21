# Elden Ring reader validation protocol

This is an engineering validation plan, not a user setup guide. Elden Ring remains
disabled in SoulsTracker until every required observation has been independently
confirmed.

## Recorded executable identity

The only local discovery evidence recorded so far is the executable identity below.
It identifies one binary and does **not** establish a death-counter address or reader
binding. The version and hash are recorded discovery evidence only; they must not
become the sole runtime compatibility lock.

| Field | Value |
| --- | --- |
| Executable name | `eldenring.exe` |
| File version | `2.6.2.0` |
| Product version | `2.6.2.0` |
| SHA-256 | `34102B1C08BB5F769A724427A6F70FE29B3B732C31CF73693F861C48D3492DDB` |

## Current blocker

There is no verified, reproducible, read-only mapping from this executable identity
to Elden Ring's active-character death total. Do not infer one from a process name,
file version, public offset list, save file, or a single observed value. The app has
no Elden Ring memory reader registered and makes no Elden Ring process attachment
from its normal runtime path.

## Compatibility policy for later implementation

Elden Ring receives unrelated patches, so a future reader must not require this
exact version or hash just to start. The recommended conservative strategy is:

1. Require the exact `eldenring.exe` executable name and the exact Windows product
   name `ELDEN RING™` before any reader-level work begins.
2. Use a bounded, read-only compatibility probe derived from independently
   reproduced live-character evidence. It may use a narrowly scoped, allowlisted
   signature and pointer path only when each read boundary, expected byte shape,
   pointer validity, and maximum work are specified in code.
3. Validate the candidate total with multiple independent invariants: complete
   reads, valid pointer chain, non-negative bounded integer, stable value while
   idle, character-specific value, and exactly one increase for a verified normal
   death.
4. Treat every failed identity check, signature/probe check, partial read, or
   invariant as `Unavailable` or `Waiting for active character`. Never surface a
   plausible-looking fallback value.

This is not a user calibration workflow. There must be no wizard, manual address,
user-entered baseline, or option that makes an unverified update look supported.
The current code has no signature scan, pointer traversal, memory read, or reader
registration; a separate reviewed change must introduce those only after live
evidence exists.

The tradeoff is deliberate: harmless updates may continue working when the verified
path is unchanged, but a changed path fails unavailable until it is reviewed. This
is safer than guessing from an old offset and less maintenance-heavy than locking
every patch to one hash.

## Later live-character validation

Run this only after a candidate binding has been proposed in a separate code review.
The user performs all in-game actions; SoulsTracker never sends input or changes
game state.

1. Record the character's current in-game Total Deaths value and keep a screenshot
   or written baseline. Do not use a newly created character for the only test.
2. Start Elden Ring normally, load that character, and leave it stable for at least
   30 seconds. Record whether the candidate reader is `Unavailable`, `Waiting for
   active character`, or `Synced`, and if synced record its displayed value.
3. Without changing SoulsTracker settings, cause exactly one normal in-game death.
   After the character has reloaded, record the in-game Total Deaths and the reader
   value.
4. Leave the character stable for another 30 seconds. The reader must retain the
   new value without additional increments, negative values, or implausible jumps.
5. Close Elden Ring and confirm SoulsTracker changes to `Unavailable` and retains
   no runtime death value as saved progress.
6. Repeat steps 1 through 5 with a second existing character whose baseline Total
   Deaths differs from the first. A candidate that cannot distinguish the two
   characters is rejected.
7. Repeat the one-death check after restarting both Elden Ring and SoulsTracker.
   The same executable identity and character value behavior must reproduce.

## Acceptance criteria

- Before a death, the reader equals the in-game Total Deaths baseline exactly.
- One normal death produces exactly one increment after reload.
- No change occurs while the character is idle.
- A different character reports that character's own total, not a shared value.
- Any identity mismatch, partial read, out-of-range value, or missing active
  character fails closed as `Unavailable` or `Waiting for active character`.
- The reader uses only the existing read/query process access surface and never
  persists, writes, injects, modifies saves, or automates input.

## No-data-change guarantee

This validation must not modify Elden Ring process memory, save files, account data,
or gameplay. It must not create an in-game rollback requirement because it makes no
in-game changes. The only local data SoulsTracker may retain is its ordinary user
settings; the observed game death value remains runtime-only until an explicitly
approved product decision says otherwise.
