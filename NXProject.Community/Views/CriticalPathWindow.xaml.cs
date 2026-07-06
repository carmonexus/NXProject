using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class CriticalPathWindow : Window
    {
        public sealed class CpRow
        {
            public string  IdKey      { get; init; } = "";
            public string? TfsType    { get; init; }
            public string  Name       { get; init; } = "";
            public string  ESText     { get; init; } = "";
            public string  EFText     { get; init; } = "";
            public string  DurText    { get; init; } = "";
            public string  FloatDaysText { get; init; } = "";
            public string  StatusText { get; init; } = "";
            public string  PredText   { get; init; } = "";
            public bool    IsCritical { get; init; }
            public bool    IsRisk     { get; init; }
            public Brush   StatusColor { get; init; } = Brushes.Black;
            public FontWeight StatusWeight { get; init; } = FontWeights.Normal;
        }

        private readonly List<CpRow> _allRows = new();
        private readonly ICollectionView _view;
        private readonly Action? _configureSlack;

        public CriticalPathWindow(
            IEnumerable<ProjectTask> allTasks,
            double riskSlackDays = 2.0,
            double criticalSlackDays = 1.0,
            Action? configureSlack = null)
        {
            InitializeComponent();
            _configureSlack = configureSlack;

            var entries = CriticalPathService.Compute(allTasks);
            var taskById = allTasks.ToDictionary(t => t.Id);
            riskSlackDays = Math.Max(0.0, riskSlackDays);
            criticalSlackDays = Math.Max(0.0, criticalSlackDays);
            if (riskSlackDays < criticalSlackDays)
                riskSlackDays = criticalSlackDays;

            foreach (var e in entries)
            {
                var t         = e.Task;
                var idKey     = t.HasTfsLink ? $"{t.TfsId}:T" : $"{t.Id}:I";
                var dur       = (e.EF - e.ES).TotalDays;
                var predNames = t.PredecessorIds
                    .Select(id => taskById.TryGetValue(id, out var pt)
                        ? (pt.HasTfsLink ? $"{pt.TfsId}:T" : $"{pt.Id}:I")
                        : id.ToString())
                    .ToList();

                bool critical = e.TotalFloat < criticalSlackDays;
                bool risk = !critical && riskSlackDays > 0.0 && e.TotalFloat <= riskSlackDays;

                _allRows.Add(new CpRow
                {
                    IdKey      = idKey,
                    TfsType    = t.TfsType,
                    Name       = t.Name,
                    ESText     = e.ES.ToString("dd/MM/yy"),
                    EFText     = e.EF.ToString("dd/MM/yy"),
                    DurText    = $"{dur:0}d",
                    FloatDaysText = $"{e.TotalFloat:0.#}d",
                    StatusText = critical ? AppStrings.Get("CPath_StatusCritical") : risk ? AppStrings.Get("CPath_StatusRisk") : AppStrings.Get("CPath_StatusNormal"),
                    PredText   = predNames.Count > 0 ? string.Join(", ", predNames) : "—",
                    IsCritical = critical,
                    IsRisk     = risk,
                    StatusColor = critical
                        ? new SolidColorBrush(Color.FromRgb(192, 57, 43))
                        : risk
                        ? new SolidColorBrush(Color.FromRgb(180, 110, 0))
                        : new SolidColorBrush(Color.FromRgb(39, 174, 96)),
                    StatusWeight = critical || risk ? FontWeights.Bold : FontWeights.Normal
                });
            }

            int critCount = _allRows.Count(r => r.IsCritical);
            int riskCount = _allRows.Count(r => r.IsRisk);
            SubtitleText.Text = riskSlackDays > 0.0
                ? AppStrings.Get("CPath_SubtitleFull", _allRows.Count, critCount, riskCount, criticalSlackDays, riskSlackDays)
                : AppStrings.Get("CPath_Subtitle", _allRows.Count, critCount);

            _view = CollectionViewSource.GetDefaultView(_allRows);
            _view.Filter = FilterRow;
            PathGrid.ItemsSource = _view;
            UpdateCount();
        }

        private bool FilterRow(object obj)
        {
            if (obj is not CpRow row) return false;
            if (OnlyCriticalCheck.IsChecked == true && !row.IsCritical) return false;
            var q = FilterBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(q)) return true;
            return row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || row.IdKey.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            if (_view == null) return;
            _view.Refresh();
            UpdateCount();
        }

        private void UpdateCount()
        {
            int visible = _allRows.Count(r => FilterRow(r));
            CountText.Text = AppStrings.Get("CPath_CountShown", visible);
        }

        private void OnConfigureSlackClick(object sender, RoutedEventArgs e)
        {
            Close();
            _configureSlack?.Invoke();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
