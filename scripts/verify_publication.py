"""Prepare or verify a source-pinned Desktop release; never publish or rebuild."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import zipfile

IMPLEMENTATION = "7e36436f7b06a1d7851fd51c9ed85480dc0755e2"
SOURCE = "8ad6a664abe56db65e8771bdd61d20f3bf31ba7d"
STEM = "SoulsTrackerV2.1.1"
APPROVED = {
    f"{STEM}.exe": "5076d9bcb8ea5b35a45b67aec7bd2e38b9a4db235c66e5f89625fc30477f371e",
    f"{STEM}-portable.zip": "a6abd0d199f67094ec331c38efd9a03cfe429d18f62d3e86a199d300665bcae4",
}
PAYLOAD = "494c0c7dc27d15cb05006a821374b1cb1085589b305bdaf40cc4c3e2a1d87d33"
NAMES = [*APPROVED, f"{STEM}.sbom.spdx.json"]


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def sha(path):
    return digest(path.read_bytes())


def inventory(folder):
    return [{"path": p.relative_to(folder).as_posix(), "bytes": p.stat().st_size, "sha256": sha(p)}
            for p in sorted(folder.rglob("*")) if p.is_file()]


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
            require(not name.startswith("/") and ".." not in Path(name).parts, "Unsafe portable path")
            data = archive.read(entry)
            require(len(data) == expected[name]["bytes"] and digest(data) == expected[name]["sha256"],
                    "Portable payload hash differs")


def prepare(folder, payload, source, workflow_commit=None, run_id=None):
    head = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    require(head == SOURCE, "Build source is not the approved commit")
    subprocess.run(["git", "-C", str(source), "diff", "--exit-code", "HEAD"], check=True, capture_output=True)
    require(not (folder / "release-manifest.json").exists(), "Refusing to replace a prepared manifest")
    check_sbom(folder / NAMES[2])
    files = inventory(payload)
    require(len(files) == 491, "Desktop distribution file count changed")
    check_zip(folder / NAMES[1], files)
    shutil.copyfile(source / "docs/releases/v2.1.1.md", folder / "release-notes.md")
    hashes = {name: sha(folder / name) for name in NAMES}
    (folder / "SHA256SUMS.txt").write_text("".join(f"{hashes[n]}  {n}\n" for n in NAMES), encoding="ascii")
    hashes.update({name: sha(folder / name) for name in ["SHA256SUMS.txt", "release-notes.md"]})
    comparison = {name: hashes[name] == expected for name, expected in APPROVED.items()}
    comparison["payload_inventory"] = inventory_digest(files) == PAYLOAD
    manifest = {
        "schema_version": 1, "version": "2.1.1", "source_commit": SOURCE,
        "implementation_commit": IMPLEMENTATION,
        "source_tree": subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD^{tree}"], text=True).strip(),
        "preparation_mode": "github_actions" if run_id else "local_unsigned",
        "workflow_commit": workflow_commit, "prepared_run_id": str(run_id) if run_id else None,
        "artifacts": hashes, "payload_files": files, "payload_inventory_sha256": inventory_digest(files),
        "matches_approved_local_candidate": comparison,
        "different_artifacts_require_explicit_manifest_hash_approval": not all(comparison.values()),
    }
    (folder / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf8")
    return inspect(folder)


def inspect(folder):
    manifest = json.loads((folder / "release-manifest.json").read_text(encoding="utf8"))
    require(manifest["source_commit"] == SOURCE and manifest["version"] == "2.1.1", "Unapproved release source")
    require(manifest.get("implementation_commit") == IMPLEMENTATION, "Unapproved application implementation")
    expected_names = set(NAMES + ["SHA256SUMS.txt", "release-notes.md"])
    require(set(manifest["artifacts"]) == expected_names, "Unexpected release files")
    require(all(p.is_file() for p in folder.iterdir()), "Unexpected directory in publication set")
    actual_names = {p.name for p in folder.iterdir() if p.is_file()}
    require(actual_names in [expected_names | {"release-manifest.json"},
                             expected_names | {"release-manifest.json", "provenance.sigstore.json"}],
            "Unexpected files in publication directory")
    for name, expected in manifest["artifacts"].items():
        require(sha(folder / name) == expected, "Release artifact checksum differs")
    sums = dict((name, value) for value, name in
                (line.split("  ", 1) for line in (folder / "SHA256SUMS.txt").read_text().splitlines()))
    require(sums == {name: manifest["artifacts"][name] for name in NAMES}, "Checksum manifest differs")
    check_sbom(folder / NAMES[2])
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
    parser.add_argument("mode", choices=["prepare", "inspect", "verify"])
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--payload", type=Path)
    parser.add_argument("--source", type=Path)
    parser.add_argument("--workflow-commit")
    parser.add_argument("--run-id")
    parser.add_argument("--approved-manifest")
    args = parser.parse_args()
    if args.mode == "prepare":
        value = prepare(args.directory, args.payload, args.source, args.workflow_commit, args.run_id)
    elif args.mode == "verify":
        value = verify(args.directory, args.approved_manifest, args.workflow_commit, args.run_id)
    else:
        value = inspect(args.directory)
    print(json.dumps({"verified": True, "source_commit": value["source_commit"],
                      "release_manifest_sha256": sha(args.directory / "release-manifest.json"),
                      "matches_approved_local_candidate": value["matches_approved_local_candidate"]}))
