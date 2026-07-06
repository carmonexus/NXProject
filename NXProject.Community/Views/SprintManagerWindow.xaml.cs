using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using NXProject.Models;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Views
{
    public partial class SprintManagerWindow : Window
    {
        private readonly MainViewModel _vm;
        public ObservableCollection<SprintRow> Rows { get; } = new();

        public SprintManagerWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;

            // Popula combos de configuração
            foreach (var m in vm.SprintNumberingModes)
                NumberingCombo.Items.Add(m);
            FirstSprintBox.Text = vm.FirstSprintNumber.ToString();
            SprintDaysBox.Text = vm.SprintDurationDays.ToString();
            NumberingCombo.SelectedItem = vm.SprintNumberingMode;
            RiskSlackBox.Text = vm.CriticalPathRiskSlackDays.ToString("0.#", CultureInfo.CurrentCulture);
            CriticalSlackBox.Text = vm.CriticalPathCriticalSlackDays.ToString("0.#", CultureInfo.CurrentCulture);

            SprintGrid.ItemsSource = Rows;
            RebuildRows();
        }

        // ── Construção da lista ───────────────────────────────────────────────

        private void RebuildRows()
        {
            Rows.Clear();
            foreach (var s in _vm.Project.Sprints.OrderBy(s => s.Number))
                Rows.Add(new SprintRow(s, _vm));
            UpdateCountLabel();
        }

        private void UpdateCountLabel()
        {
            CountLabel.Text = AppStrings.Get("Sprint_Count", Rows.Count);
        }

        // ── Aplicar configurações gerais ─────────────────────────────────────

        private void OnApplySettingsClick(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(FirstSprintBox.Text?.Trim(), out var first) && first >= 0)
                _vm.FirstSprintNumber = first;
            if (int.TryParse(SprintDaysBox.Text?.Trim(), out var days) && days > 0)
                _vm.SprintDurationDays = days;
            if (NumberingCombo.SelectedItem is string mode)
                _vm.SprintNumberingMode = mode;
            if (TryParseDays(RiskSlackBox.Text, out var riskSlackDays))
                _vm.CriticalPathRiskSlackDays = riskSlackDays;
            if (TryParseDays(CriticalSlackBox.Text, out var criticalSlackDays))
                _vm.CriticalPathCriticalSlackDays = criticalSlackDays;

            RiskSlackBox.Text = _vm.CriticalPathRiskSlackDays.ToString("0.#", CultureInfo.CurrentCulture);
            CriticalSlackBox.Text = _vm.CriticalPathCriticalSlackDays.ToString("0.#", CultureInfo.CurrentCulture);

            ShowStatus(AppStrings.Get("Sprint_SettingsApplied"));
        }

        private static bool TryParseDays(string? text, out double value)
        {
            var normalized = (text ?? string.Empty).Trim();
            return double.TryParse(normalized, NumberStyles.Number, CultureInfo.CurrentCulture, out value)
                || double.TryParse(normalized.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        // ── Incluir sprint ────────────────────────────────────────────────────

        private void OnAddSprintClick(object sender, RoutedEventArgs e)
        {
            // Sugere início = fim da última sprint + 1 dia (ou data do projeto)
            var lastEnd = _vm.Project.Sprints
                .OrderBy(s => s.End).LastOrDefault()?.End;
            var suggestStart = lastEnd.HasValue && lastEnd.Value > DateTime.MinValue
                ? lastEnd.Value.AddDays(1)
                : _vm.Project.StartDate;
            var suggestEnd = suggestStart.AddDays(Math.Max(1, _vm.SprintDurationDays) - 1);

            var nextNumber = (_vm.Project.Sprints.Select(s => s.Number).DefaultIfEmpty(0).Max()) + 1;

            var dlg = BuildAddDialog(nextNumber, suggestStart, suggestEnd);
            if (dlg.ShowDialog() != true)
                return;

            var (name, start, end) = dlg.Result!.Value;
            var sprint = new Sprint
            {
                Number = nextNumber,
                DisplayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                Start = start,
                End = end < start ? start.AddDays(Math.Max(1, _vm.SprintDurationDays) - 1) : end,
                Path = null
            };

            _vm.Project.Sprints.Add(sprint);
            _vm.Project.IsDirty = true;
            _vm.RebuildSprintCollections();
            RebuildRows();
            ShowStatus(AppStrings.Get("Sprint_Added", sprint.Name));
        }

        // ── Excluir sprint ────────────────────────────────────────────────────

        private void OnDeleteSprintClick(object sender, RoutedEventArgs e)
        {
            if (SprintGrid.SelectedItem is not SprintRow row)
            {
                MessageBox.Show(AppStrings.Get("Sprint_SelectToDelete"), AppStrings.Get("Sprint_DeleteTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sprint = row.Sprint;
            var count = row.TaskCount;
            var msg = count > 0
                ? AppStrings.Get("Sprint_DeleteConfirmWithTasks", sprint.Name, count)
                : AppStrings.Get("Sprint_DeleteConfirm", sprint.Name);

            if (MessageBox.Show(msg, AppStrings.Get("Sprint_DeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes)
                return;

            // Remove vínculo das tarefas que apontavam para esta sprint
            if (!string.IsNullOrWhiteSpace(sprint.Path))
            {
                foreach (var task in _vm.FlatTasks
                    .Where(t => string.Equals(t.Model.TfsIterationPath, sprint.Path,
                                              StringComparison.OrdinalIgnoreCase)))
                    task.Model.TfsIterationPath = null;
            }

            _vm.Project.Sprints.Remove(sprint);
            _vm.Project.IsDirty = true;
            _vm.RebuildSprintCollections();
            RebuildRows();
            ShowStatus(AppStrings.Get("Sprint_Deleted", sprint.Name));
        }

        // ── Fechar ────────────────────────────────────────────────────────────

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        // ── Status ────────────────────────────────────────────────────────────

        private void ShowStatus(string msg) => StatusText.Text = msg;

        // ── Diálogo de inclusão ───────────────────────────────────────────────

        private AddSprintDialog BuildAddDialog(int nextNumber, DateTime start, DateTime end)
        {
            return new AddSprintDialog(nextNumber, start, end) { Owner = this };
        }

        // ── Linha da grade ────────────────────────────────────────────────────

        public sealed class SprintRow : INotifyPropertyChanged
        {
            private readonly Sprint _sprint;
            private readonly MainViewModel _vm;

            public SprintRow(Sprint sprint, MainViewModel vm)
            {
                _sprint = sprint;
                _vm = vm;
            }

            public Sprint Sprint => _sprint;

            public int Number => _sprint.Number;

            public string? Path => _sprint.Path;
            public bool HasTfsPath => !string.IsNullOrWhiteSpace(_sprint.Path);

            public string DisplayName
            {
                get => _sprint.DisplayName ?? string.Empty;
                set
                {
                    _sprint.DisplayName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                    _vm.RebuildSprintCollections();
                    _vm.Project.IsDirty = true;
                    Notify();
                }
            }

            public DateTime Start
            {
                get => _sprint.Start;
                set
                {
                    if (value == default) return;
                    _sprint.Start = value;
                    _vm.RebuildSprintCollections();
                    _vm.Project.IsDirty = true;
                    Notify();
                    Notify(nameof(StartText));
                    Notify(nameof(DurationDays));
                }
            }

            public DateTime End
            {
                get => _sprint.End;
                set
                {
                    if (value == default) return;
                    _sprint.End = value;
                    _vm.RebuildSprintCollections();
                    _vm.Project.IsDirty = true;
                    Notify();
                    Notify(nameof(EndText));
                    Notify(nameof(DurationDays));
                }
            }

            public string StartText => _sprint.Start == default ? "—" : _sprint.Start.ToString("dd/MM/yy");
            public string EndText   => _sprint.End   == default ? "—" : _sprint.End.ToString("dd/MM/yy");

            public string DurationDays
            {
                get
                {
                    if (_sprint.Start == default || _sprint.End == default) return "—";
                    var d = (int)(_sprint.End.Date - _sprint.Start.Date).TotalDays + 1;
                    return d > 0 ? d.ToString() : "—";
                }
            }

            public int TaskCount => _vm.FlatTasks.Count(t =>
                t.Model.Children.Count == 0 &&
                !string.IsNullOrWhiteSpace(_sprint.Path) &&
                string.Equals(t.Model.TfsIterationPath, _sprint.Path, StringComparison.OrdinalIgnoreCase));

            private void Notify([CallerMemberName] string? p = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }

    // ── Diálogo de nova sprint (janela inline) ────────────────────────────────

    internal sealed class AddSprintDialog : Window
    {
        public (string Name, DateTime Start, DateTime End)? Result { get; private set; }

        private readonly TextBox _nameBox;
        private readonly DatePicker _startPicker;
        private readonly DatePicker _endPicker;

        public AddSprintDialog(int nextNumber, DateTime suggestStart, DateTime suggestEnd)
        {
            Title = AppStrings.Get("Sprint_AddTitle");
            Width = 380;
            Height = 230;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = System.Windows.Media.Brushes.White;

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // nome
            root.RowDefinitions.Add(new RowDefinition { Height = new Thickness(10).Left > 0 ? GridLength.Auto : GridLength.Auto }); // gap
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // datas
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // botões

            // Nome
            var namePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            namePanel.Children.Add(new TextBlock { Text = AppStrings.Get("Sprint_NameLabel"), FontWeight = FontWeights.SemiBold,
                FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            _nameBox = new TextBox { FontSize = 12, Padding = new Thickness(6, 4, 6, 4) };
            namePanel.Children.Add(_nameBox);
            Grid.SetRow(namePanel, 0);
            root.Children.Add(namePanel);

            // Datas
            var datePanel = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            datePanel.ColumnDefinitions.Add(new ColumnDefinition());
            datePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            datePanel.ColumnDefinitions.Add(new ColumnDefinition());

            var startStack = new StackPanel();
            startStack.Children.Add(new TextBlock { Text = AppStrings.Get("Sprint_StartLabel"), FontWeight = FontWeights.SemiBold,
                FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            _startPicker = new DatePicker { SelectedDate = suggestStart, FontSize = 12 };
            startStack.Children.Add(_startPicker);
            Grid.SetColumn(startStack, 0);
            datePanel.Children.Add(startStack);

            var endStack = new StackPanel();
            endStack.Children.Add(new TextBlock { Text = "Fim", FontWeight = FontWeights.SemiBold,
                FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            _endPicker = new DatePicker { SelectedDate = suggestEnd, FontSize = 12 };
            endStack.Children.Add(_endPicker);
            Grid.SetColumn(endStack, 2);
            datePanel.Children.Add(endStack);

            Grid.SetRow(datePanel, 2);
            root.Children.Add(datePanel);

            // Botões
            var btns = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = AppStrings.Get("Sprint_AddButton"), Width = 80, IsDefault = true,
                Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = AppStrings.Get("Sprint_Cancel"), Width = 80, IsCancel = true };
            ok.Click += OnOk;
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            Grid.SetRow(btns, 4);
            root.Children.Add(btns);

            Content = root;
            _nameBox.Focus();
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            var start = _startPicker.SelectedDate ?? DateTime.Today;
            var end   = _endPicker.SelectedDate   ?? start.AddDays(13);
            Result = (_nameBox.Text, start, end);
            DialogResult = true;
            Close();
        }
    }
}
