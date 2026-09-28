# ReliableWebhooks

[![CI](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml)
[![Release](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml/badge.svg)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/ReliableWebhooks.svg)](https://www.nuget.org/packages/ReliableWebhooks/)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/LICENSE)

[English](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/README.md) | **Português (Brasil)**

ReliableWebhooks é uma biblioteca .NET 10 para entrega confiável de webhooks de saída. Ela fornece primitivas explícitas para enqueue durável, dispatch baseado em leases, concorrência limitada, entrega HTTP, classificação e agendamento de retries, assinatura HMAC-SHA256, observabilidade e integração com dependency injection/hosting da Microsoft.

Use-a quando um `POST` HTTP não é suficiente: as entregas precisam sobreviver a falhas, coordenar múltiplos workers, repetir de forma determinística e expor estado operacional sem fingir que entrega exactly-once existe sobre HTTP.

## Garantias e limites

- Com um `IWebhookDeliveryStore` **durável** e aderente ao contrato, o modelo de entrega pretendido é **at-least-once**.
- ReliableWebhooks **não** garante entrega exactly-once. Receivers precisam ser idempotentes e tolerar duplicidades.
- O pacote core **não** inclui um store durável de produção. A durabilidade após reinício do processo depende do store fornecido e configurado pela aplicação consumidora.
- `InMemoryWebhookDeliveryStore` é local ao processo, **não é durável**, e serve apenas para testes, samples e desenvolvimento local.
- Uma chamada a `IWebhookDeliveryTransport.SendAsync` representa uma tentativa HTTP; o agendamento de retries é tratado separadamente pela política de retry e pelo dispatcher.
- Replay automático de dead letters, idempotência no receiver e política de rotação de secrets continuam sendo responsabilidades da aplicação.

Para o contrato completo de produção e orientações operacionais, veja [Uso em produção](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/docs/production-usage.md) e o [Contrato de persistência](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/docs/persistence.pt-BR.md).

## Instalação

O pacote tem como target `net10.0`:

```bash
dotnet add package ReliableWebhooks --version 1.0.0
```

ou:

```xml
<PackageReference Include="ReliableWebhooks" Version="1.0.0" />
```

## Quick Start

Este exemplo mínimo usa o store em memória para poder executar sem infraestrutura externa. Substitua-o por um `IWebhookDeliveryStore` durável e aderente ao contrato antes de usar ReliableWebhooks em produção.

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

IWebhookDeliveryStore store = new InstrumentedWebhookDeliveryStore(
    new InMemoryWebhookDeliveryStore());

await store.EnqueueAsync(message, DateTimeOffset.UtcNow);

using var handler = new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
};

using var httpClient = new HttpClient(handler);
IWebhookDeliveryTransport transport = new WebhookHttpTransport(httpClient);
IWebhookRetryPolicy retryPolicy = new DefaultWebhookRetryPolicy();

var dispatcher = new WebhookDispatcher(
    store,
    transport,
    retryPolicy,
    new WebhookDispatcherOptions
    {
        MaxConcurrency = 4,
        LeaseDuration = TimeSpan.FromMinutes(1),
        PollInterval = TimeSpan.FromSeconds(1),
        ShutdownGracePeriod = TimeSpan.FromSeconds(5),
    });

using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await dispatcher.RunAsync(stop.Token);
```

`AddHostedDispatcher()` é opt-in. A aplicação também pode resolver e executar `WebhookDispatcher` diretamente quando precisa controlar seu ciclo de vida.

Um sample end-to-end executável com assinatura, sucesso, retries, dead letter e diagnósticos está disponível em [`samples/ReliableWebhooks.Sample`](https://github.com/rodri-oliveira-dev/ReliableWebhooks/tree/main/samples/ReliableWebhooks.Sample).

## Fluxo de entrega

1. Crie um `WebhookMessage` com ID estável, tipo do evento, destino, bytes exatos do payload, content type e headers opcionais.
2. Faça o enqueue por `IWebhookEnqueueService` ou pelo `IWebhookDeliveryStore` de nível mais baixo.
3. `WebhookDispatcher` faz claim apenas do trabalho vencido até o limite de concorrência disponível e recebe leases com expiração.
4. `WebhookHttpTransport` executa uma tentativa de request e retorna um resultado independente do transporte.
5. Sucesso e falha permanente tornam-se estados terminais; falhas retryable são reagendadas por `IWebhookRetryPolicy`.
6. Um store durável aderente ao contrato persiste o estado resultante para que trabalho expirado ou abandonado possa ser recuperado com segurança.

IDs estáveis tornam o enqueue idempotente na fronteira do store, enquanto leases impedem que workers antigos sobrescrevam o owner atual. Esses mecanismos suportam entrega at-least-once; eles não eliminam a necessidade de idempotência no receiver.

## Registro mínimo para produção

Uma aplicação de produção é responsável pelo adapter de persistência e o registra explicitamente:

```csharp
services.AddSingleton<IWebhookDeliveryStore, MyDurableWebhookStore>();

ReliableWebhooksBuilder webhooks = services.AddReliableWebhooks();
webhooks.AddHostedDispatcher();
```

O store precisa implementar as semânticas de durabilidade, claim atômico, lease, rejeição de owner obsoleto, agendamento de retry e estados terminais definidas pelo contrato de persistência.

## Leia em seguida

- **Uso em produção:** [`docs/production-usage.md`](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/docs/production-usage.md) — DI/hosting, retry e classificação de responses, assinatura e verificação no receiver, segurança de destino, observabilidade, configuração, tuning e troubleshooting.
- **Contrato de persistência:** [`docs/persistence.pt-BR.md`](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/docs/persistence.pt-BR.md) — estado durável, enqueue idempotente, claims atômicos, leases, ownership, transições e testes de conformidade.
- **Sample executável:** [`samples/ReliableWebhooks.Sample`](https://github.com/rodri-oliveira-dev/ReliableWebhooks/tree/main/samples/ReliableWebhooks.Sample) — integração end-to-end usando a API pública.
- **Escopo da v1.0.0:** [`docs/release-v1.0.0.pt-BR.md`](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/docs/release-v1.0.0.pt-BR.md) — limites da release, distribuição e superfície suportada.
- **Política de segurança:** [`SECURITY.md`](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/SECURITY.md) — processo de reporte de segurança e orientações de segurança do projeto.

## Extensibilidade

Os comportamentos centrais são expostos por abstrações substituíveis, incluindo persistência, transport de entrega, classificação de responses, política de retry/jitter, assinatura e resolução de secrets, política de destino, resolução de headers, temporização do dispatcher e integração de observabilidade. O guia de produção documenta os defaults internos e os limites que implementações customizadas precisam preservar.

## Contribuindo

Veja [`CONTRIBUTING.md`](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CONTRIBUTING.md) para desenvolvimento local, validação e orientações de contribuição.

## Licença

ReliableWebhooks é licenciado sob a [MIT License](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/LICENSE).
