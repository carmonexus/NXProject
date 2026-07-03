param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration
)

# Remove arquivos temporários _wpftmp.csproj gerados pelo C# Dev Kit do VS Code.
$wpftmp = Get-ChildItem -Path $PSScriptRoot -Filter "*_wpftmp.csproj" -Recurse -ErrorAction SilentlyContinue
if ($wpftmp) {
    $wpftmp | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host "Arquivos temporarios _wpftmp removidos ($($wpftmp.Count))." -ForegroundColor DarkGray
}

function Get-ExePath($configuration) {
    $path = Join-Path $PSScriptRoot "NXProject.Community\bin\$configuration\net10.0-windows\NXProject.Community.exe"
    if (Test-Path $path) { return $path }
    return $null
}

function Test-NXProjectCertificateInCurrentUserRoot {
    $certSubject = "CN=NXProject Dev Local"
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $certSubject } |
            Select-Object -First 1

    if (-not $cert) { return $false }

    $store = $null
    try {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "CurrentUser")
        $store.Open("ReadOnly")
        return [bool]($store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })
    } catch {
        Write-Host "Nao foi possivel verificar CurrentUser\Root: $($_.Exception.Message)" -ForegroundColor Yellow
        return $false
    } finally {
        if ($store) { $store.Close() }
    }
}

function Ensure-NXProjectCertificateTrusted {
    if (Test-NXProjectCertificateInCurrentUserRoot) { return }

    $signScript = Join-Path $PSScriptRoot "sign-nxproject.ps1"
    if (-not (Test-Path $signScript)) {
        Write-Host "Script de assinatura nao encontrado: $signScript" -ForegroundColor Yellow
        return
    }

    Write-Host "Certificado NXProject nao esta confiavel em CurrentUser\Root. Tentando instalar e assinar..." -ForegroundColor Yellow
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Aviso: assinatura/configuracao do certificado falhou. O aplicativo ainda sera iniciado." -ForegroundColor Yellow
    }
}

if ($PSBoundParameters.ContainsKey('Configuration')) {
    $exe = Get-ExePath $Configuration
    if ($null -eq $exe) {
        Write-Host "Executavel Community nao encontrado para $Configuration." -ForegroundColor Red
        exit 1
    }
}
else {
    $exe = @(
        Get-ExePath "Debug"
        Get-ExePath "Release"
    ) | Where-Object { $null -ne $_ } |
        Sort-Object { (Get-Item $_).LastWriteTime } -Descending |
        Select-Object -First 1

    if ($null -eq $exe) {
        Write-Host "Nenhuma build Community encontrada em Debug ou Release." -ForegroundColor Red
        exit 1
    }
}

Ensure-NXProjectCertificateTrusted

Write-Host "Iniciando NXProject..." -ForegroundColor Cyan
& $exe
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0 -and $null -ne $exitCode) {
    Write-Host "App encerrou com codigo $exitCode." -ForegroundColor Yellow
}
