using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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

        private static readonly string AiLastOpenedFile =
            Path.Combine(LicenseAcceptanceDirectory, "ai.last-opened.txt");

        private bool _licenseAccepted;
        private bool _allowClose;
        private bool _aiOpenedOnFirstAccess;
        private bool _expandedLayout;

        public CommunityMainWindow()
        {
            InitializeComponent();
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null)
                Title = $"NXProject Community {v.Major}.{v.Minor}.{v.Build} build({v.Revision})";
            ProjectCalendarService.Load("NXProject.Community");
            StatusLogoImage.Source = ProtectedLogoProvider.GetLogoImage();
            var vm = new MainViewModel("NXProject.Community");
            DataContext = vm;

            // Atualiza o banner quando um projeto é aberto/carregado ou FlatTasks muda
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.Project))
                {
                    UpdateDevOpsProjectBanner(vm.Project.DevOpsProjectName, vm.Project.DevOpsRootWorkItemId);
                    vm.Project.ShowCriticalPath = true;
                    Dispatcher.InvokeAsync(() => RefreshCriticalPath(vm),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
            };
            vm.FlatTasks.CollectionChanged += (_, _) => UpdateEpicHours(vm);
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.FlatTasks) || args.PropertyName == "ProjectPercent")
                    UpdateEpicHours(vm);
            };

            LanguageService.LanguageChanged += () => TaskGridCtrl.RefreshColumnHeaders();

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
            TaskGridCtrl.FetchTaskHoursRequested += OnFetchTaskHoursFromDevOps;
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
                        ZoomLabel.Text = vm.SelectedZoom;
                        GanttCtrl.ZoomLevel = vm.SelectedZoom;
                        GanttCtrl.ForceRender();
                        GanttCtrl.ScrollToProjectStart();
                    });
                }
            };

            ZoomLabel.Text = vm.SelectedZoom;

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

        private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
        {
            try
            {
                IsEnabled = false;
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

        private void OnOpenSelectedTaskInDevOpsClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            if (vm.SelectedTask?.Model is { } m)
                OpenTaskInDevOps(m);
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
            var win = new TaskDescriptionEditWindow(task.Model) { Owner = this };
            if (win.ShowDialog() == true)
                vm.Project.IsDirty = true;
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
                MessageBox.Show($"Erro ao buscar Tasks no DevOps:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnFetchChildTasksFromDevOps(TaskViewModel storyVm) =>
            OpenTaskReviewForStory(storyVm);

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
                vm.Project.IsDirty = true;
                vm.RebuildFlatTasks();
                GanttCtrl.ForceRender();
            }
        }

        private NXProject.Models.ProjectTask? AddTaskRowsToSchedule(
            IEnumerable<TaskReviewRow> rows,
            NXProject.Models.ProjectTask story,
            MainViewModel vm)
        {
            var projectResources = vm.Project.Resources;
            var existingIds = story.Children
                .Where(c => string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase) && c.TfsId.HasValue)
                .Select(c => c.TfsId!.Value).ToHashSet();

            NXProject.Models.ProjectTask? firstAdded = null;
            int nextId = vm.FlatTasks.Count > 0
                ? vm.FlatTasks.Max(t => t.Model.Id) + 1
                : 1;
            foreach (var r in rows)
            {
                if (existingIds.Contains(r.TaskId)) continue;
                var pt = new NXProject.Models.ProjectTask
                {
                    Id               = nextId++,
                    Name             = r.Title,
                    TfsId            = r.TaskId,
                    TfsType          = "Task",
                    EstimatedHours   = r.EstimatedHours > 0 ? r.EstimatedHours : null,
                    CurrentHours     = r.CompletedHours > 0 ? r.CompletedHours : null,
                    PercentComplete  = r.PercentComplete,
                    Priority         = r.Priority > 0 ? r.Priority : 5,
                    TfsState         = r.State,
                    TfsIterationPath = story.TfsIterationPath,
                    SprintNumber     = story.SprintNumber,
                    Start            = story.Start,
                    Finish           = story.Finish,
                };
                var display = r.AssignedToDisplay;
                var email   = r.AssignedTo;
                if (!string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(display))
                {
                    var res = projectResources.FirstOrDefault(x =>
                        string.Equals(x.Email, email,   StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Name,  email,   StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Name,  display, StringComparison.OrdinalIgnoreCase));
                    if (res != null)
                        pt.Resources.Add(new NXProject.Models.TaskResource { ResourceId = res.Id, Resource = res, AllocationPercent = 100 });
                }
                story.Children.Add(pt);
                story.TasksSuppressed = false;
                firstAdded ??= pt;
            }

            if (firstAdded != null)
            {
                // Recalcula TKs da story com base nos filhos do tipo Task
                story.DevopsTaskCount = story.Children.Count(c =>
                    string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase));
                vm.Project.IsDirty = true;
                vm.RebuildFlatTasks();
                GanttCtrl.ForceRender();
            }
            return firstAdded;
        }

        private void ReleaseStoryTasks(NXProject.Models.ProjectTask story, MainViewModel vm)
        {
            var tasks = story.Children
                .Where(c => string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var t in tasks) story.Children.Remove(t);
            story.TasksSuppressed = false;
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
            bool canDeleteInDevOps = hasDevOpsId && isStory;

            // Tipo com ID real mas não é Story (Epic/Feature): não pode excluir aqui, oferece abrir no DevOps
            if (hasDevOpsId && !isStory)
            {
                var result = MessageBox.Show(
                    LanguageService.Str("Delete_ProtectedMsg", task.Name, task.Model.TfsType ?? ""),
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
                    MessageBox.Show($"Erro ao excluir no DevOps:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
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

            var options = Services.TfsConnectionStore.Load("NXProject.Community");
            if (string.IsNullOrWhiteSpace(options.OrganizationUrl) ||
                string.IsNullOrWhiteSpace(options.TeamProject) ||
                string.IsNullOrWhiteSpace(options.PersonalAccessToken))
            {
                MessageBox.Show(
                    "Configure a conexão e marque \"Lembrar o token\" primeiro em Arquivo → Importar → TFS / Azure DevOps.",
                    "Sincronizar TFS/DevOps", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                "Isto vai atualizar os work items vinculados no DevOps (título/descrição, horas, e datas conforme as regras). Continuar?",
                "Sincronizar TFS/DevOps", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
                return;

            if (!ConfirmKnownTfsResources(vm))
                return;

            if (!ConfirmInitialLoadCompletedHours(vm))
                return;

            if (!ConfirmCompletedTfsState(vm))
                return;

            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            Services.TfsImportService.SyncReport? report = null;
            try
            {
                report = await Services.TfsImportService.SyncAsync(vm.Project, options);
            }
            catch (Exception ex)
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
                MessageBox.Show(
                    $"Erro ao sincronizar:\n{ex.Message}\n\nTipo: {ex.GetType().Name}\n\n{ex.StackTrace}",
                    "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
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

            // Garante que grid e Gantt reflitam conflitos após fechar o log.
            GanttCtrl.ForceRender();
            TaskGridCtrl.RefreshRows();
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
            new TfsDevOpsConfigWindow("NXProject.Community") { Owner = this }.ShowDialog();
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
                UpdateDevOpsProjectBanner(project.DevOpsProjectName, project.DevOpsRootWorkItemId);
            }
        }

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

            // Usa FlatTasks (depth=0) para ter o DurationHours correto (SumTaskHours)
            var epicHours = vm.FlatTasks
                .Where(t => t.Depth == 0 && t.IsSummary)
                .Sum(t => t.DurationHours);

            DevOpsEpicHoursLabel.Text = epicHours > 0
                ? $"| {epicHours:0.#} HH (Epics)"
                : string.Empty;

            // % de conclusão: média ponderada pelas horas de todas as atividades folha
            var leaves = vm.FlatTasks.Where(t => !t.IsSummary && !t.IsMilestone).ToList();
            if (leaves.Count > 0)
            {
                double totalW = leaves.Sum(t => t.DurationHours);
                double pct = totalW > 0
                    ? leaves.Sum(t => t.DurationHours * t.PercentComplete) / totalW
                    : leaves.Average(t => t.PercentComplete);
                DevOpsPercentLabel.Text = $"| {pct:0.#}% concluído";
            }
            else
            {
                DevOpsPercentLabel.Text = string.Empty;
            }
        }

        private void UpdateDevOpsProjectBanner(string? name, int id)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                DevOpsProjectNameLabel.Text = name;
                DevOpsProjectIdLabel.Text = id > 0 ? $"(ID: {id})" : string.Empty;
                DevOpsProjectBanner.Visibility = Visibility.Visible;
            }
            else if (id > 0)
            {
                DevOpsProjectNameLabel.Text = $"ID {id}";
                DevOpsProjectIdLabel.Text = string.Empty;
                DevOpsProjectBanner.Visibility = Visibility.Visible;
            }
            else
            {
                DevOpsEpicHoursLabel.Text = string.Empty;
                DevOpsProjectBanner.Visibility = Visibility.Collapsed;
            }
        }

        private void OnSprintSettingsClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            new SprintManagerWindow(vm) { Owner = this }.ShowDialog();
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
            ZoomLabel.Text = zoom;
            GanttCtrl.ZoomLevel = zoom;

            // Dia, Semana, Trimestre e Semestre mostram header por dia; Sprint e Mês usam view por sprint
            bool dayMode = zoom is "Dia" or "Semana" or "Trimestre" or "Semestre";
            int currentMode = GanttCtrl.DayHeaderMode;
            int newMode = dayMode ? (currentMode == 0 ? 1 : currentMode) : 0;
            ApplyDayHeaderMode(newMode);

            GanttCtrl.ForceRender();
        }

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

        private void OnDayHeaderToggled(object sender, RoutedEventArgs e)
        {
            // Cicla: 0 (off) → 1 (dia1: seg/qua/sex) → 2 (dia2: dígito compacto) → 0
            int next = (GanttCtrl.DayHeaderMode + 1) % 3;
            ApplyDayHeaderMode(next);
        }

        private void ApplyDayHeaderMode(int mode)
        {
            GanttCtrl.DayHeaderMode = mode;
            DayHeaderToggle.IsChecked = mode > 0;

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
                        Text = cursor.ToString("MMM/yy"),
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
            var control = new Controls.CalendarSettingsControl("NXProject.Community");
            var window = new Window
            {
                Title = "Calendario de trabalho",
                Owner = this,
                Width = 720,
                Height = 520,
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
            new ProjectAllocationMapWindow() { Owner = this }.ShowDialog();
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
            new CriticalPathWindow(AllTasksFlat(vm)) { Owner = this }.ShowDialog();
        }

        private void RefreshCriticalPath(MainViewModel vm)
        {
            if (vm.Project?.ShowCriticalPath == true)
            {
                var entries = NXProject.Services.CriticalPathService.Compute(AllTasksFlat(vm));
                var ids = entries
                    .Where(e => e.TotalFloat < 0.5)
                    .Select(e => e.Task.Id)
                    .ToHashSet();
                GanttCtrl.ShowCriticalPath = true;
                GanttCtrl.CriticalTaskIds  = ids;
            }
            else
            {
                GanttCtrl.ShowCriticalPath = false;
                GanttCtrl.CriticalTaskIds  = null;
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
                BaselineToggleItem.Header = vm.Project.BaselineActive ? "Desativar Baseline" : "Ativar Baseline";

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
            return File.Exists(LicenseAcceptanceFile);
        }

        private static void PersistLicenseAcceptance()
        {
            Directory.CreateDirectory(LicenseAcceptanceDirectory);
            File.WriteAllText(LicenseAcceptanceFile, "accepted");
        }

        private void OnCommunityWindowClosing(object? sender, CancelEventArgs e)
        {
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
