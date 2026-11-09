"""Verify the pinned source-built Hollow Knight API package without installing it."""
import argparse
import hashlib
import json
from pathlib import Path
import stat
import zipfile

PACKAGE_SHA256 = "61d285874ec0a3a1aba5f4b29854917e54456fd6dc985f966c10215a5d416be4"
PROFILE_SHA256 = "c7b1c9bc7855d7507186b80d15bb2fbe8c80f01897daed8b00b1bb5a50b08b10"
SOURCE_REVISION = "468f579f25e63da2188f39fe5ece15c25d2854d7"
RUNTIME_FILE_COUNT = 21


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def reject_links(path):
    for part in [path, *path.parents]:
        if part.exists() or part.is_symlink():
            attributes = getattr(part.lstat(), "st_file_attributes", 0)
            require(not part.is_symlink() and not attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT,
                    "Linked API input paths are unsupported")


def verify(package, profile):
    reject_links(package)
    reject_links(profile)
    require(package.is_file() and profile.is_file(), "Explicit API package and profile inputs are required")
    profile_bytes = profile.read_bytes()
    value = json.loads(profile_bytes)
    require(sha(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()) == PROFILE_SHA256,
            "API profile differs from the approved application")
    require(value["SourceRevision"] == SOURCE_REVISION and len(value["RuntimeFiles"]) == RUNTIME_FILE_COUNT,
            "API source revision or runtime inventory differs")
    require(package.stat().st_size <= 16 * 1024 * 1024, "API package exceeds the supported size")
    require(sha(package.read_bytes()) == PACKAGE_SHA256, "API package archive checksum differs")
    expected = value["RuntimeFiles"]
    with zipfile.ZipFile(package) as archive:
        entries = archive.infolist()
        require(len(entries) == RUNTIME_FILE_COUNT and len({x.filename for x in entries}) == len(entries),
                "API package entry count or uniqueness differs")
        require({x.filename for x in entries} == set(expected), "API package contains unexpected entries")
        for entry in entries:
            require(not entry.is_dir() and not stat.S_ISLNK(entry.external_attr >> 16) and
                    not entry.flag_bits & 1 and 0 < entry.file_size <= 16 * 1024 * 1024,
                    "API package contains an unsupported entry")
            require(sha(archive.read(entry)) == expected[entry.filename].lower(),
                    "API package component checksum differs")
    return {"package_sha256": PACKAGE_SHA256, "profile_sha256": PROFILE_SHA256,
            "source_revision": SOURCE_REVISION, "runtime_files": RUNTIME_FILE_COUNT}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path)
    parser.add_argument("--profile", type=Path)
    parser.add_argument("--require-ci-input", action="store_true")
    args = parser.parse_args()
    if args.require_ci_input:
        raise ValueError("Release preparation is blocked: redistribution-cleared, reproducible API v78 patch-only inputs are unavailable; complete patched game assemblies are not approved release inputs")
    require(args.package is not None and args.profile is not None, "Explicit API package and profile inputs are required")
    print(json.dumps(verify(args.package, args.profile), sort_keys=True))
