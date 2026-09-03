using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NXProject.Models;

namespace NXProject.Services
{
    /// <summary>
    /// Salva e carrega o Baseline do projeto em um arquivo .nxb (JSON) ao lado do .nxp.
    /// Chave primária: TfsId (quando existir). Fallback: nome da tarefa (para tasks I: que
    /// ainda não foram sincronizadas ao TFS quando o baseline foi salvo).
    /// </summary>
    public static class BaselineService
    {
        private sealed record BaselineEntry(
            int     Id,
            int?    TfsId,
            string  Name,
            string  Start,
            string  Finish,
            double? Hours);

        private sealed record BaselineSnapshot(
            int Id,
            int? TfsId,
            string Name,
            DateTime? Start,
            DateTime? Finish,
            double? Hours);

        private static string GetBaselinePath(string projectFilePath) =>
            Path.ChangeExtension(projectFilePath, ".nxb");

        public static bool HasBaseline(string projectFilePath) =>
            !string.IsNullOrWhiteSpace(projectFilePath) &&
            File.Exists(GetBaselinePath(projectFilePath));

        /// <summary>Salva snapshot de datas, horas e identidade de todas as tarefas.</summary>
        public static void Save(string projectFilePath, IEnumerable<ProjectTask> allTasks)
        {
            var entries = allTasks.Select(t => new BaselineEntry(
                t.Id,
                t.HasTfsLink ? t.TfsId : null,
                t.Name,
                t.Start.ToString("yyyy-MM-dd"),
                t.Finish.ToString("yyyy-MM-dd"),
                t.EstimatedHours)).ToList();

            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetBaselinePath(projectFilePath), json);
        }

        /// <summary>
        /// Carrega o baseline e aplica nos campos Baseline* das tarefas em memória.
        /// Resolução: 1) TfsId  2) Id interno  3) Nome (fallback para tasks I: pós-sync)
        /// </summary>
        public static bool Load(string projectFilePath, IEnumerable<ProjectTask> allTasks, IProgress<(int Completed, int Total)>? progress = null)
        {
            var path = GetBaselinePath(projectFilePath);
            if (!File.Exists(path)) return false;

            try
            {
                var json    = File.ReadAllText(path);
                var entries = JsonSerializer.Deserialize<List<BaselineEntry>>(json);
                if (entries == null) return false;

                var tasks = allTasks.ToList();
                var snapshots = entries.Select(e => new BaselineSnapshot(
                    e.Id,
                    e.TfsId,
                    e.Name,
                    DateTime.TryParse(e.Start, out var s) ? s : null,
                    DateTime.TryParse(e.Finish, out var f) ? f : null,
                    e.Hours)).ToList();

                // Índices para resolução rápida
                var byTfsId  = snapshots.Where(e => e.TfsId.HasValue)
                                      .ToDictionary(e => e.TfsId!.Value);
                var byId     = snapshots.ToDictionary(e => e.Id);
                var byName   = snapshots
                                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                                .Where(g => g.Count() == 1)          // só quando nome é único
                                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var total = tasks.Count;
                var completed = 0;
                var reportEvery = Math.Max(1, total / 100);

                foreach (var t in tasks)
                {
                    BaselineSnapshot? entry = null;

                    // 1) TfsId
                    if (t.HasTfsLink && t.TfsId.HasValue)
                        byTfsId.TryGetValue(t.TfsId.Value, out entry);

                    // 2) Id interno
                    if (entry == null)
                        byId.TryGetValue(t.Id, out entry);

                    // 3) Nome (tasks que eram I: e viraram T: após sync)
                    if (entry == null && !string.IsNullOrWhiteSpace(t.Name))
                        byName.TryGetValue(t.Name, out entry);

                    if (entry != null)
                    {
                        t.BaselineStart  = entry.Start;
                        t.BaselineFinish = entry.Finish;
                        t.BaselineHours  = entry.Hours;
                    }

                    completed++;
                    if (completed == total || completed % reportEvery == 0)
                        progress?.Report((completed, total));
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Remove o arquivo .nxb e limpa os campos Baseline* em memória.</summary>
        public static void Clear(string projectFilePath, IEnumerable<ProjectTask> allTasks)
        {
            var path = GetBaselinePath(projectFilePath);
            if (File.Exists(path)) File.Delete(path);

            foreach (var t in allTasks)
            {
                t.BaselineStart  = null;
                t.BaselineFinish = null;
                t.BaselineHours  = null;
            }
        }
    }
}
