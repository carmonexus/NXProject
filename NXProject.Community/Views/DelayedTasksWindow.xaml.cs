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

        // Dados pré-calculados para tooltip da curva
        private List<SprintPoint>? _curvePoints;
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
            (DelayBucket.OneDay,    "Atraso 1d"),
            (DelayBucket.TwoDays,   "Atraso 2d"),
            (DelayBucket.ThreeDays, "Atraso 3d"),
            (DelayBucket.OneWeek,   "Atraso 1 sem"),
            (DelayBucket.OneSprint, "≥ 1 Sprint")
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
                SummaryText.Text = "Nenhuma atividade atrasada.";
                return;
            }
            SummaryText.Text = $"{delayed.Count} atividade(s) atrasada(s) em {resources.Count} recurso(s)";

            DelayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            foreach (var _ in BucketDefs)
                DelayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

            DelayGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            AddHeaderCell("Recurso", 0, 0);
            for (int c = 0; c < BucketDefs.Length; c++)
                AddHeaderCell(BucketDefs[c].Header, 0, c + 1);

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
            AddHeaderCell("Total", totalRow, 0, HorizontalAlignment.Left);
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
                            Resource: t.Model.Resources.FirstOrDefault()?.Resource?.Name ?? "Sem recurso");
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
                    ? $"{count} atividade(s) — {BucketDefs.First(b => b.Bucket == bucket).Header}"
                    : "Sem atrasos nesta faixa"
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
            DetailsTitle.Text = $"{resourceName}  —  {BucketDefs.First(b => b.Bucket == bucket).Header}";

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
                ? $"{rows.Count} atividade(s) atrasada(s)  |  " +
                  $"Total faltante: {rows.Sum(r => r.RemainingHours):0.#} h"
                : "Nenhuma atividade atrasada.";
        }

        // ── ABA 3: Curva S ───────────────────────────────────────────────────

        private sealed record SprintPoint(
            int SprintNumber,
            string Label,
            double PlannedPct,   // % acumulado de HH Original
            double ActualPct,    // % acumulado de HH Duração (HH Atual + HH Restante)
            bool IsFuture,
            bool IsCurrent);

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
                DrawNoDataMessage("Sem sprints configuradas para gerar a Curva S.");
                return;
            }

            // Considera somente o nível "story": o menor Depth entre todas as tasks folha.
            // Quando stories têm sub-tasks importadas do TFS, sub-tasks ficam em Depth maior
            // e são excluídas — a story (Depth menor) é a unidade de progresso.
            var allLeaves = _vm.FlatTasks.Where(t => t.Model.Children.Count == 0).ToList();
            var storyDepth = allLeaves.Count > 0 ? allLeaves.Min(t => t.Depth) : 0;
            var leafTasks = allLeaves.Where(t => t.Depth == storyDepth).ToList();

            // Usa apenas tasks que têm sprint atribuída para o denominador e cálculo da curva,
            // evitando que tasks sem sprint inflacionem os percentuais.
            var sprintNumbers = new HashSet<int>(sprints.Select(s => s.Number));
            var tasksWithSprint = leafTasks.Where(t => sprintNumbers.Contains(GetTaskSprint(t))).ToList();

            var totalOriginalHours = tasksWithSprint.Sum(t => GetOriginalHours(t.Model));
            if (totalOriginalHours < 0.01)
            {
                DrawNoDataMessage("Sem horas estimadas para gerar a Curva S.");
                return;
            }

            var today = DateTime.Today;
            int currentSprintNumber = DetermineCurrentSprint(sprints, today);

            var points = BuildCurvePoints(sprints, tasksWithSprint, totalOriginalHours, currentSprintNumber);
            _curvePoints = points;

            var pl = _chartLeft; var pt = _chartTop;
            var pr = w - _chartRight; var pb = h - _chartBottom;
            var pw = pr - pl; var ph = pb - pt;

            DrawGridAndAxes(pl, pt, pr, pb, pw, ph, points);
            DrawCurrentSprintMarker(pl, pt, pb, pw, points, currentSprintNumber);

            var plannedLine = points.Select(p => ToCanvasPoint(p.SprintNumber - points[0].SprintNumber,
                                                                p.PlannedPct, points.Count, pl, pt, pw, ph)).ToList();
            var durationLine = points.Select(p => ToCanvasPoint(p.SprintNumber - points[0].SprintNumber,
                                                                 p.ActualPct, points.Count, pl, pt, pw, ph)).ToList();
            SeparateOverlappingCurvePoints(plannedLine, durationLine, pt, pb);

            // Linha azul sólida: HH Original acumulado (baseline planejado)
            DrawPolyline(plannedLine,
                         "#1F4EA1", 2.5, false);

            // Linha laranja sólida: HH Duração acumulado (HH Atual + HH Restante)
            DrawPolyline(durationLine,
                         "#E65100", 2.5, false);

            DrawSprintLabels(pl, pb, pw, points);

            var currentPoint = points.LastOrDefault(p => p.IsCurrent)
                ?? points.LastOrDefault(p => !p.IsFuture)
                ?? points.LastOrDefault();
            var gap = currentPoint != null ? currentPoint.PlannedPct - currentPoint.ActualPct : 0;
            CurveSummary.Text = currentPoint != null
                ? $"HH Duração: {currentPoint.ActualPct:0.#}%  |  HH Original: {currentPoint.PlannedPct:0.#}%  |  " +
                  $"Gap: {gap:0.#}%"
                : string.Empty;
        }

        // HH Original da tarefa; usa EstimatedHours como fallback.
        private static double GetOriginalHours(ProjectTask task) =>
            task.OriginalEstimatedHours is > 0
                ? task.OriginalEstimatedHours.Value
                : TaskScheduleService.GetEffectiveDurationHours(task);

        private static double GetEstimatedHours(ProjectTask task) =>
            task.EstimatedHours is > 0
                ? task.EstimatedHours.Value
                : TaskScheduleService.GetEffectiveDurationHours(task);

        // Duração total = HH Atual + HH Restante quando HH Atual disponível; senão EstimatedHours ou duração calculada.
        private static double GetTotalHours(ProjectTask task) =>
            task.CurrentHours is > 0
                ? task.CurrentHours.Value + (task.EstimatedHours ?? 0)
                : task.EstimatedHours is > 0
                    ? task.EstimatedHours.Value
                    : TaskScheduleService.GetEffectiveDurationHours(task);

        private List<SprintPoint> BuildCurvePoints(
            List<SprintInfo> sprints, List<TaskViewModel> leafTasks,
            double totalOriginalHours, int currentSprintNumber)
        {
            // Denominador da linha pontilhada = soma de (HH Atual + HH Restante) de todas as tasks.
            // Deve bater com a duração total; se for zero, cai no totalOriginalHours.
            var totalDurationHours = leafTasks.Sum(t => GetTotalHours(t.Model));
            if (totalDurationHours < 0.01) totalDurationHours = totalOriginalHours;

            var hasDates = sprints.Any(s => s.Start != DateTime.MinValue);

            var points = new List<SprintPoint>();
            double cumPlanned  = 0;
            double cumProgress = 0;

            foreach (var sprint in sprints)
            {
                double planned;
                double progress;

                if (hasDates)
                {
                    // Distribui horas proporcionalmente conforme sobreposição de datas.
                    planned  = leafTasks.Sum(t => HoursInSprint(t.Model, GetOriginalHours(t.Model), sprint));
                    progress = leafTasks.Sum(t => HoursInSprint(t.Model, GetTotalHours(t.Model),    sprint));
                }
                else
                {
                    var inSprint = leafTasks.Where(t => GetTaskSprint(t) == sprint.Number).ToList();
                    planned  = inSprint.Sum(t => GetOriginalHours(t.Model));
                    progress = inSprint.Sum(t => GetTotalHours(t.Model));
                }

                // Linha azul: HH Original acumulado (baseline planejado).
                cumPlanned += planned / totalOriginalHours * 100.0;

                var isFuture  = sprint.Number > currentSprintNumber;
                var isCurrent = sprint.Number == currentSprintNumber;

                // Linha laranja: HH Duração acumulado por sprint.
                cumProgress += progress / totalDurationHours * 100.0;

                points.Add(new SprintPoint(
                    sprint.Number, sprint.Label,
                    Math.Min(100, cumPlanned),
                    Math.Min(100, cumProgress),
                    isFuture, isCurrent));
            }

            if (points.Count > 0 && points[0].PlannedPct > 0)
                points.Insert(0, new SprintPoint(points[0].SprintNumber - 1, "", 0, 0, false, false));

            return points;
        }

        private static double HoursInSprint(ProjectTask task, double totalHours, SprintInfo sprint)
        {
            if (totalHours <= 0 || sprint.Start == DateTime.MinValue) return totalHours;
            var taskStart    = task.Start.Date;
            var taskFinishEx = task.Finish.Date.AddDays(1); // fim exclusivo
            var sprintEndEx  = sprint.End.Date.AddDays(1);  // fim exclusivo

            var taskWorkingHours = ProjectCalendarService.CountWorkingHours(taskStart, taskFinishEx);
            if (taskWorkingHours <= 0) return totalHours;

            var overlapStart = taskStart    > sprint.Start ? taskStart    : sprint.Start;
            var overlapEnd   = taskFinishEx < sprintEndEx  ? taskFinishEx : sprintEndEx;
            if (overlapEnd <= overlapStart) return 0;

            var overlapHours = ProjectCalendarService.CountWorkingHours(overlapStart, overlapEnd);
            return totalHours * (overlapHours / taskWorkingHours);
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
            var yTitle = MakeText("% Progresso", 10, "#555");
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
            var lbl = MakeText("Hoje", 9, "#6060AA");
            System.Windows.Controls.Canvas.SetLeft(lbl, x + 3);
            System.Windows.Controls.Canvas.SetTop(lbl, pt + 2);
            CurveCanvas.Children.Add(lbl);
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
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (string.IsNullOrWhiteSpace(p.Label)) continue;
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
                ? $"{rows.Count} item(ns) em bloqueio"
                : "Nenhum item em bloqueio.";
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
                _vm.Model.Resources.FirstOrDefault()?.Resource?.Name ?? "Sem recurso";
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
            TooltipPlanned.Text = $"HH Original: {nearest.PlannedPct:0.#}%";
            TooltipActual.Text = $"HH Duração: {nearest.ActualPct:0.#}%";
            var gap = nearest.PlannedPct - nearest.ActualPct;
            TooltipGap.Text = gap > 0.1  ? $"Gap: -{gap:0.#}%"
                            : gap < -0.1 ? $"Gap: +{-gap:0.#}%"
                            : "Sem gap";

            CurveTooltip.Visibility = Visibility.Visible;
            var tx = pos.X + 14;
            var ty = pos.Y - 10;
            if (tx + 160 > w) tx = pos.X - 165;
            System.Windows.Controls.Canvas.SetLeft(CurveTooltip, tx);
            System.Windows.Controls.Canvas.SetTop(CurveTooltip, ty);
        }

        private void OnCurveCanvasMouseLeave(object sender, MouseEventArgs e) =>
            CurveTooltip.Visibility = Visibility.Collapsed;

        // ── Clique no ID (abre editor de justificativa) ──────────────────────

        private void OnIdClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: DelayedTaskRow row }) return;

            var dlg = new Window
            {
                Title = $"#{row.DisplayId} — {row.Name}",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                Width = 520, Height = 230, Background = Brushes.White
            };

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock { Text = "Justificativa do atraso:",
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
            var cancel = new Button { Content = "Cancelar", Width = 80, IsCancel = true };
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
                _vm.Model.Resources.FirstOrDefault()?.Resource?.Name ?? "Sem recurso";
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
                    if (d < 1.5) return "1 dia";
                    if (d < 7)   return $"{d:0} dias";
                    var weeks = (int)(d / 5);
                    return weeks == 1 ? "1 sem" : $"{weeks} sem";
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
