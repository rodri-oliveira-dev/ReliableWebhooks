# ReliableWebhooks

[English](README.md) | **Português (Brasil)**

ReliableWebhooks é uma biblioteca .NET 10 para construção de entrega confiável de webhooks de saída em aplicações e serviços.

O projeto está sendo desenvolvido para a **v0.1.0**. A base atual já inclui contratos imutáveis de webhook, um contrato de armazenamento com coordenação de workers por lease e um transporte HTTP que executa exatamente uma tentativa de entrega por chamada, expõe resultados explícitos, permite configurar timeout por tentativa, limita a captura do corpo da resposta e disponibiliza metadados de `Retry-After` sem implementar retries internamente.

## Modelo de entrega

O roadmap da v0.1.0 tem como objetivo entrega **at-least-once**, e não exactly-once. Um store durável apoiado em banco de dados, política de retry, dispatcher concorrente, assinatura, integração com injeção de dependência e observabilidade são adicionados como capacidades separadas para manter contratos explícitos e testáveis.

Os receptores de webhook devem ser preparados para tolerar entregas duplicadas através de idempotência na aplicação.

## Requisitos

- .NET SDK 10
- Git

O repositório fixa a feature band esperada do SDK .NET 10 em `global.json` e usa restore NuGet em modo bloqueado para builds reproduzíveis.

## Build

```bash
dotnet tool restore
dotnet restore ReliableWebhooks.slnx --locked-mode
dotnet build ReliableWebhooks.slnx --configuration Release --no-restore
```

## Testes

```bash
dotnet test ReliableWebhooks.slnx --configuration Release --no-build
```

Os testes usam xUnit v3 sobre Microsoft Testing Platform.

## Pacote

```bash
dotnet pack src/ReliableWebhooks/ReliableWebhooks.csproj \
  --configuration Release \
  --no-build \
  --output artifacts/packages
```

O pacote inclui documentação XML, símbolos PDB portáveis, Source Link, README do pacote e SDK Package Validation.

O primeiro pacote público está planejado como `ReliableWebhooks` **v0.1.0**, após a conclusão dos principais componentes de confiabilidade do MVP.

## Componentes públicos atuais

### `WebhookMessage`

Representa os dados imutáveis do webhook de saída: identificador estável, tipo de evento, destino, bytes exatos do payload, content type e headers customizados opcionais.

### `IWebhookDeliveryStore`

Define a fronteira de persistência para entrega confiável. O contrato cobre enqueue idempotente e determinístico, claim atômico de trabalhos vencidos, leases renováveis e expirados, agendamento de retry, contagem de tentativas, persistência do último erro e transições terminais para sucesso, falha permanente e dead letter.

`InMemoryWebhookDeliveryStore` existe apenas para testes e exemplos. Ele é local ao processo e **não é durável**: todo o estado é perdido quando o processo encerra. Aplicações de produção que precisam de entrega confiável devem usar uma implementação durável de `IWebhookDeliveryStore`.

### `WebhookHttpTransport`

Executa uma tentativa HTTP `POST` e retorna um `WebhookDeliveryResult` estável. O transporte não executa retry, não aguarda entre tentativas e não agenda trabalho futuro.

Redirects automáticos devem ser desabilitados no `HttpClient` fornecido para impedir que uma chamada do transporte resulte silenciosamente em múltiplas requisições HTTP ou em mudança do método. Configure o handler principal com `AllowAutoRedirect = false` ao usar `HttpClientHandler`, `SocketsHttpHandler` ou `IHttpClientFactory`.

### Classificação de respostas

O classificador padrão trata:

- `2xx` como sucesso;
- `408`, `425`, `429` e `5xx` como falhas retryable;
- os demais status, incluindo redirects, como falhas permanentes.

O consumidor pode substituir essa regra através de `IWebhookHttpResponseClassifier`.

## Baseline de engenharia

O repositório mantém uma baseline voltada a biblioteca de produção com:

- nullable reference types e warnings como erros;
- analyzers .NET e analyzers de segurança;
- NuGet Audit e package lock files;
- verificação de formatação;
- CI com build, testes, cobertura, pack, validação do pacote, símbolos e Source Link;
- CodeQL e Dependency Review;
- análise opcional no SonarQube Cloud;
- validação de release e Trusted Publishing para NuGet.org via GitHub OIDC.

Consulte [docs/sonarqube-cloud.pt-BR.md](docs/sonarqube-cloud.pt-BR.md) para configurar o SonarQube Cloud.

## Processo de release

`.github/workflows/release.yml` valida candidatos de release em pull requests e oferece publicação manual e explícita a partir da `main`.

Na publicação oficial, o workflow exige uma versão semântica exata, valida o pacote e os artefatos de release e pode publicar no NuGet.org por Trusted Publishing quando a Repository Variable `NUGET_USER` estiver configurada.

## Estrutura do projeto

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

## Segurança

Use [SECURITY.md](SECURITY.md) para relatar vulnerabilidades de forma privada. Payloads, segredos de assinatura e outros dados sensíveis de entrega não devem ser expostos por logs ou diagnósticos por padrão.

## Contribuição

Consulte [CONTRIBUTING.md](CONTRIBUTING.md) para o fluxo esperado de desenvolvimento e pull requests. Mudanças relevantes para consumidores devem ser registradas na seção `Unreleased` do [CHANGELOG.md](CHANGELOG.md).
