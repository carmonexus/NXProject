// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace NXProject.Services;

/// <summary>
/// Instala os CLIs de IA (Codex e Claude Code) para WINDOWS baixando SEMPRE o binário
/// nativo MAIS NOVO (não uma versão congelada), direto das fontes oficiais — sem rodar
/// script .ps1 (que o antivírus costuma bloquear). Instala por usuário em
/// %LOCALAPPDATA%\NXProject\bin e adiciona essa pasta ao PATH do usuário.
/// No WSL a instalação continua manual.
/// </summary>
public static class AiCliInstaller
{
    // Codex: release mais recente no GitHub (asset nativo Windows x64).
    // O nome do asset já mudou no passado; por isso escolhemos de forma tolerante
    // (o .exe cru é o ideal — nem precisa extrair).
    private const string CodexLatestApi = "https://api.github.com/repos/openai/codex/releases/latest";
    private const string CodexWinExe = "codex-x86_64-pc-windows-msvc.exe";        // baixa direto
    private const string CodexWinExeZip = "codex-x86_64-pc-windows-msvc.exe.zip"; // fallback (extrai)
    private const string CodexWinZipOld = "codex-x86_64-pc-windows-msvc.zip";     // fallback antigo

    // Claude Code: endpoint oficial da Anthropic (mesmo do instalador nativo).
    private const string ClaudeLatestUrl = "https://downloads.claude.ai/claude-code-releases/latest";
    private static string ClaudeBinaryUrl(string version) =>
        $"https://downloads.claude.ai/claude-code-releases/{version}/win32-x64/claude.exe";

    public static string BinDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXProject", "bin");

    /// <summary>Caminho do codex.exe instalado pelo NX (BinDir), ou null se não houver.</summary>
    public static string? CodexPath => FileIfExists(Path.Combine(BinDir, "codex.exe"));
    /// <summary>Caminho do claude.exe instalado pelo NX (BinDir), ou null se não houver.</summary>
    public static string? ClaudePath => FileIfExists(Path.Combine(BinDir, "claude.exe"));

    private static string? FileIfExists(string p) => File.Exists(p) ? p : null;

    private static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub exige User-Agent; aproveitamos para identificar a origem (NXProject).
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NXProject-Setup (Nexus Xdata)");
        return c;
    }

    /// <summary>Baixa o Codex mais novo (zip nativo), extrai o exe e instala como codex.exe.</summary>
    public static async Task InstallCodexAsync(IProgress<string>? status = null)
    {
        Directory.CreateDirectory(BinDir);
        using var client = NewClient();

        status?.Report("Codex: consultando release mais recente...");
        var json = await client.GetStringAsync(CodexLatestApi);
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;

        // Mapa nome->url de todos os assets, para escolher de forma tolerante.
        var byName = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var n = a.GetProperty("name").GetString();
                var u = a.GetProperty("browser_download_url").GetString();
                if (!string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(u)) byName[n!] = u!;
            }

        var destExe = Path.Combine(BinDir, "codex.exe");

        // Seleção POR PADRÃO (não por nome fixo), para sobreviver a mudanças futuras de
        // nome/extensão. O CLI x64 é sempre "codex-x86_64-pc-windows-msvc.<ext>" — os
        // sub-executáveis têm palavra extra (codex-app-server-..., codex-command-runner-...),
        // então a âncora "^codex-x86_64-pc-windows-msvc." isola só o CLI.
        var cli = byName.Keys
            .Where(n => System.Text.RegularExpressions.Regex.IsMatch(
                n, @"^codex-x86_64-pc-windows-msvc\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();
        var exeUrl = cli.Where(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        .Select(n => byName[n]).FirstOrDefault();
        var zipUrl = cli.Where(n => n.EndsWith(".exe.zip", StringComparison.OrdinalIgnoreCase)
                                 || n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        .Select(n => byName[n]).FirstOrDefault();

        // 1º) .exe cru (ideal): baixa direto, sem extrair.
        if (!string.IsNullOrWhiteSpace(exeUrl))
        {
            status?.Report($"Codex {tag}: baixando...");
            await DownloadAsync(client, exeUrl!, destExe);
            ValidateExe(destExe, "Codex");
        }
        // 2º) fallback: um zip do CLI x64 (.exe.zip ou .zip) -> extrai o exe.
        else if (!string.IsNullOrWhiteSpace(zipUrl))
        {
            status?.Report($"Codex {tag}: baixando...");
            var tmpZip = Path.Combine(Path.GetTempPath(), $"codex-{Guid.NewGuid():N}.zip");
            var tmpDir = Path.Combine(Path.GetTempPath(), $"codex-x-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmpDir);
            try
            {
                await DownloadAsync(client, zipUrl!, tmpZip);
                status?.Report("Codex: instalando...");
                ZipFile.ExtractToDirectory(tmpZip, tmpDir, overwriteFiles: true);
                var exe = Directory.GetFiles(tmpDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault()
                          ?? throw new InvalidOperationException("O zip do Codex não continha um .exe.");
                File.Copy(exe, destExe, overwrite: true);
            }
            finally { TryDelete(tmpZip); TryDeleteDir(tmpDir); }
        }
        else
        {
            throw new InvalidOperationException(
                $"Não encontrei o binário Windows x64 do Codex no release {tag} " +
                $"(procurei '{CodexWinExe}' e '{CodexWinExeZip}').");
        }

        EnsureBinOnUserPath();
        status?.Report($"Codex {tag} instalado.");
    }

    /// <summary>Baixa o Claude Code mais novo (claude.exe nativo) e instala.</summary>
    public static async Task InstallClaudeCodeAsync(IProgress<string>? status = null)
    {
        Directory.CreateDirectory(BinDir);
        using var client = NewClient();

        status?.Report("Claude Code: consultando versão mais recente...");
        var version = (await client.GetStringAsync(ClaudeLatestUrl)).Trim();
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("Não foi possível obter a versão mais recente do Claude Code.");

        status?.Report($"Claude Code {version}: baixando...");
        var dest = Path.Combine(BinDir, "claude.exe");
        await DownloadAsync(client, ClaudeBinaryUrl(version), dest);
        ValidateExe(dest, "Claude Code");

        EnsureBinOnUserPath();
        status?.Report($"Claude Code {version} instalado.");
    }

    /// <summary>Adiciona %LOCALAPPDATA%\NXProject\bin ao PATH do usuário (sem admin), se faltar.</summary>
    public static void EnsureBinOnUserPath()
    {
        var cur = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        var already = cur.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(p => string.Equals(p.Trim().TrimEnd('\\'), BinDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (already) return;

        var novo = string.IsNullOrEmpty(cur) ? BinDir : cur.TrimEnd(';') + ";" + BinDir;
        // Grava no PATH do usuário (HKCU) — .NET dispara o broadcast de mudança de ambiente.
        Environment.SetEnvironmentVariable("PATH", novo, EnvironmentVariableTarget.User);
    }

    private static async Task DownloadAsync(HttpClient client, string url, string destPath)
    {
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var file = File.Create(destPath);
        await src.CopyToAsync(file);
    }

    // Confere que o baixado é mesmo um executável Windows (assinatura "MZ" + tamanho
    // plausível). Se o endpoint/asset mudar e devolver HTML/404/vazio, falha claro em
    // vez de deixar um "codex.exe"/"claude.exe" quebrado no PATH.
    private static void ValidateExe(string path, string cli)
    {
        var fi = new FileInfo(path);
        var ok = fi.Exists && fi.Length > 100_000;
        if (ok)
        {
            using var fs = File.OpenRead(path);
            ok = fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        if (!ok)
        {
            TryDelete(path);
            throw new InvalidOperationException(
                $"O download do {cli} não é um executável válido (o formato de distribuição pode ter mudado). " +
                "Tente novamente mais tarde ou instale manualmente.");
        }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
