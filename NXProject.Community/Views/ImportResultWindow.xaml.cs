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

            if (_allEntries.Any(e => e.Level != TfsImportService.SyncLogLevel.Success))
                ShowSuccess.IsChecked = false;

            ApplyFilter();
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
