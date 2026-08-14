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
    private const string CodexLatestApi = "https://api.github.com/repos/openai/codex/releases/latest";
    private const string CodexWinAsset = "codex-x86_64-pc-windows-msvc.zip";

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

        string? url = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), CodexWinAsset, StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    break;
                }

        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException($"Asset '{CodexWinAsset}' não encontrado no release {tag} do Codex.");

        status?.Report($"Codex {tag}: baixando...");
        var tmpZip = Path.Combine(Path.GetTempPath(), $"codex-{Guid.NewGuid():N}.zip");
        await DownloadAsync(client, url!, tmpZip);

        status?.Report("Codex: instalando...");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"codex-x-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            ZipFile.ExtractToDirectory(tmpZip, tmpDir, overwriteFiles: true);
            var exe = Directory.GetFiles(tmpDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidOperationException("O zip do Codex não continha um .exe.");
            File.Copy(exe, Path.Combine(BinDir, "codex.exe"), overwrite: true);
        }
        finally
        {
            TryDelete(tmpZip);
            TryDeleteDir(tmpDir);
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

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
