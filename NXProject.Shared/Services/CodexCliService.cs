using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NXProject.Models;

namespace NXProject.Services
{
    /// <summary>
    /// Codex instalado na máquina, usado em modo NÃO interativo (`codex exec`): o NX manda o
    /// prompt na entrada padrão e lê a resposta na saída. Não há chave de API nem servidor —
    /// a autenticação é a do próprio Codex CLI. Como o CLI normalmente vive no WSL, a chamada
    /// padrão é `wsl.exe -- codex exec`; o comando é configurável no campo Endpoint do provedor
    /// (ex.: "C:\\Program Files\\...\\codex.exe" para instalação nativa no Windows).
    /// </summary>
    /// <summary>Falha ao INICIAR o processo (executável/comando não encontrado) — distinta de
    /// um erro devolvido pelo próprio CLI. Deixa o teste tentar a forma alternativa.</summary>
    public sealed class CliStartException : Exception
    {
        public CliStartException(string message, Exception inner) : base(message, inner) { }
    }

    public static class CodexCliService
    {
        /// <summary>Comando padrão: Codex dentro do WSL (o `.sh` do app-server usa o mesmo PATH).</summary>
        public const string DefaultCommand = "wsl.exe -- bash -lic \"codex exec --skip-git-repo-check -\"";

        /// <summary>Comando padrão do Claude Code local: modo NÃO interativo (-p) lendo o prompt
        /// do stdin. O CLI roda nativo no Windows; no WSL, prefixe com "wsl.exe -- ".</summary>
        public const string DefaultClaudeCommand = "claude -p";

        /// <summary>Comando padrão do provedor CLI local: Codex começa no WSL; Claude Code, nativo no Windows.</summary>
        public static string GetDefaultCommand(AIProvider provider)
            => BuildCommand(provider, windows: provider == AIProvider.ClaudeCli);

        /// <summary>Nome do executável do CLI (procurado no PATH).</summary>
        public static string CliName(AIProvider provider)
            => provider == AIProvider.ClaudeCli ? "claude" : "codex";

        private static string CliTail(AIProvider provider)
            => provider == AIProvider.ClaudeCli ? "-p" : "exec --skip-git-repo-check -";

        /// <summary>Monta o comando conforme o local: no Windows usa "cmd /c" (resolve o .cmd do
        /// npm pelo PATH e repassa o stdin corretamente); no WSL usa "wsl.exe -- ".</summary>
        public static string BuildCommand(AIProvider provider, bool windows)
        {
            var name = CliName(provider);
            var tail = CliTail(provider);
            // No WSL usa "bash -lic" (login + interativo): carrega ~/.profile e ~/.bashrc, onde o
            // nvm/npm coloca o PATH do CLI. Sem isso, um "wsl.exe -- codex" nao acha o codex do
            // Linux (instalado via nvm) e acaba pegando o do Windows via /mnt/c -> "node: not found".
            return windows ? $"cmd /c {name} {tail}" : $"wsl.exe -- bash -lic \"{name} {tail}\"";
        }

        /// <summary>Se o comando for o PADRÃO ANTIGO do WSL (sem "bash -lic", que não carregava o
        /// PATH do nvm), atualiza para o novo padrão com shell de login. Caso contrário, mantém
        /// (respeita comando customizado do usuário).</summary>
        public static string UpgradeWslDefault(AIProvider provider, string? command)
        {
            var cmd = (command ?? string.Empty).Trim();
            var oldDefault = $"wsl.exe -- {CliName(provider)} {CliTail(provider)}".Trim();
            return string.Equals(cmd, oldDefault, StringComparison.OrdinalIgnoreCase)
                ? BuildCommand(provider, windows: false) : cmd;
        }

        /// <summary>O comando roda no Windows (nativo) e não no WSL?</summary>
        public static bool IsWindowsCommand(string? command)
            => !string.IsNullOrWhiteSpace(command)
               && !command!.TrimStart().StartsWith("wsl", StringComparison.OrdinalIgnoreCase);

        /// <summary>Caminho do CLI no PATH do Windows (1ª ocorrência de `where`), ou null se não achar.</summary>
        public static string? FindOnWindowsPath(string cli)
        {
            try
            {
                var psi = new ProcessStartInfo("where.exe", cli)
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                var line = p?.StandardOutput.ReadLine();
                p?.WaitForExit(3000);
                return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
            }
            catch { return null; }
        }

        /// <summary>
        /// Executa o Codex local com (prompt de sistema + pergunta) e devolve o texto da resposta.
        /// </summary>
        public static async Task<string> GenerateAsync(
            string systemPrompt, string userPrompt, string? command, int timeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            var cmd = string.IsNullOrWhiteSpace(command) ? DefaultCommand : command!.Trim();
            var (exe, args) = SplitCommand(cmd);

            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                // ENTRADA tambem em UTF-8 (sem BOM): o Codex le o prompt do stdin e recusa
                // qualquer coisa que nao seja UTF-8 valido. Sem isso, o .NET escreve na code
                // page do console (CP-1252 no Windows PT-BR) e os acentos do cronograma
                // quebram com "input is not valid UTF-8".
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };

            using var process = new Process { StartInfo = psi };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException($"Não foi possível iniciar o Codex local: {cmd}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Codex local não encontrado ({cmd}). Verifique se o Codex CLI está instalado e no PATH "
                    + "(no WSL, se o comando usar wsl.exe) — ou ajuste o comando na tela do Assistente de IA.\n\n"
                    + ex.Message, ex);
            }

            // Prompt vai pela ENTRADA PADRÃO: evita limite de tamanho de linha de comando e
            // problemas de escape com aspas/acentos no texto do cronograma.
            var prompt = string.IsNullOrWhiteSpace(systemPrompt)
                ? userPrompt
                : systemPrompt.Trim() + "\n\n" + userPrompt;
            await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds <= 0 ? 600 : timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new InvalidOperationException(
                    $"O Codex local não respondeu em {timeoutSeconds}s. Aumente o timeout do provedor ou simplifique o pedido.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Codex local terminou com erro (código {process.ExitCode}).\n{stderr}\n{stdout}".Trim());

            var answer = stdout.Trim();
            if (answer.Length == 0)
                throw new InvalidOperationException(
                    "O Codex local não devolveu resposta." + (stderr.Length > 0 ? "\n" + stderr.Trim() : ""));
            return answer;
        }

        /// <summary>Forma alternativa do comando para o teste: com "wsl.exe -- " se roda nativo,
        /// ou sem esse prefixo se roda no WSL. Devolve null quando não há troca óbvia a tentar.</summary>
        public static string? AlternateCommand(string? command)
        {
            var cmd = (command ?? string.Empty).Trim();
            if (cmd.Length == 0) return null;
            const string wslPrefix = "wsl.exe -- bash -lic \"";
            if (cmd.StartsWith("wsl.exe", StringComparison.OrdinalIgnoreCase))
            {
                // WSL -> nativo no Windows: extrai o comando do CLI (tira "wsl.exe --" e o
                // wrapper "bash -lic \"...\"") e roda nativo: "cmd /c codex exec -".
                var idx = cmd.IndexOf("--", StringComparison.Ordinal);
                var inner = StripBashLogin(idx >= 0 ? cmd[(idx + 2)..].Trim() : cmd);
                return inner.Length > 0 ? "cmd /c " + inner : null;
            }
            if (cmd.StartsWith("cmd /c", StringComparison.OrdinalIgnoreCase))
            {
                // Nativo -> WSL: embrulha no shell de login para carregar o PATH do nvm/npm.
                var inner = cmd[6..].Trim();
                return inner.Length > 0 ? wslPrefix + inner + "\"" : null;
            }
            return wslPrefix + StripBashLogin(cmd) + "\""; // fallback

            // Extrai X de: bash -lic "X" / bash -lc "X" / bash -ilc "X" (ou devolve o proprio texto).
            static string StripBashLogin(string s)
            {
                s = s.Trim();
                if (!s.StartsWith("bash ", StringComparison.OrdinalIgnoreCase)) return s;
                var q1 = s.IndexOf('"');
                var q2 = s.LastIndexOf('"');
                return (q1 >= 0 && q2 > q1) ? s[(q1 + 1)..q2].Trim() : s;
            }
        }

        /// <summary>Separa "exe args..." respeitando o executável entre aspas (caminho com espaço).</summary>
        private static (string Exe, string Args) SplitCommand(string command)
        {
            if (command.StartsWith('"'))
            {
                var end = command.IndexOf('"', 1);
                if (end > 0)
                    return (command[1..end], command[(end + 1)..].Trim());
            }
            var space = command.IndexOf(' ');
            return space < 0 ? (command, string.Empty) : (command[..space], command[(space + 1)..].Trim());
        }

        private static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* processo já morreu */ }
        }

        /// <summary>Comando aponta para um script .sh (ex.: start-codex-app-server.sh)? Ele SOBE
        /// um servidor e fica rodando — não serve para uma pergunta pontual.</summary>
        public static bool LooksLikeServerScript(string? command)
            => !string.IsNullOrWhiteSpace(command)
               && command!.Contains(".sh", StringComparison.OrdinalIgnoreCase)
               && !command.Contains("codex exec", StringComparison.OrdinalIgnoreCase);
    }
}
