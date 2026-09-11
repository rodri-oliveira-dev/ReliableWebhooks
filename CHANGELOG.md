# Changelog

All notable changes to ReliableWebhooks will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and releases follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Core webhook contracts for immutable messages, delivery lifecycle states, delivery attempts, and delivery snapshots.
- HTTP webhook transport for one-attempt `POST` delivery, extensible response classification, per-attempt timeout handling, bounded response-body capture, `Retry-After` metadata, and explicit network/timeout results without internal retry loops.
- Primary GitHub Actions CI workflow with locked restore, formatting verification, Release build, tests, coverage, NuGet packaging, package validation, symbols, Source Link validation, and downloadable artifacts.
- CodeQL security analysis for C#.
- Dependency Review for pull requests.
- Dependabot configuration for NuGet and GitHub Actions dependencies.
- Optional SonarQube Cloud analysis with OpenCover import and Quality Gate support.
- Release validation with semantic-version checks, reproducible artifacts, GitHub Releases, and optional NuGet.org Trusted Publishing through GitHub OIDC.
- Portable VS Code recommendations and repository development tasks.
- English and Brazilian Portuguese project documentation.

### Changed

- Initialized the repository as the canonical `ReliableWebhooks` .NET 10 library and removed one-time initialization assets.
- Replaced generic template documentation with project-specific ReliableWebhooks documentation and v0.1.0 delivery goals.
- Finalized NuGet metadata for the `ReliableWebhooks` package.
- Set the development version baseline to `0.1.0` for the first public MVP.
- Removed source-template-only publication decisions from the generated project release path.
