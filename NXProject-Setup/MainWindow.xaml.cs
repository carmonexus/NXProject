using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace NXProject.Setup;

public partial class MainWindow : Window
{
    private const string DotNetRuntimeInstallerUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";

    private readonly string _installDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Apps", "NXProject");

    public MainWindow()
    {
        InitializeComponent();
        // Mostra os caminhos de instalação (app e CLIs) com atalho para conferir.
        AppPathRun.Text = App.Str("Setup_PathApp", _installDir) + "  ";
        CliPathRun.Text = App.Str("Setup_PathCli", NXProject.Services.AiCliInstaller.BinDir) + "  ";
        Loaded += (_, _) => { RefreshStep1State(); RefreshStep2State(); RefreshInstallStatus(); };
    }

    // O NXProject não precisa ser reinstalado sempre: se o exe já está na pasta do usuário,
    // mostra "já instalado" e o botão vira "Verificar atualização" (baixa só se houver versão nova).
    private void RefreshStep2State()
    {
        var exePath = Path.Combine(_installDir, "NXProject.Community.exe");
        if (File.Exists(exePath))
        {
            Step2StatusText.Text = App.Str("Setup_Step2Installed");
            Step2Button.Content = App.Str("Setup_Step2ButtonUpdate");
            OpenAppButton.Visibility = Visibility.Visible;   // já instalado: permite abrir
        }
        else
        {
            Step2StatusText.Text = App.Str("Setup_Step2Status");
            Step2Button.Content = App.Str("Setup_Step2Button");
            OpenAppButton.Visibility = Visibility.Collapsed;
        }
    }

    // Abre o NXProject sem fechar o Setup (para o usuário ainda poder usar o Passo 3).
    private void OnOpenAppClick(object sender, RoutedEventArgs e)
    {
        var exePath = Path.Combine(_installDir, "NXProject.Community.exe");
        if (!File.Exists(exePath)) return;
        // Se o usuário marcou LLaMA no Passo 3, o app já dispara o download da IA Local ao abrir.
        var started = TryStart(exePath, LlamaCheck.IsChecked == true ? "--install-llama" : null);
        if (started)
            Close();   // abriu o app: pode fechar o Setup
        else
            ShowError(App.Str("Setup_InstalledCantOpen",
                NXProject.Services.UpdateService.DotNetDesktopRuntimeDownloadUrl, _installDir));
    }

    // Mostra um ✓ ao lado do que já está instalado. Codex/Claude: procura no WINDOWS (exe) e
    // também no WSL — para não induzir o usuário a instalar no Windows o que já roda no WSL.
    // LLaMA: modelo GGUF na pasta de recursos.
    private enum InstallWhere { None, Windows, Wsl }

    private void RefreshInstallStatus()
    {
        var codexWin = NXProject.Services.AiCliInstaller.CodexPath != null;
        var claudeWin = NXProject.Services.AiCliInstaller.ClaudePath != null;
        SetCliStatus(CodexStatus, codexWin ? InstallWhere.Windows : InstallWhere.None);
        SetCliStatus(ClaudeStatus, claudeWin ? InstallWhere.Windows : InstallWhere.None);
        SetStatus(LlamaStatus, IsLlamaInstalled());

        // Se não achou no Windows, testa o WSL (assíncrono; mostra "verificando" na hora).
        if (!codexWin) { SetChecking(CodexStatus); _ = UpdateWslStatusAsync("codex", CodexStatus); }
        if (!claudeWin) { SetChecking(ClaudeStatus); _ = UpdateWslStatusAsync("claude", ClaudeStatus); }
    }

    private static void SetChecking(System.Windows.Controls.TextBlock tb)
    {
        tb.Text = App.Str("Setup_Checking");
        tb.Foreground = System.Windows.Media.Brushes.Gray;
    }

    private static void SetCliStatus(System.Windows.Controls.TextBlock tb, InstallWhere where)
    {
        tb.Text = where switch
        {
            InstallWhere.Windows => App.Str("Setup_InstalledWin"),
            InstallWhere.Wsl => App.Str("Setup_InstalledWsl"),
            _ => App.Str("Setup_NotInstalled"),
        };
        tb.Foreground = where == InstallWhere.None
            ? System.Windows.Media.Brushes.Gray : System.Windows.Media.Brushes.Green;
    }

    private static void SetStatus(System.Windows.Controls.TextBlock tb, bool ok)
    {
        tb.Text = ok ? App.Str("Setup_Installed") : App.Str("Setup_NotInstalled");
        tb.Foreground = ok ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Gray;
    }

    private async Task UpdateWslStatusAsync(string cli, System.Windows.Controls.TextBlock tb)
    {
        var found = await Task.Run(() => IsCliInWsl(cli));
        SetCliStatus(tb, found ? InstallWhere.Wsl : InstallWhere.None);
    }

    // Procura o CLI dentro do WSL (shell de login carrega o PATH do nvm/npm).
    private static bool IsCliInWsl(string cli)
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe",
                $"-- bash -lic \"command -v {cli} >/dev/null 2>&1 && echo FOUND\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return outp.Contains("FOUND", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // IA Local instalada? Checagem leve (sem LLamaSharp): lê a pasta de recursos no local-ai.json
    // e verifica se o modelo GGUF está lá.
    private const string LlamaModelFileName = "Qwen2.5-3B-Instruct-Q4_K_M.gguf";
    private static bool IsLlamaInstalled()
    {
        try
        {
            var cfg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NXProject.Community", "local-ai.json");
            if (!File.Exists(cfg)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
            if (!doc.RootElement.TryGetProperty("ResourcesFolder", out var f)) return false;
            var folder = f.GetString();
            if (string.IsNullOrWhiteSpace(folder)) return false;
            return File.Exists(Path.Combine(folder, LlamaModelFileName))
                   || Directory.Exists(folder) && Directory.GetFiles(folder, "*.gguf").Length > 0;
        }
        catch { return false; }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* abrir pasta é conveniência */ }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnOpenAppFolder(object sender, System.Windows.RoutedEventArgs e) => OpenFolder(_installDir);
    private void OnOpenCliFolder(object sender, System.Windows.RoutedEventArgs e)
        => OpenFolder(NXProject.Services.AiCliInstaller.BinDir);

    private void OnHyperlinkRequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void RefreshStep1State()
    {
        if (NXProject.Services.UpdateService.IsDesktopRuntimeInstalled())
        {
            Step1StatusText.Text = App.Str("Setup_Step1Installed");
            Step1Button.IsEnabled = false;
        }
        else
        {
            Step1StatusText.Text = App.Str("Setup_Step1NotFound");
            Step1Button.IsEnabled = true;
        }
    }

    private async void OnStep1Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Step1Button.IsEnabled = false;
            Step1StatusText.Text = App.Str("Setup_Step1Downloading");

            var tempExe = Path.Combine(Path.GetTempPath(), $"windowsdesktop-runtime-{Guid.NewGuid():N}.exe");
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = await client.GetAsync(DotNetRuntimeInstallerUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file = File.Create(tempExe);
                await stream.CopyToAsync(file);
            }

            Step1StatusText.Text = App.Str("Setup_Step1Installing");
            using var proc = Process.Start(new ProcessStartInfo(tempExe, "/install /quiet /norestart")
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            if (proc != null)
                await proc.WaitForExitAsync();

            try { File.Delete(tempExe); } catch { /* limpeza best-effort */ }

            RefreshStep1State();
            if (!Step1Button.IsEnabled)
                Step1StatusText.Text = App.Str("Setup_Step1Success");
        }
        catch (Exception ex)
        {
            Step1StatusText.Text = App.Str("Setup_Step1Fail",
                ex.Message, NXProject.Services.UpdateService.DotNetDesktopRuntimeDownloadUrl);
            Step1Button.IsEnabled = true;
        }
    }

    private async void OnStep2Click(object sender, RoutedEventArgs e)
    {
        Step2Button.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        InstallProgress.Value = 0;
        await RunInstallAsync();
    }

    // Passo 3: instala Codex/Claude Code (Windows) baixando o binário nativo MAIS NOVO.
    private async void OnStep3Click(object sender, RoutedEventArgs e)
    {
        if (CodexCheck.IsChecked != true && ClaudeCheck.IsChecked != true && LlamaCheck.IsChecked != true)
        {
            Step3StatusText.Text = App.Str("Setup_Step3PickOne");
            return;
        }

        Step3Button.IsEnabled = false;
        var status = new Progress<string>(s => Step3StatusText.Text = s);
        try
        {
            if (CodexCheck.IsChecked == true)
                await NXProject.Services.AiCliInstaller.InstallCodexAsync(status);
            if (ClaudeCheck.IsChecked == true)
                await NXProject.Services.AiCliInstaller.InstallClaudeCodeAsync(status);

            // LLaMA (~2 GB) não é baixado aqui: o download roda dentro do NXProject (na tela
            // Gerenciar IA Local, que define a pasta). Aciona ao abrir o app (Passo 2).
            var msg = (CodexCheck.IsChecked == true || ClaudeCheck.IsChecked == true)
                ? App.Str("Setup_Step3Done", NXProject.Services.AiCliInstaller.BinDir)
                : string.Empty;
            if (LlamaCheck.IsChecked == true)
                msg = (msg.Length > 0 ? msg + "\n" : "") + App.Str("Setup_Step3LlamaQueued");
            Step3StatusText.Text = msg;
        }
        catch (Exception ex)
        {
            Step3StatusText.Text = App.Str("Setup_Step3Fail", ex.Message);
        }
        finally
        {
            Step3Button.IsEnabled = true;
            RefreshInstallStatus();   // atualiza o ✓ após instalar
        }
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        InstallProgress.Value = 0;
        await RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        try
        {
            // O NXProject pode estar aberto e segurando DLLs (ex.: Accessibility.dll) em memória,
            // fazendo a cópia falhar com "being used by another process". Encerra antes de copiar.
            Step2StatusText.Text = App.Str("Setup_ClosingApp");
            await Task.Run(StopRunningNxProject);

            Directory.CreateDirectory(_installDir);

            Step2StatusText.Text = App.Str("Setup_InstallingBase");
            await Task.Run(() => CopySetupBundledFiles(_installDir));
            InstallProgress.Value = 15;

            Step2StatusText.Text = App.Str("Setup_CheckingLatest");
            var release = await NXProject.Services.UpdateService.GetLatestReleaseInfoAsync();
            if (release is null)
            {
                ShowError(App.Str("Setup_CannotGetLatest"));
                return;
            }
            InstallProgress.Value = 20;

            Step2StatusText.Text = App.Str("Setup_Downloading", release.TagName);
            var extractedDir = await NXProject.Services.UpdateService.DownloadAndExtractAsync(
                release.DownloadUrl,
                new Progress<int>(p => InstallProgress.Value = 20 + p * 0.65));

            Step2StatusText.Text = App.Str("Setup_Installing");
            CopyAllFiles(extractedDir, _installDir);
            TryDeleteTempParent(extractedDir);
            InstallProgress.Value = 90;

            var exePath = Path.Combine(_installDir, "NXProject.Community.exe");
            if (!File.Exists(exePath))
            {
                ShowError(App.Str("Setup_IncompleteInstall", Path.GetFileName(exePath)));
                return;
            }

            // Sem o runtime ao lado do exe (copia interrompida, antivirus, disco cheio), o
            // app self-contained abre o dialogo generico "instale o .NET Desktop Runtime" —
            // erro confuso. Falha aqui, apontando os arquivos que faltaram.
            var missing = GetMissingRuntimeFiles(_installDir);
            if (missing.Count > 0)
            {
                ShowError(App.Str("Setup_IncompleteInstall", string.Join(", ", missing)));
                return;
            }

            WriteReadme(_installDir);
            CreateDesktopShortcut(exePath);
            InstallProgress.Value = 100;

            // NÃO abre o app nem fecha o Setup: o usuário ainda pode usar o Passo 3 (instalar IA).
            // Mostra o botão "Abrir NXProject" ao lado de Fechar (o app abre quando ele quiser).
            Step2StatusText.Text = App.Str("Setup_InstalledOpenWhenReady");
            Step2Button.Content = App.Str("Setup_Step2ButtonUpdate");
            OpenAppButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowError(App.Str("Setup_InstallFail", ex.Message));
        }
        finally
        {
            Step2Button.IsEnabled = true;
        }
    }

    // Encerra qualquer NXProject.Community em execução (o próprio app instalado que pode estar
    // aberto), para liberar as DLLs antes de sobrescrever os arquivos na atualização.
    private static void StopRunningNxProject()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("NXProject.Community");
            foreach (var p in procs)
            {
                try { p.Kill(true); p.WaitForExit(5000); }
                catch { /* já saiu / sem permissão: segue e tenta copiar mesmo assim */ }
                finally { p.Dispose(); }
            }
            // Aguarda o SO liberar os handles das DLLs após o processo sair.
            if (procs.Length > 0)
                System.Threading.Thread.Sleep(500);
        }
        catch { /* não bloquear a instalação por falha ao listar/matar processos */ }
    }

    private static bool TryStart(string exePath, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
            using var proc = Process.Start(psi);
            return proc != null;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteReadme(string installDir)
    {
        var text = App.Str("Setup_ReadmeText",
            NXProject.Services.UpdateService.DotNetDesktopRuntimeDownloadUrl);
        File.WriteAllText(Path.Combine(installDir, "README-INSTALACAO.txt"), text);
    }

    private void ShowError(string message)
    {
        Step2StatusText.Text = App.Str("Setup_Interrupted");
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Arquivos do runtime self-contained que precisam estar AO LADO do exe: sem eles o
    /// Windows pede para "instalar o .NET Desktop Runtime", mesmo com o app self-contained.
    /// </summary>
    private static readonly string[] RequiredRuntimeFiles =
    {
        "hostfxr.dll", "hostpolicy.dll", "coreclr.dll",
        "System.Private.CoreLib.dll", "PresentationFramework.dll",
        "NXProject.Community.dll", "NXProject.Community.runtimeconfig.json"
    };

    /// <summary>Arquivos essenciais que NAO chegaram na pasta de instalacao.</summary>
    private static List<string> GetMissingRuntimeFiles(string installDir) =>
        RequiredRuntimeFiles.Where(f => !File.Exists(Path.Combine(installDir, f))).ToList();

    // Arquivos do proprio Setup — nao devem ir para a pasta de instalacao do NXProject.
    private static readonly string[] SetupOwnFileNames =
    {
        "NXProject-Setup.exe", "NXProject-Setup.dll", "NXProject-Setup.pdb",
        "NXProject-Setup.deps.json", "NXProject-Setup.runtimeconfig.json"
    };

    // O Setup e publicado self-contained: o runtime .NET/WPF e as bibliotecas de
    // terceiros (PdfSharp, WebView2, CommunityToolkit.Mvvm) ja ficam soltas ao lado
    // do proprio executavel. Aqui so copiamos esses arquivos para a instalacao —
    // sem download, sem zip embutido.
    private static void CopySetupBundledFiles(string installDir)
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var file in Directory.GetFiles(baseDir, "*", SearchOption.AllDirectories))
        {
            if (SetupOwnFileNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                continue;

            var rel = Path.GetRelativePath(baseDir, file);
            var dest = Path.Combine(installDir, rel);
            var destFolder = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destFolder)) Directory.CreateDirectory(destFolder);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void CopyAllFiles(string sourceDir, string destDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            var destFolder = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destFolder)) Directory.CreateDirectory(destFolder);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void TryDeleteTempParent(string extractedDir)
    {
        try
        {
            var parent = Path.GetDirectoryName(extractedDir);
            if (parent != null && Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
        catch
        {
            // Limpeza de temporário é best-effort; falha aqui não deve interromper a instalação.
        }
    }

    private static void CreateDesktopShortcut(string exePath)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shortcutPath = Path.Combine(desktop, "NXProject.lnk");
            var workingDir = Path.GetDirectoryName(exePath) ?? "";

            var script = $$"""
                $ws = New-Object -ComObject WScript.Shell
                $sc = $ws.CreateShortcut('{{Escape(shortcutPath)}}')
                $sc.TargetPath = '{{Escape(exePath)}}'
                $sc.WorkingDirectory = '{{Escape(workingDir)}}'
                $sc.IconLocation = '{{Escape(exePath)}},0'
                $sc.Save()
                """;

            var psFile = Path.Combine(Path.GetTempPath(), $"nxshortcut_{Guid.NewGuid():N}.ps1");
            File.WriteAllText(psFile, script);

            using var proc = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{psFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            proc?.WaitForExit(10_000);

            if (File.Exists(psFile)) File.Delete(psFile);
        }
        catch
        {
            // Atalho é conveniência, não impede a conclusão da instalação.
        }
    }

    private static string Escape(string path) => path.Replace("'", "''");
}
