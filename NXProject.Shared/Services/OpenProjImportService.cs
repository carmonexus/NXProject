using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using NXProject.Models;

namespace NXProject.Services
{
    /// <summary>
    /// Importa arquivos .pod do OpenProj (ZIP contendo XML interno).
    /// O XML interno usa estrutura plana com OutlineLevel para hierarquia.
    /// </summary>
    public static class OpenProjImportService
    {
        public static Project Import(string podFilePath)
        {
            XDocument doc;
            using (var zip = ZipFile.OpenRead(podFilePath))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".pod", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.FirstOrDefault()
                    ?? throw new Exception("Arquivo .pod não contém XML de projeto válido.");

                using var stream = entry.Open();
                doc = XDocument.Load(stream);
            }

            return ParseDocument(doc, podFilePath);
        }

        private static Project ParseDocument(XDocument doc, string sourceFilePath)
        {
            var root = doc.Root ?? throw new Exception("XML inválido no arquivo .pod");

            // OpenProj pode ter ou não namespace
            XNamespace ns = root.Name.Namespace;

            var project = new Project
            {
                Name = root.Element(ns + "Name")?.Value
                    ?? Path.GetFileNameWithoutExtension(sourceFilePath),
                Author = root.Element(ns + "Manager")?.Value
                    ?? root.Element(ns + "Author")?.Value,
                StartDate = ParseDate(root.Element(ns + "StartDate")?.Value
                    ?? root.Element(ns + "Start")?.Value) ?? DateTime.Today,
                FilePath = null  // importado, não tem caminho NXProject ainda
            };

            // Recursos
            var resourceMap = new Dictionary<int, Resource>();
            var resourcesEl = root.Element(ns + "Resources");
            if (resourcesEl != null)
            {
                int autoId = 1;
                foreach (var r in resourcesEl.Elements(ns + "Resource"))
                {
                    int uid = ParseInt(r.Element(ns + "UniqueID")?.Value
                        ?? r.Element(ns + "ID")?.Value) ?? autoId++;
                    var res = new Resource
                    {
                        Id = uid,
                        Name = r.Element(ns + "Name")?.Value ?? $"Recurso {uid}",
                        Type = ResourceType.Work,
                        MaxUnitsPerDay = ParseDouble(r.Element(ns + "MaxUnits")?.Value) * 8 ?? 8
                    };
                    project.Resources.Add(res);
                    resourceMap[uid] = res;
                }
            }

            // Alocações (ResourceAssignments) — indexado por TaskUID
            var assignmentsByTask = new Dictionary<int, List<(int resId, double units)>>();
            var assignmentsEl = root.Element(ns + "ResourceAssignments");
            if (assignmentsEl != null)
            {
                foreach (var a in assignmentsEl.Elements(ns + "ResourceAssignment"))
                {
                    int taskUid = ParseInt(a.Element(ns + "TaskUniqueID")?.Value
                        ?? a.Element(ns + "TaskID")?.Value) ?? -1;
                    int resUid = ParseInt(a.Element(ns + "ResourceUniqueID")?.Value
                        ?? a.Element(ns + "ResourceID")?.Value) ?? -1;
                    double units = ParseDouble(a.Element(ns + "Units")?.Value) ?? 1.0;
                    if (taskUid >= 0 && resUid >= 0)
                    {
                        if (!assignmentsByTask.ContainsKey(taskUid))
                            assignmentsByTask[taskUid] = new();
                        assignmentsByTask[taskUid].Add((resUid, units));
                    }
                }
            }

            // Tarefas (lista plana → hierarquia por OutlineLevel)
            var tasksEl = root.Element(ns + "Tasks");
            if (tasksEl != null)
            {
                var flatTasks = new List<ProjectTask>();
                int autoTaskId = 1;

                foreach (var t in tasksEl.Elements(ns + "Task"))
                {
                    int uid = ParseInt(t.Element(ns + "UniqueID")?.Value
                        ?? t.Element(ns + "ID")?.Value) ?? autoTaskId++;
                    int outlineLevel = ParseInt(t.Element(ns + "OutlineLevel")?.Value) ?? 1;

                    var durationStr = t.Element(ns + "Duration")?.Value;
                    var start = ParseDate(t.Element(ns + "Start")?.Value) ?? project.StartDate;
                    var finish = ParseDate(t.Element(ns + "Finish")?.Value) ?? start.AddDays(1);

                    // Duração em ISO 8601 (PT40H0M0S) ou em dias
                    if (durationStr != null && finish == start.AddDays(1))
                    {
                        var dur = ParseIsoDuration(durationStr);
                        if (dur.HasValue) finish = start.Add(dur.Value);
                    }

                    bool isSummary = ParseBool(t.Element(ns + "Summary")?.Value);
                    bool isMilestone = ParseBool(t.Element(ns + "Milestone")?.Value);

                    var task = new ProjectTask
                    {
                        Id = uid,
                        Name = t.Element(ns + "Name")?.Value ?? $"Tarefa {uid}",
                        Level = outlineLevel - 1,
                        Start = start,
                        Finish = finish,
                        IsSummary = isSummary,
                        IsMilestone = isMilestone,
                        PercentComplete = ParseDouble(
                            t.Element(ns + "PercentageComplete")?.Value
                            ?? t.Element(ns + "PercentComplete")?.Value) ?? 0,
                        Notes = t.Element(ns + "Notes")?.Value
                    };

                    // Predecessoras
                    var predsEl = t.Element(ns + "PredecessorTasks");
                    if (predsEl != null)
                        foreach (var p in predsEl.Elements(ns + "PredecessorTask"))
                            if (ParseInt(p.Element(ns + "UniqueID")?.Value) is int predId)
                                task.PredecessorIds.Add(predId);

                    // Alocações
                    if (assignmentsByTask.TryGetValue(uid, out var assignments))
                        foreach (var (resId, units) in assignments)
                            task.Resources.Add(new TaskResource
                            {
                                ResourceId = resId,
                                Resource = resourceMap.GetValueOrDefault(resId),
                                AllocationPercent = units * 100,
                            });

                    flatTasks.Add(task);
                }

                NormalizeTaskIds(flatTasks);
                BuildHierarchy(project.Tasks, flatTasks);
            }

            // Reconectar recursos nas tarefas
            foreach (var task in AllTasks(project.Tasks))
                foreach (var tr in task.Resources)
                    if (tr.Resource == null && resourceMap.TryGetValue(tr.ResourceId, out var res))
                        tr.Resource = res;

            return project;
        }

        /// <summary>
        /// Reconstrói hierarquia a partir de lista plana usando Level (OutlineLevel - 1).
        /// </summary>
        private static void BuildHierarchy(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> root,
            List<ProjectTask> flat)
        {
            var stack = new Stack<ProjectTask>();

            foreach (var task in flat)
            {
                // Remove itens da pilha que são do mesmo nível ou mais profundos
                while (stack.Count > 0 && stack.Peek().Level >= task.Level)
                    stack.Pop();

                if (stack.Count == 0)
                {
                    task.Parent = null;
                    root.Add(task);
                }
                else
                {
                    task.Parent = stack.Peek();
                    stack.Peek().Children.Add(task);
                }

                stack.Push(task);
            }
        }

        private static void NormalizeTaskIds(List<ProjectTask> flat)
        {
            var idMap = new Dictionary<int, int>(flat.Count);
            for (int i = 0; i < flat.Count; i++)
                idMap[flat[i].Id] = i + 1;

            foreach (var task in flat)
            {
                task.Id = idMap[task.Id];
                for (int i = 0; i < task.PredecessorIds.Count; i++)
                {
                    if (idMap.TryGetValue(task.PredecessorIds[i], out var newId))
                        task.PredecessorIds[i] = newId;
                }
            }
        }

        private static IEnumerable<ProjectTask> AllTasks(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                yield return t;
                foreach (var c in AllTasks(t.Children))
                    yield return c;
            }
        }

        // ── Helpers de parse ─────────────────────────────────────────────────

        private static DateTime? ParseDate(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return DateTime.TryParse(value, out var dt) ? dt : null;
        }

        private static int? ParseInt(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return int.TryParse(value, out var v) ? v : null;
        }

        private static double? ParseDouble(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return double.TryParse(value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v) ? v : null;
        }

        private static bool ParseBool(string? value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (bool.TryParse(value, out var b)) return b;
            return value.Trim() == "1";
        }

        /// <summary>Converte ISO 8601 duration (PT40H0M0S, P5D, etc.) para TimeSpan.</summary>
        private static TimeSpan? ParseIsoDuration(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try { return System.Xml.XmlConvert.ToTimeSpan(value); }
            catch { return null; }
        }
    }
}
