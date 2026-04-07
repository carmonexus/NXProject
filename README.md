# NXProject Community 1.0.1

Edicao comunitaria do NXProject para gerenciamento de tarefas, grafico Gantt e importacao/exportacao de projetos.

## Capturas de tela

As imagens abaixo ficam versionadas no repositorio para visualizacao direta no GitHub:

![Tela principal do NXProject Community](ScreenShot/Tela01.png)
![Tela com hierarquia e Gantt](ScreenShot/Tela02.png)
![Tela de configuracao e acompanhamento](ScreenShot/Tela03.png)

## Conteudo deste repositorio

- `NXProject.Shared`: modelos, servicos e UI compartilhada da edicao publica
- `NXProject.Community`: aplicativo desktop Community
- `NXProject.Community.sln`: solution publica da Community
- `setup-community-vscode.ps1`, `build-community.ps1`, `run-community.ps1`, `release-community.ps1`: scripts de preparacao, build, execucao e empacotamento

## Download rapido

O pacote compilado para usuario final esta em:

- `dist/community/NXProject.Community-Release.zip`
- [Baixar NXProject Community 1.0.1 (.zip)](dist/community/NXProject.Community-Release.zip)

O `.zip` de distribuicao foi gerado em ambiente com antivirus McAfee. Se houver qualquer duvida sobre o binario, o executavel pode ser gerado novamente a partir deste codigo-fonte seguindo as instrucoes de compilacao abaixo.

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

## Observacao sobre build local

O aviso `NU1900` do NuGet era um caso conhecido neste ambiente quando a checagem online de vulnerabilidades nao conseguia acessar `https://api.nuget.org/v3/index.json`.

Como isso nao indicava vulnerabilidade confirmada no projeto e apenas falha de consulta online, ele foi suprimido nos arquivos de projeto para nao poluir o build local.

## Licenca e contato

- Empresa: Nexus XData Tecnologia Ltda
- Contato: `comercial.nexus.xdata@gmail.com`

A edicao Community possui licenca propria exibida na primeira execucao do aplicativo.

Resumo atual:

- gratuita para empresas com ate 20 funcionarios
- para empresas com ate 20 funcionarios, serao aceitos donativos
- instituicoes de educacao possuem licenca 100% gratuita, com pedido de registro por e-mail apenas para catalogo e citacao do uso da ferramenta quando aplicavel
- empresas com mais de 20 funcionarios devem solicitar licenca por e-mail ate o prazo orcamentario ou, no maximo, durante o primeiro ano de uso
- para empresas acima de 20 funcionarios, o valor padrao atual e de `USD 10` por usuario por ano
- esse valor pode variar conforme a contratacao de suporte
- a versao comercial podera incluir integracoes com OpenAI, Claude AI e outras plataformas de inteligencia artificial
