# Configuração do SonarQube Cloud

**Português** | [English](sonarqube-cloud.md)

ReliableWebhooks possui análise opcional no SonarQube Cloud em `.github/workflows/sonar.yml`. O workflow permanece desabilitado até que o secret `SONAR_TOKEN` seja configurado no repositório.

## O que o workflow faz

Quando habilitado, o workflow executa em pull requests para `main` e em pushes para `main`. Ele restaura o SonarScanner for .NET fixado no repositório, restaura dependências NuGet em modo bloqueado, executa build Release não incremental, roda os testes com saída OpenCover, importa a cobertura, informa a versão do projeto e aguarda o Quality Gate.

O CI principal continua responsável pelo build normal, warnings como erros, testes, validação do pacote e artefatos de CI. O SonarQube Cloud complementa essa baseline; ele não a substitui.

## Configurar o projeto no SonarQube Cloud

Crie ou importe o projeto ReliableWebhooks no SonarQube Cloud e associe-o a este repositório GitHub. O workflow deriva os valores padrão a partir do próprio repositório:

```text
project key  = <github-owner>_<repository-name>
organization = <github-owner>
host         = https://sonarcloud.io
```

Se o projeto Sonar usar coordenadas diferentes, configure estas Repository Variables no GitHub:

```text
SONAR_PROJECT_KEY
SONAR_ORGANIZATION
SONAR_HOST_URL
```

## Configurar o secret

Crie o seguinte Repository Secret para GitHub Actions:

```text
SONAR_TOKEN=<token autorizado a analisar o projeto SonarQube Cloud>
```

Nunca versione o token. Se ele não existir ou estiver vazio, o workflow do Sonar encerra sem iniciar uma análise.

## Automatic Analysis

Mantenha **Automatic Analysis desabilitado** no SonarQube Cloud enquanto este workflow estiver habilitado. A análise via CI é responsável pelo build e pela importação de cobertura, e executar os dois modos pode gerar análises duplicadas ou inconsistentes.

## Versão do projeto e New Code

O workflow informa `sonar.projectVersion` usando o maior release tag SemVer alcançável. Antes do primeiro release tag, ele usa o `PackageVersion` do MSBuild como fallback.

Durante o desenvolvimento da v0.1.0, o fallback é portanto `0.1.0` até existir um release tag alcançável a partir de `main`.

A estratégia **Previous Version** de New Code funciona bem com esse modelo de release.

## Quality Gate

O workflow usa:

```text
sonar.qualitygate.wait=true
sonar.qualitygate.timeout=300
```

Quando a análise é executada, um Quality Gate reprovado também reprova o job no GitHub Actions. Corrija o problema de qualidade ou ajuste deliberadamente a política do projeto; não desabilite o gate apenas para deixar o CI verde.

## Cobertura

Os testes produzem saída OpenCover com Coverlet MTP e o Sonar importa:

```text
sonar.cs.opencover.reportsPaths=**/coverage.opencover*.xml
```

O artefato Cobertura produzido pelo CI principal é independente do relatório utilizado pelo Sonar.

## Pull requests de forks

O GitHub não disponibiliza Repository Secrets como `SONAR_TOKEN` para workflows disparados a partir de forks. Nesse caso, o workflow segue o caminho desabilitado e um job Sonar verde não significa que a contribuição foi analisada pelo Sonar.

Não troque ingenuamente para `pull_request_target` enquanto fizer checkout ou executar código não confiável de fork com acesso a secrets.

## Troubleshooting

### SonarQube Cloud aparece como desabilitado

Confirme que `SONAR_TOKEN` existe em **Settings → Secrets and variables → Actions → Secrets** e está disponível para o evento do workflow.

### As coordenadas do projeto não são resolvidas

Confira project key e organization no Sonar. Configure `SONAR_PROJECT_KEY` e `SONAR_ORGANIZATION` quando os valores derivados do GitHub não corresponderem ao projeto Sonar.

### O Quality Gate reprova

Abra o projeto no SonarQube Cloud e veja as condições reprovadas. Um resultado `QUALITY GATE STATUS: FAILED` significa que a integração executou corretamente e o projeto não atendeu ao gate configurado.

### A cobertura não aparece

Confirme que o passo de testes gerou `coverage.opencover*.xml` e que o log do scanner registra a importação do arquivo.

### A versão do projeto não avança

Confirme que os releases criam tags SemVer válidos no padrão `v*.*.*`, que esses tags são alcançáveis a partir de `main` e que o checkout do Sonar mantém `fetch-depth: 0`.

## Segurança

- mantenha `SONAR_TOKEN` somente em GitHub Secrets;
- use Repository Variables para coordenadas não secretas;
- não imprima credenciais nos diagnósticos do workflow;
- não execute código não confiável de forks em workflows privilegiados;
- mantenha as permissões do workflow somente leitura salvo necessidade explícita.
