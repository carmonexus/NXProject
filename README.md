# NXProject Community

Edicao comunitaria do NXProject para gerenciamento de tarefas, grafico Gantt e importacao/exportacao de projetos.

## Conteudo deste repositorio

- `NXProject.Shared`: modelos, servicos e UI compartilhada da edicao publica
- `NXProject.Community`: aplicativo desktop Community
- `NXProject.Community.sln`: solution publica da Community
- `build-community.ps1`, `run-community.ps1`, `release-community.ps1`: scripts de build, execucao e empacotamento

## Download rapido

O pacote compilado para usuario final esta em:

- `dist/community/NXProject.Community-Release.zip`

## Requisito do Windows

Para executar o aplicativo, a maquina deve ter instalado:

- `Microsoft .NET Desktop Runtime 10.0 (x64)`

Se o Windows ainda nao tiver esse runtime, instale primeiro e depois execute `NXProject.Community.exe`.

## Como compilar

```powershell
.\build-community.ps1 -Configuration Release
```

## Como gerar o zip de distribuicao

```powershell
.\release-community.ps1 -Configuration Release
```

## Licenca e contato

- Empresa: Nexus XData Tecnologia Ltda
- Contato: `comercial.nexus.xdata@gamail.com`

A edicao Community possui licenca propria exibida na primeira execucao do aplicativo.
