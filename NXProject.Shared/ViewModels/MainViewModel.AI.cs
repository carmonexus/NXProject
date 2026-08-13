using System;
using System.Collections.Generic;
using System.Linq;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.ViewModels
{
    public partial class MainViewModel
    {
        public string BuildAiProjectContext()
        {
            var existingTasks = AllTasks().Select(t => t.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Take(12).ToList();
            var resources = Project.Resources.Select(r => r.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Take(12).ToList();

            return $"""
Projeto: {Project.Name}
Descricao: {Project.Description ?? "Sem descricao"}
Data de inicio: {Project.StartDate:yyyy-MM-dd}
Duracao do sprint: {Project.SprintDurationDays} dias
Recursos atuais: {(resources.Count == 0 ? "Nenhum recurso cadastrado" : string.Join(", ", resources))}
Tarefas atuais: {(existingTasks.Count == 0 ? "Nenhuma tarefa cadastrada" : string.Join(", ", existingTasks))}
""";
        }

        /// <summary>Contexto COMPLETO do cronograma para análise/geração com IA: a árvore inteira
        /// (EPIC → Feature → Story → Task) até a folha, com HH, %, datas e responsáveis por item,
        /// mais a lista de recursos com disponibilidade. Diferente do contexto curto, não trunca.</summary>
        public string BuildFullScheduleContext()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"PROJETO: {Project.Name}");
            if (!string.IsNullOrWhiteSpace(Project.Description))
                sb.AppendLine($"Descrição: {Project.Description}");
            sb.AppendLine($"Início: {Project.StartDate:yyyy-MM-dd} | Sprint: {Project.SprintDurationDays} dias");
            sb.AppendLine();

            var people = Project.Resources
                .Where(r => r.Type == ResourceType.Work && !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => r.AvailabilityPercent > 0 && r.AvailabilityPercent != 100
                    ? $"{r.Name.Trim()} ({r.AvailabilityPercent:0}%)"
                    : r.Name.Trim())
                .ToList();
            sb.AppendLine("RECURSOS: " + (people.Count == 0 ? "nenhum cadastrado" : string.Join(" | ", people)));
            sb.AppendLine();

            sb.AppendLine("CRONOGRAMA COMPLETO (indentado por nível; cada item traz sua CHAVE id=...):");
            var byId = AllTasks().Where(x => x != null).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var root in Project.Tasks)
                AppendScheduleNode(sb, root, 0, byId);
            return sb.ToString();
        }

        private static string CleanResourceName(string? name)
            => (name ?? string.Empty).TrimStart('*').Trim();

        private static void AppendScheduleNode(System.Text.StringBuilder sb, ProjectTask t, int level,
            System.Collections.Generic.Dictionary<int, ProjectTask> byId)
        {
            var indent = new string(' ', level * 2);
            var type = string.IsNullOrWhiteSpace(t.TfsType) ? (t.IsSummary ? "Summary" : "Task") : t.TfsType!.Trim();
            var isLeaf = t.Children.Count == 0;

            sb.Append(indent).Append("- [").Append(type).Append("] ").Append(t.Name);
            sb.Append(" | id=").Append(t.DisplayId);   // CHAVE do item (preservar inalterada)

            // Predecessoras como CHAVES (DisplayId) das tarefas referenciadas.
            if (t.PredecessorIds.Count > 0)
            {
                var preds = t.PredecessorIds
                    .Where(byId.ContainsKey)
                    .Select(id => byId[id].DisplayId)
                    .ToList();
                if (preds.Count > 0) sb.Append(" | pred=").Append(string.Join(",", preds));
            }
            if (t.StartFixed) sb.Append($" | dataFixaInicio={t.Start:yyyy-MM-dd}");
            if (t.FinishFixed) sb.Append($" | dataFixaFim={t.Finish:yyyy-MM-dd}");

            if (isLeaf)
            {
                var cur = t.CurrentHours ?? 0;
                var est = t.EstimatedHours ?? 0;
                var total = cur + est;
                if (total > 0) sb.Append($" | HH {total:0.##} (atual {cur:0.##}/rest {est:0.##})");
                // HH Original (planejado) — a IA deve devolver inalterado no campo originalHours.
                var orig = t.OriginalEstimatedHours ?? 0;
                if (orig > 0) sb.Append($" | hhOriginal={orig:0.##}");
                sb.Append($" | {t.PercentComplete:0}%");
                sb.Append($" | {t.Start:dd/MM}-{t.Finish:dd/MM}");
                var resp = t.Resources
                    .Where(a => a.Resource != null && !string.IsNullOrWhiteSpace(CleanResourceName(a.Resource!.Name)))
                    .Select(a => a.AllocationPercent != 100
                        ? $"{CleanResourceName(a.Resource!.Name)} {a.AllocationPercent:0}%"
                        : CleanResourceName(a.Resource!.Name))
                    .ToList();
                if (resp.Count > 0) sb.Append(" | Resp: ").Append(string.Join(", ", resp));
            }
            else
            {
                sb.Append($" | {t.PercentComplete:0}%");
            }
            sb.AppendLine();

            foreach (var c in t.Children)
                AppendScheduleNode(sb, c, level + 1, byId);
        }

        public string BuildAiScheduleAnalysisContext(int taskLimit = 30)
        {
            if (taskLimit <= 0) taskLimit = 30;
            var tasks = AllTasks().Where(t => !string.IsNullOrWhiteSpace(t.Name)).Take(taskLimit).ToList();
            var taskLines = tasks.Select(t =>
            {
                var durationDays = Math.Max(1, (int)Math.Round((t.Finish.Date - t.Start.Date).TotalDays + 1));
                var assignee = t.Resources.FirstOrDefault()?.Resource?.Name ?? "-";
                return $"- {t.Name} | Inicio: {t.Start:yyyy-MM-dd} | Fim: {t.Finish:yyyy-MM-dd} | Duracao: {durationDays} dias | Responsavel: {assignee}";
            });

            return $"""
Projeto: {Project.Name}
Descricao: {Project.Description ?? "Sem descricao"}
Data de inicio: {Project.StartDate:yyyy-MM-dd}
Duracao do sprint: {Project.SprintDurationDays} dias

Tarefas do cronograma:
{string.Join("\n", taskLines)}

Instrucao para a IA: analise o cronograma, valide dependencias, estimativas e alocacao de recursos, e retorne observacoes em HTML ou formato estruturado.
""";
        }

        public int ApplyAiTaskSuggestions(IEnumerable<AITaskSuggestion> suggestions)
        {
            var createdCount = 0;
            var createdTasks = new Dictionary<string, ProjectTask>(StringComparer.OrdinalIgnoreCase);
            var existingTasks = AllTasks()
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .GroupBy(t => t.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            ProjectTask? previousCreatedTask = null;
            var cursor = SelectedTask?.Model?.Finish
                ?? AllTasks().Select(t => t.Finish).DefaultIfEmpty(Project.StartDate).Max();

            foreach (var suggestion in suggestions)
            {
                if (string.IsNullOrWhiteSpace(suggestion.Name))
                    continue;

                ProjectTask? predecessorTask = null;
                if (!string.IsNullOrWhiteSpace(suggestion.PredecessorTaskName))
                {
                    var predecessorName = suggestion.PredecessorTaskName.Trim();
                    if (!createdTasks.TryGetValue(predecessorName, out predecessorTask))
                        existingTasks.TryGetValue(predecessorName, out predecessorTask);
                }

                if (predecessorTask == null && previousCreatedTask != null)
                    predecessorTask = previousCreatedTask;

                var start = predecessorTask?.Finish ?? cursor;
                var durationHours = suggestion.HasDurationHours
                    ? suggestion.DurationHours
                    : ProjectCalendarService.WorkingHoursPerDay;
                var finish = suggestion.HasDurationHours
                    ? (suggestion.DurationHours == 0.0 ? start : ProjectCalendarService.AddWorkingHours(start, durationHours))
                    : ProjectCalendarService.AddWorkingHours(start, durationHours);

                var task = new ProjectTask
                {
                    Id = _nextId++,
                    Name = suggestion.Name.Trim(),
                    Start = start,
                    Finish = finish,
                    Notes = string.IsNullOrWhiteSpace(suggestion.Notes) ? null : suggestion.Notes.Trim(),
                    EstimatedHours = suggestion.HasDurationHours ? suggestion.DurationHours : ProjectCalendarService.WorkingHoursPerDay
                };

                if (predecessorTask != null)
                    task.PredecessorIds.Add(predecessorTask.Id);

                if (!string.IsNullOrWhiteSpace(suggestion.Assignee))
                {
                    var resource = EnsureResource(suggestion.Assignee.Trim());
                    task.Resources.Add(new TaskResource
                    {
                        ResourceId = resource.Id,
                        Resource = resource,
                        AllocationPercent = 100,
                        EstimatedHours = task.EstimatedHours
                    });
                }

                Project.Tasks.Add(task);
                createdTasks[task.Name.Trim()] = task;
                previousCreatedTask = task;
                cursor = finish > cursor ? finish : cursor;
                createdCount++;
            }

            if (createdCount > 0)
            {
                Project.IsDirty = true;
                RebuildFlatTasks();
                StatusMessage = $"{createdCount} tarefa(s) geradas com IA e aplicadas ao projeto";
            }

            return createdCount;
        }

        /// <summary>
        /// Aplica um cronograma hierarquico (Epic → Feature → Story [→ Task]) gerado pela IA.
        /// As folhas recebem horas estimadas e sao agendadas por fila de pessoa a partir do
        /// inicio do projeto; os resumos agregam datas dos filhos. Quando <paramref name="untilTask"/>
        /// e false, a Story e a folha (Tasks sao ignoradas/agregadas).
        /// </summary>
        public int ApplyAiSchedule(IEnumerable<AIScheduleNode> roots, bool untilTask, bool markPendingTfs,
            IReadOnlyDictionary<string, ProjectTask>? sourceByKey = null)
        {
            var perAssignee = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var created = 0;
            var newRoots = new List<ProjectTask>();
            // Preservação de âncoras: mapa CHAVE(DisplayId da IA) -> tarefa criada, e a lista
            // de predecessoras (por chave) para ligar depois que tudo estiver criado.
            var builtByKey = new Dictionary<string, ProjectTask>(StringComparer.OrdinalIgnoreCase);
            var predWiring = new List<(ProjectTask Task, List<string> Preds)>();

            foreach (var node in roots)
            {
                var root = BuildScheduleNode(node, null, 0);
                if (root != null)
                {
                    Project.Tasks.Add(root);
                    newRoots.Add(root);
                }
            }

            // Liga as predecessoras pela chave que a IA devolveu (só entre itens desta geração).
            foreach (var (task, preds) in predWiring)
                foreach (var key in preds)
                    if (builtByKey.TryGetValue(key, out var predTask) && predTask.Id != task.Id
                        && !task.PredecessorIds.Contains(predTask.Id))
                        task.PredecessorIds.Add(predTask.Id);

            foreach (var r in newRoots)
                if (r.IsSummary) r.RecalcSummary();

            if (created > 0)
            {
                Project.IsDirty = true;
                RebuildFlatTasks();
                StatusMessage = $"{created} item(ns) de cronograma gerados com IA e aplicados ao projeto";
            }

            return created;

            ProjectTask? BuildScheduleNode(AIScheduleNode node, ProjectTask? parent, int level)
            {
                if (string.IsNullOrWhiteSpace(node.Name)) return null;

                // Item de ORIGEM correspondente (quando a IA devolveu uma chave id=... existente).
                ProjectTask? srcTask = null;
                if (!string.IsNullOrWhiteSpace(node.Id) && sourceByKey != null)
                    sourceByKey.TryGetValue(node.Id.Trim(), out srcTask);

                // Corrige o tipo pelo ID: se o item já existe, usa o tipo ORIGINAL do cronograma
                // em vez de confiar no que a IA devolveu — a IA às vezes troca Story por Task ou
                // erra o nível. Só itens novos usam o tipo/nível da IA.
                var type = NormalizeScheduleType(node.Type, level);
                if (srcTask != null && !string.IsNullOrWhiteSpace(srcTask.TfsType))
                    type = srcTask.TfsType!.Trim();
                var stopAtStory = !untilTask && string.Equals(type, "Story", StringComparison.OrdinalIgnoreCase);
                var isLeaf = node.Children.Count == 0 || stopAtStory;

                var task = new ProjectTask
                {
                    Id = _nextId++,
                    Name = node.Name.Trim(),
                    Level = level,
                    TfsType = type,
                    Notes = string.IsNullOrWhiteSpace(node.Notes) ? null : node.Notes.Trim(),
                    IsPendingTfsCreate = markPendingTfs
                };
                if (parent != null) { task.Parent = parent; parent.Children.Add(task); }

                // Preserva as âncoras que a IA devolveu para itens EXISTENTES (chave id=...:T/:I).
                if (!string.IsNullOrWhiteSpace(node.Id))
                {
                    builtByKey[node.Id.Trim()] = task;
                    if (node.Id.Trim().EndsWith(":T", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(node.Id.Trim()[..^2], out var tfs) && tfs > 0)
                        task.TfsId = tfs;   // mantém o vínculo real com o DevOps
                }
                if (node.PercentComplete > 0) task.PercentComplete = System.Math.Clamp(node.PercentComplete, 0, 100);
                if (node.StartFixed) task.StartFixed = true;
                if (node.FinishFixed) task.FinishFixed = true;

                // Detalhes lidos do TFS que a IA não devolve: preserva do item de origem para
                // não "limpar" o cronograma (bloqueio/tags, estado, descrição/resumo, notas).
                if (srcTask != null)
                {
                    task.TfsState = srcTask.TfsState;
                    task.Tags = srcTask.Tags;                 // inclui a tag "Block" (bloqueado)
                    task.BlockedByChild = srcTask.BlockedByChild;
                    if (!string.IsNullOrWhiteSpace(srcTask.Description)) task.Description = srcTask.Description;
                    if (string.IsNullOrWhiteSpace(task.Notes) && !string.IsNullOrWhiteSpace(srcTask.Notes))
                        task.Notes = srcTask.Notes;           // resumo/observação da task
                }
                if (node.Predecessors.Count > 0) predWiring.Add((task, node.Predecessors));

                if (isLeaf)
                {
                    task.IsSummary = false;
                    var hours = node.EstimatedHours > 0
                        ? node.EstimatedHours
                        : node.DurationDays > 0
                            ? node.DurationDays * ProjectCalendarService.WorkingHoursPerDay
                            : SumLeafHours(node);
                    if (hours <= 0) hours = ProjectCalendarService.WorkingHoursPerDay;
                    task.EstimatedHours = hours;
                    // Preserva HH Atual e HH Original que a IA devolveu (essencial p/ itens 100%,
                    // onde o HH Restante ficou 0 mas o HH Original planejado não pode se perder).
                    if (node.CurrentHours > 0) task.CurrentHours = node.CurrentHours;
                    if (node.OriginalEstimatedHours > 0) task.OriginalEstimatedHours = node.OriginalEstimatedHours;

                    var who = node.Assignee?.Trim() ?? string.Empty;
                    var start = node.StartFixed && node.FixedStart.HasValue
                        ? node.FixedStart.Value
                        : perAssignee.TryGetValue(who, out var c) ? c : Project.StartDate;
                    var finish = node.FinishFixed && node.FixedFinish.HasValue
                        ? node.FixedFinish.Value
                        : ProjectCalendarService.AddWorkingHours(start, hours);
                    task.Start = start;
                    task.Finish = finish;
                    perAssignee[who] = finish;

                    // Sprint: 1º) item EXISTENTE (tem id) e 100% concluído → MANTÉM a sprint original
                    // (é histórica, não faz sentido remexer). 2º) se a IA mandou sprint, respeita.
                    // 3º) senão usa o MESMO algoritmo da bandeirinha (sprint real cuja janela contém
                    // a data de referência do progresso). Sem sprints reais, cálculo sequencial.
                    if (srcTask != null && task.PercentComplete >= 100)
                    {
                        task.SprintNumber = srcTask.SprintNumber;
                        task.TfsIterationPath = srcTask.TfsIterationPath;
                    }
                    else if (node.Sprint > 0)
                    {
                        task.SprintNumber = node.Sprint;
                    }
                    else
                    {
                        var reference = ProjectCalendarService
                            .GetProgressReferenceDate(start, finish, task.PercentComplete).Date;
                        var sprint = ResolveSprintByDate(reference);
                        if (sprint != null)
                        {
                            task.SprintNumber = sprint.Number;
                            task.TfsIterationPath = sprint.Path;   // vincula à sprint real do DevOps
                        }
                        else
                        {
                            task.SprintNumber = ComputeSprintNumber(start);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(who))
                    {
                        var resource = EnsureResource(who);
                        task.Resources.Add(new TaskResource
                        {
                            ResourceId = resource.Id,
                            Resource = resource,
                            AllocationPercent = 100,
                            EstimatedHours = hours
                        });
                    }
                    created++;
                }
                else
                {
                    task.IsSummary = true;
                    foreach (var child in node.Children)
                        BuildScheduleNode(child, task, level + 1);
                    created++;
                }

                return task;
            }
        }

        /// <summary>Sprint (1-based) da data, a partir do inicio do projeto e da duracao de sprint.</summary>
        private int ComputeSprintNumber(DateTime date)
        {
            var days = Math.Max(1, Project.SprintDurationDays);
            var offset = (date.Date - Project.StartDate.Date).TotalDays;
            if (offset < 0) offset = 0;
            return (int)(offset / days) + 1;
        }

        private static double SumLeafHours(AIScheduleNode node)
        {
            if (node.Children.Count == 0)
                return node.EstimatedHours > 0
                    ? node.EstimatedHours
                    : node.DurationDays * ProjectCalendarService.WorkingHoursPerDay;
            double sum = 0;
            foreach (var c in node.Children) sum += SumLeafHours(c);
            return sum;
        }

        private static string NormalizeScheduleType(string? type, int level)
        {
            var t = (type ?? string.Empty).Trim();
            // Codigos de nivel N1..N4
            if (t.Equals("N1", StringComparison.OrdinalIgnoreCase)) return "Epic";
            if (t.Equals("N2", StringComparison.OrdinalIgnoreCase)) return "Feature";
            if (t.Equals("N3", StringComparison.OrdinalIgnoreCase)) return "Story";
            if (t.Equals("N4", StringComparison.OrdinalIgnoreCase)) return "Task";
            // Nomes DevOps
            if (t.Equals("Epic", StringComparison.OrdinalIgnoreCase)) return "Epic";
            if (t.Equals("Feature", StringComparison.OrdinalIgnoreCase)) return "Feature";
            if (t.Equals("Story", StringComparison.OrdinalIgnoreCase) || t.Equals("User Story", StringComparison.OrdinalIgnoreCase)) return "Story";
            if (t.Equals("Task", StringComparison.OrdinalIgnoreCase)) return "Task";
            return level switch { 0 => "Epic", 1 => "Feature", 2 => "Story", _ => "Task" };
        }

        private Resource EnsureResource(string resourceName)
        {
            var existing = Project.Resources.FirstOrDefault(r =>
                string.Equals(r.Name, resourceName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return existing;

            var nextResourceId = Project.Resources.Select(r => r.Id).DefaultIfEmpty(0).Max() + 1;
            var resource = new Resource
            {
                Id = nextResourceId,
                Name = resourceName,
                MaxUnitsPerDay = ProjectCalendarService.WorkingHoursPerDay
            };

            Project.Resources.Add(resource);
            return resource;
        }
    }
}
