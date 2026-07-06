using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class TechLeadTaskReviewWindow : Window
    {
        private readonly Project _project;
        private List<ProjectTask> _stories;
        private readonly List<string> _activityList;
        private readonly ObservableCollection<TaskReviewRow> _allRows = [];
        private ICollectionView? _view;
        private static readonly List<string> KnownStates = ["New", "Active", "Resolved", "Closed", "Blocked"];

        // Modo cascata (ícone do menu): seleção Epic→Feature→Story antes de buscar
        private readonly bool _cascadeMode;
        private List<ProjectTask> _epicTaskList = [];
        private List<ProjectTask> _featureTaskList = [];
        private List<ProjectTask> _storyTaskList = [];

        // Drag-drop
        private Point _dragStart;
        private TaskReviewRow? _dragRow;
        private bool _isDragging;

        public bool HasChanges { get; private set; }
        public IReadOnlyList<TaskReviewRow> CurrentRows => _allRows.ToList();
        public List<string> ActivityList => _activityList;
        public List<string> ResourceList => _project.Resources.Select(r => r.DisplayName ?? r.Name ?? "").Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        /// <summary>Callback: adiciona rows ao cronograma e retorna a primeira ProjectTask adicionada (para seleção).</summary>
        public Func<IEnumerable<TaskReviewRow>, ProjectTask?>? AddToScheduleCallback { get; set; }
        public Action? ReleaseCallback { get; set; }
        private bool _memorize;

        // Chamado pelo botão direito na Story — pré-seleciona e carrega direto
        public TechLeadTaskReviewWindow(Project project, List<ProjectTask> stories, List<string>? activityList = null)
        {
            _project = project;
            _activityList = activityList ?? ["Deployment", "Design", "Development", "Documentation", "Requirements", "Testing"];
            _stories = stories;
            _cascadeMode = false;
            InitializeComponent();
            Closing += OnWindowClosing;
            Loaded += async (_, _) => await LoadAsync();
            Loaded += (_, _) =>
            {
                ReleaseButton.Visibility     = ReleaseCallback       != null ? Visibility.Visible : Visibility.Collapsed;
                ExpandAllButton.Visibility   = AddToScheduleCallback != null ? Visibility.Visible : Visibility.Collapsed;
                AddSelectedButton.Visibility = AddToScheduleCallback != null ? Visibility.Visible : Visibility.Collapsed;
                UpdateTaskActionButtons();
            };
        }

        // Chamado pelo ícone do menu — seleção em cascata Epic→Feature→Story
        public TechLeadTaskReviewWindow(Project project, List<string>? activityList = null)
        {
            _project = project;
            _activityList = activityList ?? ["Deployment", "Design", "Development", "Documentation", "Requirements", "Testing"];
            _stories = [];
            _cascadeMode = true;
            InitializeComponent();
            Closing += OnWindowClosing;
            Loaded += (_, _) => InitCascadeMode();
        }

        // Fechar de qualquer forma (X, Esc, Alt+F4 ou clique em botão) libera por
        // padrão — remove as Tasks memorizadas no cronograma local. Só o botão
        // "Memorizar Task no Cronograma" evita isso, marcando _memorize antes de fechar.
        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_memorize) return;
            ReleaseCallback?.Invoke();
            HasChanges = true;
        }

        private void InitCascadeMode()
        {
            BuscarButton.Visibility = Visibility.Visible;

            _epicTaskList = FlattenAll(_project.Tasks)
                .Where(t => string.Equals(t.TfsType, "Epic", StringComparison.OrdinalIgnoreCase) && t.TfsId is > 0)
                .ToList();

            EpicFilterBox.ItemsSource = new[] { "(Todos)" }.Concat(FilterByProgress(_epicTaskList).Select(TaskLabel)).ToList();
            EpicFilterBox.SelectedIndex = 0;

            FeatureFilterBox.ItemsSource = new[] { "(Todas)" };
            FeatureFilterBox.SelectedIndex = 0;
            FeatureFilterBox.IsEnabled = false;

            StoryFilterBox.ItemsSource = new[] { "(Todas)" };
            StoryFilterBox.SelectedIndex = 0;
            StoryFilterBox.IsEnabled = false;

            BuscarButton.IsEnabled = false;
            StatusText.Text = AppStrings.Get("TLR_SelectAndSearch");
        }

        private static string TaskLabel(ProjectTask t) => $"{t.Name ?? ""} ({(int)Math.Round(t.PercentComplete)}%)";
        private static string StripLabel(string? label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            var idx = label.LastIndexOf(" (");
            return idx > 0 ? label[..idx] : label;
        }
        private IEnumerable<ProjectTask> FilterByProgress(IEnumerable<ProjectTask> tasks)
        {
            bool emAndamento = FilterEmAndamentoBox?.IsChecked == true;
            bool naoIniciada = FilterNaoIniciadaBox?.IsChecked == true;
            if (!emAndamento && !naoIniciada) return tasks;
            return tasks.Where(t =>
                (emAndamento && t.PercentComplete > 0 && t.PercentComplete < 100) ||
                (naoIniciada && t.PercentComplete <= 0));
        }

        private static IEnumerable<ProjectTask> FlattenAll(System.Collections.ObjectModel.ObservableCollection<ProjectTask> tasks)
        {
            foreach (var t in tasks) { yield return t; foreach (var c in FlattenAll(t.Children)) yield return c; }
        }

        private static bool IsDescendantOf(ProjectTask task, ProjectTask ancestor)
        {
            var p = task.Parent;
            while (p != null) { if (ReferenceEquals(p, ancestor)) return true; p = p.Parent; }
            return false;
        }

        private async Task LoadAsync()
        {
            StatusText.Text = AppStrings.Get("TLR_Searching");
            AddSelectedButton.IsEnabled = false;
            SaveChangesButton.IsEnabled = false;
            UpdateTaskActionButtons();

            var options = TfsConnectionStore.Load("NXProject.Community");
            var rows = new List<TaskReviewRow>();

            var inScheduleIds = _stories
                .SelectMany(s => s.Children)
                .Where(c => string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase) && c.TfsId.HasValue)
                .Select(c => c.TfsId!.Value)
                .ToHashSet();

            int fetched = 0;
            foreach (var story in _stories)
            {
                if (story.TfsId is not > 0) continue;
                var tasks = await TfsImportService.FetchChildTasksFromDevOpsAsync(options, story.TfsId!.Value);
                if (tasks == null) continue;
                fetched++;
                foreach (var t in tasks)
                {
                    var row = new TaskReviewRow
                    {
                        StoryId         = story.TfsId!.Value,
                        StoryName       = story.Name,
                        FeatureName     = story.Parent?.Name ?? "",
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
                        Activity          = t.Activity ?? "",
                        Tags              = t.Tags ?? "",
                        InSchedule        = inScheduleIds.Contains(t.TfsId),
                    };
                    row.PropertyChanged += OnRowPropertyChanged;
                    rows.Add(row);
                }
            }

            // Atualiza contagem de TKs por story (para coluna TKs no cronograma)
            foreach (var story in _stories)
                story.DevopsTaskCount = rows.Count(r => r.StoryId == story.TfsId);

            _allRows.Clear();
            foreach (var r in rows) _allRows.Add(r);

            if (!_cascadeMode)
                PopulateDirectModeFilters(rows);

            var states = new[] { "(Todos)" }.Concat(rows.Select(r => r.State).Distinct().OrderBy(s => s)).ToList();
            StateFilterBox.ItemsSource = states;
            StateFilterBox.SelectedIndex = 0;

            _view = CollectionViewSource.GetDefaultView(_allRows);
            _view.Filter = ApplyFilter;
            _view.SortDescriptions.Clear();
            _view.SortDescriptions.Add(new SortDescription(nameof(TaskReviewRow.Priority), ListSortDirection.Ascending));
            TasksGrid.ItemsSource = _view;
            RefreshRowNumbers();

            // Preenche duração da story: OriginalEstimatedHours → EstimatedHours → horas de calendário
            if (_stories.Count > 0)
            {
                var s = _stories[0];
                double h = s.OriginalEstimatedHours ?? s.EstimatedHours
                    ?? NXProject.Services.ProjectCalendarService.CountWorkingHours(s.Start, s.Finish);
                StoryDurationBox.Text = (h > 0 ? h : 0).ToString("0.#");
            }

            UpdateTotals();
            UpdateTaskActionButtons();
            StatusText.Text = $"{fetched} Stories consultadas — {rows.Count} Tasks encontradas no DevOps.";
        }

        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TaskReviewRow.IsDirty)) return;
            if (sender is TaskReviewRow row && e.PropertyName != nameof(TaskReviewRow.IsSelected)
                                             && e.PropertyName != nameof(TaskReviewRow.InSchedule))
            {
                row.IsDirty = true;
                SaveChangesButton.IsEnabled = _allRows.Any(r => r.IsDirty);
                DirtyHint.Visibility = SaveChangesButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateTotals();
        }

        private bool ApplyFilter(object obj)
        {
            if (obj is not TaskReviewRow r) return true;
            var stateFilter = StateFilterBox.SelectedItem as string;
            if (!string.IsNullOrEmpty(stateFilter) && stateFilter != "(Todos)" && r.State != stateFilter) return false;
            if (!_cascadeMode)
            {
                var featureFilter = FeatureFilterBox.SelectedItem as string;
                var storyFilter   = StoryFilterBox.SelectedItem as string;
                if (!string.IsNullOrEmpty(featureFilter) && featureFilter != "(Todas)" && r.FeatureName != featureFilter) return false;
                if (!string.IsNullOrEmpty(storyFilter)   && storyFilter   != "(Todas)" && r.StoryName   != storyFilter)  return false;
            }
            bool emAndamento  = FilterEmAndamentoBox?.IsChecked == true;
            bool naoIniciada  = FilterNaoIniciadaBox?.IsChecked == true;
            if (emAndamento || naoIniciada)
            {
                bool matchEmAndamento = r.PercentComplete > 0 && r.PercentComplete < 100;
                bool matchNaoIniciada = r.PercentComplete <= 0;
                if (emAndamento && naoIniciada)
                {
                    if (!matchEmAndamento && !matchNaoIniciada) return false;
                }
                else if (emAndamento && !matchEmAndamento) return false;
                else if (naoIniciada && !matchNaoIniciada) return false;
            }
            return true;
        }

        private void OnProgressFilterChanged(object sender, RoutedEventArgs e)
        {
            if (_cascadeMode && _epicTaskList?.Count > 0)
            {
                EpicFilterBox.ItemsSource = new[] { "(Todos)" }.Concat(FilterByProgress(_epicTaskList).Select(TaskLabel)).ToList();
                EpicFilterBox.SelectedIndex = 0;
                FeatureFilterBox.ItemsSource = new[] { "(Todas)" };
                FeatureFilterBox.SelectedIndex = 0;
                FeatureFilterBox.IsEnabled = false;
                StoryFilterBox.ItemsSource = new[] { "(Todas)" };
                StoryFilterBox.SelectedIndex = 0;
                StoryFilterBox.IsEnabled = false;
            }
            else
            {
                _view?.Refresh(); RefreshRowNumbers(); UpdateTotals();
            }
        }

        private void UpdateTotals()
        {
            var visible = _view?.Cast<TaskReviewRow>().ToList() ?? [.. _allRows];
            double totalH = visible.Sum(r => r.EstimatedHours);
            double doneH  = visible.Sum(r => r.CompletedHours);
            int inSched   = visible.Count(r => r.InSchedule);
            int dirty     = _allRows.Count(r => r.IsDirty);
            TotalsText.Text = AppStrings.Get("TLR_Totals", visible.Count, totalH, doneH, inSched) +
                              (dirty > 0 ? AppStrings.Get("TLR_TotalsDirty", dirty) : "");
        }

        private void RefreshRowNumbers()
        {
            var visible = (_view?.Cast<TaskReviewRow>() ?? _allRows).ToList();
            for (int i = 0; i < visible.Count; i++)
                visible[i].RowNumber = i + 1;
        }

        private void PopulateDirectModeFilters(List<TaskReviewRow> rows)
        {
            var selectedStory = _stories.Count == 1 ? _stories[0] : null;
            var selectedFeature = selectedStory?.Parent;
            var selectedEpic = selectedFeature != null
                ? FindAncestorOrSelf(selectedFeature, "Epic")
                : selectedStory != null
                    ? FindAncestorOrSelf(selectedStory, "Epic")
                    : null;

            var epics = FlattenAll(_project.Tasks)
                .Where(t => string.Equals(t.TfsType, "Epic", StringComparison.OrdinalIgnoreCase) && t.TfsId is > 0)
                .Select(t => t.Name)
                .Concat(selectedEpic?.Name is { Length: > 0 } epicName ? [epicName] : [])
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            var features = rows.Select(r => r.FeatureName)
                .Concat(selectedFeature?.Name is { Length: > 0 } featureName ? [featureName] : [])
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct()
                .OrderBy(f => f)
                .ToList();

            var stories = rows.Select(r => r.StoryName)
                .Concat(_stories.Select(s => s.Name))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            EpicFilterBox.ItemsSource = new[] { "(Todos)" }.Concat(epics).ToList();
            EpicFilterBox.SelectedItem = selectedEpic?.Name is { Length: > 0 } && epics.Contains(selectedEpic.Name)
                ? selectedEpic.Name
                : "(Todos)";

            FeatureFilterBox.ItemsSource = new[] { "(Todas)" }.Concat(features).ToList();
            FeatureFilterBox.SelectedItem = selectedFeature?.Name is { Length: > 0 } && features.Contains(selectedFeature.Name)
                ? selectedFeature.Name
                : "(Todas)";

            StoryFilterBox.ItemsSource = new[] { "(Todas)" }.Concat(stories).ToList();
            StoryFilterBox.SelectedItem = selectedStory?.Name is { Length: > 0 } && stories.Contains(selectedStory.Name)
                ? selectedStory.Name
                : "(Todas)";
        }

        private static ProjectTask? FindAncestorOrSelf(ProjectTask task, string tfsType)
        {
            var current = task;
            while (current != null)
            {
                if (string.Equals(current.TfsType, tfsType, StringComparison.OrdinalIgnoreCase))
                    return current;
                current = current.Parent;
            }

            return null;
        }

        private void OnEpicFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_cascadeMode) { _view?.Refresh(); RefreshRowNumbers(); UpdateTotals(); return; }

            var epicName = EpicFilterBox.SelectedItem as string;
            var selectedEpic = epicName != "(Todos)" ? _epicTaskList.FirstOrDefault(ep => ep.Name == StripLabel(epicName)) : null;

            _featureTaskList = FlattenAll(_project.Tasks)
                .Where(t => string.Equals(t.TfsType, "Feature", StringComparison.OrdinalIgnoreCase) && t.TfsId is > 0)
                .Where(t => selectedEpic == null || IsDescendantOf(t, selectedEpic) || ReferenceEquals(t.Parent, selectedEpic))
                .ToList();

            FeatureFilterBox.ItemsSource = new[] { "(Todas)" }.Concat(FilterByProgress(_featureTaskList).Select(TaskLabel)).ToList();
            FeatureFilterBox.SelectedIndex = 0;
            FeatureFilterBox.IsEnabled = true;

            StoryFilterBox.ItemsSource = new[] { "(Todas)" };
            StoryFilterBox.SelectedIndex = 0;
            StoryFilterBox.IsEnabled = false;

            BuscarButton.IsEnabled = true;
        }

        private void OnFeatureFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_cascadeMode)
            {
                // Modo normal: refiltra stories disponíveis nas rows carregadas
                var featureFilter = FeatureFilterBox.SelectedItem as string;
                var filtered = string.IsNullOrEmpty(featureFilter) || featureFilter == "(Todas)"
                    ? _allRows.Select(r => r.StoryName)
                    : _allRows.Where(r => r.FeatureName == featureFilter).Select(r => r.StoryName);
                var storyNames = new[] { "(Todas)" }.Concat(filtered.Distinct().OrderBy(s => s)).ToList();
                StoryFilterBox.ItemsSource = storyNames;
                StoryFilterBox.SelectedIndex = 0;
                _view?.Refresh(); RefreshRowNumbers(); UpdateTotals();
                return;
            }

            // Modo cascata: popula Story combo a partir da Feature selecionada
            var featureName = FeatureFilterBox.SelectedItem as string;
            var selectedFeature = featureName != "(Todas)" ? _featureTaskList.FirstOrDefault(f => f.Name == StripLabel(featureName)) : null;
            var epicName = EpicFilterBox.SelectedItem as string;
            var selectedEpic = epicName != "(Todos)" ? _epicTaskList.FirstOrDefault(ep => ep.Name == StripLabel(epicName)) : null;

            _storyTaskList = FlattenAll(_project.Tasks)
                .Where(t => (TfsImportService.IsStoryTypePublic(t.TfsType) ||
                             string.Equals(t.TfsType, "Feature", StringComparison.OrdinalIgnoreCase)) && t.TfsId is > 0)
                .Where(t =>
                {
                    if (selectedFeature != null) return IsDescendantOf(t, selectedFeature) || ReferenceEquals(t.Parent, selectedFeature);
                    if (selectedEpic != null) return IsDescendantOf(t, selectedEpic) || ReferenceEquals(t.Parent, selectedEpic);
                    return true;
                })
                .ToList();

            StoryFilterBox.ItemsSource = new[] { "(Todas)" }.Concat(FilterByProgress(_storyTaskList).Select(TaskLabel)).ToList();
            StoryFilterBox.SelectedIndex = 0;
            StoryFilterBox.IsEnabled = true;
            BuscarButton.IsEnabled = true;
        }

        private void OnStoryFilterChanged(object sender, SelectionChangedEventArgs e) { _view?.Refresh(); RefreshRowNumbers(); UpdateTotals(); }
        private void OnStateFilterChanged(object sender, SelectionChangedEventArgs e) { _view?.Refresh(); RefreshRowNumbers(); UpdateTotals(); }

        private async void OnBuscarClick(object sender, RoutedEventArgs e)
        {
            var storyName   = StoryFilterBox.SelectedItem as string;
            var featureName = FeatureFilterBox.SelectedItem as string;
            var epicName    = EpicFilterBox.SelectedItem as string;

            if (storyName != null && storyName != "(Todas)")
                _stories = [.. _storyTaskList.Where(s => s.Name == StripLabel(storyName))];
            else if (featureName != null && featureName != "(Todas)")
                _stories = _storyTaskList;
            else if (epicName != null && epicName != "(Todos)")
                _stories = _storyTaskList;
            else
                _stories = FlattenAll(_project.Tasks)
                    .Where(t => (TfsImportService.IsStoryTypePublic(t.TfsType) ||
                                 string.Equals(t.TfsType, "Feature", StringComparison.OrdinalIgnoreCase)) && t.TfsId is > 0)
                    .ToList();

            if (_stories.Count == 0)
            {
                StatusText.Text = AppStrings.Get("TLR_NoStoriesFound");
                return;
            }

            _allRows.Clear();
            BuscarButton.IsEnabled = false;
            await LoadAsync();
            BuscarButton.IsEnabled = true;
        }

        private void OnTasksGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateTotals();
            AddSelectedButton.IsEnabled = _allRows.Any(r => r.IsSelected && !r.InSchedule);
            UpdateTaskActionButtons();

            // Atualiza breadcrumb com a story da linha selecionada
            var row = TasksGrid.SelectedItem as TaskReviewRow;
            if (row == null) { BreadcrumbPanel.Visibility = Visibility.Collapsed; return; }

            BreadcrumbPanel.Visibility = Visibility.Visible;
            var story = row.StoryTask;
            var feature = story.Parent;
            var epic = feature?.Parent;

            EpicBreadcrumb.Text    = epic    != null ? $"{epic.Name} › "    : "";
            FeatureBreadcrumb.Text = feature != null ? $"{feature.Name} › " : "";
            StoryBreadcrumb.Text   = $"{story.Name}";
            TaskBreadcrumb.Text    = $" › {row.Title}";
        }

        private void OnStateComboLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox cb)
                cb.ItemsSource = KnownStates;
        }

        private void OnActivityComboLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox cb)
                cb.ItemsSource = _activityList;
        }

        private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            // Marca dirty quando confirma a edição
            if (e.EditAction == DataGridEditAction.Commit && e.Row.Item is TaskReviewRow row)
            {
                row.IsDirty = true;
                SaveChangesButton.IsEnabled = true;
                DirtyHint.Visibility = Visibility.Visible;
            }
        }

        private async void OnSaveChangesClick(object sender, RoutedEventArgs e)
            => await SaveDirtyRowsAsync();

        private async Task SaveDirtyRowsAsync()
        {
            var dirty = _allRows.Where(r => r.IsDirty).ToList();
            if (dirty.Count == 0) return;

            SaveChangesButton.IsEnabled = false;
            StatusText.Text = AppStrings.Get("TLR_SyncingCount", dirty.Count);

            var options = TfsConnectionStore.Load("NXProject.Community");
            int ok = 0, fail = 0;

            foreach (var row in dirty)
            {
                try
                {
                    await TfsImportService.UpdateTaskFieldsAsync(options, row.TaskId,
                        estimatedHours: row.EstimatedHours,
                        completedHours: row.CompletedHours,
                        priority: row.Priority,
                        assignedTo: row.AssignedTo,
                        state: row.State,
                        title: row.Title,
                        activity: row.Activity,
                        tags: row.Tags);
                    row.IsDirty = false;
                    ok++;
                }
                catch
                {
                    fail++;
                }
            }

            HasChanges = true;
            DirtyHint.Visibility = _allRows.Any(r => r.IsDirty) ? Visibility.Visible : Visibility.Collapsed;
            SaveChangesButton.IsEnabled = _allRows.Any(r => r.IsDirty);
            StatusText.Text = AppStrings.Get("TLR_SyncDone", ok) + (fail > 0 ? AppStrings.Get("TLR_SyncDoneErrors", fail) : "");
            UpdateTotals();
            UpdateTaskActionButtons();
        }

        private ProjectTask? GetCurrentDevOpsStory()
        {
            if (TasksGrid.SelectedItem is TaskReviewRow row && row.StoryTask.TfsId is > 0)
                return row.StoryTask;

            if (_stories.Count == 1 && _stories[0].TfsId is > 0)
                return _stories[0];

            return null;
        }

        private TaskReviewRow? GetSelectedTaskRow()
            => TasksGrid.SelectedItem as TaskReviewRow;

        private bool CanCreateTaskForCurrentStory()
            => GetCurrentDevOpsStory() is { TfsId: > 0 } story
               && TfsImportService.IsStoryTypePublic(story.TfsType);

        private void UpdateTaskActionButtons()
        {
            var selectedRow = GetSelectedTaskRow();
            var canCreateTask = CanCreateTaskForCurrentStory();

            if (AlterTaskButton != null)
                AlterTaskButton.IsEnabled = selectedRow != null;
            if (OpenTfsButton != null)
                OpenTfsButton.IsEnabled = selectedRow != null && selectedRow.TaskId > 0;
            if (IncludeTaskButton != null)
                IncludeTaskButton.IsEnabled = canCreateTask;
            if (DeleteTaskButton != null)
                DeleteTaskButton.IsEnabled = selectedRow != null && selectedRow.TaskId > 0;
        }

        private void OnAlterTaskClick(object sender, RoutedEventArgs e)
        {
            if (GetSelectedTaskRow() == null) return;

            TasksGrid.Focus();
            TasksGrid.CurrentCell = new DataGridCellInfo(TasksGrid.SelectedItem, TasksGrid.Columns[3]);
            TasksGrid.BeginEdit();
        }

        private void OnOpenTfsClick(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedTaskRow();
            if (row == null || row.TaskId <= 0) return;

            var options = TfsConnectionStore.Load("NXProject.Community");
            if (string.IsNullOrWhiteSpace(options.OrganizationUrl) || string.IsNullOrWhiteSpace(options.TeamProject))
            {
                MessageBox.Show(AppStrings.Get("TLR_ConfigTfsFirst"), AppStrings.Get("TLR_OpenTfsTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var baseUrl = options.OrganizationUrl.TrimEnd('/');
            var project = Uri.EscapeDataString(options.TeamProject.Trim());
            var url = $"{baseUrl}/{project}/_workitems/edit/{row.TaskId}";
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(AppStrings.Get("TLR_CannotOpen", ex.Message), AppStrings.Get("TLR_OpenTfsTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OnIncludeTaskClick(object sender, RoutedEventArgs e)
        {
            var story = GetCurrentDevOpsStory();
            if (story?.TfsId is not > 0 || !TfsImportService.IsStoryTypePublic(story.TfsType))
                return;

            IncludeTaskButton.IsEnabled = false;
            StatusText.Text = AppStrings.Get("TLR_CreatingTask", story.Name);

            try
            {
                var options = TfsConnectionStore.Load("NXProject.Community");
                var newId = await TfsImportService.CreateChildTaskAsync(
                    options,
                    story.TfsId.Value,
                    AppStrings.Get("TLR_NewTask"),
                    iterationPath: story.TfsIterationPath);

                HasChanges = true;
                await LoadAsync();

                var created = _allRows.FirstOrDefault(r => r.TaskId == newId);
                if (created != null)
                {
                    TasksGrid.SelectedItem = created;
                    TasksGrid.ScrollIntoView(created);
                    StatusText.Text = AppStrings.Get("TLR_TaskCreated", newId);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = AppStrings.Get("TLR_CreateError");
                MessageBox.Show(AppStrings.Get("TLR_CreateErrorDetail", ex.Message), AppStrings.Get("TLR_IncludeTaskTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateTaskActionButtons();
            }
        }

        private async void OnDeleteTaskClick(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedTaskRow();
            if (row == null || row.TaskId <= 0) return;

            var confirm = MessageBox.Show(
                AppStrings.Get("TLR_DeleteTaskConfirm", row.TaskId, row.Title),
                AppStrings.Get("TLR_DeleteTaskTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;

            DeleteTaskButton.IsEnabled = false;
            StatusText.Text = AppStrings.Get("TLR_DeletingTask", row.TaskId);

            try
            {
                var options = TfsConnectionStore.Load("NXProject.Community");
                await TfsImportService.DeleteWorkItemAsync(options, row.TaskId);

                _allRows.Remove(row);
                HasChanges = true;
                RefreshRowNumbers();
                UpdateTotals();
                StatusText.Text = AppStrings.Get("TLR_TaskDeleted", row.TaskId);
            }
            catch (Exception ex)
            {
                StatusText.Text = AppStrings.Get("TLR_DeleteError");
                MessageBox.Show(AppStrings.Get("TLR_DeleteErrorDetail", ex.Message), AppStrings.Get("TLR_DeleteTaskTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateTaskActionButtons();
            }
        }

        private async void OnReloadClick(object sender, RoutedEventArgs e)
        {
            _allRows.Clear();
            await LoadAsync();
        }

        private void OnAddSelectedClick(object sender, RoutedEventArgs e)
        {
            var toAdd = _allRows.Where(r => r.IsSelected && !r.InSchedule).ToList();
            if (toAdd.Count == 0) return;
            AddToScheduleAndClose(toAdd);
        }

        private void OnExpandAllClick(object sender, RoutedEventArgs e)
        {
            var toAdd = (_view?.Cast<TaskReviewRow>() ?? _allRows).Where(r => !r.InSchedule).ToList();
            if (toAdd.Count == 0) { MessageBox.Show(AppStrings.Get("TLR_AllInSchedule"), AppStrings.Get("TLR_InfoTitle"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
            AddToScheduleAndClose(toAdd);
        }

        private void AddToScheduleAndClose(List<TaskReviewRow> toAdd)
        {
            AddToScheduleCallback?.Invoke(toAdd);
            HasChanges = true;
            Close();
        }

        private void OnMemorizeClick(object sender, RoutedEventArgs e)
        {
            _memorize = true;
            Close();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private void OnResourceSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is string name &&
                cb.DataContext is TaskReviewRow row)
            {
                row.AssignedToDisplay = name;
                row.IsDirty = true;
            }
        }

        private void OnGridContextMenuOpened(object sender, RoutedEventArgs e)
        {
            var row = TasksGrid.SelectedItem as TaskReviewRow;
            if (BlockTaskMenuItem != null)
                BlockTaskMenuItem.Header = (row?.IsBlockedState == true)
                    ? AppStrings.Get("TLR_RemoveBlockMenu")
                    : AppStrings.Get("TLR_AddBlockMenu");
        }

        private void OnRowToggleBlockClick(object sender, RoutedEventArgs e)
        {
            var row = TasksGrid.SelectedItem as TaskReviewRow;
            if (row == null) return;
            row.ToggleBlock();
            SaveChangesButton.IsEnabled = true;
            DirtyHint.Visibility = Visibility.Visible;
            UpdateTotals();
        }

        private void OnToggleBlockClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TaskReviewRow row)
            {
                row.ToggleBlock();
                SaveChangesButton.IsEnabled = true;
                DirtyHint.Visibility = Visibility.Visible;
                UpdateTotals();
            }
        }

        private void OnRatearClick(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(StoryDurationBox.Text.Replace(",", "."),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double storyHours) || storyHours <= 0)
            {
                MessageBox.Show(AppStrings.Get("TLR_EnterDuration"), AppStrings.Get("TLR_RateioTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var allVisible = (_view?.Cast<TaskReviewRow>() ?? _allRows).ToList();

            // Eligible: tasks sem HH Original E sem HH Atual
            var eligible = allVisible.Where(r => r.EstimatedHours <= 0 && r.CompletedHours <= 0).ToList();
            if (eligible.Count == 0)
            {
                MessageBox.Show(AppStrings.Get("TLR_AllHaveHours"), AppStrings.Get("TLR_RateioTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int n = eligible.Count;

            // Rateio de HH Original: usa o valor do campo Duração Story como fonte definitiva
            double usedOrig = allVisible.Where(r => r.EstimatedHours > 0).Sum(r => r.EstimatedHours);
            double remainOrig = Math.Max(0, storyHours - usedOrig);
            double perOrig = remainOrig > 0 ? remainOrig / n : storyHours / Math.Max(1, allVisible.Count);

            // Rateio de HH Atual: usa HH Atual da story se disponível, senão não distribui
            var storyTask = allVisible.FirstOrDefault()?.StoryTask;
            double storyCur = storyTask?.CurrentHours ?? 0;
            double usedCur = allVisible.Where(r => r.CompletedHours > 0).Sum(r => r.CompletedHours);
            double remainCur = storyCur > 0 ? Math.Max(0, storyCur - usedCur) : 0;
            double perCur = remainCur > 0 ? remainCur / n
                          : storyCur > 0 ? storyCur / Math.Max(1, allVisible.Count)
                          : 0;

            foreach (var r in eligible)
            {
                r.EstimatedHours = Math.Round(perOrig, 1);
                if (perCur > 0)
                    r.CompletedHours = Math.Round(perCur, 1);
                r.IsDirty = true;
            }

            SaveChangesButton.IsEnabled = true;
            DirtyHint.Visibility = Visibility.Visible;
            UpdateTotals();

            var msg = perCur > 0
                ? AppStrings.Get("TLR_RateioApplied2", n, perOrig, perCur)
                : AppStrings.Get("TLR_RateioApplied1", n, perOrig);
            MessageBox.Show(msg, AppStrings.Get("TLR_RateioTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnZerarClick(object sender, RoutedEventArgs e)
        {
            var allVisible = (_view?.Cast<TaskReviewRow>() ?? _allRows).ToList();
            var eligible = allVisible
                .Where(r => !string.Equals(r.State, "Closed", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(r.State, "Done",   StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (eligible.Count == 0)
            {
                MessageBox.Show(AppStrings.Get("TLR_NoUnclosedTasks"), AppStrings.Get("TLR_ZerarTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                AppStrings.Get("TLR_ZerarConfirm", eligible.Count),
                AppStrings.Get("TLR_ZerarTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            foreach (var r in eligible)
            {
                r.EstimatedHours  = 0;
                r.CompletedHours  = 0;
                r.IsDirty = true;
            }

            SaveChangesButton.IsEnabled = true;
            DirtyHint.Visibility = Visibility.Visible;
            UpdateTotals();
        }

        // ── Drag-drop para reordenar por prioridade ──────────────────────────────

        private void OnGridMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(null);
            _dragRow = GetRowUnderMouse(e);
            _isDragging = false;
        }

        private void OnGridMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragRow == null || _isDragging) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _isDragging = true;
            DragDrop.DoDragDrop(TasksGrid, _dragRow, DragDropEffects.Move);
        }

        private void OnGridDrop(object sender, DragEventArgs e)
        {
            _isDragging = false;
            if (_dragRow == null) return;

            // Commitar qualquer edição pendente antes de mexer na coleção/sort
            TasksGrid.CommitEdit(DataGridEditingUnit.Row, true);
            TasksGrid.CommitEdit(DataGridEditingUnit.Cell, true);

            var target = GetRowUnderMouse(e);
            if (target == null || ReferenceEquals(target, _dragRow)) { _dragRow = null; return; }

            // Snapshot da ordem visível atual
            var visible = (_view?.Cast<TaskReviewRow>() ?? _allRows).ToList();
            int fromIdx = visible.IndexOf(_dragRow);
            int toIdx   = visible.IndexOf(target);
            if (fromIdx < 0 || toIdx < 0) { _dragRow = null; return; }

            visible.RemoveAt(fromIdx);
            visible.Insert(toIdx, _dragRow);

            // Reatribuir prioridades sequenciais
            for (int i = 0; i < visible.Count; i++)
            {
                int newPri = i + 1;
                if (visible[i].Priority != newPri)
                {
                    visible[i].Priority = newPri;
                    visible[i].IsDirty  = true;
                }
            }

            // Remover sort automático (deve ser feito DEPOIS do CommitEdit)
            _view?.SortDescriptions.Clear();

            // Reordenar _allRows usando Move para evitar reset completo da coleção
            for (int i = 0; i < visible.Count; i++)
            {
                int cur = _allRows.IndexOf(visible[i]);
                if (cur != i) _allRows.Move(cur, i);
            }

            RefreshRowNumbers();
            SaveChangesButton.IsEnabled = _allRows.Any(r => r.IsDirty);
            DirtyHint.Visibility = SaveChangesButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            UpdateTotals();
            _dragRow = null;
        }

        private TaskReviewRow? GetRowUnderMouse(RoutedEventArgs e)
        {
            var el = e.OriginalSource as DependencyObject;
            while (el != null && el is not DataGridRow)
                el = GetParentElement(el);

            return (el as DataGridRow)?.Item as TaskReviewRow;
        }

        private static DependencyObject? GetParentElement(DependencyObject element)
        {
            if (element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                return System.Windows.Media.VisualTreeHelper.GetParent(element);

            if (element is FrameworkContentElement contentElement)
                return contentElement.Parent;

            if (element is FrameworkElement frameworkElement)
                return frameworkElement.Parent;

            return LogicalTreeHelper.GetParent(element);
        }
    }

    public class TaskReviewRow : INotifyPropertyChanged
    {
        public int StoryId { get; set; }
        public string StoryName { get; set; } = "";
        public string FeatureName { get; set; } = "";
        public ProjectTask StoryTask { get; set; } = null!;
        public int TaskId { get; set; }

        private int _rowNumber;
        public int RowNumber { get => _rowNumber; set { if (_rowNumber == value) return; _rowNumber = value; OnPropertyChanged(); } }

        private string _title = "";
        public string Title { get => _title; set { if (_title == value) return; _title = value; OnPropertyChanged(); } }

        private string _state = "New";
        public string State
        {
            get => _state;
            set
            {
                if (_state == value) return;
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBlockedState));
                OnPropertyChanged(nameof(BlockButtonLabel));
                OnPropertyChanged(nameof(BlockButtonColor));
            }
        }

        private double _estimatedHours;
        public double EstimatedHours { get => _estimatedHours; set { if (_estimatedHours == value) return; _estimatedHours = value; OnPropertyChanged(); OnPropertyChanged(nameof(EstimatedHoursDisplay)); } }

        private double _completedHours;
        public double CompletedHours { get => _completedHours; set { if (_completedHours == value) return; _completedHours = value; OnPropertyChanged(); } }
        public double PercentComplete { get; set; }

        private int _priority = 5;
        public int Priority { get => _priority; set { if (_priority == value) return; _priority = value; OnPropertyChanged(); } }

        private string _activity = "";
        public string Activity { get => _activity; set { if (_activity == value) return; _activity = value; OnPropertyChanged(); } }

        private string _assignedTo = "";
        public string AssignedTo { get => _assignedTo; set { if (_assignedTo == value) return; _assignedTo = value; OnPropertyChanged(); } }

        // displayName para exibição na grid; editável (sincroniza no AssignedTo se igual ao email)
        private string _assignedToDisplay = "";
        public string AssignedToDisplay { get => _assignedToDisplay; set { if (_assignedToDisplay == value) return; _assignedToDisplay = value; OnPropertyChanged(); } }

        private bool _inSchedule;
        public bool InSchedule
        {
            get => _inSchedule;
            set { _inSchedule = value; OnPropertyChanged(); OnPropertyChanged(nameof(InScheduleDisplay)); }
        }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

        private bool _isDirty;
        public bool IsDirty { get => _isDirty; set { _isDirty = value; OnPropertyChanged(); } }

        public string EstimatedHoursDisplay => EstimatedHours > 0 ? $"{EstimatedHours:0.#}h" : "-";
        public string InScheduleDisplay => InSchedule ? "✔ Sim" : "Não";

        private string _tags = "";
        public string Tags
        {
            get => _tags;
            set { if (_tags == value) return; _tags = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(IsBlockedState)); OnPropertyChanged(nameof(BlockButtonLabel)); OnPropertyChanged(nameof(BlockButtonColor)); }
        }

        private static bool HasBlockTag(string? tags) =>
            (tags ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(t => string.Equals(t, "Block", StringComparison.OrdinalIgnoreCase));

        public bool IsBlockedState => HasBlockTag(_tags);
        public string BlockButtonLabel => IsBlockedState ? AppStrings.Get("TLR_BlockLabelOn") : AppStrings.Get("TLR_BlockLabelOff");
        public string BlockButtonColor => IsBlockedState ? "#C0392B" : "#AAA";

        public void ToggleBlock()
        {
            var list = _tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (IsBlockedState)
                list.RemoveAll(t => string.Equals(t, "Block", StringComparison.OrdinalIgnoreCase));
            else
                list.Add("Block");
            Tags    = string.Join("; ", list);
            IsDirty = true;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
    }
}
