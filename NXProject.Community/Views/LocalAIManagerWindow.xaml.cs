using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using NXProject.Models;
using NXProject.Community.Services;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>
    /// Gerenciar IA Local (LLaMA): pasta dos recursos, download (backend CPU + modelo GGUF),
    /// instalação manual (links para copiar quando o download é bloqueado pela rede da
    /// empresa) e validação da instalação (arquivos + teste de carga da DLL).
    /// </summary>
    public partial class LocalAIManagerWindow : Window
    {
        private CancellationTokenSource? _cts;
        private readonly bool _autoInstall;

        // Config dos CLIs (Codex/Claude) reaproveitada do Assistente, com auto-save.
        private const string StorageKey = "NXProject.Community";
        private AIWorkspaceSettings? _aiWs;
        private bool _loadingCli;
        // Lembra o comando por (provedor, Windows/WSL) para a troca da combo NÃO apagar o
        // comando que funciona (ex.: o do WSL) ao alternar de local.
        private readonly System.Collections.Generic.Dictionary<string, string> _cliMem = new();
        private static string MemKey(AIProvider p, bool win) => $"{p}|{(win ? "win" : "wsl")}";

        public LocalAIManagerWindow(bool autoInstall = false)
        {
            InitializeComponent();
            _autoInstall = autoInstall;

            FolderBox.Text = LocalAIResourceStore.LoadFolder();
            SetKindRadios(LocalAIResourceStore.LoadBackendKind());
            var maxTokens = LocalAIResourceStore.LoadMaxResponseTokens();
            MaxTokensBox.Text = maxTokens.ToString();
            SelectMaxTokensPreset(maxTokens);   // marca Min/Ideal/Máximo ou "Customizado"
            UpdateManualLinks();

            Loaded += (_, _) =>
            {
                RefreshStatus(validateLoad: false);
                RefreshCliStatus();
                PopulateCliConfigs();
                if (_autoInstall) OnDownloadClick(this, new RoutedEventArgs());
            };
            // Fechar durante um download cancela o download primeiro; o segundo clique fecha.
            Closing += (_, args) =>
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                    args.Cancel = true;
                    Log(AppStrings.Get("LocalAI_CancelFirst"));
                }
            };
        }

        // ── Abas Codex / Claude Code (CLIs de IA para Windows) ───────────────
        // Instalam SEMPRE o binário nativo mais novo (mesma lógica do Setup), em
        // %LOCALAPPDATA%\NXProject\bin, e adicionam ao PATH do usuário.
        private void RefreshCliStatus()
        {
            CodexStatusText.Text = NXProject.Services.AiCliInstaller.CodexPath is { } cp
                ? AppStrings.Get("LocalAI_CliInstalledAt", cp)
                : AppStrings.Get("LocalAI_CliNotInstalled");
            ClaudeStatusText.Text = NXProject.Services.AiCliInstaller.ClaudePath is { } clp
                ? AppStrings.Get("LocalAI_CliInstalledAt", clp)
                : AppStrings.Get("LocalAI_CliNotInstalled");
        }

        private async void OnInstallCodexClick(object sender, RoutedEventArgs e)
        {
            CodexInstallBtn.IsEnabled = false;
            var status = new System.Progress<string>(s => CodexStatusText.Text = s);
            try { await NXProject.Services.AiCliInstaller.InstallCodexAsync(status); }
            catch (System.Exception ex) { CodexStatusText.Text = AppStrings.Get("LocalAI_CliFail", ex.Message); }
            finally { CodexInstallBtn.IsEnabled = true; RefreshCliStatus(); }
        }

        private async void OnInstallClaudeClick(object sender, RoutedEventArgs e)
        {
            ClaudeInstallBtn.IsEnabled = false;
            var status = new System.Progress<string>(s => ClaudeStatusText.Text = s);
            try { await NXProject.Services.AiCliInstaller.InstallClaudeCodeAsync(status); }
            catch (System.Exception ex) { ClaudeStatusText.Text = AppStrings.Get("LocalAI_CliFail", ex.Message); }
            finally { ClaudeInstallBtn.IsEnabled = true; RefreshCliStatus(); }
        }

        private void OnOpenCliFolderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(NXProject.Services.AiCliInstaller.BinDir);
                Process.Start(new ProcessStartInfo(NXProject.Services.AiCliInstaller.BinDir) { UseShellExecute = true });
            }
            catch { /* abrir pasta é conveniência */ }
        }

        // ── Config Windows/WSL + testar conexão (mesmo do Assistente; auto-salva) ──
        private void PopulateCliConfigs()
        {
            _aiWs = AISettingsStore.LoadWorkspace(StorageKey);
            PopulateCli(AIProvider.CodexCli, CodexCommandBox, CodexLocationCombo, CodexLocationStatus);
            PopulateCli(AIProvider.ClaudeCli, ClaudeCommandBox, ClaudeLocationCombo, ClaudeLocationStatus);
        }

        private void PopulateCli(AIProvider provider, TextBox command, ComboBox location, TextBlock status)
        {
            var profile = _aiWs!.GetOrCreate(provider);
            _loadingCli = true;

            // Ativo = Endpoint (o que está valendo). Windows e WSL guardados SEPARADAMENTE;
            // se ainda não houver os campos separados, migra do ativo/gera o padrão.
            var active = string.IsNullOrWhiteSpace(profile.Endpoint)
                ? CodexCliService.GetDefaultCommand(provider) : profile.Endpoint;
            var activeWin = CodexCliService.IsWindowsCommand(active);
            var winCmd = !string.IsNullOrWhiteSpace(profile.CliWindowsCommand) ? profile.CliWindowsCommand
                         : (activeWin ? active : CodexCliService.BuildCommand(provider, true));
            var wslCmd = !string.IsNullOrWhiteSpace(profile.CliWslCommand) ? profile.CliWslCommand
                         : (!activeWin ? active : CodexCliService.BuildCommand(provider, false));

            // Migra o comando WSL antigo (sem "bash -lic") para o novo padrão e persiste.
            var upgraded = CodexCliService.UpgradeWslDefault(provider, wslCmd);
            if (!string.Equals(upgraded, wslCmd, StringComparison.Ordinal))
            {
                wslCmd = upgraded;
                profile.CliWslCommand = wslCmd;
                if (!activeWin) { active = wslCmd; profile.Endpoint = active; }
                AISettingsStore.SaveWorkspace(_aiWs, StorageKey);
            }

            _cliMem[MemKey(provider, true)] = winCmd;
            _cliMem[MemKey(provider, false)] = wslCmd;

            command.Text = active;
            foreach (var item in location.Items.OfType<ComboBoxItem>())
                if ((item.Tag as string == "win") == activeWin) { location.SelectedItem = item; break; }
            _loadingCli = false;
            status.Text = string.Empty;
        }

        private void OnCodexLocationChanged(object sender, SelectionChangedEventArgs e)
            => ApplyCliLocation(AIProvider.CodexCli, CodexLocationCombo, CodexCommandBox, CodexLocationStatus);
        private void OnClaudeLocationChanged(object sender, SelectionChangedEventArgs e)
            => ApplyCliLocation(AIProvider.ClaudeCli, ClaudeLocationCombo, ClaudeCommandBox, ClaudeLocationStatus);

        private void ApplyCliLocation(AIProvider provider, ComboBox location, TextBox command, TextBlock status)
        {
            if (_loadingCli) return;
            var windows = (location.SelectedItem as ComboBoxItem)?.Tag as string == "win";
            // Restaura o comando que já funcionava para ESTE local (se houver) em vez de
            // regenerar o padrão e apagar o que o usuário tinha. Só usa o padrão na 1ª vez.
            command.Text = _cliMem.TryGetValue(MemKey(provider, windows), out var saved) && !string.IsNullOrWhiteSpace(saved)
                ? saved
                : CodexCliService.BuildCommand(provider, windows); // dispara auto-save via TextChanged
            if (!windows)
            {
                status.Foreground = Brushes.DimGray;
                status.Text = AppStrings.Get("AI_CliWslNote");
                return;
            }
            var cli = CodexCliService.CliName(provider);
            var path = CodexCliService.FindOnWindowsPath(cli);
            status.Inlines.Clear();
            if (path != null)
            {
                status.Foreground = Brushes.Green;
                status.Inlines.Add(new Run(AppStrings.Get("AI_CliFoundAt", path)));
                return;
            }
            status.Foreground = Brushes.DarkOrange;
            status.Inlines.Add(new Run(AppStrings.Get("AI_CliNotOnPath", cli) + " "));
            var url = provider == AIProvider.ClaudeCli
                ? "https://code.claude.com/docs/en/quickstart"
                : "https://developers.openai.com/codex/cli/";
            var link = new Hyperlink(new Run(AppStrings.Get("AI_CliDownloadLink"))) { NavigateUri = new Uri(url) };
            link.Click += (_, _) => OpenExternal(url);
            status.Inlines.Add(link);
        }

        private void OnCodexCommandChanged(object sender, TextChangedEventArgs e)
            => SaveCli(AIProvider.CodexCli, CodexCommandBox, CodexLocationCombo);
        private void OnClaudeCommandChanged(object sender, TextChangedEventArgs e)
            => SaveCli(AIProvider.ClaudeCli, ClaudeCommandBox, ClaudeLocationCombo);

        private void SaveCli(AIProvider provider, TextBox command, ComboBox location)
        {
            if (_loadingCli || _aiWs == null) return;
            // Guarda o comando SEPARADAMENTE por local (Windows/WSL) e marca o ativo (Endpoint).
            var win = (location.SelectedItem as ComboBoxItem)?.Tag as string == "win";
            var txt = command.Text?.Trim() ?? string.Empty;
            _cliMem[MemKey(provider, win)] = txt;
            var profile = _aiWs.GetOrCreate(provider);
            if (win) profile.CliWindowsCommand = txt; else profile.CliWslCommand = txt;
            profile.Endpoint = txt;   // ativo = o que a combo aponta
            profile.Model = string.Empty;
            profile.ApiKey = string.Empty;
            profile.AuthMode = AIAuthMode.ApiKey;
            if (profile.TimeoutSeconds <= 0)
                profile.TimeoutSeconds = AIProviderDefaults.GetDefaultTimeoutSeconds(provider);
            AISettingsStore.SaveWorkspace(_aiWs, StorageKey);
        }

        private async void OnCodexTestClick(object sender, RoutedEventArgs e)
            => await CliTest(CodexCommandBox, CodexTestButton, CodexTestResult);
        private async void OnClaudeTestClick(object sender, RoutedEventArgs e)
            => await CliTest(ClaudeCommandBox, ClaudeTestButton, ClaudeTestResult);

        // Abre um terminal rodando o LOGIN do CLI (fluxo de autenticação no navegador).
        // Usa o mesmo local (Windows/WSL) do comando configurado e injeta a pasta do NX no PATH.
        private void OnCodexAuthClick(object sender, RoutedEventArgs e)
            => LaunchCliLogin(AIProvider.CodexCli, CodexCommandBox, CodexTestResult);
        private void OnClaudeAuthClick(object sender, RoutedEventArgs e)
            => LaunchCliLogin(AIProvider.ClaudeCli, ClaudeCommandBox, ClaudeTestResult);

        private void LaunchCliLogin(AIProvider provider, TextBox command, TextBlock result)
        {
            var windows = CodexCliService.IsWindowsCommand(command.Text);
            var cli = CodexCliService.CliName(provider);
            // Codex tem "codex login"; o Claude Code autentica ao rodar "claude" (faz /login no 1º uso).
            var inner = provider == AIProvider.CodexCli ? $"{cli} login" : cli;
            var full = windows ? inner : $"wsl.exe -- bash -lic \"{inner}\"";
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/k " + full)
                {
                    UseShellExecute = false,
                    CreateNoWindow = false,
                };
                var nxBin = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXProject", "bin");
                var cur = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                psi.Environment["PATH"] = System.IO.Directory.Exists(nxBin) ? nxBin + ";" + cur : cur;
                Process.Start(psi);
                result.Foreground = Brushes.DimGray;
                result.Text = AppStrings.Get("LocalAI_CliAuthOpened");
            }
            catch (Exception ex)
            {
                result.Foreground = Brushes.Firebrick;
                result.Text = ex.Message;
            }
        }

        private async Task CliTest(TextBox commandBox, Button btn, TextBlock result)
        {
            var command = commandBox.Text?.Trim();
            if (CodexCliService.LooksLikeServerScript(command))
            {
                result.Foreground = Brushes.Firebrick;
                result.Text = AppStrings.Get("AI_CodexServerScript");
                return;
            }
            btn.IsEnabled = false;
            result.Foreground = Brushes.DimGray;
            result.Text = AppStrings.Get("AI_CodexTesting");
            try { await RunCliTest(commandBox, result, command, 120); }
            finally { btn.IsEnabled = true; }
        }

        // Testa um comando de CLI local; se nem iniciar, tenta a forma alternativa (nativo <-> WSL).
        private static async Task RunCliTest(TextBox commandBox, TextBlock result, string? command, int timeoutSeconds)
        {
            try
            {
                var answer = await CodexCliService.GenerateAsync(
                    "Responda SEMPRE com uma unica palavra: OK", "Diga OK.", command, timeoutSeconds);
                result.Foreground = Brushes.Green;
                result.Text = AppStrings.Get("AI_CodexTestOk", answer.Length > 60 ? answer[..60] + "..." : answer);
            }
            catch (NXProject.Services.CliStartException)
            {
                var alt = CodexCliService.AlternateCommand(command);
                if (alt == null)
                {
                    result.Foreground = Brushes.Firebrick;
                    result.Text = AppStrings.Get("AI_CliNotFound", command ?? "");
                    return;
                }
                try
                {
                    var answer = await CodexCliService.GenerateAsync(
                        "Responda SEMPRE com uma unica palavra: OK", "Diga OK.", alt, timeoutSeconds);
                    commandBox.Text = alt;   // salva o comando que realmente funciona (auto-save)
                    result.Foreground = Brushes.Green;
                    result.Text = AppStrings.Get("AI_CliAdjusted", alt, answer.Length > 40 ? answer[..40] + "..." : answer);
                }
                catch (Exception ex2)
                {
                    result.Foreground = Brushes.Firebrick;
                    result.Text = AppStrings.Get("AI_CliBothFailed", command ?? "", alt) + "\n" + ex2.Message;
                }
            }
            catch (Exception ex)
            {
                result.Foreground = Brushes.Firebrick;
                result.Text = ex.Message;
            }
        }

        private static void OpenExternal(string url)
            => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

        // ── Processamento (CPU/GPU) ──────────────────────────────────────────
        private bool _updatingKindRadios;

        private LocalAIResourceStore.BackendKind SelectedKind =>
            BackendCudaRadio.IsChecked == true ? LocalAIResourceStore.BackendKind.Cuda12
            : BackendVulkanRadio.IsChecked == true ? LocalAIResourceStore.BackendKind.Vulkan
            : LocalAIResourceStore.BackendKind.Cpu;

        private void SetKindRadios(LocalAIResourceStore.BackendKind kind)
        {
            _updatingKindRadios = true;
            BackendCpuRadio.IsChecked = kind == LocalAIResourceStore.BackendKind.Cpu;
            BackendCudaRadio.IsChecked = kind == LocalAIResourceStore.BackendKind.Cuda12;
            BackendVulkanRadio.IsChecked = kind == LocalAIResourceStore.BackendKind.Vulkan;
            _updatingKindRadios = false;
        }

        /// <summary>
        /// Troca de backend: valida a MELHOR escolha para a máquina. Sem GPU/driver
        /// compatível (nvcuda.dll para CUDA, vulkan-1.dll para Vulkan), avisa e oferece
        /// voltar para CPU — confirmar segue por conta (ex.: pasta de rede preparada para
        /// outra máquina). Vulkan com NVIDIA presente sugere CUDA (rende mais); CPU com
        /// GPU disponível ganha uma dica no log.
        /// </summary>
        private void OnBackendKindChanged(object sender, RoutedEventArgs e)
        {
            if (_updatingKindRadios || !IsLoaded) return;
            var kind = SelectedKind;
            var hasCuda = LocalAIResourceStore.IsBackendSupported(LocalAIResourceStore.BackendKind.Cuda12);
            var hasVulkan = LocalAIResourceStore.IsBackendSupported(LocalAIResourceStore.BackendKind.Vulkan);

            if (kind != LocalAIResourceStore.BackendKind.Cpu && !LocalAIResourceStore.IsBackendSupported(kind))
            {
                // GPU escolhida mas a máquina não tem o driver: avisa; Não volta para CPU.
                var key = kind == LocalAIResourceStore.BackendKind.Cuda12
                    ? "LocalAI_GpuNotFoundCuda" : "LocalAI_GpuNotFoundVulkan";
                var r = MessageBox.Show(this, AppStrings.Get(key),
                    AppStrings.Get("LocalAI_Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes)
                {
                    SetKindRadios(LocalAIResourceStore.BackendKind.Cpu);
                    kind = LocalAIResourceStore.BackendKind.Cpu;
                }
            }
            else if (kind == LocalAIResourceStore.BackendKind.Vulkan && hasCuda)
            {
                // Vulkan funciona, mas com NVIDIA presente o CUDA é a melhor escolha.
                var r = MessageBox.Show(this, AppStrings.Get("LocalAI_GpuBetterCuda"),
                    AppStrings.Get("LocalAI_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes)
                {
                    SetKindRadios(LocalAIResourceStore.BackendKind.Cuda12);
                    kind = LocalAIResourceStore.BackendKind.Cuda12;
                }
            }
            else if (kind == LocalAIResourceStore.BackendKind.Cpu && (hasCuda || hasVulkan))
            {
                Log(AppStrings.Get("LocalAI_GpuAvailableHint",
                    hasCuda ? AppStrings.Get("LocalAI_BackendCuda") : AppStrings.Get("LocalAI_BackendVulkan")));
            }

            LocalAIResourceStore.SaveBackendKind(kind);

            // Backend diferente do instalado com a DLL já carregada nesta sessão: avisa JÁ
            // na troca do radio que a mudança exige reiniciar o NXProject (arquivos travados).
            var folderNow = FolderBox.Text?.Trim() ?? "";
            var installedNow = LocalAIResourceStore.GetInstalledBackendKind(folderNow)
                               ?? LocalAIResourceStore.BackendKind.Cpu;
            if (kind != installedNow && LocalAIResourceStore.IsNativeLoaded)
            {
                var msg = AppStrings.Get("LocalAI_BackendNeedRestart", LocalAIResourceStore.BackendDisplayName(kind));
                MessageBox.Show(this, msg, AppStrings.Get("LocalAI_Title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Log(msg.Replace("\n\n", " ").Replace("\n", " "));
            }

            UpdateManualLinks();
            RefreshStatus(validateLoad: false);
        }

        // Teto de tokens da resposta da IA Local: grava com clamp e reexibe o valor efetivo.
        // (só editável no modo "Customizado"; nos presets o campo fica travado.)
        private void OnMaxTokensChanged(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(MaxTokensBox.Text?.Trim(), out var tokens))
                LocalAIResourceStore.SaveMaxResponseTokens(tokens);
            MaxTokensBox.Text = LocalAIResourceStore.LoadMaxResponseTokens().ToString();
        }

        // Combo Mínimo/Ideal/Máximo/Customizado: preenche o campo Token (o usuário não precisa
        // saber o número). "Customizado" libera o campo para digitação livre.
        private void OnMaxTokensPresetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MaxTokensBox == null || MaxTokensPreset.SelectedItem is not ComboBoxItem item) return;
            var tag = item.Tag?.ToString();
            if (string.Equals(tag, "custom", StringComparison.OrdinalIgnoreCase))
            {
                MaxTokensBox.IsReadOnly = false;   // campo livre de novo
                MaxTokensBox.Focus();
                MaxTokensBox.SelectAll();
            }
            else if (int.TryParse(tag, out var t))
            {
                MaxTokensBox.IsReadOnly = true;
                LocalAIResourceStore.SaveMaxResponseTokens(t);
                MaxTokensBox.Text = LocalAIResourceStore.LoadMaxResponseTokens().ToString();
            }
        }

        // Marca no combo o preset correspondente ao valor atual; se não bater, "Customizado".
        private void SelectMaxTokensPreset(int tokens)
        {
            foreach (var it in MaxTokensPreset.Items.OfType<ComboBoxItem>())
            {
                if (int.TryParse(it.Tag?.ToString(), out var t) && t == tokens)
                {
                    MaxTokensPreset.SelectedItem = it;
                    MaxTokensBox.IsReadOnly = true;
                    return;
                }
            }
            // Não bateu com nenhum preset -> Customizado (campo livre).
            MaxTokensPreset.SelectedItem = MaxTokensPreset.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), "custom", StringComparison.OrdinalIgnoreCase));
            MaxTokensBox.IsReadOnly = false;
        }

        private void UpdateManualLinks()
        {
            var kind = SelectedKind;
            var subfolder = kind switch
            {
                LocalAIResourceStore.BackendKind.Cuda12 => "runtimes/win-x64/native/cuda12",
                LocalAIResourceStore.BackendKind.Vulkan => "runtimes/win-x64/native/vulkan",
                _ => "runtimes/win-x64/native/avx2",
            };
            ManualLinksBox.Text =
                $"1) Backend {LocalAIResourceStore.BackendDisplayName(kind)} (renomeie para .zip e extraia as DLLs de {subfolder} para a pasta):\n" +
                $"{LocalAIResourceStore.BackendNupkgUrl(kind)}\n\n" +
                $"2) Modelo (~2 GB — copie o arquivo para a pasta):\n" +
                $"{LocalAIResourceStore.ModelUrl}";
        }

        private void Log(string message)
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        }

        private void RefreshStatus(bool validateLoad)
        {
            var folder = FolderBox.Text?.Trim() ?? "";
            var r = LocalAIResourceStore.Validate(folder);

            NativeStatusText.Text = r.NativePresent
                ? (!validateLoad
                    ? AppStrings.Get("LocalAI_NativeFound", LocalAIResourceStore.NativeDllName)
                    : r.NativeLoads
                        ? AppStrings.Get("LocalAI_NativeOk", LocalAIResourceStore.NativeDllName)
                        : "✗ " + r.NativeMessage)
                : "✗ " + r.NativeMessage;

            ModelStatusText.Text = r.ModelPresent
                ? r.ModelValid
                    ? AppStrings.Get("LocalAI_ModelOk", r.ModelFile ?? "")
                    : "✗ " + r.ModelMessage
                : "✗ " + r.ModelMessage;

            // Versão instalada do llama.dll (registrada pelo download) × homologada pelo NX.
            var installed = LocalAIResourceStore.GetInstalledBackendVersion(folder);
            BackendVersionText.Text = AppStrings.Get("LocalAI_VersionLine",
                installed ?? AppStrings.Get("LocalAI_UpdUnknown"),
                LocalAIResourceStore.BackendVersion);

            // Link de onde o llama.dll foi/será baixado (backend selecionado; versão
            // instalada quando registrada, senão a homologada).
            BackendSourceLinkText.Text = LocalAIResourceStore.BackendNupkgUrl(SelectedKind, installed);
        }

        private void OnBackendSourceLinkClick(object sender, RoutedEventArgs e)
            => OpenUrl(BackendSourceLinkText.Text);

        // Documentação (overview) do LLamaSharp, projeto por trás do llama.dll usado pelo NX.
        private void OnBackendDocsLinkClick(object sender, RoutedEventArgs e)
            => OpenUrl("https://scisharp.github.io/LLamaSharp/");

        private void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log(ex.Message);
            }
        }

        private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog();
            if (!string.IsNullOrWhiteSpace(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
            if (dlg.ShowDialog(this) == true)
            {
                FolderBox.Text = dlg.FolderName;
                LocalAIResourceStore.SaveFolder(dlg.FolderName);
                RefreshStatus(validateLoad: false);
            }
        }

        private async void OnDownloadClick(object sender, RoutedEventArgs e)
        {
            var folder = FolderBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                Log(AppStrings.Get("LocalAI_NeedFolder"));
                return;
            }
            LocalAIResourceStore.SaveFolder(folder);

            _cts = new CancellationTokenSource();
            DownloadBtn.IsEnabled = false;
            ValidateBtn.IsEnabled = false;
            DownloadProgress.Visibility = Visibility.Visible;
            DownloadProgress.IsIndeterminate = true;
            var status = new Progress<string>(Log);
            var percent = new Progress<double>(p =>
            {
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = p;
            });
            try
            {
                var check = LocalAIResourceStore.Validate(folder);
                // Backend selecionado ≠ instalado (pasta sem registro = instalação antiga/manual,
                // assumida CPU): re-baixa as DLLs nativas do backend novo por cima.
                var kind = SelectedKind;
                var installedKind = LocalAIResourceStore.GetInstalledBackendKind(folder)
                                    ?? LocalAIResourceStore.BackendKind.Cpu;
                var switchBackend = check.NativePresent && installedKind != kind;
                if (!check.NativePresent || switchBackend)
                {
                    if (switchBackend && LocalAIResourceStore.IsNativeLoaded)
                    {
                        // DLLs travadas nesta sessão: não dá para sobrescrever agora —
                        // alerta claro (não só log) de que precisa reiniciar o NXProject.
                        var msg = AppStrings.Get("LocalAI_BackendNeedRestart", LocalAIResourceStore.BackendDisplayName(kind));
                        MessageBox.Show(this, msg, AppStrings.Get("LocalAI_Title"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        Log(msg.Replace("\n\n", " ").Replace("\n", " "));
                    }
                    else
                    {
                        if (switchBackend)
                            Log(AppStrings.Get("LocalAI_BackendSwitch",
                                LocalAIResourceStore.BackendDisplayName(kind),
                                LocalAIResourceStore.BackendDisplayName(installedKind)));
                        await LocalAIResourceStore.DownloadBackendAsync(folder, status, _cts.Token, kind: kind);
                    }
                }
                else
                    Log(AppStrings.Get("LocalAI_NativeSkip"));

                if (!check.ModelPresent)
                    await LocalAIResourceStore.DownloadModelAsync(folder, status, percent, _cts.Token);
                else
                    Log(AppStrings.Get("LocalAI_ModelSkip", check.ModelFile ?? ""));

                Log(AppStrings.Get("LocalAI_DownloadDone"));
                RefreshStatus(validateLoad: true);
            }
            catch (OperationCanceledException)
            {
                Log(AppStrings.Get("LocalAI_Cancelled"));
            }
            catch (Exception ex)
            {
                Log("ERRO: " + ex.Message);
                Log(AppStrings.Get("LocalAI_DownloadBlockedHint"));
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                DownloadBtn.IsEnabled = true;
                ValidateBtn.IsEnabled = true;
                DownloadProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void OnValidateClick(object sender, RoutedEventArgs e)
        {
            var folder = FolderBox.Text?.Trim() ?? "";
            LocalAIResourceStore.SaveFolder(folder);
            var r = LocalAIResourceStore.Validate(folder);
            RefreshStatus(validateLoad: true);
            Log(r.Ok
                ? AppStrings.Get("LocalAI_ValidateOk")
                : AppStrings.Get("LocalAI_ValidateFail"));
            if (!r.NativeLoads && r.NativeMessage != null) Log(r.NativeMessage);
            if (!r.ModelValid && r.ModelMessage != null) Log(r.ModelMessage);
        }

        /// <summary>
        /// Verifica no NuGet se há versão nova do backend (llama.dll). A versão homologada
        /// pelo NX é a testada com o app; versão mais nova ainda NÃO homologada é oferecida
        /// com aviso — o usuário escolhe entre a testada e a mais nova.
        /// </summary>
        private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
        {
            var folder = FolderBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                Log(AppStrings.Get("LocalAI_NeedFolder"));
                return;
            }
            LocalAIResourceStore.SaveFolder(folder);

            CheckUpdateBtn.IsEnabled = false;
            try
            {
                var installed = LocalAIResourceStore.GetInstalledBackendVersion(folder);
                var homologated = LocalAIResourceStore.BackendVersion;
                Log(AppStrings.Get("LocalAI_UpdVersions",
                    installed ?? AppStrings.Get("LocalAI_UpdUnknown"), homologated));

                string? latest;
                try
                {
                    latest = await LocalAIResourceStore.GetLatestBackendVersionAsync(CancellationToken.None, SelectedKind);
                }
                catch (Exception ex)
                {
                    Log(AppStrings.Get("LocalAI_UpdCheckFail", ex.Message));
                    return;
                }
                if (string.IsNullOrWhiteSpace(latest))
                {
                    Log(AppStrings.Get("LocalAI_UpdCheckFail", "NuGet sem versões estáveis."));
                    return;
                }
                Log(AppStrings.Get("LocalAI_UpdLatest", latest));

                string chosenVersion;
                if (LocalAIResourceStore.IsNewerVersion(latest, homologated))
                {
                    // Versão mais nova existe, mas ainda não foi homologada pelo NX.
                    var r = MessageBox.Show(this,
                        AppStrings.Get("LocalAI_UpdNotCertifiedBody", latest, homologated),
                        AppStrings.Get("LocalAI_Title"),
                        MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                    if (r == MessageBoxResult.Cancel) { Log(AppStrings.Get("LocalAI_Cancelled")); return; }
                    chosenVersion = r == MessageBoxResult.Yes ? homologated : latest;
                    if (r == MessageBoxResult.No)
                        Log(AppStrings.Get("LocalAI_UpdNotCertifiedWarn", latest));
                }
                else
                {
                    if (installed != null && !LocalAIResourceStore.IsNewerVersion(homologated, installed))
                    {
                        Log(AppStrings.Get("LocalAI_UpdUpToDate", installed));
                        return;
                    }
                    chosenVersion = homologated;
                }

                if (!LocalAIResourceStore.IsNewerVersion(chosenVersion, installed) && installed != null
                    && string.Equals(chosenVersion, installed, StringComparison.OrdinalIgnoreCase))
                {
                    Log(AppStrings.Get("LocalAI_UpdUpToDate", installed));
                    return;
                }

                // DLL já carregada neste processo fica travada — atualização exige reiniciar o NX.
                if (LocalAIResourceStore.IsNativeLoaded)
                {
                    Log(AppStrings.Get("LocalAI_UpdNeedRestart"));
                    return;
                }

                _cts = new CancellationTokenSource();
                DownloadBtn.IsEnabled = false;
                ValidateBtn.IsEnabled = false;
                try
                {
                    await LocalAIResourceStore.DownloadBackendAsync(folder, new Progress<string>(Log), _cts.Token, chosenVersion, SelectedKind);
                    Log(AppStrings.Get("LocalAI_UpdDone", chosenVersion));
                    RefreshStatus(validateLoad: true);
                }
                catch (OperationCanceledException)
                {
                    Log(AppStrings.Get("LocalAI_Cancelled"));
                }
                catch (Exception ex)
                {
                    Log("ERRO: " + ex.Message);
                    Log(AppStrings.Get("LocalAI_DownloadBlockedHint"));
                }
                finally
                {
                    _cts.Dispose();
                    _cts = null;
                    DownloadBtn.IsEnabled = true;
                    ValidateBtn.IsEnabled = true;
                }
            }
            finally
            {
                CheckUpdateBtn.IsEnabled = true;
            }
        }

        private void OnCopyLinksClick(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(ManualLinksBox.Text);
            Log(AppStrings.Get("LocalAI_LinksCopied"));
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
