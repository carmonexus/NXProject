param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [ValidateSet("build", "patch", "minor", "major")]
    [string]$Bump = "build"
)

$SolutionDir = $PSScriptRoot
$ProjectFile = Join-Path $SolutionDir "NXProject.Community\NXProject.Community.csproj"
$TestProjectFile = Join-Path $SolutionDir "NXTestUnit\NXTestUnit.csproj"
$OutputDir = Join-Path $SolutionDir "NXProject.Community\bin\$Configuration\net10.0-windows"
$DistDir = Join-Path $SolutionDir "dist\community"
$Runtime = "win-x64"
$PublishDir = Join-Path $DistDir "publish-$Runtime"
$StageDir = Join-Path $DistDir "NXProject.Community"
$ReadmePath = Join-Path $StageDir "README-INSTALACAO.txt"
$SharedDllLockPattern = "because it is being used by another process"

# ── Bump de versão ────────────────────────────────────────────────────────────
function Get-ProjectVersion([string]$CsprojPath) {
    $content = Get-Content $CsprojPath -Raw
    if ($content -match '<Version>([^<]+)</Version>') { return $Matches[1] }
    return "1.0.0"
}

function Convert-ToAssemblyVersion([string]$Version) {
    $parts = $Version.Split('.')
    $major = [int]$parts[0]
    $minor = if ($parts.Count -gt 1) { [int]$parts[1] } else { 0 }
    $patch = if ($parts.Count -gt 2) { [int]$parts[2] } else { 0 }
    $build = if ($parts.Count -gt 3) { [int]$parts[3] } else { 0 }
    return "$major.$minor.$patch.$build"
}

function Set-ProjectVersion([string]$CsprojPath, [string]$NewVersion) {
    $assemblyVersion = Convert-ToAssemblyVersion $NewVersion
    $content = Get-Content $CsprojPath -Raw
    $content = $content -replace '<Version>[^<]+</Version>',           "<Version>$NewVersion</Version>"
    $content = $content -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$assemblyVersion</AssemblyVersion>"
    $content = $content -replace '<FileVersion>[^<]+</FileVersion>',   "<FileVersion>$assemblyVersion</FileVersion>"
    $content = $content -replace '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$NewVersion</InformationalVersion>"
    Set-Content -Path $CsprojPath -Value $content -Encoding UTF8 -NoNewline
}

function Step-Version([string]$Version, [string]$BumpType) {
    $parts = $Version.Split('.')
    $major = [int]$parts[0]
    $minor = if ($parts.Count -gt 1) { [int]$parts[1] } else { 0 }
    $patch = if ($parts.Count -gt 2) { [int]$parts[2] } else { 0 }
    $build = if ($parts.Count -gt 3) { [int]$parts[3] } else { 0 }
    switch ($BumpType) {
        "major" { $major++; $minor = 0; $patch = 0; $build = 1 }
        "minor" { $minor++; $patch = 0; $build = 1 }
        "patch" { $patch++; $build = 1 }
        "build" {
            $build++
            if ($build -gt 99) {
                $patch++
                $build = 1
            }
        }
    }
    return "{0}.{1}.{2}.{3:00}" -f $major, $minor, $patch, $build
}

$CurrentVersion = Get-ProjectVersion $ProjectFile
$NewVersion = Step-Version $CurrentVersion $Bump

$ZipPath = Join-Path $DistDir "NXProject.Community-Release.zip"

function Write-Step($msg) {
    Write-Host ""
    Write-Host ">> $msg" -ForegroundColor Cyan
}

function Remove-UnusedSatelliteResourceFolders([string]$PublishDir) {
    $keepCultures = @("pt-BR")
    $cultureFolders = Get-ChildItem -Path $PublishDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^[a-z]{2}(-[A-Z][A-Za-z]+)?$' -and $_.Name -notin $keepCultures }

    if ($cultureFolders) {
        $cultureFolders | Remove-Item -Recurse -Force
        Write-Host "  Pastas de recursos removidas: $($cultureFolders.Name -join ', ')" -ForegroundColor DarkGray
    }
}

function Test-IsReleasePackageFile([string]$FileName) {
    $ext = [System.IO.Path]::GetExtension($FileName).ToLowerInvariant()
    if ($ext -in @(".json", ".ps1", ".txt", ".bat")) { return $true }

    return
        ($FileName -eq "NXProject.Community.exe") -or
        ($FileName -eq "NXProject.Community.dll") -or
        ($FileName -eq "NXProject.Shared.dll")
}

function Remove-SetupOwnedFilesFromRelease([string]$PackageDir) {
    $removed = 0
    Get-ChildItem -Path $PackageDir -Recurse -File | ForEach-Object {
        if (-not (Test-IsReleasePackageFile $_.Name)) {
            Remove-Item -LiteralPath $_.FullName -Force
            $removed++
        }
    }

    Get-ChildItem -Path $PackageDir -Recurse -Directory |
        Sort-Object FullName -Descending |
        Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue) } |
        Remove-Item -Force

    Write-Host "  Arquivos de base mantidos no Setup e removidos do release.zip: $removed" -ForegroundColor DarkGray
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

        # Erros exclusivamente do _wpftmp nao indicam falha real do projeto
        $realErrors = $output | Where-Object {
            ($_ -match '\b(error|NETSDK\d+)\b' -or $_ -match ' : error ') -and $_ -notmatch '_wpftmp\.csproj'
        }
        if (-not $realErrors) {
            Write-Host "  (erros de _wpftmp ignorados)" -ForegroundColor DarkGray
            return
        }

        $hasDllLock = $combinedOutput -match [regex]::Escape($SharedDllLockPattern)
        if (-not $hasDllLock -or $attempt -eq 2) {
            Write-Host ""
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

# Remove arquivos _wpftmp.csproj gerados pelo C# Dev Kit do VS Code antes de compilar.
# Eles podem conflitar com o _wpftmp que o proprio MSBuild cria durante a compilacao WPF.
$wpftmp = Get-ChildItem -Path $SolutionDir -Filter "*_wpftmp.csproj" -Recurse -ErrorAction SilentlyContinue
if ($wpftmp) {
    $wpftmp | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host "  Arquivos temporarios _wpftmp removidos ($($wpftmp.Count))." -ForegroundColor DarkGray
}

Write-Step "Restaurando pacotes..."
Invoke-DotnetCommandWithRetry -ActionLabel "O restore do NXTestUnit" -Command {
    dotnet restore $TestProjectFile --nologo -v q
}

Write-Step "Rodando NXTestUnit..."
Invoke-DotnetCommandWithRetry -ActionLabel "O NXTestUnit" -Command {
    dotnet run --project $TestProjectFile -c $Configuration --no-restore --nologo
}

Write-Host ""
Write-Host ">> Versao: $CurrentVersion → $NewVersion" -ForegroundColor Yellow
Set-ProjectVersion $ProjectFile $NewVersion

# Remove obj/ (nao so o assets.json) para forcar restore limpo com o RID correto —
# um build/restore anterior sem RID pode deixar o assets.json sem o alvo win-x64.
$objDir = Join-Path $SolutionDir "NXProject.Community\obj"
if (Test-Path $objDir) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Remove-Item $objDir -Recurse -Force -ErrorAction Stop
            break
        } catch {
            if ($attempt -eq 3) {
                Write-Host "  Aviso: nao foi possivel remover obj/ completamente (pasta ocupada); seguindo mesmo assim." -ForegroundColor Yellow
            } else {
                Start-Sleep -Milliseconds 500
            }
        }
    }
}
Invoke-DotnetCommandWithRetry -ActionLabel "O restore" -Command {
    dotnet restore $ProjectFile -r $Runtime -p:SelfContained=true --nologo -v q --force
}

Write-Step "Publicando NXProject Community self-contained ($Runtime)..."
if (Test-Path $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}
Invoke-DotnetCommandWithRetry -ActionLabel "A compilacao" -Command {
    dotnet publish $ProjectFile -c $Configuration -r $Runtime --self-contained true -o $PublishDir --nologo --no-restore
}

Write-Step "Preparando pasta de distribuicao..."
if (Test-Path $StageDir) {
    Remove-Item -LiteralPath $StageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

$exePath = Join-Path $PublishDir "NXProject.Community.exe"
if (-not (Test-Path $exePath)) {
    Write-Host "Executavel nao encontrado apos publish: $exePath" -ForegroundColor Red
    exit 1
}
Copy-Item -Path (Join-Path $PublishDir "*") -Destination $StageDir -Recurse -Force
Remove-UnusedSatelliteResourceFolders $StageDir
Remove-SetupOwnedFilesFromRelease $StageDir

@"
NXProject Community

Este pacote usa a base instalada pelo NXProject-Setup.zip.
Se executar sem o Setup, pode ser necessario instalar o .NET Desktop Runtime 10 x64.

Como executar:
1. Instale/atualize a base com o NXProject-Setup.zip quando necessario.
2. Extraia todo o conteudo deste pacote para a pasta do NXProject.
3. Execute o arquivo NXProject.Community.exe.

Se precisar diagnosticar abertura do aplicativo, execute:
.\NXProject-Tracelog.ps1

Contato:
- Nexus XData Tecnologia Ltda
- comercial.nexus.xdata@gmail.com
"@ | Set-Content -Path $ReadmePath -Encoding UTF8

$LicenseSrc = Join-Path $SolutionDir "LICENSE.txt"
if (Test-Path $LicenseSrc) {
    Copy-Item -Path $LicenseSrc -Destination (Join-Path $StageDir "LICENSE.txt") -Force
}

$TraceScriptSrc = Join-Path $SolutionDir "NXProject-Tracelog.ps1"
if (Test-Path $TraceScriptSrc) {
    Copy-Item -Path $TraceScriptSrc -Destination (Join-Path $StageDir "NXProject-Tracelog.ps1") -Force
}

@"
@echo off
:: Use este .bat apenas para diagnostico quando solicitado pelo suporte.
cd /d "%~dp0"
dotnet NXProject.Community.dll
"@ | Set-Content -Path (Join-Path $StageDir "NXProject-FallbackLauncher.bat") -Encoding ASCII

Write-Step "Gerando arquivo compactado..."
if (Test-Path $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}
Compress-Archive -Path (Join-Path $StageDir "*") -DestinationPath $ZipPath -Force

Write-Host ""
Write-Host "Pacote Community gerado com sucesso!" -ForegroundColor Green
Write-Host "  Pasta: $StageDir" -ForegroundColor DarkGray
Write-Host "  Zip:   $ZipPath" -ForegroundColor DarkGray

# ── GitHub Release ────────────────────────────────────────────────────────────
Write-Step "Publicando GitHub Release v$NewVersion..."

$tag = "v$NewVersion"
$releaseNotes = "NXProject Community $tag"

$ghAvailable = Get-Command gh -ErrorAction SilentlyContinue
if (-not $ghAvailable) {
    Write-Host "  gh CLI nao encontrado. Instale em https://cli.github.com e faca 'gh auth login'." -ForegroundColor Yellow
    Write-Host "  Para publicar manualmente: gh release create $tag '$ZipPath' --title '$tag' --notes '$releaseNotes'" -ForegroundColor DarkGray
} else {
    gh release create $tag $ZipPath `
        --title "NXProject Community $tag" `
        --notes $releaseNotes `
        --repo nexusxdata/NXProject

    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "Release $tag publicada com sucesso no GitHub!" -ForegroundColor Green
        Write-Host "  https://github.com/nexusxdata/NXProject/releases/tag/$tag" -ForegroundColor DarkGray
    } else {
        Write-Host "  Falha ao criar a release. Verifique se esta autenticado: gh auth login" -ForegroundColor Red
    }
}
