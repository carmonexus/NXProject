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

        /// <summary>Caminho do arquivo da lista após fechar com OK.</summary>
        public string? ResultFilePath { get; private set; }

        /// <summary>Lista salva após fechar com OK.</summary>
        public ObservableCollection<DevOpsProject> ResultProjects => _projects;

        public DevOpsProjectListWindow(string? initialFilePath = null)
        {
            InitializeComponent();
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
                Title           = "Abrir ou criar Portfólio de Projetos",
                Filter          = "Portfólio de Projetos (*.devops.json)|*.devops.json|JSON (*.json)|*.json|Todos (*.*)|*.*",
                FileName        = "Portifolio de Projetos NX",
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
                Title      = "Salvar Portfólio de Projetos",
                Filter     = "Portfólio de Projetos (*.devops.json)|*.devops.json|JSON (*.json)|*.json",
                DefaultExt = ".devops.json",
                FileName   = "Portifolio de Projetos NX"
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
                SaveIfPathSet();
            }
        }

        private void OnEditClick(object sender, RoutedEventArgs e)
        {
            if (ProjectsGrid.SelectedItem is not DevOpsProject selected)
            {
                MessageBox.Show("Selecione um projeto para editar.", "Editar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new DevOpsProjectEditWindow(selected.Name, selected.RootWorkItemId,
                                                  selected.IsOpex, selected.CostCenter,
                                                  selected.CostCenterSource) { Owner = this };
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
            var options = TfsConnectionStore.Load("NXProject.Community");
            if (!options.IsValid)
            {
                MessageBox.Show(
                    "Configure a conexão com o Azure DevOps antes de usar o Discovery.\n(Menu Configurações → Conexão DevOps / TFS)",
                    "Conexão não configurada", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new DevOpsDiscoveryWindow(options, _projects) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            int added = 0;
            foreach (var p in dlg.SelectedProjects)
            {
                if (_projects.Any(x => x.RootWorkItemId == p.RootWorkItemId)) continue;
                _projects.Add(p);
                added++;
            }

            if (added > 0)
            {
                SaveIfPathSet();
                MessageBox.Show($"{added} projeto(s) adicionado(s) ao portfólio.", "Discovery", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Os itens selecionados já estão no portfólio.", "Discovery", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (ProjectsGrid.SelectedItem is not DevOpsProject selected)
            {
                MessageBox.Show("Selecione um projeto para excluir.", "Excluir", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Excluir \"{selected.Name}\"?",
                "Excluir Projeto", MessageBoxButton.YesNo, MessageBoxImage.Question);

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
                    "Selecione ou crie um arquivo para salvar a lista (botão \"Abrir / Criar...\").",
                    "Salvar Lista", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DevOpsProjectListService.Save(_projects, ResultFilePath);
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
