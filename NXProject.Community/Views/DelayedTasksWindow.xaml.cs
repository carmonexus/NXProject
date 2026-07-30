using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NXProject.Models;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Views
{
    public partial class DelayedTasksWindow : Window
    {
        private readonly MainViewModel _vm;
        private string? _selectedResource;
        private DelayBucket? _selectedBucket;
        // Curva S: quando marcado, inclui Stories com % de conclusão = 0 e tasks "New"
        // (visão planejada). Desmarcado (padrão), a linha do real considera só o que
        // está em execução: Story > 0% e tasks Active/Closed.
        private bool _includeZeroPct;
        // Curva S: quando marcado, a linha do realizado soma HH Atual + HH Restante (duração
        // cheia). Padrão desmarcado = só o concluído (HH × % conclusão).
        private bool _includeRemaining;
        // Terceira linha: base line carregado de um arquivo .nxp (snapshot) — distribui o HH
        // Atual + Restante das Stories do baseline pelas datas, como referência de plano.
        private bool _showBaseline;
        private Models.Project? _baselineProject;

        // Dados pré-calculados para tooltip da curva
        private List<SprintPoint>? _curvePoints;
        // Composição do PLANEJADO por ponto do eixo (para o drill-down ao clicar na curva):
        // janela do balde + as atividades usadas no cálculo.
        private List<(DateTime Start, DateTime End)>? _curveBuckets;
        private List<TaskViewModel>? _curvePlannedTasks;
        // Fim do cronograma (última atividade). Depois dele o eixo existe só pela projeção.
        private DateTime _scheduleEnd = DateTime.MinValue;
        // Atividades empurradas para cada semana de projeção pela velocidade (índice do ponto).
        private Dictionary<int, List<(ProjectTask Task, double Hours)>>? _forecastByPoint;
        private readonly double _chartLeft   = 64;
        private readonly double _chartTop    = 20;
        private readonly double _chartRight  = 24;
        private readonly double _chartBottom = 50;

        public DelayedTasksWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            BuildMatrix();
            BuildAllDelayedList();
            BuildBlockedList();
        }

        // ── Buckets ──────────────────────────────────────────────────────────

        private enum DelayBucket { OneDay, TwoDays, ThreeDays, OneWeek, OneSprint }

        private static readonly (DelayBucket Bucket, string Header)[] BucketDefs =
        [
            (DelayBucket.OneDay,    "Delay_Bucket1d"),
            (DelayBucket.TwoDays,   "Delay_Bucket2d"),
            (DelayBucket.ThreeDays, "Delay_Bucket3d"),
            (DelayBucket.OneWeek,   "Delay_Bucket1w"),
            (DelayBucket.OneSprint, "Delay_Bucket1Sprint")
        ];

        private DelayBucket ClassifyDelay(double workingDays, int sprintDays)
        {
            if (workingDays <= 1.5) return DelayBucket.OneDay;
            if (workingDays <= 2.5) return DelayBucket.TwoDays;
            if (workingDays <= 3.5) return DelayBucket.ThreeDays;
            if (workingDays < sprintDays) return DelayBucket.OneWeek;
            return DelayBucket.OneSprint;
        }

        private static double ComputeDelayDays(ProjectTask task)
        {
            if (task.Finish.Date >= DateTime.Today) return 0;
            var hours = ProjectCalendarService.CountWorkingHours(task.Finish.Date, DateTime.Today);
            return hours / Math.Max(1, ProjectCalendarService.WorkingHoursPerDay);
        }

        // ── Helpers de sprint ────────────────────────────────────────────────

        private sealed record SprintInfo(int Number, string? Path, string Label, DateTime Start, DateTime End);

        private List<SprintInfo> GetOrderedSprints()
        {
            if (_vm.Project.Sprints.Count > 0)
            {
                return _vm.Project.Sprints
                    .OrderBy(s => s.Number).ThenBy(s => s.Start)
                    .Select(s => new SprintInfo(
                        s.Number,
                        s.Path,
                        string.IsNullOrWhiteSpace(s.Name) ? $"Sprint {s.Number}" : s.Name,
                        s.Start,
                        s.End))
                    .ToList();
            }
            // Sem sprints configuradas: usa números das tarefas
            return _vm.FlatTasks
                .Where(t => t.SprintNumber > 0)
                .Select(t => t.SprintNumber)
                .Distinct()
                .OrderBy(n => n)
                .Select(n => new SprintInfo(n, null, $"Sprint {n}", DateTime.MinValue, DateTime.MaxValue))
                .ToList();
        }

        // Períodos semanais (segunda→domingo) cobrindo [start, end], um ponto por semana.
        private static List<SprintInfo> BuildWeeklyPeriods(DateTime start, DateTime end)
        {
            var list = new List<SprintInfo>();
            var d = start.Date;
            while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(-1);   // ancora na segunda
            int n = 1;
            int guard = 0;
            while (d <= end.Date && guard++ < 600)
            {
                var wend = d.AddDays(6);   // domingo
                list.Add(new SprintInfo(n, null, d.ToString("dd/MM"), d, wend));
                d = d.AddDays(7);
                n++;
            }
            return list;
        }

        private static double PctOf(ProjectTask t) => Math.Clamp(t.PercentComplete, 0, 100) / 100.0;
        private static DateTime AddWorkingDaysApprox(DateTime from, double workingDays)
            => from.AddDays((int)Math.Ceiling(Math.Max(0, workingDays) * 7.0 / 5.0));

        private int GetTaskSprint(TaskViewModel task)
        {
            if (!string.IsNullOrWhiteSpace(task.Model.TfsIterationPath))
            {
                var match = _vm.Project.Sprints
                    .FirstOrDefault(s => string.Equals(s.Path, task.Model.TfsIterationPath,
                                         StringComparison.OrdinalIgnoreCase));
                if (match != null) return match.Number;
            }
            if (task.SprintNumber > 0) return task.SprintNumber;
            // fallback: atribui pelo Finish da tarefa
            var sp = _vm.Project.Sprints
                .OrderBy(s => s.Number)
                .FirstOrDefault(s => task.Model.Finish.Date <= s.End.Date);
            return sp?.Number ?? 0;
        }

        private string GetTaskSprintLabel(TaskViewModel task)
        {
            if (!string.IsNullOrWhiteSpace(task.Model.TfsIterationPath))
            {
                var match = _vm.Project.Sprints
                    .FirstOrDefault(s => string.Equals(s.Path, task.Model.TfsIterationPath,
                                         StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return string.IsNullOrWhiteSpace(match.Name) ? $"Sprint {match.Number}" : match.Name;
                // Retorna a última parte do path
                var parts = task.Model.TfsIterationPath.Split('\\', '/');
                return parts[^1];
            }
            if (task.SprintNumber > 0) return $"Sprint {task.SprintNumber}";
            return "—";
        }

        // ── ABA 1: Matriz de atrasos ─────────────────────────────────────────

        private void BuildMatrix()
        {
            DelayGrid.Children.Clear();
            DelayGrid.RowDefinitions.Clear();
            DelayGrid.ColumnDefinitions.Clear();

            var today = DateTime.Today;
            var sprintDays = Math.Max(5, _vm.Project.SprintDurationDays);

            var delayed = CollectDelayed(today, sprintDays);

            var resources = delayed.Select(d => d.Resource).Distinct().OrderBy(r => r).ToList();
            if (resources.Count == 0)
            {
                SummaryText.Text = AppStrings.Get("Delay_NoDelayed");
                return;
            }
            SummaryText.Text = AppStrings.Get("Delay_SummaryByResource", delayed.Count, resources.Count);

            DelayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            foreach (var _ in BucketDefs)
                DelayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

            DelayGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            AddHeaderCell(AppStrings.Get("Delay_HeaderResource"), 0, 0);
            for (int c = 0; c < BucketDefs.Length; c++)
                AddHeaderCell(AppStrings.Get(BucketDefs[c].Header), 0, c + 1);

            for (int r = 0; r < resources.Count; r++)
            {
                var res = resources[r];
                DelayGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                AddLabelCell(res, r + 1, 0);
                for (int c = 0; c < BucketDefs.Length; c++)
                {
                    var b = BucketDefs[c].Bucket;
                    var count = delayed.Count(d => d.Resource == res && d.Bucket == b);
                    AddCountButton(res, b, count, r + 1, c + 1);
                }
            }

            var totalRow = resources.Count + 1;
            DelayGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            AddHeaderCell(AppStrings.Get("Delay_Total"), totalRow, 0, HorizontalAlignment.Left);
            for (int c = 0; c < BucketDefs.Length; c++)
            {
                var b = BucketDefs[c].Bucket;
                var t = delayed.Count(d => d.Bucket == b);
                AddHeaderCell(t > 0 ? t.ToString() : "—", totalRow, c + 1);
            }
        }

        private List<(TaskViewModel Task, DelayBucket Bucket, string Resource)> CollectDelayed(
            DateTime today, int sprintDays)
        {
            return _vm.FlatTasks
                .Where(t => t.Model.Children.Count == 0
                         && t.Model.PercentComplete < 100
                         && t.Model.Finish.Date < today)
                .Select(t =>
                {
                    var rawH = ProjectCalendarService.CountWorkingHours(t.Model.Finish.Date, today);
                    var days = rawH / Math.Max(1, ProjectCalendarService.WorkingHoursPerDay);
                    return (Task: t,
                            Bucket: ClassifyDelay(days, sprintDays),
                            Resource: t.Model.Resources.FirstOrDefault()?.Resource?.Name ?? AppStrings.Get("Delay_NoResource"));
                })
                .ToList();
        }

        private void AddHeaderCell(string text, int row, int col,
            HorizontalAlignment ha = HorizontalAlignment.Center)
        {
            var b = MakeBorder(true);
            b.Child = new TextBlock
            {
                Text = text, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = ha, Margin = new Thickness(8, 0, 8, 0)
            };
            Grid.SetRow(b, row); Grid.SetColumn(b, col);
            DelayGrid.Children.Add(b);
        }

        private void AddLabelCell(string text, int row, int col)
        {
            var b = MakeBorder(false);
            b.Background = new SolidColorBrush(Color.FromRgb(235, 239, 246));
            b.Child = new TextBlock
            {
                Text = text, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(8, 0, 8, 0)
            };
            Grid.SetRow(b, row); Grid.SetColumn(b, col);
            DelayGrid.Children.Add(b);
        }

        private void AddCountButton(string resourceName, DelayBucket bucket, int count, int row, int col)
        {
            var hasItems = count > 0;
            var btn = new Button
            {
                Content = hasItems ? count.ToString() : "—",
                Tag = (resourceName, bucket),
                BorderThickness = new Thickness(0),
                Background = hasItems ? new SolidColorBrush(Color.FromRgb(255, 235, 200)) : Brushes.White,
                Foreground = hasItems
                    ? new SolidColorBrush(Color.FromRgb(180, 60, 0))
                    : new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                FontWeight = hasItems ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize = hasItems ? 13 : 12,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = hasItems
                    ? AppStrings.Get("Delay_CellCount", count, AppStrings.Get(BucketDefs.First(b => b.Bucket == bucket).Header))
                    : AppStrings.Get("Delay_NoDelayInBucket")
            };
            btn.Click += OnCountCellClick;
            var border = MakeBorder(false);
            border.Child = btn;
            Grid.SetRow(border, row); Grid.SetColumn(border, col);
            DelayGrid.Children.Add(border);
        }

        private static Border MakeBorder(bool header) => new()
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(219, 225, 234)),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Background = header ? new SolidColorBrush(Color.FromRgb(235, 239, 246)) : Brushes.White
        };

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            BuildMatrix();
            BuildAllDelayedList();
            BuildBlockedList();
            if (_selectedResource != null && _selectedBucket.HasValue)
                ShowMatrixDetails(_selectedResource, _selectedBucket.Value);
            if (CurveCanvas.ActualWidth > 0)
                RenderCurve();
        }

        private void OnCountCellClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ValueTuple<string, DelayBucket> t })
                ShowMatrixDetails(t.Item1, t.Item2);
        }

        private void ShowMatrixDetails(string resourceName, DelayBucket bucket)
        {
            _selectedResource = resourceName;
            _selectedBucket = bucket;
            DetailsTitle.Text = AppStrings.Get("Delay_DetailsTitle", resourceName, AppStrings.Get(BucketDefs.First(b => b.Bucket == bucket).Header));

            var today = DateTime.Today;
            var sprintDays = Math.Max(5, _vm.Project.SprintDurationDays);

            DetailsGrid.ItemsSource = CollectDelayed(today, sprintDays)
                .Where(x => x.Resource == resourceName && x.Bucket == bucket)
                .Select(x => new DelayedTaskRow(x.Task, _vm))
                .ToList();
        }

        // ── ABA 2: Todas as atividades atrasadas ─────────────────────────────

        private void BuildAllDelayedList()
        {
            var today = DateTime.Today;
            var rows = _vm.FlatTasks
                .Where(t => t.Model.Children.Count == 0
                         && t.Model.PercentComplete < 100
                         && t.Model.Finish.Date < today)
                .OrderByDescending(t => ComputeDelayDays(t.Model))
                .Select(t => new DelayedTaskRow(t, _vm))
                .ToList();

            AllDelayedGrid.ItemsSource = rows;
            AllDelayedSummary.Text = rows.Count > 0
                ? AppStrings.Get("Delay_AllDelayedSummary", rows.Count, rows.Sum(r => r.RemainingHours))
                : AppStrings.Get("Delay_NoDelayed");
        }

        // ── ABA 3: Curva S ───────────────────────────────────────────────────

        private sealed record SprintPoint(
            int SprintNumber,
            string Label,
            double PlannedPct,   // % acumulado de HH Original
            double ActualPct,    // % acumulado do realizado (concluído + previsão)
            bool IsFuture,
            bool IsCurrent,
            double BaselinePct = -1);   // % acumulado do base line carregado (-1 = sem base line)

        private void RenderCurve()
        {
            CurveCanvas.Children.Clear();
            CurveCanvas.Children.Add(CurveTooltip);

            var w = CurveCanvas.ActualWidth;
            var h = CurveCanvas.ActualHeight;
            if (w < 100 || h < 80) return;

            var sprints = GetOrderedSprints();
            if (sprints.Count == 0)
            {
                DrawNoDataMessage(AppStrings.Get("Delay_NoSprintsCurve"));
                return;
            }

            // Planejado (azul) SEMPRE inclui Story % = 0; realizado (laranja) filtra pelo flag.
            // Com sprints datadas usamos as datas (Início→Fim) para posicionar; sem datas, o número.
            var hasSprintDates = sprints.Any(s => s.Start != DateTime.MinValue);
            List<TaskViewModel> plannedWithSprint, actualWithSprint;
            if (hasSprintDates)
            {
                bool Dated(TaskViewModel t) => t.Model.Start != DateTime.MinValue && t.Model.Finish != DateTime.MinValue;
                plannedWithSprint = GetPlannedTasks().Where(Dated).ToList();
                actualWithSprint  = GetActualTasks().Where(Dated).ToList();
            }
            else
            {
                var sprintNumbers = new HashSet<int>(sprints.Select(s => s.Number));
                plannedWithSprint = GetPlannedTasks().Where(t => sprintNumbers.Contains(GetTaskSprint(t))).ToList();
                actualWithSprint  = GetActualTasks().Where(t => sprintNumbers.Contains(GetTaskSprint(t))).ToList();
            }

            var totalOriginalHours = plannedWithSprint.Sum(t => GetOriginalHours(t.Model));
            if (totalOriginalHours < 0.01)
            {
                DrawNoDataMessage(AppStrings.Get("Delay_NoHoursCurve"));
                return;
            }

            var today = DateTime.Today;

            // Previsão por VELOCIDADE: o restante é entregue a partir de HOJE no ritmo histórico
            // (horas concluídas ÷ dias úteis decorridos) — olha o passado para projetar o futuro,
            // e não usa o ritmo do cronograma. Só quando "Incluir HH Restante" está marcado.
            double hpd = Math.Max(1, ProjectCalendarService.WorkingHoursPerDay);
            double completedHours = actualWithSprint.Sum(t => GetTotalHours(t.Model) * PctOf(t.Model));
            double remainingHours = _includeRemaining
                ? actualWithSprint.Sum(t => GetTotalHours(t.Model) * (1 - PctOf(t.Model)))
                : 0;
            double velPerDay = 0;
            if (hasSprintDates && plannedWithSprint.Count > 0)
            {
                var minStart = plannedWithSprint.Min(t => t.Model.Start.Date);
                var elapsedDays = ProjectCalendarService.CountWorkingHours(minStart, today) / hpd;
                velPerDay = elapsedDays > 0 ? completedHours / elapsedDays : 0;
            }

            // Períodos do eixo X: com datas, um ponto por SEMANA (segunda a domingo), do início do
            // trabalho até o mais distante entre o Fim das atividades e a conclusão projetada pela
            // velocidade. Mais pontos = barriga mais suave. Sem datas, cai nas sprints.
            List<SprintInfo> periods;
            int currentSprintNumber;
            // Régua de sprints (marcador de tempo): sprints configuradas + projetadas.
            var sprintMarks = new List<(string Name, DateTime Start, DateTime End, bool Proj)>();
            if (hasSprintDates)
            {
                var minStart = plannedWithSprint.Count > 0 ? plannedWithSprint.Min(t => t.Model.Start.Date) : sprints[0].Start;
                if (minStart > sprints[0].Start) minStart = sprints[0].Start;
                var maxFin = GetCurveTasksBase().Select(t => t.Model.Finish.Date)
                    .DefaultIfEmpty(sprints[^1].End).Max();
                var projEnd = today;
                if (remainingHours > 0.01 && velPerDay > 0.01)
                    projEnd = AddWorkingDaysApprox(today, remainingHours / velPerDay);
                var axisEnd = maxFin > projEnd ? maxFin : projEnd;
                _scheduleEnd = maxFin;   // fim do cronograma: depois disso o eixo é só projeção
                periods = BuildWeeklyPeriods(minStart, axisEnd);
                currentSprintNumber = periods.FirstOrDefault(p => today >= p.Start && today <= p.End)?.Number
                                      ?? periods[^1].Number;

                foreach (var s in sprints)
                    sprintMarks.Add((s.Label, s.Start.Date, s.End.Date, false));
                var durS = Math.Max(1, _vm.Project.SprintDurationDays);
                var st = sprints[^1].End.Date.AddDays(1);
                var nS = sprints[^1].Number; int gS = 0;
                while (st <= axisEnd.Date && gS++ < 500)
                {
                    var e = st.AddDays(durS - 1); nS++;
                    sprintMarks.Add(($"S{nS} (proj.)", st, e, true));
                    st = e.AddDays(1);
                }
            }
            else
            {
                periods = sprints;
                currentSprintNumber = DetermineCurrentSprint(sprints, today);
            }

            // Base line carregado (opcional): Stories do snapshot, HH Atual+Restante pelas datas.
            List<ProjectTask>? baselineStories = null;
            double baselineTotal = 0;
            if (_showBaseline && _baselineProject != null)
            {
                baselineStories = FlattenModel(_baselineProject.Tasks)
                    .Where(t => TfsImportService.IsStoryTypePublic(t.TfsType)
                                && t.Start != DateTime.MinValue && t.Finish != DateTime.MinValue)
                    .ToList();
                baselineTotal = baselineStories.Sum(GetTotalHours);
            }

            var points = BuildCurvePoints(periods, plannedWithSprint, actualWithSprint, totalOriginalHours,
                                          currentSprintNumber, velPerDay, remainingHours, today,
                                          baselineStories, baselineTotal);
            _curvePoints = points;

            var pl = _chartLeft; var pt = _chartTop;
            var pr = w - _chartRight; var pb = h - _chartBottom;
            var pw = pr - pl; var ph = pb - pt;

            BuildForecastQueue(points, actualWithSprint, velPerDay, today);

            DrawGridAndAxes(pl, pt, pr, pb, pw, ph, points);
            DrawForecastZone(pl, pt, pb, pw, points);
            DrawSprintBands(pl, pt, pb, pw, points, periods, sprintMarks);
            DrawCurrentSprintMarker(pl, pt, pb, pw, points, currentSprintNumber);

            var plannedLine = points.Select(p => ToCanvasPoint(p.SprintNumber - points[0].SprintNumber,
                                                                p.PlannedPct, points.Count, pl, pt, pw, ph)).ToList();
            var durationLine = points.Select(p => ToCanvasPoint(p.SprintNumber - points[0].SprintNumber,
                                                                 p.ActualPct, points.Count, pl, pt, pw, ph)).ToList();
            SeparateOverlappingCurvePoints(plannedLine, durationLine, pt, pb);

            // Linha azul sólida: HH Original acumulado (baseline planejado)
            DrawPolyline(plannedLine,
                         "#1F4EA1", 2.5, false);

            // Linha laranja sólida: realizado (concluído + previsão pela velocidade)
            DrawPolyline(durationLine,
                         "#E65100", 2.5, false);

            // Linha verde tracejada: base line carregado (HH Atual+Restante do snapshot)
            if (points.Any(p => p.BaselinePct >= 0))
            {
                var baselineLine = points.Select(p => ToCanvasPoint(p.SprintNumber - points[0].SprintNumber,
                                                                     Math.Max(0, p.BaselinePct), points.Count, pl, pt, pw, ph)).ToList();
                DrawPolyline(baselineLine, "#2E9E8F", 2.0, true);
            }

            DrawSprintLabels(pl, pb, pw, points);

            var currentPoint = points.LastOrDefault(p => p.IsCurrent)
                ?? points.LastOrDefault(p => !p.IsFuture)
                ?? points.LastOrDefault();
            var gap = currentPoint != null ? currentPoint.PlannedPct - currentPoint.ActualPct : 0;
            var summary = currentPoint != null
                ? AppStrings.Get("Delay_CurveSummary", currentPoint.ActualPct, currentPoint.PlannedPct, gap)
                : string.Empty;
            int cfg = sprintMarks.Count(m => !m.Proj), proj = sprintMarks.Count(m => m.Proj);
            if (cfg + proj > 0)
                summary += AppStrings.Get("Delay_SprintCount", cfg, proj);
            CurveSummary.Text = summary;
        }

        // ── Previsão: quem é empurrado para depois do cronograma ─────────────────
        // Distribui o HH RESTANTE nas semanas a partir de hoje, no ritmo da velocidade
        // histórica (velPerDay). A fila segue a ordem do cronograma (fim, depois início), de
        // modo que a lista responde "quais stories caem nesta semana projetada".
        private void BuildForecastQueue(List<SprintPoint> points, List<TaskViewModel> actualTasks,
                                        double velPerDay, DateTime today)
        {
            _forecastByPoint = new Dictionary<int, List<(ProjectTask, double)>>();
            if (_curveBuckets == null || velPerDay <= 0.01 || !_includeRemaining) return;

            // Fila do restante, na ordem em que o cronograma pretendia entregar.
            var queue = actualTasks
                .Select(t => (Task: t.Model, Remaining: GetTotalHours(t.Model) * (1 - PctOf(t.Model))))
                .Where(x => x.Remaining > 0.01)
                .OrderBy(x => x.Task.Finish).ThenBy(x => x.Task.Start)
                .ToList();

            int qi = 0;
            double left = queue.Count > 0 ? queue[0].Remaining : 0;
            double hpd = Math.Max(1, ProjectCalendarService.WorkingHoursPerDay);

            for (int i = 0; i < points.Count && qi < queue.Count; i++)
            {
                if (i >= _curveBuckets.Count) break;
                var (bs, be) = _curveBuckets[i];
                if (bs == DateTime.MinValue || be <= today) continue;

                var from = today > bs.Date ? today : bs.Date;
                var capacity = velPerDay * (ProjectCalendarService.CountWorkingHours(from, be.Date) / hpd);
                if (capacity <= 0.01) continue;

                var rows = new List<(ProjectTask, double)>();
                while (capacity > 0.01 && qi < queue.Count)
                {
                    var take = Math.Min(capacity, left);
                    rows.Add((queue[qi].Task, take));
                    capacity -= take;
                    left     -= take;
                    if (left <= 0.01 && ++qi < queue.Count) left = queue[qi].Remaining;
                }

                if (rows.Count > 0) _forecastByPoint[i] = rows;
            }
        }

        // Sombreado das semanas posteriores ao fim do cronograma: ali o eixo existe só por
        // causa da projeção pela velocidade.
        private void DrawForecastZone(double pl, double pt, double pb, double pw, List<SprintPoint> points)
        {
            if (_scheduleEnd == DateTime.MinValue || _curveBuckets == null || points.Count < 2) return;

            int firstProjected = -1;
            for (int i = 0; i < points.Count && i < _curveBuckets.Count; i++)
            {
                var (bs, _) = _curveBuckets[i];
                if (bs != DateTime.MinValue && bs.Date > _scheduleEnd.Date) { firstProjected = i; break; }
            }
            if (firstProjected < 0) return;

            var x = pl + pw * firstProjected / Math.Max(1, points.Count - 1);
            var band = new Rectangle
            {
                Width  = Math.Max(0, pl + pw - x),
                Height = Math.Max(0, pb - pt),
                Fill   = new SolidColorBrush(Color.FromArgb(28, 0x8E, 0x24, 0xAA)),
                IsHitTestVisible = false
            };
            System.Windows.Controls.Canvas.SetLeft(band, x);
            System.Windows.Controls.Canvas.SetTop(band, pt);
            CurveCanvas.Children.Add(band);

            var lbl = new TextBlock
            {
                Text       = AppStrings.Get("Delay_ForecastZone"),
                FontSize   = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x1B, 0x9A)),
                IsHitTestVisible = false
            };
            System.Windows.Controls.Canvas.SetLeft(lbl, x + 4);
            System.Windows.Controls.Canvas.SetTop(lbl, pt + 2);
            CurveCanvas.Children.Add(lbl);
        }

        // HH Original da tarefa; usa EstimatedHours como fallback.
        private static double GetOriginalHours(ProjectTask task) =>
            WithTaskSummary(task,
                task.OriginalEstimatedHours is > 0
                    ? task.OriginalEstimatedHours.Value
                    : TaskScheduleService.GetEffectiveDurationHours(task));

        private static double GetEstimatedHours(ProjectTask task) =>
            task.EstimatedHours is > 0
                ? task.EstimatedHours.Value
                : TaskScheduleService.GetEffectiveDurationHours(task);

        // Horas do REALIZADO (numerador da linha laranja):
        //  - padrão: só o concluído = HH Duração × % conclusão (Curva S clássica / valor agregado);
        //  - com "Incluir HH Restante": HH Atual + HH Restante (duração cheia).
        private double GetRealizedHours(ProjectTask task)
        {
            var duration = GetTotalHours(task);
            if (_includeRemaining) return duration;
            var pct = Math.Clamp(task.PercentComplete, 0, 100) / 100.0;
            return duration * pct;
        }

        // Duração total = HH Atual + HH Restante quando HH Atual disponível; senão EstimatedHours ou duração calculada.
        private static double GetTotalHours(ProjectTask task) =>
            WithTaskSummary(task,
                task.CurrentHours is > 0
                    ? task.CurrentHours.Value + (task.EstimatedHours ?? 0)
                    : task.EstimatedHours is > 0
                        ? task.EstimatedHours.Value
                        : TaskScheduleService.GetEffectiveDurationHours(task));

        // O HH da Story manda; mas quando o RESUMO DE TASKS soma mais que ela (tasks abertas no
        // DevOps que já estouram a Story), a curva usa o do resumo — é o trabalho real que existe.
        private static double WithTaskSummary(ProjectTask task, double storyHours)
        {
            if (task.TaskAllocations.Count == 0) return storyHours;
            var summary = task.TaskAllocations.Sum(a => a.Hours);
            return summary > storyHours ? summary : storyHours;
        }

        // Responsável exibido nas listas: o recurso da Story; se ela não tem recurso próprio,
        // o dono do resumo de tasks com mais horas.
        private static string ResponsibleOf(ProjectTask task)
        {
            var own = task.Resources
                .Select(r => r.Resource?.Name)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            if (!string.IsNullOrWhiteSpace(own)) return own!;

            return task.TaskAllocations
                .OrderByDescending(a => a.Hours)
                .Select(a => a.Resource)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";
        }

        // Conjunto base da curva: stories (ou folhas no nível da story quando não há stories).
        private List<TaskViewModel> GetCurveTasksBase()
        {
            var storyTasks = _vm.FlatTasks
                .Where(t => TfsImportService.IsStoryTypePublic(t.Model.TfsType))
                .ToList();
            if (storyTasks.Count > 0)
                return storyTasks;

            var leaves = _vm.FlatTasks.Where(t => t.Model.Children.Count == 0).ToList();
            var storyDepth = leaves.Count > 0 ? leaves.Min(t => t.Depth) : 0;
            return leaves.Where(t => t.Depth == storyDepth).ToList();
        }

        private static IEnumerable<ProjectTask> FlattenModel(IEnumerable<ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                yield return t;
                foreach (var c in FlattenModel(t.Children)) yield return c;
            }
        }

        // Planejado (HH Original): SEMPRE inclui Story com % = 0 (baseline completo).
        private List<TaskViewModel> GetPlannedTasks() => GetCurveTasksBase();

        // Realizado (HH Duração): só o que está em execução — Story > 0% (folha Active/Closed);
        // com o flag "Incluir planejado", passa a incluir também Story % = 0 e tasks New.
        private List<TaskViewModel> GetActualTasks()
            => GetCurveTasksBase().Where(ActualIncludes).ToList();

        private bool ActualIncludes(TaskViewModel t)
        {
            if (_includeZeroPct) return true;
            if (TfsImportService.IsStoryTypePublic(t.Model.TfsType))
                return t.Model.PercentComplete > 0;
            return t.Model.PercentComplete > 0 || TfsImportService.AllocationCountsState(t.Model.TfsState);
        }

        private List<SprintPoint> BuildCurvePoints(
            List<SprintInfo> sprints, List<TaskViewModel> plannedTasks, List<TaskViewModel> actualTasks,
            double totalOriginalHours, int currentSprintNumber,
            double velPerDay, double remainingHours, DateTime today,
            List<ProjectTask>? baselineStories, double baselineTotal)
        {
            // Denominador da linha do realizado = duração TOTAL do plano (todas as Stories).
            var totalDurationHours = plannedTasks.Sum(t => GetTotalHours(t.Model));
            if (totalDurationHours < 0.01) totalDurationHours = totalOriginalHours;
            bool hasBaseline = baselineStories is { Count: > 0 } && baselineTotal > 0.01;
            double cumBaseline = 0;

            var hasDates = sprints.Any(s => s.Start != DateTime.MinValue);
            double hpd = Math.Max(1, ProjectCalendarService.WorkingHoursPerDay);

            // Baldes ancorados no FIM REAL da sprint: bucket i = [sprint[i-1].End+1, sprint[i].End+1).
            // O gap entre uma sprint e a seguinte vai para a PRÓXIMA (não infla a atual). O 1º recua
            // até a atividade mais antiga; o último avança até a mais recente.
            DateTime[] bucketStart = Array.Empty<DateTime>();
            DateTime[] bucketEnd   = Array.Empty<DateTime>();
            if (hasDates)
            {
                var allTasks = plannedTasks.Concat(actualTasks).ToList();
                var minStart = allTasks.Count > 0 ? allTasks.Min(t => t.Model.Start.Date) : sprints[0].Start;
                var maxFin   = allTasks.Count > 0 ? allTasks.Max(t => t.Model.Finish.Date) : sprints[^1].End;
                bucketStart = new DateTime[sprints.Count];
                bucketEnd   = new DateTime[sprints.Count];
                for (int i = 0; i < sprints.Count; i++)
                {
                    bucketStart[i] = i == 0
                        ? (minStart < sprints[0].Start ? minStart : sprints[0].Start)
                        : sprints[i - 1].End.Date.AddDays(1);
                    bucketEnd[i] = i + 1 < sprints.Count
                        ? sprints[i].End.Date.AddDays(1)
                        : (maxFin > sprints[i].End.Date ? maxFin.AddDays(1) : sprints[i].End.Date.AddDays(1));
                }
            }

            // Guarda a composição para o drill-down (clique no ponto da semana).
            _curvePlannedTasks = plannedTasks;
            _curveBuckets = new List<(DateTime, DateTime)>();
            for (int i = 0; i < sprints.Count; i++)
                _curveBuckets.Add(hasDates
                    ? (bucketStart[i], bucketEnd[i])
                    : (sprints[i].Start, sprints[i].End));

            var points = new List<SprintPoint>();
            double cumPlanned  = 0;
            double cumProgress = 0;
            double deliveredForecast = 0;   // restante já entregue pela velocidade (acumulado)

            for (int i = 0; i < sprints.Count; i++)
            {
                var sprint = sprints[i];
                double planned;
                double progress;

                if (hasDates)
                {
                    var bs = bucketStart[i]; var be = bucketEnd[i];
                    // Planejado: HH Original distribuído pela faixa Início→Fim.
                    planned = plannedTasks.Sum(t => DistributeHours(GetOriginalHours(t.Model), t.Model.Start, t.Model.Finish, bs, be));

                    // Realizado = CONCLUÍDO (HH×%) no passado até HOJE
                    //           + RESTANTE entregue de HOJE p/ frente na VELOCIDADE histórica.
                    double completedInBucket = actualTasks.Sum(t => DistributeHours(
                        GetTotalHours(t.Model) * PctOf(t.Model),
                        t.Model.Start, t.Model.Finish.Date < today ? t.Model.Finish.Date : today, bs, be));

                    double forecastInBucket = 0;
                    if (remainingHours > 0.01 && velPerDay > 0.01)
                    {
                        var fs = today > bs.Date ? today : bs.Date;
                        var fdays = ProjectCalendarService.CountWorkingHours(fs, be.Date) / hpd;
                        forecastInBucket = Math.Min(velPerDay * Math.Max(0, fdays), remainingHours - deliveredForecast);
                        if (forecastInBucket < 0) forecastInBucket = 0;
                        deliveredForecast += forecastInBucket;
                    }
                    progress = completedInBucket + forecastInBucket;
                }
                else
                {
                    planned  = plannedTasks.Where(t => GetTaskSprint(t) == sprint.Number).Sum(t => GetOriginalHours(t.Model));
                    progress = actualTasks.Where(t => GetTaskSprint(t) == sprint.Number).Sum(t => GetRealizedHours(t.Model));
                }

                // Linha azul: HH Original acumulado (baseline planejado).
                cumPlanned += planned / totalOriginalHours * 100.0;

                var isFuture  = sprint.Number > currentSprintNumber;
                var isCurrent = sprint.Number == currentSprintNumber;

                // Linha laranja: realizado acumulado.
                cumProgress += progress / totalDurationHours * 100.0;

                // Linha verde (base line carregado): HH Atual+Restante distribuído pelas datas.
                double baselinePct = -1;
                if (hasBaseline && hasDates)
                {
                    var bs = bucketStart[i]; var be = bucketEnd[i];
                    double bh = baselineStories!.Sum(s => DistributeHours(GetTotalHours(s), s.Start, s.Finish, bs, be));
                    cumBaseline += bh / baselineTotal * 100.0;
                    baselinePct = Math.Min(100, cumBaseline);
                }

                points.Add(new SprintPoint(
                    sprint.Number, sprint.Label,
                    Math.Min(100, cumPlanned),
                    Math.Min(100, cumProgress),
                    isFuture, isCurrent, baselinePct));
            }

            if (points.Count > 0 && points[0].PlannedPct > 0)
            {
                points.Insert(0, new SprintPoint(points[0].SprintNumber - 1, "", 0, 0, false, false,
                    hasBaseline ? 0 : -1));
                // Ponto âncora em zero: sem balde (mantém índices alinhados com os pontos).
                _curveBuckets.Insert(0, (DateTime.MinValue, DateTime.MinValue));
            }

            return points;
        }

        // Distribui 'hours' pela sobreposição da janela de trabalho [ws, we) com o balde
        // [bStart, bEnd). Janela de duração zero (marco) entra inteira no balde que contém ws —
        // nunca é repetida em todos os baldes.
        private static double DistributeHours(double hours, DateTime ws, DateTime we, DateTime bStart, DateTime bEnd)
        {
            if (hours <= 0) return 0;
            ws = ws.Date; we = we.Date;
            var wh = ProjectCalendarService.CountWorkingHours(ws, we);
            if (wh <= 0)
                return ws >= bStart.Date && ws < bEnd.Date ? hours : 0;

            var os = ws > bStart.Date ? ws : bStart.Date;
            var oe = we < bEnd.Date   ? we : bEnd.Date;
            if (oe <= os) return 0;

            return hours * (ProjectCalendarService.CountWorkingHours(os, oe) / wh);
        }


        private static int DetermineCurrentSprint(List<SprintInfo> sprints, DateTime today)
        {
            // Se sprints têm datas, use-as
            var withDates = sprints.Where(s => s.Start != DateTime.MinValue && s.End != DateTime.MaxValue).ToList();
            if (withDates.Count > 0)
            {
                var cur = withDates.FirstOrDefault(s => today >= s.Start && today <= s.End);
                if (cur != null) return cur.Number;
                // Se passou de todas, retorna a última
                if (today > withDates[^1].End) return withDates[^1].Number;
                return withDates[0].Number;
            }
            return sprints.Count > 0 ? sprints[sprints.Count / 2].Number : 0;
        }

        // ── Desenho ──────────────────────────────────────────────────────────

        private void DrawGridAndAxes(double pl, double pt, double pr, double pb,
                                      double pw, double ph, List<SprintPoint> points)
        {
            // Fundo da área
            CurveCanvas.Children.Add(new Rectangle
            {
                Width = pw, Height = ph,
                Fill = new SolidColorBrush(Color.FromRgb(250, 252, 255)),
                Stroke = new SolidColorBrush(Color.FromRgb(200, 210, 225)),
                StrokeThickness = 1
            });
            System.Windows.Controls.Canvas.SetLeft(CurveCanvas.Children[^1] as UIElement, pl);
            System.Windows.Controls.Canvas.SetTop(CurveCanvas.Children[^1] as UIElement, pt);

            // Linhas de grade horizontais a cada 20%
            for (int pct = 0; pct <= 100; pct += 20)
            {
                var y = pt + ph - ph * pct / 100.0;
                AddLine(pl, y, pr, y,
                    pct == 0 || pct == 100
                        ? new SolidColorBrush(Color.FromRgb(160, 180, 210))
                        : new SolidColorBrush(Color.FromRgb(220, 230, 240)),
                    pct is 0 or 100 ? 1.2 : 0.7);
                // Label eixo Y
                var lbl = MakeText($"{pct}%", 10, "#555");
                System.Windows.Controls.Canvas.SetLeft(lbl, 2);
                System.Windows.Controls.Canvas.SetTop(lbl, y - 7);
                lbl.TextAlignment = TextAlignment.Right;
                lbl.Width = pl - 6;
                CurveCanvas.Children.Add(lbl);
            }

            // Eixo Y título
            var yTitle = MakeText(AppStrings.Get("Delay_AxisProgress"), 10, "#555");
            yTitle.RenderTransform = new RotateTransform(-90);
            yTitle.RenderTransformOrigin = new Point(0.5, 0.5);
            System.Windows.Controls.Canvas.SetLeft(yTitle, 2);
            System.Windows.Controls.Canvas.SetTop(yTitle, pt + ph / 2 - 30);
            CurveCanvas.Children.Add(yTitle);
        }

        private void DrawCurrentSprintMarker(double pl, double pt, double pb, double pw,
            List<SprintPoint> points, int currentSprint)
        {
            if (points.Count == 0) return;
            var base0 = points[0].SprintNumber;
            var idx = points.FindIndex(p => p.SprintNumber == currentSprint);
            if (idx < 0) return;
            var x = pl + pw * idx / Math.Max(1, points.Count - 1);
            var line = new Line
            {
                X1 = x, Y1 = pt, X2 = x, Y2 = pb,
                Stroke = new SolidColorBrush(Color.FromRgb(120, 120, 200)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection([4, 3]),
                Opacity = 0.7
            };
            CurveCanvas.Children.Add(line);
            var lbl = MakeText(AppStrings.Get("Delay_Today"), 9, "#6060AA");
            System.Windows.Controls.Canvas.SetLeft(lbl, x + 3);
            System.Windows.Controls.Canvas.SetTop(lbl, pt + 2);
            CurveCanvas.Children.Add(lbl);
        }

        // Régua de sprints por cima dos pontos semanais: divisórias verticais no início de cada
        // sprint e o nome centralizado no topo. Sprints configuradas em azul, projetadas em cinza.
        private void DrawSprintBands(double pl, double pt, double pb, double pw,
            List<SprintPoint> points, List<SprintInfo> periods,
            List<(string Name, DateTime Start, DateTime End, bool Proj)> marks)
        {
            if (points.Count < 2 || periods.Count == 0 || marks.Count == 0) return;
            bool leadingZero = points.Count == periods.Count + 1;

            double XForDate(DateTime d)
            {
                int j = periods.FindIndex(p => d.Date >= p.Start.Date && d.Date <= p.End.Date);
                if (j < 0) j = d.Date < periods[0].Start.Date ? 0 : periods.Count - 1;
                int idx = j + (leadingZero ? 1 : 0);
                return pl + pw * idx / Math.Max(1, points.Count - 1);
            }

            foreach (var m in marks)
            {
                double xs = XForDate(m.Start);
                var col = m.Proj ? Color.FromRgb(150, 150, 160) : Color.FromRgb(90, 110, 175);
                CurveCanvas.Children.Add(new Line
                {
                    X1 = xs, Y1 = pt, X2 = xs, Y2 = pb,
                    Stroke = new SolidColorBrush(col) { Opacity = m.Proj ? 0.25 : 0.4 },
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection([2, 3])
                });

                double xe = XForDate(m.End);
                var name = new TextBlock
                {
                    Text = m.Name, FontSize = 8,
                    Foreground = new SolidColorBrush(m.Proj ? Color.FromRgb(150, 150, 160) : Color.FromRgb(70, 90, 150)),
                    FontStyle = m.Proj ? FontStyles.Italic : FontStyles.Normal,
                    TextAlignment = TextAlignment.Center, Width = Math.Max(20, xe - xs),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                System.Windows.Controls.Canvas.SetLeft(name, xs);
                System.Windows.Controls.Canvas.SetTop(name, pt - 12);
                CurveCanvas.Children.Add(name);
            }
        }

        private void DrawPolyline(List<Point> pts, string color, double thickness, bool dashed)
        {
            if (pts.Count < 2) return;
            var pl = new Polyline
            {
                Stroke = (Brush)new BrushConverter().ConvertFrom(color)!,
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round
            };
            if (dashed) pl.StrokeDashArray = new DoubleCollection([6, 3]);
            foreach (var p in pts) pl.Points.Add(p);
            CurveCanvas.Children.Add(pl);

            // Pontos (círculos)
            foreach (var p in pts)
            {
                var e = new Ellipse { Width = 7, Height = 7,
                    Fill = (Brush)new BrushConverter().ConvertFrom(color)!,
                    Stroke = Brushes.White, StrokeThickness = 1.5 };
                System.Windows.Controls.Canvas.SetLeft(e, p.X - 3.5);
                System.Windows.Controls.Canvas.SetTop(e, p.Y - 3.5);
                CurveCanvas.Children.Add(e);
            }
        }

        private static void SeparateOverlappingCurvePoints(
            List<Point> plannedLine,
            List<Point> durationLine,
            double minY,
            double maxY)
        {
            var count = Math.Min(plannedLine.Count, durationLine.Count);
            const double overlapTolerancePx = 2.0;
            const double visualOffsetPx = 3.0;

            for (int i = 0; i < count; i++)
            {
                if (Math.Abs(plannedLine[i].Y - durationLine[i].Y) > overlapTolerancePx)
                    continue;

                plannedLine[i] = new Point(
                    plannedLine[i].X,
                    Math.Max(minY, plannedLine[i].Y - visualOffsetPx));
                durationLine[i] = new Point(
                    durationLine[i].X,
                    Math.Min(maxY, durationLine[i].Y + visualOffsetPx));
            }
        }

        private void DrawSprintLabels(double pl, double pb, double pw, List<SprintPoint> points)
        {
            if (points.Count == 0) return;
            // Muitos pontos (semanal): mostra ~12 rótulos, mas sempre o de HOJE.
            int step = Math.Max(1, (int)Math.Ceiling(points.Count / 12.0));
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (string.IsNullOrWhiteSpace(p.Label)) continue;
                if (step > 1 && i % step != 0 && !p.IsCurrent) continue;
                var x = pl + pw * i / Math.Max(1, points.Count - 1);
                var lbl = MakeText(p.Label, 9, p.IsCurrent ? "#6060AA" : "#555");
                lbl.Width = 80;
                lbl.TextAlignment = TextAlignment.Center;
                System.Windows.Controls.Canvas.SetLeft(lbl, x - 40);
                System.Windows.Controls.Canvas.SetTop(lbl, pb + 6);
                // Rota rótulos se muitos sprints
                if (points.Count > 8)
                {
                    lbl.RenderTransform = new RotateTransform(-35, 40, 0);
                    System.Windows.Controls.Canvas.SetTop(lbl, pb + 12);
                }
                CurveCanvas.Children.Add(lbl);
            }
        }

        private void DrawNoDataMessage(string msg)
        {
            CurveSummary.Text = string.Empty;
            var tb = MakeText(msg, 13, "#888");
            tb.Width = CurveCanvas.ActualWidth;
            tb.TextAlignment = TextAlignment.Center;
            System.Windows.Controls.Canvas.SetTop(tb, CurveCanvas.ActualHeight / 2 - 10);
            CurveCanvas.Children.Add(tb);
        }

        private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thick)
        {
            CurveCanvas.Children.Add(new Line
                { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thick });
        }

        private static TextBlock MakeText(string text, double size, string hex) => new()
        {
            Text = text, FontSize = size,
            Foreground = (Brush)new BrushConverter().ConvertFrom(hex)!
        };

        private Point ToCanvasPoint(double sprintIdx, double pct, int total,
                                     double pl, double pt, double pw, double ph)
        {
            var x = pl + pw * sprintIdx / Math.Max(1, total - 1);
            var y = pt + ph - ph * pct / 100.0;
            return new Point(x, y);
        }

        // ── Eventos da Curva S ────────────────────────────────────────────────

        private void OnTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is TabControl tc && tc.SelectedIndex == 2 && CurveCanvas.ActualWidth > 0)
                RenderCurve();
        }

        private void OnIncludePlannedChanged(object sender, RoutedEventArgs e)
        {
            _includeZeroPct   = IncludePlannedBox.IsChecked == true;
            _includeRemaining = IncludeRemainingBox.IsChecked == true;
            _showBaseline     = ShowBaselineBox.IsChecked == true;
            UpdateBaselineChrome();
            if (CurveCanvas.ActualWidth > 0) RenderCurve();
        }

        // Mostra/oculta o botão "Abrir baseline" e a legenda da 3ª linha conforme o estado.
        private void UpdateBaselineChrome()
        {
            bool loaded = _baselineProject != null;
            OpenBaselineButton.Visibility = _showBaseline && !loaded ? Visibility.Visible : Visibility.Collapsed;
            BaselineLegend.Visibility     = _showBaseline && loaded ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnOpenBaselineClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = AppStrings.Get("Delay_OpenBaseline"),
                Filter = "NXProject (*.nxp)|*.nxp|Todos (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                _baselineProject = XmlProjectService.Load(dlg.FileName);
                UpdateBaselineChrome();
                if (CurveCanvas.ActualWidth > 0) RenderCurve();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "NXProject", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ── ABA 4: Em Bloqueio ───────────────────────────────────────────────

        private void BuildBlockedList()
        {
            var rows = _vm.FlatTasks
                .Where(t => t.IsBlocked)
                .OrderBy(t => t.Model.SprintNumber)
                .ThenBy(t => t.Model.Name)
                .Select(t => new BlockedTaskRow(t, _vm))
                .ToList();

            BlockedGrid.ItemsSource = rows;
            BlockedSummary.Text = rows.Count > 0
                ? AppStrings.Get("Delay_BlockedSummary", rows.Count)
                : AppStrings.Get("Delay_NoBlocked");
        }

        public sealed class BlockedTaskRow
        {
            private readonly TaskViewModel _vm;
            private readonly MainViewModel _mainVm;

            public BlockedTaskRow(TaskViewModel vm, MainViewModel mainVm)
            {
                _vm = vm; _mainVm = mainVm;
            }

            public string DisplayId   => _vm.DisplayId;
            public string TfsType     => _vm.Model.TfsType ?? "—";
            public string Name        => _vm.Model.Name;
            public string ResourceName =>
                _vm.Model.Resources.FirstOrDefault()?.Resource?.Name ?? AppStrings.Get("Delay_NoResource");
            public string PercentText => $"{_vm.Model.PercentComplete:0}%";
            public string StartText   => _vm.Model.Start.ToString("dd/MM/yy");
            public string FinishText  => ProjectCalendarService
                .GetInclusiveFinishDate(_vm.Model.Start, _vm.Model.Finish)
                .ToString("dd/MM/yy");
            public string SprintLabel =>
                _vm.SprintNumber > 0 ? $"Sprint {_vm.SprintNumber}" : "—";
            public string Tags        => _vm.Model.Tags ?? string.Empty;
        }

        private void OnCurveCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (CurveCanvas.ActualWidth > 100)
                RenderCurve();
        }

        private void OnCurveCanvasMouseMove(object sender, MouseEventArgs e)
        {
            if (_curvePoints == null || _curvePoints.Count == 0)
            {
                CurveTooltip.Visibility = Visibility.Collapsed;
                return;
            }

            var pos = e.GetPosition(CurveCanvas);
            var w = CurveCanvas.ActualWidth;
            var h = CurveCanvas.ActualHeight;
            var pl = _chartLeft; var pt = _chartTop;
            var pw = w - _chartRight - pl;

            // Encontra sprint mais próximo do X do mouse
            double minDist = double.MaxValue;
            SprintPoint? nearest = null;
            for (int i = 0; i < _curvePoints.Count; i++)
            {
                var cx = pl + pw * i / Math.Max(1, _curvePoints.Count - 1);
                var dist = Math.Abs(pos.X - cx);
                if (dist < minDist) { minDist = dist; nearest = _curvePoints[i]; }
            }

            if (nearest == null || minDist > 40)
            {
                CurveTooltip.Visibility = Visibility.Collapsed;
                return;
            }

            TooltipSprint.Text = nearest.Label;
            TooltipPlanned.Text = AppStrings.Get("Delay_TooltipPlanned", nearest.PlannedPct);
            TooltipActual.Text = AppStrings.Get("Delay_TooltipActual", nearest.ActualPct);
            if (nearest.BaselinePct >= 0)
            {
                TooltipBaseline.Text = AppStrings.Get("Delay_TooltipBaseline", nearest.BaselinePct);
                TooltipBaseline.Visibility = Visibility.Visible;
            }
            else TooltipBaseline.Visibility = Visibility.Collapsed;
            var gap = nearest.PlannedPct - nearest.ActualPct;
            TooltipGap.Text = gap > 0.1  ? AppStrings.Get("Delay_GapNeg", gap)
                            : gap < -0.1 ? AppStrings.Get("Delay_GapPos", -gap)
                            : AppStrings.Get("Delay_NoGap");

            CurveTooltip.Visibility = Visibility.Visible;
            var tx = pos.X + 14;
            var ty = pos.Y - 10;
            if (tx + 160 > w) tx = pos.X - 165;
            System.Windows.Controls.Canvas.SetLeft(CurveTooltip, tx);
            System.Windows.Controls.Canvas.SetTop(CurveTooltip, ty);
        }

        private void OnCurveCanvasMouseLeave(object sender, MouseEventArgs e) =>
            CurveTooltip.Visibility = Visibility.Collapsed;

        // Clique na curva: lista as atividades que compõem o PLANEJADO daquela semana,
        // na mesma grade usada pelo Mapa de Alocação ao clicar nas horas da Story.
        private void OnCurveCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_curvePoints == null || _curvePoints.Count == 0
                || _curveBuckets == null || _curvePlannedTasks == null) return;

            var pos = e.GetPosition(CurveCanvas);
            var pl  = _chartLeft;
            var pw  = CurveCanvas.ActualWidth - _chartRight - pl;

            int index = -1;
            double minDist = double.MaxValue;
            for (int i = 0; i < _curvePoints.Count; i++)
            {
                var cx = pl + pw * i / Math.Max(1, _curvePoints.Count - 1);
                var dist = Math.Abs(pos.X - cx);
                if (dist < minDist) { minDist = dist; index = i; }
            }
            if (index < 0 || minDist > 40 || index >= _curveBuckets.Count) return;

            var (bs, be) = _curveBuckets[index];
            if (bs == DateTime.MinValue) return;   // ponto âncora em zero

            ShowPlannedWeekPopup(_curvePoints[index], bs, be, index);
        }

        private void ShowPlannedWeekPopup(SprintPoint point, DateTime bucketStart, DateTime bucketEnd, int index)
        {
            var opts   = TfsConnectionStore.Load();
            var orgUrl = opts.OrganizationUrl?.TrimEnd('/') ?? "";
            var tp     = opts.TeamProject ?? "";

            var rows  = new List<StoryListRow>();
            double totalWeek = 0;

            // Semana de PROJEÇÃO (depois do fim do cronograma): mostra as atividades empurradas
            // para cá pela velocidade, com o HH que a velocidade entrega na semana.
            var forecast = _forecastByPoint != null && _forecastByPoint.TryGetValue(index, out var f)
                           && bucketStart.Date > _scheduleEnd.Date
                ? f : null;

            if (forecast != null)
            {
                foreach (var (task, hours) in forecast)
                {
                    totalWeek += hours;
                    rows.Add(BuildStoryRow(task, hours, orgUrl, tp));
                }

                var projPeriod = $"{bucketStart:dd/MM/yy} – {bucketEnd.AddDays(-1):dd/MM/yy}";
                StoryListPopup.Show(
                    this,
                    AppStrings.Get("Delay_WeekPopupTitle", point.Label),
                    AppStrings.Get("Delay_ForecastPopupHeader", point.Label, projPeriod),
                    AppStrings.Get("Delay_ColHHWeekForecast"),
                    rows,
                    AppStrings.Get("Delay_ForecastPopupTotal", totalWeek));
                return;
            }

            // Semana sem HH planejado (curva no platô): em vez de lista vazia, mostra as
            // atividades que ATRAVESSAM a semana, com "–" na coluna de HH da semana.
            bool anyHours = _curvePlannedTasks!.Any(t => DistributeHours(
                GetOriginalHours(t.Model), t.Model.Start, t.Model.Finish, bucketStart, bucketEnd) > 0.01);

            foreach (var t in _curvePlannedTasks!.OrderBy(t => t.Model.Start))
            {
                var task  = t.Model;
                var weekH = DistributeHours(GetOriginalHours(task), task.Start, task.Finish, bucketStart, bucketEnd);
                if (anyHours)
                {
                    if (weekH <= 0.01) continue;
                }
                else if (task.Finish.Date < bucketStart.Date || task.Start.Date >= bucketEnd.Date)
                {
                    continue;   // nem HH na semana, nem atividade atravessando ela
                }

                totalWeek += weekH;
                rows.Add(BuildStoryRow(task, weekH, orgUrl, tp));
            }

            var period = $"{bucketStart:dd/MM/yy} – {bucketEnd.AddDays(-1):dd/MM/yy}";
            var headerText = AppStrings.Get("Delay_WeekPopupHeader", point.Label, period, point.PlannedPct);
            if (!anyHours)
                headerText += "\n" + AppStrings.Get("Delay_WeekPopupNoPlanned");

            StoryListPopup.Show(
                this,
                AppStrings.Get("Delay_WeekPopupTitle", point.Label),
                headerText,
                AppStrings.Get("Delay_ColHHWeek"),
                rows,
                AppStrings.Get("Delay_WeekPopupTotal", totalWeek));
        }

        // Linha da grade do popup (mesma usada pelo Mapa de Alocação).
        private static StoryListRow BuildStoryRow(ProjectTask task, double periodHours,
                                                  string orgUrl, string teamProject)
        {
            var totalH = GetOriginalHours(task);
            string? url = task.TfsId.HasValue && !string.IsNullOrWhiteSpace(orgUrl)
                ? $"{orgUrl}/{Uri.EscapeDataString(teamProject)}/_workitems/edit/{task.TfsId.Value}"
                : null;

            var storyNode = TfsImportService.IsStoryTypePublic(task.TfsType) ? task : FindParentStoryModel(task);
            var tipo      = AppStrings.Get(storyNode == task ? "PMap_SrTypeStory" : "PMap_SrTypeTask");

            return new StoryListRow(
                task.Name,
                tipo,
                storyNode?.Name ?? task.Name,
                task.TaskAllocations.Count > 0 ? task.TaskAllocations.Count.ToString() : "1",
                totalH > 0.01 ? $"{totalH:0.#}h" : "–",
                periodHours > 0.01 ? $"{periodHours:0.#}h" : "–",
                $"{(int)Math.Round(task.PercentComplete)}%",
                task.Start.ToString("dd/MM/yy"),
                task.Finish.ToString("dd/MM/yy"),
                url,
                null,
                ResponsibleOf(task),
                HierarchyHint(task));
        }

        // Hint da hierarquia: EPIC e Feature acima da Story/Task, subindo pelos pais.
        private static string HierarchyHint(ProjectTask task)
        {
            string? epic = null, feature = null;
            for (var p = task.Parent; p != null; p = p.Parent)
            {
                var type = p.TfsType?.Trim();
                if (feature == null && string.Equals(type, "Feature", StringComparison.OrdinalIgnoreCase))
                    feature = p.Name;
                else if (epic == null && string.Equals(type, "Epic", StringComparison.OrdinalIgnoreCase))
                    epic = p.Name;
            }

            var lines = new List<string>();
            lines.Add(AppStrings.Get("Delay_HintEpic", epic ?? "–"));
            lines.Add(AppStrings.Get("Delay_HintFeature", feature ?? "–"));
            return string.Join("\n", lines);
        }

        private static ProjectTask? FindParentStoryModel(ProjectTask task)
        {
            var p = task.Parent;
            while (p != null)
            {
                if (TfsImportService.IsStoryTypePublic(p.TfsType)) return p;
                p = p.Parent;
            }
            return null;
        }

        // ── Clique no ID (abre editor de justificativa) ──────────────────────

        private void OnIdClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: DelayedTaskRow row }) return;

            var dlg = new Window
            {
                Title = AppStrings.Get("Delay_JustifyTitle", row.DisplayId, row.Name),
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                Width = 520, Height = 230, Background = Brushes.White
            };

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock { Text = AppStrings.Get("Delay_JustifyLabel"),
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
            Grid.SetRow(lbl, 0); root.Children.Add(lbl);

            var tb = new TextBox
            {
                Text = row.Justificativa ?? string.Empty,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(tb, 1); root.Children.Add(tb);

            var btns = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right };
            var ok     = new Button { Content = "OK",       Width = 80, IsDefault = true, Margin = new Thickness(0,0,8,0) };
            var cancel = new Button { Content = AppStrings.Get("Delay_Cancel"), Width = 80, IsCancel = true };
            ok.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            Grid.SetRow(btns, 2); root.Children.Add(btns);

            dlg.Content = root;
            tb.Focus(); tb.SelectAll();
            if (dlg.ShowDialog() == true)
            {
                row.Justificativa = string.IsNullOrWhiteSpace(tb.Text) ? null : tb.Text.Trim();
                _vm.Project.IsDirty = true;
            }
        }

        // ── Linha de detalhe ─────────────────────────────────────────────────

        public sealed class DelayedTaskRow : INotifyPropertyChanged
        {
            private readonly TaskViewModel _vm;
            private readonly MainViewModel _mainVm;

            public DelayedTaskRow(TaskViewModel vm, MainViewModel mainVm)
            {
                _vm = vm; _mainVm = mainVm;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string DisplayId => _vm.DisplayId;
            public string Name      => _vm.Model.Name;
            public string ResourceName =>
                _vm.Model.Resources.FirstOrDefault()?.Resource?.Name ?? AppStrings.Get("Delay_NoResource");
            public double RemainingHours =>
                Math.Max(0, TaskScheduleService.GetEffectiveDurationHours(_vm.Model)
                            * (1.0 - _vm.Model.PercentComplete / 100.0));
            public string RemainingHoursText => $"{RemainingHours:0.##} h";
            public string StartText  => _vm.Model.Start.ToString("dd/MM/yy");
            public string FinishText => ProjectCalendarService
                .GetInclusiveFinishDate(_vm.Model.Start, _vm.Model.Finish)
                .ToString("dd/MM/yy");
            public string Predecessors => _vm.PredecessorsText;
            public string PercentText  => $"{_vm.Model.PercentComplete:0}%";
            public double DelayDays    => ComputeDelayDays(_vm.Model);
            public string DelayText
            {
                get
                {
                    var d = DelayDays;
                    if (d < 0.5) return "—";
                    if (d < 1.5) return AppStrings.Get("Delay_OneDay");
                    if (d < 7)   return AppStrings.Get("Delay_Days", d);
                    var weeks = (int)(d / 5);
                    return weeks == 1 ? AppStrings.Get("Delay_OneWeek") : AppStrings.Get("Delay_Weeks", weeks);
                }
            }
            public string SprintLabel => GetSprintLabel();

            private string GetSprintLabel()
            {
                if (!string.IsNullOrWhiteSpace(_vm.Model.TfsIterationPath))
                {
                    var match = _mainVm.Project.Sprints.FirstOrDefault(s =>
                        string.Equals(s.Path, _vm.Model.TfsIterationPath, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        return string.IsNullOrWhiteSpace(match.Name) ? $"Sprint {match.Number}" : match.Name;
                    var parts = _vm.Model.TfsIterationPath.Split('\\', '/');
                    return parts[^1];
                }
                return _vm.SprintNumber > 0 ? $"Sprint {_vm.SprintNumber}" : "—";
            }

            public string? Justificativa
            {
                get => _vm.Justificativa;
                set { _vm.Justificativa = value; Notify(); }
            }

            private void Notify([CallerMemberName] string? p = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }

    }
}
