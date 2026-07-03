using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NXProject.Services;

public static class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/nexusxdata/NXProject/releases/latest";
    private const string AssetName = "NXProject.Community-Release.zip";
    public const string DotNetDesktopRuntimeDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";

    /// <summary>Verifica se o .NET Desktop Runtime (x64) esta instalado na maquina,
    /// checando a pasta compartilhada padrao de instalacao do .NET.
    /// Usado antes de aplicar atualizacoes framework-dependent (sem runtime embutido).</summary>
    public static bool IsDesktopRuntimeInstalled(int minMajorVersion = 10)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var sharedDir = Path.Combine(programFiles, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(sharedDir)) return false;

        return Directory.GetDirectories(sharedDir)
            .Select(d => Path.GetFileName(d).Split('.')[0])
            .Any(major => int.TryParse(major, out var v) && v >= minMajorVersion);
    }

    public record ReleaseInfo(string TagName, string DownloadUrl, string HtmlUrl);

    public static async Task<ReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var release = await GetLatestReleaseInfoAsync(ct);
        if (release is null) return null;

        var latest = ParseVersion(release.TagName);
        var current = GetCurrentVersion();
        if (latest <= current) return null;

        return release;
    }

    /// <summary>Retorna a release mais recente do GitHub, sem comparar com a versão atual do executável.
    /// Usado pelo instalador (NXProject.Setup), que sempre busca a última versão disponível.</summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseInfoAsync(CancellationToken ct = default)
    {
        using var client = CreateClient();
        var release = await client.GetFromJsonAsync<GithubRelease>(ApiUrl, ct);
        if (release is null) return null;

        var asset = release.Assets?.Find(a => a.Name == AssetName);
        if (asset is null) return null;

        return new ReleaseInfo(release.TagName, asset.BrowserDownloadUrl, release.HtmlUrl);
    }

    public static async Task<string> DownloadAndExtractAsync(
        string downloadUrl,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        using var client = CreateClient();

        var tempDir = Path.Combine(Path.GetTempPath(), $"nxupdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var zipPath = Path.Combine(tempDir, "update.zip");

        using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0L;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(zipPath);

            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                if (total > 0)
                    progress?.Report((int)(downloaded * 100 / total));
            }
        }

        progress?.Report(100);

        var extractDir = Path.Combine(tempDir, "extracted");
        ZipFile.ExtractToDirectory(zipPath, extractDir);
        File.Delete(zipPath);

        return extractDir;
    }

    public static void LaunchUpdaterAndExit(string extractedDir)
    {
        var exePath = Process.GetCurrentProcess().MainModule!.FileName!;
        var appDir = Path.GetDirectoryName(exePath)!;
        var exeName = Path.GetFileName(exePath);
        var dateSuffix = DateTime.Now.ToString("yyyy_MM_dd");
        var oldName = $"old_{dateSuffix}_{exeName}";
        var scriptPath = Path.Combine(appDir, "_nxupdate.ps1");

        var script = $$"""
            $pid_target = {{Environment.ProcessId}}
            $app_dir = '{{Escape(appDir)}}'
            $exe_name = '{{exeName}}'
            $old_name = '{{oldName}}'
            $src_dir  = '{{Escape(extractedDir)}}'
            $script_path = '{{Escape(scriptPath)}}'

            $waited = 0
            while ((Get-Process -Id $pid_target -ErrorAction SilentlyContinue) -and $waited -lt 30000) {
                Start-Sleep -Milliseconds 300
                $waited += 300
            }

            # Verifica se o novo exe existe no pacote extraido antes de substituir qualquer coisa
            $new_exe_src = Join-Path $src_dir $exe_name
            if (-not (Test-Path $new_exe_src)) {
                Add-Type -AssemblyName PresentationFramework
                [System.Windows.MessageBox]::Show(
                    "Atualizacao cancelada: o arquivo '$exe_name' nao foi encontrado no pacote baixado. O executavel atual nao foi alterado.",
                    "Erro na Atualizacao",
                    [System.Windows.MessageBoxButton]::OK,
                    [System.Windows.MessageBoxImage]::Error)
                Remove-Item -Path $src_dir -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -Path (Split-Path $src_dir) -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -Path $script_path -Force -ErrorAction SilentlyContinue
                exit 1
            }

            $exe_path = Join-Path $app_dir $exe_name
            if (Test-Path $exe_path) {
                Get-ChildItem -Path $app_dir -Filter "old_*$exe_name" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
                Rename-Item -Path $exe_path -NewName $old_name -Force
            }

            Get-ChildItem -Path $src_dir -Recurse | ForEach-Object {
                $rel  = $_.FullName.Substring($src_dir.Length).TrimStart([char]'\', [char]'/')
                $dest = Join-Path $app_dir $rel
                if ($_.PSIsContainer) {
                    New-Item -ItemType Directory -Path $dest -Force | Out-Null
                } else {
                    Copy-Item -Path $_.FullName -Destination $dest -Force
                }
            }

            Remove-Item -Path $src_dir -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Split-Path $src_dir) -Recurse -Force -ErrorAction SilentlyContinue

            Start-Process (Join-Path $app_dir $exe_name)

            Start-Sleep -Seconds 1
            Remove-Item -Path $script_path -Force -ErrorAction SilentlyContinue
            """;

        File.WriteAllText(scriptPath, script, System.Text.Encoding.UTF8);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        System.Windows.Application.Current.Shutdown();
    }

    private static string Escape(string path) => path.Replace("'", "''");

    private static Version GetCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v ?? new Version(0, 0, 0);
    }

    private static Version ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = System.Net.WebRequest.GetSystemWebProxy(),
            UseDefaultCredentials = true,
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NXProject-Updater/1.0");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GithubAsset>? Assets { get; set; }
    }

    private sealed class GithubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
