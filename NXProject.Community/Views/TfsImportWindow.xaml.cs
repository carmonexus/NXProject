using System;
using System.Collections.Generic;
using System.Diagnostics;
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
                // Pré-seleciona na combo o projeto que estava marcado na grid do portfólio.
                LoadProjectList(newPath, dlg.SelectedProject?.RootWorkItemId ?? 0);

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
                // Progresso por etapa (Progress<T> despacha para a thread da UI).
                var progress = new Progress<string>(step => ImportStepText.Text = step);
                var importResult = await TfsImportService.ImportAsync(options, progress);
                var project = importResult.Project;

                // Grava a origem (organização + Team Project) para a sincronização
                // usar o projeto do cronograma aberto, não a config global.
                project.DevOpsOrganizationUrl = options.OrganizationUrl?.Trim();
                project.DevOpsTeamProject = options.TeamProject?.Trim();

                if (project.Tasks.Count == 0)
                {
                    var warn = AppStrings.Get("Imp_EmptyResult");
                    ShowStatus(warn);
                    MessageBox.Show(this, warn, AppStrings.Get("Imp_ErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

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

                if (importResult.Report.Log.Count > 0)
                {
                    var reportWin = new ImportResultWindow(importResult.Report) { Owner = System.Windows.Application.Current.MainWindow };
                    reportWin.Show();
                }
            }
            catch (Exception ex)
            {
                var (msg, isAuth) = BuildImportErrorMessage(ex);
                ShowStatus(msg);

                if (isAuth)
                {
                    var res = MessageBox.Show(this,
                        msg + "\n\n" + AppStrings.Get("Imp_OpenTokensPrompt"),
                        AppStrings.Get("Imp_ErrorTitle"),
                        MessageBoxButton.YesNo, MessageBoxImage.Error);
                    if (res == MessageBoxResult.Yes)
                        OpenTokensPage();
                }
                else
                {
                    MessageBox.Show(this, msg, AppStrings.Get("Imp_ErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                SetImporting(false);
            }
        }

        // Abre a página de Personal Access Tokens da organização no navegador.
        private void OpenTokensPage()
        {
            try
            {
                var org = _savedOptions.OrganizationUrl?.Trim().TrimEnd('/');
                if (string.IsNullOrWhiteSpace(org)) return;
                var url = org + "/_usersSettings/tokens";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                if (TfsErrorDialog.IsAuthError(ex)) { TfsErrorDialog.Show(this, AppStrings.Get("Tfs_ActionImport"), ex); return; }
                MessageBox.Show(this, ex.Message, AppStrings.Get("Imp_ErrorTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Monta uma mensagem de erro legível: encadeia InnerExceptions (redes/TLS
        // costumam trazer a causa real só no Inner) e destaca falha de PAT/autenticação.
        // Retorna (mensagem, ehFalhaDeAutenticacao).
        private static (string Message, bool IsAuth) BuildImportErrorMessage(Exception ex)
        {
            var parts = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message?.Trim();
                if (!string.IsNullOrEmpty(m) && !parts.Contains(m))
                    parts.Add(m);
            }
            if (parts.Count == 0)
                parts.Add(ex.GetType().Name);

            var full = string.Join("\n", parts);
            var lower = full.ToLowerInvariant();

            bool looksAuth = lower.Contains("autentica") || lower.Contains("401") ||
                             lower.Contains("403") || lower.Contains("unauthorized") ||
                             lower.Contains("forbidden") || lower.Contains("html") ||
                             lower.Contains("login") || lower.Contains("pat") ||
                             lower.Contains("sign in") || lower.Contains("tf400813");

            if (looksAuth)
                return (AppStrings.Get("Imp_AuthError") + "\n\n" + full, true);

            return (full, false);
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
            ImportProgressPanel.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
            if (importing) ImportStepText.Text = "";
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
