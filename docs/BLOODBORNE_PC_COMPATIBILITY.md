# Bloodborne v2.1.2 — shared PC runtime tracking

Branch: `kairo/v2.1.2-bloodborne-compat`, isolated worktree `../SoulsTracker-v2.1.2-bloodborne`, based on released v2.1.1 commit `dc726c8a650083cd5e922a58528e794b0c11325b`. Updated 2026-11-07.

## Outcome and live evidence

Normal Desktop Bloodborne tracking now uses a shared PC game-layout reader alongside the existing shadPS4 compatibility path. No PC-port executable-name allowlist is used in Desktop discovery. The owner supplied these probe results and subsequently passed normal Desktop acceptance:

| Environment | Observed counter behavior | Evidence status |
| --- | --- | --- |
| shadPS4 v0.21.0, CUSA03173 / 1.09, PID 11336 | Existing v2.1.1 reader and fixed address `0x2080673B8` both changed 3 → 4 after a death; retained after character logout/login | Existing fixed-address tracking confirmed; preserved unchanged |
| Mrsuss60 bbport v0.9.0 | `deaths=0 true_deaths=0` → one death → `deaths=1 true_deaths=0`; value retained | Shared image discovery and `GameDataMan + 0x88` counter demonstrated; normal Desktop acceptance passed |
| Lantern Engine v0.9.38 | `deaths=0 true_deaths=0` → one death → `deaths=1 true_deaths=0`; subsequent read remained 1 / 0 | Same shared layout and counter demonstrated; normal Desktop acceptance passed |
| deadinside28 Windows bbport (owner did not specify release version) | Correct total and +1 increments, stable subsequent reads | Normal Desktop acceptance passed through the shared reader |

The distinct changes of `deaths` while `true_deaths` stayed zero identify the field to integrate. This does not infer equivalence from equal initial values. The earlier shadPS4 structural `UnmatchedImage` does not invalidate its independently verified fixed-address reader and is not a requirement for the PC ports.

The earlier attachment failure was Windows error 5 because the game was elevated and the probe was not. The diagnostic preserves that distinction; no process rights were expanded and no automatic elevation was introduced.

**Acceptance passed:** the owner tested Mrsuss60, Lantern and deadinside28 through the development Desktop build. All passed correct totals and +1 increments, character logout/login, game restarts and stable counts without duplicate deaths. Existing shadPS4 tracking was verified previously. No additional port investigation or adapters are needed. Release preparation is authorized; publication and deployment remain separately gated.

## Desktop integration

- [BloodborneRuntimeDeathReader.cs](../src/SoulsTracker.Infrastructure/BloodborneRuntimeDeathReader.cs) is the single registered Bloodborne reader in [App.xaml.cs](../src/SoulsTracker.Desktop/App.xaml.cs).
- For shadPS4, it delegates to the unchanged `BloodborneActiveCharacterDeathReader` using that poll's borrowed process snapshot. The exact process/title gate, absolute four-byte address, decoding, plausibility checks and read-only handle lifecycle remain intact. Two exact shadPS4 host candidates still fail closed.
- PC runtimes use the same image fingerprints and manager/character/loading checks already exercised by the owner probe. Desktop reads only the unsigned little-endian four-byte `deaths` field at manager + `0x88`. It does not read or use `true_deaths` as a tracking total.
- Output remains one `GameId.Bloodborne`, source identity `bloodborne`, and cumulative snapshots. No per-port UI choice, new setting, additive death-event calculation, or separate tracking algorithm was added.
- Existing selected-game polling, presentation, transient-unavailable handling, text/overlay publication and confirmation of lower totals are unchanged.

The standalone compatibility probe remains available for diagnostics; its explicit named runtime modes are not the normal Desktop discovery path.

## Bounded process discovery

[BloodborneWindowProcessEnumerator.cs](../src/SoulsTracker.Infrastructure/BloodborneWindowProcessEnumerator.cs) uses Windows top-level window metadata, not a whole-system process-memory sweep:

1. Visit at most **2,048 top-level windows** on the current desktop.
2. Keep visible, unowned windows whose bounded caption starts with the exact game-name token `Bloodborne` (case-insensitive, allowing the trademark or suffix). Exclude our own process, unrelated titles, truncated/overlong captions and hidden/owned windows.
3. Deduplicate window-owner PIDs, allowing at most **eight distinct candidates**. Reject exceeded budgets instead of truncating a potentially ambiguous discovery.
4. Revalidate each candidate's window handle, PID and title immediately before attachment. The executable filename, launcher location and host PE hash are not required.
5. Use only the existing `PROCESS_VM_READ | PROCESS_QUERY_INFORMATION` attachment. Inaccessible candidates or incomplete maps fail closed; there is no UAC prompt, debug-privilege manipulation or write/injection capability.
6. A title is only a discovery hint. Require all three known guest fingerprints, valid character state and stable reads before accepting a PC total.

The shadPS4 PID is not inspected twice through its game window. A title-matching launcher with an unmatched image is ignored. Multiple matching PC layouts, multiple matching images inside one process, or a matching PC runtime alongside active shadPS4 yield no fresh count. A recognized runtime with no valid loaded character still participates in ambiguity detection.

Unknown layouts are rejected. Each poll creates fresh attachments and re-resolves the image; no memory address or handle cache survives a restart or PID reuse. Native references: [EnumWindows](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows), [GetWindowText](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowtextw), [GetWindowThreadProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid).

## Verified layout and address conventions

The shared code lives in [BloodborneCompatibilityProbe.cs](../src/SoulsTracker.Infrastructure/BloodborneCompatibilityProbe.cs), with the optional region-query capability in [WindowsReadOnlyProcessAccess.cs](../src/SoulsTracker.Infrastructure/WindowsReadOnlyProcessAccess.cs).

Lantern v0.9.38 source [engine_state.cpp](https://github.com/Awakenchan/LanternEngine/blob/4715bca355059b7bd981ca849412884059d4d0a7/engine/gpu/shim/engine/engine_state.cpp#L11) establishes the image-relative manager slot `0x553B130`, local character record at manager + `0x08`, and level at record + `0x90`. The two cumulative fields were identified in [existing 1.09 source research](https://github.com/droogie/bbhost/blob/8018f3b1ea19d4e78a5fcbd5bfb3f2def2341a56/src/engine/player_data.cpp#L12); the owner's target-port tests then demonstrated that `+0x88` changes for a death while `+0x84` does not. No additional bbhost investigation or adapter was added.

The three exact instruction byte sequences come from [Lantern release cheats.cpp](https://github.com/Awakenchan/LanternEngine/blob/4715bca355059b7bd981ca849412884059d4d0a7/engine/gpu/shim/engine/cheats.cpp#L20). Its `Matches()` subtracts the preferred disassembler base `0x400000`:

| Signature | Source disassembler address | Image-relative offset | Bytes checked |
| --- | --- | --- | --- |
| Goods row accessor | `0x231E3D0` | `0x1F1E3D0` | 28 |
| Goods cap accessor | `0x231E7E0` | `0x1F1E7E0` | 48 |
| Inventory lookup | `0x18D9F60` | `0x14D9F60` | 24 |

The initial probe incorrectly used unreduced disassembler addresses; that conversion was corrected before the successful owner PC-port tests. Independent fixtures now use the actual RVAs, and a regression rejects bytes placed only at the old offsets. All three byte sequences were compared exactly with the source release.

Both [bbport v0.9.0's loader](https://github.com/Mrsuss60/bloodborne_pc_windows_port/blob/b3350bb4f5ef237e320c7bbf2bbb95092de14c33/src/probe.c#L3244) and [Lantern v0.9.38's loader](https://github.com/Awakenchan/LanternEngine/blob/4715bca355059b7bd981ca849412884059d4d0a7/engine/src/probe.c#L928) allocate a separate guest image, apply relocations and set segment protections. Their host executable module base is not the guest image base. The shared reader walks metadata below the source-described 40-bit guest limit, takes distinct committed private executable allocation bases, and checks only the bounded fingerprint bytes. It never searches whole heaps for small integers.

After identification, it requires aligned, readable manager/character pointers, positive character level and a clear loading-request byte at image offset `0x556286B` ([previously researched source](https://github.com/droogie/bbhost/blob/8018f3b1ea19d4e78a5fcbd5bfb3f2def2341a56/src/engine/loading.cpp#L15)). It rechecks roots, loading state and fingerprints after reading deaths. Partial reads, implausible counters, cancellation and changed state cannot become a confirmed zero.

## Limits

- This supports the demonstrated **Bloodborne 1.09 guest layout on 64-bit Windows**, not fundamentally different builds. The targets' preparation code validates clean loaded-image SHA-256 `071df19c8880086d97182dbc057bc8cb37badaca57d9112683836b24a0444c0a`. Our runtime fingerprints identify the frozen layout; they are not a cryptographic hash of the entire running game.
- A renamed compatible executable can work without any adapter change, but an eligible Bloodborne game window is still required. Headless runtimes, hidden/owned game windows and altered/localized captions that do not match this gate are not discovered.
- Image discovery is capped at **65,536 regions and 512 distinct executable private allocations per candidate**. Incomplete/oversized discovery fails closed. Out-of-range allocations or changes to required fingerprints are unsupported.
- No broad support claim is made for other regions, arbitrary patched binaries, co-op/respawn modifications or other forks. Source ancestry/launcher branding alone does not imply support.
- Matching process privileges are required. Run normally for a normal game process; an elevated game requires an elevated tracker. The app does not request elevation automatically.
- A lower total requires the existing two consistent sequential reads before publication. Closing an app or encountering ambiguity can retain the previously confirmed observation briefly under the existing unavailable policy; it is not a new synced read.

## Automated checks

**134 infrastructure tests passed**, including all original Bloodborne cases, new normal-reader tests for known and renamed filenames, verified deaths-only reads, unchanged fixed-address fallback, duplicate windows, unmatched launchers, multiple instances, loading/restarts/reused PIDs, zero/repeated/lower snapshots, torn reads, permission failures and bounded window discovery.

**72 Desktop tests passed**, covering runtime publication/status contracts, lower-value confirmation and effective death-total consumers. The native Windows tests exercise actual read-only memory queries and top-level window enumeration. These synthetic cases supplement the completed owner Desktop acceptance.

The accepted development build was a self-contained Windows x64 app using .NET 10.0.9. Release preparation sets the repository version to **2.1.2** with no prerelease suffix. The installer and portable distribution use the existing release build, dependency inventory, license, WebView2 and publication-integrity checks.

## Release scope and remaining verification

Bloodborne compatibility acceptance is complete. Before publication, review the prepared release notes and package integrity report. A brief smoke check of the final portable package (version, Bloodborne sync and settings persistence in an isolated data root) is sufficient; repeating the full port matrix is not required unless packaging changes affect runtime behavior.

The existing two-stage publication workflow remains in use: source-pinned preparation produces an immutable artifact set and signed provenance; publication requires a separately approved manifest hash. A local candidate is explicitly unsigned metadata and cannot satisfy that publication gate.

No Cloudflare/Hosted Overlay code or infrastructure, identities, URLs, production settings, released v2.1.1 artifacts or Hollow Knight work was changed. The primary worktree's existing Cloudflare test edit remains untouched.
