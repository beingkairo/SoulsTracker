# Release preparation

The v2.2.1 release source is pinned to `ea328f56543b790d24c4a771fb687f4cebbcd84c`, with source tree `4ecc9ed493fb436e8c1ca86f5117c078bfca8d31`. Its tested Hollow Knight fixes are in implementation commit `5b36fd839e9de545218aa1892f532da35098013f`. Release-control changes are committed separately and pin these immutable identities. Build inputs must match the pinned release source.

## Hollow Knight build input

Release packaging requires the verified API v78 ZIP. Its SHA-256 is `61d285874ec0a3a1aba5f4b29854917e54456fd6dc985f966c10215a5d416be4`. Verification also checks the approved profile, upstream source revision, exact 21-entry inventory and each runtime DLL hash. A stale generated build directory is not a release input.

The reproducible recipe is `eng/Build-HollowKnightApi12620.ps1`. It requires the hash-pinned clean Steam managed references, .NET SDK 10.0.301, the pinned upstream source archive and locked, signature-verified NuGet packages. It reads the references and writes generated build outputs. Keep proprietary references out of source control and public build artifacts.

CI retrieves the existing prebuilt ZIP and sanitized receipt from unpublished input draft `439458049`, pinned to archive asset `728414179` and receipt asset `728411439`. `scripts/get_hollow_knight_input.py` uses the Actions token through GitHub CLI, verifies repository ownership, unpublished state, exact asset metadata, downloaded hashes, receipt provenance, source recipe/locks and all 21 components. It requires a fresh runner input directory and rejects every discrepancy before building. Global permissions remain read-only; the input probe and preparation jobs require job-scoped Contents(write) for GitHub draft access. Checkout credentials are not persisted. `action=probe-input` tests the same binary retrieval without building, signing or uploading artifacts.

The release manifest and signed predicate record the consumed asset identities, hashes and complete sanitized build receipt. CI consumes the verified locally reproduced API package; it does not rebuild that package. A local checksum or build receipt does not itself constitute signed CI provenance. API source and dependency licenses do not replace the original terms covering game-owned assemblies.

## Local qualification

Use a verified package staged outside generated `obj` directories and a new absolute output directory. Existing output directories and paths inside historical artifact directories are rejected.

```powershell
./scripts/Build-Release.ps1 -LocalQualification -HollowKnightApiPackage <verified-api-zip> -OutputRoot <new-absolute-output-directory>
```

The command performs locked restores, browser and Worker checks, formatting, Release build, automated tests, distribution validation and installer packaging. It publishes the tested binaries without rebuilding them. An explicit `-InstallerCompiler` path can select an existing Inno Setup compiler. Local qualification may use the canonical publication-control checkout only after checking that all application build inputs still match the approved implementation.

The output contains `desktop` and `installer` directories. Create the portable archive and SPDX SBOM from that verified desktop payload only, then use `scripts/verify_publication.py prepare-local` to record the actual build checkout, approved application source, checksums and payload inventory. This manifest is marked `local_unsigned` and cannot pass publication verification. Preserve qualified bytes when recording their comparison hashes; a later checksum-only control commit is not a rebuild of the application.

## Signed preparation and publication

The manual Release workflow defaults to `prepare`. Once its input prerequisite is approved and provisioned, it checks out the exact application source separately from the control commit, runs complete qualification, creates the installer, portable archive and SBOM, and records every payload hash. GitHub-signed provenance covers the packages, notes, checksums and manifest. One immutable Actions artifact contains the prepared publication set.

Publication requires separate approval of that successful preparation run ID, immutable artifact ID and exact manifest SHA-256. It checks the same workflow commit, artifact ownership, release/tag absence, every checksum, portable inventory and signed provenance before publishing. It performs no build and cannot overwrite existing release assets. Different CI bytes require approval of their actual signed manifest; the local candidate cannot be substituted. Do not push a release tag separately or deploy hosted resources during this process.
