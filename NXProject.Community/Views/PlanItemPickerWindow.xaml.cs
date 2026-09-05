// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>
    /// Busca de um objeto do cronograma (EPIC, Feature, Story ou Task) para o Task Plan.
    /// Parecida com a busca de predecessoras, mas com seleção única e do tipo pedido.
    /// </summary>
    public partial class PlanItemPickerWindow : Window
    {
        private readonly ICollectionView _view;

        public ObservableCollection<Row> Rows { get; } = new();

        /// <summary>Objeto do cronograma escolhido (null se cancelado).</summary>
        public ProjectTask? Selected { get; private set; }

        public PlanItemPickerWindow(string typeLabel, IEnumerable<ProjectTask> candidates, string? initialSearch = null)
        {
            InitializeComponent();
            DataContext = this;

            HeaderText.Text = AppStrings.Get("TaskPlan_PickHeader", typeLabel);
            Title = AppStrings.Get("TaskPlan_PickHeader", typeLabel);

            foreach (var t in candidates.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var displayId = t.TfsId is > 0 ? $"{t.TfsId.Value}:T" : $"{t.Id}:I";
                Rows.Add(new Row(t, displayId, t.Name ?? "", t.Parent?.Name ?? ""));
            }

            _view = CollectionViewSource.GetDefaultView(Rows);
            _view.Filter = FilterRow;

            if (!string.IsNullOrWhiteSpace(initialSearch))
                SearchBox.Text = initialSearch;
            SearchBox.Focus();
        }

        private bool FilterRow(object item)
        {
            if (item is not Row row) return false;
            var q = SearchBox.Text?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(q)
                || row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || row.ParentName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || row.DisplayId.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view.Refresh();

        private void OnClearSearchClick(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            SearchBox.Focus();
        }

        private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Confirm();

        private void OnOkClick(object sender, RoutedEventArgs e) => Confirm();

        private void Confirm()
        {
            if (CandidateList.SelectedItem is not Row row)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_PickNone"),
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Selected = row.Task;
            DialogResult = true;
        }

        public sealed record Row(ProjectTask Task, string DisplayId, string Name, string ParentName);
    }
}
