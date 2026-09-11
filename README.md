# ReliableWebhooks

**English** | [Português (Brasil)](README.pt-BR.md)

ReliableWebhooks is a .NET 10 library for building reliable outbound webhook delivery in applications and services.

The project is being developed toward **v0.1.0**. The current foundation includes immutable webhook contracts, a storage contract with lease-based worker coordination, and an HTTP transport that performs exactly one delivery attempt per call, exposes explicit delivery outcomes, supports configurable attempt timeouts, captures bounded response bodies, and surfaces `Retry-After` metadata without implementing retries internally.

## Delivery model

The v0.1.0 roadmap targets **at-least-once delivery**, not exactly-once delivery. A durable database-backed store, retry policy, concurrent dispatcher, signing, dependency-injection integration, and observability are being added as separate capabilities so their contracts remain explicit and testable.

Webhook receivers should ultimately be designed to tolerate duplicate deliveries through application-level idempotency.

## Requirements

- .NET SDK 10
- Git

The repository pins the expected .NET 10 SDK feature band in `global.json` and uses locked NuGet restore for reproducible builds.

## Build

```bash
dotnet tool restore
dotnet restore ReliableWebhooks.slnx --locked-mode
dotnet build ReliableWebhooks.slnx --configuration Release --no-restore
```

## Test

```bash
dotnet test ReliableWebhooks.slnx --configuration Release --no-build
```

Tests use xUnit v3 on Microsoft Testing Platform.

## Package

```bash
dotnet pack src/ReliableWebhooks/ReliableWebhooks.csproj \
  --configuration Release \
  --no-build \
  --output artifacts/packages
```

The package includes XML documentation, portable PDB symbols, Source Link metadata, the package README, and SDK package validation.

The first public package is planned as `ReliableWebhooks` **v0.1.0** after the MVP reliability primitives are complete.

## Current public building blocks

### `WebhookMessage`

Represents the immutable outbound webhook data: stable identifier, event type, destination, exact payload bytes, content type, and optional custom headers.

### `IWebhookDeliveryStore`

Defines the persistence boundary for reliable delivery. The contract supports deterministic idempotent enqueue, atomic claims of due work, renewable expiring leases, retry scheduling, attempt counts, last-error persistence, and terminal success/permanent-failure/dead-letter transitions.

`InMemoryWebhookDeliveryStore` is provided only for tests and samples. It is process-local and **non-durable**: all state is lost when the process exits. Production applications that require reliable delivery must use a durable implementation of `IWebhookDeliveryStore`.

### `WebhookHttpTransport`

Performs one HTTP `POST` attempt and returns a stable `WebhookDeliveryResult`. The transport intentionally does not retry, sleep, or schedule future work.

Automatic redirects must be disabled on the supplied `HttpClient` so one transport call cannot silently become multiple HTTP requests or change the request method. Configure the primary handler with `AllowAutoRedirect = false` when using `HttpClientHandler`, `SocketsHttpHandler`, or `IHttpClientFactory`.

### Response classification

The default classifier treats:

- `2xx` as success;
- `408`, `425`, `429`, and `5xx` as retryable failures;
- other status codes, including redirects, as permanent failures.

Consumers can replace the classifier through `IWebhookHttpResponseClassifier`.

## Engineering baseline

The repository keeps a production-oriented library baseline with:

- nullable reference types and warnings as errors;
- .NET analyzers and security analyzers;
- NuGet Audit and package lock files;
- formatting verification;
- CI build, tests, coverage, pack, package verification, symbols, and Source Link validation;
- CodeQL and Dependency Review;
- optional SonarQube Cloud analysis;
- release validation and NuGet.org Trusted Publishing through GitHub OIDC.

See [docs/sonarqube-cloud.md](docs/sonarqube-cloud.md) for SonarQube Cloud setup.

## Release process

`.github/workflows/release.yml` validates release candidates on pull requests and supports explicit manual publication from `main`.

For official publication, the workflow requires an exact semantic version, validates the package and release artifacts, and can publish to NuGet.org through Trusted Publishing when the `NUGET_USER` repository variable is configured.

## Project structure

```text
.
├── .github/workflows/
├── docs/
├── scripts/
├── src/ReliableWebhooks/
├── tests/ReliableWebhooks.Tests/
├── CHANGELOG.md
├── Directory.Build.props
├── Directory.Packages.props
├── ReliableWebhooks.slnx
└── global.json
```

## Security

Use [SECURITY.md](SECURITY.md) to report suspected vulnerabilities privately. Payloads, signing secrets, and other sensitive delivery data should never be exposed through logs or diagnostics by default.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the expected development and pull-request workflow. Consumer-relevant changes should be recorded under `Unreleased` in [CHANGELOG.md](CHANGELOG.md).
