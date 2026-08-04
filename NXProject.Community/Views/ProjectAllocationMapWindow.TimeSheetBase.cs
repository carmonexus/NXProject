using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Xml.Linq;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>
    /// "Base de cálculo" do time sheet: grava num XML tudo que entrou na geração — cronogramas
    /// carregados, recursos, atividades candidatas, calendário do mês e a visão gerada. Serve
    /// para reproduzir/diagnosticar um resultado fora da máquina onde ele aconteceu.
    /// </summary>
    public partial class ProjectAllocationMapWindow
    {
        private const string BaseFileFilter = "Base de cálculo do time sheet (*.xml)|*.xml|Todos os arquivos (*.*)|*.*";

        private void OnTimeSheetSaveBaseClick(object sender, RoutedEventArgs e)
        {
            if (SelectedTimeSheetMonth() is not { } month)
            {
                MessageBox.Show(this, AppStrings.Get("PMap_TsPickResource"), "NXProject",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var resource = TsResourceCombo.SelectedItem as string ?? "";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter   = BaseFileFilter,
                FileName = $"timesheet-base {resource} {month.Start:yyyy-MM}.xml"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                BuildBaseXml(resource, month.Start, month.End).Save(dlg.FileName);
                MessageBox.Show(this, AppStrings.Get("PMap_TsBaseSaved", dlg.FileName), "NXProject",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "NXProject", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private XDocument BuildBaseXml(string resource, DateTime monthStart, DateTime monthEnd)
        {
            var root = new XElement("TimeSheetBase",
                new XAttribute("generatedAt", DateTime.Now.ToString("s", CultureInfo.InvariantCulture)),
                new XAttribute("resource", resource),
                new XAttribute("monthStart", monthStart.ToString("yyyy-MM-dd")),
                new XAttribute("monthEnd", monthEnd.ToString("yyyy-MM-dd")),
                new XAttribute("fillGaps", TsFillGapsBox.IsChecked == true),
                new XAttribute("workingHoursPerDay", ProjectCalendarService.WorkingHoursPerDay));

            // Calendário: quais dias do mês são úteis (é o que separa dia de apontamento de feriado).
            var calendar = new XElement("Calendar");
            for (var day = monthStart; day <= monthEnd; day = day.AddDays(1))
                calendar.Add(new XElement("Day",
                    new XAttribute("date", day.ToString("yyyy-MM-dd")),
                    new XAttribute("weekDay", (int)day.DayOfWeek + 1),
                    new XAttribute("working", ProjectCalendarService.IsWorkingDay(day))));
            root.Add(calendar);

            // Ordem dos cronogramas por HH do recurso no mês (base do desempate e do gap).
            var byLoad = ProjectsByResourceLoad(resource, monthStart, monthEnd);

            var projects = new XElement("Projects");
            foreach (var proj in _projects)
            {
                var pe = new XElement("Project",
                    new XAttribute("name", proj.Name ?? ""),
                    new XAttribute("file", proj.FilePath ?? ""),
                    new XAttribute("scheduleName", proj.Data.Name ?? ""),
                    new XAttribute("owner", proj.Data.DevOpsProjectOwner ?? ""),
                    new XAttribute("pepElement", proj.Data.PepElement ?? ""),
                    new XAttribute("pepProjectName", proj.Data.PepProjectName ?? ""),
                    new XAttribute("loadOrder", byLoad.IndexOf(proj)));

                pe.Add(new XElement("Resources",
                    proj.Data.Resources.Select(r => new XElement("Resource",
                        new XAttribute("id", r.Id),
                        new XAttribute("name", r.Name ?? ""),
                        new XAttribute("email", r.Email ?? ""),
                        new XAttribute("maxUnitsPerDay", r.MaxUnitsPerDay),
                        new XAttribute("matchesSelected", SameResource(r, resource))))));

                // TODAS as atividades do cronograma (a base é para diagnóstico; o que entra na
                // planilha é decidido depois). "candidate" marca as que o time sheet percorre.
                var candidates = new HashSet<ProjectTask>(TimeSheetTasks(proj));
                var tasks = new XElement("Tasks");
                foreach (var t in AllTasks(proj.Data.Tasks))
                {
                    var owner   = IsOwnerOf(t, resource);
                    var summary = HasSummaryTasks(t, resource);
                    var te = new XElement("Task",
                        new XAttribute("id", t.Id),
                        new XAttribute("tfsId", t.TfsId?.ToString() ?? ""),
                        new XAttribute("type", t.TfsType ?? ""),
                        new XAttribute("state", t.TfsState ?? ""),
                        new XAttribute("name", t.Name ?? ""),
                        new XAttribute("start", t.Start.ToString("yyyy-MM-dd")),
                        new XAttribute("finish", t.Finish.ToString("yyyy-MM-dd")),
                        new XAttribute("percentComplete", t.PercentComplete),
                        new XAttribute("currentHours", t.CurrentHours?.ToString(CultureInfo.InvariantCulture) ?? ""),
                        new XAttribute("estimatedHours", t.EstimatedHours?.ToString(CultureInfo.InvariantCulture) ?? ""),
                        new XAttribute("hoursPerDay", HoursPerDayOf(t)),
                        new XAttribute("candidate", candidates.Contains(t)),
                        new XAttribute("parentId", t.Parent?.Id.ToString() ?? ""),
                        new XAttribute("parentName", t.Parent?.Name ?? ""),
                        new XAttribute("children", t.Children.Count),
                        new XAttribute("isOwnerOfSelected", owner),
                        new XAttribute("hasSummaryOfSelected", summary),
                        new XAttribute("feature", FindAncestorOfType(t, "Feature")?.Name ?? ""),
                        new XAttribute("epic", FindAncestorOfType(t, "Epic")?.Name ?? ""));

                    te.Add(new XElement("Assignments",
                        t.Resources.Select(r => new XElement("Assignment",
                            new XAttribute("resourceId", r.ResourceId),
                            new XAttribute("name", r.Resource?.Name ?? ""),
                            new XAttribute("allocationPercent", r.AllocationPercent)))));

                    te.Add(new XElement("TaskAllocations",
                        t.TaskAllocations.Select(a => new XElement("Allocation",
                            new XAttribute("resource", a.Resource ?? ""),
                            new XAttribute("hours", a.Hours),
                            new XAttribute("tasks", a.Tasks),
                            new XAttribute("state", a.State ?? "")))));

                    tasks.Add(te);
                }
                pe.Add(tasks);
                projects.Add(pe);
            }
            root.Add(projects);

            // Visão gerada, com as opções que cada dia ofereceu.
            var view = new XElement("TimeSheet",
                new XAttribute("rows", TimeSheetRows.Count),
                new XAttribute("totalHours", TimeSheetRows.Sum(r => r.TotalHours)));
            foreach (var r in TimeSheetRows)
            {
                var re = new XElement("Row",
                    new XAttribute("date", r.Date.ToString("yyyy-MM-dd")),
                    new XAttribute("weekDay", r.WeekDayNumber),
                    new XAttribute("activity", r.Activity ?? ""),
                    new XAttribute("carriedOver", r.IsCarriedOver),
                    new XAttribute("sourceProject", r.SourceProject ?? ""),
                    new XAttribute("morningIn", r.MorningIn ?? ""),
                    new XAttribute("morningOut", r.MorningOut ?? ""),
                    new XAttribute("afternoonIn", r.AfternoonIn ?? ""),
                    new XAttribute("afternoonOut", r.AfternoonOut ?? ""),
                    new XAttribute("totalHours", r.TotalHours),
                    new XAttribute("description", r.Description ?? ""),
                    new XAttribute("capexProject", r.CapexProject ?? ""),
                    new XAttribute("pepElement", r.PepElement ?? ""),
                    new XAttribute("manager", r.Manager ?? ""),
                    new XAttribute("attendance", r.Attendance ?? ""),
                    new XAttribute("note", r.Note ?? ""));

                re.Add(new XElement("Options",
                    r.Options.Select(o => new XElement("Option",
                        new XAttribute("label", o.Label ?? ""),
                        new XAttribute("project", o.Project?.Name ?? ""),
                        new XAttribute("hoursPerDay", o.HoursPerDay),
                        new XAttribute("isOwner", o.IsOwner),
                        new XAttribute("carriedOver", o.IsCarriedOver),
                        new XAttribute("selected", ReferenceEquals(o, r.SelectedOption))))));

                view.Add(re);
            }
            root.Add(view);

            return new XDocument(root);
        }

        // ── Abrir base de cálculo: recarrega a visão gravada (somente leitura) ────
        private void OnTimeSheetOpenBaseClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = BaseFileFilter };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var doc  = XDocument.Load(dlg.FileName);
                var root = doc.Root ?? throw new InvalidOperationException("XML vazio.");

                TimeSheetRows.Clear();
                foreach (var re in root.Element("TimeSheet")?.Elements("Row") ?? Enumerable.Empty<XElement>())
                {
                    var row = new TimeSheetRow
                    {
                        Date          = DateTime.TryParse(Attr(re, "date"), CultureInfo.InvariantCulture,
                                            DateTimeStyles.None, out var d) ? d : DateTime.MinValue,
                        WeekDayNumber = int.TryParse(Attr(re, "weekDay"), out var wd) ? wd : 0,
                        MorningIn     = Attr(re, "morningIn"),
                        MorningOut    = Attr(re, "morningOut"),
                        AfternoonIn   = Attr(re, "afternoonIn"),
                        AfternoonOut  = Attr(re, "afternoonOut"),
                        Attendance    = Attr(re, "attendance"),
                        Note          = Attr(re, "note")
                    };

                    // As opções voltam como rótulo (sem vínculo com projeto carregado): a base é
                    // para leitura/diagnóstico, não para reeditar o cronograma.
                    row.Options = (re.Element("Options")?.Elements("Option") ?? Enumerable.Empty<XElement>())
                        .Select(o => new TimeSheetOption
                        {
                            Label         = Attr(o, "label"),
                            HoursPerDay   = double.TryParse(Attr(o, "hoursPerDay"), NumberStyles.Any,
                                                CultureInfo.InvariantCulture, out var hpd) ? hpd : 0,
                            IsOwner       = string.Equals(Attr(o, "isOwner"), "true", StringComparison.OrdinalIgnoreCase),
                            IsCarriedOver = string.Equals(Attr(o, "carriedOver"), "true", StringComparison.OrdinalIgnoreCase)
                        })
                        .ToList();

                    row.Activity      = Attr(re, "activity");
                    row.IsCarriedOver = string.Equals(Attr(re, "carriedOver"), "true", StringComparison.OrdinalIgnoreCase);
                    row.SourceProject = Attr(re, "sourceProject");
                    row.Description   = Attr(re, "description");
                    row.CapexProject  = Attr(re, "capexProject");
                    row.PepElement    = Attr(re, "pepElement");
                    row.Manager       = Attr(re, "manager");

                    TimeSheetRows.Add(row);
                }

                var res   = Attr(root, "resource");
                var start = Attr(root, "monthStart");
                TsTotalText.Text = AppStrings.Get("PMap_TsBaseLoaded", res, start,
                    TimeSheetRows.Sum(r => r.TotalHours));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "NXProject", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string Attr(XElement e, string name) => e.Attribute(name)?.Value ?? "";
    }
}
