using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.ViewModels
{
    public partial class TaskViewModel : ObservableObject
    {
        private readonly ProjectTask _task;
        private readonly double _lowDaysPerSfp;
        private readonly double _mediumDaysPerSfp;
        private readonly double _highDaysPerSfp;

        // Callback fornecido pelo MainViewModel para recalcular o início quando
        // o usuário limpa o fix (digita "0"). Retorna a data de início calculada.
        public Func<DateTime>? GetSprintStart { get; set; }
        public Func<TaskViewModel, DateTime?>? GetVirtualPredecessorStart { get; set; }

        // Retorna a data fim da sprint atribuída à tarefa (null se não houver sprint ou sprint não encontrada).
        public Func<string?, DateTime?>? GetSprintFinish { get; set; }

        // Retorna a sprint atribuída à tarefa (para avaliar período x data de referência).
        public Func<string?, Sprint?>? GetSprintByPath { get; set; }

        // Confirmação (UI) ao concluir (100%) uma atividade cuja sprint está fora
        // da faixa de período. Recebe (tarefa, sprint). Retorna true para prosseguir.
        public Func<TaskViewModel, Sprint, bool>? ConfirmCompleteOutsideSprint { get; set; }

        // Data de referência da atividade ("onde ela está"): 0% → início;
        // em andamento → posição do % concluído; 100% → fim inclusivo.
        public DateTime? SprintReferenceDate
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_task.TfsIterationPath)) return null;
                return ProjectCalendarService.GetProgressReferenceDate(
                    _task.Start, _task.Finish, _task.PercentComplete);
            }
        }

        // Texto da data de referência para o hint da célula de Sprint no cronograma.
        public string SprintReferenceDateText => SprintReferenceDate is { } r ? r.ToString("dd/MM/yy") : "";
        public bool HasSprintReference => SprintReferenceDate != null;

        // Estado da sprint em relação ao período (data de referência):
        // 0 = ok; 1 = fora do período (ajustável); 2 = concluída antecipada;
        // 3 = sprint em andamento (contém hoje) com % fora do período.
        private int SprintPeriodState
        {
            get
            {
                if (!SupportsSprint || string.IsNullOrWhiteSpace(_task.TfsIterationPath)) return 0;
                var sprint = GetSprintByPath?.Invoke(_task.TfsIterationPath);
                if (sprint == null) return 0;
                // Sprint sem faixa de período válida (ex.: gerada pela IA sem datas) não destaca.
                if (sprint.Start == default || sprint.End == default || sprint.End.Date <= sprint.Start.Date)
                    return 0;

                var r = ProjectCalendarService.GetProgressReferenceDate(
                    _task.Start, _task.Finish, _task.PercentComplete);
                if (sprint.Start.Date <= r && r <= sprint.End.Date)
                    return 0; // referência dentro da sprint → ok

                var today = DateTime.Today;
                // 100% e sprint com período anterior a hoje → entrega antecipada.
                if (_task.PercentComplete >= 100 && sprint.End.Date < today)
                    return 2;
                // Sprint em andamento (contém hoje) → não ajusta.
                if (sprint.Start.Date <= today && today <= sprint.End.Date)
                    return 3;
                return 1;
            }
        }

        public bool IsSprintOutOfPeriod   => SprintPeriodState == 1;
        public bool IsSprintCompletedEarly => SprintPeriodState == 2;
        public bool IsSprintActiveMismatch => SprintPeriodState == 3;

        // True quando a data fim da tarefa ultrapassa a data fim da sprint atribuída.
        // Comparação por string dd/MM/yyyy para evitar problemas de fuso/hora.
        public bool IsFinishBeyondSprint
        {
            get
            {
                if (string.IsNullOrEmpty(_task.TfsIterationPath)) return false;
                var sprintFinish = GetSprintFinish?.Invoke(_task.TfsIterationPath);
                if (sprintFinish == null) return false;
                var fmt = System.Globalization.CultureInfo.InvariantCulture;
                // Usa a data inclusiva (igual ao FinishDisplay) para comparar com o fim da sprint.
                var inclusiveFinish = ProjectCalendarService.GetInclusiveFinishDate(_task.Start, _task.Finish);
                var taskDay   = inclusiveFinish.ToString("yyyyMMdd", fmt);
                var sprintDay = sprintFinish.Value.ToString("yyyyMMdd", fmt);
                var result = string.Compare(taskDay, sprintDay, StringComparison.Ordinal) > 0;
                SprintAlertLog.Write(_task.Name, taskDay, sprintDay, result, _task.TfsIterationPath);
                return result;
            }
        }

        // Lookups injetados pelo MainViewModel para resolução de IDs de predecessoras.
        // FindByInternalId : Id interno → TaskViewModel (para exibir o DisplayId correto).
        // FindByDisplayId  : DisplayId digitado pelo usuário → Id interno da tarefa.
        public Func<int, TaskViewModel?>? FindByInternalId { get; set; }
        public Func<string, int?>? FindByDisplayId { get; set; }

        // Callback acionado quando o Finish desta tarefa muda, para cascatear o
        // reagendamento das tarefas que a têm como predecessora.
        public Action<TaskViewModel>? ScheduleSuccessors { get; set; }

        // Todas as sprints disponíveis para este ViewModel, sem filtro de data.
        public ObservableCollection<Sprint> AvailableSprintsForTask { get; } = new();

        public void RefreshSprintOptions(IEnumerable<Sprint> allOptions)
        {
            AvailableSprintsForTask.Clear();
            foreach (var s in allOptions)
                AvailableSprintsForTask.Add(s);
        }

        public TaskViewModel(
            ProjectTask task,
            int depth = 0,
            double lowDaysPerSfp = 1.0,
            double mediumDaysPerSfp = 1.0,
            double highDaysPerSfp = 1.0)
        {
            _task = task;
            Depth = depth;
            _lowDaysPerSfp = Math.Max(0, lowDaysPerSfp);
            _mediumDaysPerSfp = Math.Max(0, mediumDaysPerSfp);
            _highDaysPerSfp = Math.Max(0, highDaysPerSfp);

            if (UsesSfpEstimate)
                ApplySfpDuration();
        }

        public ProjectTask Model => _task;

        public int Depth { get; }

        // Indentacao visual na grade
        public double Indent => Depth * 16.0;

        [ObservableProperty] private bool _isExpanded = true;
        [ObservableProperty] private bool _isSelected;
        [ObservableProperty] private bool _isHighlightedPredecessor;
        [ObservableProperty] private bool _isHighlightSource;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RowBackground))]
        private Brush? _hierarchyBackground;

        // Cinza sutil para a linha de Task (quando não selecionada), diferenciando de
        // Story/Feature/Epic. Cede lugar às cores de hierarquia customizadas se houver.
        private static readonly Brush TaskRowBrush =
            new SolidColorBrush(Color.FromRgb(0xEC, 0xEE, 0xF1)); // #ECEEF1

        static TaskViewModel() { if (TaskRowBrush is SolidColorBrush b) b.Freeze(); }

        // Fundo da linha na grade: cor de hierarquia (se ativa) ou o cinza de Task.
        public Brush? RowBackground => HierarchyBackground ?? (IsDevOpsTask ? TaskRowBrush : null);

        public int Id
        {
            get => _task.Id;
            set { _task.Id = value; OnPropertyChanged(); }
        }

        // ID exibido nas telas: o do DevOps quando vinculado, senão o interno.
        public string DisplayId => _task.HasTfsLink
            ? $"{_task.TfsId}:T"
            : $"{_task.Id}:I";

        public Brush DisplayIdBrush => _task.HasTfsLink
            ? new SolidColorBrush(Color.FromRgb(0x00, 0x4B, 0xA0))
            : new SolidColorBrush(Color.FromRgb(0x8A, 0x4B, 0x00));

        public bool HasDevOpsLink => _task.TfsId.HasValue && _task.TfsId.Value > 0;

        public bool TasksSuppressed
        {
            get => _task.TasksSuppressed;
            set { if (_task.TasksSuppressed == value) return; _task.TasksSuppressed = value; OnPropertyChanged(); }
        }

        public bool IsDevOpsTask =>
            string.Equals(_task.TfsType?.Trim(), "Task", StringComparison.OrdinalIgnoreCase);

        public int? Priority
        {
            get => _task.Priority;
            set
            {
                if (_task.Priority == value) return;
                _task.Priority = value;
                OnPropertyChanged();
            }
        }

        public string PriorityDisplay
        {
            get => IsDevOpsTask ? (_task.Priority?.ToString() ?? "5") : "";
            set
            {
                if (!IsDevOpsTask) return;
                if (int.TryParse(value, out var v) && v >= 1 && v <= 10)
                    Priority = v;
            }
        }

        public int? TfsId
        {
            get => _task.TfsId;
            set
            {
                _task.TfsId = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayId));
                OnPropertyChanged(nameof(DisplayIdBrush));
                OnPropertyChanged(nameof(HasDevOpsLink));
            }
        }

        public string? TfsType
        {
            get => _task.TfsType;
            set
            {
                _task.TfsType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayId));
                OnPropertyChanged(nameof(HasDevOpsLink));
                OnPropertyChanged(nameof(DisplayIdBrush));
                OnPropertyChanged(nameof(IsNoDevOpsActivity));
                OnPropertyChanged(nameof(IsDevOpsMilestoneActivity));
                OnPropertyChanged(nameof(CanUseZeroDuration));
                OnPropertyChanged(nameof(DevOpsTag));
                OnPropertyChanged(nameof(DevOpsTooltip));
                OnPropertyChanged(nameof(SupportsSprint));
                OnPropertyChanged(nameof(SprintDisplay));
            }
        }

        public string? TfsClassification
        {
            get => _task.TfsClassification;
            set
            {
                _task.TfsClassification = value;
                OnPropertyChanged();
            }
        }

        public string? TfsState
        {
            get => _task.TfsState;
            set
            {
                _task.TfsState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentComplete));
                OnPropertyChanged(nameof(DevOpsTag));
                OnPropertyChanged(nameof(DevOpsTooltip));
                OnPropertyChanged(nameof(DevOpsBrush));
            }
        }

        // ── Selo compacto DevOps (cronograma) ────────────────────────────────
        // Ex.: "FA" (Feature, Active). Vazio quando não vinculado.
        public string DevOpsTag
        {
            get
            {
                var t = TypeShort(_task.TfsType);
                var s = StateShort(_task.TfsState);
                if (string.IsNullOrEmpty(t) && string.IsNullOrEmpty(s)) return string.Empty;
                if (string.IsNullOrEmpty(s)) return t;
                if (string.IsNullOrEmpty(t)) return s;
                return $"{t}{s}";
            }
        }

        public string DevOpsTooltip
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(_task.TfsType)) parts.Add(_task.TfsType!);
                if (!string.IsNullOrWhiteSpace(_task.TfsState)) parts.Add(_task.TfsState!);
                return string.Join(" · ", parts);
            }
        }

        // Cor pelo estado DevOps.
        public Brush DevOpsBrush => (_task.TfsState?.Trim().ToLowerInvariant()) switch
        {
            "active" => new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)),   // azul
            "resolved" => new SolidColorBrush(Color.FromRgb(0x87, 0x64, 0xB8)), // roxo
            "closed" => new SolidColorBrush(Color.FromRgb(0x21, 0x73, 0x46)),   // verde
            "removed" => new SolidColorBrush(Color.FromRgb(0xA4, 0x26, 0x2C)),  // vermelho
            _ => new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70))           // cinza (New/none)
        };

        public string TksCountDisplay => _task.DevopsTaskCount.HasValue
            ? _task.DevopsTaskCount.Value.ToString()
            : string.Empty;

        public Brush TksBrush => (_task.DevopsTaskCount is 0)
            ? new SolidColorBrush(Color.FromRgb(0xC0, 0x20, 0x20))
            : new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));

        // Visível apenas em Stories (não-summary) vinculadas ao DevOps com contagem calculada
        public System.Windows.Visibility TksButtonVisibility =>
            (_task.DevopsTaskCount.HasValue && !_task.IsSummary && _task.TfsId is > 0)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

        public void NotifyTksChanged()
        {
            OnPropertyChanged(nameof(TksCountDisplay));
            OnPropertyChanged(nameof(TksBrush));
            OnPropertyChanged(nameof(TksButtonVisibility));
        }

        private static string TypeShort(string? type) => (type?.Trim().ToLowerInvariant()) switch
        {
            "epic" => "E",
            "feature" => "F",
            "user story" or "story" => "S",
            "task" => "T",
            "marco-devops" or "marco-devop" or "marcodevops" or "marcodevop" => "M",
            "no devops" or "nodevops" or "no devop" or "nodevop" => "N",
            null or "" => string.Empty,
            _ => type!.Trim().ToUpperInvariant()[..1]
        };

        private static string StateShort(string? state) => (state?.Trim().ToLowerInvariant()) switch
        {
            "new" => "N",
            "active" => "A",
            "resolved" => "R",
            "closed" => "C",
            "removed" => "X",
            null or "" => string.Empty,
            _ => state!.Trim().ToUpperInvariant()[..1]
        };

        public string? Description
        {
            get => _task.Description;
            set { _task.Description = value; OnPropertyChanged(); }
        }

        public string? Tags
        {
            get => _task.Tags;
            set
            {
                _task.Tags = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBlocked));
            }
        }

        private static bool HasBlockTag(string? tags) =>
            (tags ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(t => string.Equals(t, "Block", StringComparison.OrdinalIgnoreCase));

        // Block próprio da Story (tag na própria atividade)
        public bool IsBlockedByStory => HasBlockTag(_task.Tags);

        // Block herdado de Task filha (rollup)
        public bool IsBlockedByTask => _task.BlockedByChild;

        // Conveniência: item bloqueado — tag "Block" própria OU rollup de Task filha
        public bool IsBlocked => IsBlockedByStory || IsBlockedByTask;

        // Ícone a exibir: Story tem prioridade sobre Task
        public string BlockIcon => IsBlockedByStory ? "⛔ BLOCK" : "🔴 BLOCK";

        public bool ToggleBlockCommand_CanExecute => true;

        public void ToggleTaskBlock()
        {
            // Block na Task = tag "Block" nas próprias tags (igual à Story)
            var tags = (_task.Tags ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (HasBlockTag(_task.Tags))
                tags.RemoveAll(t => string.Equals(t, "Block", StringComparison.OrdinalIgnoreCase));
            else
                tags.Add("Block");
            _task.Tags = string.Join("; ", tags);
            OnPropertyChanged(nameof(Tags));
            OnPropertyChanged(nameof(IsBlocked));
            OnPropertyChanged(nameof(IsBlockedByTask));
            OnPropertyChanged(nameof(BlockIcon));
        }

        public void ToggleStoryBlock()
        {
            var tags = (_task.Tags ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (IsBlockedByStory)
                tags.RemoveAll(t => string.Equals(t, "Block", StringComparison.OrdinalIgnoreCase));
            else
                tags.Add("Block");

            _task.Tags = string.Join("; ", tags);
            OnPropertyChanged(nameof(Tags));
            OnPropertyChanged(nameof(IsBlocked));
            OnPropertyChanged(nameof(IsBlockedByStory));
            OnPropertyChanged(nameof(IsBlockedByTask));
            OnPropertyChanged(nameof(BlockIcon));
        }

        public bool HasSyncConflict => _task.HasSyncConflict;
        public bool HasBrokenPredecessorLink => _task.HasBrokenPredecessorLink;

        // IDs de predecessoras que não resolvem para uma tarefa do cronograma —
        // são TfsIds de work items fora do escopo (armazenados direto no import).
        public System.Collections.Generic.List<int> UnresolvedPredecessorIds =>
            FindByInternalId == null
                ? new System.Collections.Generic.List<int>()
                : _task.PredecessorIds.Where(id => FindByInternalId(id) == null).ToList();
        // Verdadeiro quando algum PredecessorId não resolve para uma tarefa existente no projeto.
        public bool HasUnresolvedPredecessor =>
            FindByInternalId != null &&
            _task.PredecessorIds.Count > 0 &&
            _task.PredecessorIds.Any(id => FindByInternalId(id) == null);

        public DateTime? CalculatedFinish => _task.CalculatedFinish;

        public string Name
        {
            get => _task.Name;
            set { _task.Name = value; OnPropertyChanged(); _task.Parent?.RecalcSummary(); }
        }

        public bool StartFixed
        {
            get => _task.StartFixed;
            set
            {
                _task.StartFixed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StartDisplay));
            }
        }

        public string StartDisplay => _task.Start.ToString("dd/MM/yy");

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void ClearStartFixed() => StartText = "0";

        // Setter de texto aceita data válida (marca fix) ou "0" (limpa fix e recalcula).
        public string StartText
        {
            set
            {
                var raw = value?.Trim() ?? string.Empty;
                if (raw == "0")
                {
                    _task.StartFixed = false;
                    var newStart = GetSprintStart?.Invoke() ?? _task.Start;
                    _task.Start = newStart;
                    if (!_task.FinishFixed)
                        _task.Finish = Services.TaskScheduleService.CalculateFinishFromAssignments(_task, newStart);
                    OnPropertyChanged(nameof(Start));
                    OnPropertyChanged(nameof(Finish));
                    OnPropertyChanged(nameof(StartFixed));
                    OnPropertyChanged(nameof(StartDisplay));
                    OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
                    OnPropertyChanged(nameof(DurationHours));
                    RecalcAncestorSummaries();
                    ScheduleSuccessors?.Invoke(this);
                }
                else if (DateTime.TryParse(raw, out var parsed))
                {
                    Start = parsed; // setter abaixo marca StartFixed = true
                }
            }
        }

        public DateTime Start
        {
            get => _task.Start;
            set
            {
                _task.Start = value;
                if (!_task.FinishFixed)
                    _task.Finish = Services.TaskScheduleService.CalculateFinishFromAssignments(_task, value);
                _task.StartFixed = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Finish));
                OnPropertyChanged(nameof(FinishDisplay));
                OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
                OnPropertyChanged(nameof(DurationHours));
                OnPropertyChanged(nameof(DisplayAsMilestone));
                OnPropertyChanged(nameof(StartFixed));
                OnPropertyChanged(nameof(StartDisplay));
                RecalcAncestorSummaries();
                ScheduleSuccessors?.Invoke(this);
            }
        }

        public DateTime Finish
        {
            get => _task.Finish;
            set
            {
                _task.Finish = value;
                if (CanEditPercentComplete && value.Date < DateTime.Today && _task.PercentComplete < 100)
                {
                    _task.PercentComplete = 100;
                    OnPropertyChanged(nameof(PercentComplete));
                    NotifyParentPercentChanged();
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(FinishDisplay));
                OnPropertyChanged(nameof(FinishFixed));
                OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
                OnPropertyChanged(nameof(DurationHours));
                OnPropertyChanged(nameof(DisplayAsMilestone));
                RecalcAncestorSummaries();
                ScheduleSuccessors?.Invoke(this);
            }
        }

        public DateTime? BaselineStart  => _task.BaselineStart;
        public DateTime? BaselineFinish => _task.BaselineFinish;
        public double?   BaselineHours  => _task.BaselineHours;
        public bool      HasBaseline    => _task.BaselineStart.HasValue;

        public double DurationHours
        {
            // Duração = HH Atual + HH Restante; para summaries agrega filhos sem modificar o modelo.
            get {
                if (_task.IsSummary)
                    return SumTaskHours(_task);
                if (_task.IsMilestone)
                    return 0;
                var cur = _task.CurrentHours ?? 0;
                var est = _task.EstimatedHours ?? 0;
                if (cur > 0 || est > 0) return cur + est;
                // Fallback 1: horas originais estimadas (preservadas antes de zerar pelo %)
                if (_task.OriginalEstimatedHours is > 0) return _task.OriginalEstimatedHours.Value;
                return ProjectCalendarService.CountWorkingHours(_task.Start, _task.Finish);
            }
            set
            {
                if (!CanEditDuration) return;

                if (value >= 0)
                {
                    if (value <= 0.0001)
                    {
                        if (!CanUseZeroDuration)
                            return;

                        _task.IsMilestone = true;
                        _task.CurrentHours = null;
                        _task.EstimatedHours = 0;
                        _task.OriginalEstimatedHours = null;
                        SyncAssignmentEstimatedHours(0);
                        _task.Finish = _task.Start;
                        _task.PercentComplete = 0;

                        OnPropertyChanged();
                        OnPropertyChanged(nameof(IsMilestone));
                        OnPropertyChanged(nameof(CurrentHours));
                        OnPropertyChanged(nameof(CurrentHoursDisplay));
                        OnPropertyChanged(nameof(EstimatedHoursDisplay));
                        OnPropertyChanged(nameof(PercentComplete));
                        OnPropertyChanged(nameof(Finish));
                        OnPropertyChanged(nameof(FinishDisplay));
                        OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
                        OnPropertyChanged(nameof(DisplayAsMilestone));
                        RefreshOriginalEstimatedHoursProperties();
                        RecalcAncestorSummaries();
                        RefreshAncestorCalculatedProperties();
                        ScheduleSuccessors?.Invoke(this);
                        return;
                    }

                    if (IsDevOpsMilestoneActivity)
                        return;

                    if (_task.IsMilestone)
                    {
                        _task.IsMilestone = false;
                        OnPropertyChanged(nameof(IsMilestone));
                    }

                    // Ao editar, o usuário define o total; HH Atual não muda — extrai HH Restante.
                    var remaining = _task.CurrentHours is > 0
                        ? Math.Max(0, value - _task.CurrentHours.Value)
                        : value;
                    _task.EstimatedHours = remaining;
                    SyncAssignmentEstimatedHours(remaining);
                    MoveToVirtualPredecessorStartIfNeeded(allowStartedReposition: true);
                    if (!_task.FinishFixed)
                    {
                        // Cálculo único e centralizado (mesma fórmula do import TFS e da abertura de arquivo).
                        _task.Finish = Services.TaskScheduleService.CalculateFinishFromAssignments(_task, _task.Start);
                    }
                    if (_task.PercentComplete < 0.0001)
                    {
                        _task.OriginalEstimatedHours = remaining;
                        RefreshOriginalEstimatedHoursProperties();
                    }
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(EstimatedHoursDisplay));
                    OnPropertyChanged(nameof(Finish));
                    OnPropertyChanged(nameof(FinishDisplay));
                    OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
                    OnPropertyChanged(nameof(DisplayAsMilestone));
                    RecalcAncestorSummaries();
                    RefreshAncestorCalculatedProperties();
                    ScheduleSuccessors?.Invoke(this);
                }
            }
        }

        private void MoveToVirtualPredecessorStartIfNeeded(bool allowStartedReposition = false)
        {
            if (_task.StartFixed || _task.PredecessorIds.Count > 0)
                return;
            if (!allowStartedReposition && _task.PercentComplete > 0)
                return;

            var newStart = GetVirtualPredecessorStart?.Invoke(this);
            if (!newStart.HasValue || _task.Start.Date == newStart.Value.Date)
                return;

            _task.Start = newStart.Value;
            OnPropertyChanged(nameof(Start));
            OnPropertyChanged(nameof(StartDisplay));
        }

        // Setter de texto aceita horas ("32") ou dias com sufixo "d" ("5d" → 5 × horas/dia).
        // O getter não é usado pelo binding; o display fica no CellTemplate via DurationHours.
        public string DurationText
        {
            set
            {
                if (!CanEditDuration) return;
                if (!TryParseDurationText(value, out var hours))
                    return; // entrada inválida — ignora

                DurationHours = Math.Max(0, hours);
            }
        }

        /// <summary>Dias úteis fracionários (horas úteis do calendário ÷ horas por dia),
        /// exibido na coluna "DU" com uma casa decimal. Somente leitura.</summary>
        public double DurationWorkingDays =>
            _task.IsMilestone
                ? 0.0
                : ProjectCalendarService.CountWorkingHours(_task.Start, _task.Finish)
                  / ProjectCalendarService.WorkingHoursPerDay;

        public int DurationDays
        {
            get => ProjectCalendarService.CountWorkingDays(_task.Start, _task.Finish);
            set
            {
                if (UsesSfpEstimate)
                    return;

                if (value >= 0)
                {
                    _task.Finish = ProjectCalendarService.AddWorkingHours(_task.Start, value * ProjectCalendarService.WorkingHoursPerDay);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Finish));
                    OnPropertyChanged(nameof(FinishDisplay));
                    OnPropertyChanged(nameof(DurationHours));
                    OnPropertyChanged(nameof(DisplayAsMilestone));
                    RecalcAncestorSummaries();
                }
            }
        }

        public double? SfpPoints
        {
            get => _task.SfpPoints;
            set
            {
                _task.SfpPoints = value.HasValue && value.Value > 0 ? value.Value : null;
                if (UsesSfpEstimate)
                    ApplySfpDuration();

                OnPropertyChanged();
                OnPropertyChanged(nameof(UsesSfpEstimate));
                OnPropertyChanged(nameof(CanEditDuration));
                OnPropertyChanged(nameof(IsDurationReadOnly));
                OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
                OnPropertyChanged(nameof(DurationHours));
                OnPropertyChanged(nameof(Finish));
                OnPropertyChanged(nameof(FinishDisplay));
                OnPropertyChanged(nameof(DisplayAsMilestone));
            }
        }

        public bool FinishFixed
        {
            get => _task.FinishFixed;
            set
            {
                _task.FinishFixed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FinishDisplay));
                OnPropertyChanged(nameof(IsDurationReadOnly));
                FixFinishCommand.NotifyCanExecuteChanged();
            }
        }

        public string FinishDisplay =>
            _task.FinishFixed
                ? "📌 " + ProjectCalendarService.GetInclusiveFinishDate(_task.Start, _task.Finish).ToString("dd/MM/yy")
                : ProjectCalendarService.GetInclusiveFinishDate(_task.Start, _task.Finish).ToString("dd/MM/yy");

        [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanFixFinish))]
        private void FixFinish() => FinishFixed = true;
        private bool CanFixFinish() => !_task.FinishFixed;

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void ClearFinishFixed()
        {
            _task.FinishFixed = false;
            OnPropertyChanged(nameof(FinishFixed));
            OnPropertyChanged(nameof(FinishDisplay));
            OnPropertyChanged(nameof(IsDurationReadOnly));
        }

        // Setter de texto aceita data válida (marca fix) ou "0" (limpa fix).
        public string FinishText
        {
            set
            {
                var raw = value?.Trim() ?? string.Empty;
                if (raw == "0")
                {
                    FinishFixed = false;
                }
                else if (DateTime.TryParse(raw, out var parsed))
                {
                    var inclusive = ProjectCalendarService.GetInclusiveFinishDate(_task.Start, _task.Finish);
                    if (parsed.Date != inclusive.Date)
                    {
                        _task.Finish = parsed.Date.AddDays(1);
                        _task.FinishFixed = true;
                        OnPropertyChanged(nameof(Finish));
                        OnPropertyChanged(nameof(FinishFixed));
                        OnPropertyChanged(nameof(FinishDisplay));
                        OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
                        OnPropertyChanged(nameof(DurationHours));
                        RecalcAncestorSummaries();
                    }
                }
            }
        }

        public bool UsesSfpEstimate => (SfpPoints ?? 0) > 0;

        public TaskViewModel? ParentViewModel { get; set; }

        public List<TaskViewModel> ChildrenViewModels { get; } = new();

        // ── %Daily: progresso ESPERADO ate hoje (baseline pela agenda) ─────────
        // So para atividades em aberto (< 100%). Total = HH Atual + HH Restante
        // distribuido no calendario entre Inicio e Fim; o esperado e a fracao de
        // horas uteis decorridas ate hoje (limitada ao total planejado).
        private double? ComputeDailyPercent()
        {
            if (_task.IsSummary) return null;
            if (_task.PercentComplete >= 100) return null;

            var span = ProjectCalendarService.CountWorkingHours(_task.Start, _task.Finish);
            if (span <= 0.0001) return null;

            var elapsed = ProjectCalendarService.CountWorkingHours(_task.Start, DateTime.Today);
            if (elapsed < 0) elapsed = 0;
            return Math.Clamp(elapsed / span, 0.0, 1.0) * 100.0;
        }

        public double? DailyPercent => ComputeDailyPercent();
        public bool HasDailyPercent => DailyPercent.HasValue;
        public string DailyPercentDisplay => DailyPercent is { } d ? d.ToString("0.#") + "%" : string.Empty;

        /// <summary>True quando %Daily difere do % Conclusao digitado (destaca na grid).</summary>
        public bool DailyPercentDiffers
        {
            get { var d = DailyPercent; return d.HasValue && Math.Abs(d.Value - _task.PercentComplete) >= 0.5; }
        }

        /// <summary>Fundo de destaque: vermelho se atrasada (deveria estar mais), verde se adiantada.</summary>
        public Brush DailyPercentBackground
        {
            get
            {
                var d = DailyPercent;
                if (!d.HasValue || Math.Abs(d.Value - _task.PercentComplete) < 0.5)
                    return Brushes.Transparent;
                return d.Value > _task.PercentComplete
                    ? new SolidColorBrush(Color.FromRgb(0xFB, 0xE0, 0xE0))   // atrasada (esperado > realizado)
                    : new SolidColorBrush(Color.FromRgb(0xE2, 0xF3, 0xE2));  // adiantada (esperado < realizado)
            }
        }

        private void NotifyDaily()
        {
            OnPropertyChanged(nameof(DailyPercent));
            OnPropertyChanged(nameof(DailyPercentDisplay));
            OnPropertyChanged(nameof(DailyPercentDiffers));
            OnPropertyChanged(nameof(DailyPercentBackground));
        }

        public double PercentComplete
        {
            get
            {
                if (!CanEditPercentComplete)
                    return CalculateSummaryPercent();

                return _task.PercentComplete;
            }
            set
            {
                if (!CanEditPercentComplete)
                    return;

                var normalized = Math.Clamp(value, 0, 100);
                if (Math.Abs(_task.PercentComplete - normalized) < 0.0001)
                    return;

                // Concluindo (transição para 100%) com a sprint fora da faixa de
                // período (o fim da atividade não cai na janela da sprint): confirma.
                if (normalized >= 100 && _task.PercentComplete < 100 &&
                    !string.IsNullOrWhiteSpace(_task.TfsIterationPath) &&
                    ConfirmCompleteOutsideSprint != null)
                {
                    var sprint = GetSprintByPath?.Invoke(_task.TfsIterationPath);
                    // Só confirma se a sprint existir E tiver faixa de período válida
                    // (sprints sem datas — ex.: geradas pela IA — não disparam a mensagem).
                    var hasValidRange = sprint != null
                        && sprint.Start != default && sprint.End != default
                        && sprint.End.Date > sprint.Start.Date;
                    if (hasValidRange)
                    {
                        var finish = ProjectCalendarService.GetInclusiveFinishDate(_task.Start, _task.Finish).Date;
                        var inRange = sprint!.Start.Date <= finish && finish <= sprint.End.Date;
                        if (!inRange && !ConfirmCompleteOutsideSprint(this, sprint))
                        {
                            // Cancelado: reverte a exibição na grade para o valor atual.
                            OnPropertyChanged(nameof(PercentComplete));
                            return;
                        }
                    }
                }

                var wasZero = _task.PercentComplete < 0.0001;
                _task.PercentComplete = normalized;
                NotifyDaily();
                NotifySprintPeriodChanged();

                // Regra: ao iniciar (% 0 → >0), ancoramos o início no dia atual se estiver no futuro.
                // Depois do início, o Start não pode mais ser movido pela cascata.
                if (wasZero && normalized > 0 && _task.Start.Date > DateTime.Today)
                {
                    _task.Start = DateTime.Today;
                    if (!_task.FinishFixed)
                        _task.Finish = Services.TaskScheduleService.CalculateFinishFromAssignments(_task, DateTime.Today);
                    OnPropertyChanged(nameof(Start));
                    OnPropertyChanged(nameof(StartDisplay));
                }

                // Ao zerar o %, restaura HH para a estimativa original (antes de qualquer andamento).
                if (normalized < 0.0001 && !wasZero)
                {
                    var originalH = _task.OriginalEstimatedHours is > 0
                        ? _task.OriginalEstimatedHours.Value
                        : ProjectCalendarService.CountWorkingHours(_task.Start, _task.Finish);
                    _task.CurrentHours   = 0;
                    _task.EstimatedHours = originalH > 0 ? originalH : _task.EstimatedHours;
                    SyncAssignmentEstimatedHours(_task.EstimatedHours ?? 0);
                    OnPropertyChanged(nameof(CurrentHours));
                    OnPropertyChanged(nameof(CurrentHoursDisplay));
                    OnPropertyChanged(nameof(EstimatedHoursDisplay));
                    if (!_task.FinishFixed && originalH > 0)
                    {
                        var effectiveOrigH = Services.TaskScheduleService.GetEffectiveDurationHours(_task);
                        _task.Finish = ProjectCalendarService.AddWorkingHours(_task.Start, effectiveOrigH > 0 ? effectiveOrigH : originalH);
                        OnPropertyChanged(nameof(Finish));
                        OnPropertyChanged(nameof(FinishDisplay));
                        OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
                        OnPropertyChanged(nameof(DurationHours));
                        OnPropertyChanged(nameof(DisplayAsMilestone));
                        RecalcAncestorSummaries();
                        ScheduleSuccessors?.Invoke(this);
                    }
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PercentCompleteTextBrush));
                    OnPropertyChanged(nameof(PercentCompleteTextAlignment));
                    OnPropertyChanged(nameof(PercentCompleteTextMargin));
                    OnPropertyChanged(nameof(OriginalEstimatedHoursDisplay));
                    OnPropertyChanged(nameof(OriginalEstimatedHoursText));
                    OnPropertyChanged(nameof(HasOriginalEstimate));
                    OnPropertyChanged(nameof(IsDurationReadOnly));
                    NotifyParentPercentChanged();
                    return;
                }

                // Recalcula HH Atual e HH Restante com base no % e na duração total fixada.
                // Usa CountWorkingHours como fallback quando HH estão zerados (sem informação).
                var totalH = _task.CurrentHours is > 0
                    ? _task.CurrentHours.Value + (_task.EstimatedHours ?? 0)
                    : ((_task.EstimatedHours is > 0 ? _task.EstimatedHours.Value : (double?)null)
                       ?? ProjectCalendarService.CountWorkingHours(_task.Start, _task.Finish));
                if (totalH > 0)
                {
                    var split = ScheduleHoursService.SplitByPercentComplete(totalH, normalized);
                    var newCurrentH = Math.Round(split.CurrentHours, 2);
                    var newRemainingH = Math.Round(split.RemainingHours, 2);
                    _task.CurrentHours   = newCurrentH > 0 ? newCurrentH : null;
                    _task.EstimatedHours = newRemainingH > 0 ? newRemainingH : 0;
                    SyncAssignmentEstimatedHours(_task.EstimatedHours ?? 0);
                    OnPropertyChanged(nameof(CurrentHours));
                    OnPropertyChanged(nameof(CurrentHoursDisplay));
                    OnPropertyChanged(nameof(EstimatedHoursDisplay));
                }

                if (normalized >= 100)
                {
                    if (!_task.FinishFixed && totalH > 0)
                    {
                        var calculatedFinish = Services.TaskScheduleService.CalculateFinishFromAssignments(_task, _task.Start);
                        _task.Finish = _task.StartFixed && _task.Start.Date > DateTime.Today
                            ? _task.Start.Date
                            : calculatedFinish.Date > DateTime.Today
                            ? DateTime.Today
                            : calculatedFinish;
                        OnPropertyChanged(nameof(Finish));
                        OnPropertyChanged(nameof(FinishDisplay));
                        OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
                        OnPropertyChanged(nameof(DurationHours));
                        OnPropertyChanged(nameof(DisplayAsMilestone));
                        RecalcAncestorSummaries();
                        ScheduleSuccessors?.Invoke(this);
                    }
                }
                else if (!_task.FinishFixed)
                {
                    // Restaura Finish com base no total (HH Atual + HH Restante).
                    var restoreH = (_task.CurrentHours ?? 0) + (_task.EstimatedHours ?? 0);
                    if (restoreH > 0)
                    {
                        _task.Finish = Services.TaskScheduleService.CalculateFinishFromAssignments(_task, _task.Start);
                        OnPropertyChanged(nameof(Finish));
                        OnPropertyChanged(nameof(FinishDisplay));
                        OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
                        OnPropertyChanged(nameof(DurationHours));
                        OnPropertyChanged(nameof(DisplayAsMilestone));
                        RecalcAncestorSummaries();
                        ScheduleSuccessors?.Invoke(this);
                    }
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentCompleteTextBrush));
                OnPropertyChanged(nameof(PercentCompleteTextAlignment));
                OnPropertyChanged(nameof(PercentCompleteTextMargin));
                OnPropertyChanged(nameof(OriginalEstimatedHoursDisplay));
                OnPropertyChanged(nameof(OriginalEstimatedHoursText));
                OnPropertyChanged(nameof(HasOriginalEstimate));
                OnPropertyChanged(nameof(IsDurationReadOnly));
                NotifyParentPercentChanged();
            }
        }

        public bool CanEditPercentComplete => _task.Children.Count == 0;

        public string? OriginalEstimatedHoursText
        {
            get
            {
                var orig = GetCalculatedOriginalEstimatedHours(_task);
                if (orig == null || orig <= 0) return null;
                var current = GetCalculatedEstimatedHours(_task) ?? DurationHours;
                var diff = current - orig.Value;
                var diffText = Math.Abs(diff) < 0.05 ? "" :
                    diff > 0 ? $"  (+{diff:0.#}h)" : $"  ({diff:0.#}h)";
                return $"Estimativa original: {orig.Value:0.#}h{diffText}";
            }
        }

        public bool HasOriginalEstimate => GetCalculatedOriginalEstimatedHours(_task) is > 0;

        // Valor exibido na coluna OrgH
        public string? OriginalEstimatedHoursDisplay =>
            GetCalculatedOriginalEstimatedHours(_task) is { } hours && hours > 0 ? $"{hours:0}" : null;

        public bool UseOriginalHoursView => _task.UseOriginalHoursView;

        // Valor de HH Original formatado para exibição compacta na célula (ex: "↑40").
        public string? OriginalHoursDisplay =>
            _task.UseOriginalHoursView && GetCalculatedOriginalEstimatedHours(_task) is { } hours && hours > 0
                ? $"↑{hours:0}"
                : null;

        public bool CanEditDuration => _task.Children.Count == 0 && !UsesSfpEstimate;

        public bool IsDurationReadOnly => !CanEditDuration || _task.PercentComplete >= 100;

        public bool IsNoDevOpsActivity => IsNoDevOpsType(_task.TfsType);

        public bool IsDevOpsMilestoneActivity => IsDevOpsMilestoneType(_task.TfsType);

        public bool CanUseZeroDuration => IsNoDevOpsActivity || IsDevOpsMilestoneActivity;

        public static bool IsNoDevOpsType(string? type)
        {
            var normalized = (type ?? string.Empty)
                .Trim()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);
            return string.Equals(normalized, "NoDevOps", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsDevOpsMilestoneType(string? type)
        {
            var normalized = (type ?? string.Empty)
                .Trim()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);
            return string.Equals(normalized, "MarcoDevops", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryParseDurationText(string? value, out double hours)
        {
            var raw = value?.Trim() ?? string.Empty;
            if (raw.EndsWith("d", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(raw[..^1].Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.CurrentCulture, out var days))
            {
                hours = days * ProjectCalendarService.WorkingHoursPerDay;
                return true;
            }

            return double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.CurrentCulture, out hours);
        }

        public double? CurrentHours => _task.CurrentHours;
        public double? EstimatedHoursValue => _task.EstimatedHours;

        public string CurrentHoursDisplay =>
            _task.CurrentHours.HasValue ? _task.CurrentHours.Value.ToString("0.#") : "";
        public string EstimatedHoursDisplay
        {
            get
            {
                if (_task.EstimatedHours is > 0) return _task.EstimatedHours.Value.ToString("0.#");
                // Se não há HH Atual nem HH Restante mas há estimativa original, deriva pelo %C.
                if (!_task.IsSummary && !(_task.CurrentHours is > 0) && _task.OriginalEstimatedHours is > 0)
                {
                    var orig = _task.OriginalEstimatedHours.Value;
                    var remaining = Math.Max(0, orig - Math.Round(orig * (_task.PercentComplete / 100.0), 4));
                    if (remaining > 0) return remaining.ToString("0.#");
                }
                return "";
            }
        }

        public void RefreshDerivedDisplayProperties()
        {
            OnPropertyChanged(nameof(DurationHours));
            OnPropertyChanged(nameof(EstimatedHoursDisplay));
            OnPropertyChanged(nameof(CurrentHoursDisplay));
            RefreshOriginalEstimatedHoursProperties();
        }

        private void SyncAssignmentEstimatedHours(double remainingHours)
        {
            if (_task.Resources.Count == 0)
                return;

            remainingHours = Math.Max(0, Math.Round(remainingHours, 2));
            if (_task.Resources.Count == 1)
            {
                _task.Resources[0].EstimatedHours = remainingHours;
                return;
            }

            var existingTotal = _task.Resources
                .Where(r => r.EstimatedHours is > 0)
                .Sum(r => r.EstimatedHours!.Value);
            var allocationTotal = _task.Resources
                .Sum(r => Math.Max(0.0, TaskScheduleService.NormalizeAllocationPercent(r.AllocationPercent)));

            foreach (var resource in _task.Resources)
            {
                var weight = existingTotal > 0
                    ? Math.Max(0.0, resource.EstimatedHours ?? 0) / existingTotal
                    : Math.Max(0.0, TaskScheduleService.NormalizeAllocationPercent(resource.AllocationPercent)) /
                      Math.Max(0.01, allocationTotal);
                resource.EstimatedHours = Math.Round(remainingHours * weight, 2);
            }
        }

        private void RefreshOriginalEstimatedHoursProperties()
        {
            OnPropertyChanged(nameof(OriginalEstimatedHoursDisplay));
            OnPropertyChanged(nameof(OriginalEstimatedHoursText));
            OnPropertyChanged(nameof(HasOriginalEstimate));
            OnPropertyChanged(nameof(OriginalHoursDisplay));
        }

        private static double? GetCalculatedOriginalEstimatedHours(ProjectTask task)
        {
            if (task.Children.Count == 0)
                return task.OriginalEstimatedHours is > 0 ? task.OriginalEstimatedHours.Value : null;

            var total = task.Children
                .Select(GetCalculatedOriginalEstimatedHours)
                .Where(h => h is > 0)
                .Sum(h => h!.Value);

            return total > 0 ? total : null;
        }

        private static double? GetCalculatedEstimatedHours(ProjectTask task)
        {
            if (task.Children.Count == 0)
            {
                var hours = task.EstimatedHours ?? task.DurationHours;
                return hours > 0 ? hours : null;
            }

            var total = task.Children
                .Select(GetCalculatedEstimatedHours)
                .Where(h => h is > 0)
                .Sum(h => h!.Value);

            return total > 0 ? total : null;
        }

        private void RefreshAncestorCalculatedProperties()
        {
            var current = ParentViewModel;
            while (current != null)
            {
                current.OnPropertyChanged(nameof(DurationHours));
                current.OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
                current.OnPropertyChanged(nameof(Finish));
                current.OnPropertyChanged(nameof(FinishDisplay));
                current.OnPropertyChanged(nameof(DisplayAsMilestone));
                current.RefreshOriginalEstimatedHoursProperties();
                current = current.ParentViewModel;
            }
        }

        public void SetOriginalHoursView(bool useOriginal)
        {
            if (useOriginal == _task.UseOriginalHoursView) return;
            if (useOriginal && !(GetCalculatedOriginalEstimatedHours(_task) is > 0)) return;

            _task.UseOriginalHoursView = useOriginal;
            OnPropertyChanged(nameof(UseOriginalHoursView));
            OnPropertyChanged(nameof(IsDurationReadOnly));
            OnPropertyChanged(nameof(OriginalEstimatedHoursText));
            OnPropertyChanged(nameof(OriginalHoursDisplay));
        }

        public Brush PercentCompleteTextBrush =>
            PercentComplete > 30
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(31, 41, 55));

        public HorizontalAlignment PercentCompleteTextAlignment =>
            PercentComplete <= 30 || PercentComplete >= 60
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Left;

        public Thickness PercentCompleteTextMargin =>
            PercentComplete <= 30 || PercentComplete >= 60
                ? new Thickness(0)
                : new Thickness(4, 0, 0, 0);

        private void NotifyParentPercentChanged()
        {
            ParentViewModel?.OnSummaryPercentChanged();
        }

        private void OnSummaryPercentChanged()
        {
            OnPropertyChanged(nameof(PercentComplete));
            OnPropertyChanged(nameof(PercentCompleteTextBrush));
            OnPropertyChanged(nameof(PercentCompleteTextAlignment));
            OnPropertyChanged(nameof(PercentCompleteTextMargin));
            ParentViewModel?.OnSummaryPercentChanged();
        }

        private double CalculateSummaryPercent()
        {
            // _task.PercentComplete é mantido atualizado por RecalcSummary (via RebuildFlatTasks
            // e RecalcAncestorSummaries). A fórmula usa HH Atual / (HH Atual + HH Restante)
            // de todas as folhas descendentes (ver ProjectTask.RecalcPercentComplete).
            return _task.PercentComplete;
        }

        public bool IsMilestone
        {
            get => _task.IsMilestone;
            set
            {
                _task.IsMilestone = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayAsMilestone));
            }
        }

        public bool DisplayAsMilestone
        {
            get
            {
                if (IsSummary)
                    return false;

                var totalTrackedHours = (_task.CurrentHours ?? 0) + (_task.EstimatedHours ?? 0);
                var hasZeroTrackedHours = totalTrackedHours <= 0.0001;

                return hasZeroTrackedHours &&
                    (IsMilestone ||
                     (_task.PercentComplete < 100 && !(_task.OriginalEstimatedHours is > 0)));
            }
        }

        public bool IsSummary
        {
            get => _task.IsSummary;
            set
            {
                _task.IsSummary = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEditPercentComplete));
                OnPropertyChanged(nameof(PercentComplete));
            }
        }

        public int SprintNumber
        {
            get => _task.SprintNumber;
            set
            {
                _task.SprintNumber = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SprintDisplay));
            }
        }

        // Caminho da sprint no DevOps (System.IterationPath). Editável pela grade:
        // ao escolher outra sprint, o caminho muda e é sincronizado de volta.
        public string? SprintPath
        {
            get => _task.TfsIterationPath;
            set
            {
                if (string.Equals(_task.TfsIterationPath, value, StringComparison.Ordinal)) return;
                _task.TfsIterationPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SprintDisplay));
            }
        }

        // Só Feature e Story têm sprint. Projeto/Epic (e, no DevOps, qualquer outro
        // tipo) não. Tarefas sem vínculo DevOps (TfsType vazio) usam a numeração
        // sintética do cronograma e, por isso, suportam sprint.
        public bool SupportsSprint
        {
            get
            {
                var t = _task.TfsType?.Trim();
                if (string.IsNullOrEmpty(t)) return true;
                return string.Equals(t, "Feature", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t, "Story", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t, "User Story", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t, "Task", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Texto exibido na coluna Sprint: nome da sprint do DevOps (folha do
        // IterationPath) quando vinculada; senão o número sintético do cronograma.
        // Em branco para Projeto/Epic (não têm sprint).
        public string SprintDisplay
        {
            get
            {
                if (!SupportsSprint) return string.Empty;

                var path = _task.TfsIterationPath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var idx = path.LastIndexOf('\\');
                    return idx >= 0 ? path[(idx + 1)..] : path;
                }
                return _task.SprintNumber > 0 ? _task.SprintNumber.ToString() : string.Empty;
            }
        }

        public string PredecessorsText
        {
            get
            {
                // Exibe o DisplayId (TfsId se vinculado, senão Id interno) de cada predecessora.
                if (FindByInternalId == null)
                    return string.Join(",", _task.PredecessorIds);

                var parts = new System.Collections.Generic.List<string>();
                foreach (var internalId in _task.PredecessorIds)
                {
                    var pred = FindByInternalId(internalId);
                    parts.Add(pred != null ? pred.DisplayId : internalId.ToString());
                }
                return string.Join(",", parts);
            }
            set
            {
                if (!CanEditPredecessors)
                    return;

                _task.PredecessorIds.Clear();
                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var raw = part.Trim();
                    if (string.IsNullOrEmpty(raw)) continue;

                    // Tenta resolver pelo DisplayId (TfsId ou Id interno) → armazena Id interno.
                    if (FindByDisplayId != null)
                    {
                        var resolved = FindByDisplayId(raw);
                        if (resolved.HasValue)
                        {
                            var predecessor = FindByInternalId?.Invoke(resolved.Value);
                            if (!CanUsePredecessor(predecessor))
                                continue;

                            _task.PredecessorIds.Add(resolved.Value);
                            continue;
                        }
                    }
                    // Fallback: trata como Id interno direto.
                    if (int.TryParse(raw, out int id))
                    {
                        var predecessor = FindByInternalId?.Invoke(id);
                        if (CanUsePredecessor(predecessor))
                            _task.PredecessorIds.Add(id);
                    }
                }
                if (_task.PredecessorIds.Count == 0)
                {
                    if (_task.StartFixed)
                    {
                        _task.StartFixed = false;
                        OnPropertyChanged(nameof(StartFixed));
                        OnPropertyChanged(nameof(StartDisplay));
                    }
                    // Recalcula início a partir do irmão anterior (ou início da sprint)
                    var newStart = GetSprintStart?.Invoke() ?? _task.Start;
                    if (newStart != _task.Start)
                    {
                        _task.Start = newStart;
                        if (!_task.FinishFixed)
                            _task.Finish = Services.TaskScheduleService.CalculateFinishFromAssignments(_task, newStart);
                        OnPropertyChanged(nameof(Start));
                        OnPropertyChanged(nameof(Finish));
                        OnPropertyChanged(nameof(StartDisplay));
                        OnPropertyChanged(nameof(FinishDisplay));
                        OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
                        RecalcAncestorSummaries();
                        ScheduleSuccessors?.Invoke(this);
                    }
                }

                MoveAfterLatestPredecessor();
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Remove TODAS as predecessoras (inclusive as não resolvidas / fora do
        /// projeto — work items do TFS que não estão no cronograma) e limpa o
        /// destaque amarelo. Reusa a lógica do setter de PredecessorsText.
        /// </summary>
        public void ClearAllPredecessors()
        {
            if (_task.PredecessorIds.Count == 0 && !_task.HasBrokenPredecessorLink)
                return;

            // Setter vazio já limpa os IDs (inclusive os não resolvidos) e recalcula início.
            PredecessorsText = string.Empty;

            _task.HasBrokenPredecessorLink = false;
            OnPropertyChanged(nameof(PredecessorsText));
            OnPropertyChanged(nameof(HasUnresolvedPredecessor));
            OnPropertyChanged(nameof(HasBrokenPredecessorLink));
        }

        private bool CanUsePredecessor(TaskViewModel? predecessor)
        {
            if (IsNoDevOpsActivity || predecessor == null)
                return true;

            return predecessor.Model.TfsId is > 0 || !predecessor.IsNoDevOpsActivity;
        }

        public bool CanEditPredecessors => _task.Children.Count == 0;

        public void MoveAfterLatestPredecessor(bool allowStartedReposition = false)
        {
            if (_task.PredecessorIds.Count == 0 || FindByInternalId == null)
                return;

            var predecessors = _task.PredecessorIds
                .Select(id => FindByInternalId(id))
                .Where(pred => pred != null && !ReferenceEquals(pred, this))
                .Cast<TaskViewModel>()
                .ToList();

            if (predecessors.Count == 0)
                return;

            var latestFinish = predecessors
                .Select(pred => ProjectCalendarService.GetInclusiveFinishDate(pred.Model.Start, pred.Model.Finish))
                .Max();
            var nextStart = ProjectCalendarService.AddWorkingDays(latestFinish, 1);
            if (_task.Start.Date == nextStart.Date)
            {
                // Já na posição correta, mas propaga para que irmãos virtuais (mesmo recurso)
                // sejam ajustados caso ainda não tenham sido processados.
                ScheduleSuccessors?.Invoke(this);
                return;
            }

            // Data fixada pelo usuário (📌) não move o Start.
            // Tarefas já iniciadas só são reposicionadas no recálculo geral de predecessoras.
            if (_task.StartFixed || (!allowStartedReposition && _task.PercentComplete > 0))
            {
                ScheduleSuccessors?.Invoke(this);
                return;
            }

            _task.Start = nextStart;
            _task.Finish = Services.TaskScheduleService.CalculateFinishFromAssignments(_task, nextStart);
            // Não marca StartFixed: o início é calculado pela predecessora, não fixo pelo usuário.

            OnPropertyChanged(nameof(Start));
            OnPropertyChanged(nameof(StartDisplay));
            OnPropertyChanged(nameof(Finish));
            OnPropertyChanged(nameof(FinishDisplay));
            OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
            OnPropertyChanged(nameof(DurationHours));
            OnPropertyChanged(nameof(DisplayAsMilestone));
            OnPropertyChanged(nameof(StartFixed));
            RecalcAncestorSummaries();
            ScheduleSuccessors?.Invoke(this);
        }

        // Dispara notificações de data/duração após alteração direta no Model (sem passar pelo setter).
        public void NotifySprintChanged()
        {
            OnPropertyChanged(nameof(SprintDisplay));
            OnPropertyChanged(nameof(SprintPath));
            OnPropertyChanged(nameof(IsFinishBeyondSprint));
            NotifySprintPeriodChanged();
        }

        // Notifica os estados de período da sprint (destaque na célula).
        public void NotifySprintPeriodChanged()
        {
            OnPropertyChanged(nameof(SprintReferenceDate));
            OnPropertyChanged(nameof(IsSprintOutOfPeriod));
            OnPropertyChanged(nameof(IsSprintCompletedEarly));
            OnPropertyChanged(nameof(IsSprintActiveMismatch));
        }

        public void NotifyDatesChanged()
        {
            OnPropertyChanged(nameof(Start));
            OnPropertyChanged(nameof(StartDisplay));
            OnPropertyChanged(nameof(Finish));
            OnPropertyChanged(nameof(FinishDisplay));
            OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
            OnPropertyChanged(nameof(DurationHours));
            OnPropertyChanged(nameof(DisplayAsMilestone));
            OnPropertyChanged(nameof(StartFixed));
            OnPropertyChanged(nameof(IsFinishBeyondSprint));
            NotifySprintPeriodChanged();
            RecalcAncestorSummaries();
        }

        public string ResourcesText
        {
            get
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (var r in _task.Resources)
                    names.Add(r.ToString());
                return string.Join(", ", names);
            }
        }

        public string PercAlocText
        {
            get
            {
                if (_task.Resources.Count == 0) return string.Empty;
                if (_task.Resources.Count == 1)
                    return $"{_task.Resources[0].AllocationPercent:0}%";
                return string.Join(" / ", _task.Resources.Select(r => $"{r.AllocationPercent:0}%"));
            }
        }

        public void NotifyResourcesChanged()
        {
            OnPropertyChanged(nameof(ResourcesText));
            OnPropertyChanged(nameof(PercAlocText));
        }

        public void RecalcFinishFromPercAloc()
        {
            if (_task.FinishFixed || _task.IsSummary || _task.IsMilestone) return;

            // Propaga o HH da tarefa para os assignments que ainda não têm HH explícito,
            // para que o fator de alocação/disponibilidade seja aplicado sobre as HORAS
            // DE TRABALHO (nunca sobre o span de calendário — isso inflaria a cada edição).
            var workHours = _task.EstimatedHours ?? _task.OriginalEstimatedHours;
            if (workHours is > 0)
                foreach (var r in _task.Resources)
                    if (!r.EstimatedHours.HasValue || r.EstimatedHours.Value <= 0)
                        r.EstimatedHours = workHours;

            // Cálculo único e centralizado (mesma fórmula do import TFS e da abertura de arquivo).
            _task.Finish = Services.TaskScheduleService.CalculateFinishFromAssignments(_task, _task.Start);

            OnPropertyChanged(nameof(Finish));
            OnPropertyChanged(nameof(FinishDisplay));
            OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
            OnPropertyChanged(nameof(DurationHours));
            RecalcAncestorSummaries();
            ScheduleSuccessors?.Invoke(this);
        }

        // Conveniência: recurso principal (primeiro da lista). Usado pela grade para
        // permitir editar/atribuir rapidamente um recurso único.
        // Callback acionado quando o recurso primário muda, passando o ID do recurso anterior.
        public Action<TaskViewModel, int?>? PrimaryResourceChanged { get; set; }

        public NXProject.Models.Resource? PrimaryResource
        {
            get => _task.Resources.Count > 0 ? _task.Resources[0].Resource : null;
            set
            {
                var oldResourceId = _task.Resources.Count > 0 ? (int?)_task.Resources[0].ResourceId : null;

                if (value == null)
                {
                    _task.Resources.Clear();
                }
                else
                {
                    if (_task.Resources.Count > 0)
                    {
                        var first = _task.Resources[0];
                        first.ResourceId = value.Id;
                        first.Resource = value;
                    }
                    else
                    {
                        _task.Resources.Add(new NXProject.Models.TaskResource
                        {
                            ResourceId = value.Id,
                            Resource = value,
                            AllocationPercent = 100.0
                        });
                    }
                }

                OnPropertyChanged(); // PrimaryResource
                OnPropertyChanged(nameof(ResourcesText));

                var newResourceId = _task.Resources.Count > 0 ? (int?)_task.Resources[0].ResourceId : null;
                if (oldResourceId != newResourceId)
                    PrimaryResourceChanged?.Invoke(this, oldResourceId);
            }
        }

        public string? Notes
        {
            get => _task.Notes;
            set { _task.Notes = value; OnPropertyChanged(); }
        }

        public string? Justificativa
        {
            get => _task.Justificativa;
            set { _task.Justificativa = value; OnPropertyChanged(); }
        }

        public void ShiftSchedule(int dayDelta)
        {
            if (dayDelta == 0)
                return;

            _task.Start = _task.Start.AddDays(dayDelta);
            _task.Finish = _task.Finish.AddDays(dayDelta);
            OnPropertyChanged(nameof(Start));
            OnPropertyChanged(nameof(Finish));
            OnPropertyChanged(nameof(FinishDisplay));
            OnPropertyChanged(nameof(DurationDays));
                    OnPropertyChanged(nameof(DurationWorkingDays));
            OnPropertyChanged(nameof(DisplayAsMilestone));
            RecalcAncestorSummaries();
        }

        private void RecalcAncestorSummaries()
        {
            var current = _task.Parent;
            while (current != null)
            {
                current.RecalcSummary();
                current = current.Parent;
            }
        }

        private void ApplySfpDuration()
        {
            if (!UsesSfpEstimate)
                return;

            var sfpPoints = SfpPoints ?? 0;
            var daysPerSfp = sfpPoints <= 3
                ? _lowDaysPerSfp
                : sfpPoints < 6
                    ? _mediumDaysPerSfp
                    : _highDaysPerSfp;
            var calculatedWorkingDays = Math.Max(1, (int)Math.Ceiling(sfpPoints * daysPerSfp));
            var calculatedHours = calculatedWorkingDays * ProjectCalendarService.WorkingHoursPerDay;
            _task.Finish = ProjectCalendarService.AddWorkingHours(_task.Start, calculatedHours);
            OnPropertyChanged(nameof(FinishDisplay));
            RecalcAncestorSummaries();
        }

        private static double SumTaskHours(Models.ProjectTask task)
        {
            if (!task.IsSummary || task.Children.Count == 0)
            {
                var cur = task.CurrentHours ?? 0;
                var est = task.EstimatedHours ?? 0;
                return (cur > 0 || est > 0)
                    ? cur + est
                    : ProjectCalendarService.CountWorkingHours(task.Start, task.Finish);
            }
            return task.Children.Sum(SumTaskHours);
        }
    }
}
