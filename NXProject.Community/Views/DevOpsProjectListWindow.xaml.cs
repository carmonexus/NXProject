using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class DevOpsProjectListWindow : Window
    {
        private ObservableCollection<DevOpsProject> _projects = new();
        private readonly TfsConnectionOptions? _connectionOptions;

        /// <summary>Caminho do arquivo da lista após fechar com OK.</summary>
        public string? ResultFilePath { get; private set; }

        /// <summary>Lista salva após fechar com OK.</summary>
        public ObservableCollection<DevOpsProject> ResultProjects => _projects;

        /// <summary>Projeto selecionado na grid ao fechar com OK (para pré-selecionar na importação).</summary>
        public DevOpsProject? SelectedProject { get; private set; }

        public DevOpsProjectListWindow(string? initialFilePath = null, TfsConnectionOptions? connectionOptions = null)
        {
            InitializeComponent();
            _connectionOptions = connectionOptions;
            ProjectsGrid.ItemsSource = _projects;

            if (!string.IsNullOrWhiteSpace(initialFilePath))
                LoadFromFile(initialFilePath);
        }

        private void LoadFromFile(string path)
        {
            var loaded = DevOpsProjectListService.Load(path);
            _projects.Clear();
            foreach (var p in loaded)
                _projects.Add(p);
            FilePathLabel.Text = path;
            ResultFilePath = path;
        }

        private void OnBrowseFileClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title           = AppStrings.Get("Port_OpenDlgTitle"),
                Filter          = AppStrings.Get("Port_FileFilter"),
                FileName        = AppStrings.Get("Port_DefaultFileName"),
                CheckFileExists = false
            };

            if (!string.IsNullOrWhiteSpace(ResultFilePath) && File.Exists(ResultFilePath))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(ResultFilePath);
                dlg.FileName = Path.GetFileName(ResultFilePath);
            }

            if (dlg.ShowDialog(this) != true)
                return;

            var path = dlg.FileName;
            if (!File.Exists(path))
            {
                // Cria arquivo novo vazio
                DevOpsProjectListService.Save(Array.Empty<DevOpsProject>(), path);
            }
            LoadFromFile(path);
        }

        private void OnSaveAsClick(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title      = AppStrings.Get("Port_SaveDlgTitle"),
                Filter     = AppStrings.Get("Port_SaveFilter"),
                DefaultExt = ".devops.json",
                FileName   = AppStrings.Get("Port_DefaultFileName")
            };

            if (!string.IsNullOrWhiteSpace(ResultFilePath))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(ResultFilePath);
                dlg.FileName = Path.GetFileName(ResultFilePath);
            }

            if (dlg.ShowDialog(this) != true)
                return;

            ResultFilePath = dlg.FileName;
            FilePathLabel.Text = ResultFilePath;
            DevOpsProjectListService.Save(_projects, ResultFilePath);
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            var dlg = new DevOpsProjectEditWindow { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                _projects.Add(dlg.Result);
                ProjectsGrid.SelectedItem = dlg.Result;
                ProjectsGrid.ScrollIntoView(dlg.Result);
                SaveIfPathSet();
            }
        }

        private void OnEditClick(object sender, RoutedEventArgs e)
        {
            if (ProjectsGrid.SelectedItem is not DevOpsProject selected)
            {
                MessageBox.Show(AppStrings.Get("Port_SelectToEdit"), AppStrings.Get("Port_EditTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new DevOpsProjectEditWindow(selected.Name, selected.RootWorkItemId,
                                                  selected.IsOpex, selected.CostCenter,
                                                  selected.CostCenterSource, selected.Process) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                // Owner é informativo (vem do DevOps) e não é editável nesta tela — preserva o atual.
                dlg.Result.Owner = selected.Owner;
                var idx = _projects.IndexOf(selected);
                _projects[idx] = dlg.Result;
                ProjectsGrid.SelectedItem = dlg.Result;
                SaveIfPathSet();
            }
        }

        private void OnDiscoveryClick(object sender, RoutedEventArgs e)
        {
            var options = _connectionOptions ?? TfsConnectionStore.Load("NXProject.Community");
            if (string.IsNullOrWhiteSpace(options.OrganizationUrl) ||
                string.IsNullOrWhiteSpace(options.TeamProject) ||
                string.IsNullOrWhiteSpace(options.PersonalAccessToken))
            {
                var missing = string.Join(", ", new[]
                {
                    string.IsNullOrWhiteSpace(options.OrganizationUrl) ? "URL" : null,
                    string.IsNullOrWhiteSpace(options.TeamProject) ? "Team Project" : null,
                    string.IsNullOrWhiteSpace(options.PersonalAccessToken) ? "PAT" : null
                }.Where(x => x != null));

                MessageBox.Show(
                    $"{AppStrings.Get("Port_NoConnectionMsg")}\n\nCampos ausentes: {missing}",
                    AppStrings.Get("Port_NoConnectionTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new DevOpsDiscoveryWindow(options, _projects) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            int added = 0, updated = 0;
            DevOpsProject? lastAdded = null;
            foreach (var p in dlg.SelectedProjects)
            {
                var existing = _projects.FirstOrDefault(x => x.RootWorkItemId == p.RootWorkItemId);
                if (existing != null)
                {
                    // Já existe: atualiza os dados vindos do DevOps (processo, owner e nome)
                    // sem mexer no que é configurado localmente (Tipo/OPEX-CAPEX, Centro de Custo).
                    var changed = false;
                    if (!string.IsNullOrWhiteSpace(p.Process) && existing.Process != p.Process)
                    { existing.Process = p.Process; changed = true; }
                    if (!string.IsNullOrWhiteSpace(p.Owner) && existing.Owner != p.Owner)
                    { existing.Owner = p.Owner; changed = true; }
                    if (!string.IsNullOrWhiteSpace(p.Name) && existing.Name != p.Name)
                    { existing.Name = p.Name; changed = true; }
                    if (changed) updated++;
                    continue;
                }
                _projects.Add(p);
                lastAdded = p;
                added++;
            }

            if (added > 0 || updated > 0)
            {
                // Deixa o projeto recém-adicionado selecionado na grid (para pré-selecionar na importação).
                if (lastAdded != null)
                {
                    ProjectsGrid.SelectedItem = lastAdded;
                    ProjectsGrid.ScrollIntoView(lastAdded);
                }
                ProjectsGrid.Items.Refresh();   // reflete os campos atualizados (ex.: processo)
                SaveIfPathSet();
                MessageBox.Show(AppStrings.Get("Port_AddedUpdatedMsg", added, updated),
                    AppStrings.Get("Port_DiscoveryTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(AppStrings.Get("Port_AlreadyInMsg"), AppStrings.Get("Port_DiscoveryTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (ProjectsGrid.SelectedItem is not DevOpsProject selected)
            {
                MessageBox.Show(AppStrings.Get("Port_SelectToDelete"), AppStrings.Get("Port_DeleteTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                AppStrings.Get("Port_DeleteConfirm", selected.Name),
                AppStrings.Get("Port_DeleteConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm == MessageBoxResult.Yes)
            {
                _projects.Remove(selected);
                SaveIfPathSet();
            }
        }

        private void SaveIfPathSet()
        {
            if (!string.IsNullOrWhiteSpace(ResultFilePath))
                DevOpsProjectListService.Save(_projects, ResultFilePath);
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ResultFilePath))
            {
                MessageBox.Show(
                    AppStrings.Get("Port_NoFileMsg"),
                    AppStrings.Get("Port_SaveListTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DevOpsProjectListService.Save(_projects, ResultFilePath);
            SelectedProject = ProjectsGrid.SelectedItem as DevOpsProject;
            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
