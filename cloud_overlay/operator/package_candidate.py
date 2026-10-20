"""Freeze local bundles/configuration/assets. No network or deployment commands."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import subprocess

BASELINE = "03517340ee2aaa74b03a63ea3e137a4365116f5b"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def verify_package(destination):
    manifest = json.loads((destination / "manifest.json").read_text())
    expected = {"manifest.json"}
    for item in manifest["files"]:
        name = item["path"]
        path = destination / name
        if name in expected or path.is_symlink() or not path.resolve().is_relative_to(destination.resolve()):
            raise ValueError("invalid package path")
        expected.add(name)
        data = path.read_bytes()
        if sha(data) != item["sha256"] or len(data) != item["bytes"]:
            raise ValueError("package content changed")
    if {p.relative_to(destination).as_posix() for p in destination.rglob("*") if p.is_file()} != expected:
        raise ValueError("unexpected package files")
    return manifest


def freeze(root, source_sha, source_tree, clean):
    source = root / "cloud_overlay"
    production = json.loads((source / "wrangler.jsonc").read_text())
    require(production["vars"]["PROVISIONING_CONFIGURATION_VERSION"] == "3", "authority version changed")
    require(production["vars"]["PROVISIONING_CEILING"] == production["vars"]["PROVISIONING_HARD_MAXIMUM"] == "100", "ceiling changed")
    require("PROVISIONING_ALLOCATION_POLICY" not in production["vars"], "policy must not be configured here")
    bundles = {"worker": source / "dist/index.js", "policy-gateway": source / "dist/policy-gateway/index.js",
               "diagnostic-target": source / "dist/diagnostic-target/diagnostic-index.js", "diagnostic-gateway": source / "dist/diagnostic-gateway/diagnostic.js"}
    inputs = {f"{name}/index.js": path.read_bytes() for name, path in bundles.items()}
    require(b"readAuthorityAggregate" not in inputs["worker/index.js"], "permanent module contains diagnostic")
    for data in inputs.values():
        require(b"fixtureSeed" not in data and b"fixtureClaim" not in data, "test driver in bundle")
    assets = root / "web_overlay/dist/hosted"
    for path in sorted(assets.rglob("*")):
        if path.is_symlink():
            raise ValueError("asset symlink refused")
        if path.is_file():
            require(path.suffix != ".map", "source map in assets")
            inputs[f"assets/{path.relative_to(assets).as_posix()}"] = path.read_bytes()
    if not any(name.startswith("assets/") and name.endswith(".html") for name in inputs):
        raise ValueError("browser assets missing")

    target = copy.deepcopy(production)
    target.update(main="../worker/index.js", no_bundle=True, find_additional_modules=False)
    target["assets"]["directory"] = "../assets"
    temporary = copy.deepcopy(target)
    temporary["main"] = "../diagnostic-target/index.js"
    temporary["vars"]["PROVISIONING_DIAGNOSTIC_ENABLED"] = "false"
    configurations = {"target": target, "temporary-target": temporary}
    for name, filename in [("policy-gateway", "wrangler.jsonc"), ("diagnostic-gateway", "diagnostic.wrangler.jsonc")]:
        gateway = json.loads((source / "operator_gateway" / filename).read_text())
        require(gateway["vars"] == {"OPERATOR_ENABLED": "false"}, "gateway must remain disabled")
        require("durable_objects" not in gateway and "routes" not in gateway, "gateway resource boundary changed")
        gateway.update(main=f"../{name}/index.js", no_bundle=True, find_additional_modules=False)
        configurations[name] = gateway
    for name, configuration in configurations.items():
        require(configuration["workers_dev"] is False and configuration["preview_urls"] is False, "alternate URLs enabled")
        inputs[f"config/{name}.json"] = (json.dumps(configuration, indent=2) + "\n").encode()
    manifest = {
        "manifest_schema_version": 1, "source_sha": source_sha, "source_tree": source_tree, "clean_source": clean,
        "deployment_ready": False, "baseline": BASELINE,
        "configuration_preservation": {"complete_checked_in_target": production,
            "production_config_sha256": sha((source / "wrangler.jsonc").read_bytes()),
            "namespace_linkage": "existing Worker/class/binding linkage; no namespace creation or migration rewind",
            "live_namespace_ids": None, "live_linkage_unavailable_reason": "not_accessed_or_authorized"},
        "secret_names_only": {"permanent_target": ["PROVISIONING_POLICY_READ_KEY", "PROVISIONING_POLICY_APPLY_KEY"],
            "temporary_target": ["PROVISIONING_DIAGNOSTIC_READ_KEY"], "gateways": []},
        "activation_blockers": ["owner approval and current full deployed/persisted parity", "existing namespace linkage evidence",
            "Access host/issuer/role policy/MFA/session evidence and restricted audit custody", "secure independent role credential installation",
            "resource/plan/usage/headroom and owner-selected policy", "separate deployment/invocation/application authorization"],
        "rollback": {"target_configuration": "config/target.json", "operator_configuration": "config/policy-gateway.json",
            "reconcile_current_policy_and_credentials_before_use": True,
            "preserve": ["identities", "URLs", "reservations", "policy state and revision", "legacy-writer rejection",
                         "namespaces/classes/migrations", "seed list", "rates", "historical browser assets", "permanent operator access"],
            "never": ["drop policy/identity tables", "restore stale policy/authority configuration", "rewind migrations",
                      "retry ambiguous apply automatically", "restore pre-explicit-apply code"]},
        "temporary_diagnostic": {"target_configuration": "config/temporary-target.json", "gateway_configuration": "config/diagnostic-gateway.json",
            "pre_activation_only": True, "teardown": "deny gateway; settle outstanding call; remove diagnostic Worker/binding/route/key; restore permanent target module; verify permanent read/apply"},
        "files": [{"path": name, "sha256": sha(data), "bytes": len(data)} for name, data in sorted(inputs.items())]
    }
    inventory_hash = sha(json.dumps(manifest["files"], sort_keys=True).encode())
    destination = source / "dist/operator-candidate" / f"{source_sha}-{inventory_hash[:12]}"
    destination.mkdir(parents=True, exist_ok=True)
    for name, data in inputs.items():
        output = destination / name
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(data)
    (destination / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    require({p.relative_to(destination).as_posix() for p in destination.rglob("*") if p.is_file()} == set(inputs) | {"manifest.json"}, "unexpected package files")
    verify_package(destination)
    return destination, manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--allow-dirty", action="store_true", help="precommit inspection only; manifest is explicitly unreviewed")
    parser.add_argument("--verify", type=Path, help="verify a previously frozen local package; never deploy")
    args = parser.parse_args()
    if args.verify is not None:
        manifest = verify_package(args.verify)
        print(json.dumps({"verified": True, "source_sha": manifest["source_sha"], "deployment_ready": False}))
        return
    root = Path(__file__).resolve().parents[2]
    git = lambda *arguments: subprocess.check_output(["git", *arguments], cwd=root)
    clean = not git("status", "--porcelain=v1").strip()
    if not clean and not args.allow_dirty:
        raise SystemExit("clean committed source required")
    baseline = git("show", f"{BASELINE}:cloud_overlay/wrangler.jsonc").replace(b"\r\n", b"\n")
    require((root / "cloud_overlay/wrangler.jsonc").read_bytes().replace(b"\r\n", b"\n") == baseline, "complete Production configuration changed")
    destination, manifest = freeze(root, git("rev-parse", "HEAD").decode().strip(), git("rev-parse", "HEAD^{tree}").decode().strip() if clean else "uncommitted", clean)
    print(json.dumps({"path": destination.relative_to(root).as_posix(), "files": len(manifest["files"]),
                      "source_sha": manifest["source_sha"], "clean_source": clean, "deployment_ready": False,
                      "manifest_sha256": sha((destination / "manifest.json").read_bytes())}))


if __name__ == "__main__":
    main()
