using System;
using System.Collections.Generic;
using System.Linq;
using NXProject.Models;

namespace NXProject.Services
{
    public static class TaskScheduleService
    {
        public static double NormalizeAllocationPercent(double allocationPercent) =>
            double.IsNaN(allocationPercent) || allocationPercent <= 0
                ? 100.0
                : allocationPercent;

        public static double GetAssignmentHours(ProjectTask task, TaskResource assignment)
            => GetAssignmentRemainingHours(task, assignment);

        public static double GetAssignmentRemainingHours(ProjectTask task, TaskResource assignment)
        {
            if (task.PercentComplete >= 100)
                return 0;

            if (assignment.EstimatedHours.HasValue)
                return Math.Max(0, assignment.EstimatedHours.Value);

            if (task.EstimatedHours.HasValue)
            {
                if (task.EstimatedHours.Value <= 0)
                    return 0;

                if (task.Resources.Count <= 1)
                    return task.EstimatedHours.Value;

                var weights = task.Resources
                    .Select(r => new
                    {
                        Assignment = r,
                        Weight = NormalizeAllocationPercent(r.AllocationPercent)
                    })
                    .ToList();
                var totalWeight = weights.Sum(x => x.Weight);
                if (totalWeight > 0)
                {
                    var ownWeight = weights.FirstOrDefault(x => ReferenceEquals(x.Assignment, assignment))?.Weight ?? 0;
                    return task.EstimatedHours.Value * ownWeight / totalWeight;
                }

                return task.EstimatedHours.Value / task.Resources.Count;
            }

            if (task.CurrentHours is > 0)
                return 0;

            var allocationFactor = NormalizeAllocationPercent(assignment.AllocationPercent) / 100.0;
            return Math.Max(0.0, task.DurationHours) * allocationFactor;
        }

        public static double GetAssignmentCurrentHours(ProjectTask task, TaskResource assignment)
        {
            var current = task.CurrentHours ?? 0;
            if (current <= 0)
                return 0;

            if (task.Resources.Count <= 1)
                return current;

            var remainingByAssignment = task.Resources
                .Select(r => new
                {
                    Assignment = r,
                    Hours = Math.Max(0, GetAssignmentRemainingHours(task, r))
                })
                .ToList();
            var totalRemaining = remainingByAssignment.Sum(x => x.Hours);
            if (totalRemaining > 0)
            {
                var ownRemaining = remainingByAssignment.FirstOrDefault(x => ReferenceEquals(x.Assignment, assignment))?.Hours ?? 0;
                return current * ownRemaining / totalRemaining;
            }

            var allocationWeights = task.Resources
                .Select(r => new
                {
                    Assignment = r,
                    Weight = Math.Max(0, NormalizeAllocationPercent(r.AllocationPercent))
                })
                .ToList();
            var totalWeight = allocationWeights.Sum(x => x.Weight);
            if (totalWeight > 0)
            {
                var ownWeight = allocationWeights.FirstOrDefault(x => ReferenceEquals(x.Assignment, assignment))?.Weight ?? 0;
                return current * ownWeight / totalWeight;
            }

            return current / task.Resources.Count;
        }

        public static double GetAssignmentTotalHours(ProjectTask task, TaskResource assignment) =>
            GetAssignmentCurrentHours(task, assignment) + GetAssignmentRemainingHours(task, assignment);

        public static double? GetTaskEstimatedHours(ProjectTask task)
        {
            var assignmentHours = task.Resources
                .Where(r => r.EstimatedHours.HasValue && r.EstimatedHours.Value > 0)
                .Sum(r => r.EstimatedHours!.Value);

            if (assignmentHours > 0)
                return assignmentHours;

            return task.EstimatedHours.HasValue && task.EstimatedHours.Value > 0
                ? task.EstimatedHours.Value
                : null;
        }

        /// <summary>
        /// Fator de disponibilidade geral da pessoa no projeto (0–1).
        /// Padrão 1,0 quando não definido ou inválido.
        /// </summary>
        private static double NormalizeAvailabilityFactor(Resource? resource)
        {
            var pct = resource?.AvailabilityPercent ?? 100.0;
            return (double.IsNaN(pct) || pct <= 0) ? 1.0 : Math.Min(1.0, pct / 100.0);
        }

        public static double GetEffectiveDurationHours(ProjectTask task)
        {
            if (task.IsMilestone)
                return 0.0;

            var durations = new List<double>();
            foreach (var assignment in task.Resources)
            {
                var hours = assignment.EstimatedHours;
                if (!hours.HasValue || hours.Value <= 0)
                    continue;

                // Fator combinado: % alocação na tarefa × % disponibilidade geral
                var allocationFactor    = NormalizeAllocationPercent(assignment.AllocationPercent) / 100.0;
                var availabilityFactor  = NormalizeAvailabilityFactor(assignment.Resource);
                var combined            = Math.Max(0.01, allocationFactor * availabilityFactor);
                durations.Add(hours.Value / combined);
            }

            if (durations.Count > 0)
                return durations.Max();

            var estimatedHours = task.EstimatedHours;
            if (estimatedHours.HasValue && estimatedHours.Value > 0)
            {
                // Sem horas por assignment: distribui pelo somatório de alocação × disponibilidade
                var combinedFactor = task.Resources.Count == 0
                    ? 1.0
                    : task.Resources
                        .Select(r => NormalizeAllocationPercent(r.AllocationPercent) / 100.0
                                     * NormalizeAvailabilityFactor(r.Resource))
                        .DefaultIfEmpty(1.0)
                        .Sum();

                return estimatedHours.Value / Math.Max(0.01, combinedFactor);
            }

            return Math.Max(0.0, task.DurationHours);
        }

        public static double GetEffectiveCurrentDurationHours(ProjectTask task)
        {
            if (task.IsMilestone || task.CurrentHours is not > 0)
                return 0.0;

            var durations = new List<double>();
            foreach (var assignment in task.Resources)
            {
                var currentHours = GetAssignmentCurrentHours(task, assignment);
                if (currentHours <= 0)
                    continue;

                var allocationFactor = NormalizeAllocationPercent(assignment.AllocationPercent) / 100.0;
                var availabilityFactor = NormalizeAvailabilityFactor(assignment.Resource);
                var combined = Math.Max(0.01, allocationFactor * availabilityFactor);
                durations.Add(currentHours / combined);
            }

            if (durations.Count > 0)
                return durations.Max();

            return task.CurrentHours.Value;
        }

        public static void SyncTaskEstimatedHoursFromAssignments(ProjectTask task)
        {
            var total = task.Resources
                .Where(r => r.EstimatedHours.HasValue && r.EstimatedHours.Value > 0)
                .Sum(r => r.EstimatedHours!.Value);

            task.EstimatedHours = total > 0 ? total : task.EstimatedHours;
        }

        public static void RecalculateFinishFromAssignments(ProjectTask task)
        {
            if (task.IsSummary)
                return;

            if (task.PercentComplete >= 100)
                return;

            SyncTaskEstimatedHoursFromAssignments(task);

            var durationHours = GetEffectiveDurationHours(task);
            task.Finish = durationHours <= 0
                ? task.Start
                : ProjectCalendarService.AddWorkingHours(task.Start, durationHours);
        }
    }
}
