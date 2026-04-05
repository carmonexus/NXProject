using System;
using System.Collections.ObjectModel;
using System.Xml.Linq;
using NXProject.Models;

namespace NXProject.Services
{
    /// <summary>
    /// Salva e carrega projetos em formato MSPDI XML simplificado.
    /// Compatível com a estrutura básica do Microsoft Project XML.
    /// </summary>
    public static class XmlProjectService
    {
        private static readonly XNamespace NS = "http://schemas.microsoft.com/project";
        private static readonly XNamespace EXT = "urn:nxproject:extensions:v1";

        public static void Save(Project project, string filePath)
        {
            var sprintProfile = project.GetSprintSettingsProfile();
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(NS + "Project",
                    new XAttribute(XNamespace.Xmlns + "nx", EXT),
                    new XElement(NS + "Name", project.Name),
                    new XElement(NS + "Author", project.Author ?? ""),
                    new XElement(NS + "StartDate", project.StartDate.ToString("yyyy-MM-ddTHH:mm:ss")),
                    new XElement(NS + "SprintDurationDays", project.SprintDurationDays),
                    new XElement(NS + "FirstSprintNumber", project.FirstSprintNumber),
                    new XElement(NS + "SprintNumberingMode", project.SprintNumberingMode),
                    new XElement(NS + "LowDaysPerSfp", project.LowDaysPerSfp),
                    new XElement(NS + "MediumDaysPerSfp", project.MediumDaysPerSfp),
                    new XElement(NS + "HighDaysPerSfp", project.HighDaysPerSfp),
                    SaveSprintSettingsMetadata(sprintProfile),
                    SaveResources(project.Resources),
                    SaveTasks(project.Tasks)
                )
            );
            doc.Save(filePath);
        }

        private static XElement SaveSprintSettingsMetadata(SprintSettingsProfile profile)
        {
            return new XElement(EXT + "NXProjectMetadata",
                new XElement(EXT + "SprintSettings",
                    new XElement(EXT + "SprintDurationDays", profile.SprintDurationDays),
                    new XElement(EXT + "FirstSprintNumber", profile.FirstSprintNumber),
                    new XElement(EXT + "SprintNumberingMode", profile.SprintNumberingMode),
                    new XElement(EXT + "LowDaysPerSfp", profile.LowDaysPerSfp),
                    new XElement(EXT + "MediumDaysPerSfp", profile.MediumDaysPerSfp),
                    new XElement(EXT + "HighDaysPerSfp", profile.HighDaysPerSfp)
                )
            );
        }

        private static XElement SaveResources(ObservableCollection<Resource> resources)
        {
            var el = new XElement(NS + "Resources");
            foreach (var r in resources)
            {
                el.Add(new XElement(NS + "Resource",
                    new XElement(NS + "UID", r.Id),
                    new XElement(NS + "Name", r.Name),
                    new XElement(NS + "Type", (int)r.Type),
                    new XElement(NS + "MaxUnitsPerDay", r.MaxUnitsPerDay),
                    new XElement(NS + "CostPerHour", r.CostPerHour),
                    new XElement(NS + "Email", r.Email ?? ""),
                    new XElement(NS + "Notes", r.Notes ?? "")
                ));
            }
            return el;
        }

        private static XElement SaveTasks(ObservableCollection<ProjectTask> tasks)
        {
            var el = new XElement(NS + "Tasks");
            foreach (var t in tasks)
                SaveTaskRecursive(el, t);
            return el;
        }

        private static void SaveTaskRecursive(XElement parent, ProjectTask task)
        {
            var el = new XElement(NS + "Task",
                new XElement(NS + "UID", task.Id),
                new XElement(NS + "Name", task.Name),
                new XElement(NS + "Level", task.Level),
                new XElement(NS + "IsSummary", task.IsSummary),
                new XElement(NS + "IsMilestone", task.IsMilestone),
                new XElement(NS + "Start", task.Start.ToString("yyyy-MM-ddTHH:mm:ss")),
                new XElement(NS + "Finish", task.Finish.ToString("yyyy-MM-ddTHH:mm:ss")),
                new XElement(NS + "PercentComplete", task.PercentComplete),
                new XElement(NS + "SprintNumber", task.SprintNumber),
                new XElement(NS + "Notes", task.Notes ?? ""),
                new XElement(NS + "SfpPoints", task.SfpPoints ?? 0),
                new XElement(NS + "EstimatedHours", task.EstimatedHours ?? 0)
            );

            // Predecessoras
            var preds = new XElement(NS + "PredecessorLinks");
            foreach (var predId in task.PredecessorIds)
                preds.Add(new XElement(NS + "PredecessorLink", new XElement(NS + "PredecessorUID", predId)));
            el.Add(preds);

            // Recursos alocados
            var assignments = new XElement(NS + "Assignments");
            foreach (var tr in task.Resources)
            {
                assignments.Add(new XElement(NS + "Assignment",
                    new XElement(NS + "ResourceUID", tr.ResourceId),
                    new XElement(NS + "AllocationPercent", tr.AllocationPercent),
                    new XElement(NS + "EstimatedHours", tr.EstimatedHours ?? 0)
                ));
            }
            el.Add(assignments);

            // Filhos
            foreach (var child in task.Children)
                SaveTaskRecursive(el, child);

            parent.Add(el);
        }

        // ── Load ─────────────────────────────────────────────────────────────

        public static Project Load(string filePath)
        {
            var doc = XDocument.Load(filePath);
            var root = doc.Root ?? throw new Exception("XML inválido");

            var project = new Project
            {
                Name = root.Element(NS + "Name")?.Value ?? "Projeto",
                Author = root.Element(NS + "Author")?.Value,
                StartDate = ParseDate(root.Element(NS + "StartDate")?.Value) ?? DateTime.Today,
                SprintDurationDays = int.TryParse(root.Element(NS + "SprintDurationDays")?.Value, out var sd) ? sd : 14,
                FirstSprintNumber = int.TryParse(root.Element(NS + "FirstSprintNumber")?.Value, out var fsn) ? fsn : 1,
                SprintNumberingMode = root.Element(NS + "SprintNumberingMode")?.Value ?? "Sequencial",
                LowDaysPerSfp = double.TryParse(root.Element(NS + "LowDaysPerSfp")?.Value, out var ldps) ? ldps : 1.0,
                MediumDaysPerSfp = double.TryParse(root.Element(NS + "MediumDaysPerSfp")?.Value, out var mdps) ? mdps : 1.0,
                HighDaysPerSfp = double.TryParse(root.Element(NS + "HighDaysPerSfp")?.Value, out var hdps) ? hdps : 1.0,
                FilePath = filePath
            };

            var extendedSprintSettings = LoadSprintSettingsMetadata(root);
            if (extendedSprintSettings != null)
                project.ApplySprintSettingsProfile(extendedSprintSettings);

            var resEl = root.Element(NS + "Resources");
            if (resEl != null)
                foreach (var r in resEl.Elements(NS + "Resource"))
                    project.Resources.Add(LoadResource(r));

            var tasksEl = root.Element(NS + "Tasks");
            if (tasksEl != null)
                foreach (var t in tasksEl.Elements(NS + "Task"))
                    project.Tasks.Add(LoadTask(t, null));

            return project;
        }

        private static SprintSettingsProfile? LoadSprintSettingsMetadata(XElement root)
        {
            var metadata = root.Element(EXT + "NXProjectMetadata");
            var sprintSettings = metadata?.Element(EXT + "SprintSettings");
            if (sprintSettings == null)
                return null;

            return new SprintSettingsProfile
            {
                SprintDurationDays = int.TryParse(sprintSettings.Element(EXT + "SprintDurationDays")?.Value, out var durationDays) ? durationDays : 14,
                FirstSprintNumber = int.TryParse(sprintSettings.Element(EXT + "FirstSprintNumber")?.Value, out var firstSprintNumber) ? firstSprintNumber : 1,
                SprintNumberingMode = sprintSettings.Element(EXT + "SprintNumberingMode")?.Value ?? "Sequencial",
                LowDaysPerSfp = double.TryParse(sprintSettings.Element(EXT + "LowDaysPerSfp")?.Value, out var lowDays) ? lowDays : 1.0,
                MediumDaysPerSfp = double.TryParse(sprintSettings.Element(EXT + "MediumDaysPerSfp")?.Value, out var mediumDays) ? mediumDays : 1.0,
                HighDaysPerSfp = double.TryParse(sprintSettings.Element(EXT + "HighDaysPerSfp")?.Value, out var highDays) ? highDays : 1.0
            };
        }

        private static Resource LoadResource(XElement el) => new()
        {
            Id = int.TryParse(el.Element(NS + "UID")?.Value, out var id) ? id : 0,
            Name = el.Element(NS + "Name")?.Value ?? "",
            Type = Enum.TryParse<ResourceType>(el.Element(NS + "Type")?.Value, out var rt) ? rt : ResourceType.Work,
            MaxUnitsPerDay = double.TryParse(el.Element(NS + "MaxUnitsPerDay")?.Value, out var mu) ? mu : 8,
            CostPerHour = decimal.TryParse(el.Element(NS + "CostPerHour")?.Value, out var cp) ? cp : 0,
            Email = el.Element(NS + "Email")?.Value,
            Notes = el.Element(NS + "Notes")?.Value
        };

        private static ProjectTask LoadTask(XElement el, ProjectTask? parent)
        {
            var task = new ProjectTask
            {
                Id = int.TryParse(el.Element(NS + "UID")?.Value, out var id) ? id : 0,
                Name = el.Element(NS + "Name")?.Value ?? "",
                Level = int.TryParse(el.Element(NS + "Level")?.Value, out var lv) ? lv : 0,
                IsSummary = bool.TryParse(el.Element(NS + "IsSummary")?.Value, out var iss) && iss,
                IsMilestone = bool.TryParse(el.Element(NS + "IsMilestone")?.Value, out var ism) && ism,
                Start = ParseDate(el.Element(NS + "Start")?.Value) ?? DateTime.Today,
                Finish = ParseDate(el.Element(NS + "Finish")?.Value) ?? DateTime.Today.AddDays(1),
                PercentComplete = double.TryParse(el.Element(NS + "PercentComplete")?.Value, out var pc) ? pc : 0,
                SprintNumber = int.TryParse(el.Element(NS + "SprintNumber")?.Value, out var sn) ? sn : 0,
                Notes = el.Element(NS + "Notes")?.Value,
                SfpPoints = double.TryParse(el.Element(NS + "SfpPoints")?.Value, out var sfp) ? sfp : null,
                EstimatedHours = double.TryParse(el.Element(NS + "EstimatedHours")?.Value, out var eh) ? eh : null,
                Parent = parent
            };

            // Predecessoras
            var predsEl = el.Element(NS + "PredecessorLinks");
            if (predsEl != null)
                foreach (var p in predsEl.Elements(NS + "PredecessorLink"))
                    if (int.TryParse(p.Element(NS + "PredecessorUID")?.Value, out var pid))
                        task.PredecessorIds.Add(pid);

            // Recursos
            var assignEl = el.Element(NS + "Assignments");
            if (assignEl != null)
                foreach (var a in assignEl.Elements(NS + "Assignment"))
                    task.Resources.Add(new TaskResource
                    {
                        ResourceId = int.TryParse(a.Element(NS + "ResourceUID")?.Value, out var rid) ? rid : 0,
                        AllocationPercent = double.TryParse(a.Element(NS + "AllocationPercent")?.Value, out var ap) ? ap : 100,
                        EstimatedHours = double.TryParse(a.Element(NS + "EstimatedHours")?.Value, out var teh) ? teh : null
                    });

            // Filhos recursivos
            foreach (var child in el.Elements(NS + "Task"))
                task.Children.Add(LoadTask(child, task));

            return task;
        }

        private static DateTime? ParseDate(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return DateTime.TryParse(value, out var dt) ? dt : null;
        }
    }
}
