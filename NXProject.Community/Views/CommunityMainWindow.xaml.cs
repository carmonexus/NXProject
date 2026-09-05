using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NXProject.Community.Services;
using NXProject.Community.Views;
using NXProject.Models;
using NXProject.Services;
using NXProject.ViewModels;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Polygon = System.Windows.Shapes.Polygon;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace NXProject.Views
{
    public partial class CommunityMainWindow : Window
    {
        private static readonly string LicenseAcceptanceDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXProject.Community");

        private static readonly string LicenseAcceptanceFile =
            Path.Combine(LicenseAcceptanceDirectory, "license.accepted");

        /// <summary>
        /// Log da atualizacao de base via NXProject-Setup. Quando esse fluxo falha na
        /// maquina do usuario (antivirus segurando o zip, politica bloqueando o exe), este
        /// arquivo e a unica evidencia — o caminho e mostrado ANTES da atualizacao.
        /// </summary>
        private static readonly string SetupUpdateLogFile =
            Path.Combine(LicenseAcceptanceDirectory, "setup_update.log");

        private static void LogSetupUpdate(string message)
        {
            try
            {
                Directory.CreateDirectory(LicenseAcceptanceDirectory);
                File.AppendAllText(SetupUpdateLogFile,
                    $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { /* log e best-effort: nunca atrapalha a atualizacao */ }
        }

        private static readonly string AiLastOpenedFile =
            Path.Combine(LicenseAcceptanceDirectory, "ai.last-opened.txt");

        private bool _licenseAccepted;
        private bool _allowClose;
        private bool _aiOpenedOnFirstAccess;
        private bool _expandedLayout;
        private CancellationTokenSource? _projectPercentRefreshCts;
        private string _baseTitle = "NXProject Community";

        // Título da janela = base + nome do arquivo aberto/salvo (só o nome; o path fica no ToolTip? não — só nome).
        private void UpdateWindowTitle()
        {
            var p = (DataContext as MainViewModel)?.Project;
            var file = p?.FilePath;
            var name = string.IsNullOrWhiteSpace(file) ? null : System.IO.Path.GetFileName(file);
            Title = string.IsNullOrEmpty(name) ? _baseTitle : $"{_baseTitle} — {name}";
            // Reflete o nome do arquivo também no banner (owner + arquivo), que fica visível no app.
            if (p != null) UpdateDevOpsProjectBanner(p.DevOpsProjectName, p.DevOpsRootWorkItemId, p.DevOpsProjectOwner);
        }

        public CommunityMainWindow()
        {
            InitializeComponent();
            // IA Local (LLaMA): registra o gerador usado quando o provedor padrão é LocalLlama.
            Services.ProjectAIAssistantService.LocalGenerator = Community.Services.LocalLlamaService.GenerateAsync;
            // Indicador de IA em execução no canto da toolbar (sinal vindo do Task Plan).
            Community.Services.AiRunIndicator.Changed += running =>
                Dispatcher.Invoke(() =>
                {
                    AiRunningBadge.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
                    // Mostra QUAL IA está rodando (nome do provedor padrão); sem nome, texto genérico.
                    var label = Community.Services.AiRunIndicator.ProviderLabel;
                    AiRunningLabel.Text = running && !string.IsNullOrWhiteSpace(label)
                        ? AppStrings.Get("Main_AiRunningNamed", label)
                        : AppStrings.Get("Main_AiRunning");
                });
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null)
                Title = $"NXProject Community {v.Major}.{v.Minor}.{v.Build} build({v.Revision})";
            _baseTitle = Title;
            ProjectCalendarService.Load("NXProject.Community");
            StatusLogoImage.Source = ProtectedLogoProvider.GetLogoImage();
            var vm = new MainViewModel("NXProject.Community");
            vm.ConfirmCompleteOutsideSprint = ConfirmCompleteOutsideSprint;
            // Título mostra o NOME do arquivo aberto/salvo (só o nome — o path pode ser longo).
            vm.ProjectFileChanged += UpdateWindowTitle;
            DataContext = vm;
            UpdateWindowTitle();

            // Atualiza o banner quando um projeto é aberto/carregado ou FlatTasks muda
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.Project))
                {
                    UpdateWindowTitle();
                    UpdateDevOpsProjectBanner(vm.Project.DevOpsProjectName, vm.Project.DevOpsRootWorkItemId, vm.Project.DevOpsProjectOwner);
                    ScheduleProjectPercentRefresh(vm);
                    vm.Project.ShowCriticalPath = true;
                    ResetScheduleViewport();
                    Dispatcher.InvokeAsync(() => RefreshCriticalPath(vm),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
                else if (args.PropertyName == nameof(vm.CriticalPathRiskSlackDays))
                {
                    Dispatcher.InvokeAsync(() => RefreshCriticalPath(vm),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
                else if (args.PropertyName == nameof(vm.CriticalPathCriticalSlackDays))
                {
                    Dispatcher.InvokeAsync(() => RefreshCriticalPath(vm),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
            };
            vm.FlatTasks.CollectionChanged += (_, _) =>
            {
                ScheduleProjectPercentRefresh(vm);
                UpdateEpicHours(vm);
                RefreshCriticalPath(vm);
            };
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.FlatTasks) || args.PropertyName == "ProjectPercent")
                    UpdateEpicHours(vm);
            };

            LanguageService.LanguageChanged += () =>
            {
                TaskGridCtrl.RefreshColumnHeaders();
                if (DataContext is MainViewModel vm)
                    ZoomLabel.Text = FormatZoomLabel(vm.SelectedZoom);
                GanttCtrl.ForceRender();
            };

            var syncingVerticalScroll = false;

            TaskGridCtrl.VerticalScrollChanged += offset =>
            {
                if (syncingVerticalScroll) return;
                syncingVerticalScroll = true;
                GanttCtrl.SyncVerticalOffset(offset);
                syncingVerticalScroll = false;
            };

            GanttCtrl.VerticalScrollChanged += offset =>
            {
                if (syncingVerticalScroll) return;
                syncingVerticalScroll = true;
                TaskGridCtrl.SyncVerticalOffset(offset);
                syncingVerticalScroll = false;
            };

            TaskGridCtrl.HeaderHeightMeasured += h =>
            {
                // Em modo Dia o cabeçalho do Gantt tem 3 tiers (60px); não deixar TaskGrid sobrescrever.
                if (GanttCtrl.DayHeaderMode > 0)
                    GanttCtrl.SetHeaderHeight(60.0);
                else
                    GanttCtrl.SetHeaderHeight(h);
            };
            TaskGridCtrl.RowTopsMeasured += tops => GanttCtrl.SetRowTops(tops);
            TaskGridCtrl.TaskMoveRequested += (sourceTask, targetTask, insertAfter) =>
            {
                if (vm.MoveTaskByDrop(sourceTask, targetTask, insertAfter))
                    GanttCtrl.ForceRender();
            };

            TaskGridCtrl.TaskIdClicked += OnTaskIdClicked;
            TaskGridCtrl.ViewOnlineChildrenRequested += OnViewOnlineChildren;
            TaskGridCtrl.EditDescriptionRequested += OnEditDescription;
            TaskGridCtrl.ResolveManualConflictRequested += OnResolveManualConflict;
            TaskGridCtrl.FetchTaskHoursRequested += OnFetchTaskHoursFromDevOps;
            TaskGridCtrl.ManualPercentCompleteCommitRequested += OnManualPercentCompleteCommitRequested;
            TaskGridCtrl.FetchChildTasksRequested    += OnFetchChildTasksFromDevOps;
            TaskGridCtrl.TksClickRequested           += OpenTaskReviewForStory;
            TaskGridCtrl.ExpandChildTasksRequested   += OnExpandChildTasks;
            TaskGridCtrl.SuppressChildTasksRequested += OnSuppressChildTasks;
            TaskGridCtrl.ReleaseStoryRequested       += OnReleaseStory;
            TaskGridCtrl.AddDevOpsTaskRequested      += storyVm => { vm.AskSubtaskAction = null; vm.AddSubtask(storyVm, "Task"); };
            TaskGridCtrl.AddInternalTaskRequested    += storyVm => { vm.AskSubtaskAction = null; vm.AddSubtask(storyVm, "NoDevOps"); };
            vm.RequestDevOpsDeleteDialog += task => OnConfirmDeleteTask(task);
            TaskGridCtrl.HighlightPredecessorsRequested += task =>
                GanttCtrl.HighlightPredecessors(task?.Model.PredecessorIds ?? []);
            TaskGridCtrl.EditPercAlocRequested += OnEditPercAloc;
            TaskGridCtrl.EditClassificationRequested += OnEditClassification;
            TaskGridCtrl.ConfigureClassificationRequested += () =>
            {
                new TfsDevOpsConfigWindow("NXProject.Community") { Owner = this }.ShowDialog();
                ApplyClassificationTypesToGrid();
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.ShowOriginalHoursColumn))
                    TaskGridCtrl.ShowOriginalHoursColumn = vm.ShowOriginalHoursColumn;
                if (e.PropertyName == nameof(vm.HiddenColumns) || e.PropertyName == nameof(vm.HiddenColumnsExpanded))
                    TaskGridCtrl.ApplyHiddenColumns(vm.HiddenColumns, vm.HiddenColumnsExpanded, _expandedLayout);
                if (e.PropertyName == nameof(vm.SelectedTask))
                    ViewOnlineChildrenBtn.IsEnabled = vm.SelectedTask?.Model.TfsId is > 0;
            };
            TaskGridCtrl.ShowOriginalHoursColumn = vm.ShowOriginalHoursColumn;
            TaskGridCtrl.ApplyHiddenColumns(vm.HiddenColumns, vm.HiddenColumnsExpanded, _expandedLayout);
            ApplyClassificationTypesToGrid();
            TaskGridCtrl.ColumnSettingsSaved += (hiddenDefault, hiddenExpanded) =>
            {
                vm.HiddenColumns = hiddenDefault;
                vm.HiddenColumnsExpanded = hiddenExpanded;
            };

            vm.PrepareTaskInsertionScroll = TaskGridCtrl.PreserveVerticalOffsetOnNextReset;
            vm.RequestScrollToSelected = () =>
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    () => TaskGridCtrl.ScrollToSelected());

            TaskGridCtrl.TaskSprintChangeRequested += (task, sprint) =>
            {
                vm.ApplyTaskSprintChange(task, sprint, () => TaskGridCtrl.ScrollToSelected());
                GanttCtrl.ForceRender();
            };
            TaskGridCtrl.GanttViewToggled += () =>
            {
                vm.Project.IsDirty = true;
                TaskGridCtrl.RefreshRows();
                GanttCtrl.ForceRender();
            };

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.SelectedTask))
                    GanttCtrl.SelectedTask = vm.SelectedTask;

                if (args.PropertyName == nameof(MainViewModel.SelectedZoom))
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                    {
                        ZoomLabel.Text = FormatZoomLabel(vm.SelectedZoom);
                        GanttCtrl.ZoomLevel = vm.SelectedZoom;
                        GanttCtrl.ForceRender();
                        GanttCtrl.ScrollToProjectStart();
                    });
                }
            };

            ZoomLabel.Text = FormatZoomLabel(vm.SelectedZoom);

            GanttCtrl.TaskClicked += task =>
            {
                vm.SelectedTask = task;
            };

            SubscribeTaskEvents(vm.FlatTasks);
            vm.FlatTasks.CollectionChanged += (_, args) =>
            {
                if (args.OldItems != null)
                {
                    foreach (var item in args.OldItems)
                        if (item is TaskViewModel task)
                            task.PropertyChanged -= OnTaskPropertyChanged;
                }

                if (args.NewItems != null)
                {
                    foreach (var item in args.NewItems)
                        if (item is TaskViewModel task)
                            task.PropertyChanged += OnTaskPropertyChanged;
                }

                if (args.Action == NotifyCollectionChangedAction.Reset)
                    SubscribeTaskEvents(vm.FlatTasks);

                if (args.Action == NotifyCollectionChangedAction.Add ||
                    args.Action == NotifyCollectionChangedAction.Remove ||
                    args.Action == NotifyCollectionChangedAction.Reset)
                {
                    RefreshCriticalPath(vm);
                    GanttCtrl.ForceRender();
                    TaskGridCtrl.FocusSelectedTask();
                }
            };

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.Project))
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        () => GanttCtrl.ScrollToProjectStart());
            };

            Loaded += OnCommunityWindowLoaded;
            ContentRendered += (_, _) => BringMainWindowToFront();
            Closing += OnCommunityWindowClosing;
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.F &&
                    (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
                {
                    OpenSearchPopup();
                    e.Handled = true;
                }
            };
            ApplyLayoutMode(expanded: false);
            RestoreWindowState();
        }

        private sealed class WinState { public bool Maximized { get; set; } public double Left { get; set; } public double Top { get; set; } public double Width { get; set; } public double Height { get; set; } }

        private static string WindowStateFile => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXProject.Community", "windowstate.json");

        // Restaura posição/tamanho e o estado maximizado da última sessão.
        private void RestoreWindowState()
        {
            try
            {
                if (!System.IO.File.Exists(WindowStateFile)) return;
                var s = System.Text.Json.JsonSerializer.Deserialize<WinState>(System.IO.File.ReadAllText(WindowStateFile));
                if (s == null) return;
                if (s.Width > 200 && s.Height > 200)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = s.Left; Top = s.Top; Width = s.Width; Height = s.Height;
                }
                // Maximizar no construtor costuma ser ignorado; aplica em SourceInitialized.
                _restoreMaximized = s.Maximized;
            }
            catch { }
        }
        private bool _restoreMaximized;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (_restoreMaximized) WindowState = WindowState.Maximized;
        }

        // Grava o estado atual (usa RestoreBounds para preservar o tamanho "normal" mesmo maximizado).
        private void SaveWindowState()
        {
            try
            {
                var b = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
                var s = new WinState { Maximized = WindowState == WindowState.Maximized,
                    Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height };
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(WindowStateFile)!);
                System.IO.File.WriteAllText(WindowStateFile, System.Text.Json.JsonSerializer.Serialize(s));
            }
            catch { }
        }

        /// <summary>Abre a janela principal já com um cronograma carregado de arquivo
        /// (usado pelo chat de IA para exibir o cronograma sugerido numa janela nova).</summary>
        public CommunityMainWindow(string initialProjectPath) : this()
        {
            if (!string.IsNullOrWhiteSpace(initialProjectPath) && System.IO.File.Exists(initialProjectPath)
                && DataContext is MainViewModel vm)
            {
                try { vm.LoadProjectFromPath(initialProjectPath); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Não foi possível abrir o cronograma sugerido:\n" + ex.Message,
                        "NXProject", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        // Marca quando a janela já foi fechada, para não tentar Show()/Activate() num callback
        // atrasado (BeginInvoke) que rode depois do fechamento — isso lançava
        // "Não será possível definir Visibility nem chamar Show ... depois que uma Janela for fechada".
        private bool _isClosed;

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            base.OnClosed(e);
        }

        private void BringMainWindowToFront()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                // A janela pode ter sido fechada antes deste callback ocioso rodar.
                if (_isClosed || !IsLoaded) return;

                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;

                Show();
                Activate();
                Focus();

                // Após atualização, algumas máquinas deixam o processo novo atrás/minimizado.
                // O pulso de Topmost força a janela a reaparecer sem deixá-la sempre no topo.
                var wasTopmost = Topmost;
                Topmost = true;
                Topmost = wasTopmost;
                Activate();
            });
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            var about = new CommunityAboutWindow
            {
                Owner = this
            };
            about.ShowDialog();
        }

        private void OnLicenseClick(object sender, RoutedEventArgs e)
        {
            ShowLicenseDialog(requireAcceptance: false);
        }

        private void OnAzureDevOpsBacklogHelpClick(object sender, RoutedEventArgs e)
        {
            new AzureDevOpsBacklogHelpWindow
            {
                Owner = this
            }.ShowDialog();
        }

        private void OnFeaturesHelpClick(object sender, RoutedEventArgs e)
        {
            new FeaturesHelpWindow { Owner = this }.ShowDialog();
        }

        private void OnLanguageClick(object sender, RoutedEventArgs e)
        {
            new LanguageWindow { Owner = this }.ShowDialog();
        }

        private void OnAppSettingsClick(object sender, RoutedEventArgs e)
        {
            new AppSettingsWindow { Owner = this }.ShowDialog();
        }

        private void OnScheduleUsageHelpClick(object sender, RoutedEventArgs e)
        {
            new ScheduleUsageHelpWindow
            {
                Owner = this
            }.ShowDialog();
        }

        private void OnAddMilestoneToolbarClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var asChild = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            vm.AddMilestone(asChild);
        }

        private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
        {
            try
            {
                IsEnabled = false;
                var setupUpdate = await NXProject.Services.UpdateService.CheckForSetupUpdateAsync();
                if (setupUpdate is not null)
                {
                    IsEnabled = true;
                    var proceedSetup = ShowSetupUpdateDialog();

                    LogSetupUpdate($"Atualizacao de base oferecida (tag {setupUpdate.TagName}, timestamp remoto {setupUpdate.UpdatedAt:o}); usuario continuou: {proceedSetup}.");

                    if (proceedSetup)
                    {
                        IsEnabled = false;
                        await DownloadAndLaunchSetupAsync(setupUpdate.DownloadUrl);
                        return;
                    }
                }

                var release = await NXProject.Services.UpdateService.CheckForUpdateAsync();
                IsEnabled = true;

                if (release is null)
                {
                    MessageBox.Show(
                        "Voce ja esta usando a versao mais recente.",
                        "Verificar Atualizacao",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var choice = ShowUpdateDialog(release.TagName);

                if (choice == UpdateChoice.Auto)
                {
                    var progressWindow = new UpdateProgressWindow(release.DownloadUrl) { Owner = this };
                    progressWindow.ShowDialog();
                }
                else if (choice == UpdateChoice.Manual)
                {
                    ShowDownloadLinkDialog(release.HtmlUrl, release.DownloadUrl, release.TagName);
                }
            }
            catch (Exception ex)
            {
                IsEnabled = true;
                var hint = ex.Message.Contains("502") || ex.Message.Contains("gateway", StringComparison.OrdinalIgnoreCase)
                    ? "\n\nDica: verifique se ha proxy ou firewall bloqueando acesso a api.github.com."
                    : string.Empty;
                MessageBox.Show(
                    $"Nao foi possivel verificar atualizacoes.\n\n{ex.Message}{hint}",
                    "Verificar Atualizacao",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private async Task DownloadAndLaunchSetupAsync(string downloadUrl)
        {
            var downloadsDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var zipPath = System.IO.Path.Combine(downloadsDir, "NXProject-Setup.zip");

            // Progresso do download: o pacote base tem dezenas de MB (cresceu com as
            // bibliotecas novas) e sem barra a espera parece travamento.
            var (progressWin, progressBar, progressText) = CreateSetupProgressWindow();
            progressWin.Show();
            try
            {
                // Baixa o .zip para Downloads (local fixo e visivel) — se a instalacao
                // falhar, o usuario acha o pacote e roda de novo sem precisar baixar outra vez.
                LogSetupUpdate($"Baixando {downloadUrl} para {zipPath}...");
                var percent = new Progress<int>(p => progressBar.Value = p);
                var bytes = new Progress<(long Downloaded, long Total)>(b =>
                    progressText.Text = b.Total > 0
                        ? $"{b.Downloaded / 1024d / 1024d:0.0} MB de {b.Total / 1024d / 1024d:0.0} MB ({b.Downloaded * 100 / b.Total}%)"
                        : $"{b.Downloaded / 1024d / 1024d:0.0} MB");
                await NXProject.Services.UpdateService.DownloadFileAsync(
                    downloadUrl, zipPath, percent, default, bytes);
                LogSetupUpdate($"Download concluido: {new System.IO.FileInfo(zipPath).Length / (1024 * 1024)} MB.");
                progressBar.IsIndeterminate = true;
                progressText.Text = "Extraindo o pacote e iniciando o NXProject-Setup...";

                // Antivirus (ex.: McAfee) costuma segurar o zip recem-baixado por alguns
                // segundos: tenta extrair com novas tentativas antes de desistir.
                var tempExtractDir = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), $"nxsetupupdate_{Guid.NewGuid():N}");
                const int maxTries = 5;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, tempExtractDir);
                        LogSetupUpdate($"Extraido em {tempExtractDir} (tentativa {attempt}).");
                        break;
                    }
                    catch (System.IO.IOException ioEx) when (attempt < maxTries)
                    {
                        LogSetupUpdate($"Falha ao extrair (tentativa {attempt}/{maxTries}): {ioEx.Message} — nova tentativa em 1,5s.");
                        await System.Threading.Tasks.Task.Delay(1500);
                    }
                }

                var setupExePath = System.IO.Path.Combine(tempExtractDir, "NXProject-Setup.exe");
                if (!System.IO.File.Exists(setupExePath))
                {
                    IsEnabled = true;
                    progressWin.Close();
                    LogSetupUpdate($"ERRO: NXProject-Setup.exe nao encontrado em {tempExtractDir}.");
                    TryOpenExplorerSelectingFile(zipPath);
                    MessageBox.Show(this,
                        $"Nao foi possivel encontrar o NXProject-Setup.exe no pacote baixado.\n\nO arquivo baixado foi mantido em: {zipPath} (pasta aberta ao lado). Extraia o zip e rode o NXProject-Setup.exe manualmente, com o NXProject fechado.\n\nLog: {SetupUpdateLogFile}",
                        "Atualizacao de base necessaria",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                LogSetupUpdate($"Iniciando {setupExePath}...");
                var setupProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setupExePath)
                {
                    WorkingDirectory = tempExtractDir,
                    UseShellExecute = true
                });
                if (setupProcess == null)
                    throw new InvalidOperationException(
                        "O Windows nao iniciou o NXProject-Setup.exe (possivel bloqueio de politica de seguranca ou antivirus).");
                LogSetupUpdate($"Setup iniciado (PID {setupProcess.Id}). Encerrando o NXProject.");
                progressWin.Close();

                // Chegou ate aqui com sucesso: apaga o zip baixado, nao precisa mais dele.
                try { System.IO.File.Delete(zipPath); } catch { /* best-effort */ }

                // Ja avisamos no dialogo anterior para salvar antes de continuar — nao
                // pergunta de novo aqui, senao o Setup tenta instalar por cima enquanto
                // este processo ainda esta travado nessa pergunta.
                _allowClose = true;
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                IsEnabled = true;
                progressWin.Close();
                LogSetupUpdate("ERRO: " + ex);
                // Fallback: abre a pasta Downloads com o zip selecionado para rodar manualmente.
                var zipKept = System.IO.File.Exists(zipPath);
                if (zipKept) TryOpenExplorerSelectingFile(zipPath);
                var zipHint = zipKept
                    ? $"\n\nO arquivo foi mantido em: {zipPath} (pasta aberta ao lado). Extraia o zip e rode o NXProject-Setup.exe manualmente, com o NXProject fechado."
                    : "";
                // Owner = this: sem ele, a mensagem podia ficar ESCONDIDA atras da janela
                // desabilitada — o usuario via "nada acontecer".
                MessageBox.Show(this,
                    $"Falha ao baixar/abrir o NXProject-Setup.\n\n{ex.Message}{zipHint}\n\nLog (envie ao suporte): {SetupUpdateLogFile}",
                    "Atualizacao de base necessaria",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Aviso da atualizacao de base: informa o nome/local do log e deixa o usuario ABRIR
        /// A PASTA DO LOG antes de continuar (facilita enviar o arquivo ao suporte se falhar).
        /// Devolve true quando o usuario opta por continuar.
        /// </summary>
        private bool ShowSetupUpdateDialog()
        {
            var dlg = new Window
            {
                Title = "Atualizacao de base necessaria",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                Width = 560,
                Background = System.Windows.Media.Brushes.White
            };

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = "Uma nova versão base do NXProject foi publicada (por exemplo, uma biblioteca nova) "
                     + "e requer reinstalação pelo NXProject-Setup.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "IMPORTANTE: salve o projeto aberto antes de continuar — este aplicativo será "
                     + "encerrado automaticamente, sem perguntar se deseja salvar.",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(150, 60, 60)),
                Margin = new Thickness(0, 0, 0, 10)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "O NXProject-Setup.zip será baixado para a pasta Downloads e aberto em seguida "
                     + "(se algo falhar, o arquivo fica em Downloads para tentar de novo).",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var logBox = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(245, 247, 250)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(208, 215, 224)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 14)
            };
            var logPanel = new StackPanel();
            logPanel.Children.Add(new TextBlock
            {
                Text = "Cada passo é registrado no arquivo de log:",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            logPanel.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(SetupUpdateLogFile),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 2)
            });
            logPanel.Children.Add(new TextBlock
            {
                Text = "Pasta: " + LicenseAcceptanceDirectory,
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(90, 100, 115)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            var openFolderBtn = new Button
            {
                Content = "Abrir pasta do log",
                Width = 150,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            openFolderBtn.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(LicenseAcceptanceDirectory);
                    if (File.Exists(SetupUpdateLogFile))
                        TryOpenExplorerSelectingFile(SetupUpdateLogFile);
                    else
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                            LicenseAcceptanceDirectory) { UseShellExecute = true });
                }
                catch { /* best-effort */ }
            };
            logPanel.Children.Add(openFolderBtn);
            logPanel.Children.Add(new TextBlock
            {
                Text = "Se a atualização falhar, envie esse arquivo para o suporte.",
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(90, 100, 115)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
            logBox.Child = logPanel;
            panel.Children.Add(logBox);

            var proceed = false;
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var okBtn = new Button { Content = "Continuar", Width = 110, Height = 30, IsDefault = true };
            var cancelBtn = new Button { Content = "Cancelar", Width = 110, Height = 30, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            okBtn.Click += (_, _) => { proceed = true; dlg.Close(); };
            cancelBtn.Click += (_, _) => dlg.Close();
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);
            panel.Children.Add(buttons);

            dlg.Content = panel;
            dlg.ShowDialog();
            return proceed;
        }

        /// <summary>Janela de progresso do download do NXProject-Setup.zip (dezenas de MB).</summary>
        private (Window Win, System.Windows.Controls.ProgressBar Bar, TextBlock Text) CreateSetupProgressWindow()
        {
            var bar = new System.Windows.Controls.ProgressBar
            {
                Height = 14, Minimum = 0, Maximum = 100, Value = 0,
                Margin = new Thickness(0, 0, 0, 6)
            };
            var text = new TextBlock
            {
                Text = "Iniciando o download...",
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(85, 85, 85))
            };

            var panel = new StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock
            {
                Text = "Baixando o NXProject-Setup...",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(43, 87, 154)),
                Margin = new Thickness(0, 0, 0, 10)
            });
            panel.Children.Add(bar);
            panel.Children.Add(text);

            var win = new Window
            {
                Title = "Atualizacao de base",
                Owner = this,
                Width = 430,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.White,
                Content = panel
            };
            return (win, bar, text);
        }

        /// <summary>Abre o Explorer com o arquivo selecionado (fallback da atualizacao do Setup).</summary>
        private static void TryOpenExplorerSelectingFile(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"/select,\"{path}\"")
                { UseShellExecute = true });
            }
            catch { /* best-effort */ }
        }

        private enum UpdateChoice { Auto, Manual, Cancel }

        private UpdateChoice ShowUpdateDialog(string tagName)
        {
            var dlg = new System.Windows.Window
            {
                Title = "Atualização disponível",
                Owner = this,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Width = 360,
                Height = 260,
                Background = System.Windows.Media.Brushes.White
            };

            var result = UpdateChoice.Cancel;

            var root = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(24, 20, 24, 20) };

            root.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"Nova versão disponível: {tagName}",
                FontSize = 13,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 87, 154)),
                Margin = new System.Windows.Thickness(0, 0, 0, 8)
            });
            root.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Como deseja atualizar?",
                Margin = new System.Windows.Thickness(0, 0, 0, 14),
                Foreground = System.Windows.Media.Brushes.DimGray
            });

            var btnAuto = new System.Windows.Controls.Button { Content = "⬇  Atualizar automaticamente", Height = 32, IsDefault = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, Margin = new System.Windows.Thickness(0, 0, 0, 6) };
            var btnManual = new System.Windows.Controls.Button { Content = "🌐  Baixar manualmente", Height = 32, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, Margin = new System.Windows.Thickness(0, 0, 0, 6) };
            var btnCancel = new System.Windows.Controls.Button { Content = "Agora não", Height = 32, IsCancel = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };

            btnAuto.Click   += (_, _) => { result = UpdateChoice.Auto;   dlg.DialogResult = true; dlg.Close(); };
            btnManual.Click += (_, _) => { result = UpdateChoice.Manual; dlg.DialogResult = true; dlg.Close(); };
            btnCancel.Click += (_, _) => { dlg.Close(); };

            root.Children.Add(btnAuto);
            root.Children.Add(btnManual);
            root.Children.Add(btnCancel);

            dlg.Content = root;
            dlg.ShowDialog();
            return result;
        }

        private void ShowDownloadLinkDialog(string htmlUrl, string downloadUrl, string tagName)
        {
            var dlg = new System.Windows.Window
            {
                Title = $"Download — {tagName}",
                Owner = this,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Width = 500,
                Height = 220,
                Background = System.Windows.Media.Brushes.White
            };

            var root = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(20) };
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            void AddRow(int row, string label, string url)
            {
                var lbl = new System.Windows.Controls.TextBlock
                {
                    Text = label,
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Margin = new System.Windows.Thickness(0, row == 0 ? 0 : 12, 0, 4)
                };
                System.Windows.Controls.Grid.SetRow(lbl, row * 2);
                root.Children.Add(lbl);

                var panel = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal
                };
                var box = new System.Windows.Controls.TextBox
                {
                    Text = url,
                    IsReadOnly = true,
                    Width = 360,
                    Margin = new System.Windows.Thickness(0, 0, 6, 0),
                    VerticalContentAlignment = System.Windows.VerticalAlignment.Center
                };
                var btnCopy = new System.Windows.Controls.Button { Content = "Copiar", Width = 70 };
                btnCopy.Click += (_, _) =>
                {
                    System.Windows.Clipboard.SetText(url);
                    btnCopy.Content = "Copiado!";
                };
                panel.Children.Add(box);
                panel.Children.Add(btnCopy);
                System.Windows.Controls.Grid.SetRow(panel, row * 2 + 1);
                root.Children.Add(panel);
            }

            // Adiciona linhas extras no Grid para os dois grupos
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            AddRow(0, "Pagina do release:", htmlUrl);
            AddRow(1, "Link direto do ZIP:", downloadUrl);

            dlg.Content = root;
            dlg.ShowDialog();
        }

        // Toolbar: chat de análise do cronograma com IA (conversa aberta por padrão).
        private void OnAiChatClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            // Uma única janela de chat: se já está aberta, só volta o foco para ela.
            var open = Application.Current.Windows.OfType<CommunityAIChatWindow>().FirstOrDefault();
            if (open != null)
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return;
            }
            new CommunityAIChatWindow(vm) { Owner = this }.Show();
        }

        private void OnAiAssistantClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var aiWindow = new CommunityAIWindow(vm)
            {
                Owner = this
            };
            aiWindow.ShowDialog();
        }

        // IA → Gerenciar IA Local: pasta dos recursos, status, download (botão Baixar) e validação.
        private void OnLocalAiManageClick(object sender, RoutedEventArgs e)
            => new LocalAIManagerWindow { Owner = this }.ShowDialog();

        private void OnOpenSelectedTaskInDevOpsClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            if (vm.SelectedTask?.Model is { } m)
                OpenTaskInDevOps(m);
        }

        // Abre a janela de Consultas (Shared Queries do DevOps executadas dentro do NX).
        private void OnTfsQueryClick(object sender, RoutedEventArgs e)
        {
            var conn = NXProject.Services.TfsConnectionStore.Load("NXProject.Community");
            if (string.IsNullOrWhiteSpace(conn.OrganizationUrl) || string.IsNullOrWhiteSpace(conn.TeamProject)
                || string.IsNullOrWhiteSpace(conn.PersonalAccessToken))
            {
                MessageBox.Show(this, AppStrings.Get("Query_LoadError",
                        AppStrings.Get("Menu_View_DevOpsConfig")),
                    "NXProject", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // Passa os TfsIds do cronograma aberto + a ação de focar a task, para que a lista
            // da query mostre o botão "ver no cronograma" nas linhas que já estão no plano.
            var scheduleIds = (DataContext as MainViewModel)?.FlatTasks
                .Where(t => t.Model.TfsId is > 0)
                .Select(t => t.Model.TfsId!.Value)
                .ToHashSet() ?? new System.Collections.Generic.HashSet<int>();

            new NXProject.Views.TfsQueryWindow(scheduleIds, FocusScheduleTaskByTfsId) { Owner = this }.Show();
        }

        // Abre a visão de Backlog (Epic→Feature→Story→Task ordenado pela prioridade do DevOps).
        private void OnTfsBacklogClick(object sender, RoutedEventArgs e)
        {
            var conn = NXProject.Services.TfsConnectionStore.Load("NXProject.Community");
            if (string.IsNullOrWhiteSpace(conn.OrganizationUrl) || string.IsNullOrWhiteSpace(conn.TeamProject)
                || string.IsNullOrWhiteSpace(conn.PersonalAccessToken))
            {
                MessageBox.Show(this, AppStrings.Get("Query_LoadError", AppStrings.Get("Menu_View_DevOpsConfig")),
                    "NXProject", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // Raiz: o root do cronograma aberto (se veio do DevOps); senão o ID raiz configurado.
            var rootId = (DataContext as MainViewModel)?.Project?.DevOpsRootWorkItemId ?? 0;
            if (rootId <= 0) rootId = conn.RootWorkItemId;
            if (rootId <= 0)
            {
                MessageBox.Show(this, AppStrings.Get("Backlog_NoRoot"),
                    "NXProject", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var scheduleIds = (DataContext as MainViewModel)?.FlatTasks
                .Where(t => t.Model.TfsId is > 0)
                .Select(t => t.Model.TfsId!.Value)
                .ToHashSet() ?? new System.Collections.Generic.HashSet<int>();

            new NXProject.Views.TfsBacklogWindow(rootId, scheduleIds, FocusScheduleTaskByTfsId) { Owner = this }.Show();
        }

        // Abre a visão de Sprint (Taskboard: cards por estado, filtro por pessoa e cronograma).
        private void OnTfsSprintClick(object sender, RoutedEventArgs e) => OpenTaskBoard(silent: false);

        // Abre o TaskBoard do NX. silent=true (auto-abertura na inicialização) não mostra
        // aviso caso o DevOps ainda não esteja configurado — apenas não abre.
        private void OpenTaskBoard(bool silent)
        {
            var conn = NXProject.Services.TfsConnectionStore.Load("NXProject.Community");
            if (string.IsNullOrWhiteSpace(conn.OrganizationUrl) || string.IsNullOrWhiteSpace(conn.TeamProject)
                || string.IsNullOrWhiteSpace(conn.PersonalAccessToken))
            {
                if (!silent)
                    MessageBox.Show(this, AppStrings.Get("Query_LoadError", AppStrings.Get("Menu_View_DevOpsConfig")),
                        "NXProject", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var vm = DataContext as MainViewModel;
            // FlatTasks já está na ordem da árvore do cronograma: leva a LISTA (ordem) além do
            // conjunto, para o filtro do TaskBoard seguir a mesma ordem que o usuário vê aqui.
            var scheduleOrder = vm?.FlatTasks
                .Where(t => t.Model.TfsId is > 0)
                .Select(t => t.Model.TfsId!.Value)
                .ToList() ?? new System.Collections.Generic.List<int>();
            var scheduleIds = scheduleOrder.ToHashSet();
            // Sprint sugerida: a mais frequente entre as atividades do cronograma aberto.
            var preferred = vm?.FlatTasks
                .Where(t => !string.IsNullOrWhiteSpace(t.Model.TfsIterationPath))
                .GroupBy(t => t.Model.TfsIterationPath!)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

            new NXProject.Views.TfsSprintWindow(scheduleIds, FocusScheduleTaskByTfsId, preferred, scheduleOrder)
                { Owner = this }.Show();
        }

        // Foca (seleciona + rola até) a atividade do cronograma com o TfsId dado.
        private void FocusScheduleTaskByTfsId(int tfsId)
        {
            if (DataContext is not MainViewModel vm) return;
            var target = vm.FlatTasks.FirstOrDefault(t => t.Model.TfsId == tfsId);
            if (target == null) return;
            Activate();
            vm.SelectedTask = target;
            vm.RequestScrollToSelected?.Invoke();
        }

        private void OpenTaskInDevOps(NXProject.Models.ProjectTask task)
        {
            if (task.TfsId is not > 0) return;
            try
            {
                var conn = NXProject.Services.TfsConnectionStore.Load();
                if (string.IsNullOrWhiteSpace(conn.OrganizationUrl) || string.IsNullOrWhiteSpace(conn.TeamProject)) return;
                var url = $"{conn.OrganizationUrl.TrimEnd('/')}/{Uri.EscapeDataString(conn.TeamProject.Trim())}/_workitems/edit/{task.TfsId}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        private void OnTaskIdClicked(TaskViewModel task)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var dialog = new TfsWorkItemEditWindow(task) { Owner = this };
            if (dialog.ShowDialog() == true)
                vm.Project.IsDirty = true;
            else if (dialog.ShouldDelete)
                vm.DeleteTaskViewModel(task);
            else if (dialog.ShouldImport)
                OpenTfsImport();
        }

        private void OnViewOnlineChildrenToolbarClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            if (vm.SelectedTask != null)
                OnViewOnlineChildren(vm.SelectedTask);
        }

        private void OnViewOnlineChildren(TaskViewModel task)
        {
            if (DataContext is not MainViewModel vm) return;
            if (task.Model.TfsId is not > 0) return;

            var win = new TfsOnlineChildTasksWindow(task.Model, vm) { Owner = this };
            win.ShowDialog();
            if (win.HasChanges)
                GanttCtrl.ForceRender();
        }

        private void OnEditDescription(TaskViewModel task)
        {
            if (DataContext is not MainViewModel vm) return;
            // Critérios de Aceitação é campo só da Story (Feature, Epic e Task não têm).
            var acOk = Services.TfsImportService.IsStoryTypePublic(task.Model.TfsType);
            var win = new TaskDescriptionEditWindow(task.Model,
                enableAcceptance: acOk, acceptanceHtml: task.Model.AcceptanceCriteria) { Owner = this };
            if (win.ShowDialog() == true)
            {
                if (win.AcceptanceChanged) task.Model.AcceptanceCriteria = win.AcceptanceHtml;
                vm.Project.IsDirty = true;
            }
        }

        private async void OnResolveManualConflict(TaskViewModel task)
        {
            if (DataContext is not MainViewModel vm) return;
            if (task.Model.TfsId is not > 0) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                vm.StatusMessage = AppStrings.Get("Conf_LoadingManual");

                var options = Services.TfsConnectionStore.Load("NXProject.Community");
                var conflict = await Services.TfsImportService.LoadManualConflictItemAsync(task.Model, options);

                Mouse.OverrideCursor = null;
                var resolved = new TfsSyncConflictWindow([conflict], vm.Project, options)
                {
                    Owner = this
                }.ShowDialog() == true;

                if (resolved)
                {
                    task.Model.HasSyncConflict = false;
                    vm.Project.IsDirty = true;
                    TaskGridCtrl.RefreshRows();
                    GanttCtrl.ForceRender();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    AppStrings.Get("Conf_ManualError", ex.Message),
                    AppStrings.Get("Conf_Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private async void OnFetchTaskHoursFromDevOps(TaskViewModel task)
        {
            if (DataContext is not MainViewModel vm) return;
            if (task.Model.TfsId is not > 0)
            {
                MessageBox.Show("Esta atividade não está vinculada ao DevOps.", "Sem vínculo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var options = Services.TfsConnectionStore.Load("NXProject.Community");
                var result = await Services.TfsImportService.FetchChildTaskHoursAsync(options, task.Model.TfsId!.Value);
                if (result == null)
                {
                    MessageBox.Show("Não foi possível obter os dados das Tasks no DevOps.", "Erro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (result.TaskCount == 0)
                {
                    MessageBox.Show("Nenhuma Task filha encontrada no DevOps.", "Sem Tasks", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Avisa e bloqueia se houver Tasks sem duração
                if (result.TasksWithoutHours.Count > 0)
                {
                    var taskList = string.Join("\n  • ", result.TasksWithoutHours);
                    MessageBox.Show(
                        $"As seguintes Tasks não possuem horas estimadas (Original Estimate = 0 ou vazio):\n\n  • {taskList}\n\nCorrija as horas no DevOps antes de atualizar a duração.",
                        "Tasks sem duração", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var msg = $"Tasks filhas encontradas: {result.TaskCount}\nSoma dos HH Estimados: {result.TotalHours:0.#}h\n\nDeseja atualizar as horas estimadas desta atividade?";
                if (MessageBox.Show(msg, "Atualizar duração", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    task.Model.EstimatedHours = result.TotalHours;
                    vm.Project.IsDirty = true;
                    vm.RebuildFlatTasks();
                    GanttCtrl.ForceRender();
                }
            }
            catch (Exception ex)
            {
                TfsErrorDialog.Show(this, AppStrings.Get("Tfs_ActionLoadTasks"), ex);
            }
        }

        private void OnFetchChildTasksFromDevOps(TaskViewModel storyVm) =>
            OpenTaskReviewForStory(storyVm);

        private async System.Threading.Tasks.Task<bool> OnManualPercentCompleteCommitRequested(TaskViewModel storyVm, double requestedPercent)
        {
            var savedOptions = Services.TfsConnectionStore.Load("NXProject.Community");
            if (!Services.TfsImportService.ShouldBlockManualStoryCompletionWithoutDevOpsTasks(
                    storyVm.Model,
                    requestedPercent,
                    savedOptions.EnforceStoryCompletionWithTasks))
                return true;

            if (ShowManualStoryCompletionBlockedDialog(storyVm) != ManualStoryCompletionChoice.RecountDevOpsTasks)
                return false;

            try
            {
                var tasks = await Services.TfsImportService.FetchChildTasksFromDevOpsAsync(savedOptions, storyVm.Model.TfsId!.Value);
                if (tasks == null)
                {
                    MessageBox.Show(
                        AppStrings.Get("Pct100_RecountError", AppStrings.Get("Pct100_RecountNoResponse")),
                        AppStrings.Get("Pct100_Title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                storyVm.Model.DevopsTaskCount = tasks.Count;
                storyVm.NotifyTksChanged();

                if (tasks.Count > 0)
                {
                    MessageBox.Show(
                        AppStrings.Get("Pct100_RecountFound", tasks.Count),
                        AppStrings.Get("Pct100_Title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return true;
                }

                MessageBox.Show(
                    AppStrings.Get("Pct100_RecountNone"),
                    AppStrings.Get("Pct100_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    AppStrings.Get("Pct100_RecountError", ex.Message),
                    AppStrings.Get("Pct100_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private enum ManualStoryCompletionChoice
        {
            Cancel,
            RecountDevOpsTasks
        }

        private ManualStoryCompletionChoice ShowManualStoryCompletionBlockedDialog(TaskViewModel storyVm)
        {
            var dialog = new Window
            {
                Title = AppStrings.Get("Pct100_Title"),
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                MinWidth = 430,
                Background = System.Windows.Media.Brushes.White,
                ShowInTaskbar = false
            };

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new TextBlock
            {
                Text = AppStrings.Get("Pct100_BlockMessage", storyVm.DisplayId, storyVm.Name),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
                Margin = new Thickness(0, 0, 0, 18)
            };
            Grid.SetRow(text, 0);
            root.Children.Add(text);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(buttons, 1);

            var recount = new Button
            {
                Content = AppStrings.Get("Pct100_RecountButton"),
                MinWidth = 180,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            recount.Click += (_, _) =>
            {
                dialog.Tag = ManualStoryCompletionChoice.RecountDevOpsTasks;
                dialog.DialogResult = true;
            };
            buttons.Children.Add(recount);

            var cancel = new Button
            {
                Content = AppStrings.Get("Pct100_CancelButton"),
                MinWidth = 90,
                Height = 32,
                IsCancel = true
            };
            cancel.Click += (_, _) =>
            {
                dialog.Tag = ManualStoryCompletionChoice.Cancel;
                dialog.DialogResult = false;
            };
            buttons.Children.Add(cancel);

            root.Children.Add(buttons);
            dialog.Content = root;
            return dialog.ShowDialog() == true && dialog.Tag is ManualStoryCompletionChoice choice
                ? choice
                : ManualStoryCompletionChoice.Cancel;
        }

        private async void OnExpandChildTasks(TaskViewModel storyVm)
        {
            if (DataContext is not MainViewModel vm) return;
            var story = storyVm.Model;
            story.TasksSuppressed = false;

            // Se não há tasks em memória, busca do DevOps e adiciona ao cronograma
            bool hasTasks = story.Children.Any(c =>
                string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase));
            if (!hasTasks && story.TfsId is > 0)
            {
                var options = Services.TfsConnectionStore.Load("NXProject.Community");
                var tasks = await Services.TfsImportService.FetchChildTasksFromDevOpsAsync(options, story.TfsId!.Value);
                if (tasks != null && tasks.Count > 0)
                {
                    var rows = tasks.Select(t => new Views.TaskReviewRow
                    {
                        StoryTask       = story,
                        TaskId          = t.TfsId,
                        Title           = t.Title,
                        Description     = t.Description ?? "",
                        State           = t.State ?? "New",
                        EstimatedHours  = t.EstimatedHours,
                        CompletedHours  = t.CompletedHours,
                        PercentComplete = t.PercentComplete,
                        Priority        = t.Priority,
                        AssignedTo        = t.AssignedTo ?? "",
                        AssignedToDisplay = t.AssignedToDisplay ?? t.AssignedTo ?? "",
                    });
                    var first = AddTaskRowsToSchedule(rows, story, vm);
                    if (first != null)
                        SelectTaskInSchedule(first, vm);
                    return;
                }
            }

            vm.Project.IsDirty = true;
            vm.RebuildFlatTasks();
            GanttCtrl.ForceRender();

            // Seleciona a primeira task do cronograma desta story
            var firstTask = vm.FlatTasks.FirstOrDefault(t =>
                t.Model.Parent == story &&
                string.Equals(t.Model.TfsType, "Task", StringComparison.OrdinalIgnoreCase));
            if (firstTask != null)
                SelectTaskInSchedule(firstTask.Model, vm);
        }

        private void SelectTaskInSchedule(NXProject.Models.ProjectTask task, MainViewModel vm)
        {
            var tvm = vm.FlatTasks.FirstOrDefault(t => t.Model == task);
            if (tvm != null)
            {
                vm.SelectedTask = tvm;
                TaskGridCtrl.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => TaskGridCtrl.ScrollToSelected());
            }
        }

        private void OpenTaskReviewForStory(TaskViewModel storyVm)
        {
            if (DataContext is not MainViewModel vm) return;
            if (storyVm.Model.TfsId is not > 0) return;

            var cfg = Services.TfsConnectionStore.Load("NXProject.Community");
            var dlg = new TechLeadTaskReviewWindow(vm.Project, [storyVm.Model], cfg.TaskActivityList)
            {
                Owner = this,
                AddToScheduleCallback = rows =>
                {
                    var first = AddTaskRowsToSchedule(rows, storyVm.Model, vm);
                    if (first != null) SelectTaskInSchedule(first, vm);
                    return first;
                },
                ReleaseCallback       = () => ReleaseStoryTasks(storyVm.Model, vm)
            };
            dlg.ShowDialog();

            if (dlg.HasChanges)
            {
                SyncExpandedTaskRowsToSchedule(dlg.CurrentRows, storyVm.Model, vm);
                vm.Project.IsDirty = true;
                vm.RebuildFlatTasks();
                GanttCtrl.ForceRender();
            }
        }

        /// <summary>
        /// Foca a janela principal e seleciona/mostra a atividade no cronograma
        /// (usado pelo Task Plan em "Ver no cronograma").
        /// </summary>
        public void FocusTaskInSchedule(NXProject.Models.ProjectTask task)
        {
            if (DataContext is not MainViewModel vm) return;
            Activate();
            SelectTaskInSchedule(task, vm);
        }

        /// <summary>
        /// Adiciona Tasks do DevOps sob uma Story usando a mesma rotina da grid de Tasks
        /// (usado pelo Task Plan ao sincronizar com o cronograma).
        /// </summary>
        public NXProject.Models.ProjectTask? AddDevOpsTasksToStory(
            IEnumerable<TaskReviewRow> rows, NXProject.Models.ProjectTask story)
        {
            return DataContext is MainViewModel vm ? AddTaskRowsToSchedule(rows, story, vm) : null;
        }

        private NXProject.Models.ProjectTask? AddTaskRowsToSchedule(
            IEnumerable<TaskReviewRow> rows,
            NXProject.Models.ProjectTask story,
            MainViewModel vm,
            bool refreshAfterAdd = true)
        {
            var existingIds = story.Children
                .Where(c => string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase) && c.TfsId.HasValue)
                .Select(c => c.TfsId!.Value).ToHashSet();

            NXProject.Models.ProjectTask? firstAdded = null;
            foreach (var r in rows)
            {
                if (existingIds.Contains(r.TaskId)) continue;
                var (curH, estH) = Services.TfsImportService.ResolveTaskScheduleHours(
                    r.EstimatedHours, r.CompletedHours, r.PercentComplete);
                var pt = new NXProject.Models.ProjectTask
                {
                    // Contador central do projeto — FlatTasks pode estar desatualizado
                    // (ex.: tasks internas recém-criadas pelo Task Plan) e gerar ID duplicado.
                    Id               = vm.NextId(),
                    Name             = r.Title,
                    Description      = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(),
                    Level            = story.Level + 1,
                    Parent           = story,
                    TfsId            = r.TaskId,
                    TfsType          = "Task",
                    // Task fechada (100%): restante = 0 e o esforço vem do CompletedWork — o HH
                    // Original não vira "restante" (senão AbsorbRemaining dobraria). Ver helper.
                    EstimatedHours   = estH,
                    CurrentHours     = curH,
                    PercentComplete  = r.PercentComplete,
                    Priority         = r.Priority > 0 ? r.Priority : 5,
                    TfsStackRank     = r.BacklogRank,
                    TfsState         = r.State,
                    Approved         = r.Approved,
                    TfsIterationPath = story.TfsIterationPath,
                    SprintNumber     = story.SprintNumber,
                    Start            = story.Start,
                    Finish           = story.Finish,
                };
                ApplyTaskReviewResource(r, pt, vm);
                story.Children.Add(pt);
                story.TasksSuppressed = false;
                firstAdded ??= pt;
            }

            if (firstAdded != null)
            {
                story.IsSummary = true;
                // A Story pode estar recolhida no cronograma (inclusive por nunca ter tido
                // filhos): sem expandir, as Tasks aplicadas ficariam invisíveis.
                vm.ExpandTask(story);
                // Recalcula TKs da story com base nos filhos do tipo Task
                story.DevopsTaskCount = story.Children.Count(c =>
                    string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase));
                vm.Project.IsDirty = true;
                if (refreshAfterAdd)
                {
                    vm.RebuildFlatTasks();
                    GanttCtrl.ForceRender();
                }
            }
            return firstAdded;
        }

        private static void ApplyTaskReviewResource(
            TaskReviewRow row,
            NXProject.Models.ProjectTask task,
            MainViewModel vm)
        {
            var display = row.AssignedToDisplay;
            var email = row.AssignedTo;
            task.Resources.Clear();
            if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(display))
                return;

            var res = vm.Project.Resources.FirstOrDefault(x =>
                string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Name, email, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Name, display, StringComparison.OrdinalIgnoreCase));
            if (res == null)
                return;

            task.Resources.Add(new NXProject.Models.TaskResource
            {
                ResourceId = res.Id,
                Resource = res,
                AllocationPercent = 100
            });
        }

        private void SyncExpandedTaskRowsToSchedule(
            IReadOnlyList<TaskReviewRow> rows,
            NXProject.Models.ProjectTask story,
            MainViewModel vm)
        {
            var expandedTasks = story.Children
                .Where(c => string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (expandedTasks.Count == 0)
                return;

            var rowsById = rows
                .Where(r => r.TaskId > 0)
                .GroupBy(r => r.TaskId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var task in expandedTasks)
            {
                if (task.TfsId is not > 0 || !rowsById.TryGetValue(task.TfsId.Value, out var row))
                {
                    story.Children.Remove(task);
                    continue;
                }

                ApplyTaskReviewRowToScheduleTask(row, task, story, vm);
            }

            var existingIds = story.Children
                .Where(c => string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase) && c.TfsId.HasValue)
                .Select(c => c.TfsId!.Value)
                .ToHashSet();
            var missingRows = rows.Where(r => r.TaskId > 0 && !existingIds.Contains(r.TaskId)).ToList();
            if (missingRows.Count > 0)
                AddTaskRowsToSchedule(missingRows, story, vm, refreshAfterAdd: false);

            story.DevopsTaskCount = story.Children.Count(c =>
                string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase));
            vm.Project.IsDirty = true;
        }

        private void ApplyTaskReviewRowToScheduleTask(
            TaskReviewRow row,
            NXProject.Models.ProjectTask task,
            NXProject.Models.ProjectTask story,
            MainViewModel vm)
        {
            task.Name = row.Title;
            if (!string.IsNullOrWhiteSpace(row.Description))
                task.Description = row.Description.Trim();
            task.TfsId = row.TaskId;
            task.TfsType = "Task";
            // Task fechada (100%): restante = 0 (o esforço real já está em CompletedWork);
            // não deixar o HH Original ser absorvido no Atual (senão dobra). Ver helper.
            var (curH, estH) = Services.TfsImportService.ResolveTaskScheduleHours(
                row.EstimatedHours, row.CompletedHours, row.PercentComplete);
            task.EstimatedHours = estH;
            task.CurrentHours = curH;
            task.PercentComplete = row.PercentComplete;
            task.Priority = row.Priority > 0 ? row.Priority : 5;
            task.TfsState = row.State;
            task.TfsIterationPath = story.TfsIterationPath;
            task.SprintNumber = story.SprintNumber;

            ApplyTaskReviewResource(row, task, vm);
        }

        private void ReleaseStoryTasks(NXProject.Models.ProjectTask story, MainViewModel vm)
        {
            var tasks = story.Children
                .Where(c => string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var t in tasks) story.Children.Remove(t);
            story.TasksSuppressed = false;
            // Sem filhos ela volta a ser folha: manter IsSummary tiraria o menu da grid de
            // Tasks (Tech Lead) e a edição direta da Story.
            if (story.Children.Count == 0) story.IsSummary = false;
            vm.Project.IsDirty = true;
            vm.RebuildFlatTasks();
            GanttCtrl.ForceRender();
        }

        private void OnReleaseStory(TaskViewModel storyVm)
        {
            if (DataContext is not MainViewModel vm) return;
            // Libera a story como folha editável: reseta flag e garante que não há tasks filhas
            storyVm.Model.TasksSuppressed = false;
            var tasks = storyVm.Model.Children
                .Where(c => string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var t in tasks) storyVm.Model.Children.Remove(t);
            if (storyVm.Model.Children.Count == 0) storyVm.Model.IsSummary = false;
            vm.Project.IsDirty = true;
            vm.RebuildFlatTasks();
            GanttCtrl.ForceRender();
        }

        private void OnSuppressChildTasks(TaskViewModel storyVm)
        {
            if (DataContext is not MainViewModel vm) return;
            var tasks = storyVm.Model.Children
                .Where(c => string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (tasks.Count == 0)
            {
                MessageBox.Show("Nenhuma Task no cronograma para esta atividade.", "Sem Tasks", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show($"Ocultar {tasks.Count} Task(s) do cronograma?\n(Não apaga no DevOps — use 'Expandir Tasks' para restaurar)", "Suprimir Tasks", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            foreach (var t in tasks)
                storyVm.Model.Children.Remove(t);

            storyVm.Model.TasksSuppressed = true;
            vm.Project.IsDirty = true;
            vm.RebuildFlatTasks();
            GanttCtrl.ForceRender();
        }

        // Load Task ToDo: para as Stories com % de conclusão abaixo de 100% (ainda a fazer),
        // carrega do DevOps TODAS as Tasks (inclusive as Closed, para a duração/soma de HH
        // ficar correta) e adiciona ao cronograma.
        // Com Ctrl pressionado, pergunta se as Stories já 100% concluídas também devem ser
        // incluídas (conferência visual de todas as Tasks no Gantt).
        private async void OnLoadTaskToDoClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;

            bool includeCompleted = false;
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                var answer = MessageBox.Show(this, AppStrings.Get("Main_LoadTaskToDoAskCompleted"),
                    "Load Task ToDo", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes) includeCompleted = true;
            }

            var stories = vm.FlatTasks
                .Select(t => t.Model)
                .Where(t => t.TfsId is > 0
                         && Services.TfsImportService.IsStoryTypePublic(t.TfsType)
                         && (includeCompleted || t.PercentComplete < 100.0))
                .ToList();
            if (stories.Count == 0)
            {
                MessageBox.Show(this, AppStrings.Get(includeCompleted ? "Main_LoadTaskAllNone" : "Main_LoadTaskToDoNone"),
                    "Load Task ToDo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var options = Services.TfsConnectionStore.Load("NXProject.Community");
            if (string.IsNullOrWhiteSpace(options.OrganizationUrl) || string.IsNullOrWhiteSpace(options.PersonalAccessToken))
            {
                MessageBox.Show(this, "Configure a integração com o TFS/DevOps (Importar → TFS) antes de carregar as Tasks.",
                    "Load Task ToDo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int added = 0, storiesTouched = 0;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                for (int i = 0; i < stories.Count; i++)
                {
                    var story = stories[i];
                    vm.StatusMessage = AppStrings.Get("Main_LoadTaskToDoStep", i + 1, stories.Count, story.Name ?? "");

                    var tasks = await Services.TfsImportService.FetchChildTasksFromDevOpsAsync(options, story.TfsId!.Value);
                    if (tasks == null || tasks.Count == 0) continue;

                    // Traz TODAS as Tasks da Story (inclusive as Closed), senão a
                    // duração/soma de HH da Story fica errada.
                    var rows = tasks
                        .Select(t => new Views.TaskReviewRow
                        {
                            StoryTask       = story,
                            TaskId          = t.TfsId,
                            Title           = t.Title,
                            Description     = t.Description ?? "",
                            State           = t.State ?? "New",
                            EstimatedHours  = t.EstimatedHours,
                            CompletedHours  = t.CompletedHours,
                            PercentComplete = t.PercentComplete,
                            Priority        = t.Priority,
                            AssignedTo        = t.AssignedTo ?? "",
                            AssignedToDisplay = t.AssignedToDisplay ?? t.AssignedTo ?? "",
                        })
                        .ToList();
                    if (rows.Count == 0) continue;

                    var before = story.Children.Count;
                    AddTaskRowsToSchedule(rows, story, vm, refreshAfterAdd: false);
                    var delta = story.Children.Count - before;
                    if (delta > 0) { added += delta; storiesTouched++; }
                }
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
            }

            if (added > 0)
            {
                vm.Project.IsDirty = true;
                vm.RebuildFlatTasks();
                GanttCtrl.ForceRender();
                TaskGridCtrl.RefreshRows();
            }
            vm.StatusMessage = AppStrings.Get("Main_LoadTaskToDoDone", added, storiesTouched);
            MessageBox.Show(this, AppStrings.Get("Main_LoadTaskToDoDone", added, storiesTouched),
                "Load Task ToDo", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void OnTechLeadReviewClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;

            var storiesWithDevOps = vm.FlatTasks
                .Where(t => !t.Model.IsSummary && !t.Model.IsMilestone && t.Model.TfsId is > 0 &&
                            (Services.TfsImportService.IsStoryTypePublic(t.Model.TfsType) ||
                             string.Equals(t.Model.TfsType, "Feature", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(t.Model.TfsType, "Epic", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (storiesWithDevOps.Count == 0)
            {
                MessageBox.Show("Nenhuma atividade vinculada ao DevOps encontrada.", "Revisão de Tasks", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var win = new TechLeadTaskReviewWindow(vm.Project) { Owner = this };
            win.ShowDialog();
            if (win.HasChanges)
            {
                vm.Project.IsDirty = true;
                vm.RebuildFlatTasks();
                GanttCtrl.ForceRender();
            }
        }

        private async void OnConfirmDeleteTask(TaskViewModel task)
        {
            if (DataContext is not MainViewModel vm) return;

            bool isNoDevOps = string.Equals(task.Model.TfsType?.Trim(), "No DevOps", StringComparison.OrdinalIgnoreCase);
            bool isStory = NXProject.Services.TfsImportService.IsStoryTypePublic(task.Model.TfsType);
            bool hasDevOpsId = task.TfsId is > 0;
            bool isStarted = task.PercentComplete > 0.0001;
            // Story com andamento (% > 0) também é protegida: não pode excluir aqui.
            bool storyStartedProtected = hasDevOpsId && isStory && isStarted;
            bool canDeleteInDevOps = hasDevOpsId && isStory && !isStarted;

            // Epic/Feature (ID real, não Story): não pode excluir aqui, oferece abrir no DevOps.
            if (hasDevOpsId && !isStory)
            {
                var result = MessageBox.Show(
                    LanguageService.Str("Delete_ProtectedMsg", task.Name, task.Model.TfsType ?? ""),
                    LanguageService.Str("Delete_ProtectedTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes)
                    OpenTaskInDevOps(task.Model);
                return;
            }

            // Story já iniciada (% > 0): protegida da exclusão pelo cronograma.
            if (storyStartedProtected)
            {
                var result = MessageBox.Show(
                    LanguageService.Str("Delete_StartedStoryMsg", task.Name, task.PercentComplete),
                    LanguageService.Str("Delete_ProtectedTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes)
                    OpenTaskInDevOps(task.Model);
                return;
            }

            // Monta janela de confirmação
            var confirm = new Window
            {
                Title = LanguageService.Str("Delete_ConfirmTitle"),
                Width = 480, Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.White
            };
            bool confirmed = false;
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            var taskName = task.Name ?? string.Empty;
            var tfsIdText = task.TfsId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var titulo = canDeleteInDevOps
                ? LanguageService.Str("Delete_DevOpsTitle", tfsIdText)
                : LanguageService.Str("Delete_LocalTitle", taskName);
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = titulo,
                FontSize = 15, FontWeight = FontWeights.Bold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28)),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });
            var detalhe = canDeleteInDevOps
                ? LanguageService.Str("Delete_DevOpsDetail", taskName)
                : LanguageService.Str("Delete_LocalDetail");
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = detalhe,
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            });
            var btnPanel = new System.Windows.Controls.StackPanel
                { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnConfirm = new System.Windows.Controls.Button
            {
                Content = LanguageService.Str("Delete_BtnConfirm"),
                Width = 120, Height = 30,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28)),
                Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0),
                FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand
            };
            var btnCancel = new System.Windows.Controls.Button
                { Content = LanguageService.Str("Delete_BtnCancel"), Width = 90, Height = 30, Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
            btnConfirm.Click += (_, _) => { confirmed = true; confirm.Close(); };
            btnCancel.Click  += (_, _) => confirm.Close();
            btnPanel.Children.Add(btnConfirm);
            btnPanel.Children.Add(btnCancel);
            panel.Children.Add(btnPanel);
            confirm.Content = panel;
            confirm.ShowDialog();

            if (!confirmed) return;

            if (canDeleteInDevOps)
            {
                try
                {
                    var options = NXProject.Services.TfsConnectionStore.Load("NXProject.Community");
                    await NXProject.Services.TfsImportService.DeleteWorkItemAsync(options, task.TfsId!.Value);
                }
                catch (Exception ex)
                {
                    TfsErrorDialog.Show(this, AppStrings.Get("Tfs_ActionDelete"), ex);
                    return;
                }
            }

            vm.DeleteTaskViewModel(task);
        }

        private void OnEditPercAloc(TaskViewModel task)
        {
            if (DataContext is not MainViewModel vm) return;

            var maxAllocationPercent = task.Model.PercentComplete > 0 ? 120 : 100;
            double totalH = (task.Model.CurrentHours ?? 0) + (task.Model.EstimatedHours ?? 0);
            var dialog = new PercAlocEditWindow(
                task.Name,
                task.Model.Resources[0].AllocationPercent,
                maxAllocationPercent,
                taskStart:  task.Model.Start,
                totalHours: totalH)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var r in task.Model.Resources)
                    r.AllocationPercent = dialog.ResultPercent;

                task.NotifyResourcesChanged();
                task.RecalcFinishFromPercAloc();
                vm.Project.IsDirty = true;
            }
        }

        private void OnEditClassification(TaskViewModel task)
        {
            if (DataContext is not MainViewModel vm) return;

            var currentValues = new Dictionary<string, string>(task.Model.CustomDevopsFieldValues, StringComparer.OrdinalIgnoreCase);
            // Compat: se ainda não tem dict mas tem TfsClassification, tenta preencher o campo primário
            if (currentValues.Count == 0 && !string.IsNullOrWhiteSpace(task.TfsClassification))
            {
                var opts = Services.TfsConnectionStore.Load("NXProject.Community");
                opts.TypeFieldMappings.TryGetValue(task.TfsType ?? "", out var cfg);
                if (cfg == null) opts.TypeFieldMappings.TryGetValue("*", out cfg);
                var prim = cfg?.CustomDevopsFields.FirstOrDefault()?.Field;
                if (prim != null) currentValues[prim] = task.TfsClassification;
            }

            var dlg = new CustomDevOpsEditWindow(task.TfsType ?? "", currentValues) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.FieldValues.Count > 0)
            {
                foreach (var kv in dlg.FieldValues)
                    task.Model.CustomDevopsFieldValues[kv.Key] = kv.Value;
                // Atualiza TfsClassification com o valor do primeiro campo (compat)
                var first = dlg.FieldValues.Values.FirstOrDefault();
                if (first != null) task.TfsClassification = first;
                vm.Project.IsDirty = true;
            }
        }

        private async void OnSyncTfsClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            // Cronograma "somente leitura" (marca ANTIGA do Portfólio): só bloqueia quando NÃO dá
            // para revalidar pelo grupo administrador. Com work item raiz do DevOps, quem manda é
            // a checagem AO VIVO do Adm_NX logo abaixo — o flag gravado pode estar desatualizado.
            if (vm.Project.ReadOnly && vm.Project.DevOpsRootWorkItemId <= 0)
            {
                MessageBox.Show(this, AppStrings.Get("Main_ReadOnlyBlocked"),
                    AppStrings.Get("Tfs_ActionSync"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var options = Services.TfsConnectionStore.Load("NXProject.Community");

            // Alvo da sincronização = organização + Team Project do cronograma ABERTO
            // (gravados no .nxp na importação), não a config global. Evita sincronizar
            // no projeto errado ao alternar entre cronogramas. O PAT continua o da config.
            var projOrg  = vm.Project.DevOpsOrganizationUrl?.Trim();
            var projTeam = vm.Project.DevOpsTeamProject?.Trim();
            bool targetDiffers = false;
            if (!string.IsNullOrWhiteSpace(projOrg) || !string.IsNullOrWhiteSpace(projTeam))
            {
                var connOrg  = options.OrganizationUrl?.Trim() ?? "";
                var connTeam = options.TeamProject?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(projOrg))  options.OrganizationUrl = projOrg;
                if (!string.IsNullOrWhiteSpace(projTeam)) options.TeamProject = projTeam;

                targetDiffers =
                    (!string.IsNullOrWhiteSpace(projTeam) && !string.Equals(connTeam, projTeam, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(projOrg)  && !string.Equals(connOrg, projOrg, StringComparison.OrdinalIgnoreCase));
            }

            // Work Item raiz (tipo Project) = o do cronograma ABERTO (gravado no .nxp),
            // NÃO o options.RootWorkItemId da config global (última importação). Evita
            // reparentar Epics para o root de outro projeto ao alternar cronogramas.
            options.RootWorkItemId = vm.Project.DevOpsRootWorkItemId;

            if (string.IsNullOrWhiteSpace(options.OrganizationUrl) ||
                string.IsNullOrWhiteSpace(options.TeamProject) ||
                string.IsNullOrWhiteSpace(options.PersonalAccessToken))
            {
                MessageBox.Show(
                    "Configure a conexão e marque \"Lembrar o token\" primeiro em Arquivo → Importar → TFS / Azure DevOps.",
                    "Sincronizar TFS/DevOps", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Grupo administrador do NX (campo Adm_NX): validação AO VIVO — relê o grupo direto do
            // work item Project no DevOps (não depende do checkbox nem do cache do .nxp). Campo
            // vazio/ausente = liberado; falha técnica (rede/escopo) = fail-open.
            try
            {
                var (allowed, groupName) = await Services.TfsImportService.CanCurrentUserSyncLiveAsync(
                    options, vm.Project.DevOpsRootWorkItemId, options.AdmGroupFieldName);
                if (!allowed)
                {
                    MessageBox.Show(this, AppStrings.Get("Main_AdmGroupBlocked", groupName),
                        AppStrings.Get("Tfs_ActionSync"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            catch { /* falha técnica ao validar o grupo → não bloqueia */ }

            // Confirmação mostra o PROJETO (work item raiz) + Team Project e organização.
            var rootName = string.IsNullOrWhiteSpace(vm.Project.DevOpsProjectName)
                ? AppStrings.Get("Sync_NoRootProject")
                : vm.Project.DevOpsProjectName!;
            var rootId = vm.Project.DevOpsRootWorkItemId > 0
                ? "#" + vm.Project.DevOpsRootWorkItemId
                : "-";
            var confirmBody = AppStrings.Get("Sync_ConfirmBody",
                rootName, rootId, options.TeamProject ?? "", options.OrganizationUrl ?? "");
            if (targetDiffers)
                confirmBody += "\n\n" + AppStrings.Get("Sync_ConfirmDiffersNote");
            var confirm = MessageBox.Show(
                confirmBody,
                "Sincronizar TFS/DevOps", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
                return;

            if (!ConfirmKnownTfsResources(vm))
                return;

            if (!ConfirmInitialLoadCompletedHours(vm))
                return;

            if (!ConfirmCompletedTfsState(vm))
                return;

            vm.ApplyMilestonePredecessors();

            // Tela modal de andamento: a sincronização é longa e sem feedback o usuário
            // fica sem saber em que etapa/item está. Abre sem bloquear (Show) para a
            // sincronização rodar e a janela ir se atualizando.
            Services.TfsImportService.SyncReport? report = null;
            var progressWin = new SyncProgressWindow { Owner = this };
            var reporter = new Progress<Services.TfsImportService.SyncProgress>(progressWin.Report);
            progressWin.Show();
            IsEnabled = false;   // evita mexer no cronograma durante a sincronização
            try
            {
                report = await Services.TfsImportService.SyncAsync(
                    vm.Project, options, progress: reporter);
            }
            catch (Exception ex)
            {
                IsEnabled = true;
                progressWin.Done();
                TfsErrorDialog.Show(this, AppStrings.Get("Tfs_ActionSync"), ex);
                return;
            }
            finally
            {
                IsEnabled = true;
                progressWin.Done();
            }

            vm.Project.IsDirty = true;
            vm.RefreshTasks();
            GanttCtrl.ForceRender();
            TaskGridCtrl.RefreshRows();
            try
            {
                new SyncResultWindow(report) { Owner = this }.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Erro ao abrir resultado:\n{ex.Message}\n\nTipo: {ex.GetType().Name}\n\n{ex.StackTrace}",
                    "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            if (report.ConflictItems.Count > 0)
            {
                try
                {
                    new TfsSyncConflictWindow(report.ConflictItems, vm.Project, options)
                    {
                        Owner = this
                    }.ShowDialog();
                    vm.RefreshTasks();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Erro ao abrir resolução de conflitos:\n{ex.Message}",
                        "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            // Oferece atualizar o ID interno (:I) para o ID DevOps (:T) nas planilhas de origem.
            TryBackfillTaskPlanIds(vm);

            // Garante que grid e Gantt reflitam conflitos após fechar o log.
            GanttCtrl.ForceRender();
            TaskGridCtrl.RefreshRows();
        }

        /// <summary>
        /// Após sincronizar, atualiza o ID interno (:I) para o ID DevOps (:T) nas planilhas
        /// de Plan Task que originaram Tasks. Tenta gravar direto no .xlsx; se estiver aberto
        /// no Excel (ou o usuário adiar), grava um log "<nome>_Sync_NXProject.xml" na pasta —
        /// aplicado quando a planilha for aberta no Task Plan. A sincronização já concluiu.
        /// </summary>
        private void TryBackfillTaskPlanIds(MainViewModel vm)
        {
            string DisplayId(NXProject.Models.ProjectTask t) => t.TfsId is > 0 ? $"{t.TfsId.Value}:T" : $"{t.Id}:I";
            NXProject.Models.ProjectTask? Ancestor(NXProject.Models.ProjectTask t, string type)
            {
                for (var p = t.Parent; p != null; p = p.Parent)
                    if (string.Equals(p.TfsType?.Trim(), type, StringComparison.OrdinalIgnoreCase)
                        || (type == "Story" && string.Equals(p.TfsType?.Trim(), "User Story", StringComparison.OrdinalIgnoreCase)))
                        return p;
                return null;
            }

            // Tasks que vieram de uma planilha e agora têm ID DevOps.
            var pending = vm.FlatTasks
                .Select(t => t.Model)
                .Where(t => !string.IsNullOrEmpty(t.SourcePlanPath)
                         && !string.IsNullOrEmpty(t.SourcePlanRowKey)
                         && t.TfsId is > 0)
                .GroupBy(t => t.SourcePlanPath!, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (pending.Count == 0) return;

            int totalTasks = pending.Sum(g => g.Count());
            var ask = MessageBox.Show(this,
                AppStrings.Get("TaskPlan_BackfillAsk", totalTasks, pending.Count),
                "Sincronizar TFS/DevOps", MessageBoxButton.YesNo, MessageBoxImage.Question);

            foreach (var group in pending)
            {
                var path = group.Key;
                var entries = group.Select(t => new NXProject.Community.Services.BackfillEntry
                {
                    TaskKey      = t.SourcePlanRowKey!,
                    NewTaskId    = DisplayId(t),
                    NewStoryId   = Ancestor(t, "Story")   is { } s ? DisplayId(s) : null,
                    NewFeatureId = Ancestor(t, "Feature") is { } f ? DisplayId(f) : null,
                }).ToList();

                bool applied = false;
                if (ask == MessageBoxResult.Yes && System.IO.File.Exists(path)
                    && !NXProject.Community.Services.ExcelTaskPlanService.IsLockedForWrite(path))
                {
                    try
                    {
                        NXProject.Community.Services.ExcelTaskPlanService.TryBackfillIds(path, entries);
                        NXProject.Community.Services.ExcelTaskPlanService.DeletePendingSidecar(path);
                        applied = true;
                    }
                    catch { applied = false; }
                }

                if (!applied)
                {
                    // Não deu para gravar agora (planilha aberta / adiado): grava o log lateral.
                    try { NXProject.Community.Services.ExcelTaskPlanService.WritePendingSidecar(path, entries); } catch { }
                }

                // Limpa a marcação de origem (o log lateral ou a gravação já cobrem o resto).
                foreach (var t in group)
                {
                    t.SourcePlanPath = null;
                    t.SourcePlanRowKey = null;
                }
            }

            vm.Project.IsDirty = true;
            MessageBox.Show(this,
                ask == MessageBoxResult.Yes
                    ? AppStrings.Get("TaskPlan_BackfillDone")
                    : AppStrings.Get("TaskPlan_BackfillDeferred"),
                "Sincronizar TFS/DevOps", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static bool ConfirmKnownTfsResources(MainViewModel vm)
        {
            var manualResources = vm.FlatTasks
                .Where(t => t.Model.TfsId.HasValue)
                .SelectMany(t => t.Model.Resources.Select(a => new
                {
                    Task = t,
                    Resource = a.Resource ?? vm.Project.Resources.FirstOrDefault(r => r.Id == a.ResourceId)
                }))
                .Where(x => x.Resource != null && !x.Resource.IsImportedFromTfs)
                .GroupBy(x => x.Resource!.Id)
                .Select(g => new
                {
                    Resource = g.First().Resource!,
                    Count = g.Select(x => x.Task.Model.Id).Distinct().Count()
                })
                .OrderBy(x => x.Resource.Name)
                .ToList();

            if (manualResources.Count == 0)
                return true;

            var sample = string.Join(Environment.NewLine,
                manualResources
                    .Take(8)
                    .Select(x => $"- {x.Resource.DisplayName} ({x.Count} atividade(s))"));
            var suffix = manualResources.Count > 8
                ? $"{Environment.NewLine}- ... e mais {manualResources.Count - 8}"
                : string.Empty;

            MessageBox.Show(
                "Existem recursos marcados com * que nao foram identificados no TFS/DevOps:"
                + Environment.NewLine + Environment.NewLine
                + sample + suffix
                + Environment.NewLine + Environment.NewLine
                + "Ajuste a alocacao para um recurso importado do TFS/DevOps e sincronize novamente.",
                "Sincronizar TFS/DevOps",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        private static bool ConfirmInitialLoadCompletedHours(MainViewModel vm)
        {
            var candidates = vm.FlatTasks
                .Where(t => t.Model.TfsId.HasValue
                            && t.Model.TfsId.Value > 0
                            && t.Model.Children.Count == 0
                            && t.Model.PercentComplete >= 100
                            && !(t.Model.CurrentHours is > 0)
                            && t.Model.OriginalEstimatedHours is > 0)
                .ToList();

            if (candidates.Count == 0)
                return true;

            var sample = string.Join(Environment.NewLine,
                candidates
                    .Take(8)
                    .Select(t => $"- #{t.Model.TfsId}: {t.Model.Name} (HH Original {t.Model.OriginalEstimatedHours:0.##}h)"));
            var suffix = candidates.Count > 8
                ? $"{Environment.NewLine}- ... e mais {candidates.Count - 8}"
                : string.Empty;

            var decision = MessageBox.Show(
                "Existem atividades 100% concluídas com HH Atual vazio/zero. Isso parece uma carga inicial já concluída."
                + Environment.NewLine + Environment.NewLine
                + sample + suffix
                + Environment.NewLine + Environment.NewLine
                + "Sim = definir HH Atual = HH Original e HH Restante = 0 antes de sincronizar."
                + Environment.NewLine
                + "Não = sincronizar mantendo os valores atuais."
                + Environment.NewLine
                + "Cancelar = não sincronizar.",
                "Sincronizar TFS/DevOps",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (decision == MessageBoxResult.Cancel)
                return false;

            if (decision == MessageBoxResult.Yes)
            {
                foreach (var task in candidates)
                {
                    var originalHours = task.Model.OriginalEstimatedHours!.Value;
                    task.Model.CurrentHours = originalHours;
                    task.Model.EstimatedHours = 0;
                    foreach (var assignment in task.Model.Resources)
                        assignment.EstimatedHours = 0;
                    task.RefreshDerivedDisplayProperties();
                }

                vm.Project.IsDirty = true;
            }

            return true;
        }

        private static bool ConfirmCompletedTfsState(MainViewModel vm)
        {
            // Antes de qualquer alerta: corrige silenciosamente Closed → Active para Stories < 100%.
            foreach (var t in vm.FlatTasks
                .Where(t => !t.IsSummary
                            && t.Model.TfsId is > 0
                            && Services.TfsImportService.IsStoryTypePublic(t.Model.TfsType)
                            && t.PercentComplete < 100
                            && string.Equals(t.TfsState?.Trim(), "Closed", StringComparison.OrdinalIgnoreCase)))
            {
                t.TfsState = "Active";
                vm.Project.IsDirty = true;
            }

            var completedNotClosed = vm.FlatTasks
                .Where(t => !t.IsSummary
                            && t.Model.TfsId.HasValue
                            && t.Model.TfsId.Value > 0
                            && t.PercentComplete >= 100
                            && !string.Equals(t.TfsState?.Trim(), "Closed", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (completedNotClosed.Count == 0)
                return true;

            var sample = string.Join(Environment.NewLine,
                completedNotClosed
                    .Take(8)
                    .Select(t => $"- #{t.TfsId}: {t.Name} ({t.TfsState ?? "sem estado"})"));
            var suffix = completedNotClosed.Count > 8
                ? $"{Environment.NewLine}- ... e mais {completedNotClosed.Count - 8}"
                : string.Empty;

            var decision = MessageBox.Show(
                "Existem atividades com 100% de conclusao, mas o estado no TFS/DevOps nao esta como Closed:"
                + Environment.NewLine + Environment.NewLine
                + sample + suffix
                + Environment.NewLine + Environment.NewLine
                + "Sim = atualizar o status para Closed no TFS e sincronizar."
                + Environment.NewLine
                + "Nao = sincronizar mantendo o status atual."
                + Environment.NewLine
                + "Cancelar = nao sincronizar.",
                "Sincronizar TFS/DevOps",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (decision == MessageBoxResult.Cancel)
                return false;

            if (decision == MessageBoxResult.Yes)
            {
                foreach (var task in completedNotClosed)
                    task.TfsState = "Closed";
                vm.Project.IsDirty = true;
            }

            return true;
        }

        private void OnImportTfsClick(object sender, RoutedEventArgs e) => OpenTfsImport();
        private void ApplyClassificationTypesToGrid()
        {
            var opts = Services.TfsConnectionStore.Load("NXProject.Community");
            var types = opts.TypeFieldMappings
                .Where(kv => kv.Value.CustomDevopsFields.Count > 0)
                .Select(kv => kv.Key);
            TaskGridCtrl.SetClassificationTypes(types);
        }

        private void OnConfigureAzureDevOpsClick(object sender, RoutedEventArgs e)
        {
            var projectName = (DataContext as MainViewModel)?.Project?.Name;
            new TfsDevOpsConfigWindow("NXProject.Community", projectName) { Owner = this }.ShowDialog();
            ApplyClassificationTypesToGrid();
        }
        private void OnImportTfsToolbarClick(object sender, RoutedEventArgs e) => OpenTfsImport();
        private void OnSyncTfsToolbarClick(object sender, RoutedEventArgs e) => OnSyncTfsClick(sender, e);

        private void OpenTfsImport()
        {
            if (DataContext is not MainViewModel vm)
                return;

            var dialog = new TfsImportWindow("NXProject.Community")
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && dialog.ImportedProject is { } project)
            {
                vm.ApplyImportedProject(
                    project,
                    $"Projeto importado do TFS: {project.Name}");
                // Import ao vivo: habilita a checagem de participação no grupo (Leitura/Escrita).
                _liveTfsImportRootId = project.DevOpsRootWorkItemId;
                _admStatusRootId = -1;
                UpdateDevOpsProjectBanner(project.DevOpsProjectName, project.DevOpsRootWorkItemId, project.DevOpsProjectOwner);
                ResetScheduleViewport();
            }
        }

        private void ResetScheduleViewport()
        {
            Dispatcher.InvokeAsync(() =>
            {
                TaskGridCtrl.ResetVerticalOffset();
                GanttCtrl.ScrollToProjectStart();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void OnDevOpsConfigClick(object sender, RoutedEventArgs e)
        {
            new TfsDevOpsConfigWindow("NXProject.Community") { Owner = this }.ShowDialog();
        }

        // Menu "Exibir Cronograma": delega para os mesmos controles da barra do cronograma,
        // para as ações ficarem TANTO na aba quanto no menu.
        private void OnMenuZoomClick(object sender, RoutedEventArgs e) => OnZoomMenuClick(ZoomMenuButton, e);
        private void OnMenuLayoutClick(object sender, RoutedEventArgs e) => OnLayoutToggleClick(LayoutToggleButton, e);
        private void OnMenuMagnifierClick(object sender, RoutedEventArgs e)
        {
            MagnifierToggle.IsChecked = !(MagnifierToggle.IsChecked == true);
            OnMagnifierToggleClick(MagnifierToggle, e);
        }
        private void OnMenuDailyPercentClick(object sender, RoutedEventArgs e)
        {
            DailyPercentToggle.IsChecked = !(DailyPercentToggle.IsChecked == true);
            OnDailyPercentToggleClick(DailyPercentToggle, e);
        }
        // Estes usam Checked/Unchecked — basta inverter o IsChecked para disparar o handler.
        private void OnMenuGanttOriginalClick(object sender, RoutedEventArgs e)
            => GanttOriginalToggle.IsChecked = !(GanttOriginalToggle.IsChecked == true);
        private void OnMenuDayHeaderClick(object sender, RoutedEventArgs e)
            => DayHeaderToggle.IsChecked = !(DayHeaderToggle.IsChecked == true);

        private void OnDevOpsProjectListClick(object sender, RoutedEventArgs e)
        {
            var saved = Services.TfsConnectionStore.Load("NXProject.Community");
            var dlg = new DevOpsProjectListWindow(saved.DevOpsProjectListPath) { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.ResultFilePath))
            {
                saved.DevOpsProjectListPath = dlg.ResultFilePath;
                Services.TfsConnectionStore.Save(saved, !string.IsNullOrWhiteSpace(saved.PersonalAccessToken), "NXProject.Community");
            }
        }

        private void UpdateEpicHours(MainViewModel vm)
        {
            if (DevOpsProjectBanner.Visibility != Visibility.Visible) return;

            // Usa FlatTasks (depth=0) para ter o DurationHours correto (SumTaskHours).
            // EPIC marcado como BACKLOG (campo EPIC_TYPE) fica fora do total do projeto.
            var epicHours = vm.FlatTasks
                .Where(t => t.Depth == 0 && t.IsSummary && !EpicTypes.IsBacklog(t.Model.EpicType))
                .Sum(t => t.DurationHours);

            DevOpsEpicHoursLabel.Text = epicHours > 0
                ? AppStrings.Get("Banner_EpicHours", epicHours)
                : string.Empty;

            // Datas do projeto no banner também ignoram EPICs BACKLOG (mesma regra do HH e do %).
            var scheduleTasks = NonBacklogTasks(vm.Project.Tasks)
                .Where(t => !t.IsMilestone)
                .ToList();
            if (scheduleTasks.Count > 0)
            {
                var start = scheduleTasks.Min(t => t.Start.Date);
                var finish = scheduleTasks
                    .Select(t => ProjectCalendarService.GetInclusiveFinishDate(t.Start, t.Finish).Date)
                    .Max();
                DevOpsScheduleDatesLabel.Text = AppStrings.Get("Banner_Dates", start, finish);
            }
            else
            {
                DevOpsScheduleDatesLabel.Text = string.Empty;
            }

            DevOpsPercentLabel.Text = vm.Project.Tasks.Count > 0
                ? AppStrings.Get("Banner_Percent", vm.ProjectPercent)
                : string.Empty;
        }

        /// <summary>Percorre a árvore pulando subárvores de EPIC marcado como BACKLOG.</summary>
        private static IEnumerable<Models.ProjectTask> NonBacklogTasks(IEnumerable<Models.ProjectTask> tasks)
        {
            foreach (var task in tasks)
            {
                if (EpicTypes.IsBacklog(task.EpicType))
                    continue;

                yield return task;
                foreach (var child in NonBacklogTasks(task.Children))
                    yield return child;
            }
        }

        // Cache do status do grupo Adm por projeto (evita rechecar a cada refresh do banner).
        private int _admStatusRootId = -1;
        private bool? _admStatusCanWrite;   // null = ainda verificando
        // Root do projeto RECÉM-importado do TFS nesta sessão. Só nele a checagem de participação
        // no grupo (rede) roda — ao abrir um cronograma salvo em arquivo não verifica.
        private int _liveTfsImportRootId = -1;
        private string? _lastBannerName;
        private int _lastBannerId;
        private string? _lastBannerOwner;

        private void UpdateDevOpsProjectBanner(string? name, int id, string? owner = null)
        {
            _lastBannerName = name; _lastBannerId = id; _lastBannerOwner = owner;
            // Owner + NOME DO ARQUIVO (a barra de título do Windows às vezes não é vista;
            // aqui no banner fica sempre visível ao lado do título).
            var ownerText = string.IsNullOrWhiteSpace(owner) ? string.Empty : AppStrings.Get("Banner_Owner", owner);
            // Fonte de dados: TFS/DevOps quando o projeto vem do DevOps (tem nome/id de projeto);
            // senão, o nome do arquivo local. A barra de título do Windows às vezes não é vista.
            var isDevOps = !string.IsNullOrWhiteSpace(name) || id > 0;
            var file = (DataContext as MainViewModel)?.Project?.FilePath;
            var fileName = string.IsNullOrWhiteSpace(file) ? null : System.IO.Path.GetFileName(file);
            // TFS/DevOps sem arquivo (aberto direto do DevOps) => "TFS/DevOps".
            // TFS/DevOps já gravado em arquivo => "TFS <Nome do Arquivo>".
            // Arquivo local puro => só o nome do arquivo.
            var source = isDevOps
                ? (string.IsNullOrWhiteSpace(fileName) ? "TFS/DevOps" : $"TFS {fileName}")
                : fileName;
            if (!string.IsNullOrWhiteSpace(source))
            {
                var srcText = AppStrings.Get("Banner_Source", source);
                ownerText = ownerText.Length == 0 ? srcText : ownerText + "   •   " + srcText;
            }
            // Processo do DevOps (Agile/Scrum/CMMI/Basic), lido na importação.
            var process = (DataContext as MainViewModel)?.Project?.DevOpsProcess;
            if (isDevOps && !string.IsNullOrWhiteSpace(process))
                ownerText += "   •   " + AppStrings.Get("Banner_Process", process);
            // Calendário em uso (afeta o cálculo de duração/datas): Geral (settings),
            // Cronograma (embutido no .nxp) ou Erro (falha ao ler → padrão 8h).
            var calKey = ProjectCalendarService.Origin switch
            {
                ProjectCalendarService.CalendarOrigin.Schedule => "Banner_CalSchedule",
                ProjectCalendarService.CalendarOrigin.Error    => "Banner_CalError",
                _                                              => "Banner_CalGeneral"
            };
            ownerText += "   •   " + AppStrings.Get(calKey, ProjectCalendarService.WorkingHoursPerDay.ToString("0.#"));
            // Grupo administrador do NX (Adm_NX): substitui o antigo "Somente leitura". Mostra o
            // grupo e se VOCÊ está nele (escrita) ou não (leitura) — checagem ao vivo, cacheada.
            var vmBanner = DataContext as MainViewModel;
            var admGroup = vmBanner?.Project?.AdmGroupName;
            // A checagem é sempre do usuário ATUAL, então vale também no cronograma aberto de
            // arquivo: revalida ao abrir em vez de confiar no que foi gravado no .nxp.
            var admRootId = vmBanner?.Project?.DevOpsRootWorkItemId ?? 0;
            if (isDevOps && !string.IsNullOrWhiteSpace(admGroup) && admRootId > 0)
            {
                if (_admStatusRootId != admRootId)
                {
                    _admStatusRootId = admRootId;
                    _admStatusCanWrite = null;
                    _ = CheckAdmGroupStatusAsync(admRootId);   // preenche o cache e recarrega o banner
                }
                var key = _admStatusCanWrite == true ? "Banner_AdmGroupWrite"
                        : _admStatusCanWrite == false ? "Banner_AdmGroupRead"
                        : "Banner_AdmGroup";
                ownerText += "   •   " + AppStrings.Get(key, admGroup);
            }
            else if (vmBanner?.Project?.ReadOnly == true)
            {
                // Sem grupo Adm, mantém a marca antiga de somente leitura (compat.).
                ownerText += "   •   " + AppStrings.Get("Banner_ReadOnly");
            }
            DevOpsOwnerLabel.Text = ownerText;

            if (!string.IsNullOrWhiteSpace(name))
            {
                DevOpsProjectNameLabel.Text = name;
                DevOpsProjectIdLabel.Text = id > 0 ? AppStrings.Get("Banner_Id", id) : string.Empty;
                DevOpsProjectBanner.Visibility = Visibility.Visible;
            }
            else if (id > 0)
            {
                DevOpsProjectNameLabel.Text = AppStrings.Get("Banner_IdOnly", id);
                DevOpsProjectIdLabel.Text = string.Empty;
                DevOpsProjectBanner.Visibility = Visibility.Visible;
            }
            else
            {
                DevOpsEpicHoursLabel.Text = string.Empty;
                DevOpsScheduleDatesLabel.Text = string.Empty;
                DevOpsPercentLabel.Text = string.Empty;
                DevOpsOwnerLabel.Text = string.Empty;
                DevOpsProjectBanner.Visibility = Visibility.Collapsed;
            }
        }

        // Verifica AO VIVO se o usuário atual é membro do grupo Adm (pode sincronizar = Escrita)
        // ou não (Leitura), e recarrega o banner. Só é chamado para projeto recém-importado do TFS.
        private async Task CheckAdmGroupStatusAsync(int rootId)
        {
            bool canWrite = true;
            try
            {
                var options = Services.TfsConnectionStore.Load("NXProject.Community");
                var proj = (DataContext as MainViewModel)?.Project;
                var projOrg = proj?.DevOpsOrganizationUrl?.Trim();
                var projTeam = proj?.DevOpsTeamProject?.Trim();
                if (!string.IsNullOrWhiteSpace(projOrg)) options.OrganizationUrl = projOrg;
                if (!string.IsNullOrWhiteSpace(projTeam)) options.TeamProject = projTeam;
                var (allowed, _) = await Services.TfsImportService.CanCurrentUserSyncLiveAsync(
                    options, rootId, options.AdmGroupFieldName);
                canWrite = allowed;   // membro OU não deu para resolver (fail-open) = pode gravar
            }
            catch { canWrite = true; }

            if (_admStatusRootId == rootId)
            {
                _admStatusCanWrite = canWrite;
                // Membro do grupo => a marca antiga "somente leitura" está obsoleta: derruba
                // para o banner e o Sincronizar refletirem o acesso real.
                if (canWrite && (DataContext as MainViewModel)?.Project is { ReadOnly: true } pr)
                    pr.ReadOnly = false;
                Dispatcher.Invoke(() =>
                    UpdateDevOpsProjectBanner(_lastBannerName, _lastBannerId, _lastBannerOwner));
            }
        }

        private void OnSprintSettingsClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            OpenSprintSettings(vm);
        }

        private void OpenSprintSettings(MainViewModel vm)
        {
            new SprintManagerWindow(vm) { Owner = this }.ShowDialog();
            RefreshCriticalPath(vm);
            GanttCtrl.ForceRender();
        }

        private void OnSfpSettingsClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var window = new Window
            {
                Title = "Configuracoes de SPF",
                Owner = this,
                Width = 820,
                Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)FindResource("BackgroundBrush"),
                Content = new Controls.SfpSettingsControl
                {
                    DataContext = vm
                }
            };

            window.ShowDialog();
        }

        private void OnCustomizeColumnsClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            TaskGridCtrl.ShowColumnCustomizer(vm.HiddenColumns, vm.HiddenColumnsExpanded);
        }

        private void OnResourceFilterClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            var resources = vm.Project.Resources;
            if (!resources.Any()) return;

            var dlg = new ResourceFilterWindow(resources, vm.ResourceFilter) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                vm.SetResourceFilter(dlg.SelectedResourceIds);
                UpdateResourceFilterLabel(vm);
            }
        }

        private void UpdateResourceFilterLabel(MainViewModel vm)
        {
            if (vm.ResourceFilter == null)
                ResourceFilterLabel.Text = string.Empty;
            else
                ResourceFilterLabel.Text = $"({vm.ResourceFilter.Count})";
        }

        private void OnPercentFilterClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;

            var dlg = new PercentCompleteFilterWindow(
                vm.PercentCompleteFilterMin,
                vm.PercentCompleteFilterMax,
                vm.ProgressDateFilterMode,
                vm.ProgressDateFilterReference)
            {
                Owner = this
            };

            if (dlg.ShowDialog() == true)
            {
                vm.SetPercentCompleteFilter(
                    dlg.MinPercent,
                    dlg.MaxPercent,
                    dlg.DateFilterMode,
                    dlg.ReferenceDate);
                UpdatePercentFilterLabel(vm);
            }
        }

        private void UpdatePercentFilterLabel(MainViewModel vm)
        {
            if (!vm.HasPercentCompleteFilter)
            {
                PercentFilterLabel.Text = string.Empty;
                return;
            }

            var parts = new List<string>();
            if (vm.PercentCompleteFilterMin.HasValue || vm.PercentCompleteFilterMax.HasValue)
            {
                var min = vm.PercentCompleteFilterMin?.ToString("0") ?? "0";
                var max = vm.PercentCompleteFilterMax?.ToString("0") ?? "100";
                parts.Add($"{min}-{max}");
            }

            var referenceDate = (vm.ProgressDateFilterReference ?? DateTime.Today)
                .ToString("dd/MM", CultureInfo.CurrentCulture);
            if (vm.ProgressDateFilterMode == "StartDate")
                parts.Add($"início > {referenceDate}");
            else if (vm.ProgressDateFilterMode == "FinishDate")
                parts.Add($"fim < {referenceDate}");

            PercentFilterLabel.Text = $"({string.Join(", ", parts)})";
        }

        private void OnMagnifierToggleClick(object sender, RoutedEventArgs e)
        {
            GanttCtrl.MagnifierEnabled = MagnifierToggle.IsChecked == true;
        }

        private void OnDailyPercentToggleClick(object sender, RoutedEventArgs e)
        {
            var visible = TaskGridCtrl.ToggleDailyPercentColumn();
            DailyPercentToggle.IsChecked = visible;
        }

        private void OnZoomMenuClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            var levels = vm.ZoomLevels;
            var idx = levels.IndexOf(vm.SelectedZoom);
            var next = levels[(idx + 1) % levels.Count];
            ApplyZoom(next);
        }

        private void ApplyZoom(string zoom)
        {
            if (DataContext is not MainViewModel vm) return;
            vm.SelectedZoom = zoom;
            ZoomLabel.Text = FormatZoomLabel(zoom);
            GanttCtrl.ZoomLevel = zoom;

            // Dia, Semana, Trimestre e Semestre mostram header por dia; Sprint e Mês usam view por sprint
            bool dayMode = zoom is "Dia" or "Semana" or "Trimestre" or "Semestre";
            int currentMode = GanttCtrl.DayHeaderMode;
            int newMode = dayMode ? (currentMode == 0 ? 1 : currentMode) : 0;
            ApplyDayHeaderMode(newMode);

            GanttCtrl.ForceRender();
        }

        private static string FormatZoomLabel(string zoom) => zoom switch
        {
            "Dia" => "Day",
            "Semana" => "Week",
            "Sprint" => "Sprint",
            "Mês" => "Month",
            "Trimestre" => "Quarter",
            "Semestre" => "Semester",
            _ => zoom
        };

        private void OnGanttOriginalToggleChecked(object sender, RoutedEventArgs e)
            => ApplyGanttOriginalView(true);

        private void OnGanttOriginalToggleUnchecked(object sender, RoutedEventArgs e)
            => ApplyGanttOriginalView(false);

        private void ApplyGanttOriginalView(bool useOriginal)
        {
            var vm = DataContext as MainViewModel;
            if (vm == null) return;

            foreach (var task in vm.FlatTasks)
            {
                if (task.HasOriginalEstimate)
                    task.SetOriginalHoursView(useOriginal);
            }

            TaskGridCtrl.RefreshRows();
            GanttCtrl.ForceRender();
        }

        private bool _applyingDayHeader;

        private void OnDayHeaderToggled(object sender, RoutedEventArgs e)
        {
            // Ignora o evento gerado ao ajustar o IsChecked dentro de ApplyDayHeaderMode
            // (reentrância) — senão o clique pula modos e só alterna entre 2 estados.
            if (_applyingDayHeader) return;
            // Cicla: 0 (off) → 1 (dia1: seg/qua/sex) → 2 (dia2: número do dia) → 0
            int next = (GanttCtrl.DayHeaderMode + 1) % 3;
            ApplyDayHeaderMode(next);

            if (DataContext is MainViewModel vm)
            {
                // Dia 2 só aparece com colunas largas: aproxima para "Dia" e GUARDA o zoom anterior.
                if (next == 2 && vm.SelectedZoom is not ("Dia" or "Semana"))
                {
                    _zoomBeforeDay2 = vm.SelectedZoom;
                    ApplyZoom("Dia");
                }
                // Voltou ao Off: restaura o zoom que havia antes do Dia 2 (padrão de entrada).
                else if (next == 0 && !string.IsNullOrEmpty(_zoomBeforeDay2))
                {
                    var restore = _zoomBeforeDay2!;
                    _zoomBeforeDay2 = null;
                    ApplyZoom(restore);
                }
            }
        }

        private string? _zoomBeforeDay2;

        private void ApplyDayHeaderMode(int mode)
        {
            GanttCtrl.DayHeaderMode = mode;
            _applyingDayHeader = true;
            try { DayHeaderToggle.IsChecked = mode > 0; }
            finally { _applyingDayHeader = false; }

            if (mode > 0)
            {
                TaskGridCtrl.SetColumnHeaderHeight(60.0);
                GanttCtrl.SetHeaderHeight(60.0);
            }
            else
            {
                TaskGridCtrl.SetColumnHeaderHeight(40.0);
                GanttCtrl.SetHeaderHeight(40.0);
            }

            DayHeaderToggle.ToolTip = mode switch
            {
                0 => "Visão por dia (clique para ativar Dia 1)",
                1 => "Visão Dia 1 — segunda/quarta/sexta destacadas (clique para Dia 2)",
                2 => "Visão Dia 2 — dígito compacto por dia (clique para desativar)",
                _ => "Visão por dia"
            };

            GanttCtrl.ForceRender();
        }

        private void OnRefreshViewClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.RefreshTasks();
            GanttCtrl.ForceRender();
        }

        private void OnExportPdfClick(object sender, RoutedEventArgs e)
        {
            // 1. Opções de layout
            var pdfOpts = new PdfExportOptionsWindow { Owner = this };
            if (pdfOpts.ShowDialog() != true) return;

            var vm = DataContext as MainViewModel;
            var projectName = vm?.Project?.Name ?? "Cronograma";

            // 2. Destino do arquivo
            var dlg = new SaveFileDialog
            {
                Title      = Str("Pdf_SaveTitle"),
                Filter     = Str("Pdf_Filter"),
                FileName   = $"{SanitizeFileName(projectName)}{Str("Pdf_FileSuffix")}",
                DefaultExt = "pdf"
            };
            if (dlg.ShowDialog(this) != true) return;

            // 3. Branding
            var appOpts     = TfsConnectionStore.Load();
            var companyName = appOpts.CompanyName ?? string.Empty;
            System.Windows.Media.Imaging.BitmapImage? companyLogo = null;
            if (!string.IsNullOrWhiteSpace(appOpts.CompanyLogoBase64))
            {
                try
                {
                    var bytes = Convert.FromBase64String(appOpts.CompanyLogoBase64);
                    var bmp   = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new System.IO.MemoryStream(bytes);
                    bmp.EndInit();
                    bmp.Freeze();
                    companyLogo = bmp;
                }
                catch { }
            }

            // 4. Cria cópias off-screen em modo impressão: hierarquia toda expandida,
            //    todas as linhas visíveis e Gantt com largura total do cronograma.
            var printVisuals = CreateOffscreenPdfVisuals(
                pdfOpts.LayoutMode,
                pdfOpts.TimelineDaysBefore,
                pdfOpts.TimelineDaysAfter);
            try
            {
                PdfExportService.Export(
                    tableVisual:     printVisuals.Table,
                    ganttVisual:     printVisuals.Gantt,
                    ganttData:       printVisuals.GanttData,
                    projectName:     projectName,
                    companyName:     companyName,
                    companyLogo:     companyLogo,
                    filePath:        dlg.FileName,
                    layoutMode:      pdfOpts.LayoutMode,
                    pageSize:        pdfOpts.PageSize,
                    exportedOnLabel: Str("Pdf_FooterExported"),
                    scheduleSubject: Str("Pdf_SubjectSchedule"));

                // 5. Oferecer abrir o PDF imediatamente
                var result = MessageBox.Show(
                    $"{Str("Pdf_SuccessMsg")}\n{dlg.FileName}\n\n{Str("Pdf_OpenNow")}",
                    Str("Pdf_SuccessTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Str("Pdf_ErrorMsg")}\n{ex.Message}",
                    Str("Pdf_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Fecha a janela off-screen; a tela principal não foi tocada
                printVisuals.Dispose();
            }
        }

        /// <summary>
        /// Cria um TaskGridControl em modo expandido dentro de uma janela invisível,
        /// com largura suficiente para exibir todas as colunas sem scroll.
        /// A janela principal não é afetada.
        /// </summary>
        private PdfPrintVisuals CreateOffscreenPdfVisuals(
            PdfLayoutMode layoutMode,
            int timelineDaysBefore,
            int timelineDaysAfter)
        {
            var vm = (NXProject.ViewModels.MainViewModel)DataContext;
            var printTasks = CreateExpandedPrintTasks(vm);

            const double rowHeight = 22.0;
            double headerHeight = GanttCtrl.DayHeaderMode > 0 ? 60.0 : 40.0;
            double printHeight = headerHeight + Math.Max(1, printTasks.Count) * rowHeight + 4;
            double tableWidth = layoutMode == PdfLayoutMode.Together ? 1450 : 1700;
            var ganttWindow = GetPrintGanttWindow(printTasks, vm, timelineDaysBefore, timelineDaysAfter);
            double dayWidth = GetGanttDayWidth(vm.SelectedZoom);
            double ganttWidth = layoutMode == PdfLayoutMode.Together
                ? GetPrintGanttWidth(ganttWindow.Days, vm.SelectedZoom)
                : Math.Max(GetPrintGanttWidth(ganttWindow.Days, vm.SelectedZoom), 2400);

            var ctrl = new NXProject.Controls.TaskGridControl
            {
                Width              = tableWidth,
                Height             = printHeight,
                Tasks              = printTasks,
                AvailableSprints   = vm.SprintOptions,
                AvailableResources = vm.Project?.Resources,
            };
            ctrl.SetPresentationMode(expanded: true, vm.HiddenColumnsExpanded, vm.HiddenColumnsExpanded);
            ctrl.SetPrintMode();
            ctrl.SetColumnHeaderHeight(headerHeight);

            var gantt = CreatePrintGanttVisual(printTasks, vm, ganttWindow.Start, ganttWindow.Days, ganttWidth, printHeight, headerHeight);
            var ganttData = CreatePdfGanttData(printTasks, vm, ganttWindow.Start, ganttWindow.Days, dayWidth, headerHeight, rowHeight);

            // Janela off-screen: opacidade 0, fora da área visível, sem barra de tarefas
            var win = new Window
            {
                Width          = tableWidth + ganttWidth + 20,
                Height         = printHeight,
                Left           = -10000,
                Top            = -10000,
                ShowInTaskbar  = false,
                WindowStyle    = WindowStyle.None,
                AllowsTransparency = true,
                Opacity        = 0,
                Content        = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        ctrl,
                        gantt
                    }
                },
            };
            win.Show();

            ctrl.UpdateLayout();
            gantt.UpdateLayout();
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));

            return new PdfPrintVisuals(win, ctrl, gantt, ganttData);
        }

        private static PdfExportService.PdfGanttData CreatePdfGanttData(
            ObservableCollection<TaskViewModel> tasks,
            MainViewModel vm,
            DateTime start,
            int visibleDays,
            double dayWidth,
            double headerHeight,
            double rowHeight)
        {
            var pdfTasks = tasks
                .Select(t => new PdfExportService.PdfGanttTask(
                    t.DisplayId,
                    t.DevOpsTooltip,
                    t.Name,
                    t.Depth,
                    t.DurationHours,
                    t.SfpPoints ?? 0,
                    t.Model.Start,
                    t.Model.Finish,
                    t.FinishDisplay,
                    t.IsSummary,
                    t.DisplayAsMilestone,
                    t.PercentComplete,
                    t.PredecessorsText,
                    t.ResourcesText,
                    t.SprintDisplay))
                .ToList();

            var pdfSprints = (vm.Sprints ?? new ObservableCollection<Sprint>())
                .Select(s => new PdfExportService.PdfGanttSprint(
                    s.Name,
                    s.Number,
                    s.Start,
                    s.End))
                .ToList();

            return new PdfExportService.PdfGanttData(
                pdfTasks,
                pdfSprints,
                start,
                visibleDays,
                dayWidth,
                headerHeight,
                rowHeight);
        }

        private static ObservableCollection<TaskViewModel> CreateExpandedPrintTasks(MainViewModel vm)
        {
            var printTasks = new ObservableCollection<TaskViewModel>();
            var byId = new Dictionary<int, TaskViewModel>();

            void Add(ProjectTask task, int depth, TaskViewModel? parent)
            {
                var item = new TaskViewModel(
                    task,
                    depth,
                    vm.LowDaysPerSfp,
                    vm.MediumDaysPerSfp,
                    vm.HighDaysPerSfp)
                {
                    IsExpanded = true,
                    ParentViewModel = parent,
                    FindByInternalId = id => byId.TryGetValue(id, out var found) ? found : null,
                    FindByDisplayId = displayId =>
                    {
                        if (displayId.StartsWith("T:", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(displayId[2..], out var prefixedTfs))
                        {
                            var byPrefixedTfs = byId.Values.FirstOrDefault(t => t.Model.TfsId == prefixedTfs);
                            if (byPrefixedTfs != null) return byPrefixedTfs.Model.Id;
                        }
                        if (displayId.StartsWith("I:", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(displayId[2..], out var prefixedInternal))
                        {
                            return byId.TryGetValue(prefixedInternal, out var byPrefixedInternal)
                                ? byPrefixedInternal.Model.Id
                                : null;
                        }
                        if (displayId.EndsWith(":T", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(displayId[..^2], out var suffixedTfs))
                        {
                            var bySuffixedTfs = byId.Values.FirstOrDefault(t => t.Model.TfsId == suffixedTfs);
                            if (bySuffixedTfs != null) return bySuffixedTfs.Model.Id;
                        }
                        if (displayId.EndsWith(":I", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(displayId[..^2], out var suffixedInternal))
                        {
                            return byId.TryGetValue(suffixedInternal, out var bySuffixedInternal)
                                ? bySuffixedInternal.Model.Id
                                : null;
                        }
                        if (!int.TryParse(displayId, out var value))
                            return null;

                        var byTfs = byId.Values.FirstOrDefault(t => t.Model.TfsId == value);
                        if (byTfs != null) return byTfs.Model.Id;

                        return byId.TryGetValue(value, out var byInternal)
                            ? byInternal.Model.Id
                            : null;
                    }
                };

                parent?.ChildrenViewModels.Add(item);
                printTasks.Add(item);
                byId[task.Id] = item;

                foreach (var child in task.Children)
                    Add(child, depth + 1, item);
            }

            foreach (var task in vm.Project.Tasks)
                Add(task, 0, null);

            foreach (var task in printTasks)
                task.RefreshSprintOptions(vm.SprintOptions);

            return printTasks;
        }

        private static FrameworkElement CreatePrintGanttVisual(
            ObservableCollection<TaskViewModel> tasks,
            MainViewModel vm,
            DateTime printStart,
            int visibleDays,
            double width,
            double height,
            double headerHeight)
        {
            const double leftPadding = 16.0;
            const double rowHeight = 22.0;

            double dayWidth = GetGanttDayWidth(vm.SelectedZoom);
            double bodyHeight = Math.Max(rowHeight, height - headerHeight);
            var root = new Grid
            {
                Width = width,
                Height = height,
                Background = Brushes.White,
                ClipToBounds = true
            };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerHeight) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new Canvas
            {
                Width = width,
                Height = headerHeight,
                Background = new SolidColorBrush(Color.FromRgb(232, 232, 232)),
                ClipToBounds = true
            };
            var body = new Canvas
            {
                Width = width,
                Height = bodyHeight,
                Background = Brushes.White,
                ClipToBounds = true
            };
            Grid.SetRow(header, 0);
            Grid.SetRow(body, 1);
            root.Children.Add(header);
            root.Children.Add(body);

            DrawPrintGanttHeader(header, vm, printStart, visibleDays, width, headerHeight, leftPadding, dayWidth);
            DrawPrintGanttBody(body, tasks, vm, printStart, visibleDays, width, bodyHeight, leftPadding, dayWidth, rowHeight);

            return root;
        }

        private static void DrawPrintGanttHeader(
            Canvas header,
            MainViewModel vm,
            DateTime printStart,
            int visibleDays,
            double width,
            double headerHeight,
            double leftPadding,
            double dayWidth)
        {
            double monthHeight = Math.Max(18, headerHeight * 0.48);
            double sprintTop = monthHeight;
            double sprintHeight = Math.Max(14, headerHeight - monthHeight);
            var lineBrush = new SolidColorBrush(Color.FromRgb(190, 200, 215));

            header.Children.Add(new Rectangle
            {
                Width = width,
                Height = monthHeight,
                Fill = new SolidColorBrush(Color.FromRgb(232, 232, 232))
            });
            header.Children.Add(new Rectangle
            {
                Width = width,
                Height = sprintHeight,
                Fill = new SolidColorBrush(Color.FromRgb(220, 228, 240))
            });
            Canvas.SetTop(header.Children[^1], sprintTop);

            var cursor = new DateTime(printStart.Year, printStart.Month, 1);
            if (cursor > printStart)
                cursor = cursor.AddMonths(-1);

            while (cursor < printStart.AddDays(visibleDays))
            {
                var next = cursor.AddMonths(1);
                double x1 = leftPadding + Math.Max(0, (cursor - printStart).TotalDays) * dayWidth;
                double x2 = leftPadding + Math.Min(visibleDays, (next - printStart).TotalDays) * dayWidth;
                if (x2 > 0 && x1 < width)
                {
                    header.Children.Add(new Line
                    {
                        X1 = x1,
                        X2 = x1,
                        Y1 = 0,
                        Y2 = headerHeight,
                        Stroke = lineBrush,
                        StrokeThickness = 0.6
                    });

                    var label = new TextBlock
                    {
                        Text = cursor.ToString("MMM/yy", CultureInfo.CurrentCulture),
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(95, 105, 120)),
                        TextAlignment = TextAlignment.Center,
                        Width = Math.Max(40, x2 - x1)
                    };
                    Canvas.SetLeft(label, x1);
                    Canvas.SetTop(label, Math.Max(0, (monthHeight - 14) / 2));
                    header.Children.Add(label);
                }
                cursor = next;
            }

            if (vm.Sprints != null && vm.Sprints.Count > 0)
            {
                foreach (var sprint in vm.Sprints)
                {
                    var startOffset = (sprint.Start.Date - printStart).TotalDays;
                    var endOffset = (sprint.End.Date - printStart).TotalDays + 1;
                    if (endOffset < 0 || startOffset > visibleDays)
                        continue;

                    double x = leftPadding + Math.Max(0, startOffset) * dayWidth;
                    double w = Math.Max(12, (Math.Min(visibleDays, endOffset) - Math.Max(0, startOffset)) * dayWidth);
                    var rect = new Rectangle
                    {
                        Width = w,
                        Height = sprintHeight,
                        Fill = new SolidColorBrush(sprint.Number % 2 == 0
                            ? Color.FromRgb(210, 221, 236)
                            : Color.FromRgb(222, 231, 243)),
                        Stroke = new SolidColorBrush(Color.FromRgb(180, 194, 214)),
                        StrokeThickness = 1
                    };
                    Canvas.SetLeft(rect, x);
                    Canvas.SetTop(rect, sprintTop);
                    header.Children.Add(rect);

                    var label = new TextBlock
                    {
                        Text = sprint.Name,
                        FontSize = 9,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(43, 87, 154)),
                        TextAlignment = TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Width = Math.Max(12, w - 4)
                    };
                    Canvas.SetLeft(label, x + 2);
                    Canvas.SetTop(label, sprintTop + Math.Max(0, (sprintHeight - 13) / 2));
                    header.Children.Add(label);
                }
            }

            header.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = headerHeight - 1,
                Y2 = headerHeight - 1,
                Stroke = Brushes.LightGray,
                StrokeThickness = 1
            });
        }

        private static void DrawPrintGanttBody(
            Canvas body,
            ObservableCollection<TaskViewModel> tasks,
            MainViewModel vm,
            DateTime printStart,
            int visibleDays,
            double width,
            double bodyHeight,
            double leftPadding,
            double dayWidth,
            double rowHeight)
        {
            var gridBrush = new SolidColorBrush(Color.FromRgb(235, 235, 235));
            var majorGridBrush = new SolidColorBrush(Color.FromRgb(220, 225, 232));
            var printEnd = printStart.AddDays(visibleDays);

            for (int i = 0; i <= tasks.Count; i++)
            {
                double y = i * rowHeight;
                body.Children.Add(new Line
                {
                    X1 = 0,
                    X2 = width,
                    Y1 = y,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 0.7
                });
            }

            var cursor = new DateTime(printStart.Year, printStart.Month, 1);
            if (cursor > printStart)
                cursor = cursor.AddMonths(-1);
            while (cursor <= printEnd)
            {
                double x = leftPadding + (cursor - printStart).TotalDays * dayWidth;
                if (x >= 0 && x <= width)
                {
                    body.Children.Add(new Line
                    {
                        X1 = x,
                        X2 = x,
                        Y1 = 0,
                        Y2 = bodyHeight,
                        Stroke = majorGridBrush,
                        StrokeThickness = 0.8
                    });
                }
                cursor = cursor.AddMonths(1);
            }

            var todayOffset = (DateTime.Today.Date - printStart).TotalDays;
            if (todayOffset >= 0 && todayOffset <= visibleDays)
            {
                double todayX = leftPadding + todayOffset * dayWidth;
                body.Children.Add(new Line
                {
                    X1 = todayX,
                    X2 = todayX,
                    Y1 = 0,
                    Y2 = bodyHeight,
                    Stroke = new SolidColorBrush(Color.FromRgb(255, 69, 0)),
                    StrokeThickness = 1.3,
                    StrokeDashArray = new DoubleCollection { 4, 2 }
                });
            }

            for (int i = 0; i < tasks.Count; i++)
            {
                var task = tasks[i];
                double y = i * rowHeight;
                double startOffset = (task.Model.Start.Date - printStart).TotalDays;
                double endOffset = (task.Model.Finish.Date - printStart).TotalDays;
                double x = leftPadding + startOffset * dayWidth;
                double barWidth = Math.Max(1, (endOffset - startOffset) * dayWidth);

                if (x + barWidth < 0 || x > width)
                    continue;

                x = Math.Max(0, x);
                barWidth = Math.Min(width - x, barWidth);

                if (task.DisplayAsMilestone)
                {
                    DrawPrintMilestone(body, x, y, rowHeight);
                }
                else if (task.IsSummary)
                {
                    DrawPrintBar(body, x, y, barWidth, rowHeight, Color.FromRgb(148, 163, 184), 0, true);
                }
                else
                {
                    DrawPrintBar(body, x, y, barWidth, rowHeight, Color.FromRgb(91, 155, 213), task.PercentComplete, false);
                }
            }
        }

        private static void DrawPrintBar(Canvas canvas, double x, double y, double width, double rowHeight, Color color, double percent, bool summary)
        {
            const double padding = 4.0;
            var rect = new Rectangle
            {
                Width = Math.Max(1, width),
                Height = Math.Max(4, rowHeight - padding * 2 - (summary ? 2 : 0)),
                Fill = new SolidColorBrush(color),
                RadiusX = summary ? 1 : 2,
                RadiusY = summary ? 1 : 2
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y + padding);
            canvas.Children.Add(rect);

            if (percent > 0)
            {
                var progressHeight = Math.Min(4, Math.Max(2, rect.Height / 2.0));
                var progress = new Rectangle
                {
                    Width = Math.Max(1, width * Math.Min(100, percent) / 100.0),
                    Height = progressHeight,
                    Fill = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
                    Stroke = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                    StrokeThickness = 0.5,
                    RadiusX = 1,
                    RadiusY = 1
                };
                Canvas.SetLeft(progress, x);
                Canvas.SetTop(progress, y + padding + (rect.Height - progressHeight) / 2.0);
                canvas.Children.Add(progress);
            }

            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                Stroke = Brushes.White,
                StrokeThickness = 1
            };
            Canvas.SetLeft(dot, x - 2.5);
            Canvas.SetTop(dot, y + rowHeight / 2 - 2.5);
            canvas.Children.Add(dot);
        }

        private static void DrawPrintMilestone(Canvas canvas, double x, double y, double rowHeight)
        {
            const double padding = 4.0;
            var size = Math.Max(8, rowHeight - padding * 2);
            var diamond = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(x, y + rowHeight / 2),
                    new Point(x + size / 2, y + padding),
                    new Point(x + size, y + rowHeight / 2),
                    new Point(x + size / 2, y + rowHeight - padding)
                },
                Fill = Brushes.Goldenrod,
                Stroke = Brushes.DarkGoldenrod,
                StrokeThickness = 1
            };
            canvas.Children.Add(diamond);
        }

        private static (DateTime Start, int Days) GetPrintGanttWindow(
            ObservableCollection<TaskViewModel> tasks,
            MainViewModel vm,
            int timelineDaysBefore,
            int timelineDaysAfter)
        {
            if (timelineDaysBefore > 0 || timelineDaysAfter > 0)
            {
                var focusedStart = DateTime.Today.AddDays(-timelineDaysBefore);
                var focusedEnd = DateTime.Today.AddDays(timelineDaysAfter);
                var focusedVisibleDays = Math.Max(1, (int)Math.Ceiling((focusedEnd - focusedStart).TotalDays) + 1);
                return (focusedStart, focusedVisibleDays);
            }

            var firstTaskStart = tasks
                .Select(t => t.Model.Start.Date)
                .DefaultIfEmpty(vm.Project?.StartDate.Date ?? DateTime.Today)
                .Min();

            var firstSprintStart = vm.Sprints?
                .Where(s => s.End.Date >= firstTaskStart)
                .Select(s => s.Start.Date)
                .DefaultIfEmpty(firstTaskStart)
                .Min() ?? firstTaskStart;

            var start = firstSprintStart < firstTaskStart ? firstSprintStart : firstTaskStart;
            start = start.AddDays(-2);

            var lastTaskFinish = tasks
                .Select(t => t.Model.Finish.Date)
                .DefaultIfEmpty(start.AddDays(30))
                .Max();

            var lastSprintEnd = vm.Sprints?
                .Where(s => s.Start.Date <= lastTaskFinish)
                .Select(s => s.End.Date)
                .DefaultIfEmpty(lastTaskFinish)
                .Max() ?? lastTaskFinish;

            var printEnd = lastSprintEnd > lastTaskFinish ? lastSprintEnd : lastTaskFinish;
            var visibleDays = Math.Max(30, (int)Math.Ceiling((printEnd - start).TotalDays) + 5);
            var maxDays = vm.SelectedZoom is "Semestre" ? 730 : 365;
            visibleDays = Math.Min(maxDays, visibleDays);

            return (start, visibleDays);
        }

        private static double GetPrintGanttWidth(int visibleDays, string zoomLevel)
        {
            return 16 + visibleDays * GetGanttDayWidth(zoomLevel);
        }

        private static double GetGanttDayWidth(string zoomLevel)
        {
            return zoomLevel switch
            {
                "Dia"       => 22.0,
                "Semana"    => 14.0,
                "Sprint"    => 10.0,
                "Mês"       => 7.0,
                "Trimestre" => 3.5,
                "Semestre"  => 1.8,
                _           => 14.0
            };
        }

        private sealed class PdfPrintVisuals : IDisposable
        {
            public PdfPrintVisuals(
                Window window,
                NXProject.Controls.TaskGridControl table,
                FrameworkElement gantt,
                PdfExportService.PdfGanttData ganttData)
            {
                Window = window;
                Table = table;
                Gantt = gantt;
                GanttData = ganttData;
            }

            private Window Window { get; }
            public NXProject.Controls.TaskGridControl Table { get; }
            public FrameworkElement Gantt { get; }
            public PdfExportService.PdfGanttData GanttData { get; }

            public void Dispose() => Window.Close();
        }

        private static string Str(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var result  = new System.Text.StringBuilder();
            foreach (var c in name)
                result.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return result.ToString();
        }

        private void OnRecalcDatesClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            vm.RebuildFlatTasks();
            vm.ApplyVirtualPredecessorsToAll();
            GanttCtrl.ForceRender();
        }

        private void OnCalendarSettingsClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var durations = vm.CaptureTaskWorkingDurations();
            var control = new Controls.CalendarSettingsControl("NXProject.Community", vm.Project);
            var window = new Window
            {
                Title = "Calendario de trabalho",
                Owner = this,
                Width = 820,
                Height = 540,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)FindResource("BackgroundBrush"),
                Content = control
            };

            control.Saved += (_, _) =>
            {
                vm.RecalculateScheduleFromCalendar(durations);
                GanttCtrl.ForceRender();
                window.DialogResult = true;
                window.Close();
            };

            window.ShowDialog();
        }

        private void OnResourceAllocationClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var window = new ResourceAllocationWindow(vm)
            {
                Owner = this
            };
            window.ShowDialog();
            GanttCtrl.ForceRender();
        }

        private void OnDelayedTasksClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            new DelayedTasksWindow(vm) { Owner = this }.ShowDialog();
        }

        private void OnStoryStatusChartClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            new StoryStatusChartWindow(vm) { Owner = this }.ShowDialog();
        }

        private bool ConfirmCompleteOutsideSprint(NXProject.ViewModels.TaskViewModel task, NXProject.Models.Sprint sprint)
        {
            const string fmt = "dd/MM/yyyy";
            var finish = NXProject.Services.ProjectCalendarService
                .GetInclusiveFinishDate(task.Model.Start, task.Model.Finish);
            var msg = AppStrings.Get("Complete_OutOfSprintConfirm",
                task.Model.Name,
                sprint.Name,
                sprint.Start.ToString(fmt), sprint.End.ToString(fmt),
                task.Model.Start.ToString(fmt), finish.ToString(fmt));
            var res = MessageBox.Show(this, msg,
                AppStrings.Get("Complete_OutOfSprintTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return res == MessageBoxResult.Yes;
        }

        private void OnTaskPlanClick(object sender, RoutedEventArgs e)
        {
            // Já existe um Task Plan aberto? Pergunta se abre outra planilha; "Não" foca a atual.
            var open = Application.Current.Windows.OfType<TaskPlanWindow>().FirstOrDefault();
            if (open != null)
            {
                var r = MessageBox.Show(this, AppStrings.Get("TaskPlan_AlreadyOpenAsk"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes)
                {
                    if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                    open.Activate();
                    return;
                }
            }

            var vm = DataContext as MainViewModel;
            new TaskPlanWindow(vm) { Owner = this }.Show();
        }

        private void OnFixOutOfPeriodSprintsClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var fixes = vm.GetOutOfPeriodSprintFixes();
            if (fixes.Count == 0)
            {
                MessageBox.Show(this, AppStrings.Get("FixSprints_None"),
                    AppStrings.Get("FixSprints_Title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new FixSprintsWindow(fixes) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            var applied = vm.ApplyOutOfPeriodSprintFixes(fixes);
            MessageBox.Show(this, AppStrings.Get("FixSprints_Done", applied),
                AppStrings.Get("FixSprints_Title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnHierarchyColorPaletteClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            var win = new HierarchyColorPaletteWindow(vm.Project) { Owner = this };
            if (win.ShowDialog() == true)
            {
                vm.ApplyHierarchyColors();
                GanttCtrl.ForceRender();
            }
        }

        private void OnAllocationMapClick(object sender, RoutedEventArgs e)
        {
            var openProject = (DataContext as MainViewModel)?.Project;
            new ProjectAllocationMapWindow(openProject) { Owner = this }.ShowDialog();
        }

        private void OnActivityDiagramClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.Project == null) return;
            new ActivityDiagramWindow(vm.Project.Tasks, vm.Project) { Owner = this }.ShowDialog();
        }

        private void OnResourceCostClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.Project == null) return;
            new ResourceCostWindow(AllTasksFlat(vm), vm.Project.Resources)
                { Owner = this }.ShowDialog();
        }

        // ── Caminho Crítico ──────────────────────────────────────────────────

        private void OnCriticalPathWindowClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.Project == null) return;
            new CriticalPathWindow(
                AllTasksFlat(vm),
                vm.Project.CriticalPathRiskSlackDays,
                vm.Project.CriticalPathCriticalSlackDays,
                () => OpenSprintSettings(vm))
                { Owner = this }.ShowDialog();
        }

        private void RefreshCriticalPath(MainViewModel vm)
        {
            if (vm.Project?.ShowCriticalPath == true)
            {
                var entries = NXProject.Services.CriticalPathService.Compute(AllTasksFlat(vm));
                var riskSlackDays = Math.Max(0.0, vm.Project.CriticalPathRiskSlackDays);
                var criticalSlackDays = Math.Max(0.0, vm.Project.CriticalPathCriticalSlackDays);
                if (riskSlackDays < criticalSlackDays)
                    riskSlackDays = criticalSlackDays;
                var criticalIds = entries
                    .Where(e => e.TotalFloat < criticalSlackDays)
                    .Select(e => e.Task.Id)
                    .ToHashSet();
                var riskIds = entries
                    .Where(e => e.TotalFloat >= criticalSlackDays && riskSlackDays > 0.0 && e.TotalFloat <= riskSlackDays)
                    .Select(e => e.Task.Id)
                    .ToHashSet();
                GanttCtrl.ShowCriticalPath = true;
                GanttCtrl.CriticalTaskIds  = criticalIds;
                GanttCtrl.CriticalPathRiskTaskIds = riskIds;
            }
            else
            {
                GanttCtrl.ShowCriticalPath = false;
                GanttCtrl.CriticalTaskIds  = null;
                GanttCtrl.CriticalPathRiskTaskIds = null;
            }
            GanttCtrl.ForceRender();
        }

        private static IEnumerable<NXProject.Models.ProjectTask> AllTasksFlat(MainViewModel vm)
        {
            IEnumerable<NXProject.Models.ProjectTask> Recurse(IEnumerable<NXProject.Models.ProjectTask> tasks)
            {
                foreach (var t in tasks)
                {
                    yield return t;
                    foreach (var c in Recurse(t.Children)) yield return c;
                }
            }
            return Recurse(vm.Project?.Tasks ?? Enumerable.Empty<NXProject.Models.ProjectTask>());
        }

        private void OnBaselineSaveClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            var filePath = vm.Project?.FilePath;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                MessageBox.Show("Salve o projeto antes de gravar o Baseline.", "Baseline",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var all = vm.FlatTasks.Select(t => t.Model).ToList();
            BaselineService.Save(filePath, all);
            BaselineService.Load(filePath, all);
            GanttCtrl.ForceRender();
            vm.StatusMessage = $"Baseline salvo em {Path.GetFileName(Path.ChangeExtension(filePath, ".nxb"))}.";
        }

        private void OnBaselineOpenClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            var filePath = vm.Project?.FilePath;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                MessageBox.Show("Abra um projeto antes de carregar o Baseline.", "Baseline",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!BaselineService.HasBaseline(filePath))
            {
                MessageBox.Show("Nenhum arquivo .nxb encontrado ao lado do projeto.", "Baseline",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BaselineService.Load(filePath, vm.FlatTasks.Select(t => t.Model));
            GanttCtrl.ForceRender();
            vm.StatusMessage = "Baseline carregado.";
        }

        private void OnBaselineAutoLoadToggle(object sender, RoutedEventArgs e)
        {
            var opts = Services.TfsConnectionStore.Load("NXProject.Community");
            opts.AutoLoadBaseline = BaselineAutoLoadItem.IsChecked;
            Services.TfsConnectionStore.Save(opts, !string.IsNullOrWhiteSpace(opts.PersonalAccessToken), "NXProject.Community");
        }

        private void OnBaselineMenuOpened(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && vm.Project != null)
                BaselineToggleItem.Header = vm.Project.BaselineActive ? AppStrings.Get("Main_DisableBaseline") : AppStrings.Get("Main_EnableBaseline");

            BaselineToggleItem.IsEnabled = DataContext is MainViewModel v && v.Project != null
                && BaselineService.HasBaseline(v.Project.FilePath ?? "");
        }

        private void OnBaselineToggleClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.Project == null) return;

            vm.Project.BaselineActive = !vm.Project.BaselineActive;
            vm.Project.IsDirty = true;

            if (vm.Project.BaselineActive)
            {
                // Reativar: recarrega baseline do .nxb
                var fp = vm.Project.FilePath;
                if (!string.IsNullOrWhiteSpace(fp))
                    BaselineService.Load(fp, vm.FlatTasks.Select(t => t.Model));
            }
            else
            {
                // Desativar: limpa os campos em memória mas NÃO apaga o .nxb
                foreach (var t in vm.FlatTasks)
                {
                    t.Model.BaselineStart  = null;
                    t.Model.BaselineFinish = null;
                    t.Model.BaselineHours  = null;
                }
            }

            BaselineToggleItem.Header = vm.Project.BaselineActive ? "Desativar Baseline" : "Ativar Baseline";
            GanttCtrl.ForceRender();
            vm.StatusMessage = vm.Project.BaselineActive ? "Baseline ativado." : "Baseline desativado.";
        }

        private void OnBaselineClearClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            var filePath = vm.Project?.FilePath;
            if (string.IsNullOrWhiteSpace(filePath)) return;

            var r = MessageBox.Show("Limpar o Baseline apaga o arquivo .nxb. Confirma?", "Baseline",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            BaselineService.Clear(filePath, vm.FlatTasks.Select(t => t.Model));
            GanttCtrl.ForceRender();
            vm.StatusMessage = "Baseline removido.";
        }

        private void OnPeopleClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            try
            {
                new PeopleWindow(vm) { Owner = this }.ShowDialog();
                GanttCtrl.ForceRender();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao abrir Pessoas:\n{ex.Message}", "Pessoas",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnPeopleCostClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            try
            {
                new PeopleWindow(vm, focusCost: true) { Owner = this }.ShowDialog();
                GanttCtrl.ForceRender();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnToolbarButtonClick(object sender, RoutedEventArgs e)
        {
            TaskGridCtrl.FocusSelectedTask();
        }

        private void OnLayoutToggleClick(object sender, RoutedEventArgs e)
        {
            ApplyLayoutMode(!_expandedLayout);
        }

        private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(TaskViewModel.PredecessorsText))
                GanttCtrl.ForceRender();

            if (args.PropertyName == nameof(TaskViewModel.Start) ||
                args.PropertyName == nameof(TaskViewModel.StartDisplay) ||
                args.PropertyName == nameof(TaskViewModel.Finish) ||
                args.PropertyName == nameof(TaskViewModel.FinishDisplay) ||
                args.PropertyName == nameof(TaskViewModel.DurationHours) ||
                args.PropertyName == nameof(TaskViewModel.PercentComplete))
            {
                if (DataContext is MainViewModel vm)
                {
                    ScheduleProjectPercentRefresh(vm);
                    UpdateEpicHours(vm);
                }
            }
        }

        private void ScheduleProjectPercentRefresh(MainViewModel vm)
        {
            _projectPercentRefreshCts?.Cancel();
            _projectPercentRefreshCts?.Dispose();

            var cts = new CancellationTokenSource();
            _projectPercentRefreshCts = cts;
            _ = RefreshProjectPercentAsync(vm, cts.Token);
        }

        private async System.Threading.Tasks.Task RefreshProjectPercentAsync(MainViewModel vm, CancellationToken cancellationToken)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(150, cancellationToken);

                var roots = vm.Project.Tasks.ToArray();
                var percent = await System.Threading.Tasks.Task.Run(
                    () => MainViewModel.CalculateProjectPercent(roots),
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                    return;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (cancellationToken.IsCancellationRequested || !ReferenceEquals(DataContext, vm))
                        return;

                    vm.ProjectPercent = percent;
                    UpdateEpicHours(vm);
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
            {
                if (ReferenceEquals(DataContext, vm))
                    _ = Dispatcher.InvokeAsync(() => ScheduleProjectPercentRefresh(vm), DispatcherPriority.Background);
            }
        }

        private void SubscribeTaskEvents(System.Collections.Generic.IEnumerable<TaskViewModel> tasks)
        {
            foreach (var task in tasks)
            {
                task.PropertyChanged -= OnTaskPropertyChanged;
                task.PropertyChanged += OnTaskPropertyChanged;
            }
        }

        private void OnCommunityWindowLoaded(object sender, RoutedEventArgs e)
        {
            var opts = Services.TfsConnectionStore.Load("NXProject.Community");
            BaselineAutoLoadItem.IsChecked = opts.AutoLoadBaseline;

            // Perfil desenvolvedor: abrir o TaskBoard automaticamente ao iniciar (opção da própria tela).
            if (NXProject.Views.TfsSprintWindow.ShouldAutoOpenTaskBoard())
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_licenseAccepted || HasAcceptedLicense()) OpenTaskBoard(silent: true);
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // Setup pediu para baixar a IA Local (LLaMA)? Abre o Gerenciar IA Local já baixando
            // (usa a pasta configurada nessa tela — mantém o path consistente). Após a licença.
            if (NXProject.CommunityApp.ConsumeInstallLlama())
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_licenseAccepted || HasAcceptedLicense())
                        try { new LocalAIManagerWindow(autoInstall: true) { Owner = this }.ShowDialog(); }
                        catch { /* download é opcional; não quebra a abertura */ }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            if (_licenseAccepted)
                return;

            if (HasAcceptedLicense())
            {
                _licenseAccepted = true;
                return;
            }

            _licenseAccepted = ShowLicenseDialog(requireAcceptance: true);
            if (!_licenseAccepted)
            {
                _allowClose = true;
                Application.Current.Shutdown();
                return;
            }
        }

        private bool ShowLicenseDialog(bool requireAcceptance)
        {
            var licenseWindow = new CommunityLicenseWindow
            {
                Owner = this,
                RequireAcceptance = requireAcceptance
            };

            var accepted = licenseWindow.ShowDialog() == true;
            if (!accepted && !requireAcceptance)
                return false;

            if (accepted && requireAcceptance)
                PersistLicenseAcceptance();

            return accepted;
        }

        private static bool HasAcceptedLicense()
        {
            return Services.LicenseAcceptanceStore.HasAccepted();
        }

        // Registra data e versao do app junto do aceite (o formato antigo, so com a
        // palavra "accepted", nao deixava rastro de quando nem de qual versao).
        private static void PersistLicenseAcceptance()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString();
            Services.LicenseAcceptanceStore.Persist(version);
        }

        private void OnCommunityWindowClosing(object? sender, CancelEventArgs e)
        {
            SaveWindowState(); // preserva tamanho/posição/maximizado para a próxima sessão

            if (_allowClose)
                return;

            if (DataContext is not MainViewModel vm)
                return;

            if (!vm.Project.IsDirty)
                return;

            var decision = MessageBox.Show(
                "O projeto possui alteracoes nao salvas. Deseja salvar antes de fechar?",
                "Salvar projeto",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (decision == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (decision == MessageBoxResult.Yes)
            {
                vm.SaveProjectCommand.Execute(null);
                if (vm.Project.IsDirty)
                {
                    e.Cancel = true;
                    return;
                }
            }

            _allowClose = true;
        }

        private void OpenAiAssistantOnFirstAccess()
        {
            if (_aiOpenedOnFirstAccess || DataContext is not MainViewModel vm)
                return;

            if (WasAiAutoOpenedToday())
                return;

            _aiOpenedOnFirstAccess = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (!IsLoaded || !IsVisible)
                    return;

                var aiWindow = new CommunityAIWindow(vm)
                {
                    Owner = this
                };
                aiWindow.ShowDialog();
                PersistAiLastOpenedDate();
            }));
        }

        private static bool WasAiAutoOpenedToday()
        {
            if (!File.Exists(AiLastOpenedFile))
                return false;

            var content = File.ReadAllText(AiLastOpenedFile).Trim();
            return DateOnly.TryParse(content, out var lastOpenedDate) &&
                   lastOpenedDate == DateOnly.FromDateTime(DateTime.Today);
        }

        private static void PersistAiLastOpenedDate()
        {
            Directory.CreateDirectory(LicenseAcceptanceDirectory);
            File.WriteAllText(
                AiLastOpenedFile,
                DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"));
        }

        private void ApplyLayoutMode(bool expanded)
        {
            _expandedLayout = expanded;

            double taskW = expanded ? 920 : 660;
            TaskPaneColumn.Width = new GridLength(taskW);
            // MinWidth impede que o splitter esprema as colunas; o Border ClipToBounds corta o excesso
            TaskGridCtrl.MinWidth = taskW;

            GanttPaneColumn.Width = new GridLength(1, GridUnitType.Star);

            var vm2 = DataContext as MainViewModel;
            TaskGridCtrl.SetPresentationMode(expanded, vm2?.HiddenColumns ?? "", vm2?.HiddenColumnsExpanded ?? "");
            LayoutToggleText.Text = expanded ? "⤡" : "⤢";
            LayoutToggleButton.ToolTip = expanded
                ? "Voltar para a visualização compacta"
                : "Abrir a tabela com colunas mais legíveis";

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                GanttCtrl.ForceRender();
            }));
        }

        // ── Busca de atividade ────────────────────────────────────────────────

        private sealed class TaskSearchItem
        {
            public TaskSearchItem(NXProject.ViewModels.TaskViewModel vm)
            {
                Task       = vm;
                TaskType   = vm.Model.TfsType?.Trim() ?? "";
                Id         = vm.DisplayId ?? "";
                Name       = vm.Name;
                Resources  = string.Join(", ", vm.Model.Resources
                    .Where(r => r.Resource != null)
                    .Select(r => r.Resource!.DisplayName)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                Percent    = $"{(int)Math.Round(vm.Model.PercentComplete)}%";
            }
            public NXProject.ViewModels.TaskViewModel Task      { get; }
            public string TaskType  { get; }
            public string Id        { get; }
            public string Name      { get; }
            public string Resources { get; }
            public string Percent   { get; }
        }

        private void OnSearchTaskClick(object sender, RoutedEventArgs e) => OpenSearchPopup();

        private void OpenSearchPopup()
        {
            if (DataContext is not MainViewModel vm) return;

            var popup = new Window
            {
                Title                 = "Buscar atividade",
                Width                 = 700,
                Height                = 400,
                MinWidth              = 540,
                MinHeight             = 260,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                Background            = System.Windows.Media.Brushes.White,
                ResizeMode            = ResizeMode.CanResize,
                ShowInTaskbar         = false
            };

            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var searchBox = new TextBox
            {
                Height          = 30,
                FontSize        = 13,
                Padding         = new Thickness(6, 4, 6, 4),
                Margin          = new Thickness(0, 0, 0, 6),
                BorderBrush     = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 87, 154)),
                BorderThickness = new Thickness(1.5)
            };
            Grid.SetRow(searchBox, 0);
            root.Children.Add(searchBox);

            var listBox = new DataGrid
            {
                FontSize                = 12,
                BorderThickness         = new Thickness(1),
                BorderBrush             = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 218, 230)),
                AutoGenerateColumns     = false,
                CanUserAddRows          = false,
                CanUserDeleteRows       = false,
                CanUserReorderColumns   = false,
                CanUserSortColumns      = false,
                IsReadOnly              = true,
                RowHeight               = 26,
                SelectionMode           = DataGridSelectionMode.Single,
                SelectionUnit           = DataGridSelectionUnit.FullRow,
                GridLinesVisibility     = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 235, 245)),
                HeadersVisibility       = DataGridHeadersVisibility.Column,
            };
            listBox.Columns.Add(new DataGridTextColumn { Header = "Tipo",    Binding = new System.Windows.Data.Binding(nameof(TaskSearchItem.TaskType)), Width = 80 });
            listBox.Columns.Add(new DataGridTextColumn { Header = "ID",      Binding = new System.Windows.Data.Binding(nameof(TaskSearchItem.Id)),       Width = 60 });
            listBox.Columns.Add(new DataGridTextColumn { Header = "Nome",    Binding = new System.Windows.Data.Binding(nameof(TaskSearchItem.Name)),     Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            listBox.Columns.Add(new DataGridTextColumn { Header = "Recurso", Binding = new System.Windows.Data.Binding(nameof(TaskSearchItem.Resources)),Width = 140 });
            listBox.Columns.Add(new DataGridTextColumn { Header = "%",       Binding = new System.Windows.Data.Binding(nameof(TaskSearchItem.Percent)),  Width = 46 });
            Grid.SetRow(listBox, 1);
            root.Children.Add(listBox);

            var countLabel = new TextBlock
            {
                FontSize   = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100)),
                Margin     = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(countLabel, 2);
            root.Children.Add(countLabel);

            void Refresh(string text)
            {
                var terms = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var results = vm.FlatTasks
                    .Where(t =>
                    {
                        if (terms.Length == 0) return true;
                        var haystack = $"{t.DisplayId} {t.Name}";
                        return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
                    })
                    .Take(200)
                    .Select(t => new TaskSearchItem(t))
                    .ToList();
                listBox.ItemsSource = results;
                countLabel.Text = results.Count == 0 && terms.Length > 0
                    ? "Nenhuma atividade encontrada."
                    : $"{results.Count} atividade(s)";
            }

            void SelectAndClose(TaskSearchItem? item)
            {
                if (item == null) return;
                popup.Close();
                vm.SelectedTask = item.Task;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => TaskGridCtrl.ScrollToSelected());
            }

            Refresh("");
            searchBox.TextChanged    += (_, _) => Refresh(searchBox.Text);
            listBox.MouseDoubleClick += (_, _) => SelectAndClose(listBox.SelectedItem as TaskSearchItem);
            listBox.KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                    SelectAndClose(listBox.SelectedItem as TaskSearchItem);
            };
            searchBox.KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                    SelectAndClose(listBox.SelectedItem as TaskSearchItem
                                   ?? listBox.Items.OfType<TaskSearchItem>().FirstOrDefault());
                else if (e.Key == System.Windows.Input.Key.Down)
                {
                    listBox.Focus();
                    if (listBox.Items.Count > 0)
                    {
                        listBox.SelectedIndex = 0;
                        listBox.ScrollIntoView(listBox.Items[0]);
                    }
                    e.Handled = true;
                }
                else if (e.Key == System.Windows.Input.Key.Escape)
                    popup.Close();
            };

            popup.Content = root;
            popup.Loaded  += (_, _) => searchBox.Focus();
            popup.ShowDialog();
        }
    }
}
