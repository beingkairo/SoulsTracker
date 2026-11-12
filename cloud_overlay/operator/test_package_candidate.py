import json
from pathlib import Path
import tempfile
import unittest
from package_candidate import sha, verify_package, validate_disabled_gateway


class CandidateIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / "worker.js").write_bytes(b"synthetic module")
        self.manifest = {"files": [{"path": "worker.js", "sha256": sha(b"synthetic module"), "bytes": 16}]}
        self.save()

    def save(self):
        (self.root / "manifest.json").write_text(json.dumps(self.manifest))

    def test_verifies_complete_inventory(self):
        self.assertEqual(verify_package(self.root), self.manifest)

    def test_refuses_changed_bundle(self):
        (self.root / "worker.js").write_bytes(b"tampered module")
        with self.assertRaises(ValueError):
            verify_package(self.root)

    def test_refuses_unlisted_files_and_duplicate_paths(self):
        (self.root / "extra.txt").write_text("synthetic")
        with self.assertRaises(ValueError):
            verify_package(self.root)
        self.manifest["files"].append(self.manifest["files"][0])
        self.save()
        with self.assertRaises(ValueError):
            verify_package(self.root)

    def test_refuses_outside_paths(self):
        self.manifest["files"][0]["path"] = "../outside"
        self.save()
        with self.assertRaises(ValueError):
            verify_package(self.root)


class DisabledGatewayTests(unittest.TestCase):
    def config(self):
        return {"vars": {"OPERATOR_ENABLED": "false", "OPERATOR_HOST": "soulstracker-operator.beingkairo.com"},
                "routes": [{"pattern": "soulstracker-operator.beingkairo.com", "custom_domain": True}]}

    def test_accepts_exact_approved_disabled_custom_domain(self):
        validate_disabled_gateway("policy-gateway", self.config())

    def test_preserves_unrouted_disabled_template(self):
        for name in ["policy-gateway", "diagnostic-gateway"]:
            validate_disabled_gateway(name, {"vars": {"OPERATOR_ENABLED": "false"}})

    def test_refuses_enabled_profile_and_credential_variables(self):
        for key, value in [("OPERATOR_ENABLED", "true"),
                           ("ACCESS_AUTHENTICATION_PROFILE", "independent-mfa-every-login-15m"),
                           ("ACCESS_READ_AUDIENCE", "synthetic"), ("CREDENTIAL", "synthetic")]:
            config = self.config()
            config["vars"][key] = value
            with self.assertRaises(ValueError):
                validate_disabled_gateway("policy-gateway", config)

    def test_refuses_other_routes_and_mismatched_host(self):
        for routes in [[{"pattern": "other.example.invalid", "custom_domain": True}],
                       [{"pattern": "soulstracker-operator.beingkairo.com/*"}],
                       self.config()["routes"] * 2, []]:
            config = self.config()
            config["routes"] = routes
            with self.assertRaises(ValueError):
                validate_disabled_gateway("policy-gateway", config)
        config = self.config()
        config["vars"]["OPERATOR_HOST"] = "other.example.invalid"
        with self.assertRaises(ValueError):
            validate_disabled_gateway("policy-gateway", config)

    def test_refuses_diagnostic_route_and_any_durable_object_binding(self):
        with self.assertRaises(ValueError):
            validate_disabled_gateway("diagnostic-gateway", self.config())
        config = self.config()
        config["durable_objects"] = {"bindings": []}
        with self.assertRaises(ValueError):
            validate_disabled_gateway("policy-gateway", config)


if __name__ == "__main__":
    unittest.main()
