using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class TfsImportWindow : Window
    {
        private readonly string _storageKey;
        private bool _isImporting;
        private string _devOpsProjectListPath = string.Empty;
        private List<DevOpsProject> _devOpsProjects = new();
        private TfsConnectionOptions _savedOptions = new();

        /// <summary>Projeto importado quando o diálogo retorna true.</summary>
        public Project? ImportedProject { get; private set; }

        public TfsImportWindow(string storageKey = "NXProject.Community")
        {
            InitializeComponent();
            _storageKey = string.IsNullOrWhiteSpace(storageKey) ? "NXProject.Community" : storageKey.Trim();

            _savedOptions = TfsConnectionStore.Load(_storageKey);

            if (!string.IsNullOrWhiteSpace(_savedOptions.DevOpsProjectListPath))
                LoadProjectList(_savedOptions.DevOpsProjectListPath, _savedOptions.RootWorkItemId);
            else if (_savedOptions.RootWorkItemId > 0)
                RootIdBox.Text = _savedOptions.RootWorkItemId.ToString(CultureInfo.InvariantCulture);
        }

        private void LoadProjectList(string path, int selectId = 0)
        {
            _devOpsProjectListPath = path;
            _devOpsProjects = DevOpsProjectListService.Load(path);

            DevOpsProjectCombo.ItemsSource = null;
            DevOpsProjectCombo.ItemsSource = _devOpsProjects;
            ListPathLabel.Text = path;

            if (selectId > 0)
            {
                foreach (var p in _devOpsProjects)
                {
                    if (p.RootWorkItemId == selectId)
                    {
                        DevOpsProjectCombo.SelectedItem = p;
                        break;
                    }
                }
            }

            if (DevOpsProjectCombo.SelectedItem == null && selectId > 0)
                RootIdBox.Text = selectId.ToString(CultureInfo.InvariantCulture);
        }

        private void OnProjectComboChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DevOpsProjectCombo.SelectedItem is DevOpsProject selected)
                RootIdBox.Text = selected.RootWorkItemId.ToString(CultureInfo.InvariantCulture);
        }

        private void OnManageListClick(object sender, RoutedEventArgs e)
        {
            var dlg = new DevOpsProjectListWindow(_devOpsProjectListPath) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                var newPath = dlg.ResultFilePath ?? string.Empty;
                LoadProjectList(newPath);

                var saved = TfsConnectionStore.Load(_storageKey);
                saved.DevOpsProjectListPath = newPath;
                TfsConnectionStore.Save(saved, !string.IsNullOrEmpty(saved.PersonalAccessToken), _storageKey);
            }
        }

        private void OnOpenConfigClick(object sender, RoutedEventArgs e)
        {
            var dlg = new TfsDevOpsConfigWindow(_storageKey) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _savedOptions = TfsConnectionStore.Load(_storageKey);
                UpdateConfigHint();
            }
        }

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            if (_isImporting)
                return;

            HideStatus();

            _savedOptions = TfsConnectionStore.Load(_storageKey);
            if (string.IsNullOrWhiteSpace(_savedOptions.OrganizationUrl) || string.IsNullOrWhiteSpace(_savedOptions.PersonalAccessToken))
            {
                ShowStatus(AppStrings.Get("Imp_NoConnectionStatus"));
                return;
            }

            if (!int.TryParse(RootIdBox.Text?.Trim(), out var rootId) || rootId <= 0)
            {
                ShowStatus(AppStrings.Get("Imp_SelectOrEnterId"));
                return;
            }

            var options = _savedOptions;
            options.RootWorkItemId = rootId;
            options.DevOpsProjectListPath = _devOpsProjectListPath;

            SetImporting(true);
            try
            {
                var importResult = await TfsImportService.ImportAsync(options);
                var project = importResult.Project;

                if (DevOpsProjectCombo.SelectedItem is DevOpsProject selected)
                {
                    project.DevOpsProjectName = selected.Name;
                    project.DevOpsRootWorkItemId = selected.RootWorkItemId;
                }
                else
                {
                    project.DevOpsRootWorkItemId = rootId;
                }

                TfsConnectionStore.Save(options, !string.IsNullOrEmpty(options.PersonalAccessToken), _storageKey);

                ResourceKindConfigService.ApplyTo(project.Resources);
                ImportedProject = project;
                DialogResult = true;
                Close();

                if (importResult.Report.HasIssues)
                {
                    var reportWin = new ImportResultWindow(importResult.Report) { Owner = System.Windows.Application.Current.MainWindow };
                    reportWin.Show();
                }
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message);
            }
            finally
            {
                SetImporting(false);
            }
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            UpdateConfigHint();
        }

        private void UpdateConfigHint()
        {
            bool configured = !string.IsNullOrWhiteSpace(_savedOptions.OrganizationUrl)
                && !string.IsNullOrWhiteSpace(_savedOptions.PersonalAccessToken);
            NotConfiguredHint.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnOpenCalendarClick(object sender, RoutedEventArgs e)
        {
            var control = new NXProject.Controls.CalendarSettingsControl("NXProject.Community");
            var window = new Window
            {
                Title = AppStrings.Get("Imp_CalendarWindowTitle"),
                Owner = this,
                Width = 720,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = control
            };
            control.Saved += (_, _) => { window.Close(); };
            window.ShowDialog();
        }

        private void SetImporting(bool importing)
        {
            _isImporting = importing;
            ImportButton.IsEnabled = !importing;
            ImportButton.Content = importing ? AppStrings.Get("Imp_Importing") : AppStrings.Get("Imp_Import");
            Mouse.OverrideCursor = importing ? Cursors.Wait : null;
        }

        private void ShowStatus(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
        }

        private void HideStatus()
        {
            StatusText.Visibility = Visibility.Collapsed;
        }
    }
}
