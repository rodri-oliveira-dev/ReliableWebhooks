# ReliableWebhooks

[![CI](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml)
[![Release](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml/badge.svg)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/ReliableWebhooks.svg)](https://www.nuget.org/packages/ReliableWebhooks/)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

[English](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/README.md) | **Português (Brasil)**

ReliableWebhooks é uma biblioteca .NET 10 para construção de entrega confiável de webhooks de saída.

Ela fornece componentes combináveis para identidade estável de webhooks, persistência e coordenação por lease, dispatch concorrente limitado, envio HTTP de uma única tentativa, assinatura HMAC-SHA256, classificação de respostas, agendamento determinístico de retries, observabilidade independente de backend e integração com injeção de dependência/hosting padrão do .NET. O objetivo é tornar explícitas as preocupações de confiabilidade da entrega de webhooks, em vez de escondê-las dentro de um loop de background opaco.

> **Escopo da v0.1.0:** a primeira linha pública fornece o engine de entrega confiável, contrato `IWebhookDeliveryStore` independente de tecnologia, retries, leases, assinatura, observabilidade, injeção de dependência, hosted dispatcher e orientação de produção. A durabilidade entre reinícios é fornecida por um store durável e aderente ao contrato escolhido pela aplicação.

## Por que ReliableWebhooks?

Enviar um `POST` HTTP é simples. Entregar um webhook de forma confiável não é.

Aplicações reais precisam lidar com falhas HTTP transitórias, erros de rede, queda do processo, workers concorrentes, picos de retry, trabalho abandonado, entregas duplicadas e servidores que pedem ao cliente para tentar novamente mais tarde. Uma solução confiável também precisa preservar estado suficiente para continuar com segurança após uma falha, sem fingir que exactly-once é possível sobre HTTP.

ReliableWebhooks trata esses pontos separando o fluxo de entrega em responsabilidades explícitas:

- uma identidade estável para cada mensagem de webhook;
- um contrato de persistência para enqueue, claim, lease, retry e conclusão de entregas;
- um dispatcher concorrente que faz claim apenas da capacidade disponível e coordena shutdown gracioso;
- um transporte HTTP que executa exatamente uma tentativa de requisição por chamada;
- assinatura HMAC-SHA256 opcional sobre os bytes exatos do payload enviado;
- resultados independentes do transporte para sucesso, falha retryable e falha permanente;
- uma política de retry que calcula a próxima tentativa sem aguardar nem bloquear um worker;
- logs estruturados, traces e métricas baseados nas APIs padrão de diagnostics do .NET;
- integração com Microsoft DI e Generic Host, com hosted dispatcher opcional;
- abstrações substituíveis para persistência, transporte, temporização do dispatcher, assinatura, classificação e comportamento de retry.

## Como funciona

Uma entrega com ReliableWebhooks é estruturada como uma pequena máquina de estados, e não como uma chamada HTTP fire-and-forget:

1. Crie um `WebhookMessage` com ID estável, tipo de evento, destino, bytes exatos do payload, content type e headers opcionais.
2. Faça o enqueue através de `IWebhookDeliveryStore` ou da API de aplicação `IWebhookEnqueueService`. Tentativas duplicadas com o mesmo ID estável têm comportamento determinístico. Envolva o store com `InstrumentedWebhookDeliveryStore` quando precisar de logs de enqueue e da métrica de queued independentemente da implementação de persistência.
3. `WebhookDispatcher` faz claim atômico das entregas vencidas até o limite de slots de concorrência disponíveis e recebe instâncias expirantes de `WebhookDeliveryLease`.
4. `WebhookHttpTransport` executa um único `POST` HTTP por entrega em lease; quando um signer está configurado, ele assina exatamente o mesmo buffer usado na requisição e adiciona headers com ID, tipo do evento, timestamp e assinatura.
5. O transporte retorna um `WebhookDeliveryResult` com resultado independente do transporte.
6. Sucessos e falhas permanentes são persistidos imediatamente. Falhas retryable são passadas para `IWebhookRetryPolicy`, que retorna um `NextAttemptAt` futuro ou uma decisão de dead letter.
7. O store registra o estado resultante para que trabalhos abandonados ou com falha possam ser retomados com segurança. A posse da lease impede que workers obsoletos sobrescrevam o dono atual.
8. O ciclo de vida emite logs estruturados seguros, uma activity por tentativa e métricas limitadas sem exigir backend de telemetria.

Quando combinado com um store durável, o modelo de entrega pretendido é **at-least-once**, e não exactly-once. Portanto, os receptores precisam ser idempotentes e tolerar entregas duplicadas.

## Instalação

O Package ID no NuGet é `ReliableWebhooks` e a biblioteca tem como target `net10.0`. Instale a v0.1.0 com:

```bash
dotnet add package ReliableWebhooks --version 0.1.0
```

ou:

```xml
<PackageReference Include="ReliableWebhooks" Version="0.1.0" />
```

## Limites da v0.1.0

A primeira release pública mantém a persistência intencionalmente independente de tecnologia:

- nenhum store durável de produção é incluído; a aplicação registra um `IWebhookDeliveryStore` aderente ao contrato;
- `InMemoryWebhookDeliveryStore` não é durável e serve apenas para testes, samples e desenvolvimento local;
- a entrega é at-least-once quando apoiada por um store durável aderente, portanto receivers devem ser idempotentes;
- exactly-once, idempotência no receiver, replay automático de dead letters e política de rotação de secrets não são garantidos;
- EF Core, Dapper, ADO.NET, Redis, arquivos, bancos de documentos e outras tecnologias são escolhas opcionais do consumidor, não dependências do core.

Consulte [`docs/production-usage.md`](docs/production-usage.md) para integração de produção e [`docs/release-v0.1.0.md`](docs/release-v0.1.0.md) para detalhes da release/distribuição.

## Começando

A API também permite montar os componentes diretamente. O exemplo abaixo usa o store em memória apenas para demonstrar o fluxo.

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
        ShutdownGracePeriod = TimeSpan.FromSeconds(30),
    });

await dispatcher.RunAsync(stoppingToken);
```

`WebhookDispatcher` não cria trabalho de forma ilimitada: ele faz claim no máximo da quantidade de slots de concorrência disponíveis. No shutdown, ele interrompe novos claims, permite que entregas em andamento terminem durante o grace period configurado e cancela as tentativas restantes depois desse limite. Trabalho cancelado não é marcado como sucesso; sua lease pode expirar e ser recuperada posteriormente.

`InMemoryWebhookDeliveryStore` é local ao processo e **não é durável**. Ele existe apenas para testes e exemplos e não deve ser usado como persistência de produção.

Para o contrato completo de persistência e as orientações de conformidade, consulte [`docs/persistence.pt-BR.md`](docs/persistence.pt-BR.md).

Um exemplo end-to-end executável está disponível em [`samples/ReliableWebhooks.Sample`](samples/ReliableWebhooks.Sample). Para integração em produção, configuração, idempotência do receiver, verificação de assinatura e troubleshooting, consulte [`docs/production-usage.md`](docs/production-usage.md).

Um exemplo end-to-end executável está disponível em [`samples/ReliableWebhooks.Sample`](samples/ReliableWebhooks.Sample). Para integração em produção, configuração, idempotência do receiver, verificação de assinatura e troubleshooting, consulte [`docs/production-usage.md`](docs/production-usage.md).

### Injeção de dependência e hosted dispatcher

Aplicações que usam o Generic Host do .NET podem registrar a integração padrão através de `AddReliableWebhooks`. Uma aplicação de produção deve registrar seu próprio `IWebhookDeliveryStore`; ReliableWebhooks não escolhe implicitamente o store em memória, que não é durável, como default de produção.

```csharp
services.AddSingleton<IWebhookDeliveryStore, MyDurableWebhookStore>();

ReliableWebhooksBuilder webhooks = services.AddReliableWebhooks(options =>
{
    options.Dispatcher.MaxConcurrency = 8;
    options.Dispatcher.LeaseDuration = TimeSpan.FromMinutes(2);
    options.Dispatcher.PollInterval = TimeSpan.FromSeconds(1);
    options.Transport = new WebhookHttpTransportOptions
    {
        AttemptTimeout = TimeSpan.FromSeconds(30),
    };
});

webhooks.AddHostedDispatcher();
```

`AddHostedDispatcher()` é opt-in. Sem ele, a aplicação pode resolver e executar `WebhookDispatcher` diretamente. O transporte padrão usa `IHttpClientFactory`, desabilita redirects automáticos, remove os loggers padrão de requisição do `HttpClientFactory` para evitar vazamento de paths ou queries de destino que contenham secrets, e configura `HttpClient.Timeout` como infinito para que `WebhookHttpTransportOptions.AttemptTimeout` continue sendo o timeout autoritativo de cada tentativa. Handlers ou configurações adicionais podem ser aplicados através de `ReliableWebhooksBuilder.HttpClientBuilder`.

ReliableWebhooks trata destinos como confiáveis pelo operador por padrão depois de validar que são URIs HTTP/HTTPS absolutas. Aplicações que aceitam URLs de tenants ou outra origem não confiável devem configurar `WebhookHttpTransportOptions.DestinationPolicy`, por exemplo com `new PublicNetworkWebhookDestinationPolicy(allowedHosts: ["internal-webhooks.example"])`. Essa policy resolve DNS antes de cada tentativa e nega destinos loopback, unspecified, multicast, link-local, privados e carrier-grade compartilhados para IPv4 e IPv6, salvo quando um host é explicitamente permitido. Uma negação determinística da policy é uma falha permanente e não é retentada. Com `HttpClient` padrão, a validação acontece antes de `SendAsync`; mantenha redirects automáticos desabilitados e use allow-list exata para destinos de intranet intencionais.

Classifier de resposta, política de retry, transporte e dispatcher usam registros substituíveis por DI. Store e signer são fornecidos pela aplicação, e as abstrações de temporização do dispatcher ou fonte de jitter também podem ser substituídas. Opções inválidas de dispatcher, retry, transporte ou assinatura são validadas na resolução das opções e pela validação de startup do Generic Host.

O código da aplicação pode fazer enqueue sem conhecer os detalhes de agendamento da persistência:

```csharp
IWebhookEnqueueService webhookEnqueue =
    serviceProvider.GetRequiredService<IWebhookEnqueueService>();

await webhookEnqueue.EnqueueAsync(message, cancellationToken);
```

A integração depende apenas de `Microsoft.Extensions.*`; ela não exige ASP.NET Core.

### Assinando webhooks

A assinatura é habilitada fornecendo um `IWebhookRequestSigner`. O `HmacSha256WebhookRequestSigner` nativo resolve os bytes do segredo através de `IWebhookSigningSecretProvider`, portanto a biblioteca não precisa saber se o segredo vem de configuração, secret manager ou outra fonte segura.

```csharp
IWebhookSigningSecretProvider secretProvider = GetApplicationSecretProvider();
IWebhookRequestSigner signer = new HmacSha256WebhookRequestSigner(secretProvider);

var transport = new WebhookHttpTransport(
    httpClient,
    classifier: null,
    options: null,
    signer: signer);
```

Com o `WebhookSigningOptions` padrão, cada requisição assinada contém:

- `X-Webhook-Id`: o `WebhookMessage.Id` estável;
- `X-Webhook-Event`: o `WebhookMessage.EventType`;
- `X-Webhook-Timestamp`: o timestamp UTC em Unix seconds;
- `X-Webhook-Signature`: `v1=<digest HMAC-SHA256 hexadecimal em minúsculas>`.

Os nomes dos headers podem ser customizados através de `WebhookHttpTransportOptions.Signing`. Headers gerados pela assinatura têm precedência sobre headers customizados da mensagem com o mesmo nome.

Headers customizados fornecidos ao `WebhookMessage` devem usar nomes válidos de token HTTP, são comparados sem diferenciar maiúsculas/minúsculas para detectar duplicidade e não podem conter caracteres de controle como CR, LF ou NUL nos valores. Trate valores de headers customizados como sensíveis quando vierem de tenants, assinantes ou outra configuração externa.

A entrada canônica do HMAC é:

```text
UTF8(unixTimestampSeconds + ".") || exactRequestPayloadBytes
```

O payload não é serializado novamente nem normalizado antes da assinatura. O mesmo array de bytes é usado tanto no cálculo do HMAC quanto no conteúdo da requisição HTTP.

Um receptor pode validar uma entrega de forma independente reconstruindo os bytes canônicos com o timestamp recebido e o corpo bruto, calculando HMAC-SHA256 com o segredo compartilhado e comparando a assinatura em tempo constante. O receptor também deve aplicar sua própria janela de tolerância contra replay.

Segredos de assinatura nunca são incluídos em mensagens de exceção geradas pela biblioteca nem em telemetria automática. Aplicações devem manter a mesma regra em providers de segredo e signers customizados.

## Observabilidade

ReliableWebhooks emite telemetria pelas APIs padrão do .NET e **não** depende do SDK do OpenTelemetry, de collector ou de exporter. A aplicação decide se a telemetria será coletada e para onde ela será enviada.

A classe pública `ReliableWebhooksInstrumentation` expõe os nomes canônicos usados pela biblioteca:

```csharp
ReliableWebhooksInstrumentation.ActivitySourceName // "ReliableWebhooks"
ReliableWebhooksInstrumentation.MeterName          // "ReliableWebhooks"
```

### Logs estruturados

`WebhookDispatcher` possui um overload aditivo de construtor que recebe `ILogger`. Os construtores existentes continuam válidos e usam um logger no-op. A telemetria de enqueue é fornecida por `InstrumentedWebhookDeliveryStore`, um decorator que pode envolver qualquer `IWebhookDeliveryStore`, inclusive um store durável customizado de produção.

O ciclo de vida usa event IDs estáveis para enqueue, claim, início de tentativa, sucesso, agendamento de retry, falha permanente, dead letter, cancelamento, perda de lease e falha inesperada. As propriedades de log excluem deliberadamente payload, URLs de destino/query string, segredos de assinatura e assinaturas.

### Traces

Cada tentativa do dispatcher cria uma activity `ReliableWebhooks.DeliveryAttempt` no activity source `ReliableWebhooks`. Atributos seguros incluem ID do webhook para correlação, tipo de evento, número da tentativa, resultado, status HTTP quando houver e tipo da exceção inesperada.

Payloads, segredos, assinaturas e URLs de destino não são adicionados automaticamente. IDs de webhook podem aparecer em traces para correlação, mas não são usados como dimensões de métricas.

### Métricas

| Instrumento | Tipo | Tags |
| --- | --- | --- |
| `reliablewebhooks.delivery.queued` | Counter | `webhook.event_type` |
| `reliablewebhooks.delivery.attempted` | Counter | `webhook.event_type` |
| `reliablewebhooks.delivery.succeeded` | Counter | `webhook.event_type` |
| `reliablewebhooks.delivery.retried` | Counter | `webhook.event_type` |
| `reliablewebhooks.delivery.permanently_failed` | Counter | `webhook.event_type` |
| `reliablewebhooks.delivery.dead_lettered` | Counter | `webhook.event_type` |
| `reliablewebhooks.delivery.duration` | Histogram em milissegundos | `webhook.event_type`, `webhook.outcome` |

`webhook.event_type` deve vir de um vocabulário limitado pela aplicação. IDs de cliente, request IDs, URLs e outros valores de alta cardinalidade não devem ser codificados no tipo do evento.

### Integração com OpenTelemetry

O consumidor pode habilitar OpenTelemetry com seus próprios pacotes e exporters. ReliableWebhooks não exige nenhum deles:

```csharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing =>
        tracing.AddSource(ReliableWebhooksInstrumentation.ActivitySourceName))
    .WithMetrics(metrics =>
        metrics.AddMeter(ReliableWebhooksInstrumentation.MeterName));
```

## Comportamento padrão

| Área | Padrão |
| --- | --- |
| Concorrência máxima do dispatcher | 4 |
| Duração da lease | 1 minuto |
| Intervalo de polling | 1 segundo |
| Grace period de shutdown | 30 segundos |
| Timeout de tentativa HTTP | 30 segundos |
| Corpo de resposta capturado | Até 16 KiB |
| Classificação de sucesso | Qualquer resposta `2xx` |
| Respostas HTTP retryable | `408`, `425`, `429` e `5xx` |
| Respostas HTTP permanentes | Outros status codes, incluindo redirects |
| Algoritmo de assinatura | HMAC-SHA256 quando um signer está configurado |
| Formato canônico de assinatura | `UTF8(unixTimestamp + ".") || payload bytes` |
| Headers de assinatura | `X-Webhook-Id`, `X-Webhook-Event`, `X-Webhook-Timestamp`, `X-Webhook-Signature` |
| Máximo de tentativas | 5, incluindo a tentativa atual |
| Delay base de retry | 1 segundo |
| Delay máximo de retry | 5 minutos |
| Jitter | 0 a 20% de jitter positivo antes do limite máximo |
| `Retry-After` | Respeitado quando agenda depois do delay local; `MaxDelay` limita apenas backoff/jitter locais |

Redirects automáticos precisam ficar desabilitados para que uma chamada do transporte não se transforme silenciosamente em múltiplas requisições ou altere o método HTTP. O cliente gerenciado pela integração de DI já aplica essa configuração.

## Garantias e limites de entrega

ReliableWebhooks trabalha com semântica explícita de entrega:

- **At-least-once, não exactly-once.** Entregas duplicadas podem acontecer, especialmente quando um worker envia e falha antes de persistir o resultado.
- **IDs estáveis tornam o enqueue duplicate-safe.** O receptor ainda precisa de idempotência no nível da aplicação.
- **Leases coordenam ownership ativo.** Enquanto uma lease é válida, dois workers não devem possuir a mesma entrega simultaneamente; trabalho expirado pode ser recuperado.
- **A concorrência do dispatcher é limitada.** Claims respeitam os slots disponíveis e não criam tasks ilimitadas.
- **O shutdown ocorre em duas fases.** Novos claims param primeiro; trabalho em andamento pode terminar durante o grace period antes de ser cancelado.
- **Uma chamada ao transporte corresponde a uma tentativa HTTP.** Loops de retry ficam fora do transporte.
- **Payloads assinados usam os bytes exatos da requisição.** O receptor deve validar o corpo bruto, não uma versão parseada e serializada novamente.
- **Políticas de retry agendam; elas não aguardam.** `DefaultWebhookRetryPolicy` retorna um timestamp futuro ou decisão de dead letter.
- **A telemetria é independente de backend.** Exporters continuam sob responsabilidade da aplicação.
- **A persistência é substituível.** O pacote principal não depende de um provider de banco específico.

A v0.1.0 de `ReliableWebhooks` não exige um adapter de persistência de produção nativo. A aplicação fornece um `IWebhookDeliveryStore` durável compatível; adapters opcionais para EF Core, Dapper, Redis, arquivos ou outras tecnologias podem ser introduzidos de forma independente.

## Extensibilidade

Os principais comportamentos são expostos por abstrações públicas:

- `IWebhookDeliveryStore` — persistência e coordenação por lease;
- `InstrumentedWebhookDeliveryStore` — decorator que adiciona logs de enqueue e métrica queued a qualquer store;
- `IWebhookEnqueueService` — API de enqueue voltada para a aplicação e registrada pela integração de DI;
- `IWebhookDeliveryTransport` — transporte de uma tentativa usado pelo dispatcher;
- `IWebhookDispatcherDelay` — temporização substituível para polling/grace period;
- `IWebhookHttpResponseClassifier` — classificação de respostas HTTP;
- `IWebhookRetryPolicy` — decisões de retry e dead letter;
- `IWebhookRetryJitterSource` — geração de jitter substituível;
- `IWebhookDestinationPolicy` — autorização de destinos para URLs de webhook não confiáveis;
- `IWebhookRequestSigner` — estratégia de assinatura;
- `IWebhookSigningSecretProvider` — resolução de segredo por mensagem;
- `ReliableWebhooksInstrumentation` — nomes públicos e estáveis de diagnostics.

`ReliableWebhooksOptions` agrupa a configuração de dispatcher, retry, transporte e signing para consumidores via DI. `WebhookDispatcherOptions` mantém `MaxConcurrency`, `LeaseDuration`, `PollInterval`, `ShutdownGracePeriod` e `TimeProvider` configuráveis e testáveis.

## Suporte e contribuição

Use [GitHub Issues](https://github.com/rodri-oliveira-dev/ReliableWebhooks/issues) para bugs, dúvidas e discussão de funcionalidades.

Para problemas de segurança, siga [SECURITY.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/SECURITY.md) em vez de abrir uma issue pública.

Contribuições são bem-vindas. Consulte [CONTRIBUTING.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CONTRIBUTING.md) para o fluxo de contribuição e [CHANGELOG.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CHANGELOG.md) para mudanças relevantes.

ReliableWebhooks é licenciado sob a [MIT License](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/LICENSE).
