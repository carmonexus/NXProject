using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>
    /// Seletor de pessoas do DevOps no mesmo padrao do Discovery de Projetos:
    /// abre listando pessoas, filtra a lista em memoria, e permite "Buscar no
    /// DevOps" (server-side) para localizar quem nao veio na lista inicial —
    /// necessario porque a organizacao pode ter centenas de milhares de usuarios.
    /// </summary>
    public partial class DiscoveryPeoplePickerWindow : Window
    {
        private readonly Func<string, Task<List<TfsImportService.DevOpsUserInfo>>> _search;
        private readonly Func<int, Task<List<TfsImportService.DevOpsUserInfo>>> _listAll;
        private readonly Action<Exception>? _onSearchError;
        private readonly Dictionary<string, PickItem> _all = new(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<PickItem> _view = new();

        /// <summary>Pessoas marcadas ao confirmar.</summary>
        public List<TfsImportService.DevOpsUserInfo> SelectedUsers { get; private set; } = new();

        public DiscoveryPeoplePickerWindow(
            Func<string, Task<List<TfsImportService.DevOpsUserInfo>>> search,
            Func<int, Task<List<TfsImportService.DevOpsUserInfo>>> listAll,
            Action<Exception>? onSearchError = null)
        {
            InitializeComponent();
            _search = search;
            _listAll = listAll;
            _onSearchError = onSearchError;
            PeopleGrid.ItemsSource = _view;
            Loaded += async (_, _) =>
            {
                FilterBox.Focus();
                await ReloadAsync(); // lista inicial ate o limite
            };
        }

        private int ParseLimit()
            => int.TryParse(LimitBox.Text?.Trim(), out var n) && n > 0 ? Math.Min(n, 200000) : 1000;

        // ── Carga (servidor) ─────────────────────────────────────────────

        /// <summary>Carrega a lista da organizacao ate o limite (paginado).</summary>
        private async Task ReloadAsync()
        {
            var limit = ParseLimit();
            SetBusy(true);
            try
            {
                var results = await _listAll(limit);
                MergeResults(results);
                ApplyFilter();
            }
            catch (Exception ex)
            {
                StatusText.Text = AppStrings.Get("PeoplePick_SearchError", ex.Message);
                LoadingPanel.Visibility = Visibility.Visible;
                _onSearchError?.Invoke(ex);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>Busca por termo no servidor e junta ao conjunto atual.</summary>
        private async Task LoadAsync(string term)
        {
            SetBusy(true);
            try
            {
                var results = await _search(term);
                MergeResults(results);
                ApplyFilter();
            }
            catch (Exception ex)
            {
                StatusText.Text = AppStrings.Get("PeoplePick_SearchError", ex.Message);
                LoadingPanel.Visibility = Visibility.Visible;
                _onSearchError?.Invoke(ex);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void MergeResults(List<TfsImportService.DevOpsUserInfo> results)
        {
            foreach (var u in results)
            {
                var key = !string.IsNullOrWhiteSpace(u.Email) ? u.Email : u.Name;
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (!_all.TryGetValue(key, out var item))
                {
                    item = new PickItem { Name = u.Name, Email = u.Email };
                    item.PropertyChanged += (_, _) => UpdateCount();
                    _all[key] = item;
                }
            }
        }

        private async void OnReloadClick(object sender, RoutedEventArgs e) => await ReloadAsync();

        private void SetBusy(bool busy)
        {
            ServerSearchButton.IsEnabled = !busy;
            if (busy)
            {
                StatusText.Text = AppStrings.Get("PeoplePick_Loading");
                LoadingPanel.Visibility = Visibility.Visible;
            }
            else
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }

        // ── Filtro em memoria ────────────────────────────────────────────

        private void ApplyFilter()
        {
            var q = FilterBox.Text?.Trim() ?? string.Empty;
            IEnumerable<PickItem> src = _all.Values;
            if (!string.IsNullOrEmpty(q))
                src = src.Where(p =>
                    (!string.IsNullOrEmpty(p.Name) && p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(p.Email) && p.Email.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));

            _view.Clear();
            foreach (var p in src.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                _view.Add(p);

            UpdateCount();
        }

        private void OnFilterChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

        private async void OnFilterKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                await ServerSearchAsync();
        }

        private async void OnServerSearchClick(object sender, RoutedEventArgs e) => await ServerSearchAsync();

        private async Task ServerSearchAsync()
        {
            var term = FilterBox.Text?.Trim() ?? string.Empty;
            if (term.Length < 2)
            {
                StatusText.Text = AppStrings.Get("PeoplePick_MinChars");
                LoadingPanel.Visibility = Visibility.Visible;
                return;
            }
            await LoadAsync(term);
        }

        // ── Selecao / confirmacao ────────────────────────────────────────

        private void OnSelectVisibleClick(object sender, RoutedEventArgs e)
        {
            foreach (var i in _view) i.IsChecked = true;
            UpdateCount();
        }

        private void OnClearAllClick(object sender, RoutedEventArgs e)
        {
            foreach (var i in _all.Values) i.IsChecked = false;
            UpdateCount();
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            SelectedUsers = _all.Values
                .Where(i => i.IsChecked)
                .Select(i => new TfsImportService.DevOpsUserInfo(i.Name, i.Email))
                .ToList();
            DialogResult = true;
        }

        private void UpdateCount()
        {
            var checkedCount = _all.Values.Count(i => i.IsChecked);
            CountText.Text = AppStrings.Get("PeoplePick_CountFmt", _view.Count, _all.Count, checkedCount);
        }

        public sealed class PickItem : INotifyPropertyChanged
        {
            private bool _isChecked;
            public string Name { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public bool IsChecked
            {
                get => _isChecked;
                set { _isChecked = value; OnPropertyChanged(); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? n = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }
}
