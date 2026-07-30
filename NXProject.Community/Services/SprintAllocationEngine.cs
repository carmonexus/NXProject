using System;
using System.Collections.Generic;
using System.Linq;
using NXProject.Models;
using NXProject.ViewModels;

namespace NXProject.Services
{
    /// <summary>
    /// Coluna de sprint usada pelas matrizes de alocação. Start/End são inclusivos.
    /// </summary>
    public sealed record SprintColumn(
        int Number, string? Path, string Header, double CapacityHours, DateTime Start, DateTime End);

    /// <summary>Horas x capacidade de um recurso numa sprint, já com o transbordo aplicado.</summary>
    public sealed record SprintCell(
        double Hours, double CapacityHours, double FullCapacityHours, double? AllocationPercent,
        bool AfterDeadline = false)
    {
        // Ocupação = horas alocadas sobre a capacidade da pessoa na sprint INTEIRA.
        public double? OccupancyPercent => FullCapacityHours > 0
            ? Hours / FullCapacityHours * 100.0
            : Hours > 0 ? 100.0 : null;
    }

    /// <summary>
    /// Carga de uma pessoa no horizonte do cronograma dela: HH que precisa entregar até a
    /// última atividade x capacidade disponível até lá. O que transborda das sprints seguintes
    /// volta para esse balde — depois da última atividade não há mais prazo para trabalhar.
    /// </summary>
    public sealed record ResourceLoad(double UsedHours, double CapacityHours, DateTime? LastFinish)
    {
        public double Percent => CapacityHours > 0 ? UsedHours / CapacityHours * 100.0 : 0;
        public double BalanceHours => CapacityHours - UsedHours;
    }

    /// <summary>
    /// Motor de cálculo da alocação por sprint, compartilhado pela tela "Alocação por Sprint" e
    /// pela coluna de carga da tela "Pessoas", para que os dois números tenham a mesma base.
    /// </summary>
    public sealed class SprintAllocationEngine
    {
        private readonly MainViewModel _vm;
        private readonly bool _includeZeroPct;

        public SprintAllocationEngine(MainViewModel vm, bool includeZeroPct)
        {
            _vm = vm;
            _includeZeroPct = includeZeroPct;
        }

        // ── Colunas e recursos ────────────────────────────────────────────────────

        public List<SprintColumn> BuildSprintColumns()
        {
            var columns = new List<SprintColumn>();

            if (_vm.Project.Sprints.Count > 0)
            {
                foreach (var sprint in _vm.Project.Sprints.OrderBy(s => s.Number).ThenBy(s => s.Start))
                {
                    var sprintStart = sprint.Start;
                    var sprintEnd   = sprint.End > sprint.Start ? sprint.End : sprint.Start.AddDays(_vm.Project.SprintDurationDays);
                    var capacityHours = ProjectCalendarService.CountWorkingHours(sprintStart, sprintEnd);
                    columns.Add(new SprintColumn(
                        sprint.Number,
                        sprint.Path,
                        string.IsNullOrWhiteSpace(sprint.Name) ? $"Sprint {sprint.Number}" : sprint.Name,
                        capacityHours,
                        sprintStart,
                        sprintEnd));
                }
                return columns;
            }

            // Sprints sem datas: estima pela data de início do projeto + duração por sprint.
            var projectStart = _vm.Project.StartDate == default ? DateTime.Today : _vm.Project.StartDate;
            var durationDays = Math.Max(1, _vm.Project.SprintDurationDays);
            foreach (var number in _vm.FlatTasks
                         .Where(t => t.SprintNumber > 0)
                         .Select(t => t.SprintNumber)
                         .Distinct()
                         .OrderBy(n => n))
            {
                var sprintStart = projectStart.AddDays((number - 1) * durationDays);
                var sprintEnd   = sprintStart.AddDays(durationDays);
                columns.Add(new SprintColumn(
                    number,
                    null,
                    $"Sprint {number}",
                    durationDays * ProjectCalendarService.WorkingHoursPerDay,
                    sprintStart,
                    sprintEnd));
            }

            return columns;
        }

        // Recursos das matrizes = os do projeto + pessoas que só aparecem no resumo de tasks
        // (linhas sintéticas, para que as horas das tasks apareçam para o dono da task).
        public List<Resource> MatrixResources()
        {
            var summaryOnly = SummaryOnlyResourceNames()
                .Select((n, idx) => new Resource
                {
                    Id = -1000 - idx,
                    Name = n,
                    Kind = ResourceKind.Project,
                    Type = ResourceType.Work,
                    MaxUnitsPerDay = ProjectCalendarService.WorkingHoursPerDay
                });
            return _vm.Project.Resources.Concat(summaryOnly).OrderBy(r => r.Name).ToList();
        }

        // Nomes das pessoas que só aparecem no resumo de tasks (não são recursos do projeto).
        public IEnumerable<string> SummaryOnlyResourceNames()
        {
            var known = new HashSet<string>(_vm.Project.Resources.Select(r => r.Name ?? ""), StringComparer.OrdinalIgnoreCase);
            return _vm.FlatTasks
                .Where(StoryCountsForSummary)
                .SelectMany(t => ChargeableAllocations(t.Model).Select(a => a.Resource))
                .Where(n => !string.IsNullOrWhiteSpace(n) && !known.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        // ── Distribuição por sprint ───────────────────────────────────────────────

        /// <summary>
        /// Horas por sprint de um recurso, já com o "balde": o que não cabe na capacidade
        /// restante transborda para a sprint seguinte (a última coluna absorve o excedente).
        /// </summary>
        public List<SprintCell> ComputeSprintCells(Resource resource, List<SprintColumn> sprints)
        {
            var cells = new List<SprintCell>(sprints.Count);
            var deadline = GetLastActivityDate(resource)?.Date;
            double carry = 0;

            for (int col = 0; col < sprints.Count; col++)
            {
                var sprint = sprints[col];
                var allocationPercent = GetAverageAllocationPercent(resource, sprint);
                var capacityHours     = GetSprintCapacityHours(resource, sprint, allocationPercent);
                // Base do % de ocupação: a sprint INTEIRA, não só o que resta dela.
                var fullCapacityHours = GetSprintCapacityHours(resource, sprint, 100.0, remainingOnly: false);

                var due = GetAllocatedHours(resource, sprint) + carry;
                double hours;
                if (col == sprints.Count - 1)
                {
                    hours = due;
                    carry = 0;
                }
                else
                {
                    hours = Math.Min(due, capacityHours);
                    carry = due - hours;
                }

                if (hours > 0 && allocationPercent == null)
                    allocationPercent = 100.0;

                // Horas nesta sprint que já são posteriores ao prazo da última atividade dela.
                var afterDeadline = hours > 0 && deadline != null && sprint.Start.Date > deadline.Value;

                cells.Add(new SprintCell(hours, capacityHours, fullCapacityHours, allocationPercent, afterDeadline));
            }

            return cells;
        }

        /// <summary>
        /// Carga do recurso até a última atividade dele: soma as horas das sprints até esse
        /// prazo e devolve para dentro dele tudo que transbordou para as sprints posteriores.
        /// </summary>
        public ResourceLoad ComputeLoad(Resource resource, List<SprintColumn>? sprints = null)
        {
            sprints ??= BuildSprintColumns();
            var lastFinish = GetLastActivityDate(resource);
            if (sprints.Count == 0 || lastFinish == null)
                return new ResourceLoad(0, 0, lastFinish);

            var cells = ComputeSprintCells(resource, sprints);

            // Sprints dentro do prazo = as que começam até a última atividade. Se a última
            // atividade cai antes da primeira sprint futura, fica ao menos a sprint corrente.
            int lastIndex = -1;
            for (int i = 0; i < sprints.Count; i++)
                if (sprints[i].Start.Date <= lastFinish.Value.Date)
                    lastIndex = i;
            if (lastIndex < 0) lastIndex = 0;

            double used = 0, capacity = 0;
            for (int i = 0; i <= lastIndex; i++)
            {
                used     += cells[i].Hours;
                capacity += GetSprintCapacityHours(resource, sprints[i], 100.0);
            }

            // Balde final: o que foi empurrado para depois do prazo continua sendo carga dele.
            for (int i = lastIndex + 1; i < sprints.Count; i++)
                used += cells[i].Hours;

            return new ResourceLoad(used, capacity, lastFinish);
        }

        /// <summary>
        /// Fim da última atividade da pessoa. Conta as atribuições diretas (por Id) e também as
        /// Stories em que ela aparece só no resumo de tasks (por nome) — senão quem só tem horas
        /// pelo resumo ficaria sem prazo e sem carga.
        /// </summary>
        public DateTime? GetLastActivityDate(Resource resource)
        {
            var name = resource.Name ?? string.Empty;
            DateTime? last = null;

            foreach (var t in _vm.FlatTasks)
            {
                bool mine = IsChargeableNode(t) && t.Model.Resources.Any(r => r.ResourceId == resource.Id);

                if (!mine && !string.IsNullOrWhiteSpace(name) && StoryCountsForSummary(t))
                    mine = ChargeableAllocations(t.Model)
                        .Any(a => string.Equals(a.Resource, name, StringComparison.OrdinalIgnoreCase));

                if (!mine) continue;
                if (last == null || t.Model.Finish > last) last = t.Model.Finish;
            }

            return last;
        }

        public double GetAllocatedHours(Resource resource, SprintColumn sprint)
        {
            var hasDates = sprint.Start != default && sprint.End != default;
            double assigned = _vm.FlatTasks
                .Where(t => IsChargeableNode(t) && (hasDates ? OverlapsWithSprint(t, sprint) : BelongsToSprint(t, sprint)))
                .SelectMany(t => t.Model.Resources
                    .Where(r => r.ResourceId == resource.Id)
                    .Select(r => (hasDates
                        ? ProportionalHours(t.Model, TaskScheduleService.GetAssignmentHours(t.Model, r), sprint)
                        : TaskScheduleService.GetAssignmentHours(t.Model, r)) * DecompositionFactor(t)))
                .Sum();
            // + horas das tasks (resumo) onde esta pessoa é dona da task.
            return assigned + SummaryHoursForResource(resource.Name ?? "", sprint);
        }

        public double? GetAverageAllocationPercent(Resource resource, SprintColumn sprint)
        {
            var hasDates = sprint.Start != default && sprint.End != default;
            var assignments = _vm.FlatTasks
                .Where(t => IsChargeableNode(t) && (hasDates ? OverlapsWithSprint(t, sprint) : BelongsToSprint(t, sprint)))
                .SelectMany(t => t.Model.Resources
                    .Where(r => r.ResourceId == resource.Id)
                    .Select(r => new
                    {
                        Hours = (hasDates
                            ? ProportionalHours(t.Model, TaskScheduleService.GetAssignmentHours(t.Model, r), sprint)
                            : TaskScheduleService.GetAssignmentHours(t.Model, r)) * DecompositionFactor(t),
                        Percent = TaskScheduleService.NormalizeAllocationPercent(r.AllocationPercent)
                    }))
                .Where(a => a.Hours > 0)
                .ToList();

            if (assignments.Count == 0)
                return null;

            var totalHours = assignments.Sum(a => a.Hours);
            return totalHours > 0
                ? assignments.Sum(a => a.Percent * a.Hours) / totalHours
                : assignments.Average(a => a.Percent);
        }

        // Janela em que o HH RESTANTE ainda pode ser executado: nunca antes de hoje.
        // Atividades atrasadas (janela toda no passado) concentram o restante a partir de hoje.
        public static (DateTime Start, DateTime FinishEx) RemainingWindow(ProjectTask task)
        {
            var today      = DateTime.Today;
            var start      = task.Start.Date < today ? today : task.Start.Date;
            var finishEx   = task.Finish.Date.AddDays(1); // fim exclusivo
            if (finishEx <= start) finishEx = start.AddDays(1);
            return (start, finishEx);
        }

        // Fracção das horas da atividade que cai dentro da janela da sprint.
        public static double ProportionalHours(ProjectTask task, double totalHours, SprintColumn sprint)
        {
            if (totalHours <= 0) return 0;
            var (taskStart, taskFinishEx) = RemainingWindow(task);
            var sprintEndEx    = sprint.End.Date.AddDays(1);  // fim exclusivo

            var taskWorkingHours = ProjectCalendarService.CountWorkingHours(taskStart, taskFinishEx);
            if (taskWorkingHours <= 0) return totalHours;

            var overlapStart = taskStart     > sprint.Start ? taskStart    : sprint.Start;
            var overlapEnd   = taskFinishEx  < sprintEndEx  ? taskFinishEx : sprintEndEx;
            if (overlapEnd <= overlapStart) return 0;

            var overlapHours = ProjectCalendarService.CountWorkingHours(overlapStart, overlapEnd);
            return totalHours * (overlapHours / taskWorkingHours);
        }

        // Capacidade da pessoa na sprint. remainingOnly = quanto ainda dá para trabalhar (usado
        // pelo "balde"); caso contrário, a sprint inteira (base do % de ocupação).
        public double GetSprintCapacityHours(
            Resource resource, SprintColumn sprint, double? allocationPercent, bool remainingOnly = true)
        {
            var workingHours = SprintWorkingHours(sprint, remainingOnly);
            var capacity = workingHours is { } hours
                ? hours * Math.Max(0.0, resource.MaxUnitsPerDay) / ProjectCalendarService.WorkingHoursPerDay
                : Math.Max(1, _vm.Project.SprintDurationDays) * Math.Max(0.0, resource.MaxUnitsPerDay);

            return capacity * (allocationPercent ?? 100.0) / 100.0;
        }

        // Horas úteis da sprint (fim inclusivo). Com remainingOnly, conta só o que resta:
        // sprint passada = 0, sprint corrente = de hoje até o fim. Null quando a sprint não tem
        // datas (aí vale a duração padrão do projeto).
        public static double? SprintWorkingHours(SprintColumn sprint, bool remainingOnly)
        {
            if (sprint.Start == default || sprint.End == default) return null;

            var endEx = sprint.End.Date.AddDays(1);   // o fim da sprint é inclusivo
            var from  = sprint.Start.Date;

            if (remainingOnly)
            {
                var today = DateTime.Today;
                if (today >= endEx) return 0.0;
                if (today > from) from = today;
            }

            return ProjectCalendarService.CountWorkingHours(from, endEx);
        }

        public static bool OverlapsWithSprint(TaskViewModel task, SprintColumn sprint)
        {
            // Usa a janela do restante (nunca antes de hoje): sprints passadas não recebem HH.
            var (s, fEx) = RemainingWindow(task.Model);
            return s <= sprint.End && fEx > sprint.Start;
        }

        public static bool BelongsToSprint(TaskViewModel task, SprintColumn sprint)
        {
            if (sprint.Path != null)
                return string.Equals(task.Model.TfsIterationPath, sprint.Path, StringComparison.OrdinalIgnoreCase);
            return task.SprintNumber == sprint.Number;
        }

        // ── Decomposição do HH da Story entre os recursos ─────────────────────────
        // O total por Story = HH da Story. O responsável fica com o RESTANTE (HH da Story −
        // soma das Tasks); cada Task credita seu HH ao seu recurso. Se as Tasks estouram o HH
        // da Story, aplica fator proporcional (trava). Como a distribuição por sprint é linear
        // nas horas, basta multiplicar as horas da atribuição por este fator.
        public static bool IsStoryNode(TaskViewModel t) => TfsImportService.IsStoryTypePublic(t.Model.TfsType);

        // Conta a Story (com % conclusão > 0, ou qualquer % quando o flag "planejado" está ligado)
        // e as folhas Active/Closed (ou New quando ligado); ignora resumos Epic/Feature.
        public bool IsChargeableNode(TaskViewModel t) =>
            IsStoryNode(t)
                ? (_includeZeroPct || t.Model.PercentComplete > 0)
                : t.Model.Children.Count == 0 && CountsState(t.Model.TfsState);

        // Resumos de task que contam: Active/Closed (ou legado sem estado); com o flag, também New.
        public IEnumerable<TaskAllocationSummary> ChargeableAllocations(ProjectTask story)
            => story.TaskAllocations.Where(a => CountsState(a.State));

        // Story entra no cálculo do resumo quando tem % > 0 (ou qualquer % com o flag ligado).
        public bool StoryCountsForSummary(TaskViewModel t)
            => IsStoryNode(t) && (_includeZeroPct || t.Model.PercentComplete > 0);

        // Estados que contam: Active/Closed/legado; com o flag "planejado", também "New".
        public bool CountsState(string? state)
            => TfsImportService.AllocationCountsState(state)
               || (_includeZeroPct && TfsImportService.NormalizeTaskState(state) == "New");

        public static ProjectTask? FindParentStory(ProjectTask task)
        {
            var p = task.Parent;
            while (p != null)
            {
                if (TfsImportService.IsStoryTypePublic(p.TfsType)) return p;
                p = p.Parent;
            }
            return null;
        }

        // Base da decomposição = HH TOTAL da Story (atual + restante). Usa o resumo de tasks
        // (TaskAllocations) quando presente; senão cai nas Tasks filhas carregadas (legado).
        public static double StoryTotalHours(ProjectTask story)
            => story.Resources.Sum(r => TaskScheduleService.GetAssignmentCurrentHours(story, r)
                                      + TaskScheduleService.GetAssignmentRemainingHours(story, r));

        public double StoryTaskSum(ProjectTask story)
        {
            if (story.TaskAllocations.Count > 0)
                return ChargeableAllocations(story).Sum(a => a.Hours);
            double sum = 0;
            foreach (var leaf in GetLeafTasksModel(story.Children))
                foreach (var r in leaf.Resources)
                    sum += TaskScheduleService.GetAssignmentCurrentHours(leaf, r)
                         + TaskScheduleService.GetAssignmentRemainingHours(leaf, r);
            return sum;
        }

        public static IEnumerable<ProjectTask> GetLeafTasksModel(IEnumerable<ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                if (t.Children.Count == 0) yield return t;
                else foreach (var c in GetLeafTasksModel(t.Children)) yield return c;
            }
        }

        // Fator (0..1) que a atribuição do RESPONSÁVEL contribui após decompor o HH da Story.
        public double DecompositionFactor(TaskViewModel t)
        {
            if (IsStoryNode(t))
                return TaskScheduleService.StoryResponsibleFactor(
                    StoryTotalHours(t.Model), StoryTaskSum(t.Model));
            var story = FindParentStory(t.Model);
            if (story == null) return 1.0;
            return TaskScheduleService.StoryTaskCutFactor(
                StoryTotalHours(story), StoryTaskSum(story));
        }

        // Horas das PESSOAS DAS TASKS (resumo) para um recurso, numa sprint — distribuídas na
        // janela da Story pela mesma proporção usada nas atribuições.
        public double SummaryHoursForResource(string resourceName, SprintColumn sprint)
        {
            if (string.IsNullOrWhiteSpace(resourceName)) return 0;
            bool hasDates = sprint.Start != default && sprint.End != default;
            double total = 0;
            foreach (var t in _vm.FlatTasks)
            {
                if (!StoryCountsForSummary(t) || t.Model.TaskAllocations.Count == 0) continue;
                if (hasDates ? !OverlapsWithSprint(t, sprint) : !BelongsToSprint(t, sprint)) continue;
                double cut = TaskScheduleService.StoryTaskCutFactor(StoryTotalHours(t.Model), StoryTaskSum(t.Model));
                foreach (var a in ChargeableAllocations(t.Model))
                    if (string.Equals(a.Resource, resourceName, StringComparison.OrdinalIgnoreCase))
                        total += hasDates ? ProportionalHours(t.Model, a.Hours * cut, sprint) : a.Hours * cut;
            }
            return total;
        }
    }
}
