param(
    [switch]$RecreateCertificate
)

# Nao precisa executar como Administrador.
# Cria um certificado de desenvolvimento no perfil do usuario atual
# e assina os binarios locais do NXProject.

$CertSubject = "CN=NXProject Dev Local"
$ProjectBin  = @(
    (Join-Path $PSScriptRoot "NXProject.Community\bin\Debug\net10.0-windows")
    (Join-Path $PSScriptRoot "NXProject.Community\bin\Release\net10.0-windows")
)

Write-Host "==> Verificando certificado do usuario atual..." -ForegroundColor Cyan
$certificates = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
                Where-Object { $_.Subject -eq $CertSubject }
$cert = $certificates | Select-Object -First 1

if ($RecreateCertificate) {
    Write-Host "==> Removendo certificado(s) anterior(es) e chaves quebradas..." -ForegroundColor Yellow
    $thumbprints = @($certificates | ForEach-Object Thumbprint)

    foreach ($storeName in @("My", "TrustedPublisher", "Root")) {
        try {
            $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, "CurrentUser")
            $store.Open("ReadWrite")
            $toRemove = $store.Certificates | Where-Object { $_.Thumbprint -in $thumbprints -or $_.Subject -eq $CertSubject }
            foreach ($c in $toRemove) { $store.Remove($c) }
            $store.Close()
        } catch { }
    }

    $cert = $null
}

if (-not $cert) {
    Write-Host "==> Criando certificado local sem solicitar permissao de administrador..." -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $CertSubject `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyUsage DigitalSignature `
        -FriendlyName "NXProject Dev Local" `
        -NotAfter (Get-Date).AddYears(10) `
        -ErrorAction Stop
    Write-Host "   Certificado criado: $($cert.Thumbprint)" -ForegroundColor Green
} else {
    Write-Host "   Certificado ja existe: $($cert.Thumbprint)" -ForegroundColor Green
}

# Exporta e instala como confiavel apenas para o usuario atual (so se ainda nao estiver la).
Write-Host "==> Verificando certificado nos stores confiaveis..." -ForegroundColor Cyan
try {
    $stores = @("Root", "TrustedPublisher")
    foreach ($storeName in $stores) {
        $store = $null
        try {
            $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, "CurrentUser")
            $store.Open("ReadWrite")
            $alreadyThere = $store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
            if (-not $alreadyThere) {
                Write-Host "   Adicionando ao store CurrentUser\$storeName..." -ForegroundColor Cyan
                $store.Add($cert)
            }
        } catch {
            Write-Host "   Erro ao acessar CurrentUser\${storeName}: $($_.Exception.Message)" -ForegroundColor Red
            Write-Host ""
            Write-Host "A pasta do projeto esta acessivel; a falha ocorreu no repositorio de certificados do Windows." -ForegroundColor Yellow
            Write-Host "Para o Windows confiar na assinatura, o certificado precisa estar em CurrentUser\Root e CurrentUser\TrustedPublisher." -ForegroundColor Yellow
            Write-Host "Se o Windows bloquear esse store, tente recriar o certificado ou execute uma vez em PowerShell elevado:" -ForegroundColor Yellow
            Write-Host ""
            Write-Host "  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -RecreateCertificate" -ForegroundColor Cyan
            throw
        } finally {
            if ($store) { $store.Close() }
        }
    }
    Write-Host "   Certificado confiavel." -ForegroundColor Green
} catch {
    exit 1
}

# Assina todos os .exe e .dll nas pastas de build
Write-Host "==> Assinando binarios..." -ForegroundColor Cyan
$signed = 0
$failed = 0
foreach ($dir in $ProjectBin) {
    if (-not (Test-Path $dir)) { continue }
    Get-ChildItem $dir -Include "*.exe","*.dll" -Recurse | ForEach-Object {
        try {
            $result = Set-AuthenticodeSignature -FilePath $_.FullName -Certificate $cert -ErrorAction Stop
            if ($result.Status -eq "Valid") {
                $signed++
            }
            else {
                $failed++
                Write-Host "   Falha: $($_.Name) - $($result.Status): $($result.StatusMessage)" -ForegroundColor Red
            }
        }
        catch {
            $failed++
            Write-Host "   Falha: $($_.Name) - $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}
Write-Host "   $signed arquivo(s) assinado(s), $failed falha(s)." -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })

if ($failed -gt 0) {
    Write-Host ""
    Write-Host "A assinatura nao foi concluida." -ForegroundColor Red
    if (-not $RecreateCertificate) {
        Write-Host "Tente novamente recriando o certificado:" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -RecreateCertificate" -ForegroundColor Cyan
    }
    exit 1
}

Write-Host ""
Write-Host "Pronto! Execute o run-community.ps1 normalmente." -ForegroundColor Cyan
Write-Host "O certificado fica no perfil do usuario atual e nao exige senha de administrador." -ForegroundColor DarkGray
