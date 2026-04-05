using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NXProject.Models;

namespace NXProject.Services
{
    /// <summary>
    /// Importa arquivos no formato MSPDI real exportado pelo Microsoft Project (.xml).
    /// O MSPDI usa lista plana de tarefas com OutlineLevel para hierarquia e
    /// assignments separados das tarefas.
    /// </summary>
    public static class MspdiImportService
    {
        private static readonly XNamespace NS = "http://schemas.microsoft.com/project";

        public static Project Import(string filePath)
        {
            var doc = XDocument.Load(filePath);
            var root = doc.Root ?? throw new Exception("XML invalido");

            XNamespace ns = root.Name.Namespace == XNamespace.None ? XNamespace.None : NS;

            var project = new Project
            {
                Name = root.Element(ns + "Name")?.Value
                    ?? Path.GetFileNameWithoutExtension(filePath),
                Author = root.Element(ns + "Manager")?.Value
                    ?? root.Element(ns + "Author")?.Value,
                StartDate = ParseDate(root.Element(ns + "StartDate")?.Value
                    ?? root.Element(ns + "CreationDate")?.Value) ?? DateTime.Today,
                FilePath = null
            };

            var resourceMap = new Dictionary<int, Resource>();
            var resourcesEl = root.Element(ns + "Resources");
            if (resourcesEl != null)
            {
                foreach (var r in resourcesEl.Elements(ns + "Resource"))
                {
                    int uid = ParseInt(r.Element(ns + "UID")?.Value) ?? 0;
                    if (uid == 0)
                        continue;

                    var res = new Resource
                    {
                        Id = uid,
                        Name = r.Element(ns + "Name")?.Value ?? $"Recurso {uid}",
                        Type = ParseResourceType(r.Element(ns + "Type")?.Value),
                        MaxUnitsPerDay = ParseDouble(r.Element(ns + "MaxUnits")?.Value) * 8 ?? 8,
                        CostPerHour = ParseDecimal(r.Element(ns + "StandardRate")?.Value) ?? 0,
                        Email = r.Element(ns + "EmailAddress")?.Value,
                        Notes = r.Element(ns + "Notes")?.Value
                    };
                    project.Resources.Add(res);
                    resourceMap[uid] = res;
                }
            }

            var assignmentsByTask = new Dictionary<int, List<(int resId, double units, double? hours)>>();
            var assignmentsEl = root.Element(ns + "Assignments");
            if (assignmentsEl != null)
            {
                foreach (var a in assignmentsEl.Elements(ns + "Assignment"))
                {
                    int taskUid = ParseInt(a.Element(ns + "TaskUID")?.Value) ?? -1;
                    int resUid = ParseInt(a.Element(ns + "ResourceUID")?.Value) ?? -1;
                    double units = ParseDouble(a.Element(ns + "Units")?.Value) ?? 1.0;
                    double? hours = ParseDurationHours(a.Element(ns + "Work")?.Value);

                    if (taskUid > 0 && resUid > 0)
                    {
                        if (!assignmentsByTask.ContainsKey(taskUid))
                            assignmentsByTask[taskUid] = new();
                        assignmentsByTask[taskUid].Add((resUid, units, hours));
                    }
                }
            }

            var tasksEl = root.Element(ns + "Tasks");
            if (tasksEl != null)
            {
                var flatTasks = new List<ProjectTask>();

                foreach (var t in tasksEl.Elements(ns + "Task"))
                {
                    int uid = ParseInt(t.Element(ns + "UID")?.Value) ?? 0;
                    if (uid == 0)
                        continue;

                    int outlineLevel = ParseInt(t.Element(ns + "OutlineLevel")?.Value) ?? 1;
                    bool isSummary = t.Element(ns + "Summary")?.Value == "1"
                        || string.Equals(t.Element(ns + "Summary")?.Value, "true", StringComparison.OrdinalIgnoreCase);
                    bool isMilestone = t.Element(ns + "Milestone")?.Value == "1"
                        || string.Equals(t.Element(ns + "Milestone")?.Value, "true", StringComparison.OrdinalIgnoreCase);

                    var start = ParseDate(t.Element(ns + "Start")?.Value) ?? project.StartDate;
                    var finish = ParseDate(t.Element(ns + "Finish")?.Value) ?? start.AddDays(1);

                    var task = new ProjectTask
                    {
                        Id = uid,
                        Name = t.Element(ns + "Name")?.Value ?? $"Tarefa {uid}",
                        Level = outlineLevel - 1,
                        Start = start,
                        Finish = finish,
                        IsSummary = isSummary,
                        IsMilestone = isMilestone,
                        PercentComplete = ParseDouble(t.Element(ns + "PercentComplete")?.Value) ?? 0,
                        Notes = t.Element(ns + "Notes")?.Value,
                        EstimatedHours = ParseDurationHours(t.Element(ns + "Work")?.Value)
                    };

                    foreach (var pred in t.Elements(ns + "PredecessorLink"))
                    {
                        if (ParseInt(pred.Element(ns + "PredecessorUID")?.Value) is int predId && predId > 0)
                            task.PredecessorIds.Add(predId);
                    }

                    if (assignmentsByTask.TryGetValue(uid, out var assignments))
                    {
                        foreach (var (resId, units, hours) in assignments)
                        {
                            task.Resources.Add(new TaskResource
                            {
                                ResourceId = resId,
                                Resource = resourceMap.GetValueOrDefault(resId),
                                AllocationPercent = units * 100,
                                EstimatedHours = hours
                            });
                        }
                    }

                    flatTasks.Add(task);
                }

                NormalizeTaskIds(flatTasks);
                BuildHierarchy(project.Tasks, flatTasks);
            }

            return project;
        }

        private static void BuildHierarchy(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> root,
            List<ProjectTask> flat)
        {
            var stack = new Stack<ProjectTask>();
            foreach (var task in flat)
            {
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

        private static DateTime? ParseDate(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            return DateTime.TryParse(value, out var dt) ? dt : null;
        }

        private static int? ParseInt(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            return int.TryParse(value, out var v) ? v : null;
        }

        private static double? ParseDouble(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            return double.TryParse(
                value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v) ? v : null;
        }

        private static decimal? ParseDecimal(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            return decimal.TryParse(
                value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v) ? v : null;
        }

        private static ResourceType ParseResourceType(string? value) =>
            value switch
            {
                "1" => ResourceType.Material,
                "2" => ResourceType.Cost,
                _ => ResourceType.Work
            };

        private static double? ParseDurationHours(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            try
            {
                var ts = System.Xml.XmlConvert.ToTimeSpan(value);
                return ts.TotalHours > 0 ? ts.TotalHours : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
