using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Views
{
    public partial class PredecessorPickerWindow : Window
    {
        private readonly ICollectionView _view;
        private readonly ObservableCollection<string> _externalIds;

        public ObservableCollection<PredecessorRow> CandidateRows { get; } = new();

        public string SelectedPredecessorsText { get; private set; } = string.Empty;

        public PredecessorPickerWindow(TaskViewModel targetTask, IEnumerable<TaskViewModel> candidates)
        {
            InitializeComponent();
            DataContext = this;

            TargetTaskText.Text = AppStrings.Get("Pred_TargetTask", targetTask.DisplayId, targetTask.Name);

            var selectedIds = targetTask.PredecessorsText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var task in candidates)
            {
                var parentName = task.ParentViewModel?.Name ?? "";
                var startStr = task.Start.ToString("dd/MM/yy");
                var finishStr = task.Finish.ToString("dd/MM/yy");
                var row = new PredecessorRow(task.DisplayId, task.Name, parentName, startStr, finishStr)
                {
                    IsSelected = selectedIds.Contains(task.DisplayId)
                };
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(PredecessorRow.IsSelected))
                        RefreshSelectedSummary();
                };
                CandidateRows.Add(row);
            }

            var candidateIds = CandidateRows
                .Select(r => r.DisplayId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _externalIds = new ObservableCollection<string>(
                selectedIds
                    .Where(id => !candidateIds.Contains(id))
                    .OrderBy(id => id)
                    .Select(id => AppStrings.Get("Pred_ExternalLabel", id)));

            ExternalPredsList.ItemsSource = _externalIds;
            ExternalPredsList.Visibility = _externalIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            _view = CollectionViewSource.GetDefaultView(CandidateRows);
            _view.Filter = FilterRow;
            RefreshSelectedSummary();
        }

        private bool FilterRow(object item)
        {
            if (item is not PredecessorRow row)
                return false;

            var query = SearchBox.Text?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(query)
                   || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                   || row.ParentName.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _view.Refresh();
            RefreshSelectedSummary();
        }

        private void OnClearSearchClick(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            SearchBox.Focus();
        }

        private void OnRemoveExternalClick(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string entry)
            {
                _externalIds.Remove(entry);
                ExternalPredsList.Visibility = _externalIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                RefreshSelectedSummary();
            }
        }

        private void RefreshSelectedSummary()
        {
            var selected = CandidateRows
                .Where(r => r.IsSelected)
                .OrderBy(r => r.Name)
                .Select(r => AppStrings.Get("Pred_RowLabel", r.DisplayId, r.Name))
                .ToList();

            var allLines = selected.ToList();

            SelectedSummaryText.Text = allLines.Count == 0 && _externalIds.Count == 0
                ? AppStrings.Get("Pred_NoneMarked")
                : allLines.Count == 0
                    ? string.Empty
                    : string.Join(Environment.NewLine, allLines);

            int total = selected.Count + _externalIds.Count;
            CountText.Text = AppStrings.Get("Pred_CountMarked", total);
        }

        private void OnClearPredecessorsClick(object sender, RoutedEventArgs e)
        {
            SelectedPredecessorsText = string.Empty;
            DialogResult = true;
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            // IDs externos restantes (os que o usuário não removeu)
            var externalRawIds = _externalIds
                .Select(entry => entry.Split(' ')[0])   // "1234 - fora da lista" → "1234"
                .Where(id => !string.IsNullOrWhiteSpace(id));

            var ids = externalRawIds
                .Concat(CandidateRows.Where(r => r.IsSelected).Select(r => r.DisplayId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            SelectedPredecessorsText = string.Join(",", ids);
            DialogResult = true;
        }

        public sealed class PredecessorRow : INotifyPropertyChanged
        {
            private bool _isSelected;

            public PredecessorRow(string displayId, string name, string parentName = "", string startDate = "", string finishDate = "")
            {
                DisplayId = displayId;
                Name = name;
                ParentName = parentName;
                StartDate = startDate;
                FinishDate = finishDate;
            }

            public string DisplayId { get; }
            public string Name { get; }
            public string ParentName { get; }
            public string StartDate { get; }
            public string FinishDate { get; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value)
                        return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
