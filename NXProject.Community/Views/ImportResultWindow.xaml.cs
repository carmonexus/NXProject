// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class ImportResultWindow : Window
    {
        private readonly List<TfsImportService.SyncLogEntry> _allEntries;

        public ImportResultWindow(TfsImportService.ImportReport report)
        {
            InitializeComponent();

            StateFixedNum.Text = report.StoriesStateFixed.ToString();
            ExtPredNum.Text    = report.ExternalPredecessors.ToString();
            WarningNum.Text    = report.Log.Count(e => e.Level != TfsImportService.SyncLogLevel.Success).ToString();

            _allEntries = new List<TfsImportService.SyncLogEntry>(report.Log);

            bool hasIssues = _allEntries.Any(e => e.Level != TfsImportService.SyncLogLevel.Success);
            if (hasIssues) ShowSuccess.IsChecked = false;

            // Detalhes começam recolhidos (só os totais). Se houver avisos/erros, já abre.
            DetailsToggle.IsChecked = hasIssues;
            ApplyDetailsState();

            ApplyFilter();
        }

        // Abre a janela à direita do cronograma (janela dona), sem cobri-lo.
        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            var owner = Owner;
            if (owner != null && owner.WindowState != WindowState.Maximized)
            {
                Left = owner.Left + owner.ActualWidth - Width - 24;
                Top  = owner.Top + 90;
            }
            else
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - Width - 24;
                Top  = wa.Top + 90;
            }
            // Mantém dentro da área de trabalho.
            var area = SystemParameters.WorkArea;
            if (Left < area.Left) Left = area.Left + 8;
            if (Left + Width > area.Right) Left = area.Right - Width - 8;
            if (Top < area.Top) Top = area.Top + 8;
        }

        // Mostra/oculta o bloco de detalhes (filtros + log). Colapsado, a janela fica compacta.
        private void OnDetailsToggle(object sender, RoutedEventArgs e) => ApplyDetailsState();

        private void ApplyDetailsState()
        {
            if (DetailPanel == null) return;
            bool show = DetailsToggle.IsChecked == true;
            DetailPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            DetailsToggle.Content = AppStrings.Get(show ? "ImpRes_HideDetails" : "ImpRes_ShowDetails");
            Height = show ? 500 : 235;
        }

        private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            if (LogBox == null) return;
            LogBox.Text = BuildLogText(_allEntries,
                ShowSuccess.IsChecked == true,
                ShowWarning.IsChecked == true,
                ShowError.IsChecked == true);
            LogBox.ScrollToEnd();
        }

        private static string BuildLogText(
            IEnumerable<TfsImportService.SyncLogEntry> entries,
            bool success, bool warning, bool error)
        {
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                bool include = e.Level switch
                {
                    TfsImportService.SyncLogLevel.Success => success,
                    TfsImportService.SyncLogLevel.Warning => warning,
                    TfsImportService.SyncLogLevel.Error   => error,
                    _ => true
                };
                if (!include) continue;
                var prefix = e.Level switch
                {
                    TfsImportService.SyncLogLevel.Success => AppStrings.Get("ImpRes_LogInfo"),
                    TfsImportService.SyncLogLevel.Warning => AppStrings.Get("ImpRes_LogWarn"),
                    TfsImportService.SyncLogLevel.Error   => AppStrings.Get("ImpRes_LogErr"),
                    _ => "       "
                };
                sb.AppendLine(prefix + (e.Message ?? string.Empty));
            }
            return sb.ToString().TrimEnd();
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(LogBox.Text))
                Clipboard.SetText(LogBox.Text);
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
