# Public Windows alpha release

The current release process is [v0.3.0 one-command Windows build, testing and GitHub publication](v0.3.0-windows-testing.md).

Use `scripts/release/Build-Release.ps1` with the existing official SDK, hash-bound runtime authorization and Inno compiler. It validates actual automated tests, SDK/ABI/runtime/licensing/prerequisites and installer integrity, then produces the final setup EXE, SHA-256 and sanitized release evidence automatically. **No mandatory ACCEPTANCE.json editing or self-hosted runner.** Hardware QA remains recommended and explicitly not verified until actually performed. Upload the final files through the GitHub Releases website and mark the release as a prerelease.

Historical manual-candidate procedures remain in version-specific v0.1.1/v0.2.x documents for those artifacts; they are not prerequisites for v0.3.0.
