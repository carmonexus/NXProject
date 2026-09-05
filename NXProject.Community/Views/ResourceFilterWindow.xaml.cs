// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public class ResourceFilterRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        public int ResourceId { get; }
        public string Name { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }

        public ResourceFilterRow(int id, string name, bool selected)
        {
            ResourceId = id;
            Name = name;
            _isSelected = selected;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public partial class ResourceFilterWindow : Window
    {
        private readonly ICollectionView _view;

        public ObservableCollection<ResourceFilterRow> Rows { get; } = new();

        // IDs dos recursos selecionados após OK; null = sem filtro (todos)
        public HashSet<int>? SelectedResourceIds { get; private set; }

        public ResourceFilterWindow(IEnumerable<Resource> resources, HashSet<int>? activeFilter)
        {
            InitializeComponent();
            DataContext = this;

            foreach (var r in resources.OrderBy(r => r.Name))
            {
                var row = new ResourceFilterRow(r.Id, r.Name,
                    activeFilter == null || activeFilter.Contains(r.Id));
                row.PropertyChanged += (_, _) => RefreshCount();
                Rows.Add(row);
            }

            _view = CollectionViewSource.GetDefaultView(Rows);
            _view.Filter = o => o is ResourceFilterRow r &&
                (string.IsNullOrWhiteSpace(SearchBox.Text) ||
                 r.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));

            RefreshCount();
        }

        private void RefreshCount()
        {
            var sel = Rows.Count(r => r.IsSelected);
            CountLabel.Text = AppStrings.Get("RFilter_Count", sel, Rows.Count);
        }

        private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => _view.Refresh();

        private void OnSelectAllClick(object sender, RoutedEventArgs e)
        {
            foreach (var r in Rows) r.IsSelected = true;
        }

        private void OnClearAllClick(object sender, RoutedEventArgs e)
        {
            foreach (var r in Rows) r.IsSelected = false;
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            var selected = Rows.Where(r => r.IsSelected).Select(r => r.ResourceId).ToHashSet();
            // Se todos marcados, sem filtro ativo
            SelectedResourceIds = selected.Count == Rows.Count ? null : selected;
            DialogResult = true;
        }
    }
}
