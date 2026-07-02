using System;
using System.Collections.Generic;
using System.Linq;
using NXProject.Models;

namespace NXProject.Services
{
    public sealed record ResourceCostLine(
        string   EpicName,
        string   FeatureName,
        string   StoryName,
        string   ResourceName,
        ResourceCostType CostType,
        bool     IsCapex,
        int      Year,
        int      Month,
        double   Hours,
        decimal  Cost);

    public static class ResourceCostService
    {
        /// <summary>
        /// Calcula o custo por recurso, agrupado por Feature (nível 1 acima da task) e mês.
        /// </summary>
        public static List<ResourceCostLine> Compute(
            IEnumerable<ProjectTask> allTasks,
            IEnumerable<Resource>    resources)
        {
            var resById = resources.ToDictionary(r => r.Id);
            var lines   = new List<ResourceCostLine>();

            // Coleta todas as tasks folha com recursos
            var leaves = allTasks
                .Where(t => !t.IsSummary && t.Resources.Count > 0 && t.Start < t.Finish)
                .ToList();

            // Para recursos mensais: total de HH do recurso em TODO o projeto = 1 salário
            var totalHoursByResource = new Dictionary<int, double>();
            foreach (var task in leaves)
            {
                foreach (var tr in task.Resources)
                {
                    if (!resById.TryGetValue(tr.ResourceId, out var res)) continue;
                    if (res.CostType != ResourceCostType.Monthly) continue;
                    double h = TaskScheduleService.GetAssignmentTotalHours(task, tr);
                    totalHoursByResource.TryGetValue(tr.ResourceId, out var prev);
                    totalHoursByResource[tr.ResourceId] = prev + h;
                }
            }

            foreach (var task in leaves)
            {
                var epicName    = FindEpicName(task);
                var featureName = FindFeatureName(task);
                bool isCapex    = FindEpicAncestor(task)?.TipoCentroCusto
                                    ?.Equals("CAPEX", StringComparison.OrdinalIgnoreCase) == true;

                string storyName = task.Name ?? "";

                foreach (var tr in task.Resources)
                {
                    if (!resById.TryGetValue(tr.ResourceId, out var res)) continue;
                    if (res.Kind == ResourceKind.Internal) continue;
                    double currentHours = TaskScheduleService.GetAssignmentCurrentHours(task, tr);
                    double remainingHours = TaskScheduleService.GetAssignmentRemainingHours(task, tr);
                    double assignmentHours = currentHours + remainingHours;
                    if (assignmentHours <= 0) continue;

                    if (res.CostType == ResourceCostType.Hourly)
                    {
                        decimal costPerHour = res.CostPerHour > 0 ? res.CostPerHour : 0;
                        if (costPerHour == 0) continue;

                        foreach (var (year, month, h) in SplitAssignmentHoursByMonth(task, currentHours, remainingHours))
                        {
                            lines.Add(new ResourceCostLine(
                                epicName, featureName, storyName, res.Name, ResourceCostType.Hourly, isCapex,
                                year, month, h, (decimal)h * costPerHour));
                        }
                    }
                    else // Monthly
                    {
                        if (res.MonthlyRate <= 0) continue;
                        if (!totalHoursByResource.TryGetValue(tr.ResourceId, out var totalH) || totalH <= 0) continue;

                        decimal taskCost = res.MonthlyRate * (decimal)(assignmentHours / totalH);

                        foreach (var (year, month, h) in SplitAssignmentHoursByMonth(task, currentHours, remainingHours))
                        {
                            var hourFrac = assignmentHours > 0 ? h / assignmentHours : 0;
                            lines.Add(new ResourceCostLine(
                                epicName, featureName, storyName, res.Name, ResourceCostType.Monthly, isCapex,
                                year, month, h, taskCost * (decimal)hourFrac));
                        }
                    }
                }
            }

            return lines
                .OrderBy(l => l.Year).ThenBy(l => l.Month)
                .ThenBy(l => l.FeatureName).ThenBy(l => l.ResourceName)
                .ToList();
        }

        private static IEnumerable<(int year, int month, double hours)> SplitAssignmentHoursByMonth(
            ProjectTask task, double currentHours, double remainingHours)
        {
            var start = task.Start.Date;
            var finish = task.Finish.Date;
            if (finish < start) yield break;

            var byMonth = new Dictionary<(int Year, int Month), double>();

            void Add(DateTime periodStart, DateTime periodEnd, double hours)
            {
                if (hours <= 0 || periodEnd < periodStart) return;

                var cur = new DateTime(periodStart.Year, periodStart.Month, 1);
                while (cur <= periodEnd)
                {
                    var monthStart = cur;
                    var monthEnd = cur.AddMonths(1).AddDays(-1);
                    var h = AllocateHoursInPeriod(hours, periodStart, periodEnd, monthStart, monthEnd);
                    if (h > 0)
                    {
                        var key = (cur.Year, cur.Month);
                        byMonth.TryGetValue(key, out var prev);
                        byMonth[key] = prev + h;
                    }
                    cur = cur.AddMonths(1);
                }
            }

            var currentEnd = GetCurrentHoursEnd(task);
            Add(start, currentEnd, currentHours);

            var remainingStart = currentHours > 0 && task.PercentComplete < 100 && currentEnd > start
                ? currentEnd.AddDays(1)
                : start;
            if (remainingStart <= finish)
                Add(remainingStart, finish, remainingHours);

            foreach (var item in byMonth.OrderBy(x => x.Key.Year).ThenBy(x => x.Key.Month))
                yield return (item.Key.Year, item.Key.Month, item.Value);
        }

        private static double AllocateHoursInPeriod(double hours, DateTime start, DateTime end, DateTime monthStart, DateTime monthEnd)
        {
            if (hours <= 0) return 0;
            var tStart = start.Date;
            var tEnd = end.Date;
            if (tEnd < monthStart || tStart > monthEnd) return 0;

            if (tStart >= tEnd)
                return monthStart <= tStart && tStart <= monthEnd ? hours : 0;

            var overlapStart = tStart < monthStart ? monthStart : tStart;
            var overlapEnd = tEnd > monthEnd ? monthEnd : tEnd;
            double overlapDays = Math.Max(0, (overlapEnd - overlapStart).TotalDays + 1);
            double totalDays = Math.Max(1, (tEnd - tStart).TotalDays + 1);
            return hours * (overlapDays / totalDays);
        }

        private static DateTime GetCurrentHoursEnd(ProjectTask task)
        {
            if (task.PercentComplete >= 100)
                return task.Finish.Date;

            var today = DateTime.Today;
            if (today < task.Start.Date)
                return task.Start.Date;
            if (today > task.Finish.Date)
                return task.Finish.Date;
            return today;
        }

        private static string FindFeatureName(ProjectTask task)
        {
            var p = task.Parent;
            while (p?.Parent?.Parent != null) p = p.Parent;
            return p?.Name ?? task.Name;
        }

        private static string FindEpicName(ProjectTask task)
            => FindEpicAncestor(task)?.Name ?? "";

        private static ProjectTask? FindEpicAncestor(ProjectTask task)
        {
            var cur = task.Parent;
            while (cur != null)
            {
                if (string.Equals(cur.TfsType, "Epic", StringComparison.OrdinalIgnoreCase))
                    return cur;
                cur = cur.Parent;
            }
            return null;
        }
    }
}
