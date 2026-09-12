# ReliableWebhooks v0.1.0 release

This document records the release contract for the first public ReliableWebhooks package.

## Distribution

The official release workflow validates one immutable `.nupkg` and publishes that same artifact to:

- NuGet.org using GitHub OIDC and NuGet Trusted Publishing;
- GitHub Packages using the scoped `GITHUB_TOKEN`;
- GitHub Releases as an attached artifact together with symbols, manifest, and checksums.

No long-lived NuGet API key is stored by the repository.

## External release prerequisites

Before running an official publication from `main`:

1. configure a NuGet.org Trusted Publishing policy that authorizes this repository, `.github/workflows/release.yml`, and the `release` GitHub environment;
2. configure the repository variable `NUGET_USER` with the NuGet.org profile authorized by that policy;
3. keep the GitHub `release` environment approvals or protection rules configured as required by the repository owner.

The workflow deliberately fails an official `publish=true` request when `NUGET_USER` is absent. A dry run remains credential-free.

## Release procedure

From GitHub Actions, run the `Release` workflow on `main` with:

- `version`: `0.1.0`;
- `publish`: `true`.

The workflow validates SemVer, the exact `main` SHA, restore/build/tests, the v0.1.0 public API snapshot, package metadata, Source Link, symbols, a clean custom-store consumer, manifest, checksums, and artifact attestation. It then publishes NuGet.org and GitHub Packages before creating and publishing tag `v0.1.0` and the GitHub Release.

Re-running the same version is recoverable only when any existing tag resolves to the same validated SHA. Registry pushes use duplicate-safe behavior; a tag pointing to another SHA is rejected.

## v0.1.0 guarantees

- at-least-once delivery when used with a conforming durable `IWebhookDeliveryStore`;
- stable-ID idempotent enqueue semantics;
- atomic claim/lease protocol with stale-owner rejection;
- retry/backoff/jitter and `Retry-After` support;
- bounded concurrent dispatch and graceful shutdown;
- HMAC-SHA256 request signing;
- structured logs, traces, and metrics through standard .NET APIs;
- Microsoft DI and optional hosted-dispatcher integration.

## v0.1.0 limitations

- no built-in production durable store is included;
- `InMemoryWebhookDeliveryStore` does not survive process restarts;
- exactly-once delivery is not guaranteed;
- receiver-side idempotency remains the receiver's responsibility;
- dead-letter replay/remediation is an application and operations responsibility;
- persistence adapters such as EF Core, Dapper, Redis, files, and document stores are optional and independent of the core package;
- secret lifecycle and rotation policy is not supplied by the core package.
