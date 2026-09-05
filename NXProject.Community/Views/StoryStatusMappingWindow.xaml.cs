// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class StoryStatusMappingWindow : Window
    {
        private readonly ObservableCollection<MappingRow> _rows;

        public StoryStatusMappingWindow(TfsConnectionOptions opts)
        {
            InitializeComponent();

            _rows = new ObservableCollection<MappingRow>(
                opts.StoryStatusMappings.Select(m => new MappingRow
                {
                    TfsState   = m.TfsState,
                    ChartLabel = m.ChartLabel,
                    ColorHex   = m.ColorHex,
                    Order      = m.Order
                }));

            MappingGrid.ItemsSource = _rows;
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            var row = new MappingRow { TfsState = "", ChartLabel = "", Order = _rows.Count * 10 };
            _rows.Add(row);
            MappingGrid.SelectedItem = row;
            MappingGrid.ScrollIntoView(row);
        }

        private void OnRemoveClick(object sender, RoutedEventArgs e)
        {
            if (MappingGrid.SelectedItem is MappingRow row)
                _rows.Remove(row);
        }

        private void OnLoadDefaultsClick(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                AppStrings.Get("SMap_ReplaceConfirm"),
                AppStrings.Get("SMap_Defaults"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _rows.Clear();
            foreach (var m in DefaultMappings())
                _rows.Add(m);
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            MappingGrid.CommitEdit(DataGridEditingUnit.Row, true);

            var opts = TfsConnectionStore.Load();
            opts.StoryStatusMappings = _rows
                .Where(r => !string.IsNullOrWhiteSpace(r.TfsState))
                .Select(r => new StoryStatusMapping
                {
                    TfsState   = r.TfsState.Trim(),
                    ChartLabel = string.IsNullOrWhiteSpace(r.ChartLabel) ? r.TfsState.Trim() : r.ChartLabel.Trim(),
                    ColorHex   = r.ColorHex?.Trim() ?? string.Empty,
                    Order      = r.Order
                })
                .ToList();

            var rememberToken = !string.IsNullOrEmpty(opts.PersonalAccessToken);
            TfsConnectionStore.Save(opts, rememberToken);

            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

        private static Color? ParseHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6 &&
                byte.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
                return Color.FromRgb(r, g, b);
            return null;
        }

        private static MappingRow[] DefaultMappings() =>
        [
            new() { TfsState = "New",                   ChartLabel = "Novo",                   ColorHex = "9E9E9E", Order = 0  },
            new() { TfsState = "Active",                ChartLabel = "Ativo",                  ColorHex = "4A90D9", Order = 10 },
            new() { TfsState = "Em análise",            ChartLabel = "Em análise",             ColorHex = "29B6F6", Order = 20 },
            new() { TfsState = "Corrigindo",            ChartLabel = "Corrigindo",             ColorHex = "FFA726", Order = 30 },
            new() { TfsState = "Corrigindo Causa Raiz", ChartLabel = "Corrigindo Causa Raiz",  ColorHex = "FF7043", Order = 40 },
            new() { TfsState = "Validando",             ChartLabel = "Validando",              ColorHex = "AB47BC", Order = 50 },
            new() { TfsState = "Resolved",              ChartLabel = "Resolvido",              ColorHex = "66BB6A", Order = 60 },
            new() { TfsState = "Closed",                ChartLabel = "Fechado",                ColorHex = "2E7D32", Order = 70 },
            new() { TfsState = "Removed",               ChartLabel = "Removido",               ColorHex = "D32F2F", Order = 80 },
        ];

        public sealed class MappingRow : INotifyPropertyChanged
        {
            private string _tfsState   = "";
            private string _chartLabel = "";
            private string _colorHex   = "";
            private int    _order;

            public string TfsState
            {
                get => _tfsState;
                set { _tfsState = value; OnPropertyChanged(); }
            }
            public string ChartLabel
            {
                get => _chartLabel;
                set { _chartLabel = value; OnPropertyChanged(); }
            }
            public string ColorHex
            {
                get => _colorHex;
                set { _colorHex = value; OnPropertyChanged(); }
            }
            public int Order
            {
                get => _order;
                set { _order = value; OnPropertyChanged(); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
