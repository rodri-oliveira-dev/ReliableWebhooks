# Changelog

All notable changes to ReliableWebhooks will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and releases follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Core webhook contracts for immutable messages, delivery lifecycle states, delivery attempts, and delivery snapshots.
- Store abstraction for idempotent enqueue, atomic due-delivery claims, renewable expiring leases, retry scheduling, terminal delivery transitions, and a non-durable in-memory implementation for tests and samples.
- HTTP webhook transport for one-attempt `POST` delivery, extensible response classification, per-attempt timeout handling, bounded response-body capture, `Retry-After` metadata, and explicit network/timeout results without internal retry loops.
- Configurable retry policy with capped exponential backoff, deterministic bounded jitter, `Retry-After` delta/date support, maximum-attempt dead-letter decisions, and replaceable retry/jitter abstractions.
- HMAC-SHA256 webhook request signing over the exact outbound payload bytes and a Unix timestamp, with replaceable signer/secret-provider abstractions and configurable delivery header names.
- Concurrent webhook dispatcher with bounded parallelism, atomic lease-based claims, persisted success/retry/permanent/dead-letter transitions, controllable polling, and graceful shutdown for in-flight attempts.
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
- Retry-After response parsing now ignores malformed values instead of allowing invalid metadata to interrupt delivery processing.
