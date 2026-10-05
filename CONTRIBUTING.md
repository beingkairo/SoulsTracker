# Contributing

Thank you for helping improve SoulsTracker.

## Before opening a pull request

1. Keep changes focused and include tests for changed behavior.
2. Do not commit build output, local databases, Overlay URLs, capabilities, protected state files, save files, game paths, or personal files.
3. Preserve the read-only game-memory boundary: no game memory writes, injection, input automation, save editing, or gameplay automation.
4. Run the project build and test checks before submitting.

## Pull requests

Explain the user-facing change, verification performed, and any limitations. Avoid unrelated formatting or generated-file changes.

## Issues

Use public issues for reproducible bugs and feature requests. Follow [SECURITY.md](SECURITY.md) for security-sensitive reports.

## Release notes

For every public release, write `docs/releases/v<version>.md` with the exact headings `## Compatible games` and `## Latest changes`. Use ordinary Markdown list items in both sections. Verify the compatible games against the current product for that release; do not copy an older list. Include only major user-facing changes for that release in Latest changes. Do not rename these headings.

The release workflow publishes that file as the complete GitHub release description without appending the getting-started guide or generated notes. The two sections in the published GitHub release notes are the canonical public source for future beingkairo.com SoulsTracker Compatible games and Latest changes sections. The site should read the latest published release rather than maintain separate edited lists. Website integration is separate work. Owner review and approval of the complete release notes and package are required before every public release.
