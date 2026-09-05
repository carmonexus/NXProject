// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.Linq;
using NXProject.Models;

namespace NXProject.Services
{
    public sealed record CriticalPathEntry(
        ProjectTask Task,
        DateTime    ES,     // Early Start
        DateTime    EF,     // Early Finish
        DateTime    LS,     // Late Start
        DateTime    LF,     // Late Finish
        double      TotalFloat);  // working days of slack

    public static class CriticalPathService
    {
        /// <summary>
        /// Computes CPM on leaf tasks (non-summary) using Finish-to-Start logic.
        /// Returns all entries sorted by ES; critical tasks have TotalFloat == 0.
        /// </summary>
        public static List<CriticalPathEntry> Compute(IEnumerable<ProjectTask> allTasks)
        {
            var tasks = allTasks
                .Where(t => !t.IsSummary && t.Start < t.Finish)
                .ToList();

            if (tasks.Count == 0) return new();

            var byId = tasks.ToDictionary(t => t.Id);

            // ── Forward pass ────────────────────────────────────────────────
            var es = new Dictionary<int, DateTime>();
            var ef = new Dictionary<int, DateTime>();

            // Topological sort via predecessor dependency
            var sorted = TopologicalSort(tasks, byId);

            foreach (var t in sorted)
            {
                DateTime start = t.Start; // anchored start if no preds
                foreach (var predId in t.PredecessorIds)
                {
                    if (!ef.TryGetValue(predId, out var predEf)) continue;
                    if (predEf > start) start = predEf;
                }
                es[t.Id] = start;
                ef[t.Id] = start + (t.Finish - t.Start);
            }

            DateTime projectEnd = ef.Values.Max();

            // ── Backward pass ────────────────────────────────────────────────
            var ls = new Dictionary<int, DateTime>();
            var lf = new Dictionary<int, DateTime>();

            // Build successor map
            var successors = tasks.ToDictionary(t => t.Id, _ => new List<int>());
            foreach (var t in tasks)
                foreach (var predId in t.PredecessorIds)
                    if (successors.ContainsKey(predId))
                        successors[predId].Add(t.Id);

            foreach (var t in sorted.AsEnumerable().Reverse())
            {
                DateTime finish = projectEnd;
                foreach (var succId in successors[t.Id])
                {
                    if (!ls.TryGetValue(succId, out var succLs)) continue;
                    if (succLs < finish) finish = succLs;
                }
                lf[t.Id] = finish;
                ls[t.Id] = finish - (t.Finish - t.Start);
            }

            // ── Compute float ────────────────────────────────────────────────
            var result = new List<CriticalPathEntry>();
            foreach (var t in sorted)
            {
                double floatDays = (ls[t.Id] - es[t.Id]).TotalDays;
                floatDays = Math.Round(Math.Max(0, floatDays), 1);
                result.Add(new CriticalPathEntry(t, es[t.Id], ef[t.Id], ls[t.Id], lf[t.Id], floatDays));
            }

            return result.OrderBy(e => e.ES).ToList();
        }

        private static List<ProjectTask> TopologicalSort(
            List<ProjectTask> tasks, Dictionary<int, ProjectTask> byId)
        {
            var visited = new HashSet<int>();
            var sorted  = new List<ProjectTask>();

            void Visit(ProjectTask t)
            {
                if (!visited.Add(t.Id)) return;
                foreach (var predId in t.PredecessorIds)
                    if (byId.TryGetValue(predId, out var pred))
                        Visit(pred);
                sorted.Add(t);
            }

            foreach (var t in tasks) Visit(t);
            return sorted;
        }
    }
}
