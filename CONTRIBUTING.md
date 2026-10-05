# Contributing

Thank you for helping improve SoulsTracker.

## Before opening a pull request

1. Keep changes focused and include tests for changed behavior.
2. Do not commit build output, local databases, Overlay URLs, capabilities, protected state files, save files, game paths, or personal files.
3. Preserve the read-only game-memory boundary: no game memory writes, injection, input automation, save editing, or gameplay automation.
4. Run the build and test commands from the README.

## Pull requests

Explain the user-facing change, verification performed, and any limitations. Avoid unrelated formatting or generated-file changes.

## Issues

Use public issues for reproducible bugs and feature requests. Follow [SECURITY.md](SECURITY.md) for security-sensitive reports.

## Release notes

For each release, write `docs/releases/v<version>.md` with the exact headings `## Compatible games` and `## Latest changes`. Use ordinary Markdown list items and concise user-facing wording. Verify compatible games, tracking modes, and edition/version restrictions against the current selectable product and active readers for that release; do not copy an older list. Include only meaningful user-visible changes. Published GitHub release notes are the canonical source if static or site content differs.

The release workflow appends the getting-started guide to the published notes. beingkairo.com should later read the latest published SoulsTracker GitHub release and extract these two sections, rather than maintain a separate edited games list or changelog. Website parsing and integration are separate work. The owner must review the complete release notes and package before every publication.
