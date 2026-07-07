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
        public int ApplyAiSchedule(IEnumerable<AIScheduleNode> roots, bool untilTask, bool markPendingTfs)
        {
            var perAssignee = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var created = 0;
            var newRoots = new List<ProjectTask>();

            foreach (var node in roots)
            {
                var root = BuildScheduleNode(node, null, 0);
                if (root != null)
                {
                    Project.Tasks.Add(root);
                    newRoots.Add(root);
                }
            }

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

                var type = NormalizeScheduleType(node.Type, level);
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

                    var who = node.Assignee?.Trim() ?? string.Empty;
                    var start = perAssignee.TryGetValue(who, out var c) ? c : Project.StartDate;
                    var finish = ProjectCalendarService.AddWorkingHours(start, hours);
                    task.Start = start;
                    task.Finish = finish;
                    perAssignee[who] = finish;

                    // Numero da sprint: usa o do JSON, senao calcula pela data de inicio.
                    task.SprintNumber = node.Sprint > 0 ? node.Sprint : ComputeSprintNumber(start);

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
