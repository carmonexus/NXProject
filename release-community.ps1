param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$SolutionDir = $PSScriptRoot
$ProjectFile = Join-Path $SolutionDir "NXProject.Community\NXProject.Community.csproj"
$OutputDir = Join-Path $SolutionDir "NXProject.Community\bin\$Configuration\net10.0-windows"
$DistDir = Join-Path $SolutionDir "dist\community"
$StageDir = Join-Path $DistDir "NXProject.Community"
$ZipPath = Join-Path $DistDir "NXProject.Community-$Configuration.zip"
$ReadmePath = Join-Path $StageDir "README-INSTALACAO.txt"

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

Write-Step "Compilando NXProject Community ($Configuration)..."
dotnet build $ProjectFile -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Falha na compilacao." -ForegroundColor Red
    exit 1
}

Write-Step "Preparando pasta de distribuicao..."
if (Test-Path $StageDir) {
    Remove-Item -LiteralPath $StageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

Copy-Item -Path (Join-Path $OutputDir "*") -Destination $StageDir -Recurse -Force

@"
NXProject Community

Como executar:
1. Extraia todo o conteudo deste .zip para uma pasta local.
2. Execute o arquivo NXProject.Community.exe.

Requisito:
- Microsoft .NET Desktop Runtime 10.0 para Windows

Se o aplicativo nao abrir por falta do .NET:
1. Instale o runtime ".NET Desktop Runtime 10.0 (x64)".
2. Depois execute novamente o NXProject.Community.exe.

Sugestao para distribuicao:
- Publique este .zip junto com uma pagina de download e um link para instalacao do .NET Desktop Runtime.

Contato:
- Nexus XData Tecnologia Ltda
- comercial.nexus.xdata@gamail.com
"@ | Set-Content -Path $ReadmePath -Encoding UTF8

Write-Step "Gerando arquivo ZIP..."
if (Test-Path $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}
Compress-Archive -Path (Join-Path $StageDir "*") -DestinationPath $ZipPath -Force

Write-Host ""
Write-Host "Pacote Community gerado com sucesso!" -ForegroundColor Green
Write-Host "  Pasta: $StageDir" -ForegroundColor DarkGray
Write-Host "  Zip:   $ZipPath" -ForegroundColor DarkGray
