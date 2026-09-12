from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    target = Path(path)
    text = target.read_text(encoding="utf-8")
    if old not in text:
        raise SystemExit(f"Expected marker not found in {path}: {old[:100]!r}")
    target.write_text(text.replace(old, new, 1), encoding="utf-8", newline="\n")


BADGES = """[![CI](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml)
[![Release](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml/badge.svg)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/ReliableWebhooks.svg)](https://www.nuget.org/packages/ReliableWebhooks/)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
"""

replace_once("README.md", "# ReliableWebhooks\n", "# ReliableWebhooks\n\n" + BADGES)
replace_once(
    "README.md",
    "> **Status:** v0.1.0 is under development. The first public NuGet package has not been released yet. The current version provides the reliability primitives, concurrent dispatcher, signing, observability, dependency injection, and optional hosted-dispatcher integration described below. The core defines a technology-agnostic `IWebhookDeliveryStore` contract and conformance semantics; production applications provide the durable store that fits their persistence stack.",
    "> **v0.1.0 scope:** the first public release line provides the reliable-delivery engine, technology-agnostic `IWebhookDeliveryStore` contract, retries, leasing, signing, observability, dependency injection, hosted dispatching, and production guidance. Production restart durability is supplied by a consumer-provided conforming durable store.",
)
replace_once(
    "README.md",
    "The planned NuGet package ID is `ReliableWebhooks` and the library targets `net10.0`.\n\nThe first public package has not been published yet. After the v0.1.0 release, installation will be:",
    "The NuGet package ID is `ReliableWebhooks` and the library targets `net10.0`. Install v0.1.0 with:",
)
replace_once(
    "README.md",
    "## Quick start\n",
    """## v0.1.0 boundaries

The first public release intentionally keeps persistence technology-agnostic:

- no production durable store is bundled; applications register a conforming `IWebhookDeliveryStore`;
- `InMemoryWebhookDeliveryStore` is non-durable and intended only for tests, samples, and local development;
- delivery is at-least-once when backed by a conforming durable store, so receivers must be idempotent;
- exactly-once delivery, receiver-side idempotency, automatic dead-letter replay, and secret-rotation policy are not guaranteed;
- EF Core, Dapper, ADO.NET, Redis, files, document databases, and other persistence technologies are optional consumer choices, not core dependencies.

See [`docs/production-usage.md`](docs/production-usage.md) for production integration and [`docs/release-v0.1.0.md`](docs/release-v0.1.0.md) for release/distribution details.

## Quick start
""",
)

replace_once("README.pt-BR.md", "# ReliableWebhooks\n", "# ReliableWebhooks\n\n" + BADGES)
replace_once(
    "README.pt-BR.md",
    "> **Status:** a v0.1.0 está em desenvolvimento. O primeiro pacote público no NuGet ainda não foi lançado. A versão atual fornece os componentes de confiabilidade, dispatcher concorrente, assinatura, observabilidade, injeção de dependência e integração opcional com hosted dispatcher descritos abaixo. O core define um contrato `IWebhookDeliveryStore` independente de tecnologia e suas semânticas de conformidade; aplicações de produção fornecem o store durável adequado à sua stack de persistência.",
    "> **Escopo da v0.1.0:** a primeira linha pública fornece o engine de entrega confiável, contrato `IWebhookDeliveryStore` independente de tecnologia, retries, leases, assinatura, observabilidade, injeção de dependência, hosted dispatcher e orientação de produção. A durabilidade entre reinícios é fornecida por um store durável e aderente ao contrato escolhido pela aplicação.",
)
replace_once(
    "README.pt-BR.md",
    "O Package ID planejado para o NuGet é `ReliableWebhooks` e a biblioteca tem como target `net10.0`.\n\nO primeiro pacote público ainda não foi publicado. Após a release v0.1.0, a instalação será:",
    "O Package ID no NuGet é `ReliableWebhooks` e a biblioteca tem como target `net10.0`. Instale a v0.1.0 com:",
)
replace_once(
    "README.pt-BR.md",
    "## Começando\n",
    """## Limites da v0.1.0

A primeira release pública mantém a persistência intencionalmente independente de tecnologia:

- nenhum store durável de produção é incluído; a aplicação registra um `IWebhookDeliveryStore` aderente ao contrato;
- `InMemoryWebhookDeliveryStore` não é durável e serve apenas para testes, samples e desenvolvimento local;
- a entrega é at-least-once quando apoiada por um store durável aderente, portanto receivers devem ser idempotentes;
- exactly-once, idempotência no receiver, replay automático de dead letters e política de rotação de secrets não são garantidos;
- EF Core, Dapper, ADO.NET, Redis, arquivos, bancos de documentos e outras tecnologias são escolhas opcionais do consumidor, não dependências do core.

Consulte [`docs/production-usage.md`](docs/production-usage.md) para integração de produção e [`docs/release-v0.1.0.md`](docs/release-v0.1.0.md) para detalhes da release/distribuição.

## Começando
""",
)

replace_once(
    "CHANGELOG.md",
    "## [Unreleased]\n\n### Added",
    "## [Unreleased]\n\n## [0.1.0] - 2026-09-12\n\n### Added",
)
replace_once(
    "CHANGELOG.md",
    "- English and Brazilian Portuguese project documentation.",
    "- English and Brazilian Portuguese project documentation.\n- v0.1.0 release hardening with an explicit public API snapshot, clean package-consumer validation, dual NuGet.org/GitHub Packages publication, and documented release gates.",
)

# Package validation: metadata, dependency set and packaged README.
package_verifier = Path("scripts/verify-package.cs")
text = package_verifier.read_text(encoding="utf-8")
text = text.replace(
    'AssertEqual("MIT", license?.Value, "LicenseExpression");\nAssertDeprecatedMetadataAbsent(metadata, ns);',
    'AssertEqual("MIT", license?.Value, "LicenseExpression");\nAssertEqual("https://github.com/rodri-oliveira-dev/ReliableWebhooks", metadata.Element(ns + "projectUrl")?.Value, "PackageProjectUrl");\nAssertDependencies(metadata, ns);\nAssertDeprecatedMetadataAbsent(metadata, ns);',
    1,
)
text = text.replace(
    'var repositoryCommit = repository?.Attribute("commit")?.Value;\n',
    'var repositoryCommit = repository?.Attribute("commit")?.Value;\nAssertEqual("https://github.com/rodri-oliveira-dev/ReliableWebhooks", repositoryUrl, "RepositoryUrl");\n',
    1,
)
text = text.replace(
    'var assemblyEntry = packageArchive.GetEntry("lib/net10.0/ReliableWebhooks.dll")',
    '''var readmeEntry = packageArchive.GetEntry("README.md")
    ?? throw new InvalidOperationException("README.md não encontrado no .nupkg.");
using (var readmeReader = new StreamReader(readmeEntry.Open()))
{
    string packagedReadme = readmeReader.ReadToEnd();
    AssertContains(packagedReadme, "dotnet add package ReliableWebhooks --version 0.1.0", "Package README installation command");
    AssertContains(packagedReadme, "InMemoryWebhookDeliveryStore", "Package README durability warning");
    AssertContains(packagedReadme, "not durable", "Package README durability warning");
}

var assemblyEntry = packageArchive.GetEntry("lib/net10.0/ReliableWebhooks.dll")''',
    1,
)
helper_marker = "static void AssertDeprecatedMetadataAbsent(XElement metadata, XNamespace ns)\n"
helpers = '''static void AssertContains(string value, string expected, string field)
{
    if (!value.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{field}: expected content '{expected}' was not found.");
    }
}

static void AssertDependencies(XElement metadata, XNamespace ns)
{
    HashSet<string> expected = new(StringComparer.Ordinal)
    {
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Hosting.Abstractions",
        "Microsoft.Extensions.Http",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Options",
    };

    HashSet<string> actual = metadata
        .Descendants(ns + "dependency")
        .Select(static dependency => dependency.Attribute("id")?.Value)
        .Where(static id => !string.IsNullOrWhiteSpace(id))
        .Select(static id => id!)
        .ToHashSet(StringComparer.Ordinal);

    if (!actual.SetEquals(expected))
    {
        string expectedText = string.Join(", ", expected.OrderBy(static item => item, StringComparer.Ordinal));
        string actualText = string.Join(", ", actual.OrderBy(static item => item, StringComparer.Ordinal));
        throw new InvalidOperationException(
            $"Package dependencies differ from the reviewed v0.1.0 set. Expected: {expectedText}. Actual: {actualText}.");
    }
}

'''
if helper_marker not in text:
    raise SystemExit("verify-package helper marker not found")
text = text.replace(helper_marker, helpers + helper_marker, 1)
package_verifier.write_text(text, encoding="utf-8", newline="\n")

# CI: public API, Trusted Publishing gate and clean custom-store consumer.
replace_once(
    ".github/workflows/ci.yml",
    "      - name: Compilar\n        run: dotnet build ReliableWebhooks.slnx --configuration Release --no-restore\n\n      - name: Executar sample end-to-end",
    """      - name: Compilar
        run: dotnet build ReliableWebhooks.slnx --configuration Release --no-restore

      - name: Validar snapshot da API pública v0.1.0
        run: dotnet run --file scripts/verify-public-api.cs -- src/ReliableWebhooks/bin/Release/net10.0/ReliableWebhooks.dll src/ReliableWebhooks/PublicApi.v0.1.0.txt

      - name: Validar gate de Trusted Publishing
        shell: bash
        run: |
          set -euo pipefail
          output="$(mktemp)"
          SHOULD_PUBLISH=false GITHUB_OUTPUT="$output" bash scripts/resolve-nuget-publishing.sh
          grep -Fq 'nuget_publishing_enabled=false' "$output"
          if SHOULD_PUBLISH=true NUGET_USER= GITHUB_OUTPUT="$output" bash scripts/resolve-nuget-publishing.sh; then
            echo 'Publish request without NUGET_USER should fail.' >&2
            exit 1
          fi
          : > "$output"
          SHOULD_PUBLISH=true NUGET_USER=rodri-oliveira-dev GITHUB_OUTPUT="$output" bash scripts/resolve-nuget-publishing.sh
          grep -Fq 'nuget_publishing_enabled=true' "$output"

      - name: Executar sample end-to-end""",
)
ci_path = Path(".github/workflows/ci.yml")
ci = ci_path.read_text(encoding="utf-8")
start = ci.index("      - name: Validar consumo do pacote local\n")
end = ci.index("      - name: Verificar baseline de governança\n", start)
ci = ci[:start] + '''      - name: Validar consumo do pacote local com store customizado
        shell: bash
        run: |
          project="src/ReliableWebhooks/ReliableWebhooks.csproj"
          version="$(dotnet msbuild "$project" -nologo -getProperty:PackageVersion | tr -d '\\r' | sed '/^[[:space:]]*$/d' | tail -n 1)"
          bash scripts/verify-package-consumer.sh artifacts/packages "$version"

''' + ci[end:]
ci_path.write_text(ci, encoding="utf-8", newline="\n")

# Release: public API/consumer validation, mandatory OIDC NuGet, dual registries,
# and tag/GitHub Release only after registry publication succeeds.
release_path = Path(".github/workflows/release.yml")
release = release_path.read_text(encoding="utf-8")
release = release.replace(
    "      - scripts/verify-package.cs\n",
    "      - scripts/verify-package.cs\n      - scripts/verify-package-consumer.sh\n      - scripts/verify-public-api.cs\n      - src/ReliableWebhooks/PublicApi.v0.1.0.txt\n      - docs/release-v0.1.0.md\n",
    1,
)
release = release.replace(
    "      - name: Run tests\n        run: dotnet test ReliableWebhooks.slnx --configuration Release --no-build\n",
    "      - name: Verify public API snapshot\n        run: dotnet run --file scripts/verify-public-api.cs -- src/ReliableWebhooks/bin/Release/net10.0/ReliableWebhooks.dll src/ReliableWebhooks/PublicApi.v0.1.0.txt\n\n      - name: Run tests\n        run: dotnet test ReliableWebhooks.slnx --configuration Release --no-build\n",
    1,
)
release = release.replace(
    "      - name: Generate release manifest and checksums\n",
    "      - name: Validate clean consumer with custom store\n        env:\n          RELEASE_VERSION: ${{ steps.release-metadata.outputs.version }}\n        run: bash scripts/verify-package-consumer.sh artifacts/release \"$RELEASE_VERSION\"\n\n      - name: Generate release manifest and checksums\n",
    1,
)
release = release.replace(
    "      artifact-metadata: write\n",
    "      artifact-metadata: write\n      packages: write\n",
    1,
)
release = release.replace(
    '''          if [[ "$NUGET_PUBLISHING_ENABLED" != "true" ]]; then
            echo "NuGet publication is skipped because NUGET_USER is not configured."
          fi
''',
    '''          if [[ "$NUGET_PUBLISHING_ENABLED" != "true" ]]; then
            echo "Official publication requires NuGet.org Trusted Publishing (NUGET_USER)." >&2
            exit 2
          fi
''',
    1,
)

draft_start = release.index("      - name: Create or resume draft GitHub Release\n")
nuget_auth_start = release.index("      - name: Authenticate to NuGet.org with Trusted Publishing\n", draft_start)
draft_block = release[draft_start:nuget_auth_start]
release = release[:draft_start] + release[nuget_auth_start:]

publish_release_marker = "      - name: Publish GitHub Release\n"
publish_release_index = release.index(publish_release_marker)
github_packages = '''      - name: Publish package to GitHub Packages
        env:
          GITHUB_PACKAGES_TOKEN: ${{ github.token }}
          PACKAGE_ID: ${{ needs.build.outputs.package-id }}
          RELEASE_VERSION: ${{ needs.build.outputs.version }}
          GITHUB_PACKAGES_SOURCE: https://nuget.pkg.github.com/${{ github.repository_owner }}/index.json
        run: >-
          dotnet nuget push
          "artifacts/release/${PACKAGE_ID}.${RELEASE_VERSION}.nupkg"
          --source "$GITHUB_PACKAGES_SOURCE"
          --api-key "$GITHUB_PACKAGES_TOKEN"
          --skip-duplicate

'''
release = release[:publish_release_index] + github_packages + draft_block + release[publish_release_index:]
release_path.write_text(release, encoding="utf-8", newline="\n")
