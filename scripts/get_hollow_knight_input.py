"""Retrieve and verify the fixed unpublished API input using the Actions token."""
import argparse
import json
import os
from pathlib import Path
import subprocess

import verify_hollow_knight_input as api

REPOSITORY = "beingkairo/SoulsTracker"
REPOSITORY_ID = 1304056819
OWNER_ID = 281211480
RELEASE_ID = 444814994
INPUT_SOURCE = "c89b0c0be5a1d08573427ea48e6b70aa3d5c6c56"
RECEIPT_CANONICAL_SHA256 = "5c566d0d4c0d2c0e38c041433b243ed4b1250e9fc4b01a8ba32de69d2d3d8855"
ASSETS = {
    "api-12620.zip": {"id": 745462808, "bytes": 5493138, "sha256": api.PACKAGE_SHA256,
                      "content_type": "application/zip"},
    "api-12620.receipt.json": {"id": 745462807, "bytes": 4739,
                             "sha256": "d58db460bbd163d656645958e5554dd52ff878a7f1e126458df04ae3814dd862",
                             "content_type": "application/json"},
}
ENDPOINT = f"repos/{REPOSITORY}"


def identity():
    return {"transport": "github_draft_release", "repository": REPOSITORY,
            "repository_id": REPOSITORY_ID, "owner_id": OWNER_ID, "release_id": RELEASE_ID,
            "input_source_commit": INPUT_SOURCE, "origin": "prebuilt_local",
            "profile_sha256": api.PROFILE_SHA256, "assets": {name: dict(pin) for name, pin in ASSETS.items()}}


def verify_origin(value):
    api.require(isinstance(value, dict), "Authenticated input origin required")
    api.require({k: v for k, v in value.items() if k != "build_receipt"} == identity(),
                "Authenticated input identity differs")
    receipt = value.get("build_receipt")
    api.require(api.sha(json.dumps(receipt, sort_keys=True, separators=(",", ":")).encode()) ==
                RECEIPT_CANONICAL_SHA256, "API build receipt information differs")
    return value


def load_origin(path):
    api.reject_links(path)
    return verify_origin(json.loads(path.read_text(encoding="utf8")))


def require_ci():
    api.require(os.environ.get("GITHUB_ACTIONS") == "true" and
                os.environ.get("GITHUB_REPOSITORY") == REPOSITORY and
                os.environ.get("GITHUB_SERVER_URL") == "https://github.com" and
                os.environ.get("GITHUB_API_URL") == "https://api.github.com" and
                bool(os.environ.get("GH_TOKEN")), "Expected Actions context and token required")


def github_get(endpoint, binary=False):
    allowed = {ENDPOINT, f"{ENDPOINT}/releases/{RELEASE_ID}",
               *(f"{ENDPOINT}/releases/assets/{pin['id']}" for pin in ASSETS.values())}
    api.require(endpoint in allowed and (not binary or "/releases/assets/" in endpoint),
                "Unapproved input endpoint")
    api.require(bool(os.environ.get("GH_TOKEN")), "Actions token required")
    # The official CLI owns authentication and HTTPS asset redirect handling.
    # Tokens stay in its environment, never in arguments or returned URLs.
    environment = dict(os.environ)
    environment.pop("GH_DEBUG", None)
    environment["GH_PROMPT_DISABLED"] = "1"
    arguments = ["gh", "api", "--hostname", "github.com", endpoint]
    if binary:
        arguments += ["-H", "Accept: application/octet-stream"]
    result = subprocess.run(arguments, env=environment, capture_output=True, timeout=120, check=False)
    api.require(result.returncode == 0,
                f"Authenticated input GET failed ({endpoint}): {result.stderr.decode(errors='replace').strip()}")
    return result.stdout if binary else json.loads(result.stdout)


def verify_asset(asset, name):
    pin = ASSETS[name]
    api.require(asset.get("id") == pin["id"] and asset.get("name") == name and
                asset.get("size") == pin["bytes"] and asset.get("digest") == "sha256:" + pin["sha256"] and
                asset.get("content_type") == pin["content_type"] and asset.get("state") == "uploaded" and
                asset.get("uploader", {}).get("id") == OWNER_ID and
                asset.get("uploader", {}).get("login") == "beingkairo", "Pinned API asset metadata differs")


def verify_draft(draft):
    api.require(draft.get("id") == RELEASE_ID and draft.get("draft") is True and
                draft.get("published_at") is None and draft.get("prerelease") is True and
                draft.get("target_commitish") == INPUT_SOURCE and
                draft.get("tag_name") == "build-input-hollow-knight-api78-468f579" and
                draft.get("author", {}).get("id") == OWNER_ID and
                draft.get("author", {}).get("login") == "beingkairo", "Unpublished API draft identity differs")
    assets = draft.get("assets", [])
    api.require(len(assets) == len(ASSETS) and {a.get("name") for a in assets} == set(ASSETS),
                "API draft inventory differs")
    for asset in assets:
        verify_asset(asset, asset["name"])


def fetch(destination, source, client=github_get):
    require_ci()
    api.require(destination.is_absolute() and destination == Path(os.environ["RUNNER_TEMP"]) / "SoulsTracker-input",
                "Fresh runner input directory required")
    api.reject_links(destination)
    api.reject_links(source)
    api.require(not destination.exists(), "Refusing to reuse an input directory")
    repo = client(ENDPOINT)
    api.require(repo.get("id") == REPOSITORY_ID and repo.get("full_name") == REPOSITORY and
                repo.get("owner", {}).get("id") == OWNER_ID and
                repo.get("owner", {}).get("login") == "beingkairo", "Input repository ownership differs")
    verify_draft(client(f"{ENDPOINT}/releases/{RELEASE_ID}"))
    destination.mkdir()
    for name, pin in ASSETS.items():
        endpoint = f"{ENDPOINT}/releases/assets/{pin['id']}"
        verify_asset(client(endpoint), name)
        data = client(endpoint, binary=True)
        api.require(len(data) == pin["bytes"] and api.sha(data) == pin["sha256"],
                    "Downloaded API input size or checksum differs")
        with (destination / name).open("xb") as output:
            output.write(data)
    api.verify(destination / "api-12620.zip", source / "src/SoulsTracker.Infrastructure/HollowKnightApi12620.json")
    receipt = json.loads((destination / "api-12620.receipt.json").read_bytes())
    origin = verify_origin({**identity(), "build_receipt": receipt})
    for path, pin in {receipt["recipe"]["path"]: receipt["recipe"], **receipt["dependency_locks"]}.items():
        data = subprocess.check_output(["git", "-C", str(source), "show", f"HEAD:{path}"])
        blob = subprocess.check_output(["git", "-C", str(source), "rev-parse", f"HEAD:{path}"], text=True).strip()
        api.require(api.sha(data) == pin["sha256"] and blob == pin["git_blob"],
                    "API receipt recipe or dependency lock differs from source")
    verify_draft(client(f"{ENDPOINT}/releases/{RELEASE_ID}"))
    (destination / "input-origin.json").write_text(json.dumps(origin, indent=2) + "\n", encoding="utf8")
    return origin


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--destination", required=True, type=Path)
    args = parser.parse_args()
    value = fetch(args.destination, args.source)
    print(json.dumps({"verified": True, "release_id": RELEASE_ID, "assets": value["assets"],
                      "runtime_files": api.RUNTIME_FILE_COUNT, "origin": value["origin"]}))
