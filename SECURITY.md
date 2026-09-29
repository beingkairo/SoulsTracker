# Security policy

Do not open a public issue for a security vulnerability. In particular, never include Overlay URLs, overlay capabilities, protected state files, save files, local database files, game executable paths, or personal information in an issue, screenshot, or log.

Report suspected vulnerabilities privately to the repository maintainer. Include a minimal reproduction, affected version, impact, and any mitigation you have identified. You will receive an acknowledgement and coordinated disclosure plan before public discussion.

SoulsTracker uses read-only game access and online publication to https://overlay.beingkairo.com only when Overlay is used. Anonymous creation exposes no account or delegated management authority. Desktop generates independent request/read/write values locally, protects the complete pending request before network access, and sends only bound verifiers during creation. Two fail-closed admission layers and a serialized hard allocation ceiling protect the create surface. Overlay URLs contain read authority only. Reports involving leaked capabilities, unauthorized publication, memory writes, input automation, code injection, or save editing are treated as high priority.
