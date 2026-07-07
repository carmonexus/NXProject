using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Community.Views
{
    public partial class HierarchyColorPaletteWindow : Window
    {
        private readonly Project _project;
        private bool _loading;

        private static readonly List<string> FactoryDefaults =
        [
            "#D8D8D8", "#E3E3E3", "#EEEEEE", "#F5F5F5", "#FAFAFA"
        ];

        private static readonly string DefaultsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXProject.Community", "hierarchy-color-defaults.json");

        private static List<string> LoadSavedDefaults()
        {
            try
            {
                if (File.Exists(DefaultsFile))
                {
                    var json = File.ReadAllText(DefaultsFile);
                    var saved = JsonSerializer.Deserialize<List<string>>(json);
                    if (saved != null && saved.Count == FactoryDefaults.Count)
                        return saved;
                }
            }
            catch { }
            return new List<string>(FactoryDefaults);
        }

        private TextBox[] ColorBoxes => [Color0Box, Color1Box, Color2Box, Color3Box, Color4Box];
        private Border[] Previews    => [Preview0,  Preview1,  Preview2,  Preview3,  Preview4];

        public HierarchyColorPaletteWindow(Project project)
        {
            InitializeComponent();
            _project = project;
            Load();
        }

        private void Load()
        {
            _loading = true;
            EnabledCheckBox.IsChecked = _project.UseHierarchyColors;
            var colors = _project.HierarchyLevelColors;
            var defaults = LoadSavedDefaults();
            for (int i = 0; i < ColorBoxes.Length; i++)
            {
                string hex = i < colors.Count ? colors[i] : (i < defaults.Count ? defaults[i] : "#FFFFFF");
                ColorBoxes[i].Text = hex;
                UpdatePreview(i, hex);
            }
            _loading = false;
            UpdateEnabled();
        }

        private void UpdatePreview(int idx, string hex)
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6 &&
                byte.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
                Previews[idx].Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            else
                Previews[idx].Background = Brushes.Transparent;
        }

        private void UpdateEnabled()
        {
            LevelsGrid.IsEnabled = EnabledCheckBox.IsChecked == true;
        }

        private void OnEnabledChanged(object sender, RoutedEventArgs e) => UpdateEnabled();

        private void OnColorChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            for (int i = 0; i < ColorBoxes.Length; i++)
                if (sender == ColorBoxes[i])
                    UpdatePreview(i, ColorBoxes[i].Text);
        }

        private void OnSwatchClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border swatch || swatch.Tag is not string idxStr) return;
            if (!int.TryParse(idxStr, out int idx)) return;

            var currentHex = ColorBoxes[idx].Text.Trim().TrimStart('#');
            System.Drawing.Color initial = System.Drawing.Color.LightGray;
            if (currentHex.Length == 6 &&
                byte.TryParse(currentHex[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(currentHex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(currentHex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
                initial = System.Drawing.Color.FromArgb(r, g, b);

            using var dlg = new System.Windows.Forms.ColorDialog
            {
                Color = initial,
                FullOpen = true,
                AnyColor = true
            };

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var chosen = dlg.Color;
                string hex = $"#{chosen.R:X2}{chosen.G:X2}{chosen.B:X2}";
                ColorBoxes[idx].Text = hex;
                UpdatePreview(idx, hex);
            }
        }

        private void OnSaveAsDefault(object sender, RoutedEventArgs e)
        {
            var colors = new List<string>();
            for (int i = 0; i < ColorBoxes.Length; i++)
            {
                var hex = ColorBoxes[i].Text.Trim();
                if (!hex.StartsWith('#')) hex = "#" + hex;
                colors.Add(hex);
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DefaultsFile)!);
                File.WriteAllText(DefaultsFile, JsonSerializer.Serialize(colors, new JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Show(AppStrings.Get("Pal_Saved"), AppStrings.Get("Pal_SaveTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(AppStrings.Get("Pal_SaveError", ex.Message), AppStrings.Get("Pal_ErrorTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnRestoreDefaults(object sender, RoutedEventArgs e)
        {
            _loading = true;
            var defaults = LoadSavedDefaults();
            for (int i = 0; i < ColorBoxes.Length; i++)
            {
                ColorBoxes[i].Text = defaults[i];
                UpdatePreview(i, defaults[i]);
            }
            _loading = false;
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            _project.UseHierarchyColors = EnabledCheckBox.IsChecked == true;
            var newColors = new List<string>();
            for (int i = 0; i < ColorBoxes.Length; i++)
            {
                var hex = ColorBoxes[i].Text.Trim();
                if (!hex.StartsWith('#')) hex = "#" + hex;
                newColors.Add(hex);
            }
            _project.HierarchyLevelColors = newColors;
            _project.IsDirty = true;
            DialogResult = true;
        }
    }
}
