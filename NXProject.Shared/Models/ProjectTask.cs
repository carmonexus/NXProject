using System;
using System.Collections.ObjectModel;

namespace NXProject.Models
{
    public class ProjectTask
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Level { get; set; } = 0;
        public bool IsSummary { get; set; } = false;
        public bool IsMilestone { get; set; } = false;

        public DateTime Start { get; set; } = DateTime.Today;
        public DateTime Finish { get; set; } = DateTime.Today.AddDays(1);
        public TimeSpan Duration => Finish - Start;

        public double PercentComplete { get; set; } = 0;

        public string? Notes { get; set; }

        public double? EstimatedHours { get; set; }

        // Recursos alocados nesta tarefa
        public List<TaskResource> Resources { get; set; } = new();

        // Predecessoras: lista de IDs de tarefas
        public List<int> PredecessorIds { get; set; } = new();

        // Subtarefas
        public ObservableCollection<ProjectTask> Children { get; set; } = new();

        // Referência ao pai
        public ProjectTask? Parent { get; set; }

        public int SprintNumber { get; set; } = 0;

        // Recalcula datas de tarefas de resumo com base nos filhos
        public void RecalcSummary()
        {
            if (!IsSummary || Children.Count == 0) return;

            var minStart = DateTime.MaxValue;
            var maxFinish = DateTime.MinValue;

            foreach (var child in Children)
            {
                child.RecalcSummary();
                if (child.Start < minStart) minStart = child.Start;
                if (child.Finish > maxFinish) maxFinish = child.Finish;
            }

            Start = minStart;
            Finish = maxFinish;
        }

        public override string ToString() => $"{Id} - {Name}";
    }

    public enum DependencyType
    {
        FinishToStart,
        StartToStart,
        FinishToFinish,
        StartToFinish
    }

    public class TaskDependency
    {
        public int PredecessorId { get; set; }
        public int SuccessorId { get; set; }
        public DependencyType Type { get; set; } = DependencyType.FinishToStart;
        public int LagDays { get; set; } = 0;
    }
}
