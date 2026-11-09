"""API input boundary checks using generated, non-game fixtures."""
import json
from pathlib import Path
import subprocess
import stat
import sys
import tempfile
import unittest
from unittest import mock
import warnings
import zipfile

import verify_hollow_knight_input as api


class ApiInputTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.package = self.root / "api.zip"
        self.profile = self.root / "profile.json"
        self.contents = {f"runtime-{i}.dll": f"synthetic component {i}".encode() for i in range(21)}
        self.value = {"SourceRevision": api.SOURCE_REVISION,
                      "RuntimeFiles": {name: api.sha(data).upper() for name, data in self.contents.items()}}
        self.profile.write_text(json.dumps(self.value))
        self.write_zip(self.contents.items())
        self.patches = [mock.patch.object(api, "PACKAGE_SHA256", api.sha(self.package.read_bytes())),
                        mock.patch.object(api, "PROFILE_SHA256", api.sha(json.dumps(
                            self.value, sort_keys=True, separators=(",", ":")).encode()))]
        for patch in self.patches:
            patch.start()
            self.addCleanup(patch.stop)

    def write_zip(self, entries):
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(self.package, "w") as archive:
                for name, data in entries:
                    archive.writestr(name, data)

    def verify_container(self):
        with mock.patch.object(api, "PACKAGE_SHA256", api.sha(self.package.read_bytes())):
            return api.verify(self.package, self.profile)

    def test_matching_input(self):
        self.assertEqual(api.verify(self.package, self.profile)["runtime_files"], 21)

    def test_profile_line_endings_do_not_change_identity(self):
        self.profile.write_text(json.dumps(self.value, indent=2) + "\r\n")
        self.assertEqual(api.verify(self.package, self.profile)["source_revision"], api.SOURCE_REVISION)

    def test_missing_or_changed_input_rejected(self):
        self.package.write_bytes(b"changed fixture")
        with self.assertRaisesRegex(ValueError, "archive checksum differs"):
            api.verify(self.package, self.profile)
        self.package.unlink()
        with self.assertRaisesRegex(ValueError, "inputs are required"):
            api.verify(self.package, self.profile)

    def test_changed_profile_rejected(self):
        self.value["SourceRevision"] = "b" * 40
        self.profile.write_text(json.dumps(self.value))
        with self.assertRaisesRegex(ValueError, "profile differs"):
            api.verify(self.package, self.profile)

    def test_component_drift_rejected(self):
        self.contents["runtime-0.dll"] = b"changed synthetic component"
        self.write_zip(self.contents.items())
        with self.assertRaisesRegex(ValueError, "component checksum differs"):
            self.verify_container()

    def test_duplicate_and_extra_entries_rejected(self):
        for extra in ["runtime-0.dll", "unexpected.dll", "../unsafe.dll"]:
            self.write_zip([*self.contents.items(), (extra, b"synthetic")])
            with self.subTest(extra=extra), self.assertRaises(ValueError):
                self.verify_container()

    def test_linked_inputs_rejected(self):
        with mock.patch.object(Path, "is_symlink", return_value=True):
            with self.assertRaisesRegex(ValueError, "Linked API input"):
                api.verify(self.package, self.profile)

    def test_link_entry_rejected(self):
        with zipfile.ZipFile(self.package, "w") as archive:
            for name, data in self.contents.items():
                entry = zipfile.ZipInfo(name)
                entry.external_attr = (stat.S_IFLNK | 0o777) << 16 if name == "runtime-0.dll" else 0
                archive.writestr(entry, data)
        with self.assertRaisesRegex(ValueError, "unsupported entry"):
            self.verify_container()

    def test_ci_provisioning_remains_blocked(self):
        result = subprocess.run([sys.executable, str(Path(api.__file__)), "--require-ci-input"],
                                capture_output=True, text=True, check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("the verified API v78 package is not provisioned", result.stderr)
        self.assertIn("approved authenticated CI input source", result.stderr)


if __name__ == "__main__":
    unittest.main()
