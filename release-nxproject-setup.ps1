<#
.SYNOPSIS
Gera o NXProject-Setup.exe: instalador leve (framework-dependent, requer
.NET 10 Desktop Runtime ja instalado) que ja embute as bibliotecas de
terceiros do NXProject (PdfSharp, WebView2, CommunityToolkit.Mvvm — raramente
mudam), e baixa do GitHub so o nucleo do NXProject (NXProject.Community-
Release.zip), que muda a cada release.

Rode este script quando as dependencias NuGet do projeto mudarem (upgrade de
pacote). Releases normais do app usam so release-community-new-version.ps1.
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
$PayloadDir = Join-Path $SolutionDir "NXProject-Setup\Payload"
$PayloadZip = Join-Path $PayloadDir "own-libs.zip"
$SetupPublishDir = Join-Path $SolutionDir "dist\setup\publish-$Runtime"
$SetupOutputExe = Join-Path $SolutionDir "dist\setup\NXProject-Setup.exe"
$SetupOutputZip = Join-Path $SolutionDir "dist\setup\NXProject-Setup.zip"

function Write-Step($msg) {
    Write-Host ""
    Write-Host ">> $msg" -ForegroundColor Cyan
}

# Arquivos do proprio codigo do NXProject — NAO entram no payload do Setup
# (mudam a cada release; vem sempre online via NXProject.Community-Release.zip).
$CorePrefixes = @("NXProject.Community", "NXProject.Shared")

function Test-IsCoreFile([string]$FileName) {
    foreach ($prefix in $CorePrefixes) {
        if ($FileName -like "$prefix*") { return $true }
    }
    return $false
}

Write-Step "Publicando NXProject Community framework-dependent ($Runtime) para obter as libs..."
if (Test-Path $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}
dotnet publish $CommunityProject -c $Configuration -r $Runtime --self-contained false -o $PublishDir --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Falha ao publicar NXProject.Community." -ForegroundColor Red
    exit 1
}

Write-Step "Selecionando bibliotecas de terceiros (fora do nucleo do NXProject)..."
if (Test-Path $PayloadDir) {
    Get-ChildItem -Path $PayloadDir -Filter "*.zip" -ErrorAction SilentlyContinue | Remove-Item -Force
} else {
    New-Item -ItemType Directory -Path $PayloadDir -Force | Out-Null
}

$stagingDir = Join-Path $SolutionDir "dist\setup\ownlibs-staging"
if (Test-Path $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

$allFiles = Get-ChildItem -Path $PublishDir -Recurse -File
$ownLibFiles = $allFiles | Where-Object {
    -not (Test-IsCoreFile $_.Name) -and
    $_.Name -ne "NXProject.Community.deps.json" -and
    $_.Name -ne "NXProject.Community.runtimeconfig.json"
}
$coreFiles = $allFiles | Where-Object { Test-IsCoreFile $_.Name }

foreach ($f in $ownLibFiles) {
    $rel = $f.FullName.Substring($PublishDir.Length).TrimStart('\', '/')
    $dest = Join-Path $stagingDir $rel
    $destFolder = Split-Path $dest -Parent
    if (-not (Test-Path $destFolder)) { New-Item -ItemType Directory -Path $destFolder -Force | Out-Null }
    Copy-Item -Path $f.FullName -Destination $dest -Force
}

Write-Host "  Bibliotecas de terceiros (embutidas no Setup): $($ownLibFiles.Count)" -ForegroundColor DarkGray
Write-Host "  Arquivos do nucleo (ficam fora, vem online): $($coreFiles.Count)" -ForegroundColor DarkGray

# Material de apresentacao — copiado junto para a pasta de instalacao do NXProject.
$presentationFiles = @(
    "NXProject_Gestao_Inteligente_DevOps.pptx",
    "NXProject_Intelligent_DevOps_Planning_EN.pptx"
)
foreach ($name in $presentationFiles) {
    $src = Join-Path $SolutionDir $name
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination (Join-Path $stagingDir $name) -Force
        Write-Host "  Material incluido: $name" -ForegroundColor DarkGray
    }
}

Write-Step "Compactando payload de bibliotecas..."
Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $PayloadZip -Force
Remove-Item -LiteralPath $stagingDir -Recurse -Force

$payloadSizeKb = [Math]::Round((Get-Item $PayloadZip).Length / 1KB, 0)
Write-Host "  Payload gerado: $PayloadZip ($payloadSizeKb KB)" -ForegroundColor Green

Write-Step "Publicando NXProject-Setup.exe (framework-dependent, $Runtime)..."
if (Test-Path $SetupPublishDir) {
    Remove-Item -LiteralPath $SetupPublishDir -Recurse -Force
}
dotnet publish $SetupProject -c $Configuration -r $Runtime --self-contained false -o $SetupPublishDir --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Falha ao publicar NXProject-Setup." -ForegroundColor Red
    exit 1
}

$builtExe = Join-Path $SetupPublishDir "NXProject-Setup.exe"
if (-not (Test-Path $builtExe)) {
    Write-Host "Executavel do instalador nao encontrado apos publish: $builtExe" -ForegroundColor Red
    exit 1
}

$destFolder = Split-Path $SetupOutputExe -Parent
if (-not (Test-Path $destFolder)) { New-Item -ItemType Directory -Path $destFolder -Force | Out-Null }
Copy-Item -Path $builtExe -Destination $SetupOutputExe -Force

if (Test-Path $SetupOutputZip) {
    Remove-Item -LiteralPath $SetupOutputZip -Force
}
Compress-Archive -Path $SetupOutputExe -DestinationPath $SetupOutputZip -Force

$setupSizeKb = [Math]::Round((Get-Item $SetupOutputExe).Length / 1KB, 0)
$setupZipSizeKb = [Math]::Round((Get-Item $SetupOutputZip).Length / 1KB, 0)
Write-Host ""
Write-Host "NXProject-Setup gerado com sucesso!" -ForegroundColor Green
Write-Host "  $SetupOutputExe ($setupSizeKb KB)" -ForegroundColor DarkGray
Write-Host "  $SetupOutputZip ($setupZipSizeKb KB)" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Requer .NET 10 Desktop Runtime (x64) ja instalado na maquina de destino." -ForegroundColor Yellow
Write-Host "So precisa ser regenerado quando as dependencias NuGet do projeto mudarem." -ForegroundColor Yellow
Write-Host "Na release do GitHub, publique os dois assets; prefira divulgar o .zip para reduzir bloqueios de download de .exe." -ForegroundColor Yellow

if ($Upload) {
    if ([string]::IsNullOrWhiteSpace($TagName)) {
        Write-Host "Informe -TagName vX.Y.Z para publicar os assets na release do GitHub." -ForegroundColor Red
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
