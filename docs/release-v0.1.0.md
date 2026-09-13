# ReliableWebhooks v0.1.0 release

This document records the release contract for the first public ReliableWebhooks package.

## Distribution

The official release workflow validates one immutable `.nupkg` and its matching `.snupkg`, then uses those validated artifacts for the configured distribution targets:

- GitHub Packages using the scoped `GITHUB_TOKEN`;
- GitHub Releases as attached package, symbols, manifest, and checksum artifacts;
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

The `main` branch ruleset requires the stable gates named `CI`, `CodeQL`, and `Dependency Review` to pass before merge. Release candidates must come from a `main` commit that satisfied those enforced checks. The release dry-run workflow is path-filtered to release/package-relevant files and is therefore not configured as a required status check; when it runs, its failures are release blockers for the affected change.

From GitHub Actions, run the `Release` workflow on `main` with:

- `version`: `0.1.0`;
- `publish`: `true`.

The workflow validates SemVer, the exact `main` SHA, restore/build/tests, the v0.1.0 public API signature snapshot, package metadata, Source Link, symbol package identity/version, a clean custom-store consumer, manifest, checksums, and artifact attestation. It then creates or revalidates tag `v0.1.0` at the exact validated SHA before any registry publication. NuGet.org is published only when `NUGET_USER` enables Trusted Publishing; GitHub Packages is always published for an official release. The GitHub Release is created and published only after the enabled registry publications succeed.

`release-manifest.json` records the package filename/SHA-256 and symbol-package filename/SHA-256 for the validated commit. `SHA256SUMS` is deterministic and contains one line each for the `.nupkg`, `.snupkg`, and `release-manifest.json` in that order. The publish job downloads the previously validated artifact set, re-verifies both package hashes through `scripts/release-candidate.cs`, attests the `.nupkg`, `.snupkg`, manifest, and checksum file, and uploads only that verified artifact set to the GitHub Release. The registry publication path continues to push the primary `.nupkg` only; no rebuild occurs in the publish job.

Before any registry push, `scripts/verify-registry-package.cs` checks the NuGet v3 package-base-address endpoint for the requested PackageId/version. If the registry has no package, the workflow records `missing` and publishes the already validated `.nupkg`. If the registry already has the package, the workflow records `exact` only when the remote package content matches the validated candidate while allowing registry-added repository signature metadata. A content mismatch, registry authentication failure, or unavailable registry fails the release before the GitHub Release is created or changed. After each enabled registry publication path, the same script verifies that the registry now serves the validated package contents; this also makes `--skip-duplicate` safe for retry/race recovery because a duplicate is accepted only after identity is proven.

Re-running the same version is recoverable only when the existing tag resolves to the same validated SHA. Registry pushes use duplicate-safe behavior; a tag pointing to another SHA is rejected before external publication. This prevents a retry from associating an already-published package version with a newer commit.

## Release candidate gate

The v0.1.0 release candidate must be validated from the final merged `main` commit before publication. Feature branches and pull requests may generate local packages and release-candidate manifests for review, but they must not create the official `v0.1.0` tag, GitHub Release, NuGet.org publication, or GitHub Packages publication.

Before running the official publish workflow, verify that:

- roadmap reliability, security, release-integrity, resource-limit, and observability-cardinality blockers are merged;
- `CI`, `CodeQL`, and `Dependency Review` are green on the protected `main` commit;
- `dotnet tool restore`, locked restore, formatting, Release build, all tests, coverage, package validation, public API validation, sample E2E, clean consumer validation, release manifest/checksum generation, and release-candidate verification pass for the same SHA;
- the generated `.nupkg`, `.snupkg`, `release-manifest.json`, and `SHA256SUMS` are the exact artifacts consumed by the publish job;
- the NuGet.org Trusted Publishing policy and `NUGET_USER` repository variable are configured when NuGet.org publication is required;
- no long-lived NuGet API key, signing secret, credential-bearing webhook URL, payload, authorization header, cookie, or other delivery secret is committed or emitted through release artifacts.

If NuGet.org Trusted Publishing is not configured, the repository-side release candidate can still validate, tag, publish to GitHub Packages, and create a GitHub Release, but issue #13 must remain only related rather than closed for the NuGet.org publication target until that external prerequisite is complete and the package is published.

## Public API snapshot

`src/ReliableWebhooks/PublicApi.v0.1.0.txt` records externally visible types and members, including member accessibility, modifiers, generic constraints, constants, parameter defaults, custom modifiers, and C# nullable reference annotations for returns, parameters, properties, fields, arrays, and nested generic arguments.

The snapshot is a source-compatibility gate for the v0.1.0 public surface. It does not currently claim to validate every possible source-level metadata detail, such as tuple element names or arbitrary custom attributes.

## v0.1.0 guarantees

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

## v0.1.0 limitations

- no built-in production durable store is included;
- `InMemoryWebhookDeliveryStore` does not survive process restarts;
- exactly-once delivery is not guaranteed;
- receiver-side idempotency remains the receiver's responsibility;
- dead-letter replay/remediation is an application and operations responsibility;
- persistence adapters such as EF Core, Dapper, Redis, files, and document stores are optional and independent of the core package;
- secret lifecycle and rotation policy is not supplied by the core package;
- encryption at rest, access control, retention, deletion, backup protection, and regulatory handling for persisted webhook payloads, destinations, and metadata remain responsibilities of the consumer-selected store/application boundary;
- official publication to NuGet.org requires an external NuGet Trusted Publishing policy and repository variable configuration that cannot be represented entirely in the git tree.
