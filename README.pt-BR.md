# ReliableWebhooks

[English](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/README.md) | **Português (Brasil)**

ReliableWebhooks é uma biblioteca .NET 10 para construção de entrega confiável de webhooks de saída.

Ela fornece componentes combináveis para identidade estável de webhooks, persistência e coordenação por lease, envio HTTP de uma única tentativa, classificação de respostas e agendamento determinístico de retries. O objetivo é tornar explícitas as preocupações de confiabilidade da entrega de webhooks, em vez de escondê-las dentro de um loop de background opaco.

> **Status:** a v0.1.0 está em desenvolvimento. O primeiro pacote público no NuGet ainda não foi lançado. A versão atual fornece os componentes de confiabilidade descritos abaixo; o store durável nativo e o dispatcher concorrente ainda fazem parte do trabalho necessário antes da primeira release pública.

## Por que ReliableWebhooks?

Enviar um `POST` HTTP é simples. Entregar um webhook de forma confiável não é.

Aplicações reais precisam lidar com falhas HTTP transitórias, erros de rede, queda do processo, workers concorrentes, picos de retry, trabalho abandonado, entregas duplicadas e servidores que pedem ao cliente para tentar novamente mais tarde. Uma solução confiável também precisa preservar estado suficiente para continuar com segurança após uma falha, sem fingir que exatamente-uma-vez é possível sobre HTTP.

ReliableWebhooks trata esses pontos separando o fluxo de entrega em responsabilidades explícitas:

- uma identidade estável para cada mensagem de webhook;
- um contrato de persistência para enqueue, claim, lease, retry e conclusão de entregas;
- um transporte HTTP que executa exatamente uma tentativa de requisição por chamada;
- resultados independentes do transporte para sucesso, falha retryable e falha permanente;
- uma política de retry que calcula a próxima tentativa sem aguardar nem bloquear um worker;
- abstrações substituíveis para persistência, classificação e comportamento de retry.

## Como funciona

Uma entrega com ReliableWebhooks é estruturada como uma pequena máquina de estados, e não como uma chamada HTTP fire-and-forget:

1. Crie um `WebhookMessage` com ID estável, tipo de evento, destino, bytes exatos do payload, content type e headers opcionais.
2. Faça o enqueue através de `IWebhookDeliveryStore`. Tentativas duplicadas com o mesmo ID estável têm comportamento determinístico.
3. Um worker faz o claim atômico das entregas vencidas e recebe um `WebhookDeliveryLease` com expiração.
4. `WebhookHttpTransport` executa um único `POST` HTTP e retorna um `WebhookDeliveryResult`.
5. Sucessos e falhas permanentes podem ser persistidos imediatamente. Falhas retryable são passadas para `IWebhookRetryPolicy`, que retorna um `NextAttemptAt` futuro ou uma decisão de dead letter.
6. O store registra o estado resultante para que trabalhos abandonados ou com falha possam ser retomados com segurança.

Quando combinado com um store durável e um dispatcher, o modelo de entrega pretendido é **at-least-once**, e não exactly-once. Portanto, os receptores precisam ser idempotentes e tolerar entregas duplicadas.

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
using System;
using System.Linq;
using System.Net.Http;
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
DateTimeOffset now = DateTimeOffset.UtcNow;

await store.EnqueueAsync(message, now);

WebhookDeliveryLease lease = (await store.ClaimDueAsync(
    now,
    leaseDuration: TimeSpan.FromMinutes(1),
    maxCount: 1)).Single();

using var handler = new HttpClientHandler
{
    AllowAutoRedirect = false,
};

using var httpClient = new HttpClient(handler);
var transport = new WebhookHttpTransport(httpClient);

WebhookDeliveryResult result = await transport.SendAsync(lease.Delivery.Message);
```

A lease de um minuto é propositalmente maior que o timeout padrão de 30 segundos de uma tentativa HTTP, deixando tempo para persistir o resultado enquanto a posse ainda é válida. Em produção, a duração da lease deve superar o orçamento completo de uma tentativa, ou a lease deve ser renovada quando o processamento puder levar mais tempo.

`InMemoryWebhookDeliveryStore` é local ao processo e **não é durável**. Ele existe apenas para testes e exemplos e não deve ser usado como persistência de produção.

### Tratando o resultado

O transporte propositalmente não executa retries. Ele representa uma única tentativa e deixa a transição de estado para o chamador ou para o dispatcher.

```csharp
DateTimeOffset completedAt = DateTimeOffset.UtcNow;

switch (result.Outcome)
{
    case WebhookDeliveryOutcome.Success:
        await store.MarkSucceededAsync(lease, completedAt);
        break;

    case WebhookDeliveryOutcome.PermanentFailure:
        await store.MarkPermanentlyFailedAsync(
            lease,
            completedAt,
            lastError: null);
        break;

    case WebhookDeliveryOutcome.RetryableFailure:
        var retryPolicy = new DefaultWebhookRetryPolicy();
        WebhookRetryDecision decision = retryPolicy.GetDecision(
            WebhookRetryContext.FromResult(
                lease.Delivery,
                result,
                completedAt));

        if (decision.ShouldRetry)
        {
            await store.ScheduleRetryAsync(
                lease,
                completedAt,
                decision.NextAttemptAt!.Value,
                lastError: null);
        }
        else
        {
            await store.DeadLetterAsync(
                lease,
                completedAt,
                lastError: null);
        }

        break;
}
```

## Comportamento padrão

| Área | Padrão |
| --- | --- |
| Timeout de uma tentativa HTTP | 30 segundos |
| Corpo da resposta capturado | Até 16 KiB |
| Classificação de sucesso | Qualquer resposta `2xx` |
| Respostas HTTP retryable | `408`, `425`, `429` e `5xx` |
| Respostas HTTP permanentes | Demais status, incluindo redirects |
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
- **Uma chamada do transporte significa uma tentativa HTTP.** Loops de retry ficam propositalmente fora do transporte.
- **Políticas de retry agendam; elas não esperam.** `DefaultWebhookRetryPolicy` retorna um timestamp futuro ou uma decisão de dead letter e nunca chama `Task.Delay`.
- **A persistência é substituível.** O pacote core não depende de um banco de dados específico.

A versão atual em desenvolvimento ainda não inclui o store durável de produção com EF Core, dispatcher concorrente, assinatura HMAC, integração com injeção de dependência ou observabilidade planejados para a v0.1.0.

## Extensibilidade

Os principais comportamentos são expostos por abstrações públicas:

- `IWebhookDeliveryStore` — persistência e coordenação de leases;
- `IWebhookHttpResponseClassifier` — classificação de respostas HTTP;
- `IWebhookRetryPolicy` — decisões de retry e dead letter;
- `IWebhookRetryJitterSource` — geração determinística ou customizada de jitter.

Isso mantém persistência, comportamento de transporte e estratégia de retry independentes, substituíveis e testáveis.

## Suporte e contribuição

Use [GitHub Issues](https://github.com/rodri-oliveira-dev/ReliableWebhooks/issues) para bugs, dúvidas e discussões de funcionalidades.

Para questões de segurança, siga o [SECURITY.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/SECURITY.md) em vez de abrir uma issue pública.

Contribuições são bem-vindas. Consulte [CONTRIBUTING.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CONTRIBUTING.md) para o fluxo de contribuição e [CHANGELOG.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CHANGELOG.md) para mudanças relevantes.

ReliableWebhooks é licenciado sob a [Licença MIT](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/LICENSE).
