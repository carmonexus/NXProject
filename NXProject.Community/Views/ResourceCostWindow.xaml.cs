using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class ResourceCostWindow : Window
    {
        // ── Layout constants ─────────────────────────────────────────────────
        private const double RowH    = 24;
        private const double ResW    = 150;
        private const double EpicW   = 180;
        private const double FeatW   = 220;
        private const double TypeW   = 40;
        private const double MonCapW = 72;
        private const double MonOpxW = 72;
        private const double TotW    = 90;
        private const double CapTotW = 80;
        private const double OpxTotW = 80;

        // ── Data ─────────────────────────────────────────────────────────────
        private sealed class FeatureRow
        {
            public string ResourceName { get; init; } = "";
            public string EpicName     { get; init; } = "";
            public string FeatureName  { get; init; } = "";
            public bool   IsCapex      { get; init; }
            public string TypeLabel    { get; init; } = "";
            public decimal[] CapexByMonth { get; init; } = [];
            public decimal[] OpexByMonth  { get; init; } = [];
        }

        private List<FeatureRow>        _rows      = [];
        private List<DateTime>          _months    = [];
        private List<ResourceCostLine>  _allLines  = [];
        private IEnumerable<ProjectTask> _allTasks = [];
        private List<Resource>           _resources = [];

        public ResourceCostWindow(IEnumerable<ProjectTask> allTasks, IEnumerable<Resource> resources)
        {
            InitializeComponent();
            _allTasks  = allTasks;
            _resources = resources.ToList();
            _allLines  = ResourceCostService.Compute(_allTasks, _resources);
            _months = _allLines
                .Select(l => new DateTime(l.Year, l.Month, 1))
                .Distinct().OrderBy(d => d).ToList();
            _rows = BuildFeatureRows(_allLines);

            var resItems = _rows.Select(r => r.ResourceName).Distinct().OrderBy(s => s).ToList();
            ResourceFilterList.ItemsSource = resItems;
            ResourceFilterList.SelectAll();
            ResourceFilterList.SelectionChanged += OnFilterChanged;

            var featItems = _rows.Select(r => r.FeatureName).Where(f => !string.IsNullOrEmpty(f)).Distinct().OrderBy(s => s).ToList();
            FeatureFilterList.ItemsSource = featItems;
            FeatureFilterList.SelectAll();
            FeatureFilterList.SelectionChanged += OnFilterChanged;

            BuildGrid();
        }

        // ── Build feature rows (aggregate leaf lines up to feature+resource) ─
        private List<FeatureRow> BuildFeatureRows(List<ResourceCostLine> lines)
        {
            return lines
                .GroupBy(l => (l.ResourceName, l.EpicName, l.FeatureName, l.IsCapex))
                .Select(g =>
                {
                    var capex = new decimal[_months.Count];
                    var opex  = new decimal[_months.Count];
                    foreach (var l in g)
                    {
                        int mi = _months.FindIndex(m => m.Year == l.Year && m.Month == l.Month);
                        if (mi < 0) continue;
                        if (l.IsCapex) capex[mi] += l.Cost;
                        else           opex[mi]  += l.Cost;
                    }
                    string typeLabel = g.Key.IsCapex ? "CAP" : "OPX";
                    return new FeatureRow
                    {
                        ResourceName = g.Key.ResourceName,
                        EpicName     = g.Key.EpicName,
                        FeatureName  = g.Key.FeatureName,
                        IsCapex      = g.Key.IsCapex,
                        TypeLabel    = typeLabel,
                        CapexByMonth = capex,
                        OpexByMonth  = opex
                    };
                })
                .OrderBy(r => r.ResourceName)
                .ThenBy(r => r.EpicName)
                .ThenBy(r => r.FeatureName)
                .ToList();
        }

        // ── Filters ──────────────────────────────────────────────────────────
        private List<FeatureRow> ApplyFilters()
        {
            var selRes  = ResourceFilterList.SelectedItems.Cast<string>().ToHashSet();
            var selFeat = FeatureFilterList.SelectedItems.Cast<string>().ToHashSet();
            bool onlyCost = OnlyWithCostBox.IsChecked == true;

            return _rows.Where(r =>
                (selRes.Count  == 0 || selRes.Contains(r.ResourceName)) &&
                (selFeat.Count == 0 || selFeat.Contains(r.FeatureName)) &&
                (!onlyCost || r.CapexByMonth.Any(v => v > 0) || r.OpexByMonth.Any(v => v > 0)))
                .ToList();
        }

        // ── Build visual grid ─────────────────────────────────────────────────
        private void BuildGrid()
        {
            HeaderPanel.Items.Clear();
            LeftPanel.Items.Clear();
            DataPanel.Items.Clear();

            var filtered = ApplyFilters();
            if (filtered.Count == 0)
            {
                UpdateSummary(filtered);
                return;
            }

            // Quais meses têm dados
            var visMi = Enumerable.Range(0, _months.Count)
                .Where(mi => filtered.Any(r => r.CapexByMonth[mi] > 0 || r.OpexByMonth[mi] > 0))
                .ToList();

            // ── Cabeçalho meses ──
            var capexMonBg = Color.FromRgb(140, 70, 20);
            var opexMonBg  = Color.FromRgb(43, 100, 43);
            foreach (var mi in visMi)
            {
                string lbl = _months[mi].ToString("MMM/yy");
                var monthGrid = new Grid { Width = MonCapW + MonOpxW, Height = 44 };
                monthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
                monthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
                monthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MonCapW) });
                monthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MonOpxW) });

                var topBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(43, 87, 154)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(29, 63, 115)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Child = new TextBlock { Text = lbl, FontSize = 10, FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center }
                };
                Grid.SetRow(topBorder, 0); Grid.SetColumnSpan(topBorder, 2);
                monthGrid.Children.Add(topBorder);

                monthGrid.Children.Add(MakeHeaderCell("CAPEX", 0, 1, MonCapW, capexMonBg, Color.FromRgb(100, 50, 10)));
                monthGrid.Children.Add(MakeHeaderCell("OPEX",  0, 2, MonOpxW, opexMonBg,  Color.FromRgb(20, 70, 20)));
                HeaderPanel.Items.Add(monthGrid);
            }
            HeaderPanel.Items.Add(MakeTotalHeader(AppStrings.Get("RCost_Total"),    TotW,    Color.FromRgb(25,  60, 120)));
            HeaderPanel.Items.Add(MakeTotalHeader(AppStrings.Get("RCost_CapexTot"), CapTotW, Color.FromRgb(140, 70,  20)));
            HeaderPanel.Items.Add(MakeTotalHeader(AppStrings.Get("RCost_OpexTot"),  OpxTotW, Color.FromRgb(43, 100,  43)));

            // ── Linhas por recurso ──
            var grandCapexByMonth = new decimal[_months.Count];
            var grandOpexByMonth  = new decimal[_months.Count];
            decimal grandCapex = 0, grandOpex = 0;

            var byResource = filtered
                .GroupBy(r => r.ResourceName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key);

            foreach (var resGroup in byResource)
            {
                var resCapexByMonth = new decimal[_months.Count];
                var resOpexByMonth  = new decimal[_months.Count];
                foreach (var r in resGroup)
                    for (int mi = 0; mi < _months.Count; mi++)
                    {
                        resCapexByMonth[mi] += r.CapexByMonth[mi];
                        resOpexByMonth[mi]  += r.OpexByMonth[mi];
                    }

                bool resFirst = true;
                var byEpic = resGroup.GroupBy(r => r.EpicName, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key);
                foreach (var epicGroup in byEpic)
                {
                    bool epicFirst = true;
                    foreach (var row in epicGroup.OrderBy(r => r.FeatureName))
                    {
                        var leftBg = new SolidColorBrush(Color.FromRgb(248, 250, 255));
                        var leftRow = new StackPanel { Orientation = Orientation.Horizontal };

                        leftRow.Children.Add(MakeLeftCell(resFirst && epicFirst ? resGroup.Key : "", ResW, leftBg,
                            Color.FromRgb(43, 87, 154), bold: true));
                        leftRow.Children.Add(MakeLeftCell(epicFirst ? epicGroup.Key : "", EpicW, leftBg,
                            Color.FromRgb(100, 40, 140), bold: false));
                        leftRow.Children.Add(MakeLeftCell(row.FeatureName, FeatW, leftBg,
                            Color.FromRgb(20, 100, 120), bold: false));
                        leftRow.Children.Add(MakeLeftCell(row.TypeLabel, TypeW, leftBg,
                            row.IsCapex ? Color.FromRgb(140, 70, 20) : Color.FromRgb(43, 100, 43), bold: false, center: true));

                        LeftPanel.Items.Add(leftRow);

                        var dataRow = new StackPanel { Orientation = Orientation.Horizontal };
                        decimal rowTotal = 0, rowCapex = 0, rowOpex = 0;
                        // capture for closures
                        var capturedRow = row;
                        foreach (var mi in visMi)
                        {
                            decimal c = row.CapexByMonth[mi];
                            decimal o = row.OpexByMonth[mi];
                            rowTotal += c + o; rowCapex += c; rowOpex += o;
                            var capturedMi = mi;
                            Action? capClick = c > 0 ? () => OpenDrillDown(capturedRow.ResourceName, capturedRow.EpicName, capturedRow.FeatureName, true,  _months[capturedMi].Year, _months[capturedMi].Month) : null;
                            Action? opxClick = o > 0 ? () => OpenDrillDown(capturedRow.ResourceName, capturedRow.EpicName, capturedRow.FeatureName, false, _months[capturedMi].Year, _months[capturedMi].Month) : null;
                            dataRow.Children.Add(MakeValueCell(c > 0 ? FormatR(c) : "–", MonCapW,
                                c > 0 ? Color.FromRgb(120, 60, 10) : Color.FromRgb(200, 200, 200),
                                c > 0 ? Color.FromRgb(255, 248, 240) : Color.FromRgb(252, 252, 252), onClick: capClick));
                            dataRow.Children.Add(MakeValueCell(o > 0 ? FormatR(o) : "–", MonOpxW,
                                o > 0 ? Color.FromRgb(20, 100, 20) : Color.FromRgb(200, 200, 200),
                                o > 0 ? Color.FromRgb(240, 255, 240) : Color.FromRgb(252, 252, 252), onClick: opxClick));
                        }
                        Action? totCapClick = rowCapex > 0 ? () => OpenDrillDown(capturedRow.ResourceName, capturedRow.EpicName, capturedRow.FeatureName, true,  null, null) : null;
                        Action? totOpxClick = rowOpex  > 0 ? () => OpenDrillDown(capturedRow.ResourceName, capturedRow.EpicName, capturedRow.FeatureName, false, null, null) : null;
                        dataRow.Children.Add(MakeValueCell(FormatR(rowTotal), TotW, Color.FromRgb(25, 60, 120),  Color.FromRgb(235, 242, 255), bold: true));
                        dataRow.Children.Add(MakeValueCell(rowCapex > 0 ? FormatR(rowCapex) : "–", CapTotW, Color.FromRgb(120, 60, 10),  Color.FromRgb(255, 248, 240), bold: true, onClick: totCapClick));
                        dataRow.Children.Add(MakeValueCell(rowOpex  > 0 ? FormatR(rowOpex)  : "–", OpxTotW, Color.FromRgb(20, 100, 20),  Color.FromRgb(240, 255, 240), bold: true, onClick: totOpxClick));
                        DataPanel.Items.Add(dataRow);

                        resFirst  = false;
                        epicFirst = false;
                    }
                }

                // Total do recurso
                decimal resTotCapex = resCapexByMonth.Sum();
                decimal resTotOpex  = resOpexByMonth.Sum();
                var (leftTot, dataTot) = MakeTotalRow(
                    AppStrings.Get("RCost_TotalRow", resGroup.Key), resCapexByMonth, resOpexByMonth, visMi,
                    Color.FromRgb(220, 235, 255), Color.FromRgb(43, 87, 154));
                LeftPanel.Items.Add(leftTot);
                DataPanel.Items.Add(dataTot);

                LeftPanel.Items.Add(new Border { Height = 2, Background = new SolidColorBrush(Color.FromRgb(180, 200, 230)) });
                DataPanel.Items.Add(new Border { Height = 2, Background = new SolidColorBrush(Color.FromRgb(180, 200, 230)) });

                for (int mi = 0; mi < _months.Count; mi++)
                {
                    grandCapexByMonth[mi] += resCapexByMonth[mi];
                    grandOpexByMonth[mi]  += resOpexByMonth[mi];
                }
                grandCapex += resTotCapex;
                grandOpex  += resTotOpex;
            }

            // Grand total
            var (gtLeft, gtData) = MakeTotalRow(AppStrings.Get("RCost_GrandTotal"), grandCapexByMonth, grandOpexByMonth, visMi,
                Color.FromRgb(43, 87, 154), Colors.White, fontSize: 12);
            LeftPanel.Items.Add(gtLeft);
            DataPanel.Items.Add(gtData);

            UpdateSummary(filtered, grandCapex, grandOpex);
        }

        // ── Visual helpers ────────────────────────────────────────────────────
        private static Border MakeHeaderCell(string text, int row, int col, double width, Color bg, Color border)
        {
            var b = new Border
            {
                Width = width, Height = 22,
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock { Text = text, FontSize = 9, FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center }
            };
            Grid.SetRow(b, row); Grid.SetColumn(b, col - 1);
            return b;
        }

        private static Border MakeTotalHeader(string text, double width, Color bg)
        {
            return new Border
            {
                Width = width, Height = 44,
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(Color.FromRgb(20, 50, 100)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center }
            };
        }

        private static Border MakeLeftCell(string text, double width, SolidColorBrush bg, Color fg,
            bool bold = false, bool center = false)
        {
            return new Border
            {
                Width = width, Height = RowH, Background = bg,
                BorderBrush = new SolidColorBrush(Color.FromRgb(210, 220, 240)),
                BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 0, 4, 0),
                ToolTip = string.IsNullOrEmpty(text) ? null : text,
                Child = new TextBlock { Text = text, FontSize = 11,
                    FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(fg),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                    TextTrimming = TextTrimming.CharacterEllipsis }
            };
        }

        private static Border MakeValueCell(string text, double width, Color fg, Color bg, bool bold = false,
            Action? onClick = null)
        {
            var b = new Border
            {
                Width = width, Height = RowH, Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(Color.FromRgb(210, 220, 240)),
                BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 0, 4, 0),
                Cursor = onClick != null ? System.Windows.Input.Cursors.Hand : null,
                Child = new TextBlock { Text = text, FontSize = 11,
                    FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(fg),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center }
            };
            if (onClick != null)
            {
                b.MouseLeftButtonDown += (_, _) => onClick();
                b.MouseEnter += (_, _) => b.BorderBrush = new SolidColorBrush(Color.FromRgb(43, 87, 154));
                b.MouseLeave += (_, _) => b.BorderBrush = new SolidColorBrush(Color.FromRgb(210, 220, 240));
            }
            return b;
        }

        private (StackPanel left, StackPanel data) MakeTotalRow(
            string label, decimal[] capexByMonth, decimal[] opexByMonth,
            List<int> visMi, Color rowBg, Color textColor, int fontSize = 11)
        {
            var bg = new SolidColorBrush(rowBg);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var totalWidth = ResW + EpicW + FeatW + TypeW;
            left.Children.Add(new Border
            {
                Width = totalWidth, Height = RowH + 2, Background = bg,
                BorderBrush = new SolidColorBrush(Color.FromRgb(150, 170, 210)),
                BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(6, 0, 4, 0),
                Child = new TextBlock { Text = label, FontSize = fontSize, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(textColor),
                    VerticalAlignment = VerticalAlignment.Center }
            });

            var data = new StackPanel { Orientation = Orientation.Horizontal };
            decimal tot = 0, totCap = 0, totOpx = 0;
            foreach (var mi in visMi)
            {
                decimal c = capexByMonth[mi], o = opexByMonth[mi];
                tot += c + o; totCap += c; totOpx += o;
                data.Children.Add(new Border
                {
                    Width = MonCapW, Height = RowH + 2, Background = bg,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(150, 170, 210)),
                    BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 0, 4, 0),
                    Child = new TextBlock { Text = c > 0 ? FormatR(c) : "–", FontSize = fontSize, FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(c > 0 ? Color.FromRgb(120, 60, 10) : Color.FromRgb(160, 160, 160)),
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }
                });
                data.Children.Add(new Border
                {
                    Width = MonOpxW, Height = RowH + 2, Background = bg,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(150, 170, 210)),
                    BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 0, 4, 0),
                    Child = new TextBlock { Text = o > 0 ? FormatR(o) : "–", FontSize = fontSize, FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(o > 0 ? Color.FromRgb(20, 100, 20) : Color.FromRgb(160, 160, 160)),
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }
                });
            }
            data.Children.Add(MakeTotalCell(FormatR(tot),              TotW,    rowBg, textColor, fontSize));
            data.Children.Add(MakeTotalCell(totCap > 0 ? FormatR(totCap) : "–", CapTotW, rowBg, Color.FromRgb(120, 60, 10), fontSize));
            data.Children.Add(MakeTotalCell(totOpx > 0 ? FormatR(totOpx) : "–", OpxTotW, rowBg, Color.FromRgb(20, 100, 20), fontSize));
            return (left, data);
        }

        private static Border MakeTotalCell(string text, double width, Color bg, Color fg, int fontSize = 11)
        {
            return new Border
            {
                Width = width, Height = RowH + 2, Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(Color.FromRgb(150, 170, 210)),
                BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 0, 4, 0),
                Child = new TextBlock { Text = text, FontSize = fontSize, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(fg),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }
            };
        }

        private static string FormatR(decimal v) => $"R$ {v:N0}";

        private void UpdateSummary(List<FeatureRow> rows, decimal grandCapex = 0, decimal grandOpex = 0)
        {
            decimal total = grandCapex + grandOpex;
            TotalText.Text   = AppStrings.Get("RCost_HeaderTotal", total);
            SummaryText.Text = AppStrings.Get("RCost_Summary", rows.Count, grandCapex, grandOpex, total);
            CountText.Text   = AppStrings.Get("RCost_LineCount", rows.Count);
        }

        // ── Events ────────────────────────────────────────────────────────────
        private void OnFilterChanged(object sender, RoutedEventArgs e)
        {
            if (ResourceFilterList?.ItemsSource == null) return;
            BuildGrid();
        }

        private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResourceFilterList?.ItemsSource == null) return;
            BuildGrid();
        }

        private void OnMainScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            HeaderScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            LeftScroll.ScrollToVerticalOffset(e.VerticalOffset);
        }

        private void OnLeftScrollChanged(object sender, ScrollChangedEventArgs e)
            => MainScroll.ScrollToVerticalOffset(e.VerticalOffset);

        private void OnOpenCostFileClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title            = AppStrings.Get("RCost_OpenFileTitle"),
                Filter           = ResourceCostConfigService.FileFilter,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog() != true) return;

            var password = PromptPassword();
            if (password == null) return;

            try
            {
                int updated = ResourceCostConfigService.Load(dlg.FileName, _resources, password);
                _allLines = ResourceCostService.Compute(_allTasks, _resources);
                _months   = _allLines.Select(l => new DateTime(l.Year, l.Month, 1)).Distinct().OrderBy(d => d).ToList();
                _rows     = BuildFeatureRows(_allLines);
                BuildGrid();
                MessageBox.Show(AppStrings.Get("RCost_FileLoaded", updated),
                    AppStrings.Get("RCost_CostLoadedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                MessageBox.Show(AppStrings.Get("RCost_WrongPassword"), AppStrings.Get("RCost_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(AppStrings.Get("RCost_LoadError", ex.Message), AppStrings.Get("RCost_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string? PromptPassword()
        {
            var win = new Window
            {
                Title                 = AppStrings.Get("RCost_PasswordTitle"),
                Width                 = 340,
                Height                = 148,
                ResizeMode            = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                Background            = System.Windows.Media.Brushes.White
            };
            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock { Text = AppStrings.Get("RCost_EnterPassword"), Margin = new Thickness(0, 0, 0, 8) });
            var pb = new PasswordBox { Height = 28, Margin = new Thickness(0, 0, 0, 12) };
            root.Children.Add(pb);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok     = new Button { Content = "OK",       Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = AppStrings.Get("RCost_Cancel"), Width = 80, IsCancel  = true };
            ok.Click     += (_, _) => { win.DialogResult = true;  win.Close(); };
            cancel.Click += (_, _) => { win.DialogResult = false; win.Close(); };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            root.Children.Add(btns);
            win.Content = root;
            pb.Focus();
            return win.ShowDialog() == true ? pb.Password : null;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        // ── Export Excel XML ──────────────────────────────────────────────────
        private void OnExportExcelClick(object sender, RoutedEventArgs e)
        {
            var filtered = ApplyFilters();
            if (filtered.Count == 0) { MessageBox.Show(AppStrings.Get("RCost_NoData"), AppStrings.Get("RCost_Export"), MessageBoxButton.OK, MessageBoxImage.Information); return; }

            var visMi = Enumerable.Range(0, _months.Count)
                .Where(mi => filtered.Any(r => r.CapexByMonth[mi] > 0 || r.OpexByMonth[mi] > 0))
                .ToList();

            var dlg = new SaveFileDialog
            {
                Title            = AppStrings.Get("RCost_ExportTitle"),
                Filter           = "Excel XML 2003 (*.xml)|*.xml",
                FileName         = $"custo-recurso-{DateTime.Today:yyyy-MM-dd}.xml",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                System.Xml.Linq.XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
                System.Xml.Linq.XNamespace ss = "urn:schemas-microsoft-com:office:spreadsheet";

                var styles = new System.Xml.Linq.XElement(ns + "Styles",
                    XSt(ns, "Default"),
                    XSt(ns, "H0",  bg: "#1D3F73", fg: "#FFFFFF", bold: true),
                    XSt(ns, "HM",  bg: "#2B579A", fg: "#FFFFFF", bold: true, hAlign: "Center"),
                    XSt(ns, "HC",  bg: "#8C4614", fg: "#FFFFFF", bold: true, hAlign: "Center"),
                    XSt(ns, "HO",  bg: "#2B642B", fg: "#FFFFFF", bold: true, hAlign: "Center"),
                    XSt(ns, "HT",  bg: "#193C78", fg: "#FFFFFF", bold: true, hAlign: "Center"),
                    XSt(ns, "SL",  bg: "#F8FAFF", fg: "#1E2840"),
                    XSt(ns, "SC",  bg: "#FFF8F0", fg: "#783C0A", hAlign: "Right", numFmt: "#,##0.00"),
                    XSt(ns, "SO",  bg: "#F0FAF0", fg: "#145A14", hAlign: "Right", numFmt: "#,##0.00"),
                    XSt(ns, "ST",  bg: "#E6EEFA", fg: "#143264", bold: true, hAlign: "Right", numFmt: "#,##0.00"),
                    XSt(ns, "STC", bg: "#FFF8F0", fg: "#783C0A", bold: true, hAlign: "Right", numFmt: "#,##0.00"),
                    XSt(ns, "STO", bg: "#F0FAF0", fg: "#145A14", bold: true, hAlign: "Right", numFmt: "#,##0.00"),
                    XSt(ns, "RL",  bg: "#2B579A", fg: "#FFFFFF", bold: true),
                    XSt(ns, "RC",  bg: "#8C4614", fg: "#FFFFFF", bold: true, hAlign: "Right", numFmt: "#,##0.00"),
                    XSt(ns, "RO",  bg: "#2B642B", fg: "#FFFFFF", bold: true, hAlign: "Right", numFmt: "#,##0.00"),
                    XSt(ns, "RT",  bg: "#193C78", fg: "#FFFFFF", bold: true, hAlign: "Right", numFmt: "#,##0.00"),
                    XSt(ns, "GL",  bg: "#0F2857", fg: "#FFFFFF", bold: true),
                    XSt(ns, "GC",  bg: "#643208", fg: "#FFFFFF", bold: true, hAlign: "Right", numFmt: "#,##0.00"),
                    XSt(ns, "GO",  bg: "#1E501E", fg: "#FFFFFF", bold: true, hAlign: "Right", numFmt: "#,##0.00"),
                    XSt(ns, "GT",  bg: "#0A1E3C", fg: "#FFFFFF", bold: true, hAlign: "Right", numFmt: "#,##0.00")
                );

                var tableChildren = new List<System.Xml.Linq.XElement>();

                // Larguras
                tableChildren.Add(new System.Xml.Linq.XElement(ns + "Column", new System.Xml.Linq.XAttribute(ss + "Width", "130")));
                tableChildren.Add(new System.Xml.Linq.XElement(ns + "Column", new System.Xml.Linq.XAttribute(ss + "Width", "160")));
                tableChildren.Add(new System.Xml.Linq.XElement(ns + "Column", new System.Xml.Linq.XAttribute(ss + "Width", "190")));
                tableChildren.Add(new System.Xml.Linq.XElement(ns + "Column", new System.Xml.Linq.XAttribute(ss + "Width", "45")));
                foreach (var _ in visMi)
                {
                    tableChildren.Add(new System.Xml.Linq.XElement(ns + "Column", new System.Xml.Linq.XAttribute(ss + "Width", "70")));
                    tableChildren.Add(new System.Xml.Linq.XElement(ns + "Column", new System.Xml.Linq.XAttribute(ss + "Width", "70")));
                }
                tableChildren.Add(new System.Xml.Linq.XElement(ns + "Column", new System.Xml.Linq.XAttribute(ss + "Width", "80")));
                tableChildren.Add(new System.Xml.Linq.XElement(ns + "Column", new System.Xml.Linq.XAttribute(ss + "Width", "80")));
                tableChildren.Add(new System.Xml.Linq.XElement(ns + "Column", new System.Xml.Linq.XAttribute(ss + "Width", "80")));

                // Cabeçalho linha 1
                var hdr1 = new System.Xml.Linq.XElement(ns + "Row", new System.Xml.Linq.XAttribute(ss + "Height", "22"));
                hdr1.Add(XCell(ns, AppStrings.Get("RCost_ColResource"), "H0")); hdr1.Add(XCell(ns, "EPIC", "H0"));
                hdr1.Add(XCell(ns, "Feature", "H0")); hdr1.Add(XCell(ns, AppStrings.Get("RCost_ColType"), "H0"));
                foreach (var mi in visMi)
                    hdr1.Add(XCell(ns, _months[mi].ToString("MMM/yyyy"), "HM", mergeAcross: 1));
                hdr1.Add(XCell(ns, AppStrings.Get("RCost_ExcelTotal"),      "HT"));
                hdr1.Add(XCell(ns, AppStrings.Get("RCost_ExcelCapexTotal"),"HT"));
                hdr1.Add(XCell(ns, AppStrings.Get("RCost_ExcelOpexTotal"), "HT"));
                tableChildren.Add(hdr1);

                // Cabeçalho linha 2
                var hdr2 = new System.Xml.Linq.XElement(ns + "Row", new System.Xml.Linq.XAttribute(ss + "Height", "18"));
                hdr2.Add(XCell(ns, "", "H0")); hdr2.Add(XCell(ns, "", "H0"));
                hdr2.Add(XCell(ns, "", "H0")); hdr2.Add(XCell(ns, "", "H0"));
                foreach (var _ in visMi) { hdr2.Add(XCell(ns, "CAPEX", "HC")); hdr2.Add(XCell(ns, "OPEX", "HO")); }
                hdr2.Add(XCell(ns, "", "HT")); hdr2.Add(XCell(ns, "", "HT")); hdr2.Add(XCell(ns, "", "HT"));
                tableChildren.Add(hdr2);

                // Dados
                var grandCapex = new decimal[_months.Count];
                var grandOpex  = new decimal[_months.Count];

                foreach (var resGroup in filtered.GroupBy(r => r.ResourceName).OrderBy(g => g.Key))
                {
                    var resCapex = new decimal[_months.Count];
                    var resOpex  = new decimal[_months.Count];
                    bool resFirst = true;

                    foreach (var row in resGroup.OrderBy(r => r.EpicName).ThenBy(r => r.FeatureName))
                    {
                        decimal rTot = 0, rCap = 0, rOpx = 0;
                        var xrow = new System.Xml.Linq.XElement(ns + "Row", new System.Xml.Linq.XAttribute(ss + "Height", "18"));
                        xrow.Add(XCell(ns, resFirst ? row.ResourceName : "", "SL"));
                        xrow.Add(XCell(ns, row.EpicName,    "SL"));
                        xrow.Add(XCell(ns, row.FeatureName, "SL"));
                        xrow.Add(XCell(ns, row.TypeLabel,   "SL"));
                        foreach (var mi in visMi)
                        {
                            decimal c = row.CapexByMonth[mi], o = row.OpexByMonth[mi];
                            rTot += c + o; rCap += c; rOpx += o;
                            resCapex[mi] += c; resOpex[mi] += o;
                            xrow.Add(XCell(ns, c > 0 ? (object)(double)c : "", "SC"));
                            xrow.Add(XCell(ns, o > 0 ? (object)(double)o : "", "SO"));
                        }
                        xrow.Add(XCell(ns, (double)rTot, "ST"));
                        xrow.Add(XCell(ns, rCap > 0 ? (object)(double)rCap : "", "STC"));
                        xrow.Add(XCell(ns, rOpx > 0 ? (object)(double)rOpx : "", "STO"));
                        tableChildren.Add(xrow);
                        resFirst = false;
                    }

                    // Total recurso
                    decimal resTot = resCapex.Sum() + resOpex.Sum();
                    var rRow = new System.Xml.Linq.XElement(ns + "Row", new System.Xml.Linq.XAttribute(ss + "Height", "20"));
                    rRow.Add(XCell(ns, AppStrings.Get("RCost_ExcelResTotal", resGroup.Key), "RL"));
                    rRow.Add(XCell(ns, "", "RL")); rRow.Add(XCell(ns, "", "RL")); rRow.Add(XCell(ns, "", "RL"));
                    foreach (var mi in visMi)
                    {
                        grandCapex[mi] += resCapex[mi]; grandOpex[mi] += resOpex[mi];
                        rRow.Add(XCell(ns, resCapex[mi] > 0 ? (object)(double)resCapex[mi] : "", "RC"));
                        rRow.Add(XCell(ns, resOpex[mi]  > 0 ? (object)(double)resOpex[mi]  : "", "RO"));
                    }
                    rRow.Add(XCell(ns, (double)resTot, "RT"));
                    rRow.Add(XCell(ns, resCapex.Sum() > 0 ? (object)(double)resCapex.Sum() : "", "RC"));
                    rRow.Add(XCell(ns, resOpex.Sum()  > 0 ? (object)(double)resOpex.Sum()  : "", "RO"));
                    tableChildren.Add(rRow);
                    tableChildren.Add(new System.Xml.Linq.XElement(ns + "Row", new System.Xml.Linq.XAttribute(ss + "Height", "4")));
                }

                // Grand total
                decimal gTot = grandCapex.Sum() + grandOpex.Sum();
                var gRow = new System.Xml.Linq.XElement(ns + "Row", new System.Xml.Linq.XAttribute(ss + "Height", "22"));
                gRow.Add(XCell(ns, AppStrings.Get("RCost_GrandTotal"), "GL"));
                gRow.Add(XCell(ns, "", "GL")); gRow.Add(XCell(ns, "", "GL")); gRow.Add(XCell(ns, "", "GL"));
                foreach (var mi in visMi)
                {
                    gRow.Add(XCell(ns, grandCapex[mi] > 0 ? (object)(double)grandCapex[mi] : "", "GC"));
                    gRow.Add(XCell(ns, grandOpex[mi]  > 0 ? (object)(double)grandOpex[mi]  : "", "GO"));
                }
                gRow.Add(XCell(ns, (double)gTot, "GT"));
                gRow.Add(XCell(ns, grandCapex.Sum() > 0 ? (object)(double)grandCapex.Sum() : "", "GC"));
                gRow.Add(XCell(ns, grandOpex.Sum()  > 0 ? (object)(double)grandOpex.Sum()  : "", "GO"));
                tableChildren.Add(gRow);

                var workbook = new System.Xml.Linq.XDocument(
                    new System.Xml.Linq.XDeclaration("1.0", "utf-8", "yes"),
                    new System.Xml.Linq.XElement(ns + "Workbook",
                        new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "ss", ss),
                        styles,
                        new System.Xml.Linq.XElement(ns + "Worksheet",
                            new System.Xml.Linq.XAttribute(ss + "Name", AppStrings.Get("RCost_ExcelSheetName")),
                            new System.Xml.Linq.XElement(ns + "Table", tableChildren))));

                workbook.Save(dlg.FileName);
                MessageBox.Show(AppStrings.Get("RCost_ExportSuccess", dlg.FileName), AppStrings.Get("RCost_Export"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(AppStrings.Get("RCost_ExportError", ex.Message), AppStrings.Get("RCost_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── XML helpers (mesmo padrão do ProjectAllocationMapWindow) ─────────
        private static System.Xml.Linq.XElement XSt(System.Xml.Linq.XNamespace ns, string id,
            string? bg = null, string fg = "#000000", bool bold = false,
            string hAlign = "Left", string? numFmt = null)
        {
            var style = new System.Xml.Linq.XElement(ns + "Style", new System.Xml.Linq.XAttribute(ns + "ID", id));
            style.Add(new System.Xml.Linq.XElement(ns + "Alignment",
                new System.Xml.Linq.XAttribute(ns + "Horizontal", hAlign),
                new System.Xml.Linq.XAttribute(ns + "Vertical",   "Center")));
            var font = new System.Xml.Linq.XElement(ns + "Font",
                new System.Xml.Linq.XAttribute(ns + "FontName", "Calibri"),
                new System.Xml.Linq.XAttribute(ns + "Size", "10"),
                new System.Xml.Linq.XAttribute(ns + "Color", fg));
            if (bold) font.Add(new System.Xml.Linq.XAttribute(ns + "Bold", "1"));
            style.Add(font);
            if (bg != null)
                style.Add(new System.Xml.Linq.XElement(ns + "Interior",
                    new System.Xml.Linq.XAttribute(ns + "Color",   bg),
                    new System.Xml.Linq.XAttribute(ns + "Pattern", "Solid")));
            style.Add(new System.Xml.Linq.XElement(ns + "Borders",
                XBorder(ns, "Bottom"), XBorder(ns, "Left"), XBorder(ns, "Right"), XBorder(ns, "Top")));
            if (numFmt != null)
                style.Add(new System.Xml.Linq.XElement(ns + "NumberFormat",
                    new System.Xml.Linq.XAttribute(ns + "Format", numFmt)));
            return style;
        }

        private static System.Xml.Linq.XElement XBorder(System.Xml.Linq.XNamespace ns, string pos) =>
            new(ns + "Border",
                new System.Xml.Linq.XAttribute(ns + "Position",  pos),
                new System.Xml.Linq.XAttribute(ns + "LineStyle", "Continuous"),
                new System.Xml.Linq.XAttribute(ns + "Weight",    "1"),
                new System.Xml.Linq.XAttribute(ns + "Color",     "#C8D0DC"));

        private static System.Xml.Linq.XElement XCell(System.Xml.Linq.XNamespace ns, object value, string styleId, int mergeAcross = 0)
        {
            var cell = new System.Xml.Linq.XElement(ns + "Cell", new System.Xml.Linq.XAttribute(ns + "StyleID", styleId));
            if (mergeAcross > 0) cell.Add(new System.Xml.Linq.XAttribute(ns + "MergeAcross", mergeAcross));
            bool isNum = value is double or float or int or long or decimal;
            cell.Add(new System.Xml.Linq.XElement(ns + "Data",
                new System.Xml.Linq.XAttribute(ns + "Type", isNum ? "Number" : "String"),
                isNum ? Convert.ToDouble(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                      : value?.ToString() ?? ""));
            return cell;
        }

        // ── Drill-down ────────────────────────────────────────────────────────
        private void OpenDrillDown(string resource, string epic, string feature, bool isCapex, int? year, int? month)
        {
            var lines = _allLines.Where(l =>
                l.ResourceName.Equals(resource, StringComparison.OrdinalIgnoreCase) &&
                l.EpicName.Equals(epic, StringComparison.OrdinalIgnoreCase) &&
                l.FeatureName.Equals(feature, StringComparison.OrdinalIgnoreCase) &&
                l.IsCapex == isCapex &&
                (year  == null || l.Year  == year) &&
                (month == null || l.Month == month))
                .ToList();

            var win = new CostDrillDownWindow(resource, epic, feature, isCapex, year, month, lines) { Owner = this };
            win.ShowDialog();
        }
    }
}
