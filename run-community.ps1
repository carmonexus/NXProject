param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration
)

function Get-ExePath($configuration) {
    $path = Join-Path $PSScriptRoot "NXProject.Community\bin\$configuration\net10.0-windows\NXProject.Community.exe"
    if (Test-Path $path) { return $path }
    return $null
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

Start-Process $exe
