# ReliableWebhooks

[English](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/README.md) | **Português (Brasil)**

ReliableWebhooks é uma biblioteca .NET 10 para construção de entrega confiável de webhooks de saída.

Ela fornece componentes combináveis para identidade estável de webhooks, persistência e coordenação por lease, dispatch concorrente limitado, envio HTTP de uma única tentativa, assinatura HMAC-SHA256, classificação de respostas, agendamento determinístico de retries e observabilidade independente de backend. O objetivo é tornar explícitas as preocupações de confiabilidade da entrega de webhooks, em vez de escondê-las dentro de um loop de background opaco.

> **Status:** a v0.1.0 está em desenvolvimento. O primeiro pacote público no NuGet ainda não foi lançado. A versão atual fornece os componentes de confiabilidade, dispatcher concorrente, assinatura e observabilidade descritos abaixo; o store durável nativo e a integração com injeção de dependência ainda fazem parte do trabalho necessário antes da primeira release pública.

## Por que ReliableWebhooks?

Enviar um `POST` HTTP é simples. Entregar um webhook de forma confiável não é.

Aplicações reais precisam lidar com falhas HTTP transitórias, erros de rede, queda do processo, workers concorrentes, picos de retry, trabalho abandonado, entregas duplicadas e servidores que pedem ao cliente para tentar novamente mais tarde. Uma solução confiável também precisa preservar estado suficiente para continuar com segurança após uma falha, sem fingir que exatamente-uma-vez é possível sobre HTTP.

ReliableWebhooks trata esses pontos separando o fluxo de entrega em responsabilidades explícitas:

- uma identidade estável para cada mensagem de webhook;
- um contrato de persistência para enqueue, claim, lease, retry e conclusão de entregas;
- um dispatcher concorrente que faz claim apenas da capacidade disponível e coordena shutdown gracioso;
- um transporte HTTP que executa exatamente uma tentativa de requisição por chamada;
- assinatura HMAC-SHA256 opcional sobre os bytes exatos do payload enviado;
- resultados independentes do transporte para sucesso, falha retryable e falha permanente;
- uma política de retry que calcula a próxima tentativa sem aguardar nem bloquear um worker;
- logs estruturados, traces e métricas baseados nas APIs padrão de diagnostics do .NET;
- abstrações substituíveis para persistência, transporte, temporização do dispatcher, assinatura, classificação e comportamento de retry.

## Como funciona

Uma entrega com ReliableWebhooks é estruturada como uma pequena máquina de estados, e não como uma chamada HTTP fire-and-forget:

1. Crie um `WebhookMessage` com ID estável, tipo de evento, destino, bytes exatos do payload, content type e headers opcionais.
2. Faça o enqueue através de `IWebhookDeliveryStore`. Tentativas duplicadas com o mesmo ID estável têm comportamento determinístico. Envolva o store com `InstrumentedWebhookDeliveryStore` quando precisar de logs de enqueue e da métrica de queued independentemente da implementação de persistência.
3. `WebhookDispatcher` faz claim atômico das entregas vencidas até o limite de slots de concorrência disponíveis e recebe instâncias expirantes de `WebhookDeliveryLease`.
4. `WebhookHttpTransport` executa um único `POST` HTTP por entrega em lease; quando um signer está configurado, ele assina exatamente o mesmo buffer usado na requisição e adiciona headers com ID, tipo do evento, timestamp e assinatura.
5. O transporte retorna um `WebhookDeliveryResult` com resultado independente do transporte.
6. Sucessos e falhas permanentes são persistidos imediatamente. Falhas retryable são passadas para `IWebhookRetryPolicy`, que retorna um `NextAttemptAt` futuro ou uma decisão de dead letter.
7. O store registra o estado resultante para que trabalhos abandonados ou com falha possam ser retomados com segurança. A posse da lease impede que workers obsoletos sobrescrevam o dono atual.
8. O ciclo de vida emite logs estruturados seguros, uma activity por tentativa e métricas limitadas sem exigir backend de telemetria.

Quando combinado com um store durável, o modelo de entrega pretendido é **at-least-once**, e não exactly-once. Portanto, os receptores precisam ser idempotentes e tolerar entregas duplicadas.

## Instalação

O Package ID planejado para o NuGet é `ReliableWebhooks` e a biblioteca tem como target `net10.0`.

O primeiro pacote público ainda não foi publicado. Após a release v0.1.0, a instalação será:

```bash
dotnet add package ReliableWebhooks --version 0.1.0
```

ou:

```xml
<PackageReference Include="ReliableWebhooks" Version="0.1.0" />
```

## Começando

A API atual expõe diretamente os componentes de entrega. O exemplo abaixo usa o store em memória apenas para demonstrar o fluxo.

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

A entrada canônica do HMAC é:

```text
UTF8(unixTimestampSeconds + ".") || exactRequestPayloadBytes
```

O payload não é serializado novamente nem normalizado antes da assinatura. O mesmo array de bytes é usado tanto no cálculo do HMAC quanto no conteúdo da requisição HTTP.

Um receptor pode validar a entrega de forma independente seguindo estes passos:

1. ler os headers de timestamp e assinatura sem alterar o corpo da requisição;
2. interpretar o timestamp como Unix seconds e rejeitar timestamps fora da janela de tolerância contra replay definida pelo receptor;
3. reconstruir os bytes canônicos como UTF-8 `timestamp + "."` seguido pelos bytes brutos do corpo da requisição;
4. calcular HMAC-SHA256 usando o segredo compartilhado;
5. converter o digest para hexadecimal em minúsculas e prefixá-lo com `v1=`;
6. comparar a assinatura calculada com a recebida usando comparação em tempo constante.

Segredos de assinatura nunca são incluídos em mensagens de exceção geradas pela biblioteca nem em telemetria automática. Aplicações devem manter a mesma regra em providers de segredo e signers customizados.

## Observabilidade

ReliableWebhooks emite telemetria pelas APIs padrão do .NET e **não** depende do SDK do OpenTelemetry, de collector ou de exporter. A aplicação decide se a telemetria será coletada e para onde ela será enviada.

A classe pública `ReliableWebhooksInstrumentation` expõe os nomes canônicos usados pela biblioteca:

```csharp
ReliableWebhooksInstrumentation.ActivitySourceName // "ReliableWebhooks"
ReliableWebhooksInstrumentation.MeterName          // "ReliableWebhooks"
```

A mesma classe expõe nomes estáveis para a activity de tentativa e para as métricas publicadas, evitando strings mágicas nas integrações.

### Logs estruturados

`WebhookDispatcher` possui um overload aditivo de construtor que recebe `ILogger`. Os construtores existentes continuam válidos e usam um logger no-op. A telemetria de enqueue é fornecida por `InstrumentedWebhookDeliveryStore`, um decorator que pode envolver qualquer `IWebhookDeliveryStore`, inclusive um store durável customizado de produção.

```csharp
ILogger logger = loggerFactory.CreateLogger("ReliableWebhooks");

IWebhookDeliveryStore durableStore = GetApplicationWebhookStore();
IWebhookDeliveryStore store = new InstrumentedWebhookDeliveryStore(
    durableStore,
    logger);

var dispatcher = new WebhookDispatcher(
    store,
    transport,
    retryPolicy,
    options,
    delay: null,
    logger);
```

`InstrumentedWebhookDeliveryStore` emite o evento de enqueue e a métrica queued somente quando uma nova entrega é realmente persistida. Enqueues idempotentes duplicados não contabilizam novamente a entrega. A telemetria de claim é emitida pelo `WebhookDispatcher`, de modo que o mesmo claim seja registrado exatamente uma vez e stores customizados tenham a mesma cobertura de ciclo de vida.

O ciclo de vida usa Event IDs estáveis para enqueue, claim, início da tentativa, sucesso, agendamento de retry, falha permanente, dead letter, cancelamento, perda de lease e falha inesperada. As propriedades são estruturadas e excluem deliberadamente corpos de payload, URLs/query strings de destino, segredos de assinatura e assinaturas.

### Traces

Cada tentativa do dispatcher cria uma activity chamada `ReliableWebhooks.DeliveryAttempt` no activity source `ReliableWebhooks`. Os atributos seguros incluem:

- `webhook.id` para correlação de logs/traces;
- `webhook.event_type`;
- `webhook.attempt`;
- `webhook.outcome`;
- `http.response.status_code` quando existe resposta HTTP;
- `error.type` para o tipo de exceções inesperadas.

Payloads, segredos, assinaturas e URLs de destino nunca são adicionados automaticamente. IDs de webhook podem aparecer em traces para correlação, mas são intencionalmente excluídos das dimensões de métricas.

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

`webhook.event_type` deve vir de um vocabulário definido pela aplicação e com cardinalidade limitada. Não codifique IDs de cliente, request IDs, URLs ou outros valores ilimitados dentro do event type. IDs de webhook e destinos arbitrários não são tags de métricas.

### Integração com OpenTelemetry

A aplicação consumidora pode optar pelo OpenTelemetry usando seus próprios pacotes e configuração de exporter. ReliableWebhooks não depende deles:

```csharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing =>
        tracing.AddSource(ReliableWebhooksInstrumentation.ActivitySourceName))
    .WithMetrics(metrics =>
        metrics.AddMeter(ReliableWebhooksInstrumentation.MeterName));
```

A aplicação pode então adicionar OTLP, Azure Monitor, Prometheus, Grafana/Tempo, Datadog, Dynatrace ou outro exporter/backend suportado sem alterar ReliableWebhooks. Logs continuam usando `ILogger` e podem ser conectados de forma independente ao pipeline de logging/OpenTelemetry escolhido pela aplicação.

## Comportamento padrão

| Área | Padrão |
| --- | --- |
| Concorrência máxima do dispatcher | 4 |
| Duração da lease do dispatcher | 1 minuto |
| Intervalo de polling do dispatcher | 1 segundo |
| Grace period de shutdown do dispatcher | 30 segundos |
| Timeout de uma tentativa HTTP | 30 segundos |
| Corpo da resposta capturado | Até 16 KiB |
| Classificação de sucesso | Qualquer resposta `2xx` |
| Respostas HTTP retryable | `408`, `425`, `429` e `5xx` |
| Respostas HTTP permanentes | Demais status, incluindo redirects |
| Algoritmo de assinatura | HMAC-SHA256 quando um signer está configurado |
| Formato canônico da assinatura | `UTF8(unixTimestamp + ".") || bytes do payload` |
| Headers de assinatura | `X-Webhook-Id`, `X-Webhook-Event`, `X-Webhook-Timestamp`, `X-Webhook-Signature` |
| Máximo de tentativas | 5, incluindo a tentativa atual |
| Atraso base de retry | 1 segundo |
| Atraso máximo de retry | 5 minutos |
| Jitter | De 0 a 20% de jitter positivo antes da aplicação do limite máximo |
| `Retry-After` | Respeitado quando agenda para depois do retry local, limitado pelo atraso máximo configurado |

Redirects automáticos devem ser desabilitados no handler do `HttpClient` para impedir que uma única chamada do transporte se transforme silenciosamente em múltiplas requisições HTTP ou altere o método da requisição.

Valores malformados de `Retry-After` são ignorados. Valores válidos em delta-seconds e HTTP-date são expostos através de `WebhookDeliveryResult` e consumidos pela política de retry padrão.

## Garantias e limites de entrega

ReliableWebhooks foi projetado com semântica de entrega explícita:

- **At-least-once, não exactly-once.** Uma entrega duplicada pode ocorrer, especialmente quando um worker cai após enviar a requisição e antes de persistir o resultado.
- **IDs estáveis permitem enqueue seguro contra duplicatas.** O receptor ainda precisa implementar idempotência na aplicação.
- **Leases coordenam a posse ativa.** Enquanto uma lease é válida, dois workers não devem possuir a mesma entrega simultaneamente; trabalhos expirados podem ser recuperados.
- **A concorrência do dispatcher é limitada.** Claims são limitados aos slots disponíveis em vez de criar tasks em background sem limite.
- **O shutdown ocorre em duas fases.** Novos claims param primeiro; trabalho em andamento pode terminar durante o grace period antes do cancelamento das tentativas restantes.
- **Uma chamada do transporte significa uma tentativa HTTP.** Loops de retry ficam propositalmente fora do transporte.
- **Payloads assinados usam os bytes exatos da requisição.** O receptor deve validar o corpo bruto, e não uma representação parseada/serializada novamente.
- **Políticas de retry agendam; elas não esperam.** `DefaultWebhookRetryPolicy` retorna um timestamp futuro ou uma decisão de dead letter e nunca chama `Task.Delay`.
- **A telemetria é independente de backend.** Logs, traces e métricas usam APIs padrão do .NET; exporters continuam sendo responsabilidade da aplicação.
- **A persistência é substituível.** O pacote core não depende de um banco de dados específico.

A versão atual em desenvolvimento ainda não inclui o store durável de produção com EF Core nem a integração com injeção de dependência/hosted service planejados para a v0.1.0.

## Extensibilidade

Os principais comportamentos são expostos por abstrações públicas:

- `IWebhookDeliveryStore` — persistência e coordenação de leases;
- `InstrumentedWebhookDeliveryStore` — decorator de persistência que adiciona logs de enqueue e a métrica queued a qualquer implementação de store;
- `IWebhookDeliveryTransport` — transporte de uma única tentativa usado pelo dispatcher;
- `IWebhookDispatcherDelay` — temporização substituível para polling/grace period em testes determinísticos ou agendamento customizado;
- `IWebhookHttpResponseClassifier` — classificação de respostas HTTP;
- `IWebhookRetryPolicy` — decisões de retry e dead letter;
- `IWebhookRetryJitterSource` — geração determinística ou customizada de jitter;
- `IWebhookRequestSigner` — estratégia de assinatura da requisição;
- `IWebhookSigningSecretProvider` — resolução do segredo de assinatura por mensagem;
- `ReliableWebhooksInstrumentation` — nomes públicos estáveis de diagnostics para integração de traces e métricas.

`WebhookDispatcherOptions` também expõe `MaxConcurrency`, `LeaseDuration`, `PollInterval`, `ShutdownGracePeriod` e `TimeProvider`, mantendo concorrência e temporização configuráveis e testáveis.

Isso mantém persistência, dispatch, comportamento de transporte, assinatura, estratégia de retry e seleção do backend de telemetria independentes, substituíveis e testáveis.

## Suporte e contribuição

Use [GitHub Issues](https://github.com/rodri-oliveira-dev/ReliableWebhooks/issues) para bugs, dúvidas e discussões de funcionalidades.

Para questões de segurança, siga o [SECURITY.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/SECURITY.md) em vez de abrir uma issue pública.

Contribuições são bem-vindas. Consulte [CONTRIBUTING.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CONTRIBUTING.md) para o fluxo de contribuição e [CHANGELOG.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CHANGELOG.md) para mudanças relevantes.

ReliableWebhooks é licenciado sob a [Licença MIT](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/LICENSE).
