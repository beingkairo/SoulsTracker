"""Negative checks for the release boundary; fixtures are never distributions."""
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

import verify_publication as release


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.folder = self.root / "release"
        self.folder.mkdir()
        payload = self.root / "payload"
        payload.mkdir()
        (payload / "SoulsTracker.Desktop.exe").write_bytes(b"fixture")
        files = release.inventory(payload)
        (self.folder / release.NAMES[0]).write_bytes(b"installer fixture")
        with zipfile.ZipFile(self.folder / release.NAMES[1], "w") as archive:
            archive.write(payload / "SoulsTracker.Desktop.exe", "SoulsTracker.Desktop.exe")
        (self.folder / release.NAMES[2]).write_text(json.dumps({
            "spdxVersion": "SPDX-2.3", "packages": [{"SPDXID": "SPDXRef-Fixture"}]}))
        (self.folder / "release-notes.md").write_text("approved notes fixture")
        hashes = {n: release.sha(self.folder / n) for n in release.NAMES}
        (self.folder / "SHA256SUMS.txt").write_text("".join(f"{hashes[n]}  {n}\n" for n in release.NAMES))
        hashes.update({n: release.sha(self.folder / n) for n in ["SHA256SUMS.txt", "release-notes.md"]})
        self.manifest = {"source_commit": release.SOURCE, "implementation_commit": release.IMPLEMENTATION,
                         "version": "2.1.1", "artifacts": hashes,
                         "payload_files": files, "payload_inventory_sha256": release.inventory_digest(files),
                         "preparation_mode": "github_actions", "workflow_commit": "a" * 40,
                         "prepared_run_id": "123"}
        (self.folder / "provenance.sigstore.json").write_text("fixture; no cryptographic claim")
        self.save()

    def save(self):
        (self.folder / "release-manifest.json").write_text(json.dumps(self.manifest))
        return release.sha(self.folder / "release-manifest.json")

    def verify(self):
        return release.verify(self.folder, release.sha(self.folder / "release-manifest.json"), "a" * 40, "123")

    def test_matching_metadata(self):
        self.assertEqual(self.verify()["source_commit"], release.SOURCE)

    def test_wrong_source(self):
        self.manifest["source_commit"] = "b" * 40
        self.save()
        with self.assertRaisesRegex(ValueError, "Unapproved release source"):
            self.verify()

    def test_wrong_implementation(self):
        self.manifest["implementation_commit"] = "b" * 40
        self.save()
        with self.assertRaisesRegex(ValueError, "Unapproved application implementation"):
            self.verify()

    def test_changed_artifact(self):
        for name in [*release.NAMES, "SHA256SUMS.txt", "release-notes.md"]:
            with self.subTest(name=name):
                path = self.folder / name
                original = path.read_bytes()
                path.write_bytes(original + b"changed")
                with self.assertRaisesRegex(ValueError, "checksum differs"):
                    self.verify()
                path.write_bytes(original)

    def test_manifest_approval(self):
        for value in [None, "b" * 64]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                release.verify(self.folder, value, "a" * 40, "123")

    def test_local_unsigned_rejected(self):
        self.manifest["preparation_mode"] = "local_unsigned"
        self.save()
        with self.assertRaisesRegex(ValueError, "not signed CI provenance"):
            self.verify()

    def test_wrong_run_or_workflow(self):
        for workflow, run in [("b" * 40, "123"), ("a" * 40, "456")]:
            with self.subTest(run=run), self.assertRaises(ValueError):
                release.verify(self.folder, self.save(), workflow, run)

    def test_bundle_missing(self):
        (self.folder / "provenance.sigstore.json").unlink()
        with self.assertRaisesRegex(ValueError, "bundle missing"):
            self.verify()

    def test_unexpected_files_or_directories(self):
        for directory in [False, True]:
            path = self.folder / "unexpected"
            path.mkdir() if directory else path.write_text("unexpected")
            with self.assertRaisesRegex(ValueError, "Unexpected"):
                self.verify()
            path.rmdir() if directory else path.unlink()

    def test_portable_inventory(self):
        with zipfile.ZipFile(self.folder / release.NAMES[1], "w") as archive:
            archive.writestr("different.exe", b"fixture")
        self.manifest["artifacts"][release.NAMES[1]] = release.sha(self.folder / release.NAMES[1])
        sums = "".join(f"{self.manifest['artifacts'][n]}  {n}\n" for n in release.NAMES)
        (self.folder / "SHA256SUMS.txt").write_text(sums)
        self.manifest["artifacts"]["SHA256SUMS.txt"] = release.sha(self.folder / "SHA256SUMS.txt")
        self.save()
        with self.assertRaisesRegex(ValueError, "inventory differs"):
            self.verify()

    def test_private_sbom_rejected(self):
        (self.folder / release.NAMES[2]).write_text('{"path":"C:/Users/private"}')
        with self.assertRaisesRegex(ValueError, "private build path"):
            release.check_sbom(self.folder / release.NAMES[2])


if __name__ == "__main__":
    unittest.main()
