<#
.SYNOPSIS
Gera o NXProject-Setup (self-contained, multi-arquivo): instalador leve que ja
carrega, soltos ao lado do proprio exe, o runtime .NET/WPF e as bibliotecas de
terceiros (PdfSharp, WebView2, CommunityToolkit.Mvvm — raramente mudam). Na
instalacao, ele copia esses arquivos e baixa do GitHub so a versao mais recente
do NXProject (NXProject.Community-Release.zip), sempre self-contained.

Rode este script quando as dependencias NuGet do projeto mudarem (upgrade de
pacote) ou o codigo do proprio Setup mudar. Releases normais do app usam so
release-community-new-version.ps1.
#>
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [string]$TagName = "",

    [switch]$Upload
)

$SolutionDir = $PSScriptRoot
$CommunityProject = Join-Path $SolutionDir "NXProject.Community\NXProject.Community.csproj"
$SetupProject = Join-Path $SolutionDir "NXProject-Setup\NXProject-Setup.csproj"
$Runtime = "win-x64"
$PublishDir = Join-Path $SolutionDir "dist\community\publish-$Runtime"
$SetupPublishDir = Join-Path $SolutionDir "dist\setup\publish-$Runtime"
$SetupOutputZip = Join-Path $SolutionDir "dist\setup\NXProject-Setup.zip"

function Write-Step($msg) {
    Write-Host ""
    Write-Host ">> $msg" -ForegroundColor Cyan
}

# Arquivos do proprio codigo do NXProject — NAO sao copiados para o Setup
# (mudam a cada release; vem sempre online via NXProject.Community-Release.zip).
$CorePrefixes = @("NXProject.Community", "NXProject.Shared")

function Test-IsCoreFile([string]$FileName) {
    foreach ($prefix in $CorePrefixes) {
        if ($FileName -like "$prefix*") { return $true }
    }
    return $false
}

Write-Step "Publicando NXProject Community self-contained ($Runtime) para obter runtime e libs base..."
if (Test-Path $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}
dotnet publish $CommunityProject -c $Configuration -r $Runtime --self-contained true -o $PublishDir --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Falha ao publicar NXProject.Community." -ForegroundColor Red
    exit 1
}

Write-Step "Publicando NXProject-Setup self-contained ($Runtime)..."
if (Test-Path $SetupPublishDir) {
    Remove-Item -LiteralPath $SetupPublishDir -Recurse -Force
}
dotnet publish $SetupProject -c $Configuration -r $Runtime --self-contained true -o $SetupPublishDir --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Falha ao publicar NXProject-Setup." -ForegroundColor Red
    exit 1
}

$builtExe = Join-Path $SetupPublishDir "NXProject-Setup.exe"
if (-not (Test-Path $builtExe)) {
    Write-Host "Executavel do instalador nao encontrado apos publish: $builtExe" -ForegroundColor Red
    exit 1
}

Write-Step "Copiando bibliotecas de terceiros para a pasta do Setup (fora do nucleo do NXProject)..."
$allFiles = Get-ChildItem -Path $PublishDir -Recurse -File
$ownLibFiles = $allFiles | Where-Object {
    -not (Test-IsCoreFile $_.Name) -and
    $_.Name -ne "NXProject.Community.deps.json" -and
    $_.Name -ne "NXProject.Community.runtimeconfig.json"
}
$coreFiles = $allFiles | Where-Object { Test-IsCoreFile $_.Name }

foreach ($f in $ownLibFiles) {
    $rel = $f.FullName.Substring($PublishDir.Length).TrimStart('\', '/')
    $dest = Join-Path $SetupPublishDir $rel
    $destFolder = Split-Path $dest -Parent
    if (-not (Test-Path $destFolder)) { New-Item -ItemType Directory -Path $destFolder -Force | Out-Null }
    Copy-Item -Path $f.FullName -Destination $dest -Force
}

Write-Host "  Bibliotecas de terceiros copiadas: $($ownLibFiles.Count)" -ForegroundColor DarkGray
Write-Host "  Arquivos do nucleo (ficam fora, vem online): $($coreFiles.Count)" -ForegroundColor DarkGray

# Material de apresentacao — copiado junto para a pasta de instalacao do NXProject.
$presentationFiles = @(
    "NXProject_Gestao_Inteligente_DevOps.pptx",
    "NXProject_Intelligent_DevOps_Planning_EN.pptx"
)
foreach ($name in $presentationFiles) {
    $src = Join-Path $SolutionDir $name
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination (Join-Path $SetupPublishDir $name) -Force
        Write-Host "  Material incluido: $name" -ForegroundColor DarkGray
    }
}

Write-Step "Compactando NXProject-Setup.zip..."
if (Test-Path $SetupOutputZip) {
    Remove-Item -LiteralPath $SetupOutputZip -Force
}
Compress-Archive -Path (Join-Path $SetupPublishDir "*") -DestinationPath $SetupOutputZip -Force

$setupZipSizeMb = [Math]::Round((Get-Item $SetupOutputZip).Length / 1MB, 1)
Write-Host ""
Write-Host "NXProject-Setup.zip gerado com sucesso!" -ForegroundColor Green
Write-Host "  $SetupOutputZip ($setupZipSizeMb MB)" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Instalador self-contained: nao exige .NET pre-instalado para RODAR o Setup." -ForegroundColor Yellow
Write-Host "So precisa ser regenerado quando as dependencias NuGet do projeto mudarem." -ForegroundColor Yellow

if ($Upload) {
    if ([string]::IsNullOrWhiteSpace($TagName)) {
        Write-Host "Informe -TagName vX.Y.Z para publicar o asset na release do GitHub." -ForegroundColor Red
        exit 1
    }

    Write-Step "Publicando NXProject-Setup.zip na release $TagName..."
    gh release upload $TagName $SetupOutputZip --repo nexusxdata/NXProject --clobber
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Falha ao publicar NXProject-Setup.zip no GitHub." -ForegroundColor Red
        exit 1
    }

    Write-Host "Asset publicado:" -ForegroundColor Green
    Write-Host "  NXProject-Setup.zip" -ForegroundColor DarkGray
}
