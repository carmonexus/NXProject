using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using NXProject.Models;

namespace NXProject.Services
{
    /// <summary>
    /// Exporta projeto para o formato .pod do OpenProj (ZIP com XML interno).
    /// Usa lista plana de tarefas com OutlineLevel para hierarquia.
    /// </summary>
    public static class OpenProjExportService
    {
        public static void Export(Project project, string filePath)
        {
            var doc = BuildDocument(project);

            // Escreve o XML num stream de memória e empacota no ZIP
            using var ms = new MemoryStream();
            doc.Save(ms);
            ms.Position = 0;

            if (File.Exists(filePath)) File.Delete(filePath);

            using var zip = ZipFile.Open(filePath, ZipArchiveMode.Create);
            var entryName = Path.GetFileNameWithoutExtension(filePath) + ".pod";
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            ms.CopyTo(entryStream);
        }

        private static XDocument BuildDocument(Project project)
        {
            var flatTasks = new List<ProjectTask>();
            FlattenTasks(project.Tasks, flatTasks);

            var tasksEl = new XElement("Tasks");
            for (int index = 0; index < flatTasks.Count; index++)
            {
                var t = flatTasks[index];
                var lineId = index + 1;
                var el = new XElement("Task",
                    new XElement("UniqueID", t.Id),
                    // OpenProj usa ID como número visível da linha; o identificador
                    // estável da tarefa deve permanecer em UniqueID.
                    new XElement("ID", lineId),
                    new XElement("Name", t.Name),
                    new XElement("OutlineLevel", t.Level + 1),
                    new XElement("Summary", t.IsSummary),
                    new XElement("Milestone", t.IsMilestone),
                    new XElement("Start", t.Start.ToString("yyyy-MM-ddTHH:mm:ss")),
                    new XElement("Finish", t.Finish.ToString("yyyy-MM-ddTHH:mm:ss")),
                    new XElement("Duration", ToDuration(t.Finish - t.Start)),
                    new XElement("PercentageComplete", t.PercentComplete),
                    new XElement("Notes", t.Notes ?? "")
                );

                if (t.PredecessorIds.Count > 0)
                {
                    var predsEl = new XElement("PredecessorTasks");
                    foreach (var pid in t.PredecessorIds)
                        predsEl.Add(new XElement("PredecessorTask",
                            new XElement("UniqueID", pid),
                            new XElement("Type", 1),
                            new XElement("Lag", "PT0H0M0S")));
                    el.Add(predsEl);
                }

                tasksEl.Add(el);
            }

            var resourcesEl = new XElement("Resources");
            foreach (var r in project.Resources)
                resourcesEl.Add(new XElement("Resource",
                    new XElement("UniqueID", r.Id),
                    new XElement("ID", r.Id),
                    new XElement("Name", r.Name),
                    new XElement("MaxUnits", r.MaxUnitsPerDay / 8.0)));

            var assignmentsEl = new XElement("ResourceAssignments");
            foreach (var t in flatTasks)
                foreach (var tr in t.Resources)
                    assignmentsEl.Add(new XElement("ResourceAssignment",
                        new XElement("TaskUniqueID", t.Id),
                        new XElement("ResourceUniqueID", tr.ResourceId),
                        new XElement("Units", tr.AllocationPercent / 100.0)));

            return new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("Project",
                    new XElement("Name", project.Name),
                    new XElement("Manager", project.Author ?? ""),
                    new XElement("StartDate", project.StartDate.ToString("yyyy-MM-ddTHH:mm:ss")),
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
