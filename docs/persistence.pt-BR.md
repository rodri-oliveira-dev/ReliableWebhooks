# Contrato de persistência

ReliableWebhooks não escolhe banco de dados, ORM, cache, formato de arquivo ou outra tecnologia de armazenamento. O pacote principal define o protocolo de entrega por meio de `IWebhookDeliveryStore`; a aplicação escolhe o adapter de persistência.

Uma aplicação de produção pode implementar o contrato com Entity Framework Core, Dapper, ADO.NET puro, banco relacional ou documental, Redis, arquivos ou outro mecanismo, desde que a implementação cumpra as semânticas de confiabilidade abaixo.

## O que um store precisa preservar

Uma implementação durável deve preservar informação suficiente para retomar a entrega depois que o limite de durabilidade anunciado por ela tiver sido atravessado.

| Dado | Requisito |
| --- | --- |
| ID estável do webhook | Preservar exatamente e tratar como chave de idempotência opaca e case-sensitive. |
| Tipo de evento | Preservar exatamente. |
| Destino | Preservar a URI HTTP/HTTPS absoluta completa necessária para a entrega. |
| Payload | Preservar os bytes exatos. Não reserializar nem normalizar. |
| Content type | Preservar exatamente. |
| Headers customizados | Preservar nomes e valores necessários para novas tentativas. |
| Estado da entrega | Preservar o `DeliveryState` atual. |
| Contagem de tentativas | Incrementar exatamente uma vez a cada claim bem-sucedido. |
| Próxima tentativa | Preservar o agendamento de retry de itens pendentes/falhos. |
| Último erro | Preservar o último erro neutro de transporte quando informado. |
| Token do lease | Persistir o token opaco de propriedade atual, ainda que ele não seja exposto em `WebhookDeliverySnapshot`. |
| Expiração do lease | Persistir o instante em que a propriedade atual expira. |

`InMemoryWebhookDeliveryStore` implementa o protocolo comportamental, mas é local ao processo e perde todo o estado quando o processo termina. Deve ser usado apenas em testes, exemplos e cenários locais.

## Enqueue idempotente

`EnqueueAsync` usa `WebhookMessage.Id` como chave estável de idempotência.

O primeiro enqueue bem-sucedido persiste a mensagem e o estado inicial de agendamento. Qualquer enqueue posterior com o mesmo ID deve retornar `AlreadyExists` com a entrega atualmente armazenada e não pode sobrescrever payload, destino, headers, agendamento, tentativas ou estado terminal, mesmo que a nova mensagem tenha dados diferentes.

Stores de produção devem impor essa invariável no limite da persistência, por exemplo com chave única, insert condicional, compare-and-set ou primitiva equivalente da tecnologia escolhida.

## Claims atômicos e leases

`ClaimDueAsync` pode fazer claim de entregas pendentes ou falhas cujo `NextAttemptAt` venceu e de entregas `InProgress` cujo lease anterior expirou.

Para cada claim bem-sucedido a implementação deve, de forma atômica:

1. confirmar que a entrega está elegível;
2. movê-la para `InProgress`;
3. incrementar `AttemptCount` exatamente uma vez;
4. atribuir um novo token de lease não vazio;
5. definir a expiração do lease;
6. retornar um `WebhookDeliveryLease` representando essa propriedade.

Workers concorrentes nunca podem receber ao mesmo tempo leases válidos para a mesma entrega. Um `lock` em memória só atende esse requisito para uma implementação local ao processo, como `InMemoryWebhookDeliveryStore`; ele não é suficiente para um store de produção compartilhado por múltiplos processos ou hosts.

O mecanismo concreto de atomicidade é responsabilidade do adapter. Transações, updates condicionais, concorrência otimista, tokens de versão, compare-and-set ou primitivas específicas do armazenamento são abordagens válidas.

## Renovação de lease e propriedade obsoleta

`RenewLeaseAsync` deve preservar o mesmo token, não pode incrementar `AttemptCount` e não pode encurtar um lease ainda válido.

Toda operação de alteração de estado que recebe um `WebhookDeliveryLease` deve verificar que:

- a entrega ainda existe;
- continua em `InProgress`;
- o token informado continua sendo o proprietário atual;
- o lease ainda não expirou no instante da operação.

Se a propriedade estiver obsoleta, substituída, expirada ou inválida, a operação deve lançar `WebhookDeliveryStoreConcurrencyException` sem alterar o estado atual.

Essa validação precisa acontecer no limite da persistência. Ler a propriedade e atualizar depois, sem proteção de concorrência, introduz uma race condition e não atende ao contrato.

## Transições de estado

Uma implementação compatível deve aplicar estas transições observáveis:

| Operação | Resultado |
| --- | --- |
| `ScheduleRetryAsync` | `Failed`, mantém a contagem de tentativas, registra `LastError`, salva `NextAttemptAt` e limpa o lease. |
| `MarkSucceededAsync` | `Succeeded`, limpa agendamento e lease e torna a entrega terminal. |
| `MarkPermanentlyFailedAsync` | `PermanentlyFailed`, registra `LastError`, limpa agendamento e lease e torna a entrega terminal. |
| `DeadLetterAsync` | `DeadLettered`, registra `LastError`, limpa agendamento e lease e torna a entrega terminal. |

Entregas terminais não podem voltar a ser elegíveis para claim no fluxo normal.

## Cancelamento e falhas de persistência

Todas as operações assíncronas do store recebem `CancellationToken`. Um token já cancelado deve ser observado antes de uma alteração de estado externamente visível ser confirmada.

Falhas do backend devem ser propagadas para o chamador. Um store nunca pode reportar uma transição como bem-sucedida se a gravação durável correspondente falhou ou é sabidamente não confirmada. Adapters específicos devem incluir testes de fault injection para falhas de transação, conectividade, conflitos ou erros equivalentes do backend, pois o contrato genérico não consegue produzir essas falhas artificialmente.

## Testes de conformidade

O repositório contém uma suíte comportamental reutilizável em:

`tests/ReliableWebhooks.Tests/Conformance/WebhookDeliveryStoreConformanceTests.cs`

Todo adapter de persistência mantido no repositório deve derivar seu fixture dessa suíte e fornecer um store novo e isolado:

```csharp
public sealed class MyStoreConformanceTests : WebhookDeliveryStoreConformanceTests
{
    protected override IWebhookDeliveryStore CreateStore()
    {
        return CreateIsolatedStoreForTest();
    }
}
```

A suíte valida enqueue idempotente, claims concorrentes, filtragem de itens vencidos, reclaim após expiração, rejeição de owner obsoleto, renovação, retry, estados terminais e operações pré-canceladas. Cada adapter concreto ainda deve adicionar testes específicos para durabilidade real, unicidade na persistência, concorrência entre processos/hosts quando aplicável, schema/migrations e falhas injetadas do backend.

## Dependency injection

ReliableWebhooks nunca registra implicitamente um store de produção. A aplicação escolhe a implementação:

```csharp
services.AddSingleton<IWebhookDeliveryStore, MyDurableWebhookStore>();

ReliableWebhooksBuilder webhooks = services.AddReliableWebhooks();
webhooks.AddHostedDispatcher();
```

O lifetime escolhido deve respeitar os requisitos de thread safety e gerenciamento de recursos do adapter. A integração de DI do ReliableWebhooks aceita registros singleton, scoped e transient de `IWebhookDeliveryStore`. Serviços singleton criados pela DI, como `IWebhookEnqueueService`, `WebhookDispatcher` e o hosted dispatcher, não capturam o store a partir do provider raiz; eles resolvem o store dentro de um escopo curto de operação para cada chamada ao store.

Adapters scoped e transient ainda precisam coordenar por meio de um estado durável/compartilhado. Uma instância scoped pode encapsular uma sessão de banco, unidade de trabalho ou client scoped, mas escopos independentes de operação precisam observar as mesmas entregas persistidas, leases e tokens de concorrência.

## Garantias de entrega

O core define o protocolo; o store fornece a durabilidade real.

Com um store durável compatível, ReliableWebhooks é projetado para entrega outbound **at-least-once**. Ele não oferece entrega HTTP exactly-once. Os receivers devem ser idempotentes e tolerar duplicatas, inclusive quando um worker perde o lease depois que o endpoint remoto já aceitou a requisição.
