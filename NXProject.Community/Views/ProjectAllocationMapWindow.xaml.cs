using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class ProjectAllocationMapWindow : Window
    {
        // ── Modelo interno ────────────────────────────────────────────────────
        private sealed class LoadedProject
        {
            public string  FilePath         { get; set; } = string.Empty;
            public string  Name             { get; set; } = string.Empty;
            public bool    IsOpex           { get; set; } = true;
            public string  CostCenter       { get; set; } = string.Empty;
            // "CAPEX", "OPEX" ou "EPIC" (lê de cada EPIC da hierarquia).
            public string  CostCenterSource { get; set; } = string.Empty;
            public Project Data             { get; set; } = null!;

            // Retorna OPEX/CAPEX para uma story, considerando o EPIC pai quando CostCenterSource=EPIC.
            public bool IsOpexForTask(ProjectTask task)
            {
                if (CostCenterSource.Equals("EPIC", StringComparison.OrdinalIgnoreCase))
                {
                    var epicTipo = FindEpicAncestor(task)?.TipoCentroCusto?.ToUpperInvariant();
                    if (epicTipo == "CAPEX") return false;
                    if (epicTipo == "OPEX") return true;
                    // DEFINIDO_NO_PROJETO, nulo ou valor desconhecido → fallback para configuração do projeto
                }
                return IsOpex;
            }

            private static ProjectTask? FindEpicAncestor(ProjectTask task)
            {
                var current = task.Parent;
                while (current != null)
                {
                    if (string.Equals(current.TfsType, "Epic", StringComparison.OrdinalIgnoreCase))
                        return current;
                    current = current.Parent;
                }
                return null;
            }
        }

        // ── Estado ────────────────────────────────────────────────────────────
        private List<LoadedProject> _projects = [];
        private List<DateTime>      _months   = [];

        private const double RowHeight   = 26;
        private const double ColWidth    = 90;
        private const double LeftWidth   = 220; // projeto
        private const double ResWidth    = 160; // recurso
        private const double TypeWidth   = 80;
        private const double CcWidth     = 120;
        private const double TotalColW   = 90;

        // ── Construtor ────────────────────────────────────────────────────────
        public ProjectAllocationMapWindow()
        {
            InitializeComponent();
            PopulateMonthCombos();

            var opts = TfsConnectionStore.Load();
            LoadProjects(opts);

            Loaded += (_, _) => BuildGrid();
        }

        // ── Período ───────────────────────────────────────────────────────────
        private void PopulateMonthCombos()
        {
            var start = new DateTime(DateTime.Today.Year, 1, 1);
            for (int i = 0; i < 36; i++)
            {
                var m = start.AddMonths(i);
                StartMonthBox.Items.Add(new MonthItem(m));
                EndMonthBox.Items.Add(new MonthItem(m));
            }
            StartMonthBox.SelectedIndex = 0;
            EndMonthBox.SelectedIndex   = 11;
        }

        private sealed class MonthItem(DateTime month)
        {
            public DateTime Month { get; } = month;
            public override string ToString() => Month.ToString("MMM/yyyy");
        }

        private (DateTime Start, DateTime End) GetPeriod()
        {
            var s = (StartMonthBox.SelectedItem as MonthItem)?.Month ?? new DateTime(DateTime.Today.Year, 1, 1);
            var e = (EndMonthBox.SelectedItem   as MonthItem)?.Month ?? new DateTime(DateTime.Today.Year, 12, 1);
            if (e < s) e = s;
            return (s, new DateTime(e.Year, e.Month, DateTime.DaysInMonth(e.Year, e.Month)));
        }

        private static List<DateTime> BuildMonths(DateTime start, DateTime end)
        {
            var list = new List<DateTime>();
            var cur  = new DateTime(start.Year, start.Month, 1);
            var last = new DateTime(end.Year,   end.Month,   1);
            while (cur <= last) { list.Add(cur); cur = cur.AddMonths(1); }
            return list;
        }

        // ── Carregamento ──────────────────────────────────────────────────────
        private void LoadProjects(TfsConnectionOptions opts)
        {
            _projects = [];
            foreach (var cfg in opts.PortfolioProjectConfigs)
            {
                if (string.IsNullOrWhiteSpace(cfg.FilePath) || !System.IO.File.Exists(cfg.FilePath))
                    continue;
                try
                {
                    var project = XmlProjectService.Load(cfg.FilePath);
                    _projects.Add(new LoadedProject
                    {
                        FilePath   = cfg.FilePath,
                        Name       = !string.IsNullOrWhiteSpace(cfg.ProjectName)
                                     ? cfg.ProjectName
                                     : (!string.IsNullOrWhiteSpace(project.Name)
                                         ? project.Name
                                         : System.IO.Path.GetFileNameWithoutExtension(cfg.FilePath)),
                        IsOpex           = cfg.IsOpex,
                        CostCenter       = cfg.CostCenter,
                        CostCenterSource = string.IsNullOrWhiteSpace(cfg.CostCenterSource)
                                           ? (cfg.IsOpex ? "OPEX" : "CAPEX")
                                           : cfg.CostCenterSource,
                        Data       = project
                    });
                }
                catch { /* arquivo inválido — ignora */ }
            }
        }

        // ── Horas ─────────────────────────────────────────────────────────────
        private static IEnumerable<ProjectTask> GetLeafTasks(IEnumerable<ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                if (t.Children.Count == 0) yield return t;
                else foreach (var c in GetLeafTasks(t.Children)) yield return c;
            }
        }

        // ── Crédito de horas: Story cheia + Task de outra pessoa ──────────────
        // A Story conta CHEIA para quem responde por ela, mesmo tendo Tasks filhas — quem
        // responde pela Story não perde horas por delegar, pois ainda precisa revisar o que
        // a outra pessoa fez. As Tasks filhas entram como crédito ADITIVO, e só para quem
        // NÃO é responsável da Story (senão as horas dela seriam contadas duas vezes).
        private static IEnumerable<ProjectTask> GetChargeableTasks(IEnumerable<ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                if (IsStoryNode(t))
                {
                    yield return t;                                                  // Story conta cheia
                    foreach (var c in GetChargeableTasks(t.Children)) yield return c; // + Tasks filhas
                }
                else if (t.Children.Count == 0) yield return t;
                else foreach (var c in GetChargeableTasks(t.Children)) yield return c;
            }
        }

        private static bool IsStoryNode(ProjectTask t)
            => TfsImportService.IsStoryTypePublic(t.TfsType);

        private static ProjectTask? FindParentStory(ProjectTask task)
        {
            var p = task.Parent;
            while (p != null)
            {
                if (IsStoryNode(p)) return p;
                p = p.Parent;
            }
            return null;
        }

        /// <summary>Task filha de Story só credita horas para quem NÃO responde pela Story
        /// (a Story já conta cheia para o responsável dela). Story e itens fora de Story
        /// creditam sempre.</summary>
        private static bool CreditsResource(ProjectTask task, string? resourceName)
        {
            if (string.IsNullOrWhiteSpace(resourceName)) return false;
            if (IsStoryNode(task)) return true;
            var story = FindParentStory(task);
            if (story == null) return true;
            return !story.Resources.Any(r =>
                string.Equals(r.Resource?.Name, resourceName, StringComparison.OrdinalIgnoreCase));
        }

        private bool OnlyCurrentHours => OnlyCurrentHoursBox?.IsChecked == true;

        private static double GetRemainingHours(ProjectTask task, TaskResource tr) =>
            NXProject.Services.TaskScheduleService.GetAssignmentRemainingHours(task, tr);

        private static double GetCurrentHoursForAssignment(ProjectTask task, TaskResource tr) =>
            NXProject.Services.TaskScheduleService.GetAssignmentCurrentHours(task, tr);

        private static double AllocateHoursInPeriod(double hours, DateTime start, DateTime end, DateTime monthStart, DateTime monthEnd)
        {
            if (hours <= 0) return 0;
            var tStart = start.Date;
            var tEnd = end.Date;
            if (tEnd < monthStart || tStart > monthEnd) return 0;

            if (tStart >= tEnd)
                return monthStart <= tStart && tStart <= monthEnd ? hours : 0;

            var overlapStart = tStart < monthStart ? monthStart : tStart;
            var overlapEnd = tEnd > monthEnd ? monthEnd : tEnd;
            double overlapDays = Math.Max(0, (overlapEnd - overlapStart).TotalDays + 1);
            double totalDays = Math.Max(1, (tEnd - tStart).TotalDays + 1);
            return hours * (overlapDays / totalDays);
        }

        private static DateTime GetCurrentHoursEnd(ProjectTask task)
        {
            if (task.PercentComplete >= 100)
                return task.Finish.Date;

            var today = DateTime.Today;
            if (today < task.Start.Date)
                return task.Start.Date;
            if (today > task.Finish.Date)
                return task.Finish.Date;
            return today;
        }

        private static double ComputeHours(Project project, string resourceName,
                                            DateTime monthStart, DateTime monthEnd,
                                            bool onlyCurrentHours = false)
        {
            double total = 0;
            foreach (var task in GetChargeableTasks(project.Tasks))
            {
                if (!CreditsResource(task, resourceName)) continue;
                foreach (var tr in task.Resources)
                {
                    if (!string.Equals(tr.Resource?.Name, resourceName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    total += ComputeHoursForTask(task, tr, monthStart, monthEnd, onlyCurrentHours);
                }
            }
            return total;
        }

        // ── Construção do grid ────────────────────────────────────────────────
        private void BuildGrid()
        {
            ClearPanels();

            if (_projects.Count == 0)
            {
                StatusText.Text = AppStrings.Get("PMap_NoProjectSelected");
                return;
            }

            var (periodStart, periodEnd) = GetPeriod();
            _months = BuildMonths(periodStart, periodEnd);

            bool hideZero = OnlyWithHoursBox.IsChecked == true;

            // Pré-computa: para cada projeto, quais recursos têm horas no período
            // data[pi][resourceName][mi] = horas
            var projResourceData = new List<(LoadedProject Proj,
                                             List<(string Res, double[] MonthHours)> Rows)>();
            var allResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var proj in _projects)
            {
                var resources = proj.Data.Resources
                    .Where(r => r.Type == ResourceType.Work && r.Kind == ResourceKind.Project && !string.IsNullOrWhiteSpace(r.Name))
                    .Select(r => r.Name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .ToList();

                var rows = new List<(string Res, double[] MonthHours)>();
                foreach (var res in resources)
                {
                    var monthHours = new double[_months.Count];
                    for (int mi = 0; mi < _months.Count; mi++)
                    {
                        var mStart = _months[mi];
                        var mEnd   = new DateTime(mStart.Year, mStart.Month,
                                         DateTime.DaysInMonth(mStart.Year, mStart.Month));
                        monthHours[mi] = ComputeHours(proj.Data, res, mStart, mEnd, OnlyCurrentHours);
                    }

                    double rowTotal = monthHours.Sum();
                    if (hideZero && rowTotal < 0.01) continue;

                    rows.Add((res, monthHours));
                    allResources.Add(res);
                }

                projResourceData.Add((proj, rows));
            }

            // Quais meses têm dados (para ocultar colunas zeradas)
            var visibleMonths = new List<int>();
            for (int mi = 0; mi < _months.Count; mi++)
            {
                bool hasData = projResourceData.Any(p => p.Rows.Any(r => r.MonthHours[mi] > 0.01));
                if (!hideZero || hasData) visibleMonths.Add(mi);
            }

            BuildHeaders(visibleMonths);
            BuildRows(projResourceData, visibleMonths, hideZero);

            int totalResources = allResources.Count;
            double grandTotal  = projResourceData.Sum(p => p.Rows.Sum(r => r.MonthHours.Sum()));
            StatusText.Text = $"{_projects.Count} projeto(s)  ·  {totalResources} recurso(s) com alocação  ·  " +
                              $"{_months.Count} mês(es)  ·  Total geral: {grandTotal:0.#}h";
        }

        private void ClearPanels()
        {
            MonthHeaderPanel.Items.Clear();
            ResourceHeaderPanel.Items.Clear();
            ProjectNamePanel.Items.Clear();
            ProjectResPanel.Items.Clear();
            ProjectTypePanel.Items.Clear();
            ProjectCcPanel.Items.Clear();
            DataRowsPanel.Items.Clear();
        }

        // ── Cabeçalhos ────────────────────────────────────────────────────────
        private void BuildHeaders(List<int> visibleMonths)
        {
            // Linha 1 (MonthHeaderPanel): meses
            foreach (var mi in visibleMonths)
            {
                MonthHeaderPanel.Items.Add(new Border
                {
                    Width           = ColWidth,
                    Height          = 22,
                    Background      = new SolidColorBrush(Color.FromRgb(43, 87, 154)),
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(29, 63, 115)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Child           = new TextBlock
                    {
                        Text                = _months[mi].ToString("MMM/yy"),
                        Foreground          = Brushes.White,
                        FontWeight          = FontWeights.SemiBold,
                        FontSize            = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center
                    }
                });
            }
            // Coluna TOTAL
            MonthHeaderPanel.Items.Add(new Border
            {
                Width           = TotalColW,
                Height          = 22,
                Background      = new SolidColorBrush(Color.FromRgb(25, 60, 120)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(29, 63, 115)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child           = new TextBlock
                {
                    Text                = "TOTAL",
                    Foreground          = Brushes.White,
                    FontWeight          = FontWeights.SemiBold,
                    FontSize            = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center
                }
            });

            // Linha 2 (ResourceHeaderPanel): sub-header vazio (alinha com colunas de mês)
            int totalCols = visibleMonths.Count + 1;
            ResourceHeaderPanel.Items.Add(new Border
            {
                Width           = totalCols * ColWidth,
                Height          = 20,
                Background      = new SolidColorBrush(Color.FromRgb(232, 238, 248)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(197, 208, 224)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child           = new TextBlock
                {
                    Text       = "← horas alocadas mensalmente →",
                    FontSize   = 10,
                    FontStyle  = FontStyles.Italic,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 120, 160)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center
                }
            });
        }

        // ── Linhas de dados ───────────────────────────────────────────────────
        private void BuildRows(
            List<(LoadedProject Proj, List<(string Res, double[] MonthHours)> Rows)> projData,
            List<int> visibleMonths,
            bool hideZero)
        {
            // Totais por mês (para linha de rodapé)
            var grandByMonth = new double[_months.Count];

            foreach (var (proj, resRows) in projData)
            {
                // ── Cabeçalho do projeto ──────────────────────────────────────
                var projBg = new SolidColorBrush(Color.FromRgb(224, 232, 250));

                ProjectNamePanel.Items.Add(MakeCell(proj.Name, LeftWidth, RowHeight + 2, projBg,
                    bold: true, tooltip: proj.FilePath, leftPad: 6));
                ProjectResPanel.Items.Add(MakeCell("", ResWidth, RowHeight + 2, projBg));
                ProjectTypePanel.Items.Add(MakeTypeCell(proj, projBg));
                ProjectCcPanel.Items.Add(MakeCcCell(proj, projBg));

                // Totais do projeto por mês
                var projByMonth = new double[_months.Count];
                foreach (var (_, mh) in resRows)
                    for (int mi = 0; mi < _months.Count; mi++)
                        projByMonth[mi] += mh[mi];

                double projTotal = projByMonth.Sum();
                DataRowsPanel.Items.Add(BuildDataRow(projByMonth, visibleMonths, projTotal, projBg, bold: true, months: _months));

                for (int mi = 0; mi < _months.Count; mi++)
                    grandByMonth[mi] += projByMonth[mi];

                // ── Sub-linhas por recurso ────────────────────────────────────
                for (int ri = 0; ri < resRows.Count; ri++)
                {
                    var (resName, monthHours) = resRows[ri];
                    var resBg = new SolidColorBrush(ri % 2 == 0
                        ? Color.FromRgb(255, 255, 255)
                        : Color.FromRgb(248, 250, 255));

                    ProjectNamePanel.Items.Add(MakeCell("", LeftWidth, RowHeight, resBg));
                    ProjectResPanel.Items.Add(new Border
                    {
                        Width           = ResWidth,
                        Height          = RowHeight,
                        Background      = resBg,
                        BorderBrush     = new SolidColorBrush(Color.FromRgb(220, 228, 240)),
                        BorderThickness = new Thickness(0, 0, 1, 1),
                        Padding         = new Thickness(18, 0, 4, 0),
                        ToolTip         = resName,
                        Child           = new TextBlock
                        {
                            Text              = resName,
                            FontSize          = 11,
                            Foreground        = new SolidColorBrush(Color.FromRgb(50, 90, 160)),
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming      = TextTrimming.CharacterEllipsis
                        }
                    });
                    ProjectTypePanel.Items.Add(MakeCell("", TypeWidth, RowHeight, resBg));
                    ProjectCcPanel.Items.Add(MakeCell("", CcWidth, RowHeight, resBg));

                    double resTotal = 0;
                    var resRow = new StackPanel { Orientation = Orientation.Horizontal };
                    foreach (var mi in visibleMonths)
                    {
                        double h = monthHours[mi];
                        resTotal += h;
                        var mStart = _months[mi];
                        var mEnd   = new DateTime(mStart.Year, mStart.Month,
                                         DateTime.DaysInMonth(mStart.Year, mStart.Month));
                        resRow.Children.Add(MakeHoursCellClickable(h, ColWidth, RowHeight,
                            resBg, proj, resName, mStart, mEnd));
                    }
                    resRow.Children.Add(MakeTotalCell(resTotal, TotalColW, RowHeight));
                    DataRowsPanel.Items.Add(resRow);
                }

                // Separador
                var sep = new Border { Height = 3, Background = new SolidColorBrush(Color.FromRgb(180, 200, 230)) };
                ProjectNamePanel.Items.Add(new Border { Height = 3, Background = sep.Background });
                ProjectResPanel.Items.Add(new Border  { Height = 3, Background = sep.Background });
                ProjectTypePanel.Items.Add(new Border { Height = 3, Background = sep.Background });
                ProjectCcPanel.Items.Add(new Border   { Height = 3, Background = sep.Background });
                DataRowsPanel.Items.Add(sep);
            }

            // ── Linha TOTAL GERAL ─────────────────────────────────────────────
            var totalBg = new SolidColorBrush(Color.FromRgb(43, 87, 154));
            ProjectNamePanel.Items.Add(MakeCell("TOTAL GERAL", LeftWidth, RowHeight + 2, totalBg,
                bold: true, fg: Colors.White, leftPad: 6));
            ProjectResPanel.Items.Add(MakeCell("", ResWidth, RowHeight + 2, totalBg));
            ProjectTypePanel.Items.Add(MakeCell("", TypeWidth, RowHeight + 2, totalBg));
            ProjectCcPanel.Items.Add(MakeCell("", CcWidth, RowHeight + 2, totalBg));

            double grandTotal = 0;
            var grandRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var mi in visibleMonths)
            {
                grandTotal += grandByMonth[mi];
                grandRow.Children.Add(MakeTotalCell(grandByMonth[mi], ColWidth, RowHeight + 2,
                    bold: true, fg: Colors.White, bg: Color.FromRgb(43, 87, 154)));
            }
            grandRow.Children.Add(MakeTotalCell(grandTotal, TotalColW, RowHeight + 2,
                bold: true, fg: Colors.White, bg: Color.FromRgb(25, 60, 120)));
            DataRowsPanel.Items.Add(grandRow);
        }

        // ── Linha de totais do projeto (sem coluna de recurso — está na coluna fixa) ──
        private static StackPanel BuildDataRow(double[] byMonth, List<int> visibleMonths,
            double rowTotal, SolidColorBrush rowBg, bool bold, List<DateTime>? months = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var mi in visibleMonths)
            {
                var mStart = months != null && mi < months.Count ? months[mi] : (DateTime?)null;
                row.Children.Add(MakeHoursCell(byMonth[mi], ColWidth, RowHeight + 2, rowBg, bold: bold, monthStart: mStart));
            }
            row.Children.Add(MakeTotalCell(rowTotal, TotalColW, RowHeight + 2, bold: bold));
            return row;
        }

        // ── Célula OPEX/CAPEX (leitura — configurado no picker) ──────────────
        private static UIElement MakeTypeCell(LoadedProject proj, Brush bg)
        {
            return new Border
            {
                Width           = TypeWidth,
                Height          = RowHeight + 2,
                Background      = bg,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(180, 200, 230)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding         = new Thickness(4, 0, 4, 0),
                Child           = new TextBlock
                {
                    Text              = proj.IsOpex ? "OPEX" : "CAPEX",
                    FontSize          = 11,
                    FontWeight        = FontWeights.SemiBold,
                    Foreground        = proj.IsOpex
                        ? new SolidColorBrush(Color.FromRgb(0, 100, 0))
                        : new SolidColorBrush(Color.FromRgb(140, 60, 0)),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        // ── Célula Centro de Custo (leitura — configurado no picker) ─────────
        private static UIElement MakeCcCell(LoadedProject proj, Brush bg)
        {
            return new Border
            {
                Width           = CcWidth,
                Height          = RowHeight + 2,
                Background      = bg,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(180, 200, 230)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding         = new Thickness(4, 0, 4, 0),
                ToolTip         = proj.CostCenter,
                Child           = new TextBlock
                {
                    Text             = proj.CostCenter,
                    FontSize         = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming     = TextTrimming.CharacterEllipsis,
                    Foreground       = new SolidColorBrush(Color.FromRgb(60, 60, 60))
                }
            };
        }

        // ── Células genéricas ─────────────────────────────────────────────────
        private static Border MakeCell(string text, double width, double height,
            Brush bg, bool bold = false, Color? fg = null, string? tooltip = null,
            double leftPad = 4)
        {
            var foreground = fg.HasValue
                ? new SolidColorBrush(fg.Value)
                : (Brush)new SolidColorBrush(Color.FromRgb(30, 30, 30));

            return new Border
            {
                Width           = width,
                Height          = height,
                Background      = bg,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(180, 200, 230)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding         = new Thickness(leftPad, 0, 4, 0),
                ToolTip         = tooltip,
                Child           = new TextBlock
                {
                    Text              = text,
                    FontSize          = 12,
                    FontWeight        = bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground        = foreground,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming      = TextTrimming.CharacterEllipsis
                }
            };
        }

        private static string FormatHoursWithPercent(double hours, DateTime? monthStart)
        {
            if (hours < 0.05) return "–";
            if (monthStart == null) return $"{hours:0.#}h";
            var mEnd = new DateTime(monthStart.Value.Year, monthStart.Value.Month,
                DateTime.DaysInMonth(monthStart.Value.Year, monthStart.Value.Month));
            double capacity = NXProject.Services.ProjectCalendarService.CountWorkingHours(monthStart.Value, mEnd);
            if (capacity <= 0) return $"{hours:0.#}h";
            int pct = (int)Math.Round(hours / capacity * 100);
            return $"{hours:0.#}h ({pct}%)";
        }

        private static Border MakeHoursCell(double hours, double width, double height,
            Brush rowBg, bool bold = false, DateTime? monthStart = null)
        {
            string text = FormatHoursWithPercent(hours, monthStart);
            var fg = hours < 0.05
                ? new SolidColorBrush(Color.FromRgb(200, 200, 200))
                : new SolidColorBrush(bold
                    ? Color.FromRgb(20, 60, 140)
                    : Color.FromRgb(40, 100, 200));

            return new Border
            {
                Width           = width,
                Height          = height,
                Background      = rowBg,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(220, 228, 240)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child           = new TextBlock
                {
                    Text                = text,
                    FontSize            = 11,
                    FontWeight          = bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground          = fg,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Margin              = new Thickness(0, 0, 6, 0)
                }
            };
        }

        // Versão clicável: abre popup com stories do recurso naquele mês
        private Border MakeHoursCellClickable(double hours, double width, double height,
            Brush rowBg, LoadedProject proj, string resName, DateTime monthStart, DateTime monthEnd)
        {
            string text   = FormatHoursWithPercent(hours, monthStart);
            bool hasHours = hours >= 0.05;
            var fg = hasHours
                ? new SolidColorBrush(Color.FromRgb(40, 100, 200))
                : new SolidColorBrush(Color.FromRgb(200, 200, 200));

            var tb = new TextBlock
            {
                Text                = text,
                FontSize            = 11,
                Foreground          = fg,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(0, 0, 6, 0),
                TextDecorations     = hasHours ? TextDecorations.Underline : null
            };
            var border = new Border
            {
                Width           = width,
                Height          = height,
                Background      = rowBg,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(220, 228, 240)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Cursor          = hasHours ? System.Windows.Input.Cursors.Hand : null,
                Child           = tb
            };
            if (hasHours)
                border.MouseLeftButtonUp += (_, _) =>
                    ShowStoriesPopup(proj, resName, monthStart, monthEnd);
            return border;
        }

        // ── Lista de stories de um recurso num mês ────────────────────────────
        private static List<ProjectTask> GetStoriesInMonth(Project project, string resName,
            DateTime monthStart, DateTime monthEnd)
        {
            var result = new List<ProjectTask>();
            foreach (var task in GetChargeableTasks(project.Tasks))
            {
                if (!CreditsResource(task, resName)) continue;
                bool hasRes = task.Resources.Any(r =>
                    string.Equals(r.Resource?.Name, resName, StringComparison.OrdinalIgnoreCase));
                if (!hasRes) continue;

                var tStart = task.Start.Date;
                var tEnd   = task.Finish.Date;
                if (tEnd < monthStart || tStart > monthEnd) continue;

                result.Add(task);
            }
            return result;
        }

        // ── Popup de stories ──────────────────────────────────────────────────
        private void ShowStoriesPopup(LoadedProject proj, string resName,
            DateTime monthStart, DateTime monthEnd)
        {
            var stories = GetStoriesInMonth(proj.Data, resName, monthStart, monthEnd);

            var opts   = TfsConnectionStore.Load();
            var orgUrl = opts.OrganizationUrl?.TrimEnd('/') ?? "";
            var tp     = opts.TeamProject ?? "";

            var win = new Window
            {
                Title                 = $"{resName}  ·  {monthStart:MMM/yyyy}  ·  {proj.Name}",
                Width                 = 920,
                Height                = 440,
                MinWidth              = 600,
                MinHeight             = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                Background            = System.Windows.Media.Brushes.White,
                ResizeMode            = ResizeMode.CanResize
            };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Título
            var header = new TextBlock
            {
                Text       = AppStrings.Get("PMap_StoriesTitle", resName, monthStart, proj.Name),
                FontSize   = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(43, 87, 154)),
                Margin     = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            // Lista
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var panel = new StackPanel();

            // Cabeçalho da tabela
            panel.Children.Add(MakeStoryRow(
                AppStrings.Get("PMap_ColStory"), AppStrings.Get("PMap_SrColHHTotal"), AppStrings.Get("PMap_SrColHHMonth"),
                AppStrings.Get("PMap_SrColPctDone"), AppStrings.Get("PMap_SrColStart"), AppStrings.Get("PMap_SrColFinish"), AppStrings.Get("PMap_SrColDevOps"),
                isHeader: true, devOpsUrl: null));

            double totalMonthHours = 0;
            if (stories.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text    = AppStrings.Get("PMap_NoStories"),
                    Margin  = new Thickness(8, 6, 8, 0),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120))
                });
            }
            else
            {
                bool onlyCurrentHours = OnlyCurrentHours;
                foreach (var task in stories.OrderBy(t => t.Start))
                {
                    double totalH = (task.CurrentHours ?? 0) + (task.EstimatedHours ?? 0);
                    string hh    = totalH > 0.01 ? $"{totalH:0.#}h" : "–";

                    double monthH = 0;
                    foreach (var tr in task.Resources)
                    {
                        if (!string.Equals(tr.Resource?.Name, resName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        monthH += ComputeHoursForTask(task, tr, monthStart, monthEnd, onlyCurrentHours);
                    }
                    totalMonthHours += monthH;
                    string hhMes = monthH > 0.01 ? $"{monthH:0.#}h" : "–";

                    string pct   = $"{(int)Math.Round(task.PercentComplete)}%";
                    string start = task.Start.ToString("dd/MM/yy");
                    string fin   = task.Finish.ToString("dd/MM/yy");
                    string? url  = task.TfsId.HasValue && !string.IsNullOrWhiteSpace(orgUrl)
                        ? $"{orgUrl}/{Uri.EscapeDataString(tp)}/_workitems/edit/{task.TfsId.Value}"
                        : null;
                    panel.Children.Add(MakeStoryRow(task.Name, hh, hhMes, pct, start, fin, url != null ? "↗" : "", isHeader: false, devOpsUrl: url));
                }
            }

            sv.Content = panel;
            Grid.SetRow(sv, 1);
            grid.Children.Add(sv);

            // Rodapé com total HH Mês
            var footer = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(235, 240, 252)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(180, 200, 230)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding         = new Thickness(6, 4, 6, 4),
                Margin          = new Thickness(0, 2, 0, 0)
            };
            footer.Child = new TextBlock
            {
                Text       = AppStrings.Get("PMap_TotalHHMonth", totalMonthHours > 0.01 ? $"{totalMonthHours:0.#}h" : "–"),
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(20, 60, 140)),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(footer, 2);
            grid.Children.Add(footer);

            win.Content = grid;
            win.ShowDialog();
        }

        private static UIElement MakeStoryRow(string name, string hh, string hhMes, string pct, string start, string fin,
            string devOps, bool isHeader, string? devOpsUrl)
        {
            var bg = isHeader
                ? new SolidColorBrush(Color.FromRgb(43, 87, 154))
                : (Brush)System.Windows.Media.Brushes.Transparent;
            var fgColor = isHeader ? Colors.White : Color.FromRgb(30, 30, 30);
            var fw = isHeader ? FontWeights.SemiBold : FontWeights.Normal;

            var row = new Border
            {
                Background      = bg,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(210, 220, 240)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };

            UIElement Cell(string t, double w, HorizontalAlignment ha = HorizontalAlignment.Left) => new Border
            {
                Width = w, Padding = new Thickness(6, 4, 6, 4),
                Child = new TextBlock { Text = t, FontSize = 11, FontWeight = fw,
                    Foreground = new SolidColorBrush(fgColor),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = ha,
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = t }
            };

            sp.Children.Add(Cell(name, 320));
            sp.Children.Add(Cell(hh,    72, HorizontalAlignment.Right));
            sp.Children.Add(Cell(hhMes, 72, HorizontalAlignment.Right));
            sp.Children.Add(Cell(pct,   64, HorizontalAlignment.Right));
            sp.Children.Add(Cell(start, 76));
            sp.Children.Add(Cell(fin, 76));

            if (!isHeader && !string.IsNullOrEmpty(devOpsUrl))
            {
                var btn = new Button
                {
                    Content   = "↗ DevOps",
                    FontSize  = 10,
                    Padding   = new Thickness(6, 2, 6, 2),
                    Margin    = new Thickness(4, 2, 4, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor    = System.Windows.Input.Cursors.Hand
                };
                btn.Click += (_, _) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(devOpsUrl) { UseShellExecute = true }); }
                    catch { }
                };
                sp.Children.Add(btn);
            }
            else if (isHeader)
            {
                sp.Children.Add(Cell(devOps, 90));
            }

            row.Child = sp;
            return row;
        }

        private static Border MakeTotalCell(double hours, double width, double height,
            bool bold = true, Color? fg = null, Color? bg = null)
        {
            string text = hours < 0.05 ? "–" : $"{hours:0.#}h";
            var foreground = fg.HasValue
                ? new SolidColorBrush(fg.Value)
                : new SolidColorBrush(hours < 0.05
                    ? Color.FromRgb(170, 170, 170)
                    : Color.FromRgb(20, 40, 100));

            return new Border
            {
                Width           = width,
                Height          = height,
                Background      = bg.HasValue
                    ? new SolidColorBrush(bg.Value)
                    : new SolidColorBrush(Color.FromRgb(220, 232, 252)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(180, 200, 230)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child           = new TextBlock
                {
                    Text                = text,
                    FontSize            = 11,
                    FontWeight          = bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground          = foreground,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Margin              = new Thickness(0, 0, 6, 0)
                }
            };
        }

        // ── Distribuição por Pessoa ───────────────────────────────────────────
        private void BuildDistributionGrid()
        {
            DistProjHeaderPanel.Items.Clear();
            DistTypeHeaderPanel.Items.Clear();
            DistResPanel.Items.Clear();
            DistDataPanel.Items.Clear();

            if (_projects.Count == 0) return;

            var (periodStart, periodEnd) = GetPeriod();
            bool hideZero = OnlyWithHoursBox.IsChecked == true;

            var months = BuildMonths(periodStart, periodEnd);

            // Coleta recursos únicos
            var allResources = _projects
                .SelectMany(p => p.Data.Resources
                    .Where(r => r.Type == ResourceType.Work && r.Kind == ResourceKind.Project && !string.IsNullOrWhiteSpace(r.Name))
                    .Select(r => r.Name!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            // dist[projIdx][resName][monthIdx] = horas
            var dist = new List<Dictionary<string, double[]>>();
            foreach (var proj in _projects)
            {
                var d = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var res in allResources)
                {
                    var mh = new double[months.Count];
                    for (int mi = 0; mi < months.Count; mi++)
                    {
                        var ms = months[mi];
                        var me = new DateTime(ms.Year, ms.Month, DateTime.DaysInMonth(ms.Year, ms.Month));
                        mh[mi] = ComputeHours(proj.Data, res, ms, me, onlyCurrentHours: false);
                    }
                    d[res] = mh;
                }
                dist.Add(d);
            }

            if (hideZero)
                allResources = allResources
                    .Where(r => dist.Any(d => d[r].Sum() > 0.01))
                    .ToList();

            // Projetos com alguma hora
            var visibleProjIdx = Enumerable.Range(0, _projects.Count)
                .Where(pi => !hideZero || allResources.Any(r => dist[pi][r].Sum() > 0.01))
                .ToList();

            // Meses com alguma hora
            var visibleMonthIdx = Enumerable.Range(0, months.Count)
                .Where(mi => !hideZero || visibleProjIdx.Any(pi =>
                    allResources.Any(r => dist[pi][r][mi] > 0.01)))
                .ToList();

            const double MonthColW   = 95;   // coluna por mês
            const double ProjTotalW  = 66;   // total do projeto
            const double GrandTotalW = 72;   // total geral
            const double DistRowH    = 36;   // altura maior para duas linhas

            // ── Cabeçalho linha 1: projeto (span = nMeses × MonthColW + ProjTotalW) ──
            foreach (var pi in visibleProjIdx)
            {
                var proj      = _projects[pi];
                double projW  = visibleMonthIdx.Count * MonthColW + ProjTotalW;
                var typeColor = proj.IsOpex ? Color.FromRgb(43, 100, 43) : Color.FromRgb(140, 70, 20);

                DistProjHeaderPanel.Items.Add(new Border
                {
                    Width           = projW,
                    Height          = 22,
                    Background      = new SolidColorBrush(Color.FromRgb(43, 87, 154)),
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(29, 63, 115)),
                    BorderThickness = new Thickness(0, 0, 2, 1),
                    ToolTip         = proj.Name,
                    Child           = new StackPanel
                    {
                        Orientation         = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock
                            {
                                Text       = proj.Name,
                                Foreground = Brushes.White,
                                FontWeight = FontWeights.SemiBold,
                                FontSize   = 11,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                MaxWidth   = projW - 60,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin     = new Thickness(4, 0, 6, 0)
                            },
                            new Border
                            {
                                Background      = new SolidColorBrush(typeColor),
                                CornerRadius    = new CornerRadius(3),
                                Padding         = new Thickness(5, 1, 5, 1),
                                VerticalAlignment = VerticalAlignment.Center,
                                Child           = new TextBlock
                                {
                                    Text       = proj.IsOpex ? "OPEX" : "CAPEX",
                                    FontSize   = 9,
                                    FontWeight = FontWeights.SemiBold,
                                    Foreground = Brushes.White
                                }
                            }
                        }
                    }
                });
            }
            DistProjHeaderPanel.Items.Add(new Border
            {
                Width           = GrandTotalW,
                Height          = 22,
                Background      = new SolidColorBrush(Color.FromRgb(25, 60, 120)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(29, 63, 115)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child           = new TextBlock
                {
                    Text                = "TOTAL",
                    Foreground          = Brushes.White,
                    FontWeight          = FontWeights.Bold,
                    FontSize            = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center
                }
            });

            // ── Cabeçalho linha 2: meses de cada projeto + subtotal ──
            foreach (var pi in visibleProjIdx)
            {
                foreach (var mi in visibleMonthIdx)
                {
                    DistTypeHeaderPanel.Items.Add(new Border
                    {
                        Width           = MonthColW,
                        Height          = 20,
                        Background      = new SolidColorBrush(Color.FromRgb(232, 238, 248)),
                        BorderBrush     = new SolidColorBrush(Color.FromRgb(197, 208, 224)),
                        BorderThickness = new Thickness(0, 0, 1, 1),
                        Child           = new TextBlock
                        {
                            Text                = months[mi].ToString("MMM/yy"),
                            FontSize            = 10,
                            FontWeight          = FontWeights.SemiBold,
                            Foreground          = new SolidColorBrush(Color.FromRgb(50, 80, 140)),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment   = VerticalAlignment.Center
                        }
                    });
                }
                DistTypeHeaderPanel.Items.Add(new Border
                {
                    Width           = ProjTotalW,
                    Height          = 20,
                    Background      = new SolidColorBrush(Color.FromRgb(220, 230, 248)),
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(197, 208, 224)),
                    BorderThickness = new Thickness(0, 0, 2, 1),
                    Child           = new TextBlock
                    {
                        Text                = AppStrings.Get("PMap_Total"),
                        FontSize            = 10,
                        FontWeight          = FontWeights.SemiBold,
                        Foreground          = new SolidColorBrush(Color.FromRgb(30, 60, 120)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center
                    }
                });
            }
            DistTypeHeaderPanel.Items.Add(new Border
            {
                Width           = GrandTotalW,
                Height          = 20,
                Background      = new SolidColorBrush(Color.FromRgb(210, 224, 248)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(197, 208, 224)),
                BorderThickness = new Thickness(0, 0, 1, 1)
            });

            // ── Linhas por recurso ──
            // grandByProjMonth[pi][mi] para linha de totais
            var grandByProjMonth = visibleProjIdx.ToDictionary(
                pi => pi, _ => new double[months.Count]);

            for (int ri = 0; ri < allResources.Count; ri++)
            {
                var resName = allResources[ri];

                // total de horas do recurso por mês (somando todos os projetos) — para calcular %
                var totalByMonth = new double[months.Count];
                foreach (var pi in visibleProjIdx)
                    for (int mi = 0; mi < months.Count; mi++)
                        totalByMonth[mi] += dist[pi][resName][mi];

                double resGrandTotal = totalByMonth.Sum();

                var resBg = new SolidColorBrush(ri % 2 == 0
                    ? Color.FromRgb(255, 255, 255)
                    : Color.FromRgb(248, 250, 255));

                DistResPanel.Items.Add(new Border
                {
                    Width           = 200,
                    Height          = DistRowH,
                    Background      = resBg,
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(220, 228, 240)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding         = new Thickness(8, 0, 4, 0),
                    ToolTip         = resName,
                    Child           = new TextBlock
                    {
                        Text              = resName,
                        FontSize          = 12,
                        FontWeight        = FontWeights.SemiBold,
                        Foreground        = new SolidColorBrush(Color.FromRgb(40, 70, 130)),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming      = TextTrimming.CharacterEllipsis
                    }
                });

                var row = new StackPanel { Orientation = Orientation.Horizontal };

                foreach (var pi in visibleProjIdx)
                {
                    double projResTotal = 0;

                    foreach (var mi in visibleMonthIdx)
                    {
                        double h   = dist[pi][resName][mi];
                        double pct = totalByMonth[mi] > 0.01 ? h / totalByMonth[mi] * 100 : 0;
                        projResTotal += h;

                        if (pi == visibleProjIdx[0]) { /* grandByProjMonth handled below */ }
                        grandByProjMonth[pi][mi] += h;

                        string hStr  = h   < 0.05 ? "–"         : $"{h:0.#}h";
                        string pStr  = pct < 0.05 ? ""          : $"{pct:0.#}%";

                        var cellBg = h < 0.05 ? resBg
                            : new SolidColorBrush(Color.FromArgb(
                                (byte)Math.Min(220, 60 + (int)(pct * 1.5)),
                                195, 215, 248));

                        var stack = new StackPanel
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment   = VerticalAlignment.Center
                        };
                        stack.Children.Add(new TextBlock
                        {
                            Text                = hStr,
                            FontSize            = 11,
                            FontWeight          = FontWeights.SemiBold,
                            Foreground          = h < 0.05
                                ? new SolidColorBrush(Color.FromRgb(200, 200, 200))
                                : new SolidColorBrush(Color.FromRgb(20, 50, 120)),
                            HorizontalAlignment = HorizontalAlignment.Center
                        });
                        if (!string.IsNullOrEmpty(pStr))
                            stack.Children.Add(new TextBlock
                            {
                                Text                = pStr,
                                FontSize            = 9,
                                Foreground          = new SolidColorBrush(Color.FromRgb(60, 100, 180)),
                                HorizontalAlignment = HorizontalAlignment.Center
                            });

                        row.Children.Add(new Border
                        {
                            Width           = MonthColW,
                            Height          = DistRowH,
                            Background      = cellBg,
                            BorderBrush     = new SolidColorBrush(Color.FromRgb(220, 228, 240)),
                            BorderThickness = new Thickness(0, 0, 1, 1),
                            Child           = stack
                        });
                    }

                    // Subtotal do projeto para este recurso
                    double projResPct = resGrandTotal > 0.01 ? projResTotal / resGrandTotal * 100 : 0;
                    string ptH = projResTotal < 0.05 ? "–" : $"{projResTotal:0.#}h";
                    string ptP = projResPct  < 0.05 ? ""  : $"{projResPct:0.#}%";
                    var ptStack = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center
                    };
                    ptStack.Children.Add(new TextBlock
                    {
                        Text                = ptH,
                        FontSize            = 11,
                        FontWeight          = FontWeights.Bold,
                        Foreground          = projResTotal < 0.05
                            ? new SolidColorBrush(Color.FromRgb(180, 180, 180))
                            : new SolidColorBrush(Color.FromRgb(20, 40, 100)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                    if (!string.IsNullOrEmpty(ptP))
                        ptStack.Children.Add(new TextBlock
                        {
                            Text                = ptP,
                            FontSize            = 9,
                            Foreground          = new SolidColorBrush(Color.FromRgb(40, 80, 160)),
                            HorizontalAlignment = HorizontalAlignment.Center
                        });

                    row.Children.Add(new Border
                    {
                        Width           = ProjTotalW,
                        Height          = DistRowH,
                        Background      = new SolidColorBrush(Color.FromRgb(220, 232, 252)),
                        BorderBrush     = new SolidColorBrush(Color.FromRgb(180, 200, 230)),
                        BorderThickness = new Thickness(0, 0, 2, 1),
                        Child           = ptStack
                    });
                }

                // Grand total da linha
                row.Children.Add(MakeTotalCell(resGrandTotal, GrandTotalW, DistRowH));
                DistDataPanel.Items.Add(row);
            }

            // ── Linha TOTAL GERAL ──
            var totalBg2 = new SolidColorBrush(Color.FromRgb(43, 87, 154));
            DistResPanel.Items.Add(MakeCell("TOTAL GERAL", 200, DistRowH, totalBg2,
                bold: true, fg: Colors.White, leftPad: 8));

            double grandTotal2 = 0;
            var grandRow2 = new StackPanel { Orientation = Orientation.Horizontal };

            foreach (var pi in visibleProjIdx)
            {
                double projSum = 0;
                foreach (var mi in visibleMonthIdx)
                {
                    double mv = grandByProjMonth[pi][mi];
                    projSum += mv;
                    grandRow2.Children.Add(MakeTotalCell(mv, MonthColW, DistRowH,
                        bold: true, fg: Colors.White, bg: Color.FromRgb(43, 87, 154)));
                }
                grandTotal2 += projSum;
                grandRow2.Children.Add(MakeTotalCell(projSum, ProjTotalW, DistRowH,
                    bold: true, fg: Colors.White, bg: Color.FromRgb(30, 70, 140)));
            }
            grandRow2.Children.Add(MakeTotalCell(grandTotal2, GrandTotalW, DistRowH,
                bold: true, fg: Colors.White, bg: Color.FromRgb(25, 60, 120)));
            DistDataPanel.Items.Add(grandRow2);
        }

        // ── Sincronização de scroll ───────────────────────────────────────────
        private bool _scrolling;
        private void OnMainScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scrolling) return;
            _scrolling = true;
            MonthHeaderScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            ResourceHeaderScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            LeftScroll.ScrollToVerticalOffset(e.VerticalOffset);
            ResScroll.ScrollToVerticalOffset(e.VerticalOffset);
            TypeScroll.ScrollToVerticalOffset(e.VerticalOffset);
            CcScroll.ScrollToVerticalOffset(e.VerticalOffset);
            _scrolling = false;
        }

        private void OnDistScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scrolling) return;
            _scrolling = true;
            DistProjHeaderScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            DistTypeHeaderScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            DistResScroll.ScrollToVerticalOffset(e.VerticalOffset);
            _scrolling = false;
        }

        // ── Aba Rateio ────────────────────────────────────────────────────────────
        private void BuildRateioTab()
        {
            RateioHeaderPanel.Items.Clear();
            RateioLeftPanel.Items.Clear();
            RateioDataPanel.Items.Clear();

            if (_projects.Count == 0) return;

            var (periodStart, periodEnd) = GetPeriod();
            var months = BuildMonths(periodStart, periodEnd);

            // Coleta todos os recursos
            var allRes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var proj in _projects)
                foreach (var task in GetChargeableTasks(proj.Data.Tasks))
                    foreach (var tr in task.Resources)
                        if (!string.IsNullOrWhiteSpace(tr.Resource?.Name) && tr.Resource!.Kind == ResourceKind.Project
                            && CreditsResource(task, tr.Resource!.Name))
                            allRes.Add(tr.Resource!.Name);

            // Para cada recurso × projeto × mês, calcula horas
            // [res][projIdx][mi] = horas
            var data = new Dictionary<string, double[][]>(StringComparer.OrdinalIgnoreCase);
            foreach (var res in allRes)
            {
                var matrix = new double[_projects.Count][];
                for (int pi = 0; pi < _projects.Count; pi++)
                {
                    matrix[pi] = new double[months.Count];
                    for (int mi = 0; mi < months.Count; mi++)
                    {
                        var mStart = months[mi];
                        var mEnd   = new DateTime(mStart.Year, mStart.Month, DateTime.DaysInMonth(mStart.Year, mStart.Month));
                        matrix[pi][mi] = ComputeHours(_projects[pi].Data, res, mStart, mEnd, onlyCurrentHours: false);
                    }
                }
                data[res] = matrix;
            }

            // Filtra meses com alguma hora
            var visMi = Enumerable.Range(0, months.Count)
                .Where(mi => allRes.Any(r => data[r].Any(row => row[mi] > 0.01)))
                .ToList();
            if (visMi.Count == 0) { StatusText.Text = AppStrings.Get("PMap_NoData"); return; }

            // Larguras
            const double ResColW  = 160;
            const double ProjColW = 200;
            const double MonW     = 110;
            const double RowH     = 22;
            const double TotalW   = 90;

            var headerBg = new SolidColorBrush(Color.FromRgb(43, 87, 154));
            var resBg    = new SolidColorBrush(Color.FromRgb(220, 230, 248));
            var projBg   = new SolidColorBrush(Color.FromRgb(245, 248, 255));
            var totalBg  = new SolidColorBrush(Color.FromRgb(200, 215, 245));
            var white    = System.Windows.Media.Brushes.White;

            // Cabeçalho: [Recurso] [Projeto] [mês1] [mês2] ... [Total]
            var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
            headerRow.Children.Add(RateioMakeCell(AppStrings.Get("PMap_ColResource"),  ResColW,  RowH + 2, Colors.White, Color.FromRgb(43, 87, 154), bold: true));
            headerRow.Children.Add(RateioMakeCell(AppStrings.Get("PMap_ColProject"),  ProjColW, RowH + 2, Colors.White, Color.FromRgb(43, 87, 154), bold: true));
            foreach (var mi in visMi)
                headerRow.Children.Add(RateioMakeCell(months[mi].ToString("MMM/yy"), MonW, RowH + 2, Colors.White, Color.FromRgb(43, 87, 154), bold: true));
            headerRow.Children.Add(RateioMakeCell(AppStrings.Get("PMap_Total"),    TotalW,   RowH + 2, Colors.White, Color.FromRgb(25, 60, 120), bold: true));
            RateioHeaderPanel.Items.Add(headerRow);

            // Horas úteis por mês (capacidade full = 8h × dias úteis)
            var monthCapacity = visMi.Select(mi =>
            {
                var ms = months[mi];
                var me = new DateTime(ms.Year, ms.Month, DateTime.DaysInMonth(ms.Year, ms.Month));
                return NXProject.Services.ProjectCalendarService.CountWorkingHours(ms, me);
            }).ToList();

            foreach (var res in allRes)
            {
                var matrix = data[res];

                // Total de horas do recurso por mês (todos os projetos)
                var resTotal = visMi.Select((mi, idx) => matrix.Sum(row => row[mi])).ToArray();
                if (resTotal.All(h => h < 0.01)) continue;

                bool firstProj = true;
                for (int pi = 0; pi < _projects.Count; pi++)
                {
                    var projHours = matrix[pi];
                    double projTotal = visMi.Sum(mi => projHours[mi]);
                    if (projTotal < 0.01) continue;

                    var leftRow = new StackPanel { Orientation = Orientation.Horizontal };

                    // Célula Recurso: só na primeira linha do recurso
                    string resLabel = firstProj ? res : "";
                    var resCellBg   = firstProj ? Color.FromRgb(220, 230, 248) : Color.FromRgb(235, 240, 252);
                    leftRow.Children.Add(RateioMakeCell(resLabel,              ResColW, RowH, Colors.Black, resCellBg, bold: firstProj));
                    leftRow.Children.Add(RateioMakeCell(_projects[pi].Name,   ProjColW, RowH, Color.FromRgb(40, 40, 80), Color.FromRgb(245, 248, 255), bold: false));
                    RateioLeftPanel.Items.Add(leftRow);

                    var dataRow = new StackPanel { Orientation = Orientation.Horizontal };
                    for (int idx = 0; idx < visMi.Count; idx++)
                    {
                        int mi = visMi[idx];
                        double h    = projHours[mi];
                        double tot  = resTotal[idx];
                        double cap  = monthCapacity[idx];
                        string text = BuildRateioCell(h, tot, cap);
                        bool over   = cap > 0 && tot > cap * 1.0001;
                        var fg = h < 0.01
                            ? Color.FromRgb(200, 200, 200)
                            : over ? Color.FromRgb(180, 30, 30) : Color.FromRgb(30, 80, 160);
                        var bg = h < 0.01 ? Color.FromRgb(248, 250, 255) : Color.FromRgb(240, 246, 255);
                        dataRow.Children.Add(RateioMakeCell(text, MonW, RowH, fg, bg, bold: false));
                    }
                    // Total do projeto (soma dos meses)
                    double grandCap = monthCapacity.Sum();
                    string totText = BuildRateioCell(projTotal, resTotal.Sum(), grandCap);
                    dataRow.Children.Add(RateioMakeCell(totText, TotalW, RowH, Color.FromRgb(20, 50, 120), Color.FromRgb(220, 232, 252), bold: true));
                    RateioDataPanel.Items.Add(dataRow);

                    firstProj = false;
                }

                // Linha total do recurso
                var leftTotalRow = new StackPanel { Orientation = Orientation.Horizontal };
                leftTotalRow.Children.Add(RateioMakeCell("", ResColW, RowH, Colors.Black, Color.FromRgb(200, 215, 245), bold: false));
                leftTotalRow.Children.Add(RateioMakeCell("TOTAL " + res, ProjColW, RowH, Color.FromRgb(20, 50, 120), Color.FromRgb(200, 215, 245), bold: true));
                RateioLeftPanel.Items.Add(leftTotalRow);

                var totRow = new StackPanel { Orientation = Orientation.Horizontal };
                for (int idx = 0; idx < visMi.Count; idx++)
                {
                    int mi = visMi[idx];
                    double tot = resTotal[idx];
                    double cap = monthCapacity[idx];
                    bool over  = cap > 0 && tot > cap * 1.0001;
                    int pct    = cap > 0 ? (int)Math.Round(tot / cap * 100) : 0;
                    string text = tot < 0.01 ? "–" : $"{tot:0.#}h ({pct}%)";
                    var fg = tot < 0.01 ? Color.FromRgb(180, 190, 210) : over ? Color.FromRgb(160, 20, 20) : Color.FromRgb(20, 50, 120);
                    totRow.Children.Add(RateioMakeCell(text, MonW, RowH, fg, Color.FromRgb(200, 215, 245), bold: true));
                }
                double resTotalAll = resTotal.Sum();
                double grandCapAll = monthCapacity.Sum();
                int    resPct      = grandCapAll > 0 ? (int)Math.Round(resTotalAll / grandCapAll * 100) : 0;
                string totAllText  = resTotalAll < 0.01 ? "–" : $"{resTotalAll:0.#}h ({resPct}%)";
                totRow.Children.Add(RateioMakeCell(totAllText, TotalW, RowH, Color.FromRgb(20, 50, 120), Color.FromRgb(185, 205, 240), bold: true));
                RateioDataPanel.Items.Add(totRow);

                // Separador
                RateioLeftPanel.Items.Add(new Border { Height = 3, Background = new SolidColorBrush(Color.FromRgb(43, 87, 154)) });
                RateioDataPanel.Items.Add(new Border { Height = 3, Background = new SolidColorBrush(Color.FromRgb(43, 87, 154)) });
            }
        }

        private static string BuildRateioCell(double h, double resTotalInMonth, double capacityInMonth)
        {
            if (h < 0.01) return "–";
            // % em relação ao total do recurso no mês
            int pctRes = resTotalInMonth > 0.01 ? (int)Math.Round(h / resTotalInMonth * 100) : 0;
            return $"{h:0.#}h ({pctRes}%)";
        }

        private static Border RateioMakeCell(string text, double width, double height,
            Color fg, Color bg, bool bold)
            => new Border
            {
                Width = width, Height = height,
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(Color.FromRgb(200, 215, 240)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(4, 0, 4, 0),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(fg),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = text.Length > 0 && text != "–" ? text : null
                }
            };

        private void OnTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Checkbox "Apenas HH atual" só é usado na aba 0 (Horas por Projeto)
            if (OnlyCurrentHoursBox != null)
                OnlyCurrentHoursBox.Visibility = MainTabControl.SelectedIndex == 0
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;

            if (MainTabControl.SelectedIndex == 1)
                BuildDistributionGrid();
            else if (MainTabControl.SelectedIndex == 2)
                BuildStoriesGrid();
            else if (MainTabControl.SelectedIndex == 3)
                BuildRateioTab();
            else if (MainTabControl.SelectedIndex == 4)
                BuildInternalTab();
        }

        private void OnSrScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scrolling) return;
            _scrolling = true;
            SrHeaderScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            SrLeftScroll.ScrollToVerticalOffset(e.VerticalOffset);
            _scrolling = false;
        }

        private void OnSrLeftScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scrolling) return;
            _scrolling = true;
            SrMainScroll.ScrollToVerticalOffset(e.VerticalOffset);
            _scrolling = false;
        }

        private void OnIntScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scrolling) return;
            _scrolling = true;
            IntHeaderScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            IntLeftScroll.ScrollToVerticalOffset(e.VerticalOffset);
            _scrolling = false;
        }

        private void OnIntLeftScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scrolling) return;
            _scrolling = true;
            IntMainScroll.ScrollToVerticalOffset(e.VerticalOffset);
            _scrolling = false;
        }

        // ── Aba 3: Stories por Recurso ────────────────────────────────────────
        private const double SrResW      = 130;
        private const double SrProjW     = 120;
        private const double SrEpicW     = 140;
        private const double SrFeatureW  = 140;
        private const double SrStoryW    = 200;
        private const double SrCapexMonW = 58;   // CAPEX por mês
        private const double SrOpexMonW  = 58;   // OPEX  por mês
        private const double SrTotalW    = 72;   // TOTAL geral
        private const double SrCapexW    = 72;   // CAPEX total
        private const double SrOpexW     = 72;   // OPEX  total
        private const double SrRowH      = 22;

        // Sobe a hierarquia procurando o primeiro ancestral do tipo informado.
        private static ProjectTask? FindAncestorByType(ProjectTask task, string type)
        {
            var current = task.Parent;
            while (current != null)
            {
                if (string.Equals(current.TfsType, type, StringComparison.OrdinalIgnoreCase))
                    return current;
                current = current.Parent;
            }
            return null;
        }

        private void BuildStoriesGrid()
        {
            SrHeaderPanel.Items.Clear();
            SrLeftPanel.Items.Clear();
            SrDataPanel.Items.Clear();

            if (_projects.Count == 0) return;

            var (periodStart, periodEnd) = GetPeriod();
            bool hideZero = OnlyWithHoursBox.IsChecked == true;
            var months = BuildMonths(periodStart, periodEnd);

            // Monta estrutura: recurso → projeto → lista de stories com horas/mês
            // story entry: (task, double[months])
            var byRes = new SortedDictionary<string, List<(LoadedProject Proj, ProjectTask Task, double[] MonthHours)>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var proj in _projects)
            {
                foreach (var task in GetChargeableTasks(proj.Data.Tasks))
                {
                    foreach (var tr in task.Resources)
                    {
                        var rname = tr.Resource?.Name;
                        if (string.IsNullOrWhiteSpace(rname)) continue;
                        if (tr.Resource?.Kind != ResourceKind.Project) continue;
                        if (!CreditsResource(task, rname)) continue;

                        var mh = new double[months.Count];
                        bool any = false;
                        for (int mi = 0; mi < months.Count; mi++)
                        {
                            var ms = months[mi];
                            var me = new DateTime(ms.Year, ms.Month, DateTime.DaysInMonth(ms.Year, ms.Month));
                            double h = ComputeHoursForTask(task, tr, ms, me, onlyCurrentHours: false);
                            mh[mi] = h;
                            if (h > 0.01) any = true;
                        }
                        if (hideZero && !any) continue;

                        if (!byRes.TryGetValue(rname, out var list))
                            byRes[rname] = list = [];
                        list.Add((proj, task, mh));
                    }
                }
            }

            // Quais meses têm dados
            var visMi = Enumerable.Range(0, months.Count)
                .Where(mi => !hideZero || byRes.Values.Any(l => l.Any(x => x.MonthHours[mi] > 0.01)))
                .ToList();

            // ── Cabeçalho de meses: mês mesclado (linha 1) + CAPEX|OPEX (linha 2) ──
            var capexMonBg = Color.FromRgb(140, 70, 20);
            var opexMonBg  = Color.FromRgb(43, 100, 43);
            double monPairW = SrCapexMonW + SrOpexMonW;
            foreach (var mi in visMi)
            {
                string lbl = months[mi].ToString("MMM/yy");
                var monthGrid = new Grid { Width = monPairW, Height = 44 };
                monthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
                monthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
                monthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SrCapexMonW) });
                monthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SrOpexMonW) });

                // Linha 1: mês mesclado (span 2 colunas)
                var monthBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(43, 87, 154)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(29, 63, 115)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Child = new TextBlock { Text = lbl, FontSize = 10, FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center }
                };
                Grid.SetRow(monthBorder, 0);
                Grid.SetColumn(monthBorder, 0);
                Grid.SetColumnSpan(monthBorder, 2);
                monthGrid.Children.Add(monthBorder);

                // Linha 2: CAPEX
                var capexBorder = new Border
                {
                    Background = new SolidColorBrush(capexMonBg),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(100, 50, 10)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Child = new TextBlock { Text = "CAPEX", FontSize = 9, FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center }
                };
                Grid.SetRow(capexBorder, 1);
                Grid.SetColumn(capexBorder, 0);
                monthGrid.Children.Add(capexBorder);

                // Linha 2: OPEX
                var opexBorder = new Border
                {
                    Background = new SolidColorBrush(opexMonBg),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(20, 70, 20)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Child = new TextBlock { Text = "OPEX", FontSize = 9, FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center }
                };
                Grid.SetRow(opexBorder, 1);
                Grid.SetColumn(opexBorder, 1);
                monthGrid.Children.Add(opexBorder);

                SrHeaderPanel.Items.Add(monthGrid);
            }
            // Colunas de totais: também duas linhas (label em cima, vazio em baixo)
            SrHeaderPanel.Items.Add(SrMakeTotalHeader("TOTAL", SrTotalW, Color.FromRgb(25,  60, 120)));
            SrHeaderPanel.Items.Add(SrMakeTotalHeader("CAPEX", SrCapexW, Color.FromRgb(140, 70,  20)));
            SrHeaderPanel.Items.Add(SrMakeTotalHeader("OPEX",  SrOpexW,  Color.FromRgb(43, 100,  43)));

            // ── Linhas ──
            var grandCapexByMonth = new double[months.Count];
            var grandOpexByMonth  = new double[months.Count];
            double grandCapex = 0, grandOpex = 0;

            foreach (var (resName, entries) in byRes)
            {
                var byProj = entries
                    .GroupBy(e => e.Proj.Name, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key);

                // Totais do recurso por mês separados em CAPEX/OPEX
                var resCapexByMonth = new double[months.Count];
                var resOpexByMonth  = new double[months.Count];
                foreach (var e in entries)
                    for (int mi = 0; mi < months.Count; mi++)
                        if (e.Proj.IsOpexForTask(e.Task)) resOpexByMonth[mi]  += e.MonthHours[mi];
                        else                              resCapexByMonth[mi] += e.MonthHours[mi];

                bool resFirst = true;
                foreach (var projGroup in byProj)
                {
                    var projEntries = projGroup.ToList();
                    var projCapexByMonth = new double[months.Count];
                    var projOpexByMonth  = new double[months.Count];
                    foreach (var e in projEntries)
                        for (int mi = 0; mi < months.Count; mi++)
                            if (e.Proj.IsOpexForTask(e.Task)) projOpexByMonth[mi]  += e.MonthHours[mi];
                            else                              projCapexByMonth[mi] += e.MonthHours[mi];

                    bool projFirst = true;
                    foreach (var (proj, task, mh) in projEntries.OrderBy(e => e.Task.Start))
                    {
                        bool storyIsOpex = proj.IsOpexForTask(task);
                        var leftBg = new SolidColorBrush(Color.FromRgb(248, 250, 255));
                        var leftRow = new StackPanel { Orientation = Orientation.Horizontal };

                        leftRow.Children.Add(new Border
                        {
                            Width = SrResW, Height = SrRowH, Background = leftBg,
                            BorderBrush = new SolidColorBrush(Color.FromRgb(210, 220, 240)),
                            BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(6, 0, 4, 0),
                            Child = new TextBlock
                            {
                                Text = resFirst && projFirst ? resName : "",
                                FontSize = 11, FontWeight = FontWeights.SemiBold,
                                Foreground = new SolidColorBrush(Color.FromRgb(43, 87, 154)),
                                VerticalAlignment = VerticalAlignment.Center,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                ToolTip = resFirst && projFirst ? resName : null
                            }
                        });
                        leftRow.Children.Add(new Border
                        {
                            Width = SrProjW, Height = SrRowH, Background = leftBg,
                            BorderBrush = new SolidColorBrush(Color.FromRgb(210, 220, 240)),
                            BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 0, 4, 0),
                            Child = new TextBlock
                            {
                                Text = projFirst ? projGroup.Key : "",
                                FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(60, 100, 170)),
                                VerticalAlignment = VerticalAlignment.Center,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                ToolTip = projFirst ? projGroup.Key : null
                            }
                        });
                        var epicTask    = FindAncestorByType(task, "Epic");
                        var featureTask = FindAncestorByType(task, "Feature");
                        var epicLabel    = epicTask?.Name    ?? "";
                        var featureLabel = featureTask?.Name ?? "";
                        leftRow.Children.Add(new Border
                        {
                            Width = SrEpicW, Height = SrRowH, Background = leftBg,
                            BorderBrush = new SolidColorBrush(Color.FromRgb(210, 220, 240)),
                            BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 0, 4, 0),
                            ToolTip = string.IsNullOrEmpty(epicLabel) ? null : epicLabel,
                            Child = new TextBlock { Text = epicLabel, FontSize = 10,
                                Foreground = new SolidColorBrush(Color.FromRgb(100, 40, 140)),
                                VerticalAlignment = VerticalAlignment.Center,
                                TextTrimming = TextTrimming.CharacterEllipsis }
                        });
                        leftRow.Children.Add(new Border
                        {
                            Width = SrFeatureW, Height = SrRowH, Background = leftBg,
                            BorderBrush = new SolidColorBrush(Color.FromRgb(210, 220, 240)),
                            BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 0, 4, 0),
                            ToolTip = string.IsNullOrEmpty(featureLabel) ? null : featureLabel,
                            Child = new TextBlock { Text = featureLabel, FontSize = 10,
                                Foreground = new SolidColorBrush(Color.FromRgb(20, 100, 120)),
                                VerticalAlignment = VerticalAlignment.Center,
                                TextTrimming = TextTrimming.CharacterEllipsis }
                        });
                        string storyLabel = task.Name ?? $"#{task.TfsId}";
                        leftRow.Children.Add(new Border
                        {
                            Width = SrStoryW, Height = SrRowH, Background = leftBg,
                            BorderBrush = new SolidColorBrush(Color.FromRgb(210, 220, 240)),
                            BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 0, 4, 0),
                            ToolTip = $"{storyLabel}\nInício: {task.Start:dd/MM/yy}  Fim: {task.Finish:dd/MM/yy}  HH: {task.EstimatedHours?.ToString("0.#") ?? "–"}h",
                            Child = new TextBlock { Text = storyLabel, FontSize = 10,
                                Foreground = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                                VerticalAlignment = VerticalAlignment.Center,
                                TextTrimming = TextTrimming.CharacterEllipsis }
                        });
                        SrLeftPanel.Items.Add(leftRow);

                        var dataRow = new StackPanel { Orientation = Orientation.Horizontal };
                        double rowTotal = 0;
                        foreach (var mi in visMi)
                        {
                            double h = mh[mi];
                            rowTotal += h;
                            // CAPEX cell
                            double hC = !storyIsOpex ? h : 0;
                            dataRow.Children.Add(SrMakeCell(hC > 0.01 ? $"{hC:0.#}h" : "–",
                                SrCapexMonW, hC > 0.01 ? Color.FromRgb(120, 60, 10) : Color.FromRgb(200, 200, 200),
                                Color.FromRgb(255, 248, 240), bold: false));
                            // OPEX cell
                            double hO = storyIsOpex ? h : 0;
                            dataRow.Children.Add(SrMakeCell(hO > 0.01 ? $"{hO:0.#}h" : "–",
                                SrOpexMonW, hO > 0.01 ? Color.FromRgb(20, 90, 20) : Color.FromRgb(200, 200, 200),
                                Color.FromRgb(240, 250, 240), bold: false));
                        }
                        dataRow.Children.Add(SrMakeCell(rowTotal > 0.01 ? $"{rowTotal:0.#}h" : "–",
                            SrTotalW, Color.FromRgb(20, 50, 110), Color.FromRgb(230, 238, 252), bold: true));
                        dataRow.Children.Add(SrMakeCell(!storyIsOpex && rowTotal > 0.01 ? $"{rowTotal:0.#}h" : "–",
                            SrCapexW, !storyIsOpex && rowTotal > 0.01 ? Color.FromRgb(120, 60, 10) : Color.FromRgb(200, 200, 200),
                            Color.FromRgb(255, 248, 240), bold: false));
                        dataRow.Children.Add(SrMakeCell(storyIsOpex && rowTotal > 0.01 ? $"{rowTotal:0.#}h" : "–",
                            SrOpexW, storyIsOpex && rowTotal > 0.01 ? Color.FromRgb(20, 90, 20) : Color.FromRgb(200, 200, 200),
                            Color.FromRgb(240, 250, 240), bold: false));
                        SrDataPanel.Items.Add(dataRow);

                        projFirst = false;
                        resFirst  = false;
                    }

                    // Sub-total do projeto
                    var projTotalBg = Color.FromRgb(220, 230, 248);
                    var leftProjTotal = new StackPanel { Orientation = Orientation.Horizontal };
                    leftProjTotal.Children.Add(new Border { Width = SrResW, Height = SrRowH, Background = new SolidColorBrush(projTotalBg), BorderBrush = new SolidColorBrush(Color.FromRgb(180,200,230)), BorderThickness = new Thickness(0,0,1,1) });
                    leftProjTotal.Children.Add(new Border
                    {
                        Width = SrProjW, Height = SrRowH, Background = new SolidColorBrush(projTotalBg),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(180, 200, 230)), BorderThickness = new Thickness(0,0,1,1),
                        Padding = new Thickness(4, 0, 4, 0),
                        Child = new TextBlock { Text = projGroup.Key, FontSize = 10, FontWeight = FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(Color.FromRgb(30, 60, 130)),
                            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                    });
                    leftProjTotal.Children.Add(new Border { Width = SrEpicW,    Height = SrRowH, Background = new SolidColorBrush(projTotalBg), BorderBrush = new SolidColorBrush(Color.FromRgb(180,200,230)), BorderThickness = new Thickness(0,0,1,1) });
                    leftProjTotal.Children.Add(new Border { Width = SrFeatureW, Height = SrRowH, Background = new SolidColorBrush(projTotalBg), BorderBrush = new SolidColorBrush(Color.FromRgb(180,200,230)), BorderThickness = new Thickness(0,0,1,1) });
                    leftProjTotal.Children.Add(new Border
                    {
                        Width = SrStoryW, Height = SrRowH, Background = new SolidColorBrush(projTotalBg),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(180, 200, 230)), BorderThickness = new Thickness(0,0,1,1),
                        Padding = new Thickness(4, 0, 4, 0),
                        Child = new TextBlock { Text = AppStrings.Get("PMap_Subtotal"), FontSize = 10, FontStyle = FontStyles.Italic,
                            Foreground = new SolidColorBrush(Color.FromRgb(60, 90, 150)), VerticalAlignment = VerticalAlignment.Center }
                    });
                    SrLeftPanel.Items.Add(leftProjTotal);

                    var projDataTotal = new StackPanel { Orientation = Orientation.Horizontal };
                    double ptotal = 0;
                    foreach (var mi in visMi)
                    {
                        double pC = projCapexByMonth[mi];
                        double pO = projOpexByMonth[mi];
                        double h = pC + pO;
                        ptotal += h;
                        projDataTotal.Children.Add(SrMakeCell(pC > 0.01 ? $"{pC:0.#}h" : "–",
                            SrCapexMonW, pC > 0.01 ? Color.FromRgb(120, 60, 10) : Color.FromRgb(180, 180, 180),
                            Color.FromRgb(252, 242, 230), bold: true));
                        projDataTotal.Children.Add(SrMakeCell(pO > 0.01 ? $"{pO:0.#}h" : "–",
                            SrOpexMonW, pO > 0.01 ? Color.FromRgb(20, 90, 20) : Color.FromRgb(180, 180, 180),
                            Color.FromRgb(232, 248, 232), bold: true));
                    }
                    double pCapexTotal = visMi.Sum(mi => projCapexByMonth[mi]);
                    double pOpexTotal  = visMi.Sum(mi => projOpexByMonth[mi]);
                    projDataTotal.Children.Add(SrMakeCell(ptotal > 0.01 ? $"{ptotal:0.#}h" : "–",
                        SrTotalW, Color.FromRgb(20, 40, 100), Color.FromRgb(205, 218, 245), bold: true));
                    projDataTotal.Children.Add(SrMakeCell(pCapexTotal > 0.01 ? $"{pCapexTotal:0.#}h" : "–",
                        SrCapexW, pCapexTotal > 0.01 ? Color.FromRgb(120, 60, 10) : Color.FromRgb(180, 180, 180),
                        Color.FromRgb(252, 242, 230), bold: true));
                    projDataTotal.Children.Add(SrMakeCell(pOpexTotal > 0.01 ? $"{pOpexTotal:0.#}h" : "–",
                        SrOpexW, pOpexTotal > 0.01 ? Color.FromRgb(20, 90, 20) : Color.FromRgb(180, 180, 180),
                        Color.FromRgb(232, 248, 232), bold: true));
                    SrDataPanel.Items.Add(projDataTotal);
                }

                // Total do recurso
                var resTotalBg = Color.FromRgb(43, 87, 154);
                var leftResTotal = new StackPanel { Orientation = Orientation.Horizontal };
                leftResTotal.Children.Add(new Border
                {
                    Width = SrResW, Height = SrRowH + 2, Background = new SolidColorBrush(resTotalBg),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(29, 63, 115)), BorderThickness = new Thickness(0,0,1,1),
                    Padding = new Thickness(6, 0, 4, 0),
                    Child = new TextBlock { Text = "", FontSize = 11, FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis }
                });
                leftResTotal.Children.Add(new Border { Width = SrProjW,    Height = SrRowH + 2, Background = new SolidColorBrush(resTotalBg), BorderBrush = new SolidColorBrush(Color.FromRgb(29,63,115)), BorderThickness = new Thickness(0,0,1,1) });
                leftResTotal.Children.Add(new Border { Width = SrEpicW,    Height = SrRowH + 2, Background = new SolidColorBrush(resTotalBg), BorderBrush = new SolidColorBrush(Color.FromRgb(29,63,115)), BorderThickness = new Thickness(0,0,1,1) });
                leftResTotal.Children.Add(new Border { Width = SrFeatureW, Height = SrRowH + 2, Background = new SolidColorBrush(resTotalBg), BorderBrush = new SolidColorBrush(Color.FromRgb(29,63,115)), BorderThickness = new Thickness(0,0,1,1) });
                leftResTotal.Children.Add(new Border
                {
                    Width = SrStoryW, Height = SrRowH + 2, Background = new SolidColorBrush(resTotalBg),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(29, 63, 115)), BorderThickness = new Thickness(0,0,1,1),
                    Padding = new Thickness(4, 0, 4, 0),
                    Child = new TextBlock { Text = "TOTAL", FontSize = 11, FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }
                });
                SrLeftPanel.Items.Add(leftResTotal);

                double resCapex = visMi.Sum(mi => resCapexByMonth[mi]);
                double resOpex  = visMi.Sum(mi => resOpexByMonth[mi]);
                grandCapex += resCapex;
                grandOpex  += resOpex;

                var resDataTotal = new StackPanel { Orientation = Orientation.Horizontal };
                double rtotal = 0;
                foreach (var mi in visMi)
                {
                    double rC = resCapexByMonth[mi];
                    double rO = resOpexByMonth[mi];
                    double h  = rC + rO;
                    rtotal += h;
                    grandCapexByMonth[mi] += rC;
                    grandOpexByMonth[mi]  += rO;
                    resDataTotal.Children.Add(SrMakeCell(rC > 0.01 ? $"{rC:0.#}h" : "–",
                        SrCapexMonW, Colors.White, Color.FromRgb(140, 70, 20), bold: true, height: SrRowH + 2));
                    resDataTotal.Children.Add(SrMakeCell(rO > 0.01 ? $"{rO:0.#}h" : "–",
                        SrOpexMonW, Colors.White, Color.FromRgb(43, 100, 43), bold: true, height: SrRowH + 2));
                }
                resDataTotal.Children.Add(SrMakeCell(rtotal > 0.01 ? $"{rtotal:0.#}h" : "–",
                    SrTotalW, Colors.White, Color.FromRgb(25, 60, 120), bold: true, height: SrRowH + 2));
                resDataTotal.Children.Add(SrMakeCell(resCapex > 0.01 ? $"{resCapex:0.#}h" : "–",
                    SrCapexW, Colors.White, Color.FromRgb(140, 70, 20), bold: true, height: SrRowH + 2));
                resDataTotal.Children.Add(SrMakeCell(resOpex > 0.01 ? $"{resOpex:0.#}h" : "–",
                    SrOpexW, Colors.White, Color.FromRgb(43, 100, 43), bold: true, height: SrRowH + 2));
                SrDataPanel.Items.Add(resDataTotal);

                SrLeftPanel.Items.Add(new Border { Height = 2, Background = new SolidColorBrush(Color.FromRgb(180, 200, 230)) });
                SrDataPanel.Items.Add(new Border { Height = 2, Background = new SolidColorBrush(Color.FromRgb(180, 200, 230)) });
            }

            // TOTAL GERAL
            var gtBg = Color.FromRgb(25, 60, 120);
            var gtLeft = new StackPanel { Orientation = Orientation.Horizontal };
            gtLeft.Children.Add(new Border
            {
                Width = SrResW + SrProjW + SrEpicW + SrFeatureW, Height = SrRowH + 2, Background = new SolidColorBrush(gtBg),
                BorderBrush = new SolidColorBrush(Color.FromRgb(15, 40, 90)), BorderThickness = new Thickness(0,0,1,1),
                Padding = new Thickness(6, 0, 4, 0),
                Child = new TextBlock { Text = "TOTAL GERAL", FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }
            });
            gtLeft.Children.Add(new Border { Width = SrEpicW,    Height = SrRowH + 2, Background = new SolidColorBrush(gtBg), BorderBrush = new SolidColorBrush(Color.FromRgb(15,40,90)), BorderThickness = new Thickness(0,0,1,1) });
            gtLeft.Children.Add(new Border { Width = SrFeatureW, Height = SrRowH + 2, Background = new SolidColorBrush(gtBg), BorderBrush = new SolidColorBrush(Color.FromRgb(15,40,90)), BorderThickness = new Thickness(0,0,1,1) });
            gtLeft.Children.Add(new Border { Width = SrStoryW,   Height = SrRowH + 2, Background = new SolidColorBrush(gtBg), BorderBrush = new SolidColorBrush(Color.FromRgb(15,40,90)), BorderThickness = new Thickness(0,0,1,1) });
            SrLeftPanel.Items.Add(gtLeft);

            var gtData = new StackPanel { Orientation = Orientation.Horizontal };
            double gtotal = 0;
            foreach (var mi in visMi)
            {
                double gC = grandCapexByMonth[mi];
                double gO = grandOpexByMonth[mi];
                gtotal += gC + gO;
                gtData.Children.Add(SrMakeCell(gC > 0.01 ? $"{gC:0.#}h" : "–",
                    SrCapexMonW, Colors.White, Color.FromRgb(100, 50, 10), bold: true, height: SrRowH + 2));
                gtData.Children.Add(SrMakeCell(gO > 0.01 ? $"{gO:0.#}h" : "–",
                    SrOpexMonW, Colors.White, Color.FromRgb(30, 80, 30), bold: true, height: SrRowH + 2));
            }
            gtData.Children.Add(SrMakeCell(gtotal > 0.01 ? $"{gtotal:0.#}h" : "–",
                SrTotalW, Colors.White, Color.FromRgb(15, 40, 90), bold: true, height: SrRowH + 2));
            gtData.Children.Add(SrMakeCell(grandCapex > 0.01 ? $"{grandCapex:0.#}h" : "–",
                SrCapexW, Colors.White, Color.FromRgb(100, 50, 10), bold: true, height: SrRowH + 2));
            gtData.Children.Add(SrMakeCell(grandOpex > 0.01 ? $"{grandOpex:0.#}h" : "–",
                SrOpexW, Colors.White, Color.FromRgb(30, 80, 30), bold: true, height: SrRowH + 2));
            SrDataPanel.Items.Add(gtData);
        }

        // ── Aba 5: Interno ───────────────────────────────────────────────────────
        private void BuildInternalTab()
        {
            IntHeaderPanel.Items.Clear();
            IntLeftPanel.Items.Clear();
            IntDataPanel.Items.Clear();

            if (_projects.Count == 0) return;

            var (periodStart, periodEnd) = GetPeriod();
            bool hideZero = OnlyWithHoursBox.IsChecked == true;
            var months = BuildMonths(periodStart, periodEnd);

            var byRes = new SortedDictionary<string, List<(LoadedProject Proj, ProjectTask Task, double[] MonthHours)>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var proj in _projects)
            {
                foreach (var task in GetChargeableTasks(proj.Data.Tasks))
                {
                    foreach (var tr in task.Resources)
                    {
                        var rname = tr.Resource?.Name;
                        if (string.IsNullOrWhiteSpace(rname)) continue;
                        if (tr.Resource?.Kind != ResourceKind.Internal) continue;
                        if (!CreditsResource(task, rname)) continue;

                        var mh = new double[months.Count];
                        bool any = false;
                        for (int mi = 0; mi < months.Count; mi++)
                        {
                            var ms = months[mi];
                            var me = new DateTime(ms.Year, ms.Month, DateTime.DaysInMonth(ms.Year, ms.Month));
                            double h = ComputeHoursForTask(task, tr, ms, me, onlyCurrentHours: false);
                            mh[mi] = h;
                            if (h > 0.01) any = true;
                        }
                        if (hideZero && !any) continue;

                        if (!byRes.TryGetValue(rname, out var list))
                            byRes[rname] = list = [];
                        list.Add((proj, task, mh));
                    }
                }
            }

            var visMi = Enumerable.Range(0, months.Count)
                .Where(mi => !hideZero || byRes.Values.Any(l => l.Any(x => x.MonthHours[mi] > 0.01)))
                .ToList();

            const double MonW   = 90;
            const double TotW   = 90;
            var hdrBg = Color.FromRgb(91, 50, 112);
            var hdrBorder = Color.FromRgb(61, 31, 80);

            // Cabeçalho de meses
            foreach (var mi in visMi)
            {
                IntHeaderPanel.Items.Add(new Border
                {
                    Width = MonW, Height = 44, Background = new SolidColorBrush(hdrBg),
                    BorderBrush = new SolidColorBrush(hdrBorder), BorderThickness = new Thickness(0, 0, 1, 1),
                    Child = new TextBlock { Text = months[mi].ToString("MMM/yy"), FontSize = 10,
                        FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                });
            }
            IntHeaderPanel.Items.Add(new Border
            {
                Width = TotW, Height = 44, Background = new SolidColorBrush(Color.FromRgb(61, 31, 80)),
                BorderBrush = new SolidColorBrush(hdrBorder), BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock { Text = "TOTAL", FontSize = 10, FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center }
            });

            // Linhas
            var grandByMonth = new double[months.Count];

            foreach (var (resName, entries) in byRes)
            {
                var byProj = entries.GroupBy(e => e.Proj.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key);
                bool resFirst = true;

                foreach (var projGroup in byProj)
                {
                    var projEntries = projGroup.ToList();
                    var projByMonth = new double[months.Count];
                    foreach (var e in projEntries)
                        for (int mi = 0; mi < months.Count; mi++)
                            projByMonth[mi] += e.MonthHours[mi];

                    bool projFirst = true;
                    foreach (var (proj, task, mh) in projEntries.OrderBy(e => e.Task.Start))
                    {
                        var rowBg = new SolidColorBrush(Color.FromRgb(250, 246, 255));
                        var leftRow = new StackPanel { Orientation = Orientation.Horizontal };

                        leftRow.Children.Add(new Border
                        {
                            Width = SrResW, Height = SrRowH, Background = rowBg,
                            BorderBrush = new SolidColorBrush(Color.FromRgb(200, 180, 220)), BorderThickness = new Thickness(0,0,1,1),
                            Padding = new Thickness(6, 0, 4, 0),
                            Child = new TextBlock { Text = resFirst && projFirst ? resName : "", FontSize = 11,
                                FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(91, 50, 112)),
                                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                                ToolTip = resFirst && projFirst ? resName : null }
                        });
                        leftRow.Children.Add(new Border
                        {
                            Width = SrProjW, Height = SrRowH, Background = rowBg,
                            BorderBrush = new SolidColorBrush(Color.FromRgb(200, 180, 220)), BorderThickness = new Thickness(0,0,1,1),
                            Padding = new Thickness(4, 0, 4, 0),
                            Child = new TextBlock { Text = projFirst ? projGroup.Key : "", FontSize = 11,
                                Foreground = new SolidColorBrush(Color.FromRgb(110, 70, 150)),
                                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                                ToolTip = projFirst ? projGroup.Key : null }
                        });
                        var epicTask    = FindAncestorByType(task, "Epic");
                        var featureTask = FindAncestorByType(task, "Feature");
                        leftRow.Children.Add(new Border { Width = SrEpicW, Height = SrRowH, Background = rowBg,
                            BorderBrush = new SolidColorBrush(Color.FromRgb(200,180,220)), BorderThickness = new Thickness(0,0,1,1),
                            Padding = new Thickness(4,0,4,0), ToolTip = epicTask?.Name,
                            Child = new TextBlock { Text = epicTask?.Name ?? "", FontSize = 10,
                                Foreground = new SolidColorBrush(Color.FromRgb(100,40,140)),
                                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                        });
                        leftRow.Children.Add(new Border { Width = SrFeatureW, Height = SrRowH, Background = rowBg,
                            BorderBrush = new SolidColorBrush(Color.FromRgb(200,180,220)), BorderThickness = new Thickness(0,0,1,1),
                            Padding = new Thickness(4,0,4,0), ToolTip = featureTask?.Name,
                            Child = new TextBlock { Text = featureTask?.Name ?? "", FontSize = 10,
                                Foreground = new SolidColorBrush(Color.FromRgb(20,100,120)),
                                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                        });
                        string storyLabel = task.Name ?? $"#{task.TfsId}";
                        leftRow.Children.Add(new Border { Width = SrStoryW, Height = SrRowH, Background = rowBg,
                            BorderBrush = new SolidColorBrush(Color.FromRgb(200,180,220)), BorderThickness = new Thickness(0,0,1,1),
                            Padding = new Thickness(4,0,4,0),
                            ToolTip = $"{storyLabel}\nInício: {task.Start:dd/MM/yy}  Fim: {task.Finish:dd/MM/yy}",
                            Child = new TextBlock { Text = storyLabel, FontSize = 10,
                                Foreground = new SolidColorBrush(Color.FromRgb(40,40,40)),
                                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                        });
                        IntLeftPanel.Items.Add(leftRow);

                        var dataRow = new StackPanel { Orientation = Orientation.Horizontal };
                        double rowTotal = 0;
                        foreach (var mi in visMi)
                        {
                            double h = mh[mi];
                            rowTotal += h;
                            grandByMonth[mi] += h;
                            dataRow.Children.Add(SrMakeCell(h > 0.01 ? $"{h:0.#}h" : "–",
                                MonW, h > 0.01 ? Color.FromRgb(80, 40, 110) : Color.FromRgb(200, 200, 200),
                                Color.FromRgb(248, 244, 255), bold: false));
                        }
                        dataRow.Children.Add(SrMakeCell(rowTotal > 0.01 ? $"{rowTotal:0.#}h" : "–",
                            TotW, Color.FromRgb(60, 20, 90), Color.FromRgb(230, 220, 245), bold: true));
                        IntDataPanel.Items.Add(dataRow);

                        projFirst = false;
                        resFirst  = false;
                    }
                }
            }

            // Total Geral
            var gtBg = Color.FromRgb(61, 31, 80);
            var gtLeft = new StackPanel { Orientation = Orientation.Horizontal };
            gtLeft.Children.Add(new Border { Width = SrResW + SrProjW + SrEpicW + SrFeatureW, Height = SrRowH + 2,
                Background = new SolidColorBrush(gtBg), BorderBrush = new SolidColorBrush(Color.FromRgb(40,15,60)),
                BorderThickness = new Thickness(0,0,1,1), Padding = new Thickness(6,0,4,0),
                Child = new TextBlock { Text = "TOTAL GERAL", FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }
            });
            gtLeft.Children.Add(new Border { Width = SrStoryW, Height = SrRowH + 2, Background = new SolidColorBrush(gtBg),
                BorderBrush = new SolidColorBrush(Color.FromRgb(40,15,60)), BorderThickness = new Thickness(0,0,1,1) });
            IntLeftPanel.Items.Add(gtLeft);

            var gtData = new StackPanel { Orientation = Orientation.Horizontal };
            double gtotal2 = 0;
            foreach (var mi in visMi)
            {
                double g = grandByMonth[mi];
                gtotal2 += g;
                gtData.Children.Add(SrMakeCell(g > 0.01 ? $"{g:0.#}h" : "–",
                    MonW, Colors.White, Color.FromRgb(61, 31, 80), bold: true, height: SrRowH + 2));
            }
            gtData.Children.Add(SrMakeCell(gtotal2 > 0.01 ? $"{gtotal2:0.#}h" : "–",
                TotW, Colors.White, Color.FromRgb(40, 15, 60), bold: true, height: SrRowH + 2));
            IntDataPanel.Items.Add(gtData);
        }

        private static double ComputeHoursForTask(ProjectTask task, TaskResource tr,
            DateTime monthStart, DateTime monthEnd, bool onlyCurrentHours = false)
        {
            var currentHours = GetCurrentHoursForAssignment(task, tr);
            var currentEnd = GetCurrentHoursEnd(task);
            var currentInMonth = AllocateHoursInPeriod(currentHours, task.Start, currentEnd, monthStart, monthEnd);
            if (onlyCurrentHours)
                return currentInMonth;

            var remainingHours = GetRemainingHours(task, tr);
            var remainingStart = currentHours > 0 && task.PercentComplete < 100 && currentEnd > task.Start.Date
                ? currentEnd.AddDays(1)
                : task.Start.Date;
            if (remainingStart > task.Finish.Date)
                return currentInMonth;

            var remainingInMonth = AllocateHoursInPeriod(remainingHours, remainingStart, task.Finish, monthStart, monthEnd);
            return currentInMonth + remainingInMonth;
        }

        private static Border SrMakeCell(string text, double width, Color fg, Color bg, bool bold, double height = SrRowH)
            => new Border
            {
                Width = width, Height = height,
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(Color.FromRgb(210, 220, 240)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = text, FontSize = 11,
                    FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(fg),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                }
            };

        private static Border SrMakeMonthHeader(string text, double width, Color bg)
            => new Border
            {
                Width = width, Height = 22,
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(Color.FromRgb(29, 63, 115)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

        private static Border SrMakeTotalHeader(string text, double width, Color bg)
            => new Border
            {
                Width = width, Height = 44,
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(Color.FromRgb(29, 63, 115)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

        // ── Handlers ──────────────────────────────────────────────────────────
        private void OnSelectProjectsClick(object sender, RoutedEventArgs e)
        {
            var opts       = TfsConnectionStore.Load();
            var devOpsList = DevOpsProjectListService.Load(opts.DevOpsProjectListPath);

            if (devOpsList.Count == 0)
            {
                MessageBox.Show(
                    AppStrings.Get("PMap_EmptyListMsg"),
                    AppStrings.Get("PMap_EmptyListTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var win = new ProjectPickerWindow(devOpsList, opts.PortfolioProjectConfigs) { Owner = this };
            if (win.ShowDialog() != true) return;

            // Merge: update OPEX/CC from picker; add new; remove deselected
            foreach (var newCfg in win.SelectedConfigs)
            {
                var existing = opts.PortfolioProjectConfigs.FirstOrDefault(c =>
                    string.Equals(c.ProjectName, newCfg.ProjectName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.IsOpex     = newCfg.IsOpex;
                    existing.CostCenter = newCfg.CostCenter;
                }
                else
                {
                    opts.PortfolioProjectConfigs.Add(newCfg);
                }
            }

            var selectedNames = win.SelectedConfigs
                .Select(c => c.ProjectName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            opts.PortfolioProjectConfigs.RemoveAll(c =>
                devOpsList.Any(d => string.Equals(d.Name, c.ProjectName, StringComparison.OrdinalIgnoreCase))
                && !selectedNames.Contains(c.ProjectName));

            TfsConnectionStore.Save(opts, !string.IsNullOrEmpty(opts.PersonalAccessToken));

            int count = opts.PortfolioProjectConfigs.Count;
            StatusText.Text = AppStrings.Get("PMap_ProjectsSelected", count);
        }

        private async void OnImportFromDevOpsClick(object sender, RoutedEventArgs e)
        {
            var opts = TfsConnectionStore.Load();

            if (string.IsNullOrWhiteSpace(opts.OrganizationUrl) ||
                string.IsNullOrWhiteSpace(opts.PersonalAccessToken))
            {
                MessageBox.Show(
                    AppStrings.Get("PMap_NoConnMsg"),
                    AppStrings.Get("PMap_NoConnTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (opts.PortfolioProjectConfigs.Count == 0)
            {
                MessageBox.Show(AppStrings.Get("PMap_NoProjSelMsg"),
                    AppStrings.Get("PMap_NoProjSelTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var devOpsList = DevOpsProjectListService.Load(opts.DevOpsProjectListPath);

            var toImport = new List<(PortfolioProjectConfig Cfg, int RootId)>();
            var skipped  = new List<string>();

            foreach (var cfg in opts.PortfolioProjectConfigs)
            {
                var dp = devOpsList.FirstOrDefault(d =>
                    string.Equals(d.Name, cfg.ProjectName, StringComparison.OrdinalIgnoreCase));
                if (dp == null || dp.RootWorkItemId <= 0)
                    skipped.Add(cfg.ProjectName);
                else
                    toImport.Add((cfg, dp.RootWorkItemId));
            }

            if (toImport.Count == 0)
            {
                MessageBox.Show(
                    AppStrings.Get("PMap_NoRootMsg"),
                    AppStrings.Get("PMap_NoRootTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ImportBtn.IsEnabled = false;
            var errors = new List<string>();
            var imported = new List<LoadedProject>();

            for (int i = 0; i < toImport.Count; i++)
            {
                var (cfg, rootId) = toImport[i];
                StatusText.Text = AppStrings.Get("PMap_Importing", i + 1, toImport.Count, cfg.ProjectName);
                await Task.Yield(); // permite atualizar a UI

                var importOpts = new TfsConnectionOptions
                {
                    OrganizationUrl      = opts.OrganizationUrl,
                    TeamProject          = opts.TeamProject,
                    PersonalAccessToken  = opts.PersonalAccessToken,
                    RootWorkItemId       = rootId,
                    HoursPerDay          = opts.HoursPerDay,
                    EffortFieldName      = opts.EffortFieldName,
                    StartFieldName       = opts.StartFieldName,
                    FinishFieldName      = opts.FinishFieldName,
                    PercAlocFieldName    = opts.PercAlocFieldName,
                    SyncVersionFieldName = opts.SyncVersionFieldName,
                    SyncNameFieldName    = opts.SyncNameFieldName,
                    FixedStartTagName    = opts.FixedStartTagName,
                    SyncPredecessorLinks = false,
                    FutureSprintDays     = 0
                };

                try
                {
                    var result = await TfsImportService.ImportAsync(importOpts);
                    ResourceKindConfigService.ApplyTo(result.Project.Resources);
                    imported.Add(new LoadedProject
                    {
                        FilePath         = string.Empty,
                        Name             = cfg.ProjectName,
                        IsOpex           = cfg.IsOpex,
                        CostCenter       = cfg.CostCenter,
                        CostCenterSource = string.IsNullOrWhiteSpace(cfg.CostCenterSource)
                                           ? (cfg.IsOpex ? "OPEX" : "CAPEX")
                                           : cfg.CostCenterSource,
                        Data             = result.Project
                    });
                }
                catch (Exception ex)
                {
                    errors.Add($"{cfg.ProjectName}: {ex.Message}");
                }
            }

            ImportBtn.IsEnabled = true;
            _projects = imported;
            BuildGrid();

            var sb = new StringBuilder();
            sb.Append($"{imported.Count} projeto(s) importado(s) do DevOps");
            if (skipped.Count > 0)
                sb.Append($"  ·  {skipped.Count} sem ID raiz (ignorado)");
            if (errors.Count > 0)
                sb.Append($"  ·  {errors.Count} erro(s)");
            StatusText.Text = sb.ToString();

            if (errors.Count > 0)
                MessageBox.Show(AppStrings.Get("PMap_ImportErrors", string.Join("\n", errors)),
                    AppStrings.Get("PMap_PartialImport"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OnPeriodChanged(object sender, SelectionChangedEventArgs e) { }

        private void OnFilterChanged(object sender, RoutedEventArgs e) => BuildGrid();

        private void OnExportExcelClick(object sender, RoutedEventArgs e)
        {
            if (_projects.Count == 0)
            {
                MessageBox.Show(AppStrings.Get("PMap_NoDataToExport"),
                    AppStrings.Get("PMap_Export"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int tab = MainTabControl.SelectedIndex;
            string[] tabNames  = [AppStrings.Get("PMap_TabHours"), AppStrings.Get("PMap_TabDistribution"), AppStrings.Get("PMap_TabStories"), AppStrings.Get("PMap_TabRateio"), AppStrings.Get("PMap_TabInternal")];
            string defaultName = tab < tabNames.Length ? tabNames[tab] : AppStrings.Get("PMap_DefaultMapName");

            var dlg = new SaveFileDialog
            {
                Title      = AppStrings.Get("PMap_ExportTitleFmt", defaultName),
                Filter     = "Excel XML 2003 (*.xml)|*.xml",
                DefaultExt = ".xml",
                FileName   = defaultName
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                switch (tab)
                {
                    case 0: ExportAllocationToExcel(dlg.FileName, onlyTab: 0); break;
                    case 1: ExportAllocationToExcel(dlg.FileName, onlyTab: 1); break;
                    case 2: ExportStoriesToExcel(dlg.FileName);                 break;
                    case 3: ExportRateioToExcel(dlg.FileName);                  break;
                    case 4: ExportRateioToExcel(dlg.FileName, internalOnly: true); break;
                    default: ExportAllocationToExcel(dlg.FileName, onlyTab: 0); break;
                }
                StatusText.Text = AppStrings.Get("PMap_Exported", dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(AppStrings.Get("PMap_ExportError", ex.Message), AppStrings.Get("PMap_Error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportRateioToExcel(string filePath, bool internalOnly = false)
        {
            var (periodStart, periodEnd) = GetPeriod();
            var months    = BuildMonths(periodStart, periodEnd);
            bool hideZero = OnlyWithHoursBox.IsChecked == true;
            var targetKind = internalOnly ? ResourceKind.Internal : ResourceKind.Project;

            // Reconstrói os dados do rateio
            var allRes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var proj in _projects)
                foreach (var task in GetChargeableTasks(proj.Data.Tasks))
                    foreach (var tr in task.Resources)
                        if (!string.IsNullOrWhiteSpace(tr.Resource?.Name) && tr.Resource!.Kind == targetKind
                            && CreditsResource(task, tr.Resource!.Name))
                            allRes.Add(tr.Resource!.Name);

            var data = new Dictionary<string, double[][]>(StringComparer.OrdinalIgnoreCase);
            foreach (var res in allRes)
            {
                var matrix = new double[_projects.Count][];
                for (int pi = 0; pi < _projects.Count; pi++)
                {
                    matrix[pi] = new double[months.Count];
                    for (int mi = 0; mi < months.Count; mi++)
                    {
                        var mStart = months[mi];
                        var mEnd   = new DateTime(mStart.Year, mStart.Month, DateTime.DaysInMonth(mStart.Year, mStart.Month));
                        matrix[pi][mi] = ComputeHours(_projects[pi].Data, res, mStart, mEnd, onlyCurrentHours: false);
                    }
                }
                data[res] = matrix;
            }

            var visMi = Enumerable.Range(0, months.Count)
                .Where(mi => allRes.Any(r => data[r].Any(row => row[mi] > 0.01)))
                .ToList();
            if (visMi.Count == 0) { StatusText.Text = AppStrings.Get("PMap_NoDataExportPeriod"); return; }

            var monthCapacity = visMi.Select(mi =>
            {
                var ms = months[mi];
                var me = new DateTime(ms.Year, ms.Month, DateTime.DaysInMonth(ms.Year, ms.Month));
                return NXProject.Services.ProjectCalendarService.CountWorkingHours(ms, me);
            }).ToList();

            XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
            XNamespace ss = "urn:schemas-microsoft-com:office:spreadsheet";

            var styles = new XElement(ns + "Styles",
                ExSt(ns, "Default"),
                ExSt(ns, "RH",  bg: "#2B579A", fg: "#FFFFFF", bold: true, hAlign: "Center"),
                ExSt(ns, "RHL", bg: "#2B579A", fg: "#FFFFFF", bold: true),
                ExSt(ns, "RR",  bg: "#DCE6F8", fg: "#000000", bold: true),
                ExSt(ns, "RP",  bg: "#F5F8FF", fg: "#282850"),
                ExSt(ns, "RD",  bg: "#F0F6FF", fg: "#1E50A0", hAlign: "Right"),
                ExSt(ns, "RT",  bg: "#DCE8FC", fg: "#142832", bold: true, hAlign: "Right")
            );

            var sheet = new List<XElement>();
            sheet.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "160")));
            sheet.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "200")));
            foreach (var _ in visMi)
                sheet.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "95")));
            sheet.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "90")));

            // Cabeçalho
            var hdr = new XElement(ns + "Row", new XAttribute(ss + "Height", "22"));
            hdr.Add(ExStCell(ns, AppStrings.Get("PMap_ColResource"),  "RHL"));
            hdr.Add(ExStCell(ns, AppStrings.Get("PMap_ColProject"),  "RHL"));
            foreach (var mi in visMi) hdr.Add(ExStCell(ns, months[mi].ToString("MMM/yyyy"), "RH"));
            hdr.Add(ExStCell(ns, AppStrings.Get("PMap_Total"), "RH"));
            sheet.Add(hdr);

            foreach (var res in allRes)
            {
                var matrix   = data[res];
                var resTotal = visMi.Select((_, idx) => matrix.Sum(row => row[visMi[idx]])).ToArray();
                if (resTotal.All(h => h < 0.01)) continue;

                bool firstProj = true;
                for (int pi = 0; pi < _projects.Count; pi++)
                {
                    var projHours = matrix[pi];
                    double projTotal = visMi.Sum(mi => projHours[mi]);
                    if (projTotal < 0.01) continue;

                    var row = new XElement(ns + "Row", new XAttribute(ss + "Height", "20"));
                    row.Add(ExStCell(ns, firstProj ? res : "", "RR"));
                    row.Add(ExStCell(ns, _projects[pi].Name, "RP"));
                    for (int idx = 0; idx < visMi.Count; idx++)
                    {
                        double h   = projHours[visMi[idx]];
                        double tot = resTotal[idx];
                        string txt = h < 0.01 ? "–" : $"{h:0.#}h ({(tot > 0.01 ? (int)Math.Round(h / tot * 100) : 0)}%)";
                        row.Add(ExStCell(ns, txt, "RD"));
                    }
                    string totTxt = $"{projTotal:0.#}h ({(resTotal.Sum() > 0.01 ? (int)Math.Round(projTotal / resTotal.Sum() * 100) : 0)}%)";
                    row.Add(ExStCell(ns, totTxt, "RT"));
                    sheet.Add(row);
                    firstProj = false;
                }
            }

            var workbook = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(ns + "Workbook",
                    new XAttribute(XNamespace.Xmlns + "ss", ss),
                    styles,
                    new XElement(ns + "Worksheet",
                        new XAttribute(ss + "Name", internalOnly ? AppStrings.Get("PMap_TabInternal") : AppStrings.Get("PMap_TabRateio")),
                        new XElement(ns + "Table", sheet))));

            workbook.Save(filePath);
        }

        private void ExportStoriesToExcel(string filePath)
        {
            var (periodStart, periodEnd) = GetPeriod();
            bool hideZero = OnlyWithHoursBox.IsChecked == true;
            var months = BuildMonths(periodStart, periodEnd);

            var byRes = new SortedDictionary<string, List<(LoadedProject Proj, ProjectTask Task, double[] MonthHours)>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var proj in _projects)
            {
                foreach (var task in GetChargeableTasks(proj.Data.Tasks))
                {
                    foreach (var tr in task.Resources)
                    {
                        var rname = tr.Resource?.Name;
                        if (string.IsNullOrWhiteSpace(rname)) continue;
                        if (tr.Resource?.Kind != ResourceKind.Project) continue;
                        if (!CreditsResource(task, rname)) continue;

                        var mh = new double[months.Count];
                        bool any = false;
                        for (int mi = 0; mi < months.Count; mi++)
                        {
                            var ms = months[mi];
                            var me = new DateTime(ms.Year, ms.Month, DateTime.DaysInMonth(ms.Year, ms.Month));
                            double h = ComputeHoursForTask(task, tr, ms, me, onlyCurrentHours: false);
                            mh[mi] = h;
                            if (h > 0.01) any = true;
                        }
                        if (hideZero && !any) continue;

                        if (!byRes.TryGetValue(rname, out var list))
                            byRes[rname] = list = [];
                        list.Add((proj, task, mh));
                    }
                }
            }

            var visMi = Enumerable.Range(0, months.Count)
                .Where(mi => !hideZero || byRes.Values.Any(l => l.Any(x => x.MonthHours[mi] > 0.01)))
                .ToList();

            XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
            XNamespace ss = "urn:schemas-microsoft-com:office:spreadsheet";

            // ── Estilos ──
            var styles = new XElement(ns + "Styles",
                ExSt(ns, "Default"),
                // Cabeçalhos fixos (Recurso/Projeto/Story)
                ExSt(ns, "H0", bg: "#1D3F73", fg: "#FFFFFF", bold: true),
                // Mês mesclado
                ExSt(ns, "HM", bg: "#2B579A", fg: "#FFFFFF", bold: true, hAlign: "Center"),
                // Sub-header CAPEX
                ExSt(ns, "HC", bg: "#8C4614", fg: "#FFFFFF", bold: true, hAlign: "Center"),
                // Sub-header OPEX
                ExSt(ns, "HO", bg: "#2B642B", fg: "#FFFFFF", bold: true, hAlign: "Center"),
                // Colunas TOTAL/CAPEX/OPEX no cabeçalho
                ExSt(ns, "HT", bg: "#193C78", fg: "#FFFFFF", bold: true, hAlign: "Center"),
                // Story: célula de label
                ExSt(ns, "SL", bg: "#F8FAFF", fg: "#1E2840"),
                // Story: valor CAPEX
                ExSt(ns, "SC", bg: "#FFF8F0", fg: "#783C0A", hAlign: "Right", numFmt: "0.0"),
                // Story: valor OPEX
                ExSt(ns, "SO", bg: "#F0FAF0", fg: "#145A14", hAlign: "Right", numFmt: "0.0"),
                // Story: coluna TOTAL
                ExSt(ns, "ST", bg: "#E6EEFA", fg: "#143264", bold: true, hAlign: "Right", numFmt: "0.0"),
                // Story: CAPEX total col
                ExSt(ns, "STC", bg: "#FFF8F0", fg: "#783C0A", hAlign: "Right", numFmt: "0.0"),
                // Story: OPEX total col
                ExSt(ns, "STO", bg: "#F0FAF0", fg: "#145A14", hAlign: "Right", numFmt: "0.0"),
                // Subtotal projeto: label
                ExSt(ns, "PL", bg: "#DCE6F8", fg: "#1E3C82", bold: true, italic: true),
                // Subtotal: CAPEX value
                ExSt(ns, "PC", bg: "#FCF2E6", fg: "#783C0A", bold: true, hAlign: "Right", numFmt: "0.0"),
                // Subtotal: OPEX value
                ExSt(ns, "PO", bg: "#E8F8E8", fg: "#145A14", bold: true, hAlign: "Right", numFmt: "0.0"),
                // Subtotal: TOTAL
                ExSt(ns, "PT", bg: "#CDDBF5", fg: "#1E3C82", bold: true, hAlign: "Right", numFmt: "0.0"),
                // Subtotal: CAPEX/OPEX total cols
                ExSt(ns, "PTC", bg: "#FCF2E6", fg: "#783C0A", bold: true, hAlign: "Right", numFmt: "0.0"),
                ExSt(ns, "PTO", bg: "#E8F8E8", fg: "#145A14", bold: true, hAlign: "Right", numFmt: "0.0"),
                // Total recurso: label
                ExSt(ns, "RL", bg: "#2B579A", fg: "#FFFFFF", bold: true),
                // Total recurso: CAPEX
                ExSt(ns, "RC", bg: "#8C4614", fg: "#FFFFFF", bold: true, hAlign: "Right", numFmt: "0.0"),
                // Total recurso: OPEX
                ExSt(ns, "RO", bg: "#2B642B", fg: "#FFFFFF", bold: true, hAlign: "Right", numFmt: "0.0"),
                // Total recurso: TOTAL col
                ExSt(ns, "RT", bg: "#193C78", fg: "#FFFFFF", bold: true, hAlign: "Right", numFmt: "0.0"),
                // Grand total: label
                ExSt(ns, "GL", bg: "#0F2857", fg: "#FFFFFF", bold: true),
                // Grand total: CAPEX
                ExSt(ns, "GC", bg: "#643208", fg: "#FFFFFF", bold: true, hAlign: "Right", numFmt: "0.0"),
                // Grand total: OPEX
                ExSt(ns, "GO", bg: "#1E501E", fg: "#FFFFFF", bold: true, hAlign: "Right", numFmt: "0.0"),
                // Grand total: TOTAL col
                ExSt(ns, "GT", bg: "#0A1E3C", fg: "#FFFFFF", bold: true, hAlign: "Right", numFmt: "0.0")
            );

            var tableChildren = new List<XElement>();

            // ── Larguras das colunas ──
            tableChildren.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "120")));
            tableChildren.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "150")));
            tableChildren.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "150")));
            tableChildren.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "150")));
            tableChildren.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "200")));
            foreach (var _ in visMi)
            {
                tableChildren.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "55")));
                tableChildren.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "55")));
            }
            tableChildren.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "65")));
            tableChildren.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "65")));
            tableChildren.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "65")));

            // ── Cabeçalho linha 1: labels fixos + meses mesclados + totais ──
            var hdrRow1 = new XElement(ns + "Row", new XAttribute(ss + "Height", "22"));
            hdrRow1.Add(ExStCell(ns, AppStrings.Get("PMap_ColResource"), "H0"));
            hdrRow1.Add(ExStCell(ns, AppStrings.Get("PMap_ColProject"), "H0"));
            hdrRow1.Add(ExStCell(ns, "EPIC",    "H0"));
            hdrRow1.Add(ExStCell(ns, "Feature", "H0"));
            hdrRow1.Add(ExStCell(ns, "Story",   "H0"));
            foreach (var mi in visMi)
                hdrRow1.Add(ExStCell(ns, months[mi].ToString("MMM/yyyy"), "HM", mergeAcross: 1));
            hdrRow1.Add(ExStCell(ns, "TOTAL", "HT"));
            hdrRow1.Add(ExStCell(ns, "CAPEX", "HT"));
            hdrRow1.Add(ExStCell(ns, "OPEX",  "HT"));
            tableChildren.Add(hdrRow1);

            // ── Cabeçalho linha 2: CAPEX | OPEX por mês ──
            var hdrRow2 = new XElement(ns + "Row", new XAttribute(ss + "Height", "20"));
            hdrRow2.Add(ExStCell(ns, "", "H0"));
            hdrRow2.Add(ExStCell(ns, "", "H0"));
            hdrRow2.Add(ExStCell(ns, "", "H0"));
            hdrRow2.Add(ExStCell(ns, "", "H0"));
            hdrRow2.Add(ExStCell(ns, "", "H0"));
            foreach (var _ in visMi)
            {
                hdrRow2.Add(ExStCell(ns, "CAPEX", "HC"));
                hdrRow2.Add(ExStCell(ns, "OPEX",  "HO"));
            }
            hdrRow2.Add(ExStCell(ns, "", "HT"));
            hdrRow2.Add(ExStCell(ns, "", "HT"));
            hdrRow2.Add(ExStCell(ns, "", "HT"));
            tableChildren.Add(hdrRow2);

            // ── Linhas de dados ──
            var grandCapexByMonth = new double[months.Count];
            var grandOpexByMonth  = new double[months.Count];
            double grandCapex = 0, grandOpex = 0;

            foreach (var (resName, entries) in byRes)
            {
                var resCapexByMonth = new double[months.Count];
                var resOpexByMonth  = new double[months.Count];
                foreach (var e in entries)
                    for (int mi = 0; mi < months.Count; mi++)
                        if (e.Proj.IsOpexForTask(e.Task)) resOpexByMonth[mi]  += e.MonthHours[mi];
                        else                              resCapexByMonth[mi] += e.MonthHours[mi];

                bool resFirst = true;
                foreach (var projGroup in entries.GroupBy(e => e.Proj.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
                {
                    var projEntries = projGroup.ToList();
                    var projCapexByMonth = new double[months.Count];
                    var projOpexByMonth  = new double[months.Count];
                    foreach (var e in projEntries)
                        for (int mi = 0; mi < months.Count; mi++)
                            if (e.Proj.IsOpexForTask(e.Task)) projOpexByMonth[mi]  += e.MonthHours[mi];
                            else                              projCapexByMonth[mi] += e.MonthHours[mi];

                    bool projFirst = true;
                    foreach (var (proj, task, mh) in projEntries.OrderBy(e => e.Task.Start))
                    {
                        bool isOpex = proj.IsOpexForTask(task);
                        double rowTotal = visMi.Sum(mi => mh[mi]);

                        var epicEx    = FindAncestorByType(task, "Epic");
                        var featureEx = FindAncestorByType(task, "Feature");
                        var row = new XElement(ns + "Row", new XAttribute(ss + "Height", "18"));
                        row.Add(ExStCell(ns, resFirst && projFirst ? resName : "", "SL"));
                        row.Add(ExStCell(ns, projFirst ? projGroup.Key : "", "SL"));
                        row.Add(ExStCell(ns, epicEx?.Name    ?? "", "SL"));
                        row.Add(ExStCell(ns, featureEx?.Name ?? "", "SL"));
                        row.Add(ExStCell(ns, task.Name ?? $"#{task.TfsId}", "SL"));
                        foreach (var mi in visMi)
                        {
                            double h = mh[mi];
                            double hC = !isOpex ? h : 0;
                            double hO =  isOpex ? h : 0;
                            row.Add(ExStCell(ns, hC > 0.01 ? (object)Math.Round(hC, 2) : "", "SC"));
                            row.Add(ExStCell(ns, hO > 0.01 ? (object)Math.Round(hO, 2) : "", "SO"));
                        }
                        row.Add(ExStCell(ns, Math.Round(rowTotal, 2), "ST"));
                        row.Add(ExStCell(ns, !isOpex && rowTotal > 0.01 ? (object)Math.Round(rowTotal, 2) : "", "STC"));
                        row.Add(ExStCell(ns,  isOpex && rowTotal > 0.01 ? (object)Math.Round(rowTotal, 2) : "", "STO"));
                        tableChildren.Add(row);

                        projFirst = false;
                        resFirst  = false;
                    }

                    // Subtotal do projeto
                    double pCapexTotal = visMi.Sum(mi => projCapexByMonth[mi]);
                    double pOpexTotal  = visMi.Sum(mi => projOpexByMonth[mi]);
                    double ptotal = pCapexTotal + pOpexTotal;
                    var pRow = new XElement(ns + "Row", new XAttribute(ss + "Height", "18"));
                    pRow.Add(ExStCell(ns, "", "PL"));
                    pRow.Add(ExStCell(ns, projGroup.Key, "PL"));
                    pRow.Add(ExStCell(ns, "", "PL"));
                    pRow.Add(ExStCell(ns, "", "PL"));
                    pRow.Add(ExStCell(ns, AppStrings.Get("PMap_Subtotal"), "PL"));
                    foreach (var mi in visMi)
                    {
                        double pC = projCapexByMonth[mi];
                        double pO = projOpexByMonth[mi];
                        pRow.Add(ExStCell(ns, pC > 0.01 ? (object)Math.Round(pC, 2) : "", "PC"));
                        pRow.Add(ExStCell(ns, pO > 0.01 ? (object)Math.Round(pO, 2) : "", "PO"));
                    }
                    pRow.Add(ExStCell(ns, Math.Round(ptotal, 2), "PT"));
                    pRow.Add(ExStCell(ns, pCapexTotal > 0.01 ? (object)Math.Round(pCapexTotal, 2) : "", "PTC"));
                    pRow.Add(ExStCell(ns, pOpexTotal > 0.01 ? (object)Math.Round(pOpexTotal, 2) : "", "PTO"));
                    tableChildren.Add(pRow);
                }

                // Total do recurso
                double resCapex = visMi.Sum(mi => resCapexByMonth[mi]);
                double resOpex  = visMi.Sum(mi => resOpexByMonth[mi]);
                double rtotal   = resCapex + resOpex;
                grandCapex += resCapex; grandOpex += resOpex;
                for (int mi = 0; mi < months.Count; mi++) { grandCapexByMonth[mi] += resCapexByMonth[mi]; grandOpexByMonth[mi] += resOpexByMonth[mi]; }

                var rRow = new XElement(ns + "Row", new XAttribute(ss + "Height", "20"));
                rRow.Add(ExStCell(ns, resName,  "RL"));
                rRow.Add(ExStCell(ns, "",       "RL"));
                rRow.Add(ExStCell(ns, "TOTAL",  "RL"));
                rRow.Add(ExStCell(ns, "",       "RL"));
                rRow.Add(ExStCell(ns, "",       "RL"));
                foreach (var mi in visMi)
                {
                    rRow.Add(ExStCell(ns, resCapexByMonth[mi] > 0.01 ? (object)Math.Round(resCapexByMonth[mi], 2) : "", "RC"));
                    rRow.Add(ExStCell(ns, resOpexByMonth[mi]  > 0.01 ? (object)Math.Round(resOpexByMonth[mi],  2) : "", "RO"));
                }
                rRow.Add(ExStCell(ns, Math.Round(rtotal,  2), "RT"));
                rRow.Add(ExStCell(ns, resCapex > 0.01 ? (object)Math.Round(resCapex, 2) : "", "RC"));
                rRow.Add(ExStCell(ns, resOpex  > 0.01 ? (object)Math.Round(resOpex,  2) : "", "RO"));
                tableChildren.Add(rRow);

                tableChildren.Add(new XElement(ns + "Row", new XAttribute(ss + "Height", "4")));
            }

            // TOTAL GERAL
            double gtotal = grandCapex + grandOpex;
            var gRow = new XElement(ns + "Row", new XAttribute(ss + "Height", "22"));
            gRow.Add(ExStCell(ns, "TOTAL GERAL", "GL"));
            gRow.Add(ExStCell(ns, "", "GL"));
            gRow.Add(ExStCell(ns, "", "GL"));
            gRow.Add(ExStCell(ns, "", "GL"));
            gRow.Add(ExStCell(ns, "", "GL"));
            foreach (var mi in visMi)
            {
                gRow.Add(ExStCell(ns, grandCapexByMonth[mi] > 0.01 ? (object)Math.Round(grandCapexByMonth[mi], 2) : "", "GC"));
                gRow.Add(ExStCell(ns, grandOpexByMonth[mi]  > 0.01 ? (object)Math.Round(grandOpexByMonth[mi],  2) : "", "GO"));
            }
            gRow.Add(ExStCell(ns, Math.Round(gtotal,     2), "GT"));
            gRow.Add(ExStCell(ns, grandCapex > 0.01 ? (object)Math.Round(grandCapex, 2) : "", "GC"));
            gRow.Add(ExStCell(ns, grandOpex  > 0.01 ? (object)Math.Round(grandOpex,  2) : "", "GO"));
            tableChildren.Add(gRow);

            var workbook = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(ns + "Workbook",
                    new XAttribute(XNamespace.Xmlns + "ss", ss),
                    styles,
                    new XElement(ns + "Worksheet",
                        new XAttribute(ss + "Name", AppStrings.Get("PMap_TabStories")),
                        new XElement(ns + "Table", tableChildren))));

            workbook.Save(filePath);
        }

        private static XElement ExSt(XNamespace ns, string id,
            string? bg = null, string fg = "#000000",
            bool bold = false, bool italic = false,
            string hAlign = "Left", string vAlign = "Center",
            string? numFmt = null)
        {
            var style = new XElement(ns + "Style", new XAttribute(ns + "ID", id));
            style.Add(new XElement(ns + "Alignment",
                new XAttribute(ns + "Horizontal", hAlign),
                new XAttribute(ns + "Vertical",   vAlign)));
            var font = new XElement(ns + "Font",
                new XAttribute(ns + "FontName", "Calibri"),
                new XAttribute(ns + "Size", "10"),
                new XAttribute(ns + "Color", fg));
            if (bold)   font.Add(new XAttribute(ns + "Bold",   "1"));
            if (italic) font.Add(new XAttribute(ns + "Italic", "1"));
            style.Add(font);
            if (bg != null)
                style.Add(new XElement(ns + "Interior",
                    new XAttribute(ns + "Color",   bg),
                    new XAttribute(ns + "Pattern", "Solid")));
            style.Add(new XElement(ns + "Borders",
                ExStBorder(ns, "Bottom"), ExStBorder(ns, "Left"),
                ExStBorder(ns, "Right"),  ExStBorder(ns, "Top")));
            if (numFmt != null)
                style.Add(new XElement(ns + "NumberFormat",
                    new XAttribute(ns + "Format", numFmt)));
            return style;
        }

        private static XElement ExStBorder(XNamespace ns, string position) =>
            new(ns + "Border",
                new XAttribute(ns + "Position",  position),
                new XAttribute(ns + "LineStyle", "Continuous"),
                new XAttribute(ns + "Weight",    "1"),
                new XAttribute(ns + "Color",     "#C8D0DC"));

        private static XElement ExStCell(XNamespace ns, object value, string styleId, int mergeAcross = 0)
        {
            var cell = new XElement(ns + "Cell", new XAttribute(ns + "StyleID", styleId));
            if (mergeAcross > 0) cell.Add(new XAttribute(ns + "MergeAcross", mergeAcross));
            bool isNum = value is double or float or int or long;
            cell.Add(new XElement(ns + "Data",
                new XAttribute(ns + "Type", isNum ? "Number" : "String"),
                isNum ? Convert.ToDouble(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                      : value?.ToString() ?? ""));
            return cell;
        }

        // overload used in subtotal row to avoid ambiguity with the string/object overloads
        private static XElement ExStCell(XNamespace ns, string _ignored, string projName, string styleId) =>
            ExStCell(ns, projName, styleId);

        private void ExportAllocationToExcel(string filePath, int onlyTab = 0)
        {
            var (periodStart, periodEnd) = GetPeriod();
            var months    = BuildMonths(periodStart, periodEnd);
            bool hideZero = OnlyWithHoursBox.IsChecked == true;

            XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
            XNamespace ss = "urn:schemas-microsoft-com:office:spreadsheet";

            // ── Pré-computa dados ──
            bool exportOnlyCurrentHours = onlyTab == 0 && OnlyCurrentHours;
            var projResourceData = new List<(LoadedProject Proj,
                                             List<(string Res, double[] MonthHours)> Rows)>();
            var allResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var proj in _projects)
            {
                var resources = proj.Data.Resources
                    .Where(r => r.Type == ResourceType.Work && r.Kind == ResourceKind.Project && !string.IsNullOrWhiteSpace(r.Name))
                    .Select(r => r.Name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .ToList();

                var rows = new List<(string Res, double[] MonthHours)>();
                foreach (var res in resources)
                {
                    var mh = new double[months.Count];
                    for (int mi = 0; mi < months.Count; mi++)
                    {
                        var ms = months[mi];
                        var me = new DateTime(ms.Year, ms.Month, DateTime.DaysInMonth(ms.Year, ms.Month));
                        mh[mi] = ComputeHours(proj.Data, res, ms, me, exportOnlyCurrentHours);
                    }
                    if (hideZero && mh.Sum() < 0.01) continue;
                    rows.Add((res, mh));
                    allResources.Add(res);
                }
                projResourceData.Add((proj, rows));
            }

            var visibleMonths = Enumerable.Range(0, months.Count)
                .Where(mi => !hideZero || projResourceData.Any(p => p.Rows.Any(r => r.MonthHours[mi] > 0.01)))
                .ToList();

            // ── Estilos ──
            var styles = new XElement(ns + "Styles",
                ExSt(ns, "Default"),
                // Aba 1
                ExSt(ns, "AH",  bg: "#1D3F73", fg: "#FFFFFF", bold: true, hAlign: "Center"),
                ExSt(ns, "AHL", bg: "#1D3F73", fg: "#FFFFFF", bold: true),
                ExSt(ns, "AP",  bg: "#2B579A", fg: "#FFFFFF", bold: true),
                ExSt(ns, "APN", bg: "#2B579A", fg: "#FFFFFF", bold: true,  hAlign: "Right", numFmt: "0.0"),
                ExSt(ns, "AR",  bg: "#EEF2FA", fg: "#1E2840"),
                ExSt(ns, "ARN", bg: "#EEF2FA", fg: "#1E2840", hAlign: "Right", numFmt: "0.0"),
                ExSt(ns, "AT",  bg: "#0F2857", fg: "#FFFFFF", bold: true),
                ExSt(ns, "ATN", bg: "#0F2857", fg: "#FFFFFF", bold: true,  hAlign: "Right", numFmt: "0.0"),
                // Aba 2
                ExSt(ns, "BH1", bg: "#2B579A", fg: "#FFFFFF", bold: true, hAlign: "Center"),
                ExSt(ns, "BH2", bg: "#3A6EBF", fg: "#FFFFFF", bold: true, hAlign: "Center"),
                ExSt(ns, "BR",  bg: "#EEF2FA", fg: "#1E2840"),
                ExSt(ns, "BD",  bg: "#FFFFFF", fg: "#1E2840", hAlign: "Right"),
                ExSt(ns, "BDN", bg: "#FFFFFF", fg: "#1E2840", hAlign: "Right", numFmt: "0.0"),
                ExSt(ns, "BTL", bg: "#0F2857", fg: "#FFFFFF", bold: true),
                ExSt(ns, "BTN", bg: "#0F2857", fg: "#FFFFFF", bold: true,  hAlign: "Right", numFmt: "0.0")
            );

            // ── Aba 1: Horas por Projeto ──
            var sheet1 = new List<XElement>();

            // Larguras de colunas
            sheet1.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "180")));
            sheet1.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "140")));
            sheet1.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "60")));
            sheet1.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "110")));
            foreach (var _ in visibleMonths)
                sheet1.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "65")));
            sheet1.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "70")));

            // Cabeçalho
            var hdr1 = new XElement(ns + "Row", new XAttribute(ss + "Height", "22"));
            hdr1.Add(ExStCell(ns, AppStrings.Get("PMap_ColProject"),         "AHL"));
            hdr1.Add(ExStCell(ns, AppStrings.Get("PMap_ColResource"),         "AHL"));
            hdr1.Add(ExStCell(ns, AppStrings.Get("PMap_ColType"),            "AH"));
            hdr1.Add(ExStCell(ns, AppStrings.Get("PMap_ColCostCenter"), "AHL"));
            foreach (var mi in visibleMonths) hdr1.Add(ExStCell(ns, months[mi].ToString("MMM/yyyy"), "AH"));
            hdr1.Add(ExStCell(ns, "TOTAL", "AH"));
            sheet1.Add(hdr1);

            var grandByMonth1 = new double[months.Count];

            foreach (var (proj, resRows) in projResourceData)
            {
                var projByMonth = new double[months.Count];
                foreach (var (_, mh) in resRows)
                    for (int mi = 0; mi < months.Count; mi++)
                        projByMonth[mi] += mh[mi];

                // Linha do projeto
                var projRow = new XElement(ns + "Row", new XAttribute(ss + "Height", "20"));
                projRow.Add(ExStCell(ns, proj.Name,                       "AP"));
                projRow.Add(ExStCell(ns, "",                              "AP"));
                projRow.Add(ExStCell(ns, proj.IsOpex ? "OPEX" : "CAPEX", "AP"));
                projRow.Add(ExStCell(ns, proj.CostCenter ?? "",           "AP"));
                foreach (var mi in visibleMonths) projRow.Add(ExStCell(ns, Math.Round(projByMonth[mi], 1), "APN"));
                projRow.Add(ExStCell(ns, Math.Round(projByMonth.Sum(), 1), "APN"));
                sheet1.Add(projRow);

                for (int mi = 0; mi < months.Count; mi++)
                    grandByMonth1[mi] += projByMonth[mi];

                // Sub-linhas de recurso
                foreach (var (resName, mh) in resRows)
                {
                    var resRow = new XElement(ns + "Row", new XAttribute(ss + "Height", "18"));
                    resRow.Add(ExStCell(ns, "", "AR"));
                    resRow.Add(ExStCell(ns, resName, "AR"));
                    resRow.Add(ExStCell(ns, "", "AR"));
                    resRow.Add(ExStCell(ns, "", "AR"));
                    foreach (var mi in visibleMonths) resRow.Add(ExStCell(ns, Math.Round(mh[mi], 1), "ARN"));
                    resRow.Add(ExStCell(ns, Math.Round(mh.Sum(), 1), "ARN"));
                    sheet1.Add(resRow);
                }
            }

            // Linha TOTAL GERAL
            var totRow1 = new XElement(ns + "Row", new XAttribute(ss + "Height", "22"));
            totRow1.Add(ExStCell(ns, "TOTAL GERAL", "AT"));
            totRow1.Add(ExStCell(ns, "", "AT"));
            totRow1.Add(ExStCell(ns, "", "AT"));
            totRow1.Add(ExStCell(ns, "", "AT"));
            foreach (var mi in visibleMonths) totRow1.Add(ExStCell(ns, Math.Round(grandByMonth1[mi], 1), "ATN"));
            totRow1.Add(ExStCell(ns, Math.Round(visibleMonths.Select(mi => grandByMonth1[mi]).Sum(), 1), "ATN"));
            sheet1.Add(totRow1);

            // ── Aba 2: Distribuição por Pessoa ──
            var resList = allResources.OrderBy(r => r).ToList();
            if (hideZero)
                resList = resList.Where(r => projResourceData.Any(p => p.Rows.Any(x => x.Res == r))).ToList();

            var dist2 = projResourceData.Select(p =>
                resList.ToDictionary(r => r,
                    r => p.Rows.FirstOrDefault(x => string.Equals(x.Res, r, StringComparison.OrdinalIgnoreCase))
                               .MonthHours ?? new double[months.Count],
                    StringComparer.OrdinalIgnoreCase))
                .ToList();

            var visibleProj2 = Enumerable.Range(0, projResourceData.Count)
                .Where(pi => !hideZero || resList.Any(r => dist2[pi][r].Sum() > 0.01))
                .ToList();

            var sheet2 = new List<XElement>();

            // Larguras de colunas aba 2
            sheet2.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "140")));
            foreach (var pi in visibleProj2)
            {
                foreach (var _ in visibleMonths)
                    sheet2.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "85")));
                sheet2.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "85")));
            }
            sheet2.Add(new XElement(ns + "Column", new XAttribute(ss + "Width", "70")));

            // Cabeçalho linha 1: nome do projeto mesclado sobre meses + total
            var hdr2a = new XElement(ns + "Row", new XAttribute(ss + "Height", "22"));
            hdr2a.Add(ExStCell(ns, AppStrings.Get("PMap_ColResource"), "AHL"));
            foreach (var pi in visibleProj2)
            {
                var p = projResourceData[pi].Proj;
                string label = $"{p.Name} ({(p.IsOpex ? "OPEX" : "CAPEX")})";
                hdr2a.Add(ExStCell(ns, label, "BH1", mergeAcross: visibleMonths.Count));
            }
            hdr2a.Add(ExStCell(ns, "TOTAL GERAL", "AHL"));
            sheet2.Add(hdr2a);

            // Cabeçalho linha 2: meses + "Total" por projeto
            var hdr2b = new XElement(ns + "Row", new XAttribute(ss + "Height", "20"));
            hdr2b.Add(ExStCell(ns, "", "AHL"));
            foreach (var pi in visibleProj2)
            {
                foreach (var mi in visibleMonths) hdr2b.Add(ExStCell(ns, months[mi].ToString("MMM/yyyy"), "BH2"));
                hdr2b.Add(ExStCell(ns, AppStrings.Get("PMap_Total"), "BH2"));
            }
            hdr2b.Add(ExStCell(ns, "", "AHL"));
            sheet2.Add(hdr2b);

            var grandByProjMonth2 = visibleProj2.ToDictionary(pi => pi, _ => new double[months.Count]);

            foreach (var resName in resList)
            {
                var totalByMonth = new double[months.Count];
                foreach (var pi in visibleProj2)
                    for (int mi = 0; mi < months.Count; mi++)
                        totalByMonth[mi] += dist2[pi][resName][mi];

                double resTotal = totalByMonth.Sum();
                var dataRow = new XElement(ns + "Row", new XAttribute(ss + "Height", "18"));
                dataRow.Add(ExStCell(ns, resName, "BR"));

                foreach (var pi in visibleProj2)
                {
                    double projResTotal = 0;
                    foreach (var mi in visibleMonths)
                    {
                        double h   = dist2[pi][resName][mi];
                        double pct = totalByMonth[mi] > 0.01 ? h / totalByMonth[mi] * 100 : 0;
                        projResTotal += h;
                        grandByProjMonth2[pi][mi] += h;
                        dataRow.Add(ExStCell(ns, $"{h:0.#}h / {pct:0.#}%", "BD"));
                    }
                    double projPct = resTotal > 0.01 ? projResTotal / resTotal * 100 : 0;
                    dataRow.Add(ExStCell(ns, $"{projResTotal:0.#}h / {projPct:0.#}%", "BD"));
                }
                dataRow.Add(ExStCell(ns, Math.Round(resTotal, 1), "BDN"));
                sheet2.Add(dataRow);
            }

            // Total geral aba 2
            var totRow2 = new XElement(ns + "Row", new XAttribute(ss + "Height", "22"));
            totRow2.Add(ExStCell(ns, "TOTAL GERAL", "BTL"));
            double grand2 = 0;
            foreach (var pi in visibleProj2)
            {
                foreach (var mi in visibleMonths)
                    totRow2.Add(ExStCell(ns, Math.Round(grandByProjMonth2[pi][mi], 1), "BTN"));
                double ps = visibleMonths.Sum(mi => grandByProjMonth2[pi][mi]);
                grand2 += ps;
                totRow2.Add(ExStCell(ns, Math.Round(ps, 1), "BTN"));
            }
            totRow2.Add(ExStCell(ns, Math.Round(grand2, 1), "BTN"));
            sheet2.Add(totRow2);

            // ── Gera o arquivo com apenas a aba solicitada ──
            var worksheets = new List<XElement>();
            if (onlyTab == 0)
                worksheets.Add(new XElement(ns + "Worksheet",
                    new XAttribute(ss + "Name", AppStrings.Get("PMap_TabHours")),
                    new XElement(ns + "Table", sheet1)));
            else
                worksheets.Add(new XElement(ns + "Worksheet",
                    new XAttribute(ss + "Name", AppStrings.Get("PMap_TabDistribution")),
                    new XElement(ns + "Table", sheet2)));

            var workbook = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(ns + "Workbook",
                    new XAttribute(XNamespace.Xmlns + "ss", ss),
                    styles,
                    worksheets));

            workbook.Save(filePath);
        }

        private static XElement ExXmlRow(XNamespace ns, params string[] values)
        {
            return new XElement(ns + "Row",
                values.Select(v => new XElement(ns + "Cell",
                    new XElement(ns + "Data",
                        new XAttribute(ns + "Type", "String"), v))));
        }

        private static XElement ExXmlRowMixed(XNamespace ns, params object[] values)
        {
            return new XElement(ns + "Row",
                values.Select(v =>
                {
                    bool isNum = v is double or float or int;
                    return new XElement(ns + "Cell",
                        new XElement(ns + "Data",
                            new XAttribute(ns + "Type", isNum ? "Number" : "String"),
                            isNum ? ((double)Convert.ToDouble(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) : v.ToString()));
                }));
        }

        private void OnSaveConfigClick(object sender, RoutedEventArgs e)
        {
            var opts = TfsConnectionStore.Load();
            foreach (var proj in _projects)
            {
                var cfg = opts.PortfolioProjectConfigs
                    .FirstOrDefault(c => string.Equals(c.ProjectName, proj.Name,
                                         StringComparison.OrdinalIgnoreCase));
                if (cfg == null)
                {
                    cfg = new PortfolioProjectConfig { ProjectName = proj.Name };
                    opts.PortfolioProjectConfigs.Add(cfg);
                }
                cfg.IsOpex     = proj.IsOpex;
                cfg.CostCenter = proj.CostCenter;
            }
            TfsConnectionStore.Save(opts, !string.IsNullOrEmpty(opts.PersonalAccessToken));
            StatusText.Text = "Configuração salva com sucesso.";
        }
    }
}
