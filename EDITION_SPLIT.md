# NXProject Editions

## Estrutura

- `NXProject.Shared`: pasta raiz compartilhada que pode ir para o GitHub. Contem modelos, servicos e UI realmente compartilhada.
- `NXProject.Core`: base reservada da edicao proprietaria/comercial, incluindo calendario e recursos exclusivos.
- `NXProject.Community`: edicao comunitaria com tarefas, grafico Gantt e importacao/exportacao.
- `NXProject`: aplicativo desktop atual, que segue como base da edicao proprietaria/comercial.

## Regra pratica

1. Tudo que for comum entre `NXProject.Community` e `NXProject` deve ficar fisicamente dentro de `NXProject.Shared`.
2. `NXProject.Shared` e a pasta pensada para liberacao publica no GitHub.
3. Tudo que for base exclusiva da edicao comercial pode ir para `NXProject.Core`.
4. Recursos publicos ficam em `NXProject.Community`.
5. Recursos comerciais continuam no app `NXProject` ate que valha a pena extrair modulos fechados.

## O que ja esta em Shared

- modelos do projeto que nao sao exclusivos da versao proprietaria
- servicos de importacao e exportacao
- controles `TaskGrid` e `Gantt`
- tema compartilhado
- viewmodels compartilhados da grade e do Gantt

## O que fica em Core

- calendario do projeto
- regras e modelos exclusivos da versao proprietaria

## Candidatos a recursos proprietarios

- impressao e PDF
- alocacao avancada de recursos
- integracoes e IA
- relatorios empresariais
