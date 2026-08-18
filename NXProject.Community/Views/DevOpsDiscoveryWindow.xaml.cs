using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class DevOpsDiscoveryWindow : Window
    {
        private readonly ObservableCollection<DiscoveryItem> _items = [];
        private readonly ICollectionView _itemsView;
        public List<DevOpsProject> SelectedProjects { get; private set; } = [];
        // Processo do Team Project (Agile/Scrum/CMMI/Basic) — mesmo para todos os itens
        // deste projeto; lido uma vez e gravado em cada DevOpsProject selecionado.
        private string _process = "";

        public DevOpsDiscoveryWindow(TfsConnectionOptions options, IEnumerable<DevOpsProject> existing)
        {
            InitializeComponent();
            _itemsView = CollectionViewSource.GetDefaultView(_items);
            _itemsView.Filter = FilterItem;
            ItemsGrid.ItemsSource = _itemsView;
            _items.CollectionChanged += (_, _) => RefreshCount();
            SubtitleText.Text = AppStrings.Get("Disc_Subtitle", options.OrganizationUrl, options.TeamProject);
            LoadAsync(options, existing.Select(p => p.RootWorkItemId).ToHashSet());
        }

        private bool FilterItem(object obj)
        {
            if (obj is not DiscoveryItem item) return false;

            var nameFilter  = NameFilterBox?.Text?.Trim() ?? "";
            var ownerFilter = OwnerFilterBox?.Text?.Trim() ?? "";

            if (nameFilter.Length > 0 &&
                (item.Title?.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
                return false;

            if (ownerFilter.Length > 0 &&
                (item.Owner?.IndexOf(ownerFilter, StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
                return false;

            return true;
        }

        private void OnFilterChanged(object sender, TextChangedEventArgs e)
        {
            _itemsView?.Refresh();
            RefreshCount();
        }

        private void OnClearFilterClick(object sender, RoutedEventArgs e)
        {
            NameFilterBox.Text = "";
            OwnerFilterBox.Text = "";
            _itemsView?.Refresh();
            RefreshCount();
        }

        private async void LoadAsync(TfsConnectionOptions options, HashSet<int> existingIds)
        {
            try
            {
                var found = await TfsImportService.FetchRootWorkItemsAsync(options);
                _process = await TfsImportService.GetProcessAsync(options) ?? "";
                LoadingPanel.Visibility = Visibility.Collapsed;

                if (found.Count == 0)
                {
                    StatusText.Text = AppStrings.Get("Disc_NoneFound");
                    LoadingPanel.Visibility = Visibility.Visible;
                    return;
                }

                foreach (var (id, title, type, owner) in found)
                {
                    var item = new DiscoveryItem { Id = id, Title = title, Type = type, Owner = owner };
                    item.PropertyChanged += (_, _) => RefreshCount();
                    _items.Add(item);
                }

                AddButton.IsEnabled = true;
                RefreshCount();
            }
            catch (Exception ex)
            {
                StatusText.Text = AppStrings.Get("Disc_FetchError", ex.Message);
            }
        }

        private void RefreshCount()
        {
            int sel   = _items.Count(i => i.IsSelected);
            int total = _items.Count;
            CountText.Text = AppStrings.Get("Disc_CountSelected", sel, total);
        }

        private void OnSelectAllClick(object sender, RoutedEventArgs e)
        {
            foreach (var i in _items) i.IsSelected = true;
        }

        private void OnDeselectAllClick(object sender, RoutedEventArgs e)
        {
            foreach (var i in _items) i.IsSelected = false;
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            SelectedProjects = _items
                .Where(i => i.IsSelected)
                .Select(i => new DevOpsProject { Name = i.Title, RootWorkItemId = i.Id, Owner = i.Owner, Process = _process })
                .ToList();

            if (SelectedProjects.Count == 0)
            {
                MessageBox.Show(AppStrings.Get("Disc_SelectAtLeastOne"), AppStrings.Get("Port_DiscoveryTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class DiscoveryItem : INotifyPropertyChanged
    {
        public int    Id    { get; set; }
        public string Title { get; set; } = "";
        public string Type  { get; set; } = "";
        public string Owner { get; set; } = "";

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
