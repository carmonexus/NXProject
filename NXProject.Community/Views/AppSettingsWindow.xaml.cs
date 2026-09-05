// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NXProject.Community.Services;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class AppSettingsWindow : Window
    {
        private const int MaxLogoBytes = 5 * 1024 * 1024;
        private const int NormalizedWidth  = 300;
        private const int NormalizedHeight = 80;

        private string _logoBase64 = string.Empty;
        private string _selectedLanguage;

        public AppSettingsWindow()
        {
            InitializeComponent();
            _selectedLanguage = LanguageService.CurrentLanguage;
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            var opts = TfsConnectionStore.Load();
            CompanyNameBox.Text    = opts.CompanyName ?? string.Empty;
            _logoBase64            = opts.CompanyLogoBase64 ?? string.Empty;
            DebugLogCheck.IsChecked = opts.DebugLogEnabled;
            ActivityListBox.Text   = string.Join("\n", opts.TaskActivityList);

            if (!string.IsNullOrEmpty(_logoBase64))
                ShowLogoPreview(_logoBase64);

            foreach (ComboBoxItem item in LanguageCombo.Items)
            {
                if (item.Tag?.ToString() == _selectedLanguage)
                {
                    LanguageCombo.SelectedItem = item;
                    break;
                }
            }
            if (LanguageCombo.SelectedItem == null)
                LanguageCombo.SelectedIndex = 0;
        }

        // ── Logo ──────────────────────────────────────────────────────────

        private void OnBrowseLogoClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = Str("Settings_BrowseTitle"),
                Filter = Str("Settings_BrowseFilter")
            };
            if (dlg.ShowDialog(this) != true) return;

            if (new FileInfo(dlg.FileName).Length > MaxLogoBytes)
            {
                MessageBox.Show(Str("Settings_LogoTooBig"),
                    Str("Settings_LogoTooBigTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var normalized = NormalizeImage(dlg.FileName);
                _logoBase64 = Convert.ToBase64String(normalized);
                ShowLogoPreview(_logoBase64);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Str("Settings_LogoErrorMsg")}\n{ex.Message}",
                    Str("Settings_LogoErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnClearLogoClick(object sender, RoutedEventArgs e)
        {
            _logoBase64 = string.Empty;
            LogoPreviewImage.Source     = null;
            LogoPreviewImage.Visibility = Visibility.Collapsed;
            LogoPlaceholder.Visibility  = Visibility.Visible;
            ClearLogoButton.IsEnabled   = false;
        }

        private void ShowLogoPreview(string base64)
        {
            try
            {
                var bytes  = Convert.FromBase64String(base64);
                var bitmap = LoadBitmapFromBytes(bytes);
                LogoPreviewImage.Source     = bitmap;
                LogoPreviewImage.Visibility = Visibility.Visible;
                LogoPlaceholder.Visibility  = Visibility.Collapsed;
                ClearLogoButton.IsEnabled   = true;
            }
            catch { /* preview opcional */ }
        }

        private static byte[] NormalizeImage(string filePath)
        {
            var src = new BitmapImage();
            src.BeginInit();
            src.UriSource   = new Uri(filePath, UriKind.Absolute);
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.EndInit();
            src.Freeze();

            double scaleX = NormalizedWidth  / (double)src.PixelWidth;
            double scaleY = NormalizedHeight / (double)src.PixelHeight;
            double scale  = Math.Min(scaleX, scaleY);

            int dstW = Math.Max(1, (int)(src.PixelWidth  * scale));
            int dstH = Math.Max(1, (int)(src.PixelHeight * scale));

            var tb = new System.Windows.Media.Imaging.TransformedBitmap(
                src, new System.Windows.Media.ScaleTransform(scale, scale));

            var rtb = new RenderTargetBitmap(dstW, dstH, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);

            var dv = new System.Windows.Media.DrawingVisual();
            using (var ctx = dv.RenderOpen())
                ctx.DrawImage(tb, new Rect(0, 0, dstW, dstH));
            rtb.Render(dv);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static BitmapImage LoadBitmapFromBytes(byte[] bytes)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        // ── Idioma ────────────────────────────────────────────────────────

        private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LanguageCombo.SelectedItem is ComboBoxItem item)
                _selectedLanguage = item.Tag?.ToString() ?? "pt-BR";
        }

        // ── Salvar / Cancelar ─────────────────────────────────────────────

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            LanguageService.Apply(_selectedLanguage);

            var opts = TfsConnectionStore.Load();
            opts.Language          = _selectedLanguage;
            opts.CompanyName       = CompanyNameBox.Text.Trim();
            opts.CompanyLogoBase64 = _logoBase64;
            opts.DebugLogEnabled   = DebugLogCheck.IsChecked == true;
            opts.TaskActivityList  = ActivityListBox.Text
                .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            SprintAlertLog.Enabled = opts.DebugLogEnabled;

            var rememberToken = !string.IsNullOrEmpty(opts.PersonalAccessToken);
            TfsConnectionStore.Save(opts, rememberToken);

            DialogResult = true;
        }

        private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
        {
            var folder = Path.GetDirectoryName(SprintAlertLog.LogFilePath)!;
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start("explorer.exe", folder);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // ── Helper ────────────────────────────────────────────────────────

        private static string Str(string key)
            => Application.Current.TryFindResource(key) as string ?? key;
    }
}
