param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Build,
    [switch]$Run,
    [switch]$Clean
)

$SolutionDir = $PSScriptRoot
$ProjectFile = Join-Path $SolutionDir "NXProject.Community\NXProject.Community.csproj"
$TestProjectFile = Join-Path $SolutionDir "NXTestUnit\NXTestUnit.csproj"
$OutputDir = Join-Path $SolutionDir "NXProject.Community\bin\$Configuration\net10.0-windows"
$Exe = Join-Path $OutputDir "NXProject.Community.exe"
$SharedDllLockPattern = "because it is being used by another process"

function Write-Step($msg) {
    Write-Host ""
    Write-Host ">> $msg" -ForegroundColor Cyan
}

function Get-ProjectVersion([string]$CsprojPath) {
    $content = Get-Content $CsprojPath -Raw
    if ($content -match '<Version>([^<]+)</Version>') { return $Matches[1] }
    return "1.0.0.000"
}

function Convert-ToAssemblyVersion([string]$Version) {
    $parts = $Version.Split('.')
    $major = [int]$parts[0]
    $minor = if ($parts.Count -gt 1) { [int]$parts[1] } else { 0 }
    $patch = if ($parts.Count -gt 2) { [int]$parts[2] } else { 0 }
    $build = if ($parts.Count -gt 3) { [int]$parts[3] } else { 0 }
    return "$major.$minor.$patch.$build"
}

function Step-BuildVersion([string]$Version) {
    $parts = $Version.Split('.')
    $major = [int]$parts[0]
    $minor = if ($parts.Count -gt 1) { [int]$parts[1] } else { 0 }
    $patch = if ($parts.Count -gt 2) { [int]$parts[2] } else { 0 }
    $build = if ($parts.Count -gt 3) { [int]$parts[3] } else { 0 }
    $build++
    if ($build -gt 99) {
        $patch++
        $build = 1
    }
    return "{0}.{1}.{2}.{3:00}" -f $major, $minor, $patch, $build
}

function Set-ProjectVersion([string]$CsprojPath, [string]$NewVersion) {
    $assemblyVersion = Convert-ToAssemblyVersion $NewVersion
    $content = Get-Content $CsprojPath -Raw
    $content = $content -replace '<Version>[^<]+</Version>', "<Version>$NewVersion</Version>"
    $content = $content -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$assemblyVersion</AssemblyVersion>"
    $content = $content -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$assemblyVersion</FileVersion>"
    $content = $content -replace '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$NewVersion</InformationalVersion>"
    Set-Content -Path $CsprojPath -Value $content -Encoding UTF8 -NoNewline
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

if ($Build) {
    $currentVersion = Get-ProjectVersion $ProjectFile
    $newVersion = Step-BuildVersion $currentVersion
    Write-Step "Versionando build Community ($currentVersion -> $newVersion)..."
    Set-ProjectVersion $ProjectFile $newVersion
}

# Remove arquivos temporarios _wpftmp.csproj gerados pelo C# Dev Kit do VS Code.
$wpftmp = Get-ChildItem -Path $SolutionDir -Filter "*_wpftmp.csproj" -Recurse -ErrorAction SilentlyContinue
if ($wpftmp) {
    $wpftmp | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host "Arquivos temporarios _wpftmp removidos ($($wpftmp.Count))." -ForegroundColor DarkGray
}

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
Invoke-DotnetCommandWithRetry -ActionLabel "O restore do NXTestUnit" -Command {
    dotnet restore $TestProjectFile --nologo -v q
}

Write-Step "Rodando NXTestUnit..."
Invoke-DotnetCommandWithRetry -ActionLabel "O NXTestUnit" -Command {
    dotnet run --project $TestProjectFile -c $Configuration --no-restore --nologo
}

Write-Step "Gerando executavel Community ($Configuration)..."
Invoke-DotnetCommandWithRetry -ActionLabel "A compilacao" -Command {
    dotnet build $ProjectFile -c $Configuration --nologo --no-restore
}

if (-not (Test-Path $Exe)) {
    Write-Host "Executavel nao encontrado: $Exe" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Executavel Community gerado com sucesso!" -ForegroundColor Green
Write-Host "  Exe: $Exe" -ForegroundColor DarkGray

if ($Run) {
    Write-Step "Iniciando aplicacao..."
    Start-Process $Exe
}
