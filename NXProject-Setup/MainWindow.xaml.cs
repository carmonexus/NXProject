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
        Loaded += (_, _) => RefreshStep1State();
    }

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
        if (CodexCheck.IsChecked != true && ClaudeCheck.IsChecked != true)
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

            Step3StatusText.Text = App.Str("Setup_Step3Done", NXProject.Services.AiCliInstaller.BinDir);
        }
        catch (Exception ex)
        {
            Step3StatusText.Text = App.Str("Setup_Step3Fail", ex.Message);
        }
        finally
        {
            Step3Button.IsEnabled = true;
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

            Step2StatusText.Text = App.Str("Setup_Done");
            var started = TryStart(exePath);
            if (!started)
            {
                ShowError(App.Str("Setup_InstalledCantOpen",
                    NXProject.Services.UpdateService.DotNetDesktopRuntimeDownloadUrl, _installDir));
                return;
            }

            await Task.Delay(800);
            Close();
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

    private static bool TryStart(string exePath)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(exePath)
            {
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                UseShellExecute = true
            });
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
