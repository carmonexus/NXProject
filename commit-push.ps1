<#
.SYNOPSIS
    Faz commit e push juntos.

.DESCRIPTION
    Prepara as alteracoes, cria o commit e envia para o remoto (branch atual).
    Por padrao inclui apenas arquivos JA rastreados (git add -u) - nao adiciona
    untracked nem a pasta dist/. Use -Files para escolher exatamente o que entra.

    A mensagem pode vir de tres formas (nesta ordem de prioridade):
      1) -Message "texto"       (inline)
      2) -MessageFile caminho   (arquivo com a mensagem)
      3) COMMIT_MSG.txt         (arquivo padrao na raiz, se existir)

    O arquivo de mensagem segue o padrao do git: 1a linha = assunto (<= 72
    caracteres), linha em branco, depois o corpo (quebrado em ~72). O script
    avisa se o assunto passar de 72. O COMMIT_MSG.txt fica fora do commit
    (untracked; add -u nao o inclui) e e apagado apos o commit bem-sucedido.

.EXAMPLE
    ./commit-push.ps1 -Message "Curva S: ajuste do gap"

.EXAMPLE
    # grava a mensagem detalhada em COMMIT_MSG.txt e roda sem -Message
    ./commit-push.ps1

.EXAMPLE
    ./commit-push.ps1 -MessageFile msg.txt -Files NXProject.Community/Views/DelayedTasksWindow.xaml.cs
#>
param(
    [Parameter(Position = 0)]
    [string]$Message,

    [Parameter(Position = 1)]
    [string[]]$Files,

    [string]$MessageFile
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

$DefaultMsgFile = Join-Path $PSScriptRoot "COMMIT_MSG.txt"

# Resolve a mensagem: -Message > -MessageFile > COMMIT_MSG.txt
$usedMsgFile = $null
if (-not [string]::IsNullOrWhiteSpace($Message)) {
    # usa a mensagem inline
} elseif ($MessageFile) {
    if (-not (Test-Path $MessageFile)) { Write-Host "Arquivo de mensagem nao encontrado: $MessageFile" -ForegroundColor Red; exit 1 }
    $Message = (Get-Content -Path $MessageFile -Raw)
    $usedMsgFile = (Resolve-Path $MessageFile).Path
} elseif (Test-Path $DefaultMsgFile) {
    $Message = (Get-Content -Path $DefaultMsgFile -Raw)
    $usedMsgFile = $DefaultMsgFile
} else {
    Write-Host "Sem mensagem. Use -Message, -MessageFile ou crie COMMIT_MSG.txt na raiz." -ForegroundColor Red
    exit 1
}

$Message = $Message.TrimEnd()
if ([string]::IsNullOrWhiteSpace($Message)) { Write-Host "Mensagem vazia." -ForegroundColor Red; exit 1 }

# Aviso de limite do git (assunto <= 72 caracteres)
$subject = ($Message -split "`r?`n")[0]
if ($subject.Length -gt 72) {
    Write-Host "  Aviso: o assunto tem $($subject.Length) caracteres (git recomenda <= 72)." -ForegroundColor Yellow
}

# Garante que estamos num repositorio git
git rev-parse --is-inside-work-tree *> $null
if ($LASTEXITCODE -ne 0) { Write-Host "Nao e um repositorio git." -ForegroundColor Red; exit 1 }

$branch = (git rev-parse --abbrev-ref HEAD).Trim()
Write-Host ">> Branch: $branch" -ForegroundColor Cyan

# Prepara as alteracoes
if ($Files -and $Files.Count -gt 0) {
    Write-Host ">> git add (arquivos informados)..." -ForegroundColor Yellow
    git add -- $Files
} else {
    Write-Host ">> git add -u (apenas arquivos rastreados)..." -ForegroundColor Yellow
    git add -u
}
if ($LASTEXITCODE -ne 0) { Write-Host "Falha ao preparar as alteracoes." -ForegroundColor Red; exit 1 }

# Nada para commitar?
git diff --cached --quiet
if ($LASTEXITCODE -eq 0) {
    Write-Host "Nada preparado para commit. Nada a fazer." -ForegroundColor Yellow
    exit 0
}

Write-Host ">> Arquivos no commit:" -ForegroundColor Cyan
git diff --cached --name-only | ForEach-Object { Write-Host "   $_" }

# Commit
$tmp = [System.IO.Path]::GetTempFileName()
Set-Content -Path $tmp -Value $Message -Encoding UTF8
try {
    git commit -F $tmp
    if ($LASTEXITCODE -ne 0) { Write-Host "Falha no commit." -ForegroundColor Red; exit 1 }
} finally {
    Remove-Item -Path $tmp -ErrorAction SilentlyContinue
}

# Consome o arquivo de mensagem apos o commit bem-sucedido (evita reuso acidental).
if ($usedMsgFile -and (Test-Path $usedMsgFile)) {
    Remove-Item -Path $usedMsgFile -ErrorAction SilentlyContinue
    Write-Host ">> Mensagem consumida: $(Split-Path $usedMsgFile -Leaf) removido." -ForegroundColor DarkGray
}

# Push
Write-Host ">> git push origin $branch..." -ForegroundColor Yellow
git push origin $branch
if ($LASTEXITCODE -ne 0) { Write-Host "Falha no push." -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "Commit e push concluidos com sucesso!" -ForegroundColor Green
git log --oneline -1
