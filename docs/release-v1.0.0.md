# ReliableWebhooks v1.0.0 release

This document records the release contract for the first public ReliableWebhooks package.

Português (Brasil): [`release-v1.0.0.pt-BR.md`](release-v1.0.0.pt-BR.md).

## Distribution

The official release workflow validates one immutable release candidate containing:

- `ReliableWebhooks.<version>.nupkg`;
- `ReliableWebhooks.<version>.snupkg`;
- `release-manifest.json`;
- `SHA256SUMS`.

The validated artifact is then reused without rebuilding for every distribution target:

- NuGet.org package and symbol-package publication through GitHub OIDC and NuGet Trusted Publishing;
- GitHub Packages publication with the scoped `GITHUB_TOKEN`;
- GitHub Release attachments for the package, symbols, manifest, and checksums;
- GitHub artifact attestations for the same release files.

No long-lived NuGet API key is stored by the repository.

## External Release Prerequisites

Official publication requires:

1. a NuGet.org Trusted Publishing policy that authorizes this repository, `.github/workflows/release.yml`, and the `release` GitHub environment;
2. repository variable `NUGET_USER` containing the NuGet.org profile authorized by that policy;
3. the GitHub `release` environment approvals or protection rules required by the repository owner.

`NUGET_USER` is a repository variable, not a secret. If it is absent during an official release, the NuGet publication job fails with a clear configuration error and the GitHub Release is not published.

## Job Sequence

The `Release` workflow has one global release concurrency group with `cancel-in-progress: false`. Release-state mutations wait for each other and are not canceled halfway through.

Pull requests that touch release/package-relevant files run a dry validation path through `build-and-pack` only. Manual `workflow_dispatch` runs are official release requests and must be started from `main` with a required `version` input that does not include the `v` prefix.

Official release jobs run in this order:

1. `build-and-pack`: validates SemVer and branch, restores locked dependencies, verifies formatting, builds Release, runs tests, validates `PublicApi.v1.0.0.txt`, packs, validates package metadata, symbols and Source Link, runs the clean consumer/custom `IWebhookDeliveryStore` validation, writes the release manifest and checksums, and uploads one immutable release candidate artifact.
2. `ensure-release-tag`: creates `v<version>` only for the validated SHA. If the tag already points to the same SHA it is accepted; if it points anywhere else the release fails. Existing tags are never moved.
3. `publish-nuget`: enters the `release` environment, exchanges GitHub OIDC for a temporary NuGet API key through `NuGet/login`, downloads the validated artifact, verifies manifest/checksums, and publishes only those downloaded files.
4. `publish-github-packages`: enters the `release` environment, downloads the same validated artifact, verifies manifest/checksums, and publishes the validated package to GitHub Packages without rebuilding.
5. `github-release`: runs only after both registries succeed, verifies the same artifact, creates or resumes a draft release, attaches the validated files, generates attestations, and publishes the GitHub Release.

## Idempotent Publication

Publication is handled by `scripts/publish-release-package.cs` so that the YAML stays focused on orchestration.

For NuGet.org, the helper checks the NuGet v3 flat-container package URL before pushing:

- if `ReliableWebhooks <version>` is absent, the helper pushes the validated `.nupkg` with implicit symbol publication disabled;
- if the package already exists, the helper downloads it and compares its content with the local artifact;
- repository-signing metadata added by NuGet.org is ignored during the content comparison;
- if the existing package differs, publication fails closed;
- after a new push, the helper waits for flat-container convergence for up to 60 attempts at 10-second intervals;
- `404` during that window is treated as pending indexing and logged before retrying;
- `200` must download and validate the package content;
- any unexpected response fails closed;
- the validated `.snupkg` is submitted in its dedicated step after the primary package identity is proven.

For GitHub Packages, the helper supports safe reruns:

- if the version is absent, it pushes the validated `.nupkg`;
- if the version already exists, it downloads the remote artifact and accepts it only when the content matches;
- if the content diverges, publication fails;
- after a new push, the helper waits for package visibility and validates the visible artifact before continuing.

This makes a rerun after partial publication safe: already-completed registry work is verified instead of blindly skipped, while conflicting package content stops the release before announcement.

## Release Candidate Integrity

`release-manifest.json` records the package filename/SHA-256 and symbol-package filename/SHA-256 for the validated commit. `SHA256SUMS` is deterministic and contains one line each for the `.nupkg`, `.snupkg`, and `release-manifest.json` in that order.

Every publishing job downloads the artifact produced by `build-and-pack` and verifies it through `scripts/release-candidate.cs` before doing registry or release work. No package is rebuilt between validation and publication.

## Release Candidate Gate

The v1.0.0 release candidate must be validated from the final merged `main` commit before publication. Feature branches and pull requests may generate local packages and release-candidate manifests for review, but they must not create the official `v1.0.0` tag, publish to NuGet.org, publish to GitHub Packages, or publish a GitHub Release.

Before running the official workflow, verify that:

- `CI`, `CodeQL`, and `Dependency Review` are green on the protected `main` commit;
- `dotnet tool restore`, locked restore, formatting, Release build, all tests, coverage, package validation, public API validation, sample E2E, clean consumer validation, release manifest/checksum generation, and release-candidate verification pass for the same SHA;
- the generated `.nupkg`, `.snupkg`, `release-manifest.json`, and `SHA256SUMS` are the exact artifacts consumed by the publishing jobs;
- the NuGet.org Trusted Publishing policy, `NUGET_USER` repository variable, and GitHub `release` environment are configured;
- no long-lived NuGet API key, signing secret, credential-bearing webhook URL, payload, authorization header, cookie, or other delivery secret is committed or emitted through release artifacts.

## Public API Snapshot

`src/ReliableWebhooks/PublicApi.v1.0.0.txt` records externally visible types and members, including member accessibility, modifiers, generic constraints, constants, parameter defaults, custom modifiers, and C# nullable reference annotations for returns, parameters, properties, fields, arrays, and nested generic arguments.

The snapshot is a source-compatibility gate for the v1.0.0 public surface. It does not currently claim to validate every possible source-level metadata detail, such as tuple element names or arbitrary custom attributes.

## v1.0.0 Guarantees

- at-least-once delivery when used with a conforming durable `IWebhookDeliveryStore`;
- stable-ID idempotent enqueue semantics;
- atomic claim/lease protocol with stale-owner rejection, active lease renewal, and documented authoritative-clock expectations;
- retry/backoff/jitter with server-provided `Retry-After` values honored when they delay later than local backoff;
- bounded concurrent dispatch, graceful shutdown, and delivery-scoped poison-message isolation;
- HMAC-SHA256 request signing with a versioned authenticated envelope, generated metadata binding, and 256-bit minimum secret strength;
- HTTPS-required default HTTP delivery, disabled redirects/cookies in the DI-managed client, request-log suppression, SSRF destination-policy hooks, and reserved transport-header protection;
- validated custom headers, stable IDs, event types, content types, message resource limits, and bounded/default-safe metric cardinality;
- structured logs, traces, and metrics through standard .NET APIs without default payload, credential, destination-secret, signature, or high-cardinality metric leakage;
- Microsoft DI and optional hosted-dispatcher integration with singleton, scoped, and transient store registrations supported.

## v1.0.0 Limitations

- no built-in production durable store is included;
- `InMemoryWebhookDeliveryStore` does not survive process restarts;
- exactly-once delivery is not guaranteed;
- receiver-side idempotency remains the receiver's responsibility;
- dead-letter replay/remediation is an application and operations responsibility;
- persistence adapters such as EF Core, Dapper, Redis, files, and document stores are optional and independent of the core package;
- secret lifecycle and rotation policy is not supplied by the core package;
- encryption at rest, access control, retention, deletion, backup protection, and regulatory handling for persisted webhook payloads, destinations, and metadata remain responsibilities of the consumer-selected store/application boundary;
- official publication to NuGet.org requires external NuGet Trusted Publishing policy, repository variable, and GitHub environment configuration that cannot be represented entirely in the git tree.
