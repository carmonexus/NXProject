using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

        /// <summary>Achata a árvore de tarefas (todas as gerações).</summary>
        private static IEnumerable<ProjectTask> FlattenTasks(IEnumerable<ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                yield return t;
                foreach (var c in FlattenTasks(t.Children))
                    yield return c;
            }
        }

        /// <summary>
        /// IDs de atividade duplicados no projeto (não pode acontecer; se ocorrer é bug
        /// de quem criou a atividade). Usado para BLOQUEAR a gravação e a sincronização.
        /// </summary>
        public static string? DescribeDuplicateTaskIds(Project project)
        {
            var dups = FlattenTasks(project.Tasks)
                .GroupBy(t => t.Id)
                .Where(g => g.Count() > 1)
                .OrderBy(g => g.Key)
                .ToList();
            if (dups.Count == 0) return null;
            return string.Join("; ", dups.Select(g =>
                $"ID {g.Key}: " + string.Join(" | ", g.Select(t => $"\"{t.Name}\""))));
        }

        public static void Save(Project project, string filePath)
        {
            // Nunca grava com ID duplicado — o arquivo ficaria ilegível/inconsistente.
            var dupMsg = DescribeDuplicateTaskIds(project);
            if (dupMsg != null)
                throw new InvalidOperationException(
                    $"Não é possível gravar: existem atividades com o MESMO ID interno ({dupMsg}). " +
                    "Feche sem salvar e reabra o cronograma; se persistir, reporte o passo que criou as atividades.");

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
                    new XElement(EXT + "LastZoom", project.LastZoom ?? "Mês"),
                    new XElement(NS + "ShowOriginalHoursColumn", project.ShowOriginalHoursColumn),
                    new XElement(NS + "HiddenColumns", project.HiddenColumns ?? ""),
                    new XElement(NS + "HiddenColumnsExpanded", project.HiddenColumnsExpanded ?? ""),
                    new XElement(EXT + "DevOpsProjectName", project.DevOpsProjectName ?? ""),
                    new XElement(EXT + "DevOpsRootWorkItemId", project.DevOpsRootWorkItemId),
                    new XElement(EXT + "DevOpsProjectOwner", project.DevOpsProjectOwner ?? ""),
                    new XElement(EXT + "PepElement", project.PepElement ?? ""),
                    new XElement(EXT + "PepProjectName", project.PepProjectName ?? ""),
                    new XElement(EXT + "DevOpsOrganizationUrl", project.DevOpsOrganizationUrl ?? ""),
                    new XElement(EXT + "DevOpsTeamProject", project.DevOpsTeamProject ?? ""),
                    new XElement(EXT + "PlanSheetPath", project.PlanSheetPath ?? ""),
                    new XElement(EXT + "UseHierarchyColors", project.UseHierarchyColors),
                    new XElement(EXT + "BaselineActive", project.BaselineActive),
                    new XElement(EXT + "DiagramLevelWidths",    project.DiagramLevelWidths    ?? ""),
                    new XElement(EXT + "DiagramExpandedLevels", project.DiagramExpandedLevels ?? ""),
                    new XElement(EXT + "ShowCriticalPath",      project.ShowCriticalPath),
                    new XElement(EXT + "CriticalPathRiskSlackDays", project.CriticalPathRiskSlackDays),
                    new XElement(EXT + "CriticalPathCriticalSlackDays", project.CriticalPathCriticalSlackDays),
                    new XElement(EXT + "HierarchyLevelColors",
                        project.HierarchyLevelColors.Select((c, i) =>
                            new XElement(EXT + "Color", new XAttribute("depth", i), c))),
                    SaveSprintSettingsMetadata(sprintProfile),
                    SaveSprints(project.Sprints),
                    SaveResources(project.Resources),
                    SaveCalendar(project.Calendar),
                    SaveTasks(project.Tasks)
                )
            );
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
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
                    new XElement(EXT + "HighDaysPerSfp", profile.HighDaysPerSfp),
                    new XElement(EXT + "CriticalPathRiskSlackDays", profile.CriticalPathRiskSlackDays),
                    new XElement(EXT + "CriticalPathCriticalSlackDays", profile.CriticalPathCriticalSlackDays)
                )
            );
        }

        private static XElement SaveSprints(ObservableCollection<Sprint> sprints)
        {
            var el = new XElement(EXT + "Sprints");
            foreach (var s in sprints)
            {
                el.Add(new XElement(EXT + "Sprint",
                    new XElement(EXT + "Number", s.Number),
                    new XElement(EXT + "DisplayName", s.DisplayName ?? ""),
                    new XElement(EXT + "Path", s.Path ?? ""),
                    new XElement(EXT + "Start", s.Start.ToString("yyyy-MM-ddTHH:mm:ss")),
                    new XElement(EXT + "End", s.End.ToString("yyyy-MM-ddTHH:mm:ss"))
                ));
            }
            return el;
        }

        // Calendário embutido no cronograma (opcional). Retorna null quando o
        // projeto não tem calendário próprio (usa o Geral da máquina).
        private static XElement? SaveCalendar(NXProject.Models.ProjectCalendar? cal)
        {
            if (cal == null) return null;
            var el = new XElement(EXT + "Calendar",
                new XElement(EXT + "WorkingHoursPerDay", cal.WorkingHoursPerDay),
                new XElement(EXT + "TreatSaturdayAsWorkday", cal.TreatSaturdayAsWorkday),
                new XElement(EXT + "TreatSundayAsWorkday", cal.TreatSundayAsWorkday));
            var holidays = new XElement(EXT + "Holidays");
            foreach (var h in cal.Holidays)
                holidays.Add(new XElement(EXT + "Holiday",
                    new XElement(EXT + "Date", h.Date.ToString("yyyy-MM-dd")),
                    new XElement(EXT + "Name", h.Name ?? "")));
            el.Add(holidays);
            return el;
        }

        private static NXProject.Models.ProjectCalendar? LoadCalendar(XElement root)
        {
            var el = root.Element(EXT + "Calendar");
            if (el == null) return null;
            var cal = new NXProject.Models.ProjectCalendar
            {
                WorkingHoursPerDay = ParseDouble(el.Element(EXT + "WorkingHoursPerDay")?.Value) ?? 8.0,
                TreatSaturdayAsWorkday = bool.TryParse(el.Element(EXT + "TreatSaturdayAsWorkday")?.Value, out var sat) && sat,
                TreatSundayAsWorkday = bool.TryParse(el.Element(EXT + "TreatSundayAsWorkday")?.Value, out var sun) && sun
            };
            foreach (var h in el.Element(EXT + "Holidays")?.Elements(EXT + "Holiday") ?? Enumerable.Empty<XElement>())
            {
                if (DateTime.TryParse(h.Element(EXT + "Date")?.Value, out var d))
                    cal.Holidays.Add(new NXProject.Models.ProjectHoliday { Date = d, Name = h.Element(EXT + "Name")?.Value ?? "" });
            }
            return cal;
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
                    new XElement(NS + "Notes", r.Notes ?? ""),
                    new XElement(EXT + "IsImportedFromTfs", r.IsImportedFromTfs),
                    new XElement(EXT + "Kind", r.Kind.ToString()),
                    new XElement(EXT + "AvailabilityPercent", r.AvailabilityPercent),
                    new XElement(EXT + "Team", r.Team ?? "")
                    // CostType, CostPerHour e MonthlyRate NÃO são gravados no .nxp (sigilosos)
                    // → usar "Salvar config de custo" na tela Pessoas para persistir separadamente
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
            var percentComplete = CalculateSerializablePercentComplete(task);
            var el = new XElement(NS + "Task",
                new XElement(NS + "UID", task.Id),
                new XElement(NS + "Name", task.Name),
                new XElement(NS + "Level", task.Level),
                new XElement(NS + "IsSummary", task.IsSummary),
                new XElement(NS + "IsMilestone", task.IsMilestone),
                new XElement(NS + "Start", task.Start.ToString("yyyy-MM-ddTHH:mm:ss")),
                new XElement(NS + "Finish", task.Finish.ToString("yyyy-MM-ddTHH:mm:ss")),
                new XElement(NS + "PercentComplete", percentComplete),
                new XElement(NS + "SprintNumber", task.SprintNumber),
                new XElement(NS + "Notes", task.Notes ?? ""),
                new XElement(NS + "SfpPoints", task.SfpPoints ?? 0),
                new XElement(NS + "EstimatedHours", task.EstimatedHours ?? 0),
                new XElement(EXT + "OriginalEstimatedHours", task.OriginalEstimatedHours ?? 0),
                new XElement(EXT + "CurrentHours", task.CurrentHours ?? 0),
                new XElement(EXT + "UseOriginalHoursView", task.UseOriginalHoursView),
                new XElement(EXT + "TfsId", task.TfsId?.ToString() ?? ""),
                new XElement(EXT + "TfsParentId", task.TfsParentId?.ToString() ?? ""),
                new XElement(EXT + "TfsType", task.TfsType ?? ""),
                new XElement(EXT + "TfsClassification", task.TfsClassification ?? ""),
                new XElement(EXT + "EpicType", task.EpicType ?? ""),
                new XElement(EXT + "Approved", task.Approved?.ToString() ?? ""),
                task.CustomDevopsFieldValues.Count > 0
                    ? new XElement(EXT + "CustomDevopsFieldValues",
                        task.CustomDevopsFieldValues.Select(kv =>
                            new XElement(EXT + "CF", new XAttribute("ref", kv.Key), kv.Value)))
                    : null!,
                new XElement(EXT + "TfsState", task.TfsState ?? ""),
                new XElement(EXT + "TipoCentroCusto", task.TipoCentroCusto ?? ""),
                new XElement(EXT + "TfsTags", task.Tags ?? ""),
                new XElement(EXT + "BlockedByChild", task.BlockedByChild),
                new XElement(EXT + "TfsStackRank", task.TfsStackRank?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""),
                new XElement(EXT + "TfsIterationPath", task.TfsIterationPath ?? ""),
                new XElement(EXT + "Description", task.Description ?? ""),
                new XElement(EXT + "SyncVersion", task.SyncVersion?.ToString() ?? ""),
                new XElement(EXT + "HasSyncConflict", task.HasSyncConflict),
                new XElement(EXT + "HasBrokenPredecessorLink", task.HasBrokenPredecessorLink),
                new XElement(EXT + "StartFixed", task.StartFixed),
                new XElement(EXT + "FinishFixed", task.FinishFixed),
                new XElement(EXT + "CalculatedFinish", task.CalculatedFinish?.ToString("yyyy-MM-ddTHH:mm:ss") ?? ""),
                new XElement(EXT + "Priority", task.Priority?.ToString() ?? ""),
                new XElement(EXT + "TasksSuppressed", task.TasksSuppressed),
                new XElement(EXT + "DevopsTaskCount", task.DevopsTaskCount?.ToString() ?? ""),
                new XElement(EXT + "Justificativa", task.Justificativa ?? ""),
                new XElement(EXT + "SourcePlanPath", task.SourcePlanPath ?? ""),
                new XElement(EXT + "SourcePlanRowKey", task.SourcePlanRowKey ?? ""),
                new XElement(EXT + "PlanObservation", task.PlanObservation ?? "")
            );

            // Resumo de alocação das Tasks do DevOps por recurso (dono + horas).
            if (task.TaskAllocations.Count > 0)
            {
                var allocs = new XElement(EXT + "TaskAllocations");
                foreach (var a in task.TaskAllocations)
                    allocs.Add(new XElement(EXT + "TaskAllocation",
                        new XAttribute("resource", a.Resource ?? ""),
                        new XAttribute("hours", a.Hours),
                        new XAttribute("tasks", a.Tasks),
                        new XAttribute("state", a.State ?? "")));
                el.Add(allocs);
            }

            // Predecessoras
            var preds = new XElement(NS + "PredecessorLinks");
            foreach (var predId in task.PredecessorIds)
                preds.Add(new XElement(NS + "PredecessorLink", new XElement(NS + "PredecessorUID", predId)));
            el.Add(preds);

            // Recursos alocados
            var assignments = new XElement(NS + "Assignments");
            foreach (var tr in task.Resources)
            {
                var assignmentHours = GetSerializableAssignmentEstimatedHours(task, tr);
                assignments.Add(new XElement(NS + "Assignment",
                    new XElement(NS + "ResourceUID", tr.ResourceId),
                    new XElement(NS + "AllocationPercent", tr.AllocationPercent),
                    new XElement(NS + "EstimatedHours", assignmentHours ?? 0)
                ));
            }
            el.Add(assignments);

            // Filhos
            foreach (var child in task.Children)
                SaveTaskRecursive(el, child);

            parent.Add(el);
        }

        private static double CalculateSerializablePercentComplete(ProjectTask task)
        {
            if (task.Children.Count == 0)
                return task.PercentComplete;

            double totalWeight = 0.0;
            double weightedPercent = 0.0;
            foreach (var child in task.Children)
            {
                var weight = Math.Max(1.0, TaskScheduleService.GetEffectiveDurationHours(child));
                weightedPercent += CalculateSerializablePercentComplete(child) * weight;
                totalWeight += weight;
            }

            return totalWeight > 0
                ? weightedPercent / totalWeight
                : task.Children.Average(c => c.PercentComplete);
        }

        private static double? GetSerializableAssignmentEstimatedHours(ProjectTask task, TaskResource assignment)
        {
            if (!(task.EstimatedHours is > 0))
                return assignment.EstimatedHours;

            if (task.Resources.Count == 1)
                return task.EstimatedHours.Value;

            var assignmentTotal = task.Resources
                .Where(r => r.EstimatedHours is > 0)
                .Sum(r => r.EstimatedHours!.Value);
            if (assignmentTotal <= 0)
                return assignment.EstimatedHours;

            if (Math.Abs(assignmentTotal - task.EstimatedHours.Value) <= 0.01)
                return assignment.EstimatedHours;

            var assignmentHours = Math.Max(0, assignment.EstimatedHours ?? 0);
            return Math.Round(assignmentHours / assignmentTotal * task.EstimatedHours.Value, 2);
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
                LowDaysPerSfp = ParseDouble(root.Element(NS + "LowDaysPerSfp")?.Value) ?? 1.0,
                MediumDaysPerSfp = ParseDouble(root.Element(NS + "MediumDaysPerSfp")?.Value) ?? 1.0,
                HighDaysPerSfp = ParseDouble(root.Element(NS + "HighDaysPerSfp")?.Value) ?? 1.0,
                LastZoom = string.IsNullOrWhiteSpace(root.Element(EXT + "LastZoom")?.Value)
                    ? "Mês" : root.Element(EXT + "LastZoom")!.Value,
                ShowOriginalHoursColumn = bool.TryParse(root.Element(NS + "ShowOriginalHoursColumn")?.Value, out var sohc) && sohc,
                HiddenColumns = root.Element(NS + "HiddenColumns")?.Value ?? "",
                HiddenColumnsExpanded = root.Element(NS + "HiddenColumnsExpanded")?.Value ?? "",
                DevOpsProjectName = string.IsNullOrWhiteSpace(root.Element(EXT + "DevOpsProjectName")?.Value)
                    ? null : root.Element(EXT + "DevOpsProjectName")!.Value,
                DevOpsRootWorkItemId = int.TryParse(root.Element(EXT + "DevOpsRootWorkItemId")?.Value, out var devOpsId) ? devOpsId : 0,
                DevOpsProjectOwner = string.IsNullOrWhiteSpace(root.Element(EXT + "DevOpsProjectOwner")?.Value)
                    ? null : root.Element(EXT + "DevOpsProjectOwner")!.Value,
                PepElement = string.IsNullOrWhiteSpace(root.Element(EXT + "PepElement")?.Value)
                    ? null : root.Element(EXT + "PepElement")!.Value,
                PepProjectName = string.IsNullOrWhiteSpace(root.Element(EXT + "PepProjectName")?.Value)
                    ? null : root.Element(EXT + "PepProjectName")!.Value,
                DevOpsOrganizationUrl = string.IsNullOrWhiteSpace(root.Element(EXT + "DevOpsOrganizationUrl")?.Value)
                    ? null : root.Element(EXT + "DevOpsOrganizationUrl")!.Value,
                DevOpsTeamProject = string.IsNullOrWhiteSpace(root.Element(EXT + "DevOpsTeamProject")?.Value)
                    ? null : root.Element(EXT + "DevOpsTeamProject")!.Value,
                PlanSheetPath = string.IsNullOrWhiteSpace(root.Element(EXT + "PlanSheetPath")?.Value)
                    ? null : root.Element(EXT + "PlanSheetPath")!.Value,
                UseHierarchyColors    = bool.TryParse(root.Element(EXT + "UseHierarchyColors")?.Value, out var uhc) && uhc,
                BaselineActive        = !bool.TryParse(root.Element(EXT + "BaselineActive")?.Value, out var ba) || ba,
                DiagramLevelWidths    = root.Element(EXT + "DiagramLevelWidths")?.Value    ?? "",
                DiagramExpandedLevels = root.Element(EXT + "DiagramExpandedLevels")?.Value ?? "",
                ShowCriticalPath      = bool.TryParse(root.Element(EXT + "ShowCriticalPath")?.Value, out var scp) && scp,
                CriticalPathRiskSlackDays = Math.Max(0.0, ParseDouble(root.Element(EXT + "CriticalPathRiskSlackDays")?.Value) ?? 2.0),
                CriticalPathCriticalSlackDays = Math.Max(0.0, ParseDouble(root.Element(EXT + "CriticalPathCriticalSlackDays")?.Value) ?? 1.0),
                FilePath = filePath
            };
            if (project.CriticalPathRiskSlackDays < project.CriticalPathCriticalSlackDays)
                project.CriticalPathRiskSlackDays = project.CriticalPathCriticalSlackDays;

            var colorsEl = root.Element(EXT + "HierarchyLevelColors");
            if (colorsEl != null)
            {
                var loaded = colorsEl.Elements(EXT + "Color")
                    .OrderBy(e => int.TryParse(e.Attribute("depth")?.Value, out var d) ? d : 0)
                    .Select(e => e.Value?.Trim())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList();
                if (loaded.Count > 0)
                    project.HierarchyLevelColors = loaded!;
            }

            var extendedSprintSettings = LoadSprintSettingsMetadata(root);
            if (extendedSprintSettings != null)
                project.ApplySprintSettingsProfile(extendedSprintSettings);

            var sprintsEl = root.Element(EXT + "Sprints");
            if (sprintsEl != null)
                foreach (var s in sprintsEl.Elements(EXT + "Sprint"))
                    project.Sprints.Add(new Sprint
                    {
                        Number = int.TryParse(s.Element(EXT + "Number")?.Value, out var num) ? num : 0,
                        DisplayName = string.IsNullOrWhiteSpace(s.Element(EXT + "DisplayName")?.Value) ? null : s.Element(EXT + "DisplayName")?.Value,
                        Path = string.IsNullOrWhiteSpace(s.Element(EXT + "Path")?.Value) ? null : s.Element(EXT + "Path")?.Value,
                        Start = ParseDate(s.Element(EXT + "Start")?.Value) ?? DateTime.Today,
                        End = ParseDate(s.Element(EXT + "End")?.Value) ?? DateTime.Today
                    });

            var resEl = root.Element(NS + "Resources");
            if (resEl != null)
                foreach (var r in resEl.Elements(NS + "Resource"))
                    project.Resources.Add(LoadResource(r));

            project.Calendar = LoadCalendar(root);

            var tasksEl = root.Element(NS + "Tasks");
            if (tasksEl != null)
                foreach (var t in tasksEl.Elements(NS + "Task"))
                    project.Tasks.Add(LoadTask(t, null));

            ResolveResourceReferences(project.Tasks, project.Resources);
            NormalizeDuplicateTaskIds(project);
            return project;
        }

        /// <summary>
        /// Arquivo legado gravado com ID duplicado (bug já corrigido na origem): mantém o
        /// primeiro e dá um ID novo aos demais — as predecessoras por ID continuam
        /// apontando para o primeiro. Assim o arquivo abre e se conserta no próximo salvar.
        /// </summary>
        private static void NormalizeDuplicateTaskIds(Project project)
        {
            var all = FlattenTasks(project.Tasks).ToList();
            if (all.Count == 0) return;

            var seen = new HashSet<int>();
            int next = all.Max(t => t.Id) + 1;
            foreach (var t in all)
                if (!seen.Add(t.Id))
                {
                    t.Id = next;
                    seen.Add(next);
                    next++;
                }
        }

        private static void ResolveResourceReferences(
            ObservableCollection<ProjectTask> tasks,
            ObservableCollection<Resource> resources)
        {
            foreach (var task in tasks)
            {
                foreach (var assignment in task.Resources)
                    assignment.Resource = resources.FirstOrDefault(r => r.Id == assignment.ResourceId);

                ResolveResourceReferences(task.Children, resources);
            }
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
                LowDaysPerSfp = ParseDouble(sprintSettings.Element(EXT + "LowDaysPerSfp")?.Value) ?? 1.0,
                MediumDaysPerSfp = ParseDouble(sprintSettings.Element(EXT + "MediumDaysPerSfp")?.Value) ?? 1.0,
                HighDaysPerSfp = ParseDouble(sprintSettings.Element(EXT + "HighDaysPerSfp")?.Value) ?? 1.0,
                CriticalPathRiskSlackDays = ParseDouble(sprintSettings.Element(EXT + "CriticalPathRiskSlackDays")?.Value) ?? 2.0,
                CriticalPathCriticalSlackDays = ParseDouble(sprintSettings.Element(EXT + "CriticalPathCriticalSlackDays")?.Value) ?? 1.0
            };
        }

        private static Resource LoadResource(XElement el) => new()
        {
            Id = int.TryParse(el.Element(NS + "UID")?.Value, out var id) ? id : 0,
            Name = el.Element(NS + "Name")?.Value ?? "",
            Type = Enum.TryParse<ResourceType>(el.Element(NS + "Type")?.Value, out var rt) ? rt : ResourceType.Work,
            MaxUnitsPerDay = ParseDouble(el.Element(NS + "MaxUnitsPerDay")?.Value) ?? 8,
            CostPerHour = ParseDecimal(el.Element(NS + "CostPerHour")?.Value) ?? 0,
            Email = el.Element(NS + "Email")?.Value,
            Notes = el.Element(NS + "Notes")?.Value,
            IsImportedFromTfs = bool.TryParse(el.Element(EXT + "IsImportedFromTfs")?.Value, out var importedFromTfs) && importedFromTfs,
            Kind = Enum.TryParse<ResourceKind>(el.Element(EXT + "Kind")?.Value, out var rk) ? rk : ResourceKind.Project,
            AvailabilityPercent = ParseDouble(el.Element(EXT + "AvailabilityPercent")?.Value) ?? 100.0,
            Team = string.IsNullOrWhiteSpace(el.Element(EXT + "Team")?.Value) ? null : el.Element(EXT + "Team")!.Value
            // CostType/CostPerHour/MonthlyRate carregados separadamente via ResourceCostConfigService
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
                PercentComplete = ParseDouble(el.Element(NS + "PercentComplete")?.Value) ?? 0,
                SprintNumber = int.TryParse(el.Element(NS + "SprintNumber")?.Value, out var sn) ? sn : 0,
                Notes = el.Element(NS + "Notes")?.Value,
                SfpPoints = ParseDouble(el.Element(NS + "SfpPoints")?.Value),
                EstimatedHours = ParseDouble(el.Element(NS + "EstimatedHours")?.Value),
                OriginalEstimatedHours = ParseDouble(el.Element(EXT + "OriginalEstimatedHours")?.Value) is { } oeh && oeh > 0 ? oeh : null,
                CurrentHours = ParseDouble((el.Element(EXT + "CurrentHours") ?? el.Element(EXT + "RealizedHours"))?.Value) is { } rh && rh > 0 ? rh : null,
                UseOriginalHoursView = bool.TryParse(el.Element(EXT + "UseOriginalHoursView")?.Value, out var uohv) && uohv,
                TfsId = int.TryParse(el.Element(EXT + "TfsId")?.Value, out var tfsId) ? tfsId : null,
                TfsParentId = int.TryParse(el.Element(EXT + "TfsParentId")?.Value, out var tfsPid) ? tfsPid : null,
                TfsType = string.IsNullOrWhiteSpace(el.Element(EXT + "TfsType")?.Value) ? null : el.Element(EXT + "TfsType")?.Value,
                TfsClassification = string.IsNullOrWhiteSpace(el.Element(EXT + "TfsClassification")?.Value) ? null : el.Element(EXT + "TfsClassification")?.Value,
                EpicType = string.IsNullOrWhiteSpace(el.Element(EXT + "EpicType")?.Value) ? null : el.Element(EXT + "EpicType")?.Value,
                Approved = bool.TryParse(el.Element(EXT + "Approved")?.Value, out var approvedFlag) ? approvedFlag : null,
                CustomDevopsFieldValues = el.Element(EXT + "CustomDevopsFieldValues")
                    ?.Elements(EXT + "CF")
                    .Where(cf => cf.Attribute("ref") != null)
                    .ToDictionary(cf => cf.Attribute("ref")!.Value, cf => cf.Value, StringComparer.OrdinalIgnoreCase)
                    ?? [],
                TfsState = string.IsNullOrWhiteSpace(el.Element(EXT + "TfsState")?.Value) ? null : el.Element(EXT + "TfsState")?.Value,
                TipoCentroCusto = string.IsNullOrWhiteSpace(el.Element(EXT + "TipoCentroCusto")?.Value) ? null : el.Element(EXT + "TipoCentroCusto")?.Value,
                Tags = string.IsNullOrWhiteSpace(el.Element(EXT + "TfsTags")?.Value) ? null : el.Element(EXT + "TfsTags")?.Value,
                BlockedByChild = bool.TryParse(el.Element(EXT + "BlockedByChild")?.Value, out var bbc) && bbc,
                TfsStackRank = ParseDouble(el.Element(EXT + "TfsStackRank")?.Value),
                TfsIterationPath = string.IsNullOrWhiteSpace(el.Element(EXT + "TfsIterationPath")?.Value) ? null : el.Element(EXT + "TfsIterationPath")?.Value,
                Description = string.IsNullOrWhiteSpace(el.Element(EXT + "Description")?.Value) ? null : el.Element(EXT + "Description")?.Value,
                SyncVersion = int.TryParse(el.Element(EXT + "SyncVersion")?.Value, out var sv) ? sv : null,
                HasSyncConflict = bool.TryParse(el.Element(EXT + "HasSyncConflict")?.Value, out var hsc) && hsc,
                HasBrokenPredecessorLink = bool.TryParse(el.Element(EXT + "HasBrokenPredecessorLink")?.Value, out var hbpl) && hbpl,
                StartFixed    = bool.TryParse(el.Element(EXT + "StartFixed")?.Value, out var sf) && sf,
                FinishFixed   = bool.TryParse(el.Element(EXT + "FinishFixed")?.Value, out var ff) && ff,
                CalculatedFinish = ParseDate(el.Element(EXT + "CalculatedFinish")?.Value),
                Priority = int.TryParse(el.Element(EXT + "Priority")?.Value, out var prio) ? prio : null,
                TasksSuppressed = bool.TryParse(el.Element(EXT + "TasksSuppressed")?.Value, out var ts) && ts,
                DevopsTaskCount = int.TryParse(el.Element(EXT + "DevopsTaskCount")?.Value, out var dtc) ? dtc : null,
                Justificativa = string.IsNullOrWhiteSpace(el.Element(EXT + "Justificativa")?.Value) ? null : el.Element(EXT + "Justificativa")?.Value,
                SourcePlanPath = string.IsNullOrWhiteSpace(el.Element(EXT + "SourcePlanPath")?.Value) ? null : el.Element(EXT + "SourcePlanPath")?.Value,
                SourcePlanRowKey = string.IsNullOrWhiteSpace(el.Element(EXT + "SourcePlanRowKey")?.Value) ? null : el.Element(EXT + "SourcePlanRowKey")?.Value,
                PlanObservation = string.IsNullOrWhiteSpace(el.Element(EXT + "PlanObservation")?.Value) ? null : el.Element(EXT + "PlanObservation")?.Value,
                Parent = parent
            };

            ScheduleHoursService.ApplyMissingProgressHours(task);

            // Resumo de alocação das Tasks do DevOps por recurso (retrocompatível: ausente = vazio).
            var allocsEl = el.Element(EXT + "TaskAllocations");
            if (allocsEl != null)
                foreach (var a in allocsEl.Elements(EXT + "TaskAllocation"))
                {
                    var resource = a.Attribute("resource")?.Value ?? "";
                    if (string.IsNullOrWhiteSpace(resource)) continue;
                    task.TaskAllocations.Add(new TaskAllocationSummary
                    {
                        Resource = resource,
                        Hours = ParseDouble(a.Attribute("hours")?.Value) ?? 0,
                        Tasks = int.TryParse(a.Attribute("tasks")?.Value, out var tc) ? tc : 0,
                        State = a.Attribute("state")?.Value ?? ""
                    });
                }

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
                        AllocationPercent = ParseDouble(a.Element(NS + "AllocationPercent")?.Value) ?? 100,
                        EstimatedHours = ParseDouble(a.Element(NS + "EstimatedHours")?.Value)
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

        private static double? ParseDouble(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return double.TryParse(
                    value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var invariant)
                ? invariant
                : double.TryParse(
                    value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture,
                    out var current)
                    ? current
                    : null;
        }

        private static decimal? ParseDecimal(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return decimal.TryParse(
                    value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var invariant)
                ? invariant
                : decimal.TryParse(
                    value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture,
                    out var current)
                    ? current
                    : null;
        }
    }
}
