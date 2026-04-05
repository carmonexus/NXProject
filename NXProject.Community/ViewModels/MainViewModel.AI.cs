using System;
using System.Collections.Generic;
using System.Linq;
using NXProject.Models;

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
Instrucao para a IA: sempre devolver durationDays e predecessorTaskName para cada atividade.
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

                var start = (predecessorTask?.Finish ?? cursor).Date;
                var durationDays = Math.Max(suggestion.DurationDays, 1);
                var finish = start.AddDays(durationDays);

                var task = new ProjectTask
                {
                    Id = _nextId++,
                    Name = suggestion.Name.Trim(),
                    Start = start,
                    Finish = finish,
                    Notes = string.IsNullOrWhiteSpace(suggestion.Notes) ? null : suggestion.Notes.Trim(),
                    EstimatedHours = durationDays * 8
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
                Name = resourceName
            };

            Project.Resources.Add(resource);
            return resource;
        }
    }
}
