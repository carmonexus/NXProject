// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class ProjectPickerWindow : Window
    {
        private readonly ObservableCollection<ProjectEntry> _all = [];

        public List<PortfolioProjectConfig> SelectedConfigs { get; private set; } = [];

        public ProjectPickerWindow(List<DevOpsProject> devOpsProjects,
                                   List<PortfolioProjectConfig> existingConfigs)
        {
            InitializeComponent();

            foreach (var dp in devOpsProjects)
            {
                var cfg = existingConfigs.FirstOrDefault(c =>
                    string.Equals(c.ProjectName, dp.Name, StringComparison.OrdinalIgnoreCase));

                var src = string.IsNullOrWhiteSpace(dp.CostCenterSource)
                    ? (dp.IsOpex ? "OPEX" : "CAPEX")
                    : dp.CostCenterSource.ToUpperInvariant();
                _all.Add(new ProjectEntry
                {
                    ProjectName      = dp.Name,
                    RootWorkItemId   = dp.RootWorkItemId,
                    IsOpex           = dp.IsOpex,
                    CostCenter       = dp.CostCenter,
                    CostCenterSource = src,
                    IsSelected       = cfg != null
                });
            }

            if (_all.Count == 0)
                EmptyMsg.Visibility = Visibility.Visible;

            ApplyFilter();
            RefreshSummary();
        }

        private void ApplyFilter()
        {
            var q = SearchBox.Text.Trim();
            var filtered = string.IsNullOrEmpty(q)
                ? _all
                : (IEnumerable<ProjectEntry>)_all.Where(e =>
                    e.ProjectName.Contains(q, StringComparison.OrdinalIgnoreCase));

            ProjectList.Items.Clear();
            foreach (var entry in filtered)
                ProjectList.Items.Add(BuildRow(entry));
        }

        private UIElement BuildRow(ProjectEntry entry)
        {
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });

            var cb = new CheckBox
            {
                IsChecked           = entry.IsSelected,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(4, 0, 0, 0)
            };
            cb.Checked   += (_, _) => { entry.IsSelected = true;  RefreshSummary(); };
            cb.Unchecked += (_, _) => { entry.IsSelected = false; RefreshSummary(); };
            Grid.SetColumn(cb, 0);

            var nameBlock = new TextBlock
            {
                Text              = entry.ProjectName,
                FontSize          = 12,
                FontWeight        = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                Margin            = new Thickness(4, 4, 4, 4),
                ToolTip           = entry.ProjectName
            };
            Grid.SetColumn(nameBlock, 1);

            var typeLabel = entry.CostCenterSource == "EPIC" ? "EPIC"
                          : entry.IsOpex ? "OPEX" : "CAPEX";
            var typeColor = entry.CostCenterSource == "EPIC"
                ? Color.FromRgb(80, 0, 140)
                : entry.IsOpex ? Color.FromRgb(0, 100, 0) : Color.FromRgb(140, 60, 0);
            var typeBlock = new TextBlock
            {
                Text              = typeLabel,
                FontSize          = 11,
                FontWeight        = FontWeights.SemiBold,
                Foreground        = new SolidColorBrush(typeColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(4, 4, 4, 4)
            };
            Grid.SetColumn(typeBlock, 2);

            var ccBlock = new TextBlock
            {
                Text             = entry.CostCenter,
                FontSize         = 11,
                Foreground       = Brushes.DimGray,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming     = TextTrimming.CharacterEllipsis,
                Margin           = new Thickness(4, 4, 4, 4),
                ToolTip          = entry.CostCenter
            };
            Grid.SetColumn(ccBlock, 3);

            var idBlock = new TextBlock
            {
                Text                = entry.RootWorkItemId > 0 ? entry.RootWorkItemId.ToString() : "—",
                FontSize            = 11,
                Foreground          = entry.RootWorkItemId > 0 ? Brushes.DarkGreen : Brushes.Gray,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(4, 4, 6, 4),
                ToolTip             = entry.RootWorkItemId > 0
                    ? AppStrings.Get("ProjPick_RootIdTip", entry.RootWorkItemId)
                    : AppStrings.Get("ProjPick_NoRootIdTip")
            };
            Grid.SetColumn(idBlock, 4);

            grid.Children.Add(cb);
            grid.Children.Add(nameBlock);
            grid.Children.Add(typeBlock);
            grid.Children.Add(ccBlock);
            grid.Children.Add(idBlock);

            return new Border
            {
                Child           = grid,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
        }

        private void RefreshSummary()
        {
            int selected = _all.Count(e => e.IsSelected);
            int semId    = _all.Count(e => e.IsSelected && e.RootWorkItemId <= 0);
            SummaryText.Text = AppStrings.Get("ProjPick_Summary", selected) +
                               (semId > 0 ? AppStrings.Get("ProjPick_SummaryNoId", semId) : "");
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
            RefreshSummary();
        }

        private void OnClearSearchClick(object sender, RoutedEventArgs e) => SearchBox.Text = string.Empty;

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            SelectedConfigs = _all
                .Where(x => x.IsSelected)
                .Select(x => new PortfolioProjectConfig
                {
                    ProjectName      = x.ProjectName,
                    FilePath         = string.Empty,
                    IsOpex           = x.IsOpex,
                    CostCenter       = x.CostCenter,
                    CostCenterSource = x.CostCenterSource
                })
                .ToList();

            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

        public sealed class ProjectEntry : INotifyPropertyChanged
        {
            private bool _isSelected;

            public string ProjectName      { get; set; } = string.Empty;
            public int    RootWorkItemId   { get; set; }
            public bool   IsOpex           { get; set; } = true;
            public string CostCenter       { get; set; } = string.Empty;
            public string CostCenterSource { get; set; } = string.Empty;

            public bool IsSelected
            {
                get => _isSelected;
                set { _isSelected = value; OnPropertyChanged(); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? n = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }
}
