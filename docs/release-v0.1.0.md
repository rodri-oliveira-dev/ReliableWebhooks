# ReliableWebhooks v0.1.0 release

This document records the release contract for the first public ReliableWebhooks package.

## Distribution

The official release workflow validates one immutable `.nupkg` and uses that validated artifact for the configured distribution targets:

- GitHub Packages using the scoped `GITHUB_TOKEN`;
- GitHub Releases as an attached artifact together with symbols, manifest, and checksums;
- optionally, NuGet.org using GitHub OIDC and NuGet Trusted Publishing when `NUGET_USER` is configured.

No long-lived NuGet API key is stored by the repository.

## External release prerequisites

GitHub Packages, the release tag, and the GitHub Release do not require `NUGET_USER`.

To include NuGet.org in the v0.1.0 publication and satisfy the NuGet.org distribution target from issue #13:

1. configure a NuGet.org Trusted Publishing policy that authorizes this repository, `.github/workflows/release.yml`, and the `release` GitHub environment;
2. configure the repository variable `NUGET_USER` with the NuGet.org profile authorized by that policy;
3. keep the GitHub `release` environment approvals or protection rules configured as required by the repository owner.

`NUGET_USER` is an optional NuGet-only opt-in. When it is absent, the workflow skips NuGet authentication/publication while preserving the tag, GitHub Packages publication, and GitHub Release. A dry run remains credential-free.

## Release procedure

From GitHub Actions, run the `Release` workflow on `main` with:

- `version`: `0.1.0`;
- `publish`: `true`.

The workflow validates SemVer, the exact `main` SHA, restore/build/tests, the v0.1.0 public API signature snapshot, package metadata, Source Link, symbols, a clean custom-store consumer, manifest, checksums, and artifact attestation. It then creates or revalidates tag `v0.1.0` at the exact validated SHA before any registry publication. NuGet.org is published only when `NUGET_USER` enables Trusted Publishing; GitHub Packages is always published for an official release. The GitHub Release is created and published only after the enabled registry publications succeed.

Re-running the same version is recoverable only when the existing tag resolves to the same validated SHA. Registry pushes use duplicate-safe behavior; a tag pointing to another SHA is rejected before external publication. This prevents a retry from associating an already-published package version with a newer commit.

## Public API snapshot

`src/ReliableWebhooks/PublicApi.v0.1.0.txt` records externally visible types and members, including member accessibility, modifiers, generic constraints, constants, parameter defaults, custom modifiers, and C# nullable reference annotations for returns, parameters, properties, fields, arrays, and nested generic arguments.

The snapshot is a source-compatibility gate for the v0.1.0 public surface. It does not currently claim to validate every possible source-level metadata detail, such as tuple element names or arbitrary custom attributes.

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
