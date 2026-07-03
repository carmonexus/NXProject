using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
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
            Step1StatusText.Text = "✓ .NET 10 Desktop Runtime já está instalado nesta máquina.";
            Step1Button.IsEnabled = false;
        }
        else
        {
            Step1StatusText.Text = "Não encontrado nesta máquina. Necessário para o NXProject abrir.";
            Step1Button.IsEnabled = true;
        }
    }

    private async void OnStep1Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Step1Button.IsEnabled = false;
            Step1StatusText.Text = "Baixando o instalador do .NET 10 Desktop Runtime...";

            var tempExe = Path.Combine(Path.GetTempPath(), $"windowsdesktop-runtime-{Guid.NewGuid():N}.exe");
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = await client.GetAsync(DotNetRuntimeInstallerUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file = File.Create(tempExe);
                await stream.CopyToAsync(file);
            }

            Step1StatusText.Text = "Instalando .NET 10 Desktop Runtime (pode pedir permissão de administrador)...";
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
                Step1StatusText.Text = "✓ .NET 10 Desktop Runtime instalado com sucesso.";
        }
        catch (Exception ex)
        {
            Step1StatusText.Text =
                $"Falha ao instalar o .NET automaticamente: {ex.Message}\n" +
                $"Baixe manualmente em: {NXProject.Services.UpdateService.DotNetDesktopRuntimeDownloadUrl}";
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

            Step2StatusText.Text = "Instalando bibliotecas base...";
            await Task.Run(() => ExtractEmbeddedOwnLibs(_installDir));
            InstallProgress.Value = 15;

            Step2StatusText.Text = "Verificando a versão mais recente do NXProject...";
            var release = await NXProject.Services.UpdateService.GetLatestReleaseInfoAsync();
            if (release is null)
            {
                ShowError("Não foi possível obter a versão mais recente do NXProject. Verifique sua conexão com a internet e tente novamente.");
                return;
            }
            InstallProgress.Value = 20;

            Step2StatusText.Text = $"Baixando NXProject {release.TagName}...";
            var extractedDir = await NXProject.Services.UpdateService.DownloadAndExtractAsync(
                release.DownloadUrl,
                new Progress<int>(p => InstallProgress.Value = 20 + p * 0.65));

            Step2StatusText.Text = "Instalando NXProject...";
            CopyAllFiles(extractedDir, _installDir);
            TryDeleteTempParent(extractedDir);
            InstallProgress.Value = 90;

            var exePath = Path.Combine(_installDir, "NXProject.Community.exe");
            if (!File.Exists(exePath))
            {
                ShowError($"Instalação incompleta: '{Path.GetFileName(exePath)}' não foi encontrado após o download.");
                return;
            }

            WriteReadme(_installDir);
            CreateDesktopShortcut(exePath);
            InstallProgress.Value = 100;

            Step2StatusText.Text = "Instalação concluída! Iniciando o NXProject...";
            var started = TryStart(exePath);
            if (!started)
            {
                ShowError(
                    "O NXProject foi instalado, mas não foi possível abri-lo automaticamente. " +
                    $"Baixe o .NET em: {NXProject.Services.UpdateService.DotNetDesktopRuntimeDownloadUrl}\n\nDepois, abra o NXProject.Community.exe em: {_installDir}");
                return;
            }

            await Task.Delay(800);
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"Falha na instalação: {ex.Message}");
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
        var text = $"""
            NXProject Community

            Requisito: .NET 10 Desktop Runtime (x64) instalado na máquina.
            Se o aplicativo não abrir ou o Windows pedir para instalar o .NET, baixe em:
            {NXProject.Services.UpdateService.DotNetDesktopRuntimeDownloadUrl}
            (escolha ".NET Desktop Runtime" para Windows x64)

            Contato:
            - Nexus XData Tecnologia Ltda
            - comercial.nexus.xdata@gmail.com
            """;
        File.WriteAllText(Path.Combine(installDir, "README-INSTALACAO.txt"), text);
    }

    private void ShowError(string message)
    {
        Step2StatusText.Text = "Instalação interrompida.";
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Visible;
    }

    private static void ExtractEmbeddedOwnLibs(string installDir)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("own-libs.zip", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            return; // Instalador antigo/sem payload embutido: as libs virao do zip de update.

        using var resourceStream = asm.GetManifestResourceStream(resourceName);
        if (resourceStream is null) return;

        var tempZip = Path.Combine(Path.GetTempPath(), $"nxsetup_ownlibs_{Guid.NewGuid():N}.zip");
        try
        {
            using (var fileStream = File.Create(tempZip))
                resourceStream.CopyTo(fileStream);

            ZipFile.ExtractToDirectory(tempZip, installDir, overwriteFiles: true);
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
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
