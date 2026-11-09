"""Prepare or verify a source-pinned Desktop release; never publish or rebuild."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import zipfile

import verify_hollow_knight_input as hollow_knight

IMPLEMENTATION = "695e535f551e04a11ff84f3aef5190098b574a64"
SOURCE = IMPLEMENTATION
SOURCE_TREE = "d43e25ebfe46c9fe5d84d4b6966acab23a5f0f24"
VERSION = "2.2.0"
STEM = f"SoulsTrackerV{VERSION}"
PAYLOAD_COUNT = 495
# Qualified local hashes are recorded after the first complete local build.
# An absent comparison never counts as a match or authorizes publication.
APPROVED = {f"{STEM}.exe": None, f"{STEM}-portable.zip": None}
PAYLOAD = None
NAMES = [*APPROVED, f"{STEM}.sbom.spdx.json"]
NOTES = Path(__file__).resolve().parent.parent / f"docs/releases/v{VERSION}.md"
PROTECTED_INPUTS = ["src", "plugins", "assets", "eng", "web_overlay", "cloud_overlay", "installer",
                    "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json",
                    "SoulsTracker.sln", "NuGet.config", "nuget.config", "LICENSE", "README.md",
                    "THIRD_PARTY_NOTICES.md", "docs/THIRD_PARTY_NOTICES.md", "docs/licenses",
                    "docs/WEBVIEW2_RUNTIME_LICENSE.txt"]


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def sha(path):
    return digest(path.read_bytes())


def git(source, *arguments):
    return subprocess.check_output(["git", "-C", str(source), *arguments], text=True).strip()


def verify_source(source, local=False):
    head = git(source, "rev-parse", "HEAD")
    if local:
        subprocess.run(["git", "-C", str(source), "merge-base", "--is-ancestor", SOURCE, head], check=True)
        for left, right in [(SOURCE, "HEAD"), ("HEAD", None)]:
            arguments = ["git", "-C", str(source), "diff", "--exit-code", left]
            if right:
                arguments.append(right)
            subprocess.run([*arguments, "--", *PROTECTED_INPUTS], check=True, capture_output=True)
    else:
        require(head == SOURCE, "Build source is not the approved commit")
        subprocess.run(["git", "-C", str(source), "diff", "--exit-code", "HEAD"], check=True, capture_output=True)
    require(not git(source, "ls-files", "--others", "--exclude-standard", "--", *PROTECTED_INPUTS),
            "Untracked application build inputs are present")
    return {"source_commit": head, "source_tree": git(source, "rev-parse", "HEAD^{tree}"),
            "approved_source_commit": SOURCE, "implementation_commit": IMPLEMENTATION}


def inventory(folder):
    hollow_knight.reject_links(folder)
    paths = sorted(folder.rglob("*"))
    for path in paths:
        hollow_knight.reject_links(path)
    return [{"path": p.relative_to(folder).as_posix(), "bytes": p.stat().st_size, "sha256": sha(p)}
            for p in paths if p.is_file()]


def inventory_digest(files):
    return digest("".join(f"{x['path']}\t{x['bytes']}\t{x['sha256']}\n"
                          for x in sorted(files, key=lambda x: x["path"])).encode())


def check_sbom(path):
    text = path.read_text(encoding="utf-8-sig")
    require(not re.search(r"(?i)onedrive|agents[/\\]|(?<![a-z])[a-z]:[/\\]|/home/runner|file://", text),
            "SBOM contains an absolute or private build path")
    value = json.loads(text)
    require(value.get("spdxVersion") == "SPDX-2.3" and value.get("packages"), "Invalid SPDX SBOM")
    identifiers = [x["SPDXID"] for x in value["packages"] + value.get("files", [])]
    require(len(identifiers) == len(set(identifiers)), "Duplicate SPDX identifiers")
    return value


def check_zip(path, files):
    expected = {x["path"]: x for x in files}
    require(len(expected) == len(files), "Duplicate payload path")
    with zipfile.ZipFile(path) as archive:
        entries = [x for x in archive.infolist() if not x.is_dir()]
        names = [x.filename.replace("\\", "/") for x in entries]
        require(len(names) == len(set(names)) and set(names) == set(expected), "Portable inventory differs")
        for entry, name in zip(entries, names):
            require(not name.startswith("/") and ":" not in name and ".." not in Path(name).parts,
                    "Unsafe portable path")
            data = archive.read(entry)
            require(len(data) == expected[name]["bytes"] and digest(data) == expected[name]["sha256"],
                    "Portable payload hash differs")


def prepare(folder, payload, source, workflow_commit=None, run_id=None, local=False):
    identity = verify_source(source, local)
    subprocess.run(["git", "-C", str(source), "diff", "--exit-code", "HEAD"], check=True, capture_output=True)
    if not local:
        require(re.fullmatch("[a-f0-9]{40}", workflow_commit or ""), "Workflow commit required")
        require(re.fullmatch("[1-9][0-9]*", str(run_id or "")), "Prepared run ID required")
    else:
        require(not workflow_commit and not run_id, "Local metadata cannot claim a workflow run")
    hollow_knight.reject_links(folder)
    require(not (folder / "release-manifest.json").exists(), "Refusing to replace a prepared manifest")
    check_sbom(folder / NAMES[2])
    files = inventory(payload)
    require(len(files) == PAYLOAD_COUNT, "Desktop distribution file count changed")
    api = hollow_knight.verify(payload / "HollowKnight/api-12620.zip",
                              source / "src/SoulsTracker.Infrastructure/HollowKnightApi12620.json")
    check_zip(folder / NAMES[1], files)
    shutil.copyfile(NOTES, folder / "release-notes.md")
    hashes = {name: sha(folder / name) for name in NAMES}
    (folder / "SHA256SUMS.txt").write_text("".join(f"{hashes[n]}  {n}\n" for n in NAMES), encoding="ascii")
    hashes.update({name: sha(folder / name) for name in ["SHA256SUMS.txt", "release-notes.md"]})
    comparison = {name: expected is not None and hashes[name] == expected for name, expected in APPROVED.items()}
    comparison["payload_inventory"] = PAYLOAD is not None and inventory_digest(files) == PAYLOAD
    manifest = {
        "schema_version": 2, "version": VERSION, **identity,
        "preparation_mode": "local_unsigned" if local else "github_actions",
        "workflow_commit": workflow_commit, "prepared_run_id": str(run_id) if run_id else None,
        "api78_input": api, "release_notes_sha256": sha(NOTES),
        "artifacts": hashes, "payload_files": files, "payload_inventory_sha256": inventory_digest(files),
        "matches_approved_local_candidate": comparison,
        "different_artifacts_require_explicit_manifest_hash_approval": not all(comparison.values()),
    }
    (folder / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf8")
    return inspect(folder)


def inspect(folder):
    hollow_knight.reject_links(folder)
    manifest = json.loads((folder / "release-manifest.json").read_text(encoding="utf8"))
    require(manifest.get("schema_version") == 2 and manifest["version"] == VERSION, "Unapproved release version")
    require(manifest.get("implementation_commit") == IMPLEMENTATION, "Unapproved application implementation")
    require(manifest.get("approved_source_commit") == SOURCE, "Unapproved release source")
    require(manifest.get("preparation_mode") in ["local_unsigned", "github_actions"], "Unsupported preparation mode")
    if manifest["preparation_mode"] == "github_actions":
        require(manifest["source_commit"] == SOURCE and manifest["source_tree"] == SOURCE_TREE,
                "Unapproved release source")
    else:
        require(re.fullmatch("[a-f0-9]{40}", manifest.get("source_commit", "")) and
                re.fullmatch("[a-f0-9]{40}", manifest.get("source_tree", "")), "Invalid local checkout identity")
        require(manifest.get("workflow_commit") is None and manifest.get("prepared_run_id") is None,
                "Local metadata cannot claim a workflow run")
    require(manifest.get("api78_input") == {
        "package_sha256": hollow_knight.PACKAGE_SHA256, "profile_sha256": hollow_knight.PROFILE_SHA256,
        "source_revision": hollow_knight.SOURCE_REVISION, "runtime_files": hollow_knight.RUNTIME_FILE_COUNT},
        "API input provenance differs")
    expected_names = set(NAMES + ["SHA256SUMS.txt", "release-notes.md"])
    require(set(manifest["artifacts"]) == expected_names, "Unexpected release files")
    require(all(p.is_file() and not p.is_symlink() for p in folder.iterdir()), "Unexpected directory in publication set")
    actual_names = {p.name for p in folder.iterdir() if p.is_file()}
    require(actual_names in [expected_names | {"release-manifest.json"},
                             expected_names | {"release-manifest.json", "provenance.sigstore.json"}],
            "Unexpected files in publication directory")
    for name, expected in manifest["artifacts"].items():
        require(sha(folder / name) == expected, "Release artifact checksum differs")
    require(manifest.get("release_notes_sha256") == sha(folder / "release-notes.md") == sha(NOTES),
            "Release notes differ from the publication controls")
    sums = dict((name, value) for value, name in
                (line.split("  ", 1) for line in (folder / "SHA256SUMS.txt").read_text().splitlines()))
    require(sums == {name: manifest["artifacts"][name] for name in NAMES}, "Checksum manifest differs")
    check_sbom(folder / NAMES[2])
    require(len(manifest["payload_files"]) == PAYLOAD_COUNT, "Desktop distribution file count changed")
    require(inventory_digest(manifest["payload_files"]) == manifest["payload_inventory_sha256"], "Payload manifest differs")
    check_zip(folder / NAMES[1], manifest["payload_files"])
    return manifest


def verify(folder, expected_manifest, workflow_commit, run_id):
    require(re.fullmatch("[a-f0-9]{64}", expected_manifest or ""), "Explicit manifest hash approval required")
    require(sha(folder / "release-manifest.json") == expected_manifest, "Approved manifest hash differs")
    manifest = inspect(folder)
    require(manifest["preparation_mode"] == "github_actions", "Local metadata is not signed CI provenance")
    require(re.fullmatch("[a-f0-9]{40}", workflow_commit or ""), "Workflow commit required")
    require(manifest["workflow_commit"] == workflow_commit, "Preparation workflow commit differs")
    require(re.fullmatch("[1-9][0-9]*", str(run_id or "")), "Prepared run ID required")
    require(manifest["prepared_run_id"] == str(run_id), "Preparation run differs")
    require((folder / "provenance.sigstore.json").is_file(), "Signed attestation bundle missing")
    return manifest  # The workflow additionally performs cryptographic gh attestation verify.


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["source", "source-local", "prepare", "prepare-local", "inspect", "verify"])
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--payload", type=Path)
    parser.add_argument("--source", type=Path)
    parser.add_argument("--workflow-commit")
    parser.add_argument("--run-id")
    parser.add_argument("--approved-manifest")
    args = parser.parse_args()
    if args.mode in ["source", "source-local"]:
        require(args.source is not None, "Source checkout required")
        print(json.dumps(verify_source(args.source, args.mode == "source-local")))
    else:
        require(args.directory is not None, "Publication directory required")
        if args.mode in ["prepare", "prepare-local"]:
            require(args.payload is not None and args.source is not None, "Source and payload required")
            value = prepare(args.directory, args.payload, args.source, args.workflow_commit, args.run_id,
                            args.mode == "prepare-local")
        elif args.mode == "verify":
            value = verify(args.directory, args.approved_manifest, args.workflow_commit, args.run_id)
        else:
            value = inspect(args.directory)
        print(json.dumps({"verified": True, "source_commit": value["source_commit"],
                          "release_manifest_sha256": sha(args.directory / "release-manifest.json"),
                          "matches_approved_local_candidate": value["matches_approved_local_candidate"]}))
