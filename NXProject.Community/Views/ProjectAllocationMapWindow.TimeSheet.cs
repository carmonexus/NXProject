using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using ClosedXML.Excel;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>
    /// Aba "Time sheet" do Mapa de Alocação: horas/dia de um recurso num mês, seguindo o
    /// calendário útil do projeto, com a atividade (Feature - Story) mais relevante do dia.
    /// Exporta no layout da planilha de apontamento do cliente.
    /// </summary>
    public partial class ProjectAllocationMapWindow
    {
        // Jornada padrão do apontamento (mesmos horários da planilha modelo).
        private static readonly TimeSpan MorningInDefault    = new(9, 0, 0);
        private static readonly TimeSpan MorningOutDefault   = new(12, 0, 0);
        private static readonly TimeSpan AfternoonInDefault  = new(13, 0, 0);
        private static readonly TimeSpan AfternoonOutDefault = new(18, 0, 0);

        private const string NonProductiveLabel = "FERIADO - NÃO PRODUTIVO";

        internal ObservableCollection<TimeSheetRow> TimeSheetRows { get; } = [];
        private bool _timeSheetInitialized;

        private void InitTimeSheetTab()
        {
            // A lista de recursos é remontada a cada entrada na aba: os projetos podem ter sido
            // importados/abertos depois da primeira visita.
            TsGrid.ItemsSource = TimeSheetRows;
            RefreshTimeSheetResources();

            if (_timeSheetInitialized) return;
            _timeSheetInitialized = true;

            var culture = CultureInfo.CurrentCulture;
            TsMonthCombo.ItemsSource = Enumerable.Range(1, 12)
                .Select(m => culture.DateTimeFormat.GetMonthName(m)).ToList();
            TsMonthCombo.SelectedIndex = DateTime.Today.Month - 1;

            var year = DateTime.Today.Year;
            TsYearCombo.ItemsSource = Enumerable.Range(year - 2, 5).ToList();
            TsYearCombo.SelectedItem = year;

        }

        /// <summary>
        /// Pessoas dos cronogramas carregados: recursos cadastrados no projeto + donos das
        /// tasks no resumo do DevOps. Não filtra por % de conclusão — logo após importar as
        /// Stories ainda estão em 0% e a lista sairia vazia.
        /// </summary>
        private void RefreshTimeSheetResources()
        {
            var previous = TsResourceCombo.SelectedItem as string;

            var names = _projects
                .SelectMany(p => p.Data.Resources.Select(r => r.Name)
                    .Concat(AllTasks(p.Data.Tasks).SelectMany(t =>
                        t.Resources.Select(r => r.Resource?.Name)
                         .Concat(t.TaskAllocations.Select(a => a.Resource)))))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            TsResourceCombo.ItemsSource = names;
            if (previous != null && names.Contains(previous, StringComparer.OrdinalIgnoreCase))
                TsResourceCombo.SelectedItem = names.First(n => string.Equals(n, previous, StringComparison.OrdinalIgnoreCase));
            else if (names.Count > 0)
                TsResourceCombo.SelectedIndex = 0;
        }

        private static IEnumerable<ProjectTask> AllTasks(IEnumerable<ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                yield return t;
                foreach (var c in AllTasks(t.Children)) yield return c;
            }
        }

        private (DateTime Start, DateTime End)? SelectedTimeSheetMonth()
        {
            if (TsMonthCombo.SelectedIndex < 0 || TsYearCombo.SelectedItem is not int year) return null;
            var start = new DateTime(year, TsMonthCombo.SelectedIndex + 1, 1);
            return (start, start.AddMonths(1).AddDays(-1));
        }

        // Trocar recurso, mês/ano ou o modo de preenchimento invalida o que está na tela:
        // limpa para não confundir o apontamento de uma pessoa com o de outra.
        private void OnTimeSheetFilterChanged(object sender, RoutedEventArgs e)
        {
            if (!_timeSheetInitialized) return;
            ClearTimeSheet();
        }

        private void OnTimeSheetClearClick(object sender, RoutedEventArgs e) => ClearTimeSheet();

        private void ClearTimeSheet()
        {
            TimeSheetRows.Clear();
            if (TsTotalText != null) TsTotalText.Text = "";
        }

        private void OnTimeSheetGenerateClick(object sender, RoutedEventArgs e)
        {
            var resource = TsResourceCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(resource) || SelectedTimeSheetMonth() is not { } month)
            {
                MessageBox.Show(this, AppStrings.Get("PMap_TsPickResource"), "NXProject",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BuildTimeSheet(resource!, month.Start, month.End);
        }

        private void BuildTimeSheet(string resource, DateTime monthStart, DateTime monthEnd)
        {
            TimeSheetRows.Clear();

            var attendance = TsAttendanceBox.Text?.Trim() ?? "";
            double total   = 0;

            // Projetos ordenados pelo HH do recurso NO MÊS: quando o dia não tem atividade,
            // a busca pela anterior começa pelo projeto onde ele mais trabalhou no período.
            var projectsByLoad = ProjectsByResourceLoad(resource, monthStart, monthEnd);

            for (var day = monthStart; day <= monthEnd; day = day.AddDays(1))
            {
                var row = new TimeSheetRow
                {
                    Date          = day,
                    WeekDayNumber = (int)day.DayOfWeek + 1   // domingo = 1, como na planilha
                };

                if (!ProjectCalendarService.IsWorkingDay(day))
                {
                    // Dia não útil (fim de semana ou feriado do calendário): linha sem horas.
                    row.Activity    = NonProductiveLabel;
                    row.Description = "-";
                    row.CapexProject = "-";
                    row.PepElement   = "-";
                    TimeSheetRows.Add(row);
                    continue;
                }

                // Candidatos do dia: melhor atividade de CADA cronograma carregado. O primeiro
                // (maior HH/dia) entra selecionado; a combo da grade permite trocar de cronograma.
                var options = DayOptions(resource, day, projectsByLoad, TsFillGapsBox.IsChecked == true);
                row.Options      = options;
                row.Attendance   = attendance;
                row.MorningIn    = FormatTime(MorningInDefault);
                row.MorningOut   = FormatTime(MorningOutDefault);
                row.AfternoonIn  = FormatTime(AfternoonInDefault);
                row.AfternoonOut = FormatTime(AfternoonOutDefault);
                row.SelectedOption = options.FirstOrDefault();

                total += row.TotalHours;
                TimeSheetRows.Add(row);
            }

            TsTotalText.Text = AppStrings.Get("PMap_TsTotal", total);
        }

        /// <summary>
        /// Opções de atividade para o dia — a melhor de cada cronograma carregado, ordenadas
        /// pelo HH/dia. Quando nenhum cronograma tem atividade no dia e o preenchimento de gap
        /// está ligado, entram as atividades anteriores mais recentes (marcadas como herdadas).
        /// </summary>
        private List<TimeSheetOption> DayOptions(
            string resource, DateTime day, List<LoadedProject> projectsByLoad, bool fillGaps)
        {
            var options = new List<TimeSheetOption>();

            foreach (var proj in projectsByLoad)
            {
                ProjectTask? best = null;
                double bestHoursPerDay = -1;

                foreach (var task in GetChargeableTasks(proj.Data.Tasks))
                {
                    if (day.Date < task.Start.Date || day.Date > task.Finish.Date) continue;
                    if (!ResourceWorksOn(task, resource)) continue;

                    var hours = TotalHoursOf(task);
                    var days  = Math.Max(1, ProjectCalendarService.CountWorkingHours(
                        task.Start.Date, task.Finish.Date.AddDays(1)) / Math.Max(1, ProjectCalendarService.WorkingHoursPerDay));
                    var perDay = hours / days;

                    if (perDay > bestHoursPerDay)
                    {
                        bestHoursPerDay = perDay;
                        best = task;
                    }
                }

                if (best != null)
                    options.Add(MakeOption(best, proj, bestHoursPerDay, carried: false));
            }

            if (options.Count > 0)
                return options.OrderByDescending(o => o.HoursPerDay).ToList();

            // Nenhum cronograma tem atividade no dia: cai na anterior mais recente de cada um,
            // na ordem de HH do recurso no mês.
            if (!fillGaps) return options;

            foreach (var proj in projectsByLoad)
            {
                ProjectTask? candidate = null;
                DateTime bestFinish = DateTime.MinValue;

                foreach (var task in GetChargeableTasks(proj.Data.Tasks))
                {
                    if (task.Finish.Date >= day.Date) continue;
                    if (!ResourceWorksOn(task, resource)) continue;
                    if (task.Finish.Date <= bestFinish) continue;

                    bestFinish = task.Finish.Date;
                    candidate  = task;
                }

                if (candidate != null)
                    options.Add(MakeOption(candidate, proj, 0, carried: true));
            }

            return options;
        }

        private static TimeSheetOption MakeOption(ProjectTask task, LoadedProject proj, double hoursPerDay, bool carried)
        {
            var story   = IsStoryNode(task) ? task : FindParentStory(task);
            var feature = FindAncestorOfType(task, "Feature");
            var label   = feature != null && story != null
                ? $"{feature.Name} - {story.Name}"
                : story?.Name ?? task.Name ?? "";

            return new TimeSheetOption
            {
                Label         = label,
                Project       = proj,
                HoursPerDay   = hoursPerDay,
                IsCarriedOver = carried
            };
        }

        /// <summary>
        /// Projetos carregados ordenados pelo HH do recurso dentro do mês (maior primeiro).
        /// Empata pelos que não têm horas no período, ao fim da lista.
        /// </summary>
        private List<LoadedProject> ProjectsByResourceLoad(string resource, DateTime monthStart, DateTime monthEnd)
        {
            var monthEndEx = monthEnd.Date.AddDays(1);

            double HoursInMonth(LoadedProject proj)
            {
                double sum = 0;
                foreach (var task in GetChargeableTasks(proj.Data.Tasks))
                {
                    if (!ResourceWorksOn(task, resource)) continue;
                    if (task.Finish.Date < monthStart.Date || task.Start.Date >= monthEndEx) continue;

                    var taskHours = ProjectCalendarService.CountWorkingHours(task.Start.Date, task.Finish.Date.AddDays(1));
                    if (taskHours <= 0) { sum += TotalHoursOf(task); continue; }

                    var from = task.Start.Date > monthStart.Date ? task.Start.Date : monthStart.Date;
                    var to   = task.Finish.Date.AddDays(1) < monthEndEx ? task.Finish.Date.AddDays(1) : monthEndEx;
                    var overlap = ProjectCalendarService.CountWorkingHours(from, to);
                    sum += TotalHoursOf(task) * (overlap / taskHours);
                }
                return sum;
            }

            return _projects
                .Select(p => (Project: p, Hours: HoursInMonth(p)))
                .OrderByDescending(x => x.Hours)
                .Select(x => x.Project)
                .ToList();
        }

        private static bool ResourceWorksOn(ProjectTask task, string resource)
            => task.Resources.Any(r => string.Equals(r.Resource?.Name, resource, StringComparison.OrdinalIgnoreCase))
               || task.TaskAllocations.Any(a => string.Equals(a.Resource, resource, StringComparison.OrdinalIgnoreCase));

        private static double TotalHoursOf(ProjectTask task)
            => (task.CurrentHours ?? 0) + (task.EstimatedHours ?? 0);

        private static ProjectTask? FindAncestorOfType(ProjectTask task, string type)
        {
            for (var p = task.Parent; p != null; p = p.Parent)
                if (string.Equals(p.TfsType?.Trim(), type, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        private static string FormatTime(TimeSpan t) => $"{t.Hours:00}:{t.Minutes:00}";

        // ── Exportação no layout da planilha de apontamento ───────────────────────
        private void OnTimeSheetExportClick(object sender, RoutedEventArgs e)
        {
            if (TimeSheetRows.Count == 0)
            {
                MessageBox.Show(this, AppStrings.Get("PMap_TsGenerateFirst"), "NXProject",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (SelectedTimeSheetMonth() is not { } month) return;

            var resource = TsResourceCombo.SelectedItem as string ?? "";
            var monthName = CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month.Start.Month);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter   = "Planilha do Excel (*.xlsx)|*.xlsx",
                FileName = $"Time sheet - {resource} {monthName} {month.Start.Year}.xlsx"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                ExportTimeSheet(dlg.FileName, resource, month.Start, month.End);
                MessageBox.Show(this, AppStrings.Get("PMap_TsExported", dlg.FileName), "NXProject",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "NXProject", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportTimeSheet(string path, string resource, DateTime monthStart, DateTime monthEnd)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("TIMESHEET");

            // Cabeçalho (mesmo bloco do modelo do cliente).
            ws.Cell("C3").Value = "Período de:";
            ws.Cell("D3").Value = monthStart;
            ws.Cell("D3").Style.DateFormat.Format = "dd/MM/yyyy";
            ws.Cell("C4").Value = "à";
            ws.Cell("D4").Value = monthEnd;
            ws.Cell("D4").Style.DateFormat.Format = "dd/MM/yyyy";
            ws.Cell("F4").Value = "Cliente:";
            ws.Cell("G4").Value = TfsConnectionStore.Load("NXProject.Community").CompanyName ?? "";
            ws.Cell("I4").Value = "Projeto:";
            ws.Cell("L4").Value = _projects.FirstOrDefault()?.Name ?? "";
            ws.Cell("F3").Value = "Recurso:";
            ws.Cell("G3").Value = resource;

            string[] headers =
            [
                "Dia", "Dia Semana", "Atividades", "Entrada Manhã", "Saída Manhã",
                "Entrada Tarde", "Saída Tarde", "Total", "Descrição do Projeto ou Chamado",
                "Projeto Capex", "Elemento Pep", "Gestor Responsável ArcelorMittal",
                "Atendimento", "Observação"
            ];
            for (int c = 0; c < headers.Length; c++)
            {
                var cell = ws.Cell(6, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9E1F2");
                cell.Style.Alignment.WrapText = true;
            }

            int row = 7;
            foreach (var r in TimeSheetRows)
            {
                ws.Cell(row, 1).Value = r.Date;
                ws.Cell(row, 1).Style.DateFormat.Format = "dd/MM/yyyy";
                ws.Cell(row, 2).Value = r.WeekDayNumber;
                ws.Cell(row, 3).Value = r.Activity;

                if (r.TotalHours > 0)
                {
                    SetTimeCell(ws.Cell(row, 4), r.MorningIn);
                    SetTimeCell(ws.Cell(row, 5), r.MorningOut);
                    SetTimeCell(ws.Cell(row, 6), r.AfternoonIn);
                    SetTimeCell(ws.Cell(row, 7), r.AfternoonOut);
                }

                // Total em fração de dia, com formato de horas — igual ao modelo.
                ws.Cell(row, 8).Value = r.TotalHours / 24.0;
                ws.Cell(row, 8).Style.DateFormat.Format = "[h]:mm";

                ws.Cell(row, 9).Value  = r.Description;
                ws.Cell(row, 10).Value = r.CapexProject;
                ws.Cell(row, 11).Value = r.PepElement;
                ws.Cell(row, 12).Value = r.Manager;
                ws.Cell(row, 13).Value = r.Attendance;
                ws.Cell(row, 14).Value = r.Note;
                // Mesma marcação da tela: atividade herdada de um dia anterior sai em negrito.
                if (r.IsCarriedOver)
                    ws.Range(row, 1, row, 14).Style.Font.Bold = true;
                row++;
            }

            var totalCell = ws.Cell(row, 8);
            totalCell.FormulaA1 = $"SUM(H7:H{row - 1})";
            totalCell.Style.Font.Bold = true;
            totalCell.Style.DateFormat.Format = "[h]:mm";

            ws.Columns(1, 14).AdjustToContents();
            ws.Column(3).Width  = 42;
            ws.Column(9).Width  = 38;
            ws.Column(10).Width = 28;
            ws.SheetView.FreezeRows(6);

            wb.SaveAs(path);
        }

        private static void SetTimeCell(IXLCell cell, string text)
        {
            if (TimeSpan.TryParse(text, out var t))
            {
                cell.Value = t.TotalHours / 24.0;
                cell.Style.DateFormat.Format = "hh:mm";
            }
            else cell.Value = text;
        }

        /// <summary>Opção de atividade para um dia — uma por cronograma carregado.</summary>
        internal sealed class TimeSheetOption
        {
            public string Label { get; set; } = "";
            public LoadedProject? Project { get; set; }
            public double HoursPerDay { get; set; }
            public bool IsCarriedOver { get; set; }

            /// <summary>Texto da combo: atividade + cronograma (e aviso quando é herdada).</summary>
            public string Display => Project == null
                ? Label
                : $"{Label}  ·  {Project.Name}{(IsCarriedOver ? "  (dia anterior)" : "")}";
        }

        /// <summary>Uma linha (um dia) do apontamento.</summary>
        internal sealed class TimeSheetRow : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            private void Raise(string name) =>
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

            public DateTime Date { get; set; }
            public int WeekDayNumber { get; set; }
            public string DayText => Date.ToString("dd/MM/yyyy");

            /// <summary>Atividades possíveis no dia (uma por cronograma).</summary>
            public List<TimeSheetOption> Options { get; set; } = [];
            public bool HasOptions => Options.Count > 0;

            private TimeSheetOption? _selectedOption;
            /// <summary>Trocar a opção reescreve atividade, cronograma, Capex, PEP e gestor.</summary>
            public TimeSheetOption? SelectedOption
            {
                get => _selectedOption;
                set
                {
                    _selectedOption = value;
                    Activity      = value?.Label ?? "";
                    IsCarriedOver = value?.IsCarriedOver ?? false;
                    SourceProject = value?.Project?.Name ?? "";
                    Description   = value?.Project?.Data.Name ?? "";
                    CapexProject  = value?.Project?.Data.PepProjectName ?? "";
                    PepElement    = value?.Project?.Data.PepElement ?? "";
                    Manager       = value?.Project?.Data.DevOpsProjectOwner ?? "";
                    Raise(nameof(SelectedOption));
                }
            }

            private string _activity = "";
            public string Activity { get => _activity; set { _activity = value; Raise(nameof(Activity)); } }

            public string MorningIn    { get; set; } = "";
            public string MorningOut   { get; set; } = "";
            public string AfternoonIn  { get; set; } = "";
            public string AfternoonOut { get; set; } = "";

            private string _description = "";
            public string Description { get => _description; set { _description = value; Raise(nameof(Description)); } }

            private string _capexProject = "";
            public string CapexProject { get => _capexProject; set { _capexProject = value; Raise(nameof(CapexProject)); } }

            private string _pepElement = "";
            public string PepElement { get => _pepElement; set { _pepElement = value; Raise(nameof(PepElement)); } }

            private string _manager = "";
            public string Manager { get => _manager; set { _manager = value; Raise(nameof(Manager)); } }

            public string Attendance { get; set; } = "";
            public string Note       { get; set; } = "";

            private bool _isCarriedOver;
            /// <summary>Atividade herdada de um dia anterior (não havia atividade no dia).</summary>
            public bool IsCarriedOver { get => _isCarriedOver; set { _isCarriedOver = value; Raise(nameof(IsCarriedOver)); } }

            private string _sourceProject = "";
            /// <summary>Cronograma de onde veio a atividade — confere quando há vários abertos.</summary>
            public string SourceProject { get => _sourceProject; set { _sourceProject = value; Raise(nameof(SourceProject)); } }

            /// <summary>Horas do dia = manhã + tarde, pelos horários informados.</summary>
            public double TotalHours
            {
                get
                {
                    double h = 0;
                    if (TimeSpan.TryParse(MorningIn, out var mi) && TimeSpan.TryParse(MorningOut, out var mo) && mo > mi)
                        h += (mo - mi).TotalHours;
                    if (TimeSpan.TryParse(AfternoonIn, out var ai) && TimeSpan.TryParse(AfternoonOut, out var ao) && ao > ai)
                        h += (ao - ai).TotalHours;
                    return h;
                }
            }

            public string TotalText => TotalHours > 0 ? $"{TotalHours:0.##}h" : "-";
        }
    }
}
