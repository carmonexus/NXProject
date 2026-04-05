using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        [ObservableProperty] private Project _project = new();
        [ObservableProperty] private string _statusMessage = "Pronto";
        [ObservableProperty] private int _selectedViewIndex = 0;
        [ObservableProperty] private string _selectedZoom = "Semana";
        [ObservableProperty] private TaskViewModel? _selectedTask;

        public ObservableCollection<string> ZoomLevels { get; } = new()
        {
            "Dia", "Semana", "Mês", "Trimestre"
        };

        // Lista plana de tarefas para o DataGrid (com hierarquia via indentação)
        public ObservableCollection<TaskViewModel> FlatTasks { get; } = new();

        // IDs das tarefas que o usuário recolheu manualmente
        private readonly HashSet<int> _collapsedTaskIds = new();

        // Agrupamentos para aba Sprints
        public ObservableCollection<SprintGroup> SprintGroups { get; } = new();

        // Agrupamentos para aba Recursos
        public ObservableCollection<ResourceAllocationGroup> ResourceAllocationGroups { get; } = new();

        private int _nextId = 1;

        public MainViewModel()
        {
            // Projeto de exemplo
            NewProject();
        }

        private void RebuildFlatTasks()
        {
            FlatTasks.Clear();
            foreach (var task in Project.Tasks)
                AddFlatRecursive(task, 0);
            RecalcSprints();
            RebuildSprintGroups();
            RebuildResourceGroups();
        }

        private bool _rebuildPending = false;

        private void AddFlatRecursive(ProjectTask task, int depth)
        {
            var vm = new TaskViewModel(task, depth);
            vm.IsSelected = SelectedTask?.Model == task;
            vm.IsExpanded = !_collapsedTaskIds.Contains(task.Id);

            // Reagir ao toggle de expand/collapse sem criar loop infinito
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(TaskViewModel.IsExpanded)) return;
                if (vm.IsExpanded) _collapsedTaskIds.Remove(task.Id);
                else _collapsedTaskIds.Add(task.Id);
                if (!_rebuildPending)
                {
                    _rebuildPending = true;
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        _rebuildPending = false;
                        RebuildFlatTasks();
                    });
                }
            };

            FlatTasks.Add(vm);
            if (vm.IsExpanded)
                foreach (var child in task.Children)
                    AddFlatRecursive(child, depth + 1);
        }

        private void RecalcSprints()
        {
            var sprintDays = Project.SprintDurationDays;
            var projectStart = Project.StartDate;

            foreach (var vm in FlatTasks)
            {
                var dayOffset = (vm.Start - projectStart).TotalDays;
                vm.SprintNumber = dayOffset < 0 ? 0 : (int)(dayOffset / sprintDays) + 1;
            }
        }

        // ── Comandos ─────────────────────────────────────────────────────────

        [RelayCommand]
        private void NewProject()
        {
            Project = new Project { Name = "Novo Projeto", StartDate = DateTime.Today };
            _nextId = 1;
            _collapsedTaskIds.Clear();
            SelectedTask = null;
            FlatTasks.Clear();
            StatusMessage = "Novo projeto criado";
        }

        [RelayCommand]
        private void ClearProject()
        {
            if (Project.Tasks.Count == 0 && Project.Resources.Count == 0)
            {
                StatusMessage = "O projeto ja esta limpo";
                return;
            }

            var confirm = MessageBox.Show(
                "Deseja remover todas as tarefas e recursos do projeto atual?",
                "Limpar projeto",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                StatusMessage = "Limpeza do projeto cancelada";
                return;
            }

            Project.Tasks.Clear();
            Project.Resources.Clear();
            Project.IsDirty = true;
            _nextId = 1;
            _collapsedTaskIds.Clear();
            SelectedTask = null;
            RebuildFlatTasks();
            StatusMessage = "Projeto limpo";
        }

        [RelayCommand]
        private void OpenProject()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Projeto NXProject (*.xml)|*.xml|Todos os arquivos (*.*)|*.*",
                Title = "Abrir Projeto"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var project = XmlProjectService.Load(dlg.FileName);
                    Project = project;
                    _nextId = AllTasks().Select(t => t.Id).DefaultIfEmpty(0).Max() + 1;
                    RebuildFlatTasks();
                    StatusMessage = $"Projeto aberto: {dlg.FileName}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao abrir projeto:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        [RelayCommand]
        private void ImportOpenProj()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "OpenProj (*.pod)|*.pod|Todos os arquivos (*.*)|*.*",
                Title = "Importar projeto do OpenProj"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var project = OpenProjImportService.Import(dlg.FileName);
                Project = project;
                _nextId = AllTasks().Select(t => t.Id).DefaultIfEmpty(0).Max() + 1;
                RebuildFlatTasks();
                StatusMessage = $"OpenProj importado: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao importar OpenProj:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void ImportMspdi()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "MS Project XML (*.xml)|*.xml|Todos os arquivos (*.*)|*.*",
                Title = "Importar projeto do Microsoft Project (MSPDI XML)"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var project = MspdiImportService.Import(dlg.FileName);
                Project = project;
                _nextId = AllTasks().Select(t => t.Id).DefaultIfEmpty(0).Max() + 1;
                RebuildFlatTasks();
                StatusMessage = $"MS Project importado: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao importar MS Project XML:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void ImportExcel()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Excel XML 2003 (*.xml)|*.xml|Arquivos Excel antigos (*.xls)|*.xls|Todos os arquivos (*.*)|*.*",
                Title = "Importar projeto do Excel"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var project = ExcelXmlService.Import(dlg.FileName);
                Project = project;
                _nextId = AllTasks().Select(t => t.Id).DefaultIfEmpty(0).Max() + 1;
                RebuildFlatTasks();
                StatusMessage = $"Excel importado: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao importar Excel:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void SaveProject()
        {
            if (string.IsNullOrEmpty(Project.FilePath))
            {
                SaveAsProject();
                return;
            }
            Save(Project.FilePath);
        }

        [RelayCommand]
        private void SaveAsProject()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Projeto NXProject (*.xml)|*.xml",
                Title = "Salvar Projeto",
                FileName = Project.Name
            };
            if (dlg.ShowDialog() == true)
                Save(dlg.FileName);
        }

        private void Save(string path)
        {
            try
            {
                XmlProjectService.Save(Project, path);
                Project.FilePath = path;
                Project.IsDirty = false;
                StatusMessage = $"Salvo: {path}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao salvar:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void ExportOpenProj()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "OpenProj (*.pod)|*.pod",
                Title = "Exportar para OpenProj",
                FileName = Project.Name
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                OpenProjExportService.Export(Project, dlg.FileName);
                StatusMessage = $"Exportado OpenProj: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao exportar OpenProj:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void ExportMspdi()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "MS Project XML (*.xml)|*.xml",
                Title = "Exportar para MS Project XML (MSPDI)",
                FileName = Project.Name
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                MspdiExportService.Export(Project, dlg.FileName);
                StatusMessage = $"Exportado MSPDI: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao exportar MSPDI:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void ExportCsv()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv",
                Title = "Exportar para CSV",
                FileName = Project.Name
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    CsvService.Export(AllTasks().ToList(), dlg.FileName);
                    StatusMessage = $"Exportado: {dlg.FileName}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao exportar:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        [RelayCommand]
        private void ExportExcel()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel XML 2003 (*.xml)|*.xml",
                Title = "Exportar para Excel",
                FileName = Project.Name
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                ExcelXmlService.Export(Project, AllTasks().ToList(), dlg.FileName);
                StatusMessage = $"Exportado Excel: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao exportar Excel:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

#if !COMMUNITY
        [RelayCommand]
        private void PrintProject()
        {
            try
            {
                if (PrintService.PrintProject(Project, FlatTasks, pdfMode: false))
                    StatusMessage = "Documento enviado para impressão";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao imprimir:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void PrintProjectPdf()
        {
            try
            {
                if (PrintService.PrintProject(Project, FlatTasks, pdfMode: true))
                    StatusMessage = "Fluxo de geração de PDF iniciado";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao gerar PDF:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
#endif

        [RelayCommand]
        private void AddTask()
        {
            var start = SelectedTask?.Start
                ?? AllTasks().Select(t => t.Start).DefaultIfEmpty(Project.StartDate).Min();
            var task = new ProjectTask
            {
                Id = _nextId++,
                Name = "Nova Tarefa",
                Start = start,
                Finish = start.AddDays(1)
            };
            Project.Tasks.Add(task);
            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = "Tarefa adicionada";
        }

        [RelayCommand]
        private void AddSubtask()
        {
            if (SelectedTask == null) { StatusMessage = "Selecione uma tarefa pai primeiro"; return; }

            var parent = SelectedTask.Model;
            var task = new ProjectTask
            {
                Id = _nextId++,
                Name = "Nova Subtarefa",
                Start = parent.Start,
                Finish = parent.Start.AddDays(3),
                Level = parent.Level + 1,
                Parent = parent
            };
            parent.Children.Add(task);
            parent.IsSummary = true;
            parent.RecalcSummary();
            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = "Subtarefa adicionada";
        }

        [RelayCommand]
        private void DeleteTask()
        {
            if (SelectedTask == null) return;

            var task = SelectedTask.Model;
            if (task.Parent != null)
            {
                task.Parent.Children.Remove(task);
                if (task.Parent.Children.Count == 0)
                    task.Parent.IsSummary = false;
            }
            else
            {
                Project.Tasks.Remove(task);
            }

            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = "Tarefa excluída";
        }

        [RelayCommand]
        private void IndentTask()
        {
            if (SelectedTask == null) return;
            var task = SelectedTask.Model;
            var allFlat = AllTasks().ToList();
            var idx = allFlat.IndexOf(task);
            if (idx <= 0) return;

            var newParent = allFlat[idx - 1];

            // Remove do pai atual ou da raiz
            if (task.Parent != null) task.Parent.Children.Remove(task);
            else Project.Tasks.Remove(task);

            task.Parent = newParent;
            task.Level = newParent.Level + 1;
            newParent.Children.Add(task);
            newParent.IsSummary = true;
            newParent.RecalcSummary();
            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        [RelayCommand]
        private void OutdentTask()
        {
            if (SelectedTask == null) return;
            var task = SelectedTask.Model;
            if (task.Parent == null) return;

            var oldParent = task.Parent;
            oldParent.Children.Remove(task);
            if (oldParent.Children.Count == 0) oldParent.IsSummary = false;

            var grandParent = oldParent.Parent;
            task.Parent = grandParent;
            task.Level = grandParent != null ? grandParent.Level + 1 : 0;

            if (grandParent != null) grandParent.Children.Add(task);
            else Project.Tasks.Add(task);

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        [RelayCommand] private void Undo() { StatusMessage = "Desfazer (em desenvolvimento)"; }
        [RelayCommand] private void Redo() { StatusMessage = "Refazer (em desenvolvimento)"; }
        [RelayCommand] private void ShowGantt() { SelectedViewIndex = 0; }
#if !COMMUNITY
        [RelayCommand] private void ShowSprints() { SelectedViewIndex = 1; }
        [RelayCommand] private void ShowResourceUsage() { SelectedViewIndex = 2; }
        [RelayCommand] private void ShowPert() { SelectedViewIndex = 3; }
        [RelayCommand] private void ProjectInfo() { StatusMessage = "Informações do projeto (em desenvolvimento)"; }
        [RelayCommand] private void EditCalendar() { StatusMessage = "Editor de calendário (em desenvolvimento)"; }
        [RelayCommand] private void SprintSettings() { StatusMessage = "Configurações de sprint (em desenvolvimento)"; }
        [RelayCommand] private void ManageResources() { StatusMessage = "Gerenciar recursos (em desenvolvimento)"; }
        [RelayCommand] private void ReportTasks() { StatusMessage = "Relatório de tarefas (em desenvolvimento)"; }
        [RelayCommand] private void ReportResources() { StatusMessage = "Relatório de recursos (em desenvolvimento)"; }
        [RelayCommand] private void AIGenerateTasks() { StatusMessage = "Geração de tarefas com IA (em desenvolvimento)"; }
        [RelayCommand] private void AISuggestAllocation() { StatusMessage = "Sugestão de alocação com IA (em desenvolvimento)"; }
        [RelayCommand] private void AISettings() { StatusMessage = "Configurações de IA (em desenvolvimento)"; }
#endif

        private void RebuildSprintGroups()
        {
            SprintGroups.Clear();
            var bySprint = new Dictionary<int, List<TaskViewModel>>();
            foreach (var vm in FlatTasks)
            {
                if (!bySprint.ContainsKey(vm.SprintNumber))
                    bySprint[vm.SprintNumber] = new();
                bySprint[vm.SprintNumber].Add(vm);
            }
            foreach (var kv in bySprint.OrderBy(x => x.Key))
            {
                var sprintStart = Project.StartDate.AddDays((kv.Key - 1) * Project.SprintDurationDays);
                var sprintEnd = sprintStart.AddDays(Project.SprintDurationDays - 1);
                SprintGroups.Add(new SprintGroup
                {
                    Header = $"Sprint {kv.Key}  ({sprintStart:dd/MM/yy} – {sprintEnd:dd/MM/yy})",
                    Tasks = new ObservableCollection<TaskViewModel>(kv.Value)
                });
            }
        }

        private void RebuildResourceGroups()
        {
            ResourceAllocationGroups.Clear();
            foreach (var resource in Project.Resources)
            {
                var group = new ResourceAllocationGroup
                {
                    ResourceName = resource.Name,
                    CapacityText = $"Capacidade: {resource.MaxUnitsPerDay}h/dia"
                };

                foreach (var vm in FlatTasks)
                {
                    var assignment = vm.Model.Resources.FirstOrDefault(r => r.ResourceId == resource.Id);
                    if (assignment == null) continue;

                    group.Tasks.Add(new ResourceTaskRow
                    {
                        SprintNumber = vm.SprintNumber,
                        TaskName = vm.Name,
                        AllocationPercent = assignment.AllocationPercent,
                        EstimatedHours = assignment.EstimatedHours ?? 0
                    });
                }

                // Verifica sobrealocação (total de horas estimadas > capacidade total)
                var totalHours = group.Tasks.Sum(t => t.EstimatedHours);
                var sprintCount = group.Tasks.Select(t => t.SprintNumber).Distinct().Count();
                var capacityHours = sprintCount * Project.SprintDurationDays * resource.MaxUnitsPerDay;
                group.IsOverAllocated = totalHours > capacityHours;

                ResourceAllocationGroups.Add(group);
            }
        }

        // Helpers
        private IEnumerable<ProjectTask> AllTasks()
        {
            foreach (var t in Project.Tasks)
                foreach (var ft in FlattenTask(t))
                    yield return ft;
        }

        private IEnumerable<ProjectTask> FlattenTask(ProjectTask task)
        {
            yield return task;
            foreach (var child in task.Children)
                foreach (var ft in FlattenTask(child))
                    yield return ft;
        }
    }
}
