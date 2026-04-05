param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Run,
    [switch]$Clean
)

$SolutionDir = $PSScriptRoot
$ProjectFile = Join-Path $SolutionDir "NXProject.Community\NXProject.Community.csproj"
$OutputDir = Join-Path $SolutionDir "NXProject.Community\bin\$Configuration\net10.0-windows"
$Exe = Join-Path $OutputDir "NXProject.Community.exe"

function Write-Step($msg) {
    Write-Host ""
    Write-Host ">> $msg" -ForegroundColor Cyan
}

function Stop-NXProjectCommunityProcess {
    $processes = Get-Process -Name "NXProject.Community" -ErrorAction SilentlyContinue
    if ($null -eq $processes) { return }

    Write-Step "Encerrando NXProject.Community em execucao..."
    $processes | Stop-Process -Force
}

Stop-NXProjectCommunityProcess

if ($Clean) {
    Write-Step "Limpando build anterior..."
    dotnet clean $ProjectFile -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Falha no clean." -ForegroundColor Red
        exit 1
    }
}

Write-Step "Restaurando pacotes..."
dotnet restore $ProjectFile --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "Falha no restore." -ForegroundColor Red
    exit 1
}

Write-Step "Compilando Community ($Configuration)..."
dotnet build $ProjectFile -c $Configuration --nologo --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Falha na compilacao." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Build Community concluido com sucesso!" -ForegroundColor Green
Write-Host "  Saida: $OutputDir" -ForegroundColor DarkGray

if ($Run) {
    Write-Step "Iniciando aplicacao..."
    Start-Process $Exe
}
