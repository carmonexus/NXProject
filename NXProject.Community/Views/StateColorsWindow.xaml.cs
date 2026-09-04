using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NXProject.Community.Services;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>Editor de cores por estado do TaskBoard. Cada estado tem um hex (#RRGGBB);
    /// deixar no valor padrão remove a customização daquele estado.</summary>
    public partial class StateColorsWindow : Window
    {
        private readonly Func<string, Color> _defaultColor;
        private readonly List<(string Key, string Label, TextBox Box, Border Preview)> _rows = new();

        /// <summary>Mapa chave(lower) -> hex das cores CUSTOMIZADAS (só as diferentes do padrão).</summary>
        public Dictionary<string, string> Result { get; } = new();

        public StateColorsWindow(IEnumerable<string> states,
            IReadOnlyDictionary<string, string>? current, Func<string, Color> defaultColor,
            IEnumerable<(string Label, string Key)>? extraRows = null)
        {
            InitializeComponent();
            _defaultColor = defaultColor;

            // Entradas: estados do board (chave = nome minúsculo) + extras (chave própria).
            var entries = states.Distinct().Select(s => (Label: s, Key: s.ToLowerInvariant())).ToList();
            if (extraRows != null) entries.AddRange(extraRows.Select(x => (x.Label, Key: x.Key.ToLowerInvariant())));

            foreach (var (label, key) in entries)
            {
                var hex = current != null && current.TryGetValue(key, out var h) ? h : ToHex(defaultColor(key));

                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

                var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(lbl, 0);

                var box = new TextBox { Text = hex, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
                Grid.SetColumn(box, 1);

                var prev = new Border { Width = 24, Height = 22, CornerRadius = new CornerRadius(3),
                    BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = BrushFrom(hex, key),
                    Cursor = System.Windows.Input.Cursors.Hand, ToolTip = AppStrings.Get("Colors_SwatchTip") };
                Grid.SetColumn(prev, 2);

                // Botão explícito da paleta: o quadrado de cor sozinho não parece clicável
                // (a dica só aparece parando o mouse em cima).
                var pick = new Button
                {
                    Content = "🎨", FontSize = 12, Width = 26, Height = 22,
                    Margin = new Thickness(4, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = AppStrings.Get("Colors_SwatchTip")
                };
                Grid.SetColumn(pick, 3);

                box.TextChanged += (_, _) => prev.Background = BrushFrom(box.Text, key);
                prev.MouseLeftButtonUp += (_, _) => ColorPickerHelper.PickInto(box);
                pick.Click += (_, _) => ColorPickerHelper.PickInto(box);

                row.Children.Add(lbl); row.Children.Add(box); row.Children.Add(prev); row.Children.Add(pick);
                RowsHost.Children.Add(row);
                _rows.Add((key, label, box, prev));
            }
        }

        private Brush BrushFrom(string? hex, string key)
            => ColorPickerHelper.BrushFrom(hex, _defaultColor(key));

        private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private void OnResetAll(object sender, RoutedEventArgs e)
        {
            foreach (var (key, _, box, _) in _rows) box.Text = ToHex(_defaultColor(key));
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            foreach (var (key, label, box, _) in _rows)
            {
                var txt = (box.Text ?? "").Trim();
                Color parsed;
                try { parsed = (Color)ColorConverter.ConvertFromString(txt); }
                catch
                {
                    MessageBox.Show(this, AppStrings.Get("Colors_Invalid", label), "NXProject",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // Só guarda quando difere do padrão (mantém o arquivo enxuto).
                if (ToHex(parsed).Equals(ToHex(_defaultColor(key)), StringComparison.OrdinalIgnoreCase))
                    Result.Remove(key);
                else
                    Result[key] = ToHex(parsed);
            }
            DialogResult = true;
            Close();
        }
    }
}
