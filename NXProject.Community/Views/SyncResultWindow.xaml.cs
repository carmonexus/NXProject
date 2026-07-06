using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class SyncResultWindow : Window
    {
        private readonly List<TfsImportService.SyncLogEntry> _allEntries;

        public SyncResultWindow(TfsImportService.SyncReport report)
        {
            InitializeComponent();

            // Conta excluindo a linha [config] de diagnóstico
            var ops = report.Log.Where(e => !e.Message.StartsWith("[config]")).ToList();
            UpdatedNum.Text  = report.Updated.ToString();
            CreatedNum.Text  = report.Created.ToString();
            SkippedNum.Text  = report.Skipped.ToString();
            WarningNum.Text  = ops.Count(e => e.Level == TfsImportService.SyncLogLevel.Warning).ToString();
            ErrorNum.Text    = ops.Count(e => e.Level == TfsImportService.SyncLogLevel.Error).ToString();
            ConflictNum.Text = report.Conflicts.ToString();
            if (report.Conflicts > 0)
                ConflictBanner.Visibility = System.Windows.Visibility.Visible;

            _allEntries = new List<TfsImportService.SyncLogEntry>(report.Log);

            foreach (var name in report.WithoutSprint)
                _allEntries.Add(new TfsImportService.SyncLogEntry(
                    TfsImportService.SyncLogLevel.Warning,
                    AppStrings.Get("SyncRes_NoSprint", name)));

            // Com problemas → ocultar sucesso por padrão para destacar erros/avisos.
            if (_allEntries.Any(e => e.Level != TfsImportService.SyncLogLevel.Success))
                ShowSuccess.IsChecked = false;

            ApplyFilter();
        }

        private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            if (LogBox == null) return; // chamado durante InitializeComponent antes do XAML terminar

            var showSuccess = ShowSuccess.IsChecked == true;
            var showWarning = ShowWarning.IsChecked == true;
            var showError   = ShowError.IsChecked == true;

            LogBox.Text = BuildLogText(_allEntries, showSuccess, showWarning, showError);
            LogBox.ScrollToEnd();
        }

        private static string BuildLogText(
            IEnumerable<TfsImportService.SyncLogEntry> entries,
            bool success, bool warning, bool error)
        {
            var sb = new StringBuilder();
            bool lastWasError = false;

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

                // Linha em branco antes de erros para separar visualmente.
                bool isError = e.Level == TfsImportService.SyncLogLevel.Error;
                if (isError && !lastWasError && sb.Length > 0)
                    sb.AppendLine();

                var prefix = e.Level switch
                {
                    TfsImportService.SyncLogLevel.Success => AppStrings.Get("SyncRes_LogOk"),
                    TfsImportService.SyncLogLevel.Warning => AppStrings.Get("SyncRes_LogWarn"),
                    TfsImportService.SyncLogLevel.Error   => AppStrings.Get("SyncRes_LogErr"),
                    _ => "      "
                };
                sb.AppendLine(prefix + (e.Message ?? string.Empty));
                lastWasError = isError;
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
