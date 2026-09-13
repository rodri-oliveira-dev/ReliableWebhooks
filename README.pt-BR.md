# ReliableWebhooks

[![CI](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml)
[![Release](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml/badge.svg)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/ReliableWebhooks.svg)](https://www.nuget.org/packages/ReliableWebhooks/)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

[English](README.md) | **Português (Brasil)**

ReliableWebhooks é uma biblioteca .NET 10 para entrega confiável de webhooks de saída.

A primeira release pública é a **v1.0.0**. Ela fornece contrato de persistência independente de tecnologia, dispatch concorrente limitado, ownership por lease, retries configuráveis, assinatura HMAC-SHA256, padrões HTTP seguros, observabilidade padrão do .NET e integração com DI/Generic Host.

## Por que ReliableWebhooks?

Enviar uma requisição HTTP é simples; entregá-la de forma confiável diante de falhas transitórias, reinícios de processo, workers concorrentes, janelas de retry e cenários de entrega duplicada não é. ReliableWebhooks mantém essas responsabilidades explícitas e substituíveis, em vez de escondê-las em um loop de background opaco.

Os principais recursos incluem:

- identidades estáveis e semântica de enqueue segura contra duplicidade;
- `IWebhookDeliveryStore` como porta de persistência substituível;
- dispatch concorrente limitado com leases renováveis;
- transporte HTTP de uma tentativa com classificação de resposta;
- backoff exponencial, jitter e suporte a `Retry-After`;
- assinatura HMAC-SHA256 sobre um envelope autenticado e versionado;
- HTTPS por padrão, isolamento de redirects/cookies e política de destino;
- logs estruturados, traces e métricas limitadas por meio das APIs padrão do .NET;
- injeção de dependência e hosted dispatcher opcional.

## Instalação

O pacote tem como target `net10.0`.

```bash
dotnet add package ReliableWebhooks --version 1.0.0
```

ou:

```xml
<PackageReference Include="ReliableWebhooks" Version="1.0.0" />
```

## Começando

```csharp
using System.Text.Json;
using ReliableWebhooks;

byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
{
    OrderId = 123,
    Status = "created",
});

var message = new WebhookMessage(
    id: Guid.NewGuid().ToString("N"),
    eventType: "order.created",
    destination: new Uri("https://example.com/webhooks"),
    payload: payload,
    contentType: "application/json");

IWebhookDeliveryStore store = new InMemoryWebhookDeliveryStore();
await store.EnqueueAsync(message, DateTimeOffset.UtcNow);
```

`InMemoryWebhookDeliveryStore` **não é durável**. Ele é destinado a testes, samples e desenvolvimento local. Durabilidade entre reinícios em produção exige um `IWebhookDeliveryStore` durável e aderente ao contrato, fornecido pela aplicação.

## Injeção de dependência

```csharp
services.AddSingleton<IWebhookDeliveryStore, MyDurableWebhookStore>();

ReliableWebhooksBuilder webhooks = services.AddReliableWebhooks(options =>
{
    options.Dispatcher.MaxConcurrency = 8;
    options.Dispatcher.LeaseDuration = TimeSpan.FromMinutes(2);
    options.MessageLimits.MaxPayloadBytes = 1024 * 1024;
});

webhooks.AddHostedDispatcher();
```

`AddHostedDispatcher()` é opt-in. A integração depende de `Microsoft.Extensions.*` e não exige ASP.NET Core.

## Garantias de entrega

ReliableWebhooks foi projetado para **at-least-once delivery**, não exactly-once. Por isso, receivers devem ser idempotentes.

Um store durável aderente ao contrato é responsável por enqueue durável, claims atômicos, ownership por lease, rejeição de owner obsoleto, agendamento de retries e persistência dentro da fronteira de durabilidade que anuncia.

O pacote core não exige EF Core, Dapper, Redis, arquivos, banco relacional ou outra tecnologia específica de persistência.

## Padrões seguros

A integração padrão exige destinos HTTPS, desabilita redirects e cookies automáticos, valida metadados de protocolo, limita tamanhos de mensagens de saída e suporta autorização de destino quando a aplicação aceita URLs de webhook não confiáveis.

O signer nativo usa HMAC-SHA256 com envelope canônico versionado que autentica timestamp, ID estável do webhook, tipo de evento, content type e bytes exatos do payload. Aplicações de produção devem proteger os dados persistidos conforme seu threat model.

Consulte [Uso em produção](docs/production-usage.md) para orientações completas sobre segurança, persistência, assinatura, retry e operação.

## Observabilidade

ReliableWebhooks emite telemetria pelas APIs padrão do .NET e não exige backend específico.

```csharp
ReliableWebhooksInstrumentation.ActivitySourceName // "ReliableWebhooks"
ReliableWebhooksInstrumentation.MeterName          // "ReliableWebhooks"
```

As métricas nativas evitam cardinalidade ilimitada de tipo de evento por padrão. A aplicação pode integrar o activity source e o meter com a stack de OpenTelemetry/exporter que preferir.

## Limites da v1.0.0

A primeira release pública mantém a persistência intencionalmente independente de tecnologia:

- nenhum store durável de produção é incluído no core;
- `InMemoryWebhookDeliveryStore` permanece não durável;
- exactly-once delivery não é garantido;
- idempotência no receiver continua sendo responsabilidade da aplicação;
- adapters de persistência de produção podem ser implementados de forma independente do pacote core.

## Documentação

- [Uso em produção](docs/production-usage.md)
- [Contrato de persistência](docs/persistence.pt-BR.md)
- [Contrato da release v1.0.0](docs/release-v1.0.0.md)
- [SonarQube Cloud](docs/sonarqube-cloud.pt-BR.md)
- [Contribuindo](CONTRIBUTING.md)
- [Changelog](CHANGELOG.md)

## Suporte e licença

Use [GitHub Issues](https://github.com/rodri-oliveira-dev/ReliableWebhooks/issues) para bugs, dúvidas e discussão de funcionalidades. Para questões de segurança, siga [SECURITY.md](SECURITY.md).

ReliableWebhooks é licenciado sob a [MIT License](LICENSE).
