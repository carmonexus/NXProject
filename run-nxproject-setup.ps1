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
    $path = Join-Path $PSScriptRoot "NXProject-Setup\bin\$configuration\net10.0-windows\win-x64\NXProject-Setup.exe"
    if (Test-Path $path) { return $path }
    return $null
}

if ($PSBoundParameters.ContainsKey('Configuration')) {
    $exe = Get-ExePath $Configuration
    if ($null -eq $exe) {
        Write-Host "Executavel NXProject-Setup nao encontrado para $Configuration." -ForegroundColor Red
        Write-Host "Rode antes: dotnet build NXProject-Setup\NXProject-Setup.csproj -c $Configuration" -ForegroundColor DarkGray
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
        Write-Host "Nenhuma build NXProject-Setup encontrada em Debug ou Release." -ForegroundColor Red
        Write-Host "Rode antes: dotnet build NXProject-Setup\NXProject-Setup.csproj" -ForegroundColor DarkGray
        exit 1
    }
}

Write-Host "Iniciando NXProject-Setup..." -ForegroundColor Cyan
& $exe
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0 -and $null -ne $exitCode) {
    Write-Host "App encerrou com codigo $exitCode." -ForegroundColor Yellow
}
