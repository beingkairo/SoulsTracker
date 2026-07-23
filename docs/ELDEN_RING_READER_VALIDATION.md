# Elden Ring save reader validation protocol

This is an engineering validation plan, not a user setup guide. Elden Ring Total
Deaths is read from a user-selected `ER0000.sl2` save file and selected character
slot. There is no Elden Ring process or memory reader in the normal runtime path.

## Reader boundary

The production reader uses only the selected local save file. It never opens,
queries, injects into, or changes the Elden Ring process. It does not write to the
save file. A missing, locked, incomplete, malformed, or unsupported save fails
closed without surfacing a guessed value.

The character picker may read the save's profile-summary metadata for a local name
and level label. Each field is independently validated. Invalid metadata falls back
to a generic character label and is never shown as a guessed value.

## Compatibility policy

The save parser must not lock itself to a game executable version. It validates the
bounded BND4 save-file structure and selected profile entry instead. If a game
update changes that format, the reader reports unavailable until the new layout is
reviewed and tested.

## Live-character validation

The user performs all in-game actions. SoulsTracker never sends input or changes
game state.

1. Select a copy of the local `ER0000.sl2` file and the profile index that matches
   the loaded character. Record the in-game Total Deaths baseline.
2. Confirm the reader shows the same baseline after the game saves.
3. Cause exactly one normal in-game death and wait for the game to finish saving.
4. Confirm the reader changes by exactly one and remains stable while the character
   is idle.
5. Switch to a second existing profile with a different known baseline. Confirm it
   reports that profile's value rather than sharing the first profile's total.
6. Restart SoulsTracker, select the same file and profile, and confirm the value is
   reproduced after the next save.
7. Temporarily make the selected file unavailable or locked. Confirm the app shows
   no guessed value and recovers after the file is readable again.

## Acceptance criteria

- Before a death, the reader equals the selected profile's in-game Total Deaths
  baseline exactly.
- One normal death produces exactly one increment after the game saves.
- No change occurs while the character is idle.
- A different selected profile reports that character's own total.
- Missing, locked, partial, malformed, or unsupported data fails closed as
  unavailable or waiting for a character.
- The reader reads only the selected save file and never persists, writes, injects,
  modifies saves, or automates input.

## No-data-change guarantee

This validation must not modify Elden Ring process memory, save files, account data,
or gameplay. It must not create an in-game rollback requirement because it makes no
in-game changes. The only local data SoulsTracker retains is its ordinary user
settings, including the selected save path and profile slot.
