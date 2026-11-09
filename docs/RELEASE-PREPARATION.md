# Release preparation

The v2.2.0 application source is pinned to `695e535f551e04a11ff84f3aef5190098b574a64`. Publication controls are reviewed separately. Gameplay code, plugin sources, assets, runtime versions and dependency locks must remain identical to that application source.

## Hollow Knight build input

Release packaging requires the verified API v78 ZIP. Its SHA-256 is `61d285874ec0a3a1aba5f4b29854917e54456fd6dc985f966c10215a5d416be4`. Verification also checks the approved profile, upstream source revision, exact 21-entry inventory and each runtime DLL hash. A stale generated build directory is not a release input.

The reproducible recipe is `eng/Build-HollowKnightApi12620.ps1`. It requires the hash-pinned clean Steam managed references, .NET SDK 10.0.301, the pinned upstream source archive and locked, signature-verified NuGet packages. It reads the references and writes generated build outputs. Keep proprietary references out of source control and public build artifacts.

Authenticated CI provisioning is not configured. Preparation intentionally stops before building or signing until an approved input source and access verification are implemented. A local checksum or build receipt does not constitute signed CI provenance. API source and dependency licenses do not replace the original terms covering game-owned assemblies.

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
