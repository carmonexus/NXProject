# NXProject Community Distribution

## O que publicar no Git

- `NXProject.Shared`
- `NXProject.Community`
- scripts da Community, como `build-community.ps1`, `run-community.ps1` e `release-community.ps1`

## O que manter fechado

- `NXProject`
- `NXProject.Core`

## Como gerar o pacote para usuario final

1. Execute:
   `.\release-community.ps1`
2. O pacote sera gerado em:
   `dist\community\NXProject.Community-Release.zip`

## O que vai dentro do ZIP

- executavel `NXProject.Community.exe`
- dependencias da build
- `README-INSTALACAO.txt`

## Aviso sobre o pacote

O `.zip` de distribuicao foi gerado em ambiente com antivirus McAfee. Se houver qualquer duvida sobre o executavel distribuido, ele pode ser gerado novamente com base no codigo-fonte publico deste repositorio.

## Requisito para o usuario final

O usuario deve instalar o `Microsoft .NET Desktop Runtime 10.0 (x64)` antes de abrir o aplicativo, caso a maquina ainda nao tenha esse runtime.

## Observacao

Se no futuro voce quiser remover a dependencia de instalacao do .NET, o proximo passo e publicar a Community como `self-contained`.
