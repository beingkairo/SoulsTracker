import importlib.util
import json
import os
import shutil
import uuid
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("provision", Path(__file__).with_name("provision.py"))
provision = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(provision)


class ProvisionTests(unittest.TestCase):
    def test_verify_without_receipt_never_contacts_service(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "pairing.json").write_text(json.dumps({"version": 1, "origin": provision.ORIGIN,
                "overlayId": "1" * 32, "readCapability": "2" * 64, "writeCapability": "3" * 64}))
            with patch.object(provision.sys, "argv", ["provision", "verify", "--directory", str(directory)]), patch.object(provision, "probe") as probe, patch("builtins.print"):
                self.assertEqual(provision.main(), 1)
                probe.assert_not_called()

    @unittest.skipUnless(os.name == "nt", "Windows ACL boundary")
    def test_real_private_directory_blocks_inheritance_and_refuses_reuse(self):
        directory = Path(os.environ["LOCALAPPDATA"]) / "SoulsTrackerOperator" / ("test-" + uuid.uuid4().hex)
        try:
            provision.private_directory(directory)
            output = provision.subprocess.run(["icacls", str(directory)], check=True, capture_output=True, text=True).stdout
            self.assertNotIn("(I)", output)
            self.assertEqual(output.count("(OI)(CI)(F)"), 1)
            with self.assertRaises(ValueError):
                provision.private_directory(directory)
        finally:
            if directory.exists():
                shutil.rmtree(directory)

    def test_invalid_inputs_fail_before_entropy_or_private_files(self):
        for inputs in [{}, {"accountId": "bad", "origin": provision.ORIGIN},
                       {"accountId": "a" * 32, "origin": "https://elsewhere.test"}]:
            with patch.object(provision.secrets, "token_hex") as random, patch.object(provision, "private_directory") as mkdir:
                with self.assertRaises(ValueError):
                    provision.prepare(inputs, Path("unused"), Path("unused"))
                random.assert_not_called()
                mkdir.assert_not_called()

    def test_probe_never_follows_redirect_or_emits_error_response(self):
        from unittest.mock import MagicMock
        pairing = {"version": 1, "origin": provision.ORIGIN, "overlayId": "1" * 32,
                   "readCapability": "2" * 64, "writeCapability": "3" * 64}
        for status, body in [(302, b"secret"), (500, b"secret"), (200, b"x" * 8193)]:
            connection = MagicMock()
            connection.getresponse.return_value.status = status
            connection.getresponse.return_value.read.return_value = body
            with patch.object(provision.http.client, "HTTPSConnection", return_value=connection):
                with self.assertRaisesRegex(ValueError, "Provisioning verification failed"):
                    provision.probe(pairing)
                connection.request.assert_called_once()
                connection.close.assert_called_once()

    def test_probe_uses_header_only_and_checks_origin_before_connecting(self):
        pairing = {"version": 1, "origin": provision.ORIGIN, "overlayId": "1" * 32,
                   "readCapability": "2" * 64, "writeCapability": "3" * 64}
        from unittest.mock import MagicMock
        connection = MagicMock()
        response = connection.getresponse.return_value
        response.status = 200
        response.read.return_value = b'{"v":1,"epoch":"0","generation":"0","death":{"revision":"0","digest":"x"},"appearance":{"revision":"0","digest":"y"}}'
        with patch.object(provision.http.client, "HTTPSConnection", return_value=connection) as constructor:
            result = provision.probe(pairing)
            self.assertEqual(result["epoch"], "0")
            constructor.assert_called_once_with("overlay.beingkairo.com", timeout=10)
            args, kwargs = connection.request.call_args
            self.assertEqual(args, ("GET", "/api/v1/overlays/" + "1" * 32 + "/publisher"))
            self.assertEqual(kwargs["headers"]["Authorization"], "Bearer " + "3" * 64)
            connection.close.assert_called_once()
        for origin in ["http://overlay.beingkairo.com", "https://overlay.beingkairo.com.evil.test", "https://elsewhere.test"]:
            with patch.object(provision.http.client, "HTTPSConnection") as constructor:
                with self.assertRaises(ValueError):
                    provision.probe({**pairing, "origin": origin})
                constructor.assert_not_called()

    def test_preparation_separates_pairing_and_verifiers_without_deployment(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifact = root / "artifact"
            artifact.mkdir()
            (artifact / "index.js").write_text("export default {}", encoding="utf-8")
            (artifact / "assets").mkdir()
            inputs = {"accountId": "a" * 32, "origin": "https://overlay.beingkairo.com"}
            with patch.object(provision, "private_directory", side_effect=lambda path: path.mkdir()), patch.object(
                    provision.secrets, "token_hex", side_effect=["1" * 32, "2" * 64, "3" * 64]):
                provision.prepare(inputs, root / "private", artifact)
            pairing = json.loads((root / "private/pairing.json").read_text())
            bootstrap = json.loads((root / "private/bootstrap.wrangler.json").read_text())
            runtime = json.loads((root / "private/runtime.wrangler.json").read_text())
            self.assertEqual(pairing, {"version": 1, "origin": inputs["origin"], "overlayId": "1" * 32,
                                      "readCapability": "2" * 64, "writeCapability": "3" * 64})
            self.assertNotIn("BOOTSTRAP", runtime["vars"])
            self.assertEqual(bootstrap["vars"]["BOOTSTRAP"]["writeVerifier"], provision.verifier("1" * 32, "write", "3" * 64))
            for config in [bootstrap, runtime]:
                text = json.dumps(config)
                self.assertNotIn("2" * 64, text)
                self.assertNotIn("3" * 64, text)
                self.assertEqual(config["vars"]["PROVISIONED_IDS"], ["1" * 32])
                self.assertEqual(config["durable_objects"]["bindings"], [{"name": "OVERLAYS", "class_name": "OverlayState"}])
                self.assertFalse(config["workers_dev"])
                self.assertFalse(config["preview_urls"])
                self.assertFalse(config["find_additional_modules"])


if __name__ == "__main__":
    unittest.main()
