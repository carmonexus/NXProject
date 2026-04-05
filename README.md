# NXProject Community

Edicao comunitaria do NXProject para gerenciamento de tarefas, grafico Gantt e importacao/exportacao de projetos.

## Conteudo deste repositorio

- `NXProject.Shared`: modelos, servicos e UI compartilhada da edicao publica
- `NXProject.Community`: aplicativo desktop Community
- `NXProject.Community.sln`: solution publica da Community
- `setup-community-vscode.ps1`, `build-community.ps1`, `run-community.ps1`, `release-community.ps1`: scripts de preparacao, build, execucao e empacotamento

## Download rapido

O pacote compilado para usuario final esta em:

- `dist/community/NXProject.Community-Release.zip`

Se preferir baixar os componentes oficiais:

- Runtime para executar no Windows: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- SDK para compilar no VS Code: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- Download do VS Code: https://code.visualstudio.com/download

## Requisito do Windows

Para executar o aplicativo, a maquina deve ter instalado:

- `Microsoft .NET Desktop Runtime 10.0 (x64)`

Se o Windows ainda nao tiver esse runtime, instale primeiro e depois execute `NXProject.Community.exe`.

## Compilar no VS Code

Se voce nao quiser baixar o `.zip`, pode compilar o projeto no VS Code:

1. Instale o `.NET 10 SDK` pelo link oficial: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
2. Instale o VS Code: https://code.visualstudio.com/download
3. Abra a pasta do repositorio `NXProject` no VS Code.
4. Abra o terminal integrado do VS Code em `Terminal > New Terminal`.
5. Prepare o ambiente com o script abaixo na raiz do projeto:

```powershell
.\setup-community-vscode.ps1
```

6. Depois rode o build do executavel:

```powershell
.\build-community.ps1 -Configuration Release
```

7. Ao final da compilacao, o executavel sera gerado em:

```text
NXProject.Community\bin\Release\net10.0-windows\NXProject.Community.exe
```

Para abrir o executavel gerado, basta executar esse arquivo no Windows.

Se preferir executar diretamente em modo de desenvolvimento, use:

```powershell
.\run-community.ps1
```

## Como gerar o zip de distribuicao

```powershell
.\release-community.ps1 -Configuration Release
```

## Licenca e contato

- Empresa: Nexus XData Tecnologia Ltda
- Contato: `comercial.nexus.xdata@gamail.com`

A edicao Community possui licenca propria exibida na primeira execucao do aplicativo.

Resumo atual:

- gratuita para empresas com ate 20 funcionarios
- para empresas com ate 20 funcionarios, serao aceitos donativos
- empresas com mais de 20 funcionarios devem solicitar licenca por e-mail ate o prazo orcamentario ou, no maximo, durante o primeiro ano de uso
- para empresas acima de 20 funcionarios, o valor padrao atual e de `USD 1` por usuario por ano
- esse valor pode variar conforme a contratacao de suporte
