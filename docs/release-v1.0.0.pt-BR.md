# Release v1.0.0 do ReliableWebhooks

Este documento registra o contrato de release do primeiro pacote público do ReliableWebhooks.

English: [`release-v1.0.0.md`](release-v1.0.0.md).

## Distribuição

O workflow oficial de release valida um único release candidate imutável contendo:

- `ReliableWebhooks.<version>.nupkg`;
- `ReliableWebhooks.<version>.snupkg`;
- `release-manifest.json`;
- `SHA256SUMS`.

O artifact validado é reutilizado sem rebuild para todos os destinos de distribuição:

- publicação do pacote e do pacote de símbolos no NuGet.org com GitHub OIDC e NuGet Trusted Publishing;
- publicação no GitHub Packages com o `GITHUB_TOKEN` escopado;
- anexos da GitHub Release com pacote, símbolos, manifest e checksums;
- attestations do GitHub para os mesmos arquivos de release.

Nenhuma API key duradoura do NuGet é armazenada no repositório.

## Pré-requisitos externos

A publicação oficial exige:

1. uma política de Trusted Publishing no NuGet.org autorizando este repositório, `.github/workflows/release.yml` e o GitHub Environment `release`;
2. a variável de repositório `NUGET_USER` com o perfil NuGet.org autorizado por essa política;
3. as aprovações ou regras de proteção do GitHub Environment `release` exigidas pelo mantenedor.

`NUGET_USER` é uma repository variable, não um secret. Se ela estiver ausente durante uma release oficial, o job de NuGet falha com erro claro de configuração e a GitHub Release não é publicada.

## Sequência dos jobs

O workflow `Release` usa um único grupo global de concurrency para release com `cancel-in-progress: false`. Operações que alteram estado de release aguardam umas às outras e não são canceladas pela metade.

Pull requests que alteram arquivos relevantes de release/package executam apenas o dry-run de validação em `build-and-pack`. Execuções manuais por `workflow_dispatch` são pedidos oficiais de release e devem partir de `main`, com entrada obrigatória `version` sem prefixo `v`.

Os jobs oficiais rodam nesta ordem:

1. `build-and-pack`: valida SemVer e branch, restaura dependências em locked mode, verifica formatação, compila em Release, executa testes, valida `PublicApi.v1.0.0.txt`, empacota, valida metadados do pacote, símbolos e Source Link, executa validação de consumidor limpo com `IWebhookDeliveryStore` customizado, escreve manifest/checksums e publica um único artifact imutável de release candidate.
2. `ensure-release-tag`: cria `v<version>` apenas para o SHA validado. Se a tag já aponta para o mesmo SHA, aceita; se aponta para qualquer outro SHA, falha. Tags existentes nunca são movidas.
3. `publish-nuget`: entra no Environment `release`, troca GitHub OIDC por uma API key temporária com `NuGet/login`, baixa o artifact validado, confere manifest/checksums e publica somente esses arquivos baixados.
4. `publish-github-packages`: entra no Environment `release`, baixa o mesmo artifact validado, confere manifest/checksums e publica o pacote validado no GitHub Packages sem rebuild.
5. `github-release`: roda somente após os dois registries passarem, verifica o mesmo artifact, cria ou retoma um draft de release, anexa os arquivos validados, gera attestations e publica a GitHub Release.

## Publicação idempotente

A publicação é feita por `scripts/publish-release-package.cs`, deixando o YAML focado em orquestração.

Para NuGet.org, o helper consulta o pacote no NuGet v3 flat container antes do push:

- se `ReliableWebhooks <version>` está ausente, publica o `.nupkg` validado com publicação implícita de símbolos desabilitada;
- se o pacote já existe, baixa o artifact remoto e compara seu conteúdo com o artifact local;
- metadados de repository signing adicionados pelo NuGet.org são ignorados na comparação de conteúdo;
- se o pacote existente diverge, a publicação falha fechada;
- após um push novo, o helper aguarda convergência do flat container por até 60 tentativas com intervalo de 10 segundos;
- `404` durante essa janela é tratado como indexação pendente e registrado antes de nova tentativa;
- `200` deve baixar e validar o conteúdo do pacote;
- qualquer resposta inesperada falha fechada;
- o `.snupkg` validado é enviado em etapa dedicada depois que a identidade do pacote primário foi provada.

Para GitHub Packages, o helper suporta reruns seguros:

- se a versão está ausente, publica o `.nupkg` validado;
- se a versão já existe, baixa o artifact remoto e aceita apenas quando o conteúdo corresponde;
- se o conteúdo diverge, a publicação falha;
- após um push novo, aguarda visibilidade do pacote e valida o artifact visível antes de continuar.

Isso torna seguro reexecutar uma release após publicação parcial: trabalho já concluído em registry é verificado em vez de ignorado cegamente, enquanto conflito de conteúdo bloqueia a release antes do anúncio.

## Integridade do release candidate

`release-manifest.json` registra nomes e SHA-256 do pacote e do pacote de símbolos para o commit validado. `SHA256SUMS` é determinístico e contém uma linha para `.nupkg`, `.snupkg` e `release-manifest.json`, nessa ordem.

Todo job de publicação baixa o artifact produzido por `build-and-pack` e o verifica com `scripts/release-candidate.cs` antes de publicar em registry ou release. Nenhum pacote é reconstruído entre validação e publicação.

## Gate do release candidate

O release candidate v1.0.0 deve ser validado a partir do commit final já integrado em `main`. Branches de feature e pull requests podem gerar pacotes locais e manifests de release candidate para revisão, mas não devem criar a tag oficial `v1.0.0`, publicar no NuGet.org, publicar no GitHub Packages ou publicar uma GitHub Release.

Antes de rodar o workflow oficial, verifique que:

- `CI`, `CodeQL` e `Dependency Review` estão verdes no commit protegido de `main`;
- `dotnet tool restore`, restore em locked mode, formatação, build Release, todos os testes, cobertura, validação de pacote, validação de API pública, sample E2E, validação de consumidor limpo, geração de manifest/checksums e verificação do release candidate passam para o mesmo SHA;
- `.nupkg`, `.snupkg`, `release-manifest.json` e `SHA256SUMS` gerados são os artifacts exatos consumidos pelos jobs de publicação;
- a política de Trusted Publishing do NuGet.org, a variável `NUGET_USER` e o Environment `release` estão configurados;
- nenhuma API key duradoura do NuGet, segredo de assinatura, URL de webhook com credencial, payload, header de autorização, cookie ou outro segredo de entrega é commitado ou emitido nos artifacts.

## Snapshot de API pública

`src/ReliableWebhooks/PublicApi.v1.0.0.txt` registra tipos e membros visíveis externamente, incluindo acessibilidade, modificadores, constraints genéricas, constantes, valores padrão de parâmetros, custom modifiers e anotações nullable de C# para retornos, parâmetros, propriedades, campos, arrays e genéricos aninhados.

O snapshot é um gate de compatibilidade de código-fonte para a superfície pública v1.0.0. Ele não afirma validar todos os detalhes possíveis de metadata de código-fonte, como nomes de elementos de tuplas ou atributos customizados arbitrários.

## Garantias da v1.0.0

- entrega at-least-once quando usada com um `IWebhookDeliveryStore` durável e aderente ao contrato;
- semântica de enqueue idempotente por ID estável;
- protocolo atômico de claim/lease com rejeição de owner obsoleto, renovação de lease ativa e expectativas documentadas sobre relógio autoritativo;
- retry/backoff/jitter com `Retry-After` do servidor respeitado quando agenda depois do backoff local;
- dispatch concorrente limitado, shutdown gracioso e isolamento de mensagens problemáticas por entrega;
- assinatura HMAC-SHA256 com envelope autenticado versionado, vínculo de metadados gerados e força mínima de segredo de 256 bits;
- entrega HTTP exigindo HTTPS por padrão, redirects/cookies desabilitados no client gerenciado por DI, supressão de logs de request, hooks de política anti-SSRF e proteção de headers reservados;
- validação de headers customizados, IDs estáveis, tipos de evento, content types, limites de recursos de mensagem e cardinalidade segura de métricas;
- logs estruturados, traces e métricas via APIs padrão do .NET sem payload, credenciais, secrets de destino, assinaturas ou cardinalidade alta por padrão;
- integração com Microsoft DI e hosted dispatcher opcional, com registros singleton, scoped e transient de store suportados.

## Limitações da v1.0.0

- nenhum store durável de produção é incluído;
- `InMemoryWebhookDeliveryStore` não sobrevive a reinícios do processo;
- entrega exactly-once não é garantida;
- idempotência no receiver continua sendo responsabilidade do receiver;
- replay/remediação de dead letters é responsabilidade da aplicação e da operação;
- adapters de persistência como EF Core, Dapper, Redis, arquivos e document stores são opcionais e independentes do pacote core;
- ciclo de vida e rotação de secrets não são fornecidos pelo pacote core;
- criptografia em repouso, controle de acesso, retenção, exclusão, proteção de backup e tratamento regulatório para payloads, destinos e metadados persistidos permanecem responsabilidades do store escolhido pelo consumidor e da fronteira da aplicação;
- publicação oficial no NuGet.org exige política externa de Trusted Publishing, variável de repositório e configuração de Environment do GitHub que não podem ser representadas inteiramente na árvore git.
