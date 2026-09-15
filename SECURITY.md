# Security Policy

## Supported Versions

Security fixes are prioritized for the latest released version and the current `main` branch.

Older versions may receive fixes when the impact is high and a backport is practical, but long-term support is not guaranteed by default.

## Reporting a Vulnerability

Please privately report suspected vulnerabilities instead of opening a public issue.

Preferred reporting channels are:

- GitHub private vulnerability reporting or Security Advisories, when enabled for the repository;
- a maintainer contact channel documented by the project, when private GitHub reporting is not available.

Include enough detail to help maintainers reproduce and assess the issue:

- affected package version or commit;
- affected platform or runtime, when relevant;
- a minimal reproduction or proof of concept;
- expected impact and any known mitigations.

## Automated Security Baseline

ReliableWebhooks treats webhook payloads and delivery metadata as untrusted external input, so repository security uses layered controls:

- CodeQL runs C# analysis with the `security-extended` query suite using the same locked restore and Release build contract used by the repository;
- Dependency Review evaluates dependency changes introduced by pull requests;
- Dependabot maintains NuGet packages, the .NET SDK declared by `global.json`, and GitHub Actions references;
- SonarQube Cloud and CI provide complementary static analysis, build, test, and package validation.

The `security-extended` suite intentionally trades a small amount of precision for broader security coverage. Findings must be triaged on their technical merit rather than suppressed solely to keep automation green.

.NET SDK updates are proposed as dedicated Dependabot pull requests so compiler/toolchain changes remain explicit and reviewable. Major SDK upgrades require compatibility review before merge.

## Triage Expectations

Maintainers should acknowledge and triage reports as soon as reasonably possible. Response and fix timelines depend on severity, maintainer availability, release complexity, and coordinated disclosure needs.

Sensitive details should remain private until a fix or mitigation is available, unless disclosure is legally required or already public.
