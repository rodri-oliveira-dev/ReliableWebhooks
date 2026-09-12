# Changelog

All notable changes to ReliableWebhooks will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and releases follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Added an opt-in webhook destination authorization policy hook and `PublicNetworkWebhookDestinationPolicy` for deployments that accept untrusted or tenant-configurable webhook URLs.

### Changed

- Custom webhook headers are validated when a `WebhookMessage` is created so invalid header names or control-character values cannot be persisted as poison deliveries.
- Webhook HTTP transport now requires HTTPS destinations by default; plaintext HTTP delivery requires explicit `WebhookHttpTransportOptions.AllowInsecureHttp` opt-in.

## [0.1.0] - 2026-09-12

### Added

- Core webhook contracts for immutable messages, delivery lifecycle states, delivery attempts, and delivery snapshots.
- Store abstraction for idempotent enqueue, atomic due-delivery claims, renewable expiring leases, retry scheduling, terminal delivery transitions, and a non-durable in-memory implementation for tests and samples.
- Reusable `IWebhookDeliveryStore` conformance tests and persistence implementation guidance covering idempotency, atomic claims, leases, stale ownership, retries, terminal transitions, cancellation, durability boundaries, and backend-failure expectations.
- HTTP webhook transport for one-attempt `POST` delivery, extensible response classification, per-attempt timeout handling, bounded response-body capture, `Retry-After` metadata, and explicit network/timeout results without internal retry loops.
- Configurable retry policy with capped exponential backoff, deterministic bounded jitter, `Retry-After` delta/date support, maximum-attempt dead-letter decisions, and replaceable retry/jitter abstractions.
- HMAC-SHA256 webhook request signing over the exact outbound payload bytes and a Unix timestamp, with replaceable signer/secret-provider abstractions and configurable delivery header names.
- Concurrent webhook dispatcher with bounded parallelism, atomic lease-based claims, persisted success/retry/permanent/dead-letter transitions, controllable polling, and graceful shutdown for in-flight attempts.
- Backend-neutral observability with structured logging, `ActivitySource` tracing, `Meter` metrics, stable public instrumentation names, and an `InstrumentedWebhookDeliveryStore` decorator for enqueue telemetry across custom stores.
- Microsoft dependency-injection integration with validated options, `IHttpClientFactory`, an application-facing enqueue service, replaceable default abstractions, and optional hosted dispatcher execution.
- Runnable end-to-end sample covering DI, hosted dispatching, HMAC verification, success, retry, dead-letter behavior, and diagnostics, plus production usage and troubleshooting guidance.
- Primary GitHub Actions CI workflow with locked restore, formatting verification, Release build, tests, coverage, NuGet packaging, package validation, symbols, Source Link validation, and downloadable artifacts.
- CodeQL security analysis for C#.
- Dependency Review for pull requests.
- Dependabot configuration for NuGet and GitHub Actions dependencies.
- Optional SonarQube Cloud analysis with OpenCover import and Quality Gate support.
- Release validation with semantic-version checks, reproducible artifacts, GitHub Releases, and optional NuGet.org Trusted Publishing through GitHub OIDC.
- Portable VS Code recommendations and repository development tasks.
- English and Brazilian Portuguese project documentation.
- v0.1.0 release hardening with an explicit public API snapshot, clean package-consumer validation, dual NuGet.org/GitHub Packages publication, and documented release gates.

### Changed

- Initialized the repository as the canonical `ReliableWebhooks` .NET 10 library and removed one-time initialization assets.
- Replaced generic template documentation with project-specific ReliableWebhooks documentation and v0.1.0 delivery goals.
- Finalized NuGet metadata for the `ReliableWebhooks` package.
- Set the development version baseline to `0.1.0` for the first public MVP.
- Removed source-template-only publication decisions from the generated project release path.
- Clarified `IWebhookDeliveryStore` as a technology-agnostic persistence port: the core package defines reliability semantics while consumers choose EF Core, Dapper, files, Redis, document stores, or other conforming persistence mechanisms.
- Retry-After response parsing now ignores malformed values instead of allowing invalid metadata to interrupt delivery processing.
