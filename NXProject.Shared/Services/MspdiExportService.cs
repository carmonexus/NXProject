using System;
using System.Collections.Generic;
using System.Xml.Linq;
using NXProject.Models;

namespace NXProject.Services
{
    /// <summary>
    /// Exporta projeto no formato MSPDI real do Microsoft Project (.xml).
    /// Lista plana de tarefas com OutlineLevel e assignments separados.
    /// </summary>
    public static class MspdiExportService
    {
        private static readonly XNamespace NS = "http://schemas.microsoft.com/project";

        public static void Export(Project project, string filePath)
        {
            var doc = BuildDocument(project);
            doc.Save(filePath);
        }

        private static XDocument BuildDocument(Project project)
        {
            var flatTasks = new List<ProjectTask>();
            FlattenTasks(project.Tasks, flatTasks);

            var tasksEl = new XElement(NS + "Tasks",
                new XElement(NS + "Task",
                    new XElement(NS + "UID", 0),
                    new XElement(NS + "ID", 0),
                    new XElement(NS + "Name", project.Name),
                    new XElement(NS + "Start", project.StartDate.ToString("yyyy-MM-ddTHH:mm:ss")),
                    new XElement(NS + "Finish", project.StartDate.ToString("yyyy-MM-ddTHH:mm:ss")),
                    new XElement(NS + "Summary", 1),
                    new XElement(NS + "OutlineLevel", 0)
                )
            );

            for (int index = 0; index < flatTasks.Count; index++)
            {
                var t = flatTasks[index];
                var lineId = index + 1;
                var el = new XElement(NS + "Task",
                    new XElement(NS + "UID", t.Id),
                    new XElement(NS + "ID", lineId),
                    new XElement(NS + "Name", t.Name),
                    new XElement(NS + "OutlineLevel", t.Level + 1),
                    new XElement(NS + "Summary", t.IsSummary ? 1 : 0),
                    new XElement(NS + "Milestone", t.IsMilestone ? 1 : 0),
                    new XElement(NS + "Start", t.Start.ToString("yyyy-MM-ddTHH:mm:ss")),
                    new XElement(NS + "Finish", t.Finish.ToString("yyyy-MM-ddTHH:mm:ss")),
                    new XElement(NS + "Duration", ToDuration(t.Finish - t.Start)),
                    new XElement(NS + "PercentComplete", (int)t.PercentComplete),
                    new XElement(NS + "Notes", t.Notes ?? "")
                );

                if (t.EstimatedHours.HasValue)
                    el.Add(new XElement(NS + "Work", ToDuration(TimeSpan.FromHours(t.EstimatedHours.Value))));

                foreach (var pid in t.PredecessorIds)
                {
                    el.Add(new XElement(NS + "PredecessorLink",
                        new XElement(NS + "PredecessorUID", pid),
                        new XElement(NS + "Type", 1),
                        new XElement(NS + "LinkLag", 0)));
                }

                tasksEl.Add(el);
            }

            var resourcesEl = new XElement(NS + "Resources");
            foreach (var r in project.Resources)
            {
                var rel = new XElement(NS + "Resource",
                    new XElement(NS + "UID", r.Id),
                    new XElement(NS + "ID", r.Id),
                    new XElement(NS + "Name", r.Name),
                    new XElement(NS + "Type", (int)r.Type),
                    new XElement(NS + "MaxUnits", r.MaxUnitsPerDay / 8.0),
                    new XElement(NS + "StandardRate", r.CostPerHour),
                    new XElement(NS + "EmailAddress", r.Email ?? ""),
                    new XElement(NS + "Notes", r.Notes ?? ""));
                resourcesEl.Add(rel);
            }

            var assignmentsEl = new XElement(NS + "Assignments");
            int assignUid = 1;
            foreach (var t in flatTasks)
            {
                foreach (var tr in t.Resources)
                {
                    var ael = new XElement(NS + "Assignment",
                        new XElement(NS + "UID", assignUid++),
                        new XElement(NS + "TaskUID", t.Id),
                        new XElement(NS + "ResourceUID", tr.ResourceId),
                        new XElement(NS + "Units", tr.AllocationPercent / 100.0));

                    if (tr.EstimatedHours.HasValue)
                    {
                        ael.Add(new XElement(NS + "Work",
                            ToDuration(TimeSpan.FromHours(tr.EstimatedHours.Value))));
                    }

                    assignmentsEl.Add(ael);
                }
            }

            return new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(NS + "Project",
                    new XElement(NS + "Name", project.Name),
                    new XElement(NS + "Manager", project.Author ?? ""),
                    new XElement(NS + "StartDate", project.StartDate.ToString("yyyy-MM-ddTHH:mm:ss")),
                    new XElement(NS + "CreationDate", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")),
                    tasksEl,
                    resourcesEl,
                    assignmentsEl
                )
            );
        }

        private static void FlattenTasks(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> tasks,
            List<ProjectTask> result)
        {
            foreach (var t in tasks)
            {
                result.Add(t);
                FlattenTasks(t.Children, result);
            }
        }

        private static string ToDuration(TimeSpan ts) =>
            System.Xml.XmlConvert.ToString(ts);
    }
}
