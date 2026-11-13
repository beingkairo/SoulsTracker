"""Authenticated input boundary tests using synthetic, non-game bytes."""
import copy
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock
import zipfile

import get_hollow_knight_input as get


class RetrievalTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source = self.root / "source"
        profile_path = self.source / "src/SoulsTracker.Infrastructure/HollowKnightApi12620.json"
        profile_path.parent.mkdir(parents=True)
        components = {f"fixture-{n}.dll": f"synthetic {n}".encode() for n in range(21)}
        profile = {"SourceRevision": get.api.SOURCE_REVISION,
                   "RuntimeFiles": {name: get.api.sha(data) for name, data in components.items()}}
        profile_path.write_text(json.dumps(profile))
        archive_path = self.root / "fixture.zip"
        with zipfile.ZipFile(archive_path, "w") as archive:
            for name, data in components.items():
                archive.writestr(name, data)
        package = archive_path.read_bytes()
        self.receipt = {"recipe": {"path": "eng/fixture.ps1", "sha256": get.api.sha(b"recipe"),
                                   "git_blob": "b" * 40},
                        "dependency_locks": {"eng/fixture.json": {"sha256": get.api.sha(b"lock"),
                                                                  "git_blob": "c" * 40}}}
        receipt_bytes = json.dumps(self.receipt).encode()
        pins = copy.deepcopy(get.ASSETS)
        for name, data in [("api-12620.zip", package), ("api-12620.receipt.json", receipt_bytes)]:
            pins[name].update(bytes=len(data), sha256=get.api.sha(data))
        self.binary = {pins["api-12620.zip"]["id"]: package,
                       pins["api-12620.receipt.json"]["id"]: receipt_bytes}
        for patch in [mock.patch.object(get, "ASSETS", pins),
                      mock.patch.object(get, "RECEIPT_CANONICAL_SHA256", self.canonical(self.receipt)),
                      mock.patch.object(get.api, "PACKAGE_SHA256", get.api.sha(package)),
                      mock.patch.object(get.api, "PROFILE_SHA256", self.canonical(profile)),
                      mock.patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "GITHUB_REPOSITORY": get.REPOSITORY,
                          "GITHUB_SERVER_URL": "https://github.com", "GITHUB_API_URL": "https://api.github.com",
                          "GH_TOKEN": "synthetic-not-a-credential", "RUNNER_TEMP": str(self.root)})]:
            patch.start()
            self.addCleanup(patch.stop)
        self.repository = {"id": get.REPOSITORY_ID, "full_name": get.REPOSITORY,
                           "owner": {"id": get.OWNER_ID, "login": "beingkairo"}}
        self.draft = {"id": get.RELEASE_ID, "draft": True, "published_at": None, "prerelease": True,
                      "target_commitish": get.INPUT_SOURCE, "tag_name": "build-input-hollow-knight-api78-468f579",
                      "author": {"id": get.OWNER_ID, "login": "beingkairo"},
                      "assets": [{"id": pin["id"], "name": name, "size": pin["bytes"],
                                  "digest": "sha256:" + pin["sha256"], "content_type": pin["content_type"],
                                  "state": "uploaded", "uploader": {"id": get.OWNER_ID, "login": "beingkairo"}}
                                 for name, pin in pins.items()]}
        self.destination = self.root / "SoulsTracker-input"
        self.requests = []
        patch = mock.patch.object(get.subprocess, "check_output", side_effect=self.git_output)
        patch.start()
        self.addCleanup(patch.stop)

    @staticmethod
    def canonical(value):
        return get.api.sha(json.dumps(value, sort_keys=True, separators=(",", ":")).encode())

    def test_recreated_github_input_identity_is_pinned(self):
        self.assertEqual(get.RELEASE_ID, 444814994)
        self.assertEqual(get.ASSETS["api-12620.zip"]["id"], 745462808)
        self.assertEqual(get.ASSETS["api-12620.receipt.json"]["id"], 745462807)

    @staticmethod
    def git_output(args, **kwargs):
        recipe = args[-1].endswith("fixture.ps1")
        return (("b" if recipe else "c") * 40) if kwargs.get("text") else (b"recipe" if recipe else b"lock")

    def client(self, endpoint, binary=False):
        self.requests.append((endpoint, binary))
        if endpoint == get.ENDPOINT:
            return copy.deepcopy(self.repository)
        if endpoint == f"{get.ENDPOINT}/releases/{get.RELEASE_ID}":
            return copy.deepcopy(self.draft)
        asset_id = int(endpoint.rsplit("/", 1)[1])
        if binary:
            return self.binary[asset_id]
        return copy.deepcopy(next(a for a in self.draft["assets"] if a["id"] == asset_id))

    def fetch(self, client=None):
        return get.fetch(self.destination, self.source, client or self.client)

    def test_matching_input_writes_origin_only_after_complete_verification(self):
        origin = self.fetch()
        self.assertEqual(get.load_origin(self.destination / "input-origin.json"), origin)
        self.assertEqual(origin["build_receipt"], self.receipt)
        self.assertEqual(sum(binary for _, binary in self.requests), 2)
        self.assertEqual((self.destination / "api-12620.zip").read_bytes(),
                         self.binary[get.ASSETS["api-12620.zip"]["id"]])

    def test_bad_context_or_absent_token_stops_before_network(self):
        for variable in ["GITHUB_ACTIONS", "GITHUB_REPOSITORY", "GITHUB_SERVER_URL", "GITHUB_API_URL", "GH_TOKEN"]:
            with self.subTest(variable=variable), mock.patch.dict(os.environ, {variable: ""}):
                with self.assertRaisesRegex(ValueError, "Actions context"):
                    self.fetch()
        self.assertEqual(self.requests, [])

    def test_wrong_repository_ownership_rejected(self):
        self.repository["owner"]["id"] = 1
        with self.assertRaisesRegex(ValueError, "repository ownership"):
            self.fetch()

    def test_published_or_changed_draft_rejected(self):
        for field, value in [("id", 1), ("draft", False), ("published_at", "published"),
                             ("target_commitish", "main"), ("tag_name", "latest")]:
            with self.subTest(field=field), mock.patch.dict(self.draft, {field: value}):
                with self.assertRaisesRegex(ValueError, "draft identity"):
                    self.fetch()
        self.assertFalse(self.destination.exists())

    def test_asset_inventory_and_metadata_drift_rejected(self):
        for field, value in [("id", 1), ("name", "different.zip"), ("size", 1), ("digest", "changed"),
                             ("content_type", "text/plain"), ("state", "new"), ("uploader", {"id": 1})]:
            with self.subTest(field=field), mock.patch.dict(self.draft["assets"][0], {field: value}):
                with self.assertRaises(ValueError):
                    self.fetch()
        with mock.patch.dict(self.draft, {"assets": self.draft["assets"][:1]}):
            with self.assertRaisesRegex(ValueError, "inventory"):
                self.fetch()

    def test_individual_asset_metadata_must_match_draft(self):
        def client(endpoint, binary=False):
            value = self.client(endpoint, binary)
            if "/assets/" in endpoint and not binary:
                value["size"] += 1
            return value
        with self.assertRaisesRegex(ValueError, "asset metadata"):
            self.fetch(client)
        self.assertFalse((self.destination / "input-origin.json").exists())

    def test_modified_or_missing_binary_rejected(self):
        self.binary[get.ASSETS["api-12620.zip"]["id"]] = b"changed archive"
        with self.assertRaisesRegex(ValueError, "checksum differs"):
            self.fetch()
        self.assertFalse((self.destination / "input-origin.json").exists())

    def test_authentication_failure_has_no_fallback(self):
        with self.assertRaisesRegex(RuntimeError, "HTTP 403"):
            self.fetch(mock.Mock(side_effect=RuntimeError("HTTP 403")))
        self.assertFalse(self.destination.exists())

    def test_existing_or_wrong_output_directory_rejected(self):
        self.destination.mkdir()
        with self.assertRaisesRegex(ValueError, "reuse"):
            self.fetch()
        with self.assertRaisesRegex(ValueError, "runner input directory"):
            get.fetch(self.root / "different", self.source, self.client)
        self.assertEqual(self.requests, [])

    def test_linked_input_path_rejected(self):
        self.destination.mkdir()
        with mock.patch.object(Path, "is_symlink", return_value=True):
            with self.assertRaisesRegex(ValueError, "Linked API input"):
                self.fetch()

    def test_draft_published_during_download_is_rejected(self):
        calls = 0
        def client(endpoint, binary=False):
            nonlocal calls
            if endpoint == f"{get.ENDPOINT}/releases/{get.RELEASE_ID}":
                calls += 1
                if calls == 2:
                    self.draft["draft"] = False
            return self.client(endpoint, binary)
        with self.assertRaisesRegex(ValueError, "draft identity"):
            self.fetch(client)
        self.assertFalse((self.destination / "input-origin.json").exists())

    def test_receipt_recipe_drift_rejected(self):
        with mock.patch.object(get.subprocess, "check_output", return_value=b"different recipe"):
            with self.assertRaisesRegex(ValueError, "recipe or dependency lock"):
                self.fetch()

    def test_changed_receipt_information_or_transport_rejected(self):
        value = {**get.identity(), "build_receipt": self.receipt}
        for field, changed in [("release_id", 1), ("transport", "public_url"), ("build_receipt", {})]:
            with self.subTest(field=field), mock.patch.dict(value, {field: changed}):
                with self.assertRaises(ValueError):
                    get.verify_origin(value)

    def test_cli_owns_redirects_and_token_is_only_in_environment(self):
        result = subprocess.CompletedProcess([], 0, stdout=b"synthetic download", stderr=b"")
        with mock.patch.dict(os.environ, {"GH_DEBUG": "api"}), \
                mock.patch.object(get.subprocess, "run", return_value=result) as run:
            asset_path = f"{get.ENDPOINT}/releases/assets/{get.ASSETS['api-12620.zip']['id']}"
            self.assertEqual(get.github_get(asset_path, True), result.stdout)
            args, options = run.call_args
            self.assertEqual(args[0], ["gh", "api", "--hostname", "github.com",
                                      asset_path, "-H", "Accept: application/octet-stream"])
            self.assertNotIn(os.environ["GH_TOKEN"], args[0])
            self.assertNotIn("GH_DEBUG", options["env"])
            self.assertEqual(options["env"]["GH_TOKEN"], os.environ["GH_TOKEN"])
        with self.assertRaisesRegex(ValueError, "Unapproved input endpoint"):
            get.github_get("https://example.invalid/redirect", True)


if __name__ == "__main__":
    unittest.main()
