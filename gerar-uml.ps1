<#
=====================================================================
 gerar-uml.ps1 — Documentacao de diagramas de classe (UML) do NXProject
=====================================================================

 OBJETIVO
   Gerar automaticamente os diagramas de classe (.puml) do codigo C#
   usando o PlantUmlClassDiagramGenerator (engenharia reversa via Roslyn),
   organizados por camada, dentro de docs/uml/.

 NAO renderiza PNG por padrao — apenas produz os arquivos .puml.
 Para renderizar, use a extensao PlantUML do VS Code, o site plantuml.com
 ou instale Java + plantuml.jar e rode com o parametro -Render.

 ---------------------------------------------------------------------
 TODO / REVISAO COM IA (nao feito hoje — apenas planejado):
   O dump automatico do PlantUmlClassDiagramGenerator vem "cru" e poluido:
     - mostra herança/interfaces bem, mas as ASSOCIACOES (ex.: Project tem
       lista de ProjectTask 1..*) saem como simples propriedades, sem setas;
     - gera um diagrama por arquivo, sem hierarquia de importancia;
     - inclui ruido (DTOs, helpers, tipos internos).
   Passo seguinte (a fazer depois): pedir a uma IA para CURAR o resultado:
     1. Consolidar num overview.puml focado nas relacoes que importam:
          MainViewModel -> Project -> ProjectTask (1..*)
          TaskViewModel -> ProjectTask
          *Service estaticos (TaskScheduleService, ProjectCalendarService,
          XmlProjectService, TfsImportService, BaselineService, ...)
     2. Remover ruido e agrupar por camada (Models / ViewModels / Services).
     3. Marcar padroes: MVVM (CommunityToolkit.Mvvm), [ObservableProperty],
        [RelayCommand].
     4. Renderizar PNG/SVG final e revisar legibilidade.
 ---------------------------------------------------------------------

 PRE-REQUISITOS
   - .NET SDK (dotnet) — ja usado no projeto.
   - dotnet tool PlantUmlClassDiagramGenerator (o script instala se faltar).
   - (Opcional, apenas para -Render) Java + plantuml.jar.

 USO
   ./gerar-uml.ps1                 # gera os .puml em docs/uml/
   ./gerar-uml.ps1 -Render         # tambem renderiza PNG (precisa Java+plantuml.jar)
   ./gerar-uml.ps1 -PlantUmlJar "C:\tools\plantuml.jar" -Render
=====================================================================
#>

[CmdletBinding()]
param(
    # Renderiza PNG a partir dos .puml (requer Java + plantuml.jar).
    [switch]$Render,
    # Caminho do plantuml.jar (usado somente com -Render).
    [string]$PlantUmlJar = "plantuml.jar"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$outDir = Join-Path $root "docs\uml"

# Camadas a documentar: rotulo => pasta de origem.
$camadas = [ordered]@{
    "models"     = "NXProject.Shared\Models"
    "viewmodels" = "NXProject.Shared\ViewModels"
    "services"   = "NXProject.Shared\Services"
    "community"  = "NXProject.Community\Views"
}

Write-Host ">> Documentacao UML do NXProject" -ForegroundColor Cyan

# 1. Garante o dotnet tool PlantUmlClassDiagramGenerator instalado.
if (-not (Get-Command "puml-gen" -ErrorAction SilentlyContinue)) {
    Write-Host ">> Instalando dotnet tool PlantUmlClassDiagramGenerator..." -ForegroundColor Yellow
    dotnet tool install --global PlantUmlClassDiagramGenerator
    # Garante o PATH dos global tools nesta sessao.
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
}

# 2. Gera um conjunto de .puml por camada (um arquivo por classe + include.puml).
foreach ($rotulo in $camadas.Keys) {
    $src = Join-Path $root $camadas[$rotulo]
    if (-not (Test-Path $src)) {
        Write-Host "   [SKIP] $rotulo — pasta nao encontrada: $src" -ForegroundColor DarkYellow
        continue
    }
    $dst = Join-Path $outDir $rotulo
    Write-Host ">> Gerando UML da camada '$rotulo'..." -ForegroundColor Green
    # -dir: percorre a pasta recursivamente e cria include.puml consolidando tudo.
    puml-gen $src $dst -dir -public
}

# 3. (Opcional) Renderiza PNG de cada include.puml.
if ($Render) {
    if (-not (Get-Command "java" -ErrorAction SilentlyContinue)) {
        Write-Host "!! -Render pedido, mas 'java' nao esta no PATH. Pulei o render." -ForegroundColor Red
    }
    elseif (-not (Test-Path $PlantUmlJar)) {
        Write-Host "!! plantuml.jar nao encontrado em '$PlantUmlJar'. Pulei o render." -ForegroundColor Red
        Write-Host "   Baixe em https://plantuml.com/download e informe via -PlantUmlJar." -ForegroundColor DarkYellow
    }
    else {
        Get-ChildItem -Path $outDir -Recurse -Filter "include.puml" | ForEach-Object {
            Write-Host ">> Renderizando PNG: $($_.FullName)" -ForegroundColor Green
            java -jar $PlantUmlJar -tpng $_.FullName
        }
    }
}

Write-Host ""
Write-Host "UML gerado em: $outDir" -ForegroundColor Cyan
Write-Host "Proximo passo (nao feito hoje): revisar/curar os .puml com IA — ver TODO no topo deste script." -ForegroundColor DarkCyan
