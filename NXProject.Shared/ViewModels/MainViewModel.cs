using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly string _sprintSettingsStorageKey;
        private bool _isApplyingProjectSprintSettings;

        [ObservableProperty] private Project _project = new();
        [ObservableProperty] private string _statusMessage = "Pronto";
        [ObservableProperty] private int _selectedViewIndex = 0;
        [ObservableProperty] private string _selectedZoom = "Mês";
        [ObservableProperty] private TaskViewModel? _selectedTask;
        [ObservableProperty] private int _sprintDurationDays = 14;
        [ObservableProperty] private int _firstSprintNumber = 1;
        [ObservableProperty] private string _sprintNumberingMode = "Sequencial";
        [ObservableProperty] private double _lowDaysPerSfp = 1.0;

        // null = sem filtro (todos visíveis); conjunto de IDs = apenas esses recursos
        public HashSet<int>? ResourceFilter { get; private set; }
        public double? PercentCompleteFilterMin { get; private set; }
        public double? PercentCompleteFilterMax { get; private set; }
        public string? ProgressDateFilterMode { get; private set; }
        public DateTime? ProgressDateFilterReference { get; private set; }

        public bool HasResourceFilter => ResourceFilter != null;
        public bool HasPercentCompleteFilter =>
            PercentCompleteFilterMin.HasValue ||
            PercentCompleteFilterMax.HasValue ||
            !string.IsNullOrWhiteSpace(ProgressDateFilterMode);

        public void SetResourceFilter(HashSet<int>? filter)
        {
            ResourceFilter = filter;
            OnPropertyChanged(nameof(HasResourceFilter));
            RebuildFlatTasks();
        }

        public void SetPercentCompleteFilter(
            double? min,
            double? max,
            string? dateFilterMode = null,
            DateTime? dateFilterReference = null)
        {
            PercentCompleteFilterMin = NormalizePercentFilterValue(min);
            PercentCompleteFilterMax = NormalizePercentFilterValue(max);
            ProgressDateFilterMode = NormalizeProgressDateFilterMode(dateFilterMode);
            ProgressDateFilterReference = ProgressDateFilterMode != null
                ? (dateFilterReference ?? DateTime.Today).Date
                : null;

            if (PercentCompleteFilterMin.HasValue &&
                PercentCompleteFilterMax.HasValue &&
                PercentCompleteFilterMin.Value > PercentCompleteFilterMax.Value)
            {
                (PercentCompleteFilterMin, PercentCompleteFilterMax) =
                    (PercentCompleteFilterMax, PercentCompleteFilterMin);
            }

            OnPropertyChanged(nameof(HasPercentCompleteFilter));
            RebuildFlatTasks();
        }

        private static double? NormalizePercentFilterValue(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value))
                return null;

            return Math.Clamp(Math.Round(value.Value), 0.0, 100.0);
        }

        private static string? NormalizeProgressDateFilterMode(string? mode)
        {
            return mode switch
            {
                "StartDate" or "StartToday" => "StartDate",
                "FinishDate" or "FinishToday" => "FinishDate",
                _ => null
            };
        }
        [ObservableProperty] private double _mediumDaysPerSfp = 1.0;
        [ObservableProperty] private double _highDaysPerSfp = 1.0;
        [ObservableProperty] private double _criticalPathRiskSlackDays = 2.0;
        [ObservableProperty] private double _criticalPathCriticalSlackDays = 1.0;
        [ObservableProperty] private bool _showOriginalHoursColumn = false;
        [ObservableProperty] private string _hiddenColumns = "";
        [ObservableProperty] private string _hiddenColumnsExpanded = "";

        public ObservableCollection<string> ZoomLevels { get; } = new()
        {
            "Dia", "Semana", "Sprint", "Mês", "Trimestre", "Semestre"
        };

        public ObservableCollection<string> SprintNumberingModes { get; } = new()
        {
            "Sequencial", "Par", "Impar"
        };

        // Lista plana de tarefas para o DataGrid (com hierarquia via indentação)
        public ObservableCollection<TaskViewModel> FlatTasks { get; } = new();

        // Sprints reais do DevOps (nome + janela), usadas na coluna Sprint da grade
        // e nas faixas nomeadas do cronograma. Espelha Project.Sprints.
        public ObservableCollection<Sprint> Sprints { get; } = new();

        // Opções do dropdown de sprint da grade: "(sem sprint)" + as sprints reais.
        // A 1a opção (Path nulo) permite deixar a tarefa sem sprint.
        public ObservableCollection<Sprint> SprintOptions { get; } = new();
        private static readonly Sprint NoSprintOption = new() { DisplayName = "(sem sprint)", Path = null };

        // IDs das tarefas que o usuário recolheu manualmente
        private readonly HashSet<int> _collapsedTaskIds = new();

        // Agrupamentos para aba Sprints
        public ObservableCollection<SprintGroup> SprintGroups { get; } = new();

        // Agrupamentos para aba Recursos
        public ObservableCollection<ResourceAllocationGroup> ResourceAllocationGroups { get; } = new();

        private int _nextId = 1;
        private int _nextNoDevOpsId = -1; // IDs negativos para tarefas No DevOps

        public int NextId() => _nextId++;

        /// <summary>Chamado pela View quando a tarefa selecionada é DevOps e o usuário clicou em Excluir.</summary>
        public Action<TaskViewModel>? RequestDevOpsDeleteDialog { get; set; }

        /// <summary>Chamado após AddTask/AddSubtask para a View rolar até a nova tarefa.</summary>
        public Action? RequestScrollToSelected { get; set; }

        /// <summary>Chamado antes de reconstruir a grade ao inserir uma tarefa.</summary>
        public Action? PrepareTaskInsertionScroll { get; set; }

        /// <summary>
        /// Callback unificado ao adicionar subtarefa de uma Story.
        /// Recebe a Story (TaskViewModel). Retorna:
        ///   "Task"     → criar Task DevOps
        ///   "NoDevOps" → criar atividade interna
        ///   "Fetch"    → buscar Tasks do DevOps (a View dispara e não cria subtarefa)
        ///   null       → cancelado
        /// </summary>
        public Func<TaskViewModel, string?>? AskSubtaskAction { get; set; }

        // Mantidos por compatibilidade mas não mais usados internamente
        [Obsolete] public Func<bool?>? AskSubtaskIsDevOpsTask { get; set; }
        [Obsolete] public Func<TaskViewModel, bool?>? AskFetchTasksBeforeSubtask { get; set; }

        public MainViewModel(string sprintSettingsStorageKey = "NXProject.Community")
        {
            _sprintSettingsStorageKey = string.IsNullOrWhiteSpace(sprintSettingsStorageKey)
                ? "NXProject.Community"
                : sprintSettingsStorageKey.Trim();

            // Projeto de exemplo
            NewProject();
        }

        public void RebuildFlatTasks()
        {
            NormalizeNoDevOpsType(Project.Tasks);
            SyncOriginalHoursWhenZeroPercent(Project.Tasks);
            RecalculateLeafFinishesFromAssignments(Project.Tasks);
            RecalcTaskDatesFromPriority(Project.Tasks);
            foreach (var root in Project.Tasks)
                root.RecalcSummary();
            var selectedModel = SelectedTask?.Model;
            FlatTasks.Clear();
            foreach (var task in Project.Tasks)
                AddFlatRecursive(task, 0);
            RecalcSprints();
            RebuildSprintGroups();
            RebuildResourceGroups();
            ApplyHierarchyColors();

            SelectedTask = selectedModel == null
                ? null
                : FlatTasks.FirstOrDefault(vm => vm.Model == selectedModel);
        }

        private static void RecalculateLeafFinishesFromAssignments(IEnumerable<ProjectTask> tasks)
        {
            foreach (var task in tasks)
            {
                if (task.Children.Count > 0)
                {
                    RecalculateLeafFinishesFromAssignments(task.Children);
                    continue;
                }

                if (task.IsSummary || task.IsMilestone || task.FinishFixed || task.PercentComplete >= 100)
                    continue;

                task.Finish = TaskScheduleService.CalculateFinishFromAssignments(task, task.Start);
            }
        }

        public void ApplyHierarchyColors()
        {
            var colors = Project.HierarchyLevelColors;
            bool enabled = Project.UseHierarchyColors;
            foreach (var vm in FlatTasks)
            {
                if (!enabled)
                {
                    vm.HierarchyBackground = null;
                    continue;
                }
                int idx = Math.Min(vm.Depth, colors.Count - 1);
                vm.HierarchyBackground = ParseHexBrush(colors[idx]);
            }
        }

        private static SolidColorBrush? ParseHexBrush(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6 &&
                byte.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
                return new SolidColorBrush(Color.FromRgb(r, g, b));
            return null;
        }

        private bool _rebuildPending = false;

        private void AddFlatRecursive(ProjectTask task, int depth, TaskViewModel? parentVm = null)
        {
            var vm = new TaskViewModel(task, depth, LowDaysPerSfp, MediumDaysPerSfp, HighDaysPerSfp)
            {
                ParentViewModel = parentVm,
                GetSprintStart = () => GetDefaultStart(task),
                GetVirtualPredecessorStart = source => GetVirtualPredecessorStart(source),
                FindByInternalId = internalId =>
                    FlatTasks.FirstOrDefault(t => t.Model.Id == internalId),
                FindByDisplayId = displayId =>
                {
                    // Formato legado: "T:1234" (TFS) ou "I:45" (interno)
                    if (displayId.StartsWith("T:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(displayId[2..], out var tfsNum))
                    {
                        var byTfs = FlatTasks.FirstOrDefault(t => t.Model.TfsId == tfsNum);
                        if (byTfs != null) return byTfs.Model.Id;
                    }
                    if (displayId.StartsWith("I:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(displayId[2..], out var intNum))
                    {
                        var byInt = FlatTasks.FirstOrDefault(t => t.Model.Id == intNum);
                        if (byInt != null) return byInt.Model.Id;
                    }
                    // Formato atual da UI: "1234:T" (TFS) ou "45:I" (interno)
                    if (displayId.EndsWith(":T", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(displayId[..^2], out var gridTfsNum))
                    {
                        var byTfs = FlatTasks.FirstOrDefault(t => t.Model.TfsId == gridTfsNum);
                        if (byTfs != null) return byTfs.Model.Id;
                    }
                    if (displayId.EndsWith(":I", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(displayId[..^2], out var gridIntNum))
                    {
                        var byInt = FlatTasks.FirstOrDefault(t => t.Model.Id == gridIntNum);
                        if (byInt != null) return byInt.Model.Id;
                    }
                    // Fallback legado: número puro → tenta TfsId depois Id interno
                    if (int.TryParse(displayId, out var num))
                    {
                        var byTfs = FlatTasks.FirstOrDefault(t => t.Model.TfsId == num);
                        if (byTfs != null) return byTfs.Model.Id;
                        var byInternal = FlatTasks.FirstOrDefault(t => t.Model.Id == num);
                        if (byInternal != null) return byInternal.Model.Id;
                    }
                    return null;
                },
                ScheduleSuccessors = source => CascadeSuccessors(source),
                PrimaryResourceChanged = (source, oldResourceId) => OnPrimaryResourceChanged(source, oldResourceId),
                GetSprintFinish = path =>
                {
                    if (string.IsNullOrEmpty(path)) return null;
                    var sprint = Sprints.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
                    return sprint?.End;
                }
            };
            if (parentVm != null)
                parentVm.ChildrenViewModels.Add(vm);

            vm.IsSelected = SelectedTask?.Model == task;
            vm.IsExpanded = !_collapsedTaskIds.Contains(task.Id);
            vm.RefreshSprintOptions(SprintOptions);

            // Reagir a mudanças que afetam a lista filtrada de sprints por tarefa.
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(TaskViewModel.Start))
                    vm.RefreshSprintOptions(SprintOptions);
            };

            // Reagir ao toggle de expand/collapse sem criar loop infinito
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(TaskViewModel.IsExpanded)) return;
                if (vm.IsExpanded) _collapsedTaskIds.Remove(task.Id);
                else _collapsedTaskIds.Add(task.Id);
                if (!_rebuildPending)
                {
                    _rebuildPending = true;
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        _rebuildPending = false;
                        RebuildFlatTasks();
                    });
                }
            };

            // Filtros: a tarefa entra se ela ou algum descendente passar nos filtros ativos.
            if (!TaskMatchesActiveFilters(task))
                return;

            FlatTasks.Add(vm);
            if (vm.IsExpanded)
                foreach (var child in task.Children)
                    AddFlatRecursive(child, depth + 1, vm);
        }

        private bool TaskMatchesActiveFilters(ProjectTask task)
        {
            if (TaskSelfMatchesActiveFilters(task))
                return true;

            return task.Children.Any(TaskMatchesActiveFilters);
        }

        private bool TaskSelfMatchesActiveFilters(ProjectTask task)
        {
            return TaskSelfMatchesResourceFilter(task) &&
                   TaskSelfMatchesPercentCompleteFilter(task) &&
                   TaskSelfMatchesProgressDateFilter(task);
        }

        private bool TaskSelfMatchesResourceFilter(ProjectTask task)
        {
            if (ResourceFilter == null) return true;
            return task.Resources.Any(r => r.Resource != null && ResourceFilter.Contains(r.Resource.Id));
        }

        private bool TaskSelfMatchesPercentCompleteFilter(ProjectTask task)
        {
            if (!HasPercentCompleteFilter) return true;

            var percent = Math.Clamp(task.PercentComplete, 0.0, 100.0);
            if (PercentCompleteFilterMin.HasValue && percent < PercentCompleteFilterMin.Value)
                return false;
            if (PercentCompleteFilterMax.HasValue && percent > PercentCompleteFilterMax.Value)
                return false;

            return true;
        }

        private bool TaskSelfMatchesProgressDateFilter(ProjectTask task)
        {
            if (string.IsNullOrWhiteSpace(ProgressDateFilterMode))
                return true;

            var today = DateTime.Today;
            var referenceDate = (ProgressDateFilterReference ?? today).Date;
            return ProgressDateFilterMode switch
            {
                "StartDate" => task.Start.Date > referenceDate,
                "FinishDate" => ProjectCalendarService
                    .GetInclusiveFinishDate(task.Start, task.Finish)
                    .Date < referenceDate,
                _ => true
            };
        }

        private DateTime GetTaskSprintStart(ProjectTask task)
        {
            if (Project.Sprints.Count > 0 && !string.IsNullOrWhiteSpace(task.TfsIterationPath))
            {
                var sprint = Project.Sprints.FirstOrDefault(s =>
                    string.Equals(s.Path, task.TfsIterationPath, StringComparison.OrdinalIgnoreCase));
                if (sprint != null)
                    return sprint.Start;
            }
            return Project.StartDate;
        }

        // Retorna a data de início padrão ao limpar o fix de uma tarefa sem predecessora:
        // se existe irmão anterior no mesmo nível → dia útil seguinte ao fim do irmão,
        // caso contrário → início da sprint/projeto.
        private DateTime GetDefaultStart(ProjectTask task)
        {
            System.Collections.Generic.IList<ProjectTask> siblings = task.Parent != null
                ? (System.Collections.Generic.IList<ProjectTask>)task.Parent.Children
                : Project.Tasks;

            var idx = siblings.IndexOf(task);
            if (idx > 0)
            {
                var prev = siblings[idx - 1];
                var inclusiveFinish = ProjectCalendarService.GetInclusiveFinishDate(prev.Start, prev.Finish);
                return ProjectCalendarService.AddWorkingDays(inclusiveFinish, 1);
            }

            return GetTaskSprintStart(task);
        }

        private void RecalcSprints()
        {
            var projectStart = Project.StartDate;
            bool hasDevOpsSprints = Project.Sprints.Count > 0;

            foreach (var vm in FlatTasks)
            {
                if (!vm.SupportsSprint)
                {
                    vm.SprintNumber = 0; // Projeto/Epic não têm sprint
                    continue;
                }

                if (hasDevOpsSprints)
                {
                    // Vínculo explícito pelo IterationPath (sem inferir por janela:
                    // assim "(sem sprint)" permanece sem sprint).
                    var sprint = string.IsNullOrWhiteSpace(vm.Model.TfsIterationPath)
                        ? null
                        : Project.Sprints.FirstOrDefault(s =>
                            string.Equals(s.Path, vm.Model.TfsIterationPath, StringComparison.OrdinalIgnoreCase));
                    vm.SprintNumber = sprint?.Number ?? 0;
                }
                else
                {
                    var sprintIndex = GetSprintIndex(vm.Start, projectStart);
                    vm.SprintNumber = sprintIndex < 0 ? 0 : GetSprintNumberFromIndex(sprintIndex);
                }
            }
        }

        /// <summary>
        /// Aplica a troca de sprint feita na grade: grava o novo IterationPath na
        /// tarefa, desliza a barra para a janela da nova sprint (preservando a
        /// duração) e marca o projeto como alterado, para sincronizar de volta ao
        /// DevOps no próximo Export → Sincronizar.
        /// </summary>
        public void ApplyTaskSprintChange(TaskViewModel vm, Sprint? sprint, Action? afterRebuild = null)
        {
            if (vm == null) return;
            var task = vm.Model;

            var newPath = sprint?.Path;
            if (string.Equals(task.TfsIterationPath, newPath, StringComparison.Ordinal))
                return;

            task.TfsIterationPath = newPath;

            if (sprint != null && !task.IsSummary)
            {
                // Só reposiciona o início se a tarefa ainda não foi iniciada (% = 0).
                // Se já tiver progresso, mantém a data de início original.
                if (task.PercentComplete == 0)
                {
                    task.Start = sprint.Start;
                    task.Finish = task.IsMilestone
                        ? sprint.Start
                        : TaskScheduleService.CalculateFinishFromAssignments(task, sprint.Start);
                }
                else
                {
                    // Apenas garante que o Fim não caia antes do Início ao trocar de sprint.
                    task.Finish = task.IsMilestone
                        ? task.Start
                        : TaskScheduleService.CalculateFinishFromAssignments(task, task.Start);
                }
                RecalcSummaryChain(task.Parent);
            }

            Project.IsDirty = true;

            // Atualiza apenas as propriedades afetadas — sem reconstruir FlatTasks para não perder scroll.
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                // Notifica a task e seus ancestrais sem reconstruir a coleção inteira.
                vm.NotifyDatesChanged();
                vm.NotifySprintChanged();

                // Atualiza agrupamentos de sprint/recurso sem tocar em FlatTasks.
                RecalcSprints();
                RebuildSprintGroups();
                RebuildResourceGroups();

                afterRebuild?.Invoke();
                StatusMessage = sprint != null
                    ? $"Sprint da tarefa alterada para \"{sprint.Name}\" (sincronize para aplicar no DevOps)."
                    : "Sprint removida da tarefa.";
            });
        }

        partial void OnSprintDurationDaysChanged(int value)
        {
            var normalizedValue = Math.Max(1, value);
            if (value != normalizedValue)
            {
                SprintDurationDays = normalizedValue;
                return;
            }

            Project.SprintDurationDays = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        partial void OnFirstSprintNumberChanged(int value)
        {
            var normalizedValue = Math.Max(1, value);
            if (value != normalizedValue)
            {
                FirstSprintNumber = normalizedValue;
                return;
            }

            Project.FirstSprintNumber = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        partial void OnSprintNumberingModeChanged(string value)
        {
            var normalizedValue = SprintNumberingModes.Contains(value) ? value : "Sequencial";
            if (!string.Equals(value, normalizedValue, StringComparison.Ordinal))
            {
                SprintNumberingMode = normalizedValue;
                return;
            }

            Project.SprintNumberingMode = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        partial void OnLowDaysPerSfpChanged(double value)
        {
            var normalizedValue = value < 0 ? 0 : value;
            if (Math.Abs(value - normalizedValue) > double.Epsilon)
            {
                LowDaysPerSfp = normalizedValue;
                return;
            }

            Project.LowDaysPerSfp = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        partial void OnMediumDaysPerSfpChanged(double value)
        {
            var normalizedValue = value < 0 ? 0 : value;
            if (Math.Abs(value - normalizedValue) > double.Epsilon)
            {
                MediumDaysPerSfp = normalizedValue;
                return;
            }

            Project.MediumDaysPerSfp = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        partial void OnHighDaysPerSfpChanged(double value)
        {
            var normalizedValue = value < 0 ? 0 : value;
            if (Math.Abs(value - normalizedValue) > double.Epsilon)
            {
                HighDaysPerSfp = normalizedValue;
                return;
            }

            Project.HighDaysPerSfp = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        partial void OnCriticalPathRiskSlackDaysChanged(double value)
        {
            var normalizedValue = value < 0 ? 0 : Math.Round(value, 1);
            normalizedValue = Math.Max(normalizedValue, CriticalPathCriticalSlackDays);
            if (Math.Abs(value - normalizedValue) > double.Epsilon)
            {
                CriticalPathRiskSlackDays = normalizedValue;
                return;
            }

            Project.CriticalPathRiskSlackDays = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
        }

        partial void OnCriticalPathCriticalSlackDaysChanged(double value)
        {
            var normalizedValue = value < 0 ? 0 : Math.Round(value, 1);
            if (Math.Abs(value - normalizedValue) > double.Epsilon)
            {
                CriticalPathCriticalSlackDays = normalizedValue;
                return;
            }

            Project.CriticalPathCriticalSlackDays = normalizedValue;
            if (CriticalPathRiskSlackDays < normalizedValue)
                CriticalPathRiskSlackDays = normalizedValue;

            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
        }

        partial void OnShowOriginalHoursColumnChanged(bool value)
        {
            Project.ShowOriginalHoursColumn = value;
            Project.IsDirty = true;
        }

        partial void OnHiddenColumnsChanged(string value)
        {
            Project.HiddenColumns = value ?? "";
            Project.IsDirty = true;
        }

        partial void OnHiddenColumnsExpandedChanged(string value)
        {
            Project.HiddenColumnsExpanded = value ?? "";
            Project.IsDirty = true;
        }

        partial void OnSelectedTaskChanged(TaskViewModel? oldValue, TaskViewModel? newValue)
        {
            if (oldValue != null)
                oldValue.IsSelected = false;

            if (newValue != null)
                newValue.IsSelected = true;

        }

        // ── Comandos ─────────────────────────────────────────────────────────

        [RelayCommand]
        private void NewProject()
        {
            var project = new Project { Name = "Novo Projeto", StartDate = DateTime.Today };
            project.ApplySprintSettingsProfile(SprintSettingsStore.Load(_sprintSettingsStorageKey));
            Project = project;
            ApplyProjectSprintSettingsToViewModel(project);
            _nextId = 1;
            _collapsedTaskIds.Clear();
            SelectedTask = null;
            FlatTasks.Clear();
            StatusMessage = AppStrings.Get("Status_NewProject");
        }

        [RelayCommand]
        private void ClearProject()
        {
            if (Project.Tasks.Count == 0 && Project.Resources.Count == 0)
            {
                StatusMessage = AppStrings.Get("Status_ProjectAlreadyClean");
                return;
            }

            var confirm = MessageBox.Show(
                AppStrings.Get("Msg_ClearProjectBody"),
                AppStrings.Get("Msg_ClearProjectTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                StatusMessage = AppStrings.Get("Status_ProjectClearCancelled");
                return;
            }

            Project.Tasks.Clear();
            Project.Resources.Clear();
            Project.IsDirty = true;
            _nextId = 1;
            _collapsedTaskIds.Clear();
            SelectedTask = null;
            RebuildFlatTasks();
            StatusMessage = AppStrings.Get("Status_ProjectCleared");
        }

        [RelayCommand]
        private void OpenProject()
        {
            SprintAlertLog.Clear();
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Projeto NXProject (*.xml)|*.xml|Todos os arquivos (*.*)|*.*",
                Title = "Abrir Projeto"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var project = XmlProjectService.Load(dlg.FileName);
                    foreach (var root in project.Tasks)
                        root.RecalcSummary();
                    SyncOriginalHoursWhenZeroPercent(project.Tasks);
                    Project = project;
                    ApplyProjectSprintSettingsToViewModel(project);
                    RecalcIdCounters();
                    RebuildFlatTasks();
                    ApplyVirtualPredecessorsToAll();
                    var opts = NXProject.Services.TfsConnectionStore.Load("NXProject.Community");
                    if (opts.AutoLoadBaseline && project.BaselineActive)
                        BaselineService.Load(dlg.FileName, FlatTasks.Select(t => t.Model));
                    StatusMessage = $"Projeto aberto: {dlg.FileName}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao abrir projeto:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        [RelayCommand]
        private void ImportOpenProj()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "OpenProj (*.pod)|*.pod|Todos os arquivos (*.*)|*.*",
                Title = "Importar projeto do OpenProj"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var project = OpenProjImportService.Import(dlg.FileName);
                Project = project;
                ApplyProjectSprintSettingsToViewModel(project);
                RecalcIdCounters();
                RebuildFlatTasks();
                StatusMessage = $"OpenProj importado: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao importar OpenProj:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Aplica um projeto importado externamente (ex.: TFS/Azure DevOps) ao
        /// estado atual, replicando os passos dos comandos de import de arquivo.
        /// </summary>
        public void ApplyImportedProject(Project project, string? statusMessage = null)
        {
            if (project == null) return;
            SprintAlertLog.Clear();

            var existingAllocations       = CaptureAllocationPercentByDevOpsTask();
            var existingAvailability      = CaptureAvailabilityByResourceKey();
            var existingOriginalHours     = CaptureOriginalEstimatedHoursByTfsId();
            var noDevOpsTasks             = CaptureNoDevOpsTasks();

            // Preserva configurações de UI do projeto atual que não vêm do TFS.
            if (string.IsNullOrEmpty(project.HiddenColumns) && !string.IsNullOrEmpty(Project?.HiddenColumns))
                project.HiddenColumns = Project.HiddenColumns;
            if (string.IsNullOrEmpty(project.HiddenColumnsExpanded) && !string.IsNullOrEmpty(Project?.HiddenColumnsExpanded))
                project.HiddenColumnsExpanded = Project.HiddenColumnsExpanded;
            if (!project.ShowOriginalHoursColumn && Project?.ShowOriginalHoursColumn == true)
                project.ShowOriginalHoursColumn = Project.ShowOriginalHoursColumn;

            Project = project;
            RestoreAllocationPercentByDevOpsTask(Project.Tasks, existingAllocations);
            RestoreAvailabilityByResourceKey(Project.Resources, existingAvailability);
            RestoreOriginalEstimatedHours(Project.Tasks, existingOriginalHours);
            RestoreNoDevOpsTasks(noDevOpsTasks);
            ClearImportedSyncConflicts(Project.Tasks);
            ApplyProjectSprintSettingsToViewModel(project);
            _nextId = AllTasks().Select(t => t.Id).DefaultIfEmpty(0).Max() + 1;
            RebuildFlatTasks();
            ApplyVirtualPredecessorsToAll();
            Project.IsDirty = true;
            StatusMessage = statusMessage ?? "Projeto importado.";
        }

        private static void ClearImportedSyncConflicts(IEnumerable<Models.ProjectTask> tasks)
        {
            foreach (var task in tasks)
            {
                if (task.TfsId is > 0 && task.SyncVersion.HasValue)
                    task.HasSyncConflict = false;

                ClearImportedSyncConflicts(task.Children);
            }
        }

        private sealed record InternalTaskSnapshot(
            Models.ProjectTask Task,
            int? ParentTfsId,
            int? PreviousSiblingTfsId,
            int? NextSiblingTfsId);

        private List<InternalTaskSnapshot> CaptureNoDevOpsTasks()
        {
            var result = new List<InternalTaskSnapshot>();
            if (Project == null) return result;

            void Collect(IList<Models.ProjectTask> tasks)
            {
                for (var i = 0; i < tasks.Count; i++)
                {
                    var t = tasks[i];
                    if (ShouldCaptureInternalTaskOnImport(t))
                    {
                        result.Add(new InternalTaskSnapshot(
                            t,
                            t.Parent?.TfsId,
                            FindSiblingTfsId(tasks, i - 1, -1),
                            FindSiblingTfsId(tasks, i + 1, 1)));
                        continue;
                    }

                    Collect(t.Children);
                }
            }

            Collect(Project.Tasks);
            return result;
        }

        private void RestoreNoDevOpsTasks(List<InternalTaskSnapshot> internalTasks)
        {
            if (internalTasks.Count == 0) return;

            // Mapa nome -> tarefa importada do TFS. Se uma atividade DevOps interna (I)
            // voltou do TFS com o mesmo nome, ela passa a ser a T importada, sem duplicar.
            var importedDevOpsByName = new System.Collections.Generic.Dictionary<string, Models.ProjectTask>(StringComparer.OrdinalIgnoreCase);
            void IndexImportedDevOpsByName(IEnumerable<Models.ProjectTask> tasks)
            {
                foreach (var t in tasks)
                {
                    if (t.HasTfsLink && !string.IsNullOrWhiteSpace(t.Name))
                        importedDevOpsByName[t.Name.Trim()] = t;
                    IndexImportedDevOpsByName(t.Children);
                }
            }
            IndexImportedDevOpsByName(Project.Tasks);

            // Mapa TfsId → tarefa importada (para encontrar o pai)
            var byTfsId = new System.Collections.Generic.Dictionary<int, Models.ProjectTask>();
            void IndexByTfsId(IEnumerable<Models.ProjectTask> tasks)
            {
                foreach (var t in tasks)
                {
                    if (t.TfsId is > 0) byTfsId[t.TfsId.Value] = t;
                    IndexByTfsId(t.Children);
                }
            }
            IndexByTfsId(Project.Tasks);

            foreach (var snapshot in internalTasks)
            {
                var task = snapshot.Task;
                var isNoDevOps = IsNoDevOpsType(task.TfsType);

                if (!isNoDevOps &&
                    !string.IsNullOrWhiteSpace(task.Name) &&
                    importedDevOpsByName.ContainsKey(task.Name.Trim()))
                {
                    continue;
                }

                // Re-insere sob o mesmo pai (por TfsId) ou na raiz
                if (snapshot.ParentTfsId.HasValue && byTfsId.TryGetValue(snapshot.ParentTfsId.Value, out var parent))
                {
                    task.Parent = parent;
                    task.Level = parent.Level + 1;
                    InsertNoDevOpsTask(parent.Children, task, snapshot.PreviousSiblingTfsId, snapshot.NextSiblingTfsId);
                    parent.IsSummary = true;
                }
                else
                {
                    task.Parent = null;
                    task.Level = 0;
                    InsertNoDevOpsTask(Project.Tasks, task, snapshot.PreviousSiblingTfsId, snapshot.NextSiblingTfsId);
                }
            }
        }

        private static bool ShouldCaptureInternalTaskOnImport(Models.ProjectTask task)
        {
            if (task.IsSummary)
                return false;

            if (IsNoDevOpsType(task.TfsType))
                return true;

            return !task.HasTfsLink && IsDevOpsActivityType(task.TfsType);
        }

        private static int? FindSiblingTfsId(IList<Models.ProjectTask> siblings, int startIndex, int step)
        {
            for (var i = startIndex; i >= 0 && i < siblings.Count; i += step)
            {
                if (siblings[i].TfsId is > 0)
                    return siblings[i].TfsId;
            }

            return null;
        }

        private static void InsertNoDevOpsTask(
            IList<Models.ProjectTask> siblings,
            Models.ProjectTask task,
            int? previousSiblingTfsId,
            int? nextSiblingTfsId)
        {
            if (nextSiblingTfsId.HasValue)
            {
                var nextIndex = IndexOfTfsId(siblings, nextSiblingTfsId.Value);
                if (nextIndex >= 0)
                {
                    siblings.Insert(nextIndex, task);
                    return;
                }
            }

            if (previousSiblingTfsId.HasValue)
            {
                var previousIndex = IndexOfTfsId(siblings, previousSiblingTfsId.Value);
                if (previousIndex >= 0)
                {
                    siblings.Insert(previousIndex + 1, task);
                    return;
                }
            }

            siblings.Add(task);
        }

        private static int IndexOfTfsId(IList<Models.ProjectTask> siblings, int tfsId)
        {
            for (var i = 0; i < siblings.Count; i++)
            {
                if (siblings[i].TfsId == tfsId)
                    return i;
            }

            return -1;
        }

        private Dictionary<(int taskId, string resourceKey), double> CaptureAllocationPercentByDevOpsTask()
        {
            var result = new Dictionary<(int, string), double>();
            foreach (var task in AllTasks())
            {
                if (!task.TfsId.HasValue || task.TfsId.Value <= 0)
                    continue;

                foreach (var assignment in task.Resources)
                {
                    var key = GetResourceKey(assignment.Resource);
                    if (!string.IsNullOrWhiteSpace(key))
                        result[(task.TfsId.Value, key)] = assignment.AllocationPercent;
                }
            }

            return result;
        }

        private static void RestoreAllocationPercentByDevOpsTask(
            IEnumerable<ProjectTask> tasks,
            Dictionary<(int taskId, string resourceKey), double> existingAllocations)
        {
            foreach (var task in tasks)
            {
                if (task.TfsId.HasValue && task.TfsId.Value > 0)
                {
                    foreach (var assignment in task.Resources)
                    {
                        var key = GetResourceKey(assignment.Resource);
                        if (!string.IsNullOrWhiteSpace(key) &&
                            existingAllocations.TryGetValue((task.TfsId.Value, key), out var allocationPercent))
                        {
                            assignment.AllocationPercent = allocationPercent;
                        }
                    }

                    TaskScheduleService.RecalculateFinishFromAssignments(task);
                }

                RestoreAllocationPercentByDevOpsTask(task.Children, existingAllocations);
                task.RecalcSummary();
            }
        }

        private Dictionary<string, double> CaptureAvailabilityByResourceKey()
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Project.Resources)
            {
                var key = GetResourceKey(r);
                if (!string.IsNullOrWhiteSpace(key))
                    result[key] = r.AvailabilityPercent;
            }
            return result;
        }

        private static void RestoreAvailabilityByResourceKey(
            IEnumerable<Models.Resource> resources,
            Dictionary<string, double> saved)
        {
            foreach (var r in resources)
            {
                var key = GetResourceKey(r);
                if (!string.IsNullOrWhiteSpace(key) && saved.TryGetValue(key, out var pct))
                    r.AvailabilityPercent = pct;
            }
        }

        private Dictionary<int, double> CaptureOriginalEstimatedHoursByTfsId()
        {
            var result = new Dictionary<int, double>();
            foreach (var task in AllTasks())
                if (task.TfsId is > 0 && task.OriginalEstimatedHours is > 0)
                    result[task.TfsId.Value] = task.OriginalEstimatedHours.Value;
            return result;
        }

        private static void RestoreOriginalEstimatedHours(
            IEnumerable<Models.ProjectTask> tasks,
            Dictionary<int, double> saved)
        {
            foreach (var task in tasks)
            {
                if (task.TfsId is > 0)
                {
                    if (task.OriginalEstimatedHours == null || task.OriginalEstimatedHours <= 0)
                    {
                        // TFS não trouxe Esforço Estimado — restaura do valor salvo antes do import.
                        if (saved.TryGetValue(task.TfsId.Value, out var orig))
                            task.OriginalEstimatedHours = orig;
                    }
                }
                RestoreOriginalEstimatedHours(task.Children, saved);
            }
            SyncOriginalHoursWhenZeroPercent(tasks);
        }

        // Quando % = 0, nenhum trabalho foi feito: OrgH deve espelhar HH Restante.
        // Tarefas sem tipo definido (TfsType nulo) são classificadas como "No DevOps"
        // para garantir que nunca sejam enviadas ao TFS acidentalmente.
        private void NormalizeNoDevOpsType(IEnumerable<Models.ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                if (!t.IsSummary && string.IsNullOrWhiteSpace(t.TfsType))
                    t.TfsType = "No DevOps";
                // Garante TfsId negativo único para tarefas No DevOps sem ID negativo
                if (IsNoDevOpsType(t.TfsType) && !(t.TfsId < 0))
                    t.TfsId = _nextNoDevOpsId--;
                NormalizeNoDevOpsType(t.Children);
            }
        }

        private static void SyncOriginalHoursWhenZeroPercent(IEnumerable<Models.ProjectTask> tasks)
        {
            foreach (var task in tasks)
            {
                // Tasks DevOps (TfsType="Task") têm OriginalEstimatedHours gerenciado pelo rateio — não tocar aqui
                bool isDevOpsTask = string.Equals(task.TfsType?.Trim(), "Task", StringComparison.OrdinalIgnoreCase);
                if (!task.IsSummary && !isDevOpsTask && !(task.OriginalEstimatedHours is > 0))
                {
                    // Para % = 0: EstimatedHours é a duração total planejada.
                    // Para % > 0: usa CurrentHours + EstimatedHours (total = atual + restante).
                    var cur = task.CurrentHours ?? 0;
                    var est = task.EstimatedHours ?? 0;
                    var h = (cur > 0 || est > 0)
                        ? cur + est
                        : ProjectCalendarService.CountWorkingHours(task.Start, task.Finish);
                    if (h > 0) task.OriginalEstimatedHours = h;
                }
                SyncOriginalHoursWhenZeroPercent(task.Children);
            }
        }

        private static string GetResourceKey(Resource? resource)
        {
            if (resource == null)
                return string.Empty;

            return !string.IsNullOrWhiteSpace(resource.Email)
                ? resource.Email.Trim().ToLowerInvariant()
                : resource.Name.Trim().ToLowerInvariant();
        }

        /// <summary>Reconstrói a lista plana (ex.: após sincronizar com o TFS, quando
        /// IDs/vínculos de tarefas mudam fora da grade).</summary>
        public void RefreshTasks()
        {
            RebuildFlatTasks();
        }

        public Dictionary<int, double> CaptureTaskWorkingDurations()
        {
            return FlatTasks
                .Where(t => !t.IsSummary)
                .ToDictionary(t => t.Id, t => Math.Max(0.0, t.DurationHours));
        }

        private bool _cascading;

        private DateTime? GetVirtualPredecessorStart(TaskViewModel task)
        {
            if (task.IsSummary || task.Model.PredecessorIds.Count > 0)
                return null;

            var resourceId = task.Model.Resources.FirstOrDefault()?.ResourceId;
            if (resourceId == null)
                return null;

            var taskIndex = FlatTasks.IndexOf(task);
            if (taskIndex < 0)
                return null;

            for (int i = taskIndex - 1; i >= 0; i--)
            {
                var prev = FlatTasks[i];
                if (prev.Depth < task.Depth) break;
                if (prev.Depth > task.Depth) continue;
                if (!ReferenceEquals(prev.ParentViewModel, task.ParentViewModel)) break;
                if (prev.IsSummary) continue;
                if (prev.Model.Resources.FirstOrDefault()?.ResourceId != resourceId) continue;

                var prevFinish = ProjectCalendarService.GetInclusiveFinishDate(prev.Model.Start, prev.Model.Finish);
                return ProjectCalendarService.AddWorkingDays(prevFinish, 1);
            }

            return null;
        }

        private void CascadeSuccessors(TaskViewModel changed, bool allowStartedReposition = false)
        {
            if (_cascading) return;
            _cascading = true;
            try
            {
                var visited = new System.Collections.Generic.HashSet<int> { changed.Model.Id };
                var queue = new System.Collections.Generic.Queue<int>();
                queue.Enqueue(changed.Model.Id);

                while (queue.Count > 0)
                {
                    var currentId = queue.Dequeue();
                    var current = FlatTasks.FirstOrDefault(t => t.Model.Id == currentId);

                    // Predecessoras explícitas
                    foreach (var task in FlatTasks)
                    {
                        if (!task.Model.PredecessorIds.Contains(currentId)) continue;
                        if (!visited.Add(task.Model.Id)) continue;

                        var oldFinish = task.Model.Finish;
                        task.MoveAfterLatestPredecessor(allowStartedReposition);
                        if (task.Model.Finish != oldFinish)
                            queue.Enqueue(task.Model.Id);
                    }

                    // Predecessor virtual: irmãos do mesmo pai, mesmo recurso, sem predecessoras, após a tarefa alterada
                    if (current != null)
                        CascadeVirtualSiblings(current, visited, queue, allowStartedReposition);
                }
            }
            finally
            {
                _cascading = false;
            }
        }

        private void CascadeVirtualSiblings(
            TaskViewModel changed,
            System.Collections.Generic.HashSet<int> visited,
            System.Collections.Generic.Queue<int> queue,
            bool allowStartedReposition = false)
        {
            if (changed.IsSummary)
                return;

            // Só propaga para irmãos sem predecessoras explícitas, mesmo pai e mesmo recurso primário
            var changedResource = changed.Model.Resources.FirstOrDefault()?.ResourceId;
            if (changedResource == null) return;

            var changedIndex = FlatTasks.IndexOf(changed);
            if (changedIndex < 0) return;

            // Quando a task voltou a % = 0 e não tem data fixa, reposiciona após o irmão anterior
            // do mesmo recurso (caso o Start tenha sido ancorado em Today quando % foi definido).
            if (changed.Model.PercentComplete < 0.0001 && !changed.Model.StartFixed &&
                changed.Model.PredecessorIds.Count == 0)
            {
                var newStart = GetVirtualPredecessorStart(changed);
                if (newStart.HasValue && changed.Model.Start.Date != newStart.Value.Date)
                {
                    changed.Model.Start  = newStart.Value;
                    changed.Model.Finish = TaskScheduleService.CalculateFinishFromAssignments(changed.Model, newStart.Value);
                    changed.NotifyDatesChanged();
                }
            }

            var changedFinish = ProjectCalendarService.GetInclusiveFinishDate(changed.Model.Start, changed.Model.Finish);

            for (int i = changedIndex + 1; i < FlatTasks.Count; i++)
            {
                var sibling = FlatTasks[i];

                // Para quando sair do grupo (profundidade menor = subiu na hierarquia)
                if (sibling.Depth < changed.Depth) break;
                // Pula filhos mais profundos
                if (sibling.Depth > changed.Depth) continue;
                // Deve ser do mesmo pai
                if (!ReferenceEquals(sibling.ParentViewModel, changed.ParentViewModel)) break;
                // Predecessor virtual só vale entre tarefas folha, nunca agrupadores/features.
                if (sibling.IsSummary) continue;
                // Deve ter o mesmo recurso primário
                if (sibling.Model.Resources.FirstOrDefault()?.ResourceId != changedResource) continue;
                // Não reprocessar
                if (!visited.Add(sibling.Model.Id)) continue;

                var nextStart = ProjectCalendarService.AddWorkingDays(changedFinish, 1);

                // Tarefas com predecessoras explícitas: aplica restrição de recurso só se o virtual
                // empurrar para frente (nunca recua uma data já fixada pela predecessora explícita).
                if (sibling.Model.PredecessorIds.Count > 0)
                {
                    if (nextStart.Date <= sibling.Model.Start.Date)
                    {
                        changedFinish = ProjectCalendarService.GetInclusiveFinishDate(sibling.Model.Start, sibling.Model.Finish);
                        continue;
                    }
                    // nextStart > sibling.Start → conflito de recurso: empurra para frente
                }

                if (sibling.Model.Start.Date == nextStart.Date)
                {
                    // Já na posição correta; encadeia a partir do fim deste
                    changedFinish = ProjectCalendarService.GetInclusiveFinishDate(sibling.Model.Start, sibling.Model.Finish);
                    continue;
                }

                // Data fixada (📌) não move o Start; tarefa iniciada só é reposicionada
                // quando o recálculo geral de predecessoras está normalizando o cronograma.
                if (sibling.Model.StartFixed || (!allowStartedReposition && sibling.Model.PercentComplete > 0))
                {
                    changedFinish = ProjectCalendarService.GetInclusiveFinishDate(sibling.Model.Start, sibling.Model.Finish);
                    continue;
                }

                var oldFinish = sibling.Model.Finish;
                sibling.Model.Start = nextStart;
                sibling.Model.Finish = TaskScheduleService.CalculateFinishFromAssignments(sibling.Model, nextStart);

                sibling.NotifyDatesChanged();

                if (sibling.Model.Finish != oldFinish)
                    queue.Enqueue(sibling.Model.Id);

                // O próximo irmão deve considerar o fim deste
                changedFinish = ProjectCalendarService.GetInclusiveFinishDate(sibling.Model.Start, sibling.Model.Finish);
            }
        }

        // Quando o recurso de uma tarefa muda, reposiciona ela com base no novo recurso
        // e libera a lacuna deixada no recurso antigo.
        private void OnPrimaryResourceChanged(TaskViewModel changed, int? oldResourceId)
        {
            if (_cascading) return;
            if (changed == null) return;
            if (changed.IsSummary) return;

            try
            {
                var changedIndex = FlatTasks.IndexOf(changed);
                if (changedIndex < 0) return; // tarefa não visível (filtrada)

                // 1. Reposicionar a tarefa no contexto do NOVO recurso:
                //    procura o último irmão com o novo recurso antes desta tarefa.
                if (changed.Model.PredecessorIds.Count == 0)
                {
                    var newResourceId = changed.Model.Resources.FirstOrDefault()?.ResourceId;

                    if (newResourceId != null && changedIndex > 0)
                    {
                        DateTime? lastFinish = null;
                        for (int i = changedIndex - 1; i >= 0; i--)
                        {
                            var prev = FlatTasks[i];
                            if (prev.Depth < changed.Depth) break;
                            if (prev.Depth > changed.Depth) continue;
                            if (!ReferenceEquals(prev.ParentViewModel, changed.ParentViewModel)) break;
                            if (prev.IsSummary) continue;
                            if (prev.Model.Resources.FirstOrDefault()?.ResourceId != newResourceId) continue;

                            lastFinish = ProjectCalendarService.GetInclusiveFinishDate(prev.Model.Start, prev.Model.Finish);
                            break;
                        }

                        if (lastFinish.HasValue)
                        {
                            var nextStart = ProjectCalendarService.AddWorkingDays(lastFinish.Value, 1);
                            if (changed.Model.Start.Date != nextStart.Date)
                            {
                                changed.Model.Start  = nextStart;
                                changed.Model.Finish = TaskScheduleService.CalculateFinishFromAssignments(changed.Model, nextStart);
                                changed.NotifyDatesChanged();
                            }
                        }
                    }
                }

                // 2. Cascatar irmãos do NOVO recurso após a tarefa alterada.
                CascadeSuccessors(changed);

                // 3. Cascatar irmãos do recurso ANTIGO — a lacuna deixada permite que avancem.
                if (oldResourceId != null)
                {
                    for (int i = changedIndex + 1; i < FlatTasks.Count; i++)
                    {
                        var sibling = FlatTasks[i];
                        if (sibling.Depth < changed.Depth) break;
                        if (sibling.Depth > changed.Depth) continue;
                        if (!ReferenceEquals(sibling.ParentViewModel, changed.ParentViewModel)) break;
                        if (sibling.IsSummary) continue;
                        if (sibling.Model.Resources.FirstOrDefault()?.ResourceId != oldResourceId) continue;
                        if (sibling.Model.PredecessorIds.Count > 0) continue;

                        CascadeSuccessors(sibling);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OnPrimaryResourceChanged] {ex}");
                StatusMessage = AppStrings.Get("Status_RecalcError");
            }
        }

        // Após arrasto: reposiciona a tarefa com base no irmão anterior do mesmo recurso
        // e cascata os irmãos afetados (origem e destino).
        private void RescheduleAfterDrop(TaskViewModel moved)
        {
            if (moved.IsSummary) return;
            if (moved.Model.PredecessorIds.Count > 0) return;

            var resourceId = moved.Model.Resources.FirstOrDefault()?.ResourceId;
            if (resourceId == null) return;

            var movedIndex = FlatTasks.IndexOf(moved);
            if (movedIndex < 0) return;

            // 1. Procura o último irmão anterior com mesmo recurso → define o início da tarefa movida
            DateTime? lastFinishBefore = null;
            for (int i = movedIndex - 1; i >= 0; i--)
            {
                var prev = FlatTasks[i];
                if (prev.Depth < moved.Depth) break;
                if (prev.Depth > moved.Depth) continue;
                if (!ReferenceEquals(prev.ParentViewModel, moved.ParentViewModel)) break;
                if (prev.IsSummary) continue;
                if (prev.Model.Resources.FirstOrDefault()?.ResourceId != resourceId) continue;

                lastFinishBefore = ProjectCalendarService.GetInclusiveFinishDate(prev.Model.Start, prev.Model.Finish);
                break;
            }

            if (lastFinishBefore.HasValue)
            {
                var nextStart = ProjectCalendarService.AddWorkingDays(lastFinishBefore.Value, 1);
                if (moved.Model.Start.Date != nextStart.Date)
                {
                    moved.Model.Start  = nextStart;
                    moved.Model.Finish = TaskScheduleService.CalculateFinishFromAssignments(moved.Model, nextStart);
                    moved.NotifyDatesChanged();
                }
            }

            // 2. Cascata os irmãos seguintes (novo vizinho agora pode precisar recalcular)
            CascadeSuccessors(moved);

            // 3. Cascata a partir do primeiro irmão do mesmo recurso após o buraco deixado
            //    (posição original — já que RebuildFlatTasks reorganizou, cascateamos do topo do grupo)
            for (int i = 0; i < FlatTasks.Count; i++)
            {
                var t = FlatTasks[i];
                if (t.IsSummary) continue;
                if (t.Depth != moved.Depth) continue;
                if (!ReferenceEquals(t.ParentViewModel, moved.ParentViewModel)) continue;
                if (t.Model.Resources.FirstOrDefault()?.ResourceId != resourceId) continue;
                if (t.Model.PredecessorIds.Count > 0) continue;
                if (t == moved) break; // já tratado acima

                CascadeSuccessors(t);
                break;
            }
        }

        // Percorre todos os grupos de irmãos e aplica predecessor virtual para todos.
        // Chamado ao abrir ou importar projeto para garantir consistência inicial.
        public void ApplyVirtualPredecessorsToAll()
        {
            // 0. Marcos (milestones) não têm recurso e não entram na fila por (pai + recurso);
            //    usam a irmã imediatamente anterior no mesmo pai como predecessora virtual,
            //    para preservar o posicionamento ao sincronizar/importar do DevOps.
            ApplyMilestonePredecessors();

            // 1. Cascata virtual: primeiro de cada grupo (pai + recurso) propaga para os irmãos seguintes.
            var processed = new System.Collections.Generic.HashSet<(int parentId, int resourceId)>();
            foreach (var task in FlatTasks)
            {
                if (task.IsSummary) continue;
                if (task.Model.PredecessorIds.Count > 0) continue;
                var resourceId = task.Model.Resources.FirstOrDefault()?.ResourceId;
                if (resourceId == null) continue;
                var parentId = task.ParentViewModel?.Model.Id ?? -1;
                var key = (parentId, resourceId.Value);
                if (!processed.Add(key)) continue;

                CascadeSuccessors(task, allowStartedReposition: true);
            }

            // 2. Predecessoras explícitas: processa em ordem topológica.
            //    Uma tarefa só é recalculada depois que todas as suas predecessoras já foram processadas.
            //    Assim cada tarefa consulta todas as predecessoras e pega a maior data fim.
            var tasksWithPred = FlatTasks
                .Where(t => !t.IsSummary && t.Model.PredecessorIds.Count > 0)
                .ToList();

            // IDs ainda pendentes de processamento
            var pending = new System.Collections.Generic.HashSet<int>(tasksWithPred.Select(t => t.Model.Id));
            var knownIds = new System.Collections.Generic.HashSet<int>(FlatTasks.Select(t => t.Model.Id));
            int maxIterations = pending.Count + 1; // evita loop infinito em referência cruzada

            while (pending.Count > 0 && maxIterations-- > 0)
            {
                // Tarefas prontas: todas as predecessoras já foram processadas (ou não estão pendentes)
                var ready = tasksWithPred
                    .Where(t => pending.Contains(t.Model.Id) &&
                                t.Model.PredecessorIds.All(pid => !pending.Contains(pid)))
                    .ToList();

                if (ready.Count == 0)
                    break; // apenas referências cruzadas restam — interrompe sem loop infinito

                foreach (var task in ready)
                {
                    task.MoveAfterLatestPredecessor(allowStartedReposition: true); // consulta todas predecessoras → pega maior data fim
                    pending.Remove(task.Model.Id);
                }
            }
        }

        // Marcos usam como predecessora virtual o irmão anterior no MESMO NÍVEL
        // (mesmo pai). Se não houver irmão anterior nesse nível (marco é 1º/único
        // filho), sobe para o nível do pai e procura o irmão anterior do pai, e
        // assim por diante — nunca desce para dentro de um ramo anterior (ex.: um
        // marco filho de uma Feature que vem depois de outra Feature com Stories
        // usa a Feature anterior como predecessora, não a última Story dela).
        // Recalculado a cada chamada — se o usuário arrastar o marco, a
        // predecessora é corrigida automaticamente antes da sincronização.
        public void ApplyMilestonePredecessors()
        {
            static ProjectTask? FindVirtualPredecessor(ProjectTask task)
            {
                var current = task;
                while (current.Parent != null)
                {
                    var siblings = current.Parent.Children;
                    var idx = siblings.IndexOf(current);
                    if (idx > 0)
                        return siblings[idx - 1];

                    current = current.Parent;
                }
                return null;
            }

            foreach (var task in AllTasks())
            {
                if (!task.IsMilestone) continue;

                var prev = FindVirtualPredecessor(task);
                if (prev == null)
                {
                    if (task.PredecessorIds.Count > 0) task.PredecessorIds.Clear();
                    continue;
                }

                if (task.PredecessorIds.Count == 1 && task.PredecessorIds[0] == prev.Id)
                    continue;

                task.PredecessorIds.Clear();
                task.PredecessorIds.Add(prev.Id);
            }
        }

        public void RecalculateScheduleRespectingAssignments()
        {
            foreach (var task in AllTasks().Where(t => !t.IsSummary && !t.FinishFixed))
                TaskScheduleService.RecalculateFinishFromAssignments(task);

            foreach (var root in Project.Tasks)
                root.RecalcSummary();

            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = AppStrings.Get("Status_DatesRecalculated");
        }

        public void RecalculateScheduleFromCalendar(Dictionary<int, double> durationByTaskId)
        {
            if (durationByTaskId.Count == 0)
                return;

            foreach (var task in AllTasks().Where(t => !t.IsSummary))
            {
                if (!durationByTaskId.ContainsKey(task.Id))
                    continue;

                if (!task.FinishFixed)
                    task.Finish = task.IsMilestone
                        ? task.Start
                        : TaskScheduleService.CalculateFinishFromAssignments(task, task.Start);
            }

            foreach (var root in Project.Tasks)
                root.RecalcSummary();

            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = AppStrings.Get("Status_CalendarApplied");
        }

        [RelayCommand]
        private void ImportMspdi()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "MS Project XML (*.xml)|*.xml|Todos os arquivos (*.*)|*.*",
                Title = "Importar projeto do Microsoft Project (MSPDI XML)"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var project = MspdiImportService.Import(dlg.FileName);
                Project = project;
                ApplyProjectSprintSettingsToViewModel(project);
                RecalcIdCounters();
                RebuildFlatTasks();
                StatusMessage = $"MS Project importado: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao importar MS Project XML:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void ImportExcel()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Excel XML 2003 (*.xml)|*.xml|Arquivos Excel antigos (*.xls)|*.xls|Todos os arquivos (*.*)|*.*",
                Title = "Importar projeto do Excel"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var project = ExcelXmlService.Import(dlg.FileName);
                Project = project;
                ApplyProjectSprintSettingsToViewModel(project);
                RecalcIdCounters();
                RebuildFlatTasks();
                StatusMessage = $"Excel importado: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao importar Excel:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void SaveProject()
        {
            if (string.IsNullOrEmpty(Project.FilePath))
            {
                SaveAsProject();
                return;
            }
            Save(Project.FilePath);
        }

        [RelayCommand]
        private void SaveAsProject()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Projeto NXProject (*.xml)|*.xml",
                Title = "Salvar Projeto",
                FileName = Project.Name
            };
            if (dlg.ShowDialog() == true)
                Save(dlg.FileName);
        }

        private void Save(string path)
        {
            try
            {
                XmlProjectService.Save(Project, path);
                Project.FilePath = path;
                Project.IsDirty = false;
                StatusMessage = $"Salvo: {path}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao salvar:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void ExportOpenProj()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "OpenProj (*.pod)|*.pod",
                Title = "Exportar para OpenProj",
                FileName = Project.Name
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                OpenProjExportService.Export(Project, dlg.FileName);
                StatusMessage = $"Exportado OpenProj: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao exportar OpenProj:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void ExportMspdi()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "MS Project XML (*.xml)|*.xml",
                Title = "Exportar para MS Project XML (MSPDI)",
                FileName = Project.Name
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                MspdiExportService.Export(Project, dlg.FileName);
                StatusMessage = $"Exportado MSPDI: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao exportar MSPDI:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void ExportCsv()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv",
                Title = "Exportar para CSV",
                FileName = Project.Name
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    CsvService.Export(AllTasks().ToList(), dlg.FileName);
                    StatusMessage = $"Exportado: {dlg.FileName}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao exportar:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        [RelayCommand]
        private void ExportExcel()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel XML 2003 (*.xml)|*.xml",
                Title = "Exportar para Excel",
                FileName = Project.Name
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                ExcelXmlService.Export(Project, AllTasks().ToList(), dlg.FileName);
                StatusMessage = $"Exportado Excel: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao exportar Excel:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void SaveSprintSettingsAsDefault()
        {
            try
            {
                SprintSettingsStore.Save(Project.GetSprintSettingsProfile(), _sprintSettingsStorageKey);
                StatusMessage = AppStrings.Get("Status_SprintSaved");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao salvar configuracao padrao de sprint:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

#if !COMMUNITY
        [RelayCommand]
        private void PrintProject()
        {
            try
            {
                if (PrintService.PrintProject(Project, FlatTasks, pdfMode: false))
                    StatusMessage = AppStrings.Get("Status_PrintSent");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao imprimir:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void PrintProjectPdf()
        {
            try
            {
                if (PrintService.PrintProject(Project, FlatTasks, pdfMode: true))
                    StatusMessage = AppStrings.Get("Status_PdfStarted");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao gerar PDF:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
#endif

        [RelayCommand]
        private void AddTask()
        {
            var selected = SelectedTask;
            var start = selected?.Start
                ?? AllTasks().Select(t => t.Start).DefaultIfEmpty(Project.StartDate).Min();

            // Copia tipo, recurso e sprint da tarefa selecionada
            var task = new ProjectTask
            {
                Id = _nextId++,
                Name = "Nova Tarefa",
                Start = start,
                Finish = ProjectCalendarService.AddWorkingHours(start, ProjectCalendarService.WorkingHoursPerDay),
                TfsType = selected?.Model.TfsType,
                TfsClassification = selected?.Model.TfsType, // default = tipo; editável pelo usuário
                TfsIterationPath = selected?.Model.TfsIterationPath,
                // TfsId=0 indica "criar no TFS"; negativo = No DevOps (local, único para predecessoras)
                TfsId = IsNoDevOpsType(selected?.Model.TfsType)
                        ? _nextNoDevOpsId-- : 0,
                TfsState = IsNoDevOpsType(selected?.Model.TfsType) ? null : "New"
            };
            // Copia o primeiro recurso da tarefa selecionada
            if (selected?.Model.Resources.Count > 0)
            {
                var srcRes = selected.Model.Resources[0];
                task.Resources.Add(new Models.TaskResource
                {
                    ResourceId       = srcRes.ResourceId,
                    Resource         = srcRes.Resource,
                    AllocationPercent = srcRes.AllocationPercent,
                    EstimatedHours   = null
                });
            }

            if (selected == null)
            {
                Project.Tasks.Add(task);
            }
            else if (selected.Model.Parent == null)
            {
                var idx = Project.Tasks.IndexOf(selected.Model);
                Project.Tasks.Insert(idx + 1, task);
            }
            else
            {
                var parent = selected.Model.Parent;
                task.Level = selected.Model.Level;
                task.Parent = parent;
                var idx = parent.Children.IndexOf(selected.Model);
                parent.Children.Insert(idx + 1, task);
                parent.RecalcSummary();
            }

            Project.IsDirty = true;
            PrepareTaskInsertionScroll?.Invoke();
            RebuildFlatTasks();
            SelectedTask = FlatTasks.FirstOrDefault(t => t.Id == task.Id);
            StatusMessage = AppStrings.Get("Status_TaskAdded");
            RequestScrollToSelected?.Invoke();
        }

        [RelayCommand]
        private void AddSubtask()
        {
            if (SelectedTask == null) { StatusMessage = AppStrings.Get("Status_SelectParentTask"); return; }

            var parent = SelectedTask.Model;
            var previousSibling = parent.Children.LastOrDefault();

            // Deriva o tipo da sub-atividade com base no tipo do pai:
            // Epic → Feature, Feature → Story, Story → pergunta (Task DevOps ou interno), outro → No DevOps
            string childType;
            var parentType = parent.TfsType?.Trim();
            if (parentType == "Epic")
            {
                childType = "Feature";
            }
            else if (parentType == "Feature")
            {
                childType = "Story";
            }
            else if (IsStoryLikeType(parentType))
            {
                if (AskSubtaskAction != null)
                {
                    var parentVm = FlatTasks.FirstOrDefault(t => t.Model == parent);
                    var action = AskSubtaskAction.Invoke(parentVm ?? new TaskViewModel(parent));
                    if (action == null || action == "Cancel") return;
                    if (action == "Fetch") return; // View dispara busca; não cria subtarefa
                    childType = action == "Task" ? "Task" : "No DevOps";
                }
                else
                {
                    childType = "No DevOps";
                }
            }
            else
            {
                childType = "No DevOps";
            }
            var isDevOps = !IsNoDevOpsType(childType);
            var task = new ProjectTask
            {
                Id       = _nextId++,
                Name     = isDevOps ? "Nova Tarefa" : "Nova Subtarefa",
                Start    = parent.Start,
                Finish   = ProjectCalendarService.AddWorkingHours(parent.Start, ProjectCalendarService.WorkingHoursPerDay * 3.0),
                Level    = parent.Level + 1,
                Parent   = parent,
                TfsType  = childType,
                TfsIterationPath = parent.TfsIterationPath,
                TfsId    = isDevOps ? 0 : _nextNoDevOpsId--,
                TfsState = isDevOps ? "New" : null
            };

            parent.Children.Add(task);
            parent.IsSummary = true;
            parent.RecalcSummary();
            Project.IsDirty = true;
            PrepareTaskInsertionScroll?.Invoke();
            RebuildFlatTasks();
            SelectedTask = FlatTasks.FirstOrDefault(t => t.Id == task.Id);
            StatusMessage = previousSibling != null
                ? "Subtarefa adicionada com predecessora da subtarefa anterior."
                : "Subtarefa adicionada";
            RequestScrollToSelected?.Invoke();
        }

        public void AddMilestone(bool asChild)
        {
            var selected = SelectedTask;
            if (asChild && selected == null)
            {
                StatusMessage = AppStrings.Get("Status_SelectParentTask");
                return;
            }

            var selectedModel = selected?.Model;
            if (asChild && selectedModel?.IsMilestone == true)
            {
                StatusMessage = "Marco nao pode ter outro marco como filho. Use clique simples para criar um marco irmao.";
                return;
            }

            var isDevOpsMilestone = selectedModel != null && IsDevOpsActivityType(selectedModel.TfsType);
            var milestoneType = isDevOpsMilestone ? "Marco-Devops" : "No DevOps";

            ProjectTask? parent = null;
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> siblings;
            var insertIndex = -1;
            ProjectTask? predecessor = null;

            if (asChild)
            {
                parent = selectedModel;
                siblings = parent!.Children;
                predecessor = siblings.LastOrDefault();
                insertIndex = siblings.Count;
            }
            else if (selectedModel?.Parent != null)
            {
                parent = selectedModel.Parent;
                siblings = parent.Children;
                insertIndex = siblings.IndexOf(selectedModel) + 1;
                predecessor = selectedModel;
            }
            else
            {
                siblings = Project.Tasks;
                if (selectedModel != null)
                {
                    insertIndex = Project.Tasks.IndexOf(selectedModel) + 1;
                    predecessor = selectedModel;
                }
                else
                {
                    insertIndex = Project.Tasks.Count;
                }
            }

            var start = ResolveMilestoneStart(parent, predecessor, selectedModel);
            var task = new ProjectTask
            {
                Id = _nextId++,
                Name = isDevOpsMilestone ? "Novo Marco DevOps" : "Novo Marco",
                Start = start,
                Finish = start,
                Level = parent != null ? parent.Level + 1 : selectedModel?.Level ?? 0,
                Parent = parent,
                IsMilestone = true,
                EstimatedHours = 0,
                OriginalEstimatedHours = 0,
                CurrentHours = 0,
                PercentComplete = 0,
                TfsType = milestoneType,
                TfsClassification = milestoneType,
                TfsIterationPath = selectedModel?.TfsIterationPath ?? parent?.TfsIterationPath,
                TfsId = isDevOpsMilestone ? 0 : _nextNoDevOpsId--,
                IsPendingTfsCreate = isDevOpsMilestone,
                TfsState = isDevOpsMilestone ? "New" : null,
                Tags = isDevOpsMilestone ? "MARCO-PROJECT" : null
            };

            if (predecessor != null)
                task.PredecessorIds.Add(predecessor.Id);

            if (insertIndex < 0 || insertIndex > siblings.Count)
                siblings.Add(task);
            else
                siblings.Insert(insertIndex, task);

            if (parent != null)
            {
                parent.IsSummary = true;
                parent.RecalcSummary();
            }

            Project.IsDirty = true;
            PrepareTaskInsertionScroll?.Invoke();
            RebuildFlatTasks();
            SelectedTask = FlatTasks.FirstOrDefault(t => t.Id == task.Id);
            StatusMessage = asChild
                ? "Marco adicionado como filho da atividade selecionada."
                : "Marco adicionado abaixo da atividade selecionada.";
            RequestScrollToSelected?.Invoke();
        }

        private static DateTime ResolveMilestoneStart(ProjectTask? parent, ProjectTask? predecessor, ProjectTask? selected)
        {
            if (predecessor != null)
            {
                var finish = ProjectCalendarService.GetInclusiveFinishDate(predecessor.Start, predecessor.Finish);
                return ProjectCalendarService.AddWorkingDays(finish, 1);
            }

            if (parent != null)
                return parent.Start;

            return selected?.Start ?? DateTime.Today;
        }

        /// <summary>Adiciona subtarefa ao pai especificado com ação direta ("Task" ou "NoDevOps").</summary>
        public void AddSubtask(TaskViewModel parentVm, string action)
        {
            var parent    = parentVm.Model;
            var childType = action == "Task" ? "Task" : "No DevOps";
            var isDevOps  = !IsNoDevOpsType(childType);
            var task = new ProjectTask
            {
                Id       = _nextId++,
                Name     = isDevOps ? "Nova Tarefa" : "Nova Subtarefa",
                Start    = parent.Start,
                Finish   = ProjectCalendarService.AddWorkingHours(parent.Start, ProjectCalendarService.WorkingHoursPerDay * 3.0),
                Level    = parent.Level + 1,
                Parent   = parent,
                TfsType  = childType,
                TfsIterationPath = parent.TfsIterationPath,
                TfsId    = isDevOps ? 0 : _nextNoDevOpsId--,
                TfsState = isDevOps ? "New" : null
            };
            parent.Children.Add(task);
            parent.IsSummary = true;
            parent.RecalcSummary();
            Project.IsDirty = true;
            PrepareTaskInsertionScroll?.Invoke();
            RebuildFlatTasks();
            SelectedTask = FlatTasks.FirstOrDefault(t => t.Id == task.Id);
            RequestScrollToSelected?.Invoke();
        }

        [RelayCommand]
        public void DeleteTaskViewModel(TaskViewModel vm)
        {
            var task = vm.Model;
            var removedTasks = FlattenTask(task).ToList();
            if (task.Parent != null)
            {
                task.Parent.Children.Remove(task);
                if (task.Parent.Children.Count == 0)
                    task.Parent.IsSummary = false;
            }
            else
            {
                Project.Tasks.Remove(task);
            }
            foreach (var removedTask in removedTasks)
                _collapsedTaskIds.Remove(removedTask.Id);
            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        [RelayCommand]
        private void DeleteTask()
        {
            if (SelectedTask == null)
            {
                StatusMessage = AppStrings.Get("Status_SelectTaskToDelete");
                return;
            }

            // Sempre delega à View para mostrar a confirmação (No DevOps = local, DevOps = no DevOps)
            RequestDevOpsDeleteDialog?.Invoke(SelectedTask);
        }

        [RelayCommand]
        private void IndentTask()
        {
            if (SelectedTask == null)
            {
                StatusMessage = AppStrings.Get("Status_SelectTaskForHierarchy");
                return;
            }

            var task = SelectedTask.Model;
            var allFlat = AllTasks().ToList();
            var idx = allFlat.IndexOf(task);
            if (idx <= 0)
            {
                StatusMessage = AppStrings.Get("Status_FirstTaskCannotIndent");
                return;
            }

            var targetParentLevel = task.Level;
            var newParent = FindPreviousTaskAtLevel(allFlat, idx - 1, targetParentLevel);
            if (newParent == null)
            {
                StatusMessage = AppStrings.Get("Status_NoParentForIndent");
                return;
            }

            var oldParent = task.Parent;
            RemoveTaskFromCurrentCollection(task);

            task.Parent = newParent;
            newParent.Children.Add(task);
            newParent.IsSummary = true;
            UpdateTaskLevelRecursive(task, newParent.Level + 1);
            RecalcSummaryChain(oldParent);
            RecalcSummaryChain(newParent);
            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = AppStrings.Get("Status_TaskIndented");
        }

        [RelayCommand]
        private void OutdentTask()
        {
            if (SelectedTask == null)
            {
                StatusMessage = AppStrings.Get("Status_SelectTaskForHierarchy");
                return;
            }

            var task = SelectedTask.Model;
            if (task.Parent == null)
            {
                StatusMessage = AppStrings.Get("Status_TaskAlreadyRoot");
                return;
            }

            var oldParent = task.Parent;
            oldParent.Children.Remove(task);
            if (oldParent.Children.Count == 0)
                oldParent.IsSummary = false;

            var grandParent = oldParent.Parent;
            task.Parent = grandParent;
            UpdateTaskLevelRecursive(task, grandParent != null ? grandParent.Level + 1 : 0);

            var targetCollection = grandParent?.Children ?? Project.Tasks;
            var insertAfterIndex = targetCollection.IndexOf(oldParent);
            if (insertAfterIndex < 0)
                insertAfterIndex = targetCollection.Count - 1;
            targetCollection.Insert(insertAfterIndex + 1, task);

            RecalcSummaryChain(oldParent);
            RecalcSummaryChain(grandParent);
            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = AppStrings.Get("Status_TaskPromoted");
        }

        [RelayCommand]
        private void MoveTaskUp(TaskViewModel? taskVm)
        {
            var task = (taskVm ?? SelectedTask)?.Model;
            if (task == null)
                return;

            if (MoveTaskByOffset(task, -1))
            {
                if (task.IsMilestone) task.PredecessorIds.Clear();
                StatusMessage = AppStrings.Get("Status_TaskMovedUp");
            }
        }

        [RelayCommand]
        private void MoveTaskDown(TaskViewModel? taskVm)
        {
            var task = (taskVm ?? SelectedTask)?.Model;
            if (task == null)
                return;

            if (MoveTaskByOffset(task, 1))
            {
                if (task.IsMilestone) task.PredecessorIds.Clear();
                StatusMessage = AppStrings.Get("Status_TaskMovedDown");
            }
        }

        public bool MoveTaskByDrop(TaskViewModel sourceVm, TaskViewModel targetVm, bool insertAfter)
        {
            if (sourceVm.Model == targetVm.Model)
                return false;

            var sourceCollection = GetTaskCollection(sourceVm.Model);
            if (IsTaskInSubtree(sourceVm.Model, targetVm.Model))
            {
                StatusMessage = AppStrings.Get("Status_CannotMoveInsideOwn");
                return false;
            }

            var targetTask = FindTaskInCollectionHierarchy(targetVm.Model, sourceCollection);
            if (targetTask == null)
            {
                StatusMessage = AppStrings.Get("Status_DragSameLevel");
                return false;
            }

            var currentIndex = sourceCollection.IndexOf(sourceVm.Model);
            var targetIndex = sourceCollection.IndexOf(targetTask);
            if (currentIndex < 0 || targetIndex < 0)
                return false;

            if (insertAfter)
                targetIndex++;

            if (targetIndex > currentIndex)
                targetIndex--;

            if (targetIndex == currentIndex)
                return false;

            MoveTask(sourceCollection, currentIndex, targetIndex);
            RescheduleAfterDrop(sourceVm);
            if (sourceVm.Model.IsMilestone) sourceVm.Model.PredecessorIds.Clear();
            StatusMessage = AppStrings.Get("Status_TaskReordered");
            return true;
        }

        [RelayCommand]
        private void LinkTasksSequentially()
        {
            if (SelectedTask == null)
            {
                StatusMessage = AppStrings.Get("Status_SelectTaskToChainFrom");
                return;
            }

            var selectedModel = SelectedTask.Model;
            var selectedParent = selectedModel.Parent;

            // Coleta apenas irmãos não-summary do mesmo pai, na ordem, a partir da selecionada inclusive
            var siblings = (selectedParent == null
                    ? Project.Tasks.AsEnumerable()
                    : selectedParent.Children.AsEnumerable())
                .Where(t => !t.IsSummary && t.TfsId != 0)
                .ToList();

            var startIdx = siblings.IndexOf(selectedModel);
            if (startIdx < 0)
            {
                StatusMessage = AppStrings.Get("Status_TaskNotFoundInHierarchy");
                return;
            }

            var toChain = siblings.Skip(startIdx).ToList();
            if (toChain.Count < 2)
            {
                StatusMessage = AppStrings.Get("Status_NoNextTaskToChain");
                return;
            }

            ProjectTask? previousTask = null;
            foreach (var task in toChain)
            {
                if (previousTask != null)
                {
                    task.PredecessorIds.Clear();
                    task.PredecessorIds.Add(previousTask.Id);
                    var start = ProjectCalendarService.AddWorkingDays(
                        ProjectCalendarService.GetInclusiveFinishDate(previousTask.Start, previousTask.Finish),
                        1);
                    task.Start = start;
                    task.Finish = TaskScheduleService.CalculateFinishFromAssignments(task, start);
                    task.StartFixed = false;
                }

                previousTask = task;
            }

            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = $"Encadeadas {toChain.Count} atividades na mesma hierarquia.";
        }

        [RelayCommand]
        private void ExpandSelected()
        {
            if (SelectedTask == null) return;
            var targetDepth = SelectedTask.Depth;

            // Expande todas as summaries no mesmo nível da selecionada (um nível abaixo)
            foreach (var vm in FlatTasks)
            {
                if (vm.Depth == targetDepth && vm.IsSummary)
                    _collapsedTaskIds.Remove(vm.Model.Id);
            }
            RebuildFlatTasks();
        }

        [RelayCommand]
        private void CollapseAll()
        {
            CollapseRecursive(Project.Tasks);
            RebuildFlatTasks();
        }

        private void CollapseRecursive(IEnumerable<ProjectTask> tasks)
        {
            foreach (var task in tasks)
            {
                if (task.IsSummary)
                {
                    _collapsedTaskIds.Add(task.Id);
                    CollapseRecursive(task.Children);
                }
            }
        }

        [RelayCommand]
        private void ExpandAll()
        {
            _collapsedTaskIds.Clear();
            RebuildFlatTasks();
        }

        [RelayCommand] private void Undo() { StatusMessage = AppStrings.Get("Status_Undo"); }
        [RelayCommand] private void Redo() { StatusMessage = AppStrings.Get("Status_Redo"); }
        [RelayCommand] private void ShowGantt() { SelectedViewIndex = 0; }
#if !COMMUNITY
        [RelayCommand] private void ShowSprints() { SelectedViewIndex = 1; }
        [RelayCommand] private void ShowResourceUsage() { SelectedViewIndex = 2; }
        [RelayCommand] private void ShowPert() { SelectedViewIndex = 3; }
        [RelayCommand] private void ProjectInfo() { StatusMessage = AppStrings.Get("Status_ProjectInfo"); }
        [RelayCommand] private void EditCalendar() { StatusMessage = AppStrings.Get("Status_EditCalendar"); }
        [RelayCommand] private void SprintSettings() { StatusMessage = AppStrings.Get("Status_SprintSettings"); }
        [RelayCommand] private void ManageResources() { StatusMessage = AppStrings.Get("Status_ManageResources"); }
        [RelayCommand] private void ReportTasks() { StatusMessage = AppStrings.Get("Status_ReportTasks"); }
        [RelayCommand] private void ReportResources() { StatusMessage = AppStrings.Get("Status_ReportResources"); }
        [RelayCommand] private void AIGenerateTasks() { StatusMessage = AppStrings.Get("Status_AIGenerateTasks"); }
        [RelayCommand] private void AISuggestAllocation() { StatusMessage = AppStrings.Get("Status_AISuggestAllocation"); }
        [RelayCommand] private void AISettings() { StatusMessage = AppStrings.Get("Status_AISettings"); }
#endif

        private void RebuildSprintGroups()
        {
            SprintGroups.Clear();
            var bySprint = new Dictionary<int, List<TaskViewModel>>();
            foreach (var vm in FlatTasks)
            {
                var sprintIndex = GetSprintIndex(vm.Start, Project.StartDate);
                if (sprintIndex < 0)
                    continue;

                if (!bySprint.ContainsKey(sprintIndex))
                    bySprint[sprintIndex] = new();
                bySprint[sprintIndex].Add(vm);
            }
            foreach (var kv in bySprint.OrderBy(x => x.Key))
            {
                var sprintIndex = kv.Key;
                var sprintStart = Project.StartDate.AddDays(sprintIndex * Math.Max(1, SprintDurationDays));
                var sprintEnd = sprintStart.AddDays(Math.Max(1, SprintDurationDays) - 1);
                var sprintNumber = GetSprintNumberFromIndex(sprintIndex);
                SprintGroups.Add(new SprintGroup
                {
                    Header = $"Sprint {sprintNumber}  ({sprintStart:dd/MM/yy} – {sprintEnd:dd/MM/yy})",
                    Tasks = new ObservableCollection<TaskViewModel>(kv.Value)
                });
            }
        }

        private void RebuildResourceGroups()
        {
            ResourceAllocationGroups.Clear();
            foreach (var resource in Project.Resources)
            {
                var group = new ResourceAllocationGroup
                {
                    ResourceName = resource.Name,
                    CapacityText = $"Capacidade: {resource.MaxUnitsPerDay}h/dia"
                };

                foreach (var vm in FlatTasks.Where(t => t.Model.Children.Count == 0))
                {
                    var assignment = vm.Model.Resources.FirstOrDefault(r => r.ResourceId == resource.Id);
                    if (assignment == null) continue;

                    group.Tasks.Add(new ResourceTaskRow
                    {
                        SprintNumber = vm.SprintNumber,
                        TaskName = vm.Name,
                        AllocationPercent = assignment.AllocationPercent,
                        EstimatedHours = TaskScheduleService.GetAssignmentHours(vm.Model, assignment)
                    });
                }

                // Verifica sobrealocação (total de horas estimadas > capacidade total)
                var totalHours = group.Tasks.Sum(t => t.EstimatedHours);
                var sprintCount = group.Tasks.Select(t => t.SprintNumber).Distinct().Count();
                var capacityHours = sprintCount * Math.Max(1, SprintDurationDays) * resource.MaxUnitsPerDay;
                group.IsOverAllocated = totalHours > capacityHours;

                ResourceAllocationGroups.Add(group);
            }
        }

        // Helpers
        private IEnumerable<ProjectTask> AllTasks()
        {
            foreach (var t in Project.Tasks)
                foreach (var ft in FlattenTask(t))
                    yield return ft;
        }

        private IEnumerable<ProjectTask> FlattenTask(ProjectTask task)
        {
            yield return task;
            foreach (var child in task.Children)
                foreach (var ft in FlattenTask(child))
                    yield return ft;
        }

        partial void OnProjectChanged(Project value)
        {
            RebuildSprintCollections();
            if (!string.IsNullOrEmpty(value?.LastZoom))
                SelectedZoom = value.LastZoom;
        }

        partial void OnSelectedZoomChanged(string value)
        {
            if (Project != null)
                Project.LastZoom = value;
        }

        /// <summary>
        /// Sincroniza Sprints/SprintOptions com Project.Sprints e recalcula números
        /// das tarefas. Chame após adicionar ou remover sprints em Project.Sprints.
        /// </summary>
        public void RebuildSprintCollections()
        {
            Sprints.Clear();
            SprintOptions.Clear();
            SprintOptions.Add(NoSprintOption);
            if (Project?.Sprints != null)
                foreach (var s in Project.Sprints)
                {
                    Sprints.Add(s);
                    SprintOptions.Add(s);
                }
            foreach (var vm in FlatTasks)
                vm.RefreshSprintOptions(SprintOptions);
            RecalcSprints();
        }

        private void ApplyProjectSprintSettingsToViewModel(Project project)
        {
            _isApplyingProjectSprintSettings = true;
            try
            {
                SprintDurationDays = project.SprintDurationDays;
                FirstSprintNumber = project.FirstSprintNumber;
                SprintNumberingMode = project.SprintNumberingMode;
                LowDaysPerSfp = project.LowDaysPerSfp;
                MediumDaysPerSfp = project.MediumDaysPerSfp;
                HighDaysPerSfp = project.HighDaysPerSfp;
                CriticalPathCriticalSlackDays = project.CriticalPathCriticalSlackDays;
                CriticalPathRiskSlackDays = project.CriticalPathRiskSlackDays;
                ShowOriginalHoursColumn = project.ShowOriginalHoursColumn;
                HiddenColumns = project.HiddenColumns ?? "";
                HiddenColumnsExpanded = project.HiddenColumnsExpanded ?? "";
            }
            finally
            {
                _isApplyingProjectSprintSettings = false;
            }
        }

        private bool MoveTaskByOffset(ProjectTask task, int offset)
        {
            var collection = GetTaskCollection(task);
            var currentIndex = collection.IndexOf(task);
            if (currentIndex < 0)
                return false;

            var targetIndex = currentIndex + offset;
            if (targetIndex < 0 || targetIndex >= collection.Count)
                return false;

            MoveTask(collection, currentIndex, targetIndex);
            return true;
        }

        private void MoveTask(ObservableCollection<ProjectTask> collection, int currentIndex, int targetIndex)
        {
            if (currentIndex == targetIndex)
                return;

            var task = collection[currentIndex];
            collection.RemoveAt(currentIndex);
            collection.Insert(targetIndex, task);

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        private ObservableCollection<ProjectTask> GetTaskCollection(ProjectTask task)
        {
            return task.Parent?.Children ?? Project.Tasks;
        }

        private static ProjectTask? FindTaskInCollectionHierarchy(
            ProjectTask task,
            ObservableCollection<ProjectTask> targetCollection)
        {
            var current = task;
            while (current != null)
            {
                if (ReferenceEquals(current.Parent?.Children ?? targetCollection, targetCollection) &&
                    targetCollection.Contains(current))
                {
                    return current;
                }

                current = current.Parent;
            }

            return null;
        }

        private static bool IsTaskInSubtree(ProjectTask root, ProjectTask candidate)
        {
            var current = candidate.Parent;
            while (current != null)
            {
                if (current == root)
                    return true;

                current = current.Parent;
            }

            return false;
        }

        private static ProjectTask? FindPreviousTaskAtLevel(IReadOnlyList<ProjectTask> tasks, int startIndex, int targetLevel)
        {
            for (var index = startIndex; index >= 0; index--)
            {
                var candidate = tasks[index];
                if (candidate.Level == targetLevel)
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Para cada Story/Feature/Epic, ordena as Tasks filhas por Prioridade (menor = maior prioridade)
        /// e calcula datas sequenciais: Task[n+1] começa quando Task[n] termina.
        /// A data de início da primeira Task herda a data de início da Story.
        /// </summary>
        private static void RecalcTaskDatesFromPriority(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                // Separa Tasks dos outros filhos (mantendo posições relativas dos não-Task)
                var taskChildren = t.Children
                    .Where(c => string.Equals(c.TfsType?.Trim(), "Task", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c.Priority ?? 5)
                    .ThenBy(c => c.Id)
                    .ToList();

                if (taskChildren.Count > 0)
                {
                    // Reordena fisicamente as Tasks dentro de Children por prioridade
                    var taskIndices = new List<int>();
                    for (int i = 0; i < t.Children.Count; i++)
                    {
                        if (string.Equals(t.Children[i].TfsType?.Trim(), "Task", StringComparison.OrdinalIgnoreCase))
                            taskIndices.Add(i);
                    }
                    for (int k = 0; k < taskIndices.Count; k++)
                    {
                        var currentItem = t.Children[taskIndices[k]];
                        var desiredItem = taskChildren[k];
                        if (!ReferenceEquals(currentItem, desiredItem))
                            t.Children.Move(t.Children.IndexOf(desiredItem), taskIndices[k]);
                    }

                    // Duração efetiva de cada task para cálculo de Gantt:
                    //   HH Original > 0 → HH Original + HH Atual
                    //   HH Original = 0, HH Atual > 0 → HH Atual
                    //   ambos = 0 → recebe rateio
                    static double EffDur(ProjectTask tc)
                    {
                        var orig = tc.OriginalEstimatedHours ?? 0;
                        var cur  = tc.CurrentHours ?? 0;
                        return orig > 0 ? orig + cur : cur;
                    }

                    var storyHours    = Services.ProjectCalendarService.CountWorkingHours(t.Start, t.Finish);
                    var storyOriginal = t.OriginalEstimatedHours ?? 0;
                    var storyCurrent  = t.CurrentHours ?? 0;

                    var tasksWithHours    = taskChildren.Where(tc => EffDur(tc) > 0).ToList();
                    var tasksWithoutHours = taskChildren.Where(tc => EffDur(tc) <= 0).ToList();
                    if (tasksWithoutHours.Count > 0)
                    {
                        int n = tasksWithoutHours.Count;

                        // Rateio de HH Original da story → OriginalEstimatedHours das tasks
                        double usedOrig    = tasksWithHours.Sum(tc => tc.OriginalEstimatedHours ?? 0);
                        double remainOrig  = storyOriginal > 0 ? Math.Max(0, storyOriginal - usedOrig) : 0;
                        double perOriginal = remainOrig > 0 ? remainOrig / n
                                          : storyOriginal > 0 ? storyOriginal / Math.Max(1, taskChildren.Count)
                                          : storyHours > 0 ? storyHours / Math.Max(1, taskChildren.Count)
                                          : Services.ProjectCalendarService.WorkingHoursPerDay;

                        // Rateio de HH Atual da story → CurrentHours das tasks (somente se story tem HH Atual)
                        double usedCur   = tasksWithHours.Sum(tc => tc.CurrentHours ?? 0);
                        double remainCur = storyCurrent > 0 ? Math.Max(0, storyCurrent - usedCur) : 0;
                        double perCurrent = remainCur > 0 ? remainCur / n
                                         : storyCurrent > 0 ? storyCurrent / Math.Max(1, taskChildren.Count)
                                         : 0;

                        foreach (var tc in tasksWithoutHours)
                        {
                            tc.OriginalEstimatedHours = perOriginal;
                            if (perCurrent > 0)
                                tc.CurrentHours = perCurrent;
                        }
                    }

                    // Se a soma das Tasks superar a duração da story, expande a story
                    double totalTaskHours = taskChildren.Sum(EffDur);
                    if (totalTaskHours > storyHours + 0.01)
                    {
                        var storyDuration = Services.TaskScheduleService
                            .ConvertWorkHoursToCalendarDurationHours(t, totalTaskHours);
                        t.Finish = Services.ProjectCalendarService.AddWorkingHours(t.Start, storyDuration);
                    }

                    // Calcula datas sequenciais por ordem de prioridade
                    var cursor = t.Start;
                    foreach (var tc in taskChildren)
                    {
                        if (tc.PercentComplete >= 100 || tc.StartFixed || tc.FinishFixed)
                        {
                            if (tc.Finish > cursor)
                                cursor = tc.Finish;
                            continue;
                        }

                        var taskHours = EffDur(tc);
                        var taskDuration = Services.TaskScheduleService
                            .ConvertWorkHoursToCalendarDurationHours(tc, taskHours);
                        tc.Start = cursor;
                        tc.Finish = Services.ProjectCalendarService.AddWorkingHours(cursor, taskDuration);
                        cursor = tc.Finish;
                    }
                }

                // Recursivo para sub-hierarquias
                RecalcTaskDatesFromPriority(t.Children);
            }
        }

        private static bool IsNoDevOpsType(string? type)
        {
            var normalized = (type ?? string.Empty)
                .Trim()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);
            return string.Equals(normalized, "NoDevOps", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDevOpsActivityType(string? type) =>
            !string.IsNullOrWhiteSpace(type) && !IsNoDevOpsType(type);

        private static bool IsStoryLikeType(string? type) =>
            string.Equals(type?.Trim(), "User Story",  StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type?.Trim(), "Story",       StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type?.Trim(), "STO",         StringComparison.OrdinalIgnoreCase);

        private void RecalcIdCounters()
        {
            _nextId = AllTasks().Select(t => t.Id).DefaultIfEmpty(0).Max() + 1;
            var minNoDevOps = AllTasks()
                .Where(t => t.TfsId.HasValue && t.TfsId.Value < 0)
                .Select(t => t.TfsId!.Value)
                .DefaultIfEmpty(0)
                .Min();
            _nextNoDevOpsId = Math.Min(minNoDevOps - 1, -1);
        }

        private void RemoveTaskFromCurrentCollection(ProjectTask task)
        {
            if (task.Parent != null)
            {
                task.Parent.Children.Remove(task);
                if (task.Parent.Children.Count == 0)
                    task.Parent.IsSummary = false;
                return;
            }

            Project.Tasks.Remove(task);
        }

        private static void UpdateTaskLevelRecursive(ProjectTask task, int level)
        {
            task.Level = level;
            foreach (var child in task.Children)
            {
                child.Parent = task;
                UpdateTaskLevelRecursive(child, level + 1);
            }
        }

        private static void RecalcSummaryChain(ProjectTask? task)
        {
            var current = task;
            while (current != null)
            {
                current.RecalcSummary();
                current = current.Parent;
            }
        }

        private int GetSprintIndex(DateTime taskStart, DateTime projectStart)
        {
            var sprintDays = Math.Max(1, SprintDurationDays);
            var dayOffset = (taskStart - projectStart).TotalDays;
            return dayOffset < 0 ? -1 : (int)(dayOffset / sprintDays);
        }

        private int GetSprintNumberFromIndex(int sprintIndex)
        {
            var firstSprint = Math.Max(1, FirstSprintNumber);
            return SprintNumberingMode switch
            {
                "Par" => NormalizeParity(firstSprint, even: true) + (sprintIndex * 2),
                "Impar" => NormalizeParity(firstSprint, even: false) + (sprintIndex * 2),
                _ => firstSprint + sprintIndex
            };
        }

        private static int NormalizeParity(int value, bool even)
        {
            var normalized = Math.Max(1, value);
            var isEven = normalized % 2 == 0;
            if (even == isEven)
                return normalized;

            return normalized + 1;
        }
    }
}
