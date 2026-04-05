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
$SharedDllLockPattern = "because it is being used by another process"

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

function Invoke-DotnetCommandWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ActionLabel,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )

    $attempt = 1
    while ($attempt -le 2) {
        $output = & $Command 2>&1
        $exitCode = $LASTEXITCODE
        if ($output) {
            $output | ForEach-Object { Write-Host $_ }
        }

        if ($exitCode -eq 0) {
            return
        }

        $combinedOutput = ($output | Out-String)
        $hasDllLock = $combinedOutput -match [regex]::Escape($SharedDllLockPattern)
        if (-not $hasDllLock -or $attempt -eq 2) {
            Write-Host "$ActionLabel falhou." -ForegroundColor Red
            exit 1
        }

        Write-Step "Detectado bloqueio temporario de DLL do .NET. Reiniciando build server e tentando novamente..."
        dotnet build-server shutdown | Out-Host
        Start-Sleep -Seconds 1
        $attempt++
    }
}

Stop-NXProjectCommunityProcess

if ($Clean) {
    Write-Step "Limpando build anterior..."
    Invoke-DotnetCommandWithRetry -ActionLabel "O clean" -Command {
        dotnet clean $ProjectFile -c $Configuration --nologo -v q
    }
}

Write-Step "Restaurando pacotes..."
Invoke-DotnetCommandWithRetry -ActionLabel "O restore" -Command {
    dotnet restore $ProjectFile --nologo -v q
}

Write-Step "Compilando Community ($Configuration)..."
Invoke-DotnetCommandWithRetry -ActionLabel "A compilacao" -Command {
    dotnet build $ProjectFile -c $Configuration --nologo --no-restore
}

Write-Host ""
Write-Host "Build Community concluido com sucesso!" -ForegroundColor Green
Write-Host "  Saida: $OutputDir" -ForegroundColor DarkGray

if ($Run) {
    Write-Step "Iniciando aplicacao..."
    Start-Process $Exe
}
