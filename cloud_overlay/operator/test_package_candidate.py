import json
from pathlib import Path
import tempfile
import unittest
from package_candidate import sha, verify_package


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


if __name__ == "__main__":
    unittest.main()
