using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
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
