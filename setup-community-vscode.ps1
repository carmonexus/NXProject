param(
    [switch]$InstallVsCodeExtensions = $true
)

$SolutionDir = $PSScriptRoot
$ProjectFile = Join-Path $SolutionDir "NXProject.Community\NXProject.Community.csproj"
$DotnetDownloadUrl = "https://dotnet.microsoft.com/en-us/download/dotnet/10.0"
$RecommendedExtensions = @(
    "ms-dotnettools.csdevkit",
    "ms-dotnettools.csharp"
)

function Write-Step($msg) {
    Write-Host ""
    Write-Host ">> $msg" -ForegroundColor Cyan
}

function Fail-Step($msg) {
    Write-Host ""
    Write-Host $msg -ForegroundColor Red
    exit 1
}

Write-Step "Verificando .NET SDK..."
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    Fail-Step "O comando 'dotnet' nao foi encontrado. Instale o .NET 10 SDK em $DotnetDownloadUrl"
}

$sdkList = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0) {
    Fail-Step "Nao foi possivel listar os SDKs instalados."
}

$hasNet10Sdk = $sdkList | Where-Object { $_ -match '^10\.' }
if ($null -eq $hasNet10Sdk) {
    Fail-Step ".NET 10 SDK nao encontrado. Instale-o em $DotnetDownloadUrl"
}

if ($InstallVsCodeExtensions) {
    Write-Step "Verificando extensoes do VS Code..."
    $codeCommand = Get-Command code -ErrorAction SilentlyContinue
    if ($null -eq $codeCommand) {
        Write-Host "Comando 'code' nao encontrado. Pulei a instalacao das extensoes do VS Code." -ForegroundColor Yellow
    }
    else {
        foreach ($extension in $RecommendedExtensions) {
            Write-Host "Instalando extensao $extension..." -ForegroundColor DarkGray
            & code --install-extension $extension --force | Out-Null
        }
    }
}

Write-Step "Restaurando pacotes do projeto..."
& dotnet restore $ProjectFile --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Fail-Step "Falha no restore do projeto Community."
}

Write-Host ""
Write-Host "Ambiente pronto para compilar no VS Code!" -ForegroundColor Green
Write-Host "Proximo passo: .\build-community.ps1 -Configuration Release" -ForegroundColor DarkGray
