using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class TfsDevOpsConfigWindow : Window
    {
        private readonly string _storageKey;
        private string _devOpsProjectListPath = string.Empty;
        private readonly System.Collections.ObjectModel.ObservableCollection<ExtraWorkItemField> _extraFields = new();
        private readonly System.Collections.ObjectModel.ObservableCollection<ClassificationMapping> _classificationMappings = new();

        public sealed class ClassificationMapping
        {
            public static readonly string[] AllTypes       = ["Epic", "Feature", "Story", "Task", "Todos"];
            public static readonly string[] AllFieldTypes  = ["Picklist", "Integer", "Text", "Date"];
            public string[] AvailableTypes      => AllTypes;
            public string[] AvailableFieldTypes => AllFieldTypes;
            public string DevOpsType { get; set; } = "Feature";
            public string FieldRef   { get; set; } = string.Empty;
            public string FieldType  { get; set; } = "Picklist";
            /// <summary>Valores separados por vírgula; viram o combo ao editar classificação deste tipo.</summary>
            public string Values     { get; set; } = string.Empty;
        }

        // Projeto (cronograma) aberto — habilita o campo do path da planilha de Task Plan.
        private readonly string? _currentProjectName;

        public TfsDevOpsConfigWindow(string storageKey = "NXProject.Community", string? currentProjectName = null)
        {
            InitializeComponent();
            _storageKey = string.IsNullOrWhiteSpace(storageKey) ? "NXProject.Community" : storageKey.Trim();
            _currentProjectName = string.IsNullOrWhiteSpace(currentProjectName) ? null : currentProjectName.Trim();

            // Planilha de Task Plan associada ao projeto aberto (configuração local).
            if (_currentProjectName != null)
            {
                var tp = Community.Services.TaskPlanSettingsStore.Load();
                TaskPlanFileBox.Text = tp.GetProjectFile(_currentProjectName) ?? "";
            }
            else
            {
                TaskPlanFileBox.IsEnabled = false;
                TaskPlanFileBrowse.IsEnabled = false;
            }

            var saved = TfsConnectionStore.Load(_storageKey);
            OrgUrlBox.Text = saved.OrganizationUrl;
            ProjectBox.Text = saved.TeamProject;
            EffortFieldBox.Text = saved.EffortFieldName;
            StartFieldBox.Text = saved.StartFieldName;
            FinishFieldBox.Text = saved.FinishFieldName;
            PercAlocFieldBox.Text = saved.PercAlocFieldName;
            PercConclusaoFieldBox.Text = saved.PercConclusaoFieldName;
            EpicTypeFieldEnabledBox.IsChecked = saved.EpicTypeFieldEnabled;
            EpicTypeFieldBox.Text = string.IsNullOrWhiteSpace(saved.EpicTypeFieldName) ? "EPIC_TYPE" : saved.EpicTypeFieldName;
            EpicTypeFieldBox.IsEnabled = saved.EpicTypeFieldEnabled;
            ApprovedFieldEnabledBox.IsChecked = saved.ApprovedFieldEnabled;
            ApprovedFieldBox.Text = string.IsNullOrWhiteSpace(saved.ApprovedFieldName) ? "Approved" : saved.ApprovedFieldName;
            ApprovedFieldBox.IsEnabled = saved.ApprovedFieldEnabled;
            TaskPriorityRangeEnabledBox.IsChecked = saved.TaskPriorityRangeEnabled;
            TaskPriorityMinBox.Text = saved.TaskPriorityMin.ToString(CultureInfo.InvariantCulture);
            TaskPriorityMaxBox.Text = saved.TaskPriorityMax.ToString(CultureInfo.InvariantCulture);
            TaskPriorityMinBox.IsEnabled = saved.TaskPriorityRangeEnabled;
            TaskPriorityMaxBox.IsEnabled = saved.TaskPriorityRangeEnabled;
            SyncVersionFieldBox.Text = saved.SyncVersionFieldName;
            SyncNameFieldBox.Text = saved.SyncNameFieldName;
            FixedStartTagBox.Text = saved.FixedStartTagName;
            SyncPredecessorLinksCheck.IsChecked = saved.SyncPredecessorLinks;
            EnforceStoryCompletionWithTasksCheck.IsChecked = saved.EnforceStoryCompletionWithTasks;
            EnableOrgDiscoveryCheck.IsChecked = saved.EnableOrgPeopleDiscovery;
            FutureSprintDaysBox.Text = saved.FutureSprintDays.ToString(CultureInfo.InvariantCulture);

            foreach (var f in saved.ExtraCreateFields)
                _extraFields.Add(new ExtraWorkItemField { Ref = f.Ref, Value = f.Value });
            ExtraFieldsList.ItemsSource = _extraFields;

            // Carrega mapeamentos de classificação por tipo
            foreach (var kv in saved.TypeFieldMappings)
            {
                foreach (var fd in kv.Value.CustomDevopsFields)
                    _classificationMappings.Add(new ClassificationMapping
                    {
                        DevOpsType = kv.Key,
                        FieldRef   = fd.Field,
                        FieldType  = fd.FieldType,
                        Values     = fd.Values ?? string.Empty,
                    });
            }
            // Padrão: Feature → Custom.Type (Picklist) com valores de exemplo
            if (_classificationMappings.Count == 0)
                _classificationMappings.Add(new ClassificationMapping
                {
                    DevOpsType = "Feature", FieldRef = "Custom.Type", FieldType = "Picklist",
                    Values = "Architecture,Burocracy,Docs,Feature,Hotfix,Refactor",
                });
            ClassificationMappingsList.ItemsSource = _classificationMappings;

            if (!string.IsNullOrEmpty(saved.PersonalAccessToken))
            {
                PatBox.Password = saved.PersonalAccessToken;
                RememberTokenCheck.IsChecked = true;
            }

            if (!string.IsNullOrWhiteSpace(saved.DevOpsProjectListPath))
            {
                _devOpsProjectListPath = saved.DevOpsProjectListPath;
                ListPathLabel.Text = _devOpsProjectListPath;
            }
        }

        // Abre a página de Personal Access Tokens da organização digitada acima.
        private void OnOpenTokensPageClick(object sender, RoutedEventArgs e)
        {
            var org = OrgUrlBox.Text?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(org))
            {
                MessageBox.Show(this, AppStrings.Get("Cfg_OpenTokensNeedUrl"),
                    AppStrings.Get("Cfg_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(org + "/_usersSettings/tokens") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, AppStrings.Get("Cfg_Title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Os nomes dos campos opcionais só são editáveis com a leitura habilitada.
        private void OnEpicTypeFieldEnabledChanged(object sender, RoutedEventArgs e)
        {
            if (EpicTypeFieldBox == null) return;
            EpicTypeFieldBox.IsEnabled = EpicTypeFieldEnabledBox.IsChecked == true;
        }

        private void OnApprovedFieldEnabledChanged(object sender, RoutedEventArgs e)
        {
            if (ApprovedFieldBox == null) return;
            ApprovedFieldBox.IsEnabled = ApprovedFieldEnabledBox.IsChecked == true;
        }

        private void OnTaskPriorityRangeEnabledChanged(object sender, RoutedEventArgs e)
        {
            if (TaskPriorityMinBox == null || TaskPriorityMaxBox == null) return;
            var enabled = TaskPriorityRangeEnabledBox.IsChecked == true;
            TaskPriorityMinBox.IsEnabled = enabled;
            TaskPriorityMaxBox.IsEnabled = enabled;
        }

        private void OnManageListClick(object sender, RoutedEventArgs e)
        {
            var dlg = new DevOpsProjectListWindow(_devOpsProjectListPath, BuildOptions()) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _devOpsProjectListPath = dlg.ResultFilePath ?? string.Empty;
                ListPathLabel.Text = string.IsNullOrWhiteSpace(_devOpsProjectListPath)
                    ? AppStrings.Get("Imp_NoPortfolio")
                    : _devOpsProjectListPath;
            }
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

        private void OnAddClassificationMapping(object sender, RoutedEventArgs e)
            => _classificationMappings.Add(new ClassificationMapping { DevOpsType = "Feature", FieldRef = string.Empty });

        private void OnRemoveClassificationMapping(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ClassificationMapping m)
                _classificationMappings.Remove(m);
        }

        private void OnAddExtraField(object sender, RoutedEventArgs e)
            => _extraFields.Add(new ExtraWorkItemField());

        private void OnRemoveExtraField(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ExtraWorkItemField field)
                _extraFields.Remove(field);
        }

        private void ShowStatus(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(OrgUrlBox.Text) || string.IsNullOrWhiteSpace(PatBox.Password))
            {
                ShowStatus(AppStrings.Get("Cfg_UrlPatRequired"));
                return;
            }

            var options = BuildOptions();
            TfsConnectionStore.Save(options, RememberTokenCheck.IsChecked == true, _storageKey);
            // Conexão nova (URL, projeto ou PAT): descarta metadados lidos com a anterior.
            TfsImportService.ResetMetadataCaches();
            SaveTaskPlanFileAssociation();
            DialogResult = true;
            Close();
        }

        private void SaveTaskPlanFileAssociation()
        {
            if (_currentProjectName == null) return;
            var path = TaskPlanFileBox.Text?.Trim() ?? "";
            var tp = Community.Services.TaskPlanSettingsStore.Load();
            if (string.Equals(tp.GetProjectFile(_currentProjectName) ?? "", path, StringComparison.OrdinalIgnoreCase))
                return;
            tp.SetProjectFile(_currentProjectName, path);
            Community.Services.TaskPlanSettingsStore.Save(tp);
        }

        private void OnBrowseTaskPlanFileClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = AppStrings.Get("Cfg_TaskPlanFile"),
                Filter = "Planilha do Excel (*.xlsx)|*.xlsx|Todos os arquivos (*.*)|*.*",
                CheckFileExists = true
            };
            if (!string.IsNullOrWhiteSpace(TaskPlanFileBox.Text))
                try { dlg.InitialDirectory = System.IO.Path.GetDirectoryName(TaskPlanFileBox.Text.Trim()); } catch { }
            if (dlg.ShowDialog(this) == true)
                TaskPlanFileBox.Text = dlg.FileName;
        }

        private TfsConnectionOptions BuildOptions() => new()
        {
            OrganizationUrl     = OrgUrlBox.Text?.Trim() ?? string.Empty,
            TeamProject         = ProjectBox.Text?.Trim() ?? string.Empty,
            PersonalAccessToken = PatBox.Password,
            RootWorkItemId      = TfsConnectionStore.Load(_storageKey).RootWorkItemId,
            HoursPerDay         = ProjectCalendarService.WorkingHoursPerDay,
            EffortFieldName     = string.IsNullOrWhiteSpace(EffortFieldBox.Text)    ? "HH Estimado"   : EffortFieldBox.Text.Trim(),
            StartFieldName      = string.IsNullOrWhiteSpace(StartFieldBox.Text)     ? "Data_Inicio"   : StartFieldBox.Text.Trim(),
            FinishFieldName     = string.IsNullOrWhiteSpace(FinishFieldBox.Text)    ? "Data_Fim"      : FinishFieldBox.Text.Trim(),
            PercAlocFieldName   = string.IsNullOrWhiteSpace(PercAlocFieldBox.Text)  ? "Perc_Alocacao" : PercAlocFieldBox.Text.Trim(),
            PercConclusaoFieldName = string.IsNullOrWhiteSpace(PercConclusaoFieldBox.Text) ? "Perc_Conclusao" : PercConclusaoFieldBox.Text.Trim(),
            EpicTypeFieldEnabled = EpicTypeFieldEnabledBox.IsChecked == true,
            EpicTypeFieldName = string.IsNullOrWhiteSpace(EpicTypeFieldBox.Text) ? "EPIC_TYPE" : EpicTypeFieldBox.Text.Trim(),
            ApprovedFieldEnabled = ApprovedFieldEnabledBox.IsChecked == true,
            ApprovedFieldName = string.IsNullOrWhiteSpace(ApprovedFieldBox.Text) ? "Approved" : ApprovedFieldBox.Text.Trim(),
            TaskPriorityRangeEnabled = TaskPriorityRangeEnabledBox.IsChecked == true,
            TaskPriorityMin = int.TryParse(TaskPriorityMinBox.Text?.Trim(), out var prioMin) && prioMin >= 1 ? prioMin : 1,
            TaskPriorityMax = int.TryParse(TaskPriorityMaxBox.Text?.Trim(), out var prioMax) && prioMax >= 1 ? prioMax : 4,
            SyncVersionFieldName = string.IsNullOrWhiteSpace(SyncVersionFieldBox.Text) ? "Sync_version" : SyncVersionFieldBox.Text.Trim(),
            SyncNameFieldName   = string.IsNullOrWhiteSpace(SyncNameFieldBox.Text)   ? "Sync_Name"    : SyncNameFieldBox.Text.Trim(),
            FixedStartTagName   = string.IsNullOrWhiteSpace(FixedStartTagBox.Text)  ? "DT-INI-NEG"   : FixedStartTagBox.Text.Trim(),
            SyncPredecessorLinks = SyncPredecessorLinksCheck.IsChecked == true,
            EnforceStoryCompletionWithTasks = EnforceStoryCompletionWithTasksCheck.IsChecked == true,
            EnableOrgPeopleDiscovery = EnableOrgDiscoveryCheck.IsChecked == true,
            FutureSprintDays    = int.TryParse(FutureSprintDaysBox.Text?.Trim(), out var fsd) && fsd >= 0 ? fsd : 90,
            DevOpsProjectListPath = _devOpsProjectListPath,
            ExtraCreateFields   = [.. _extraFields.Where(f => !string.IsNullOrWhiteSpace(f.Ref))],
            ClassificationPicklistValues = TfsConnectionStore.Load(_storageKey).ClassificationPicklistValues,
            TypeFieldMappings = BuildTypeFieldMappings()
        };

        private Dictionary<string, TypeFieldConfig> BuildTypeFieldMappings()
        {
            var saved = TfsConnectionStore.Load(_storageKey);
            var mappings = new Dictionary<string, TypeFieldConfig>(saved.TypeFieldMappings, StringComparer.OrdinalIgnoreCase);

            // Limpa CustomDevopsFields de todos os tipos antes de reaplicar
            foreach (var cfg in mappings.Values)
                cfg.CustomDevopsFields = [];

            // Agrupa por tipo DevOps e salva lista de campos
            var grouped = _classificationMappings
                .Where(m => !string.IsNullOrWhiteSpace(m.FieldRef))
                .GroupBy(m => string.Equals(m.DevOpsType, "Todos", StringComparison.OrdinalIgnoreCase) ? "*" : m.DevOpsType,
                         StringComparer.OrdinalIgnoreCase);

            foreach (var g in grouped)
            {
                if (!mappings.TryGetValue(g.Key, out var cfg))
                    cfg = new TypeFieldConfig();
                cfg.CustomDevopsFields = g.Select(m => new ClassificationFieldDef
                {
                    Field     = m.FieldRef.Trim(),
                    FieldType = string.IsNullOrWhiteSpace(m.FieldType) ? "Picklist" : m.FieldType.Trim(),
                    Values    = string.IsNullOrWhiteSpace(m.Values)    ? null       : m.Values.Trim(),
                }).ToList();
                mappings[g.Key] = cfg;
            }

            return mappings;
        }
    }
}
