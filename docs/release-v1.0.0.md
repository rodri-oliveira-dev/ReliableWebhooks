# ReliableWebhooks v1.0.0 release

This document defines the release contract for the first public ReliableWebhooks package.

## Distribution

The official release workflow validates a single `.nupkg` and matching `.snupkg` and reuses those exact artifacts for publication. It can publish the primary package to GitHub Packages and NuGet.org, attach the validated artifacts to the GitHub Release, and publish symbols to NuGet.org when Trusted Publishing is enabled.

## External prerequisites

For NuGet.org publication, configure a Trusted Publishing policy for this repository, `.github/workflows/release.yml`, and the GitHub environment `release`. Configure the repository variable `NUGET_USER` with the authorized NuGet.org profile.

## Release procedure

Run the `Release` workflow from `main` with:

- `version`: `1.0.0`;
- `publish`: `true`.

The workflow validates SemVer, the exact `main` SHA, restore/build/tests, the v1.0.0 public API snapshot, package metadata, Source Link, symbols, clean package consumption, release manifest, checksums, registry identity, and artifact attestation.

The validated `.nupkg` is published to NuGet.org with `--no-symbols`; the validated `.snupkg` is published by its dedicated step. This prevents the primary package push from implicitly publishing symbols twice. No rebuild occurs in the publish job.

Tag `v1.0.0` must point to the exact validated commit. The GitHub Release is published only after the configured registry publication and verification steps succeed.

## Public API snapshot

`src/ReliableWebhooks/PublicApi.v1.0.0.txt` records the reviewed public API surface for the first stable release. CI and the release workflow compare the built assembly against this snapshot.

## v1.0.0 guarantees

- at-least-once delivery with a conforming durable `IWebhookDeliveryStore`;
- stable-ID enqueue semantics and lease-based ownership;
- bounded concurrent dispatch and graceful shutdown;
- retry/backoff/jitter with `Retry-After` support;
- HMAC-SHA256 signing with a versioned authenticated envelope;
- HTTPS-secure defaults and destination-policy support;
- structured logs, traces, and bounded metrics through standard .NET APIs;
- Microsoft DI and optional hosted-dispatcher integration.

## v1.0.0 limitations

- no built-in production durable store is included;
- `InMemoryWebhookDeliveryStore` is non-durable;
- exactly-once delivery is not guaranteed;
- receiver idempotency remains an application responsibility;
- production persistence adapters remain independent of the core package.
