// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Views
{
    public partial class StoryStatusChartWindow : Window
    {
        private readonly MainViewModel _vm;

        private sealed record StatusBucket(string Label, int Count, Color BarColor);

        private Border _tooltip = null!;
        private TextBlock _tooltipText = null!;
        private List<StatusBucket> _buckets = [];

        private const double ChartLeft   = 60;
        private const double ChartTop    = 20;
        private const double ChartRight  = 24;
        private const double ChartBottom = 50;

        // Cores padrão para estados TFS conhecidos (usadas quando não há mapeamento configurado)
        private static readonly Dictionary<string, (int Order, Color Color)> KnownStates =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["new"]                    = (0,  Color.FromRgb(158, 158, 158)),
                ["proposed"]               = (1,  Color.FromRgb(158, 158, 158)),
                ["to do"]                  = (2,  Color.FromRgb(158, 158, 158)),
                ["active"]                 = (10, Color.FromRgb(74,  144, 217)),
                ["in progress"]            = (11, Color.FromRgb(74,  144, 217)),
                ["em análise"]             = (12, Color.FromRgb(41,  182, 246)),
                ["corrigindo"]             = (13, Color.FromRgb(255, 167, 38 )),
                ["corrigindo causa raiz"]  = (14, Color.FromRgb(255, 112, 67 )),
                ["validando"]              = (15, Color.FromRgb(171, 71,  188)),
                ["resolved"]               = (20, Color.FromRgb(102, 187, 106)),
                ["closed"]                 = (30, Color.FromRgb(46,  125, 50 )),
                ["done"]                   = (31, Color.FromRgb(46,  125, 50 )),
                ["removed"]                = (40, Color.FromRgb(211, 47,  47 )),
            };

        private static readonly Color[] FallbackPalette =
        [
            Color.FromRgb(255, 112, 67),
            Color.FromRgb(38,  166, 154),
            Color.FromRgb(121, 85,  72),
            Color.FromRgb(66,  165, 245),
            Color.FromRgb(236, 64,  122),
            Color.FromRgb(156, 204, 101),
        ];

        public StoryStatusChartWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;

            _tooltip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Visibility = Visibility.Collapsed
            };
            _tooltipText = new TextBlock { FontSize = 12 };
            _tooltip.Child = _tooltipText;
            ChartCanvas.Children.Add(_tooltip);

            Refresh();
        }

        private void Refresh()
        {
            var opts = TfsConnectionStore.Load();
            _buckets = ComputeBuckets(opts.StoryStatusMappings);
            UpdateSubtitle(opts.StoryStatusMappings);
            UpdateSummary();
            Render();
        }

        private void UpdateSubtitle(List<StoryStatusMapping> mappings)
        {
            var projectName = string.IsNullOrWhiteSpace(_vm.Project.Name) ? null : _vm.Project.Name.Trim();
            var projectPart = projectName != null ? AppStrings.Get("SChart_SubtitleProject", projectName) : "";
            SubtitleText.Text = projectPart + (mappings.Count > 0
                ? AppStrings.Get("SChart_SubtitleMapped", mappings.Count)
                : AppStrings.Get("SChart_SubtitleUnmapped"));
        }

        private List<StatusBucket> ComputeBuckets(List<StoryStatusMapping> mappings)
        {
            var leaves = _vm.FlatTasks
                .Where(t => t.Model.Children.Count == 0 && !t.Model.IsMilestone)
                .ToList();

            // Resolve label para cada tarefa
            // label→(count, color, order)
            var groups = new Dictionary<string, (int Count, Color Color, int Order)>(StringComparer.OrdinalIgnoreCase);

            int fallbackIdx = 0;

            foreach (var task in leaves)
            {
                string rawState = string.IsNullOrWhiteSpace(task.Model.TfsState)
                    ? ResolveFromPercent(task.Model.PercentComplete)
                    : task.Model.TfsState.Trim();

                // Tenta encontrar mapeamento configurado
                StoryStatusMapping? mapped = mappings.Count > 0
                    ? mappings.FirstOrDefault(m => string.Equals(m.TfsState, rawState, StringComparison.OrdinalIgnoreCase))
                    : null;

                string label = mapped?.ChartLabel is { Length: > 0 } cl ? cl : rawState;

                if (!groups.TryGetValue(label, out var existing))
                {
                    var (color, order) = ResolveColorAndOrder(rawState, mapped, mappings, ref fallbackIdx);
                    groups[label] = (1, color, order);
                }
                else
                {
                    groups[label] = (existing.Count + 1, existing.Color, existing.Order);
                }
            }

            return groups
                .Select(kv => new StatusBucket(kv.Key, kv.Value.Count, kv.Value.Color))
                .OrderBy(b =>
                {
                    // Mantém a ordem do grupo
                    return groups[b.Label].Order;
                })
                .ThenBy(b => b.Label)
                .ToList();
        }

        private static string ResolveFromPercent(double pct) =>
            pct >= 100 ? "Closed" : pct > 0 ? "Active" : "New";

        private static (Color Color, int Order) ResolveColorAndOrder(
            string rawState,
            StoryStatusMapping? mapped,
            List<StoryStatusMapping> allMappings,
            ref int fallbackIdx)
        {
            // Se há mapeamento configurado com cor
            if (mapped != null && !string.IsNullOrWhiteSpace(mapped.ColorHex))
            {
                var c = ParseHex(mapped.ColorHex);
                if (c.HasValue)
                    return (c.Value, mapped.Order);
            }

            // Usa a ordem do mapeamento mesmo sem cor
            int configOrder = mapped?.Order ?? int.MaxValue;

            // Tenta cor da tabela built-in
            if (KnownStates.TryGetValue(rawState, out var known))
                return (known.Color, configOrder == int.MaxValue ? known.Order : configOrder);

            // Fallback de cor
            var fallback = FallbackPalette[fallbackIdx++ % FallbackPalette.Length];
            return (fallback, configOrder == int.MaxValue ? 100 + fallbackIdx : configOrder);
        }

        private static Color? ParseHex(string hex)
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6 &&
                byte.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
                return Color.FromRgb(r, g, b);
            return null;
        }

        private void UpdateSummary()
        {
            int total = _buckets.Sum(b => b.Count);
            SummaryText.Text = total > 0
                ? AppStrings.Get("SChart_SummaryTotal", total) +
                  string.Join("  ·  ", _buckets.Select(b => AppStrings.Get("SChart_BarLabel", b.Label, b.Count)))
                : AppStrings.Get("SChart_NoStories");
        }

        private void OnConfigureClick(object sender, RoutedEventArgs e)
        {
            var opts = TfsConnectionStore.Load();
            var win  = new StoryStatusMappingWindow(opts) { Owner = this };
            if (win.ShowDialog() == true)
                Refresh();
        }

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => Render();

        private void Render()
        {
            ChartCanvas.Children.Clear();
            ChartCanvas.Children.Add(_tooltip);

            var w = ChartCanvas.ActualWidth;
            var h = ChartCanvas.ActualHeight;
            if (w < 100 || h < 80) return;

            var pl = ChartLeft;
            var pt = ChartTop;
            var pr = w - ChartRight;
            var pb = h - ChartBottom;
            var pw = pr - pl;
            var ph = pb - pt;

            int maxCount = _buckets.Count > 0 ? _buckets.Max(b => b.Count) : 1;
            if (maxCount == 0) maxCount = 1;

            // background
            var bg = new Rectangle
            {
                Width = pw, Height = ph,
                Fill = new SolidColorBrush(Color.FromRgb(250, 252, 255)),
                Stroke = new SolidColorBrush(Color.FromRgb(210, 218, 230)),
                StrokeThickness = 1,
                RadiusX = 2, RadiusY = 2
            };
            ChartCanvas.Children.Add(bg);
            Canvas.SetLeft(bg, pl);
            Canvas.SetTop(bg, pt);

            // y-axis grid lines & labels
            int gridLines = 5;
            for (int i = 0; i <= gridLines; i++)
            {
                double pct = (double)i / gridLines;
                double yVal = Math.Round(pct * maxCount);
                double cy = pb - pct * ph;

                var line = new Line
                {
                    X1 = pl, Y1 = cy, X2 = pr, Y2 = cy,
                    Stroke = new SolidColorBrush(Color.FromRgb(220, 225, 235)),
                    StrokeThickness = i == 0 ? 1.5 : 1,
                    StrokeDashArray = i > 0 ? new DoubleCollection([4, 3]) : null
                };
                ChartCanvas.Children.Add(line);

                var lbl = new TextBlock
                {
                    Text = ((int)yVal).ToString(),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120))
                };
                ChartCanvas.Children.Add(lbl);
                lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(lbl, pl - lbl.DesiredSize.Width - 4);
                Canvas.SetTop(lbl, cy - lbl.DesiredSize.Height / 2);
            }

            // bars
            int n = _buckets.Count;
            if (n == 0) return;

            double barAreaWidth = pw / n;
            double barWidthRatio = Math.Min(0.65, Math.Max(0.35, 3.0 / n));

            for (int i = 0; i < n; i++)
            {
                var b = _buckets[i];
                double barH = b.Count == 0 ? 0 : (b.Count / (double)maxCount) * ph;
                double cx = pl + i * barAreaWidth + barAreaWidth / 2;
                double barW = barAreaWidth * barWidthRatio;
                double bx = cx - barW / 2;
                double by = pb - barH;

                if (b.Count > 0)
                {
                    var rect = new Rectangle
                    {
                        Width = barW,
                        Height = barH,
                        Fill = new SolidColorBrush(b.BarColor),
                        RadiusX = 3, RadiusY = 3,
                        Cursor = Cursors.Hand,
                        Tag = b
                    };
                    Canvas.SetLeft(rect, bx);
                    Canvas.SetTop(rect, by);
                    rect.MouseEnter += OnBarMouseEnter;
                    rect.MouseLeave += OnBarMouseLeave;
                    rect.MouseMove  += OnBarMouseMove;
                    ChartCanvas.Children.Add(rect);
                }

                // count label above bar
                if (b.Count > 0)
                {
                    var countLbl = new TextBlock
                    {
                        Text = b.Count.ToString(),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(50, 50, 50))
                    };
                    ChartCanvas.Children.Add(countLbl);
                    countLbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(countLbl, cx - countLbl.DesiredSize.Width / 2);
                    Canvas.SetTop(countLbl, by - countLbl.DesiredSize.Height - 3);
                }

                // x-axis label (wraps for long names)
                var xLbl = new TextBlock
                {
                    Text = b.Label,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Width = barAreaWidth - 4
                };
                ChartCanvas.Children.Add(xLbl);
                Canvas.SetLeft(xLbl, pl + i * barAreaWidth + 2);
                Canvas.SetTop(xLbl, pb + 6);

                // color dot
                var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(b.BarColor) };
                ChartCanvas.Children.Add(dot);
                Canvas.SetLeft(dot, cx - 4);
                Canvas.SetTop(dot, pb + 26);
            }

            // axis lines
            ChartCanvas.Children.Add(new Line
            {
                X1 = pl, Y1 = pt, X2 = pl, Y2 = pb,
                Stroke = new SolidColorBrush(Color.FromRgb(160, 170, 185)),
                StrokeThickness = 1.5
            });
            ChartCanvas.Children.Add(new Line
            {
                X1 = pl, Y1 = pb, X2 = pr, Y2 = pb,
                Stroke = new SolidColorBrush(Color.FromRgb(160, 170, 185)),
                StrokeThickness = 1.5
            });

            Panel.SetZIndex(_tooltip, 100);
        }

        private void OnBarMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is not Rectangle rect || rect.Tag is not StatusBucket b) return;
            int total = _buckets.Sum(x => x.Count);
            double pct = total > 0 ? b.Count * 100.0 / total : 0;
            _tooltipText.Text = AppStrings.Get("SChart_Tooltip", b.Label, b.Count, pct);
            _tooltip.Visibility = Visibility.Visible;
            MoveTooltip(e.GetPosition(ChartCanvas));
        }

        private void OnBarMouseLeave(object sender, MouseEventArgs e) =>
            _tooltip.Visibility = Visibility.Collapsed;

        private void OnBarMouseMove(object sender, MouseEventArgs e) =>
            MoveTooltip(e.GetPosition(ChartCanvas));

        private void MoveTooltip(Point p)
        {
            _tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tx = p.X + 12;
            double ty = p.Y - _tooltip.DesiredSize.Height - 6;
            if (tx + _tooltip.DesiredSize.Width > ChartCanvas.ActualWidth)
                tx = p.X - _tooltip.DesiredSize.Width - 4;
            if (ty < 0) ty = p.Y + 10;
            Canvas.SetLeft(_tooltip, tx);
            Canvas.SetTop(_tooltip, ty);
        }
    }
}
