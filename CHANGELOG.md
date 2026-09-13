# Changelog

All notable changes to ReliableWebhooks will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and releases follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-09-13

### Added

- Core webhook contracts for immutable messages, delivery lifecycle states, delivery attempts, and delivery snapshots.
- Store abstraction for idempotent enqueue, atomic due-delivery claims, renewable expiring leases, retry scheduling, terminal delivery transitions, and a non-durable in-memory implementation for tests and samples.
- Reusable `IWebhookDeliveryStore` conformance tests and persistence implementation guidance covering idempotency, atomic claims, leases, stale ownership, retries, terminal transitions, cancellation, durability boundaries, and backend-failure expectations.
- HTTP webhook transport for one-attempt `POST` delivery, extensible response classification, per-attempt timeout handling, bounded response-body capture, `Retry-After` metadata, and explicit network/timeout results without internal retry loops.
- Configurable retry policy with capped exponential backoff, deterministic bounded jitter, `Retry-After` delta/date support, maximum-attempt dead-letter decisions, and replaceable retry/jitter abstractions.
- HMAC-SHA256 webhook request signing over the exact outbound payload bytes and authenticated metadata, with replaceable signer/secret-provider abstractions and configurable delivery header names.
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
- v1.0.0 release hardening with an explicit public API snapshot, clean package-consumer validation, dual NuGet.org/GitHub Packages publication, and documented release gates.
- An opt-in webhook destination authorization policy hook and `PublicNetworkWebhookDestinationPolicy` for deployments that accept untrusted or tenant-configurable webhook URLs.
- Configurable `WebhookMessageLimits` and DI enqueue-service enforcement for outbound payload, custom-header, and persisted metadata size limits.
- `WebhookMetricsOptions` to keep built-in metric event-type dimensions disabled by default and allow bounded opt-in through an event-type allow-list.

### Changed

- Initialized the repository as the canonical `ReliableWebhooks` .NET 10 library and removed one-time initialization assets.
- Replaced generic template documentation with project-specific ReliableWebhooks documentation and v1.0.0 delivery goals.
- Finalized NuGet metadata for the `ReliableWebhooks` package.
- Set the development version baseline to `1.0.0` for the first public stable release.
- Removed source-template-only publication decisions from the generated project release path.
- Clarified `IWebhookDeliveryStore` as a technology-agnostic persistence port: the core package defines reliability semantics while consumers choose EF Core, Dapper, files, Redis, document stores, or other conforming persistence mechanisms.
- `Retry-After` response parsing now ignores malformed values instead of allowing invalid metadata to interrupt delivery processing.
- Release automation now publishes the validated primary `.nupkg` to NuGet.org with implicit symbol publication disabled and publishes/deduplicates the validated `.snupkg` in its dedicated step when Trusted Publishing is enabled, while preserving the no-rebuild publication path.
- `WebhookHttpTransport` now reuses the immutable payload buffer captured by `WebhookMessage` for signing and request content instead of creating an additional full payload copy on each send.
- Built-in metrics no longer tag raw webhook event types by default; logs and traces continue to include event type for correlation.
- Custom webhook headers are validated when a `WebhookMessage` is created so invalid header names or control-character values cannot be persisted as poison deliveries.
- Webhook IDs, event types, and content types are validated when a `WebhookMessage` is created before those values can be used in generated HTTP headers or telemetry.
- Webhook HTTP transport now requires HTTPS destinations by default; plaintext HTTP delivery requires explicit `WebhookHttpTransportOptions.AllowInsecureHttp` opt-in.
- The DI-managed webhook `HttpClient` now disables automatic cookies to avoid hidden state sharing between deliveries.
- Dispatcher delivery-scoped extension failures are isolated to the active delivery and progress through retry/dead-letter handling instead of stopping unrelated processing.
- The built-in HMAC-SHA256 signer now rejects signing secrets shorter than 32 bytes (256 bits) so weak keys fail closed before delivery.
- The built-in HMAC-SHA256 `v1` signature now authenticates the timestamp, webhook ID, event type, content type, and exact payload bytes with a versioned length-prefixed canonical envelope.
- `WebhookMessage.ContentType` is now normalized with the platform HTTP media-type parser when the message is created so the signed content type matches the serialized `Content-Type` header sent by the default transport.
- Custom webhook headers can no longer use transport-reserved routing or framing names such as `Host`, `Content-Length`, `Transfer-Encoding`, `Connection`, `TE`, `Trailer`, or `Upgrade`.
- Added `IWebhookRequestHeaderProvider` for send-time credential headers and reserved persisted `Authorization`/`Cookie` headers to avoid storing request credentials with durable deliveries.
