"""Negative checks for the release boundary; fixtures are never distributions."""
import json
from pathlib import Path
import tempfile
import subprocess
from unittest import mock
import unittest
import zipfile

import verify_publication as release


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.control = self.root / "control"
        self.control.mkdir()
        subprocess.run(["git", "init", "--quiet", str(self.control)], check=True)
        self.control_git("config", "user.name", "Release Test")
        self.control_git("config", "user.email", "release-test@example.invalid")
        self.control_git("config", "core.autocrlf", "true")
        control_notes = self.control / release.NOTES_RELATIVE
        control_notes.parent.mkdir(parents=True)
        control_notes.write_bytes(release.NOTES.read_bytes())
        (self.control / "control.py").write_text("synthetic control fixture")
        self.control_git("add", ".")
        self.control_git("-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Control fixture")
        self.control_commit = self.control_git("rev-parse", "HEAD")
        for patch in [mock.patch.object(release, "CONTROL_ROOT", self.control),
                      mock.patch.object(release, "NOTES", control_notes)]:
            patch.start()
            self.addCleanup(patch.stop)
        self.folder = self.root / "release"
        self.folder.mkdir()
        payload = self.root / "payload"
        self.payload = payload
        payload.mkdir()
        (payload / "SoulsTracker.Desktop.exe").write_bytes(b"fixture")
        for number in range(release.PAYLOAD_COUNT - 1):
            (payload / f"fixture-{number}.dll").write_bytes(b"synthetic")
        files = release.inventory(payload)
        (self.folder / release.NAMES[0]).write_bytes(b"installer fixture")
        with zipfile.ZipFile(self.folder / release.NAMES[1], "w") as archive:
            for path in payload.iterdir():
                archive.write(path, path.name)
        (self.folder / release.NAMES[2]).write_text(json.dumps({
            "spdxVersion": "SPDX-2.3", "packages": [{"SPDXID": "SPDXRef-Fixture"}]}))
        (self.folder / "release-notes.md").write_bytes(self.committed_notes())
        hashes = {n: release.sha(self.folder / n) for n in release.NAMES}
        (self.folder / "SHA256SUMS.txt").write_text("".join(f"{hashes[n]}  {n}\n" for n in release.NAMES))
        hashes.update({n: release.sha(self.folder / n) for n in ["SHA256SUMS.txt", "release-notes.md"]})
        self.manifest = {"source_commit": release.SOURCE, "implementation_commit": release.IMPLEMENTATION,
                         "schema_version": 2, "version": release.VERSION, "artifacts": hashes,
                         "approved_source_commit": release.SOURCE, "source_tree": release.SOURCE_TREE,
                         "release_notes_sha256": release.sha(self.folder / "release-notes.md"),
                         "api78_input": {"package_sha256": release.hollow_knight.PACKAGE_SHA256,
                                         "profile_sha256": release.hollow_knight.PROFILE_SHA256,
                                         "source_revision": release.hollow_knight.SOURCE_REVISION,
                                         "runtime_files": release.hollow_knight.RUNTIME_FILE_COUNT},
                         "payload_files": files, "payload_inventory_sha256": release.inventory_digest(files),
                         "preparation_mode": "github_actions", "workflow_commit": self.control_commit,
                         "prepared_run_id": "123"}
        (self.folder / "provenance.sigstore.json").write_text("fixture; no cryptographic claim")
        self.save()

    def save(self):
        (self.folder / "release-manifest.json").write_text(json.dumps(self.manifest))
        return release.sha(self.folder / "release-manifest.json")

    def control_git(self, *args):
        return release.git(self.control, *args)

    def committed_notes(self):
        return subprocess.check_output(["git", "-C", str(self.control), "show",
                                        f"{self.control_commit}:{release.NOTES_RELATIVE}"])

    def verify(self):
        return release.verify(self.folder, release.sha(self.folder / "release-manifest.json"), self.control_commit, "123")

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
                release.verify(self.folder, value, self.control_commit, "123")

    def test_local_unsigned_rejected(self):
        self.manifest["preparation_mode"] = "local_unsigned"
        self.manifest["workflow_commit"] = None
        self.manifest["prepared_run_id"] = None
        self.save()
        with self.assertRaisesRegex(ValueError, "not signed CI provenance"):
            self.verify()

    def test_wrong_run_or_workflow(self):
        for workflow, run in [("b" * 40, "123"), (self.control_commit, "456")]:
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

    def test_release_version_and_tree_are_pinned(self):
        for field, value in [("version", "2.1.2"), ("source_tree", "b" * 40),
                             ("approved_source_commit", "b" * 40), ("schema_version", 1)]:
            original = self.manifest[field]
            self.manifest[field] = value
            self.save()
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.verify()
            self.manifest[field] = original

    def test_api_input_identity_is_pinned(self):
        self.manifest["api78_input"]["package_sha256"] = "b" * 64
        self.save()
        with self.assertRaisesRegex(ValueError, "API input provenance differs"):
            self.verify()

    def test_payload_count_cannot_drift(self):
        self.manifest["payload_files"].pop()
        self.save()
        with self.assertRaisesRegex(ValueError, "file count changed"):
            self.verify()

    def test_local_metadata_cannot_claim_a_workflow(self):
        self.manifest["preparation_mode"] = "local_unsigned"
        self.save()
        with self.assertRaisesRegex(ValueError, "Local metadata cannot claim"):
            self.verify()

    def test_wrong_control_head_rejected(self):
        self.control_git("-c", "commit.gpgsign=false", "commit", "--quiet", "--allow-empty", "-m", "Different control fixture")
        with self.assertRaisesRegex(ValueError, "control commit differs"):
            self.verify()

    def test_dirty_control_notes_stop_preparation_before_source(self):
        release.NOTES.write_bytes(b"uncommitted release notes")
        with mock.patch.object(release, "verify_source") as source_check:
            with self.assertRaises(subprocess.CalledProcessError):
                release.prepare(self.folder, self.payload, self.control, self.control_commit, "123")
            source_check.assert_not_called()

    def test_dirty_control_script_stops_preparation(self):
        (self.control / "control.py").write_text("uncommitted control change")
        with mock.patch.object(release, "verify_source") as source_check:
            with self.assertRaises(subprocess.CalledProcessError):
                release.prepare(self.folder, self.payload, self.control, self.control_commit, "123")
            source_check.assert_not_called()

    def test_prepare_uses_committed_blob_with_clean_crlf_worktree(self):
        committed = self.committed_notes()
        release.NOTES.write_bytes(committed.replace(b"\n", b"\r\n"))
        self.assertNotEqual(release.NOTES.read_bytes(), committed)
        self.assertEqual(release.verify_control(self.control_commit), committed)
        (self.folder / "release-manifest.json").unlink()
        identity = {"source_commit": release.SOURCE, "source_tree": release.SOURCE_TREE,
                    "approved_source_commit": release.SOURCE, "implementation_commit": release.IMPLEMENTATION}
        # API bytes have separate verification tests; isolate this control/notes boundary.
        with mock.patch.object(release, "verify_source", return_value=identity), \
                mock.patch.object(release.hollow_knight, "verify", return_value=self.manifest["api78_input"]):
            manifest = release.prepare(self.folder, self.payload, self.control, self.control_commit, "123")
        self.assertEqual((self.folder / "release-notes.md").read_bytes(), committed)
        self.assertEqual(manifest["release_notes_sha256"], release.digest(committed))

    def change_output_notes(self, notes):
        (self.folder / "release-notes.md").write_bytes(notes)
        self.manifest["release_notes_sha256"] = release.digest(notes)
        self.manifest["artifacts"]["release-notes.md"] = release.digest(notes)
        self.save()

    def test_rehashed_output_notes_cannot_replace_committed_notes(self):
        self.change_output_notes(b"different release body")
        with self.assertRaisesRegex(ValueError, "committed publication controls"):
            self.verify()

    def test_rehashed_dirty_control_notes_are_rejected(self):
        release.NOTES.write_bytes(b"uncommitted release body")
        self.change_output_notes(release.NOTES.read_bytes())
        with self.assertRaises(subprocess.CalledProcessError):
            self.verify()

    def test_signed_notes_require_exact_committed_line_endings(self):
        self.change_output_notes(self.committed_notes().replace(b"\n", b"\r\n"))
        with self.assertRaisesRegex(ValueError, "committed publication controls"):
            self.verify()


class SourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.source = Path(self.temp.name)
        subprocess.run(["git", "init", "--quiet", str(self.source)], check=True)
        self.git("config", "user.name", "Release Test")
        self.git("config", "user.email", "release-test@example.invalid")
        (self.source / "src").mkdir()
        (self.source / "src/app.cs").write_text("approved fixture")
        self.commit("Approved fixture")
        self.pin = self.git("rev-parse", "HEAD")
        self.patch = mock.patch.object(release, "SOURCE", self.pin)
        self.patch.start()
        self.addCleanup(self.patch.stop)

    def git(self, *args):
        return release.git(self.source, *args)

    def commit(self, message):
        self.git("add", ".")
        self.git("-c", "commit.gpgsign=false", "commit", "--quiet", "-m", message)

    def test_signed_source_requires_exact_commit(self):
        self.assertEqual(release.verify_source(self.source)["source_commit"], self.pin)
        (self.source / "release-notes.md").write_text("control fixture")
        self.commit("Control fixture")
        with self.assertRaisesRegex(ValueError, "not the approved commit"):
            release.verify_source(self.source)
        self.assertEqual(release.verify_source(self.source, local=True)["source_commit"], self.git("rev-parse", "HEAD"))

    def test_local_source_rejects_changed_build_inputs(self):
        (self.source / "src/app.cs").write_text("changed fixture")
        with self.assertRaises(subprocess.CalledProcessError):
            release.verify_source(self.source, local=True)
        self.commit("Changed fixture")
        with self.assertRaises(subprocess.CalledProcessError):
            release.verify_source(self.source, local=True)

    def test_untracked_build_inputs_rejected(self):
        (self.source / "src/extra.cs").write_text("untracked fixture")
        with self.assertRaisesRegex(ValueError, "Untracked application build inputs"):
            release.verify_source(self.source, local=True)

    def test_all_distribution_build_roots_are_protected(self):
        for path in ["plugins", "assets", "eng", "web_overlay", "cloud_overlay", "installer",
                     "Directory.Build.props", "Directory.Packages.props", "global.json", "docs/licenses"]:
            self.assertIn(path, release.PROTECTED_INPUTS)


if __name__ == "__main__":
    unittest.main()
