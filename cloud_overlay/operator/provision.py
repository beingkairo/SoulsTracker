"""Bounded Windows operator preparation. No deployment or public create endpoint."""
import argparse
import csv
import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys

ORIGIN = "https://overlay.beingkairo.com"


def verifier(identity, role, capability):
    return hashlib.sha256(f"overlay-v1:{identity}:{role}:{capability}".encode("ascii")).hexdigest()


def deployment_config(account_id, artifact, identities, slots):
    return {
        "name": "soulstracker-cloud-overlay", "account_id": account_id,
        "main": str(artifact / "index.js"), "no_bundle": True, "find_additional_modules": False,
        "compatibility_date": "2026-08-15", "compatibility_flags": ["nodejs_compat"],
        "workers_dev": False, "preview_urls": False, "send_metrics": False, "observability": {"enabled": False},
        "routes": [{"pattern": "overlay.beingkairo.com", "custom_domain": True}],
        "vars": {"PROVISIONED_IDS": identities, "PROVISIONING_SLOTS": slots, "BROWSER_ORIGIN": ORIGIN},
        "assets": {"directory": str(artifact / "assets"), "run_worker_first": ["/api/*"]},
        "ratelimits": [
            {"name": "PUBLISHER_RATE_LIMITER", "namespace_id": "450501", "simple": {"limit": 60, "period": 60}},
            {"name": "LIVE_RATE_LIMITER", "namespace_id": "450502", "simple": {"limit": 120, "period": 60}},
            {"name": "PROVISIONING_RATE_LIMITER", "namespace_id": "450503", "simple": {"limit": 10, "period": 60}}
        ],
        "durable_objects": {"bindings": [{"name": "OVERLAYS", "class_name": "OverlayState"}]},
        "migrations": [{"tag": "v1", "new_sqlite_classes": ["OverlayState"]}]
    }


def private_directory(path):
    # Refuse shared/synced working trees. The new leaf contains no data until
    # inheritance is removed and an owner-only ACL has been installed.
    if os.name != "nt":
        raise ValueError("Windows private file handling required")
    base = Path(os.environ["LOCALAPPDATA"]) / "SoulsTrackerOperator"
    path = path.absolute()
    if path.parent != base or path.exists() or base.is_symlink():
        raise ValueError("Use a new directory directly under LocalAppData/SoulsTrackerOperator")
    base.mkdir(exist_ok=True)
    # Reject junctions/reparse points as well as ordinary symlinks.
    import stat
    if base.stat(follow_symlinks=False).st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
        raise ValueError("Private parent must not be a reparse point")
    who = subprocess.run(["whoami", "/user", "/fo", "csv", "/nh"], check=True, capture_output=True, text=True)
    sid = next(csv.reader(io.StringIO(who.stdout)))[1]
    if not re.fullmatch(r"S-1-(?:\d+-)+\d+", sid):
        raise ValueError("Cannot resolve current owner")
    path.mkdir()
    try:
        subprocess.run(["icacls", str(path), "/inheritance:r", "/grant:r", f"*{sid}:(OI)(CI)F"],
                       check=True, capture_output=True)
    except Exception:
        path.rmdir()
        raise ValueError("Private ACL installation failed") from None


def prepare(inputs, directory, artifact):
    if set(inputs) != {"accountId", "origin"} or inputs["origin"] != ORIGIN or not re.fullmatch(r"[0-9a-f]{32}", inputs["accountId"]):
        raise ValueError("Invalid approved deployment inputs")
    artifact = artifact.resolve(strict=True)
    if not (artifact / "index.js").is_file() or not (artifact / "assets").is_dir():
        raise ValueError("Reviewed production artifact required")
    private_directory(directory)
    identity, read, write = secrets.token_hex(16), secrets.token_hex(32), secrets.token_hex(32)
    if read == write:
        raise ValueError("Independent capabilities required")
    pairing = {"version": 1, "origin": ORIGIN, "overlayId": identity, "readCapability": read, "writeCapability": write}
    config = deployment_config(inputs["accountId"], artifact, [identity], [])
    # Exclusive creation: never overwrite a previous identity or pairing bundle.
    for name, value in [("pairing.json", pairing), ("runtime.wrangler.json", config)]:
        with (directory / name).open("x", encoding="utf-8") as file:
            json.dump(value, file, indent=2)
    config["vars"]["BOOTSTRAP"] = {"v": 1, "overlayId": identity,
        "readVerifier": verifier(identity, "read", read), "writeVerifier": verifier(identity, "write", write)}
    with (directory / "bootstrap.wrangler.json").open("x", encoding="utf-8") as file:
        json.dump(config, file, indent=2)


def prepare_setup(inputs, directory, artifact):
    if set(inputs) != {"accountId", "origin", "existingIds", "slotCount"} or inputs["origin"] != ORIGIN or \
            not re.fullmatch(r"[0-9a-f]{32}", inputs["accountId"]) or type(inputs["slotCount"]) is not int or \
            inputs["slotCount"] < 1 or inputs["slotCount"] > 16 or not isinstance(inputs["existingIds"], list) or \
            len(inputs["existingIds"]) + inputs["slotCount"] > 16 or len(set(inputs["existingIds"])) != len(inputs["existingIds"]) or \
            not all(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{32}", value) for value in inputs["existingIds"]):
        raise ValueError("Invalid approved setup inputs")
    artifact = artifact.resolve(strict=True)
    if not (artifact / "index.js").is_file() or not (artifact / "assets").is_dir():
        raise ValueError("Reviewed production artifact required")
    private_directory(directory)
    codes, slots = [], []
    identities = list(inputs["existingIds"])
    for _ in range(inputs["slotCount"]):
        identity, grant = secrets.token_hex(16), secrets.token_hex(32)
        if identity in identities:
            raise ValueError("Independent setup slots required")
        identities.append(identity)
        codes.append({"version": 1, "setupCode": f"st1.{identity}.{grant}"})
        slots.append({"v": 1, "overlayId": identity, "setupVerifier": verifier(identity, "setup", grant)})
    config = deployment_config(inputs["accountId"], artifact, identities, slots)
    with (directory / "setup-codes.json").open("x", encoding="utf-8") as file:
        json.dump(codes, file, indent=2)
    with (directory / "runtime.wrangler.json").open("x", encoding="utf-8") as file:
        json.dump(config, file, indent=2)


def probe(pairing):
    if set(pairing) != {"version", "origin", "overlayId", "readCapability", "writeCapability"} or pairing["version"] != 1 or pairing["origin"] != ORIGIN:
        raise ValueError("Invalid pairing")
    for key, size in [("overlayId", 32), ("readCapability", 64), ("writeCapability", 64)]:
        if not isinstance(pairing[key], str) or not re.fullmatch("[0-9a-f]{" + str(size) + "}", pairing[key]):
            raise ValueError("Invalid pairing")
    if pairing["readCapability"] == pairing["writeCapability"]:
        raise ValueError("Invalid pairing")
    # HTTPSConnection validates TLS, never follows redirects and ignores proxies.
    connection = http.client.HTTPSConnection("overlay.beingkairo.com", timeout=10)
    try:
        connection.request("GET", f"/api/v1/overlays/{pairing['overlayId']}/publisher",
                           headers={"Authorization": "Bearer " + pairing["writeCapability"]})
        response = connection.getresponse()
        raw = response.read(8193)
        if response.status != 200 or len(raw) > 8192:
            raise ValueError("Provisioning verification failed")
        result = json.loads(raw)
        if set(result) != {"v", "epoch", "generation", "death", "appearance"} or result["v"] != 1:
            raise ValueError("Invalid publisher status")
        return result
    finally:
        connection.close()


def main():
    parser = argparse.ArgumentParser(description="Private operator preparation and explicit provisioning verification; never deploys.")
    parser.add_argument("action", choices=["prepare", "prepare-setup", "initialize", "verify"])
    parser.add_argument("--inputs", type=Path)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--artifact", type=Path)
    args = parser.parse_args()
    try:
        if args.action == "prepare":
            prepare(json.loads(args.inputs.read_text(encoding="utf-8")), args.directory, args.artifact)
        elif args.action == "prepare-setup":
            prepare_setup(json.loads(args.inputs.read_text(encoding="utf-8")), args.directory, args.artifact)
        else:
            pairing = json.loads((args.directory / "pairing.json").read_text(encoding="utf-8"))
            receipt = args.directory / "receipt.json"
            if args.action == "initialize" and receipt.exists():
                raise ValueError("Already initialized; use verify")
            previous = json.loads(receipt.read_text(encoding="utf-8")) if args.action == "verify" else None
            result = probe(pairing)
            if args.action == "initialize":
                if result["epoch"] != "0" or result["generation"] != "0" or any(result[key]["revision"] != "0" for key in ["death", "appearance"]):
                    raise ValueError("Unexpected existing state")
                with receipt.open("x", encoding="utf-8") as file:
                    json.dump(result, file)
            elif previous != result:
                raise ValueError("State changed across provisioning")
    except Exception:
        # Never print exception paths, input contents, capabilities or headers.
        print("Operator step failed. Preserve private files; no automatic retry or deployment performed.", file=sys.stderr)
        return 1
    print("Operator step verified. No deployment performed by this tool.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
