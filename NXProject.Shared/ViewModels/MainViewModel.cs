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
        private readonly string _sprintSettingsStorageKey;
        private bool _isApplyingProjectSprintSettings;

        [ObservableProperty] private Project _project = new();
        [ObservableProperty] private string _statusMessage = "Pronto";
        [ObservableProperty] private int _selectedViewIndex = 0;
        [ObservableProperty] private string _selectedZoom = "Mês";
        [ObservableProperty] private TaskViewModel? _selectedTask;
        [ObservableProperty] private int _sprintDurationDays = 14;
        [ObservableProperty] private int _firstSprintNumber = 1;
        [ObservableProperty] private string _sprintNumberingMode = "Sequencial";
        [ObservableProperty] private double _lowDaysPerSfp = 1.0;
        [ObservableProperty] private double _mediumDaysPerSfp = 1.0;
        [ObservableProperty] private double _highDaysPerSfp = 1.0;

        public ObservableCollection<string> ZoomLevels { get; } = new()
        {
            "Dia", "Semana", "Mês", "Trimestre"
        };

        public ObservableCollection<string> SprintNumberingModes { get; } = new()
        {
            "Sequencial", "Par", "Impar"
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

        public MainViewModel(string sprintSettingsStorageKey = "NXProject.Community")
        {
            _sprintSettingsStorageKey = string.IsNullOrWhiteSpace(sprintSettingsStorageKey)
                ? "NXProject.Community"
                : sprintSettingsStorageKey.Trim();

            // Projeto de exemplo
            NewProject();
        }

        private void RebuildFlatTasks()
        {
            var selectedModel = SelectedTask?.Model;
            FlatTasks.Clear();
            foreach (var task in Project.Tasks)
                AddFlatRecursive(task, 0);
            RecalcSprints();
            RebuildSprintGroups();
            RebuildResourceGroups();

            SelectedTask = selectedModel == null
                ? null
                : FlatTasks.FirstOrDefault(vm => vm.Model == selectedModel);
        }

        private bool _rebuildPending = false;

        private void AddFlatRecursive(ProjectTask task, int depth)
        {
            var vm = new TaskViewModel(task, depth, LowDaysPerSfp, MediumDaysPerSfp, HighDaysPerSfp);
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
            var projectStart = Project.StartDate;

            foreach (var vm in FlatTasks)
            {
                var sprintIndex = GetSprintIndex(vm.Start, projectStart);
                vm.SprintNumber = sprintIndex < 0 ? 0 : GetSprintNumberFromIndex(sprintIndex);
            }
        }

        partial void OnSprintDurationDaysChanged(int value)
        {
            var normalizedValue = Math.Max(1, value);
            if (value != normalizedValue)
            {
                SprintDurationDays = normalizedValue;
                return;
            }

            Project.SprintDurationDays = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        partial void OnFirstSprintNumberChanged(int value)
        {
            var normalizedValue = Math.Max(1, value);
            if (value != normalizedValue)
            {
                FirstSprintNumber = normalizedValue;
                return;
            }

            Project.FirstSprintNumber = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        partial void OnSprintNumberingModeChanged(string value)
        {
            var normalizedValue = SprintNumberingModes.Contains(value) ? value : "Sequencial";
            if (!string.Equals(value, normalizedValue, StringComparison.Ordinal))
            {
                SprintNumberingMode = normalizedValue;
                return;
            }

            Project.SprintNumberingMode = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        partial void OnLowDaysPerSfpChanged(double value)
        {
            var normalizedValue = value < 0 ? 0 : value;
            if (Math.Abs(value - normalizedValue) > double.Epsilon)
            {
                LowDaysPerSfp = normalizedValue;
                return;
            }

            Project.LowDaysPerSfp = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        partial void OnMediumDaysPerSfpChanged(double value)
        {
            var normalizedValue = value < 0 ? 0 : value;
            if (Math.Abs(value - normalizedValue) > double.Epsilon)
            {
                MediumDaysPerSfp = normalizedValue;
                return;
            }

            Project.MediumDaysPerSfp = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        partial void OnHighDaysPerSfpChanged(double value)
        {
            var normalizedValue = value < 0 ? 0 : value;
            if (Math.Abs(value - normalizedValue) > double.Epsilon)
            {
                HighDaysPerSfp = normalizedValue;
                return;
            }

            Project.HighDaysPerSfp = normalizedValue;
            if (_isApplyingProjectSprintSettings)
                return;

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        partial void OnSelectedTaskChanged(TaskViewModel? oldValue, TaskViewModel? newValue)
        {
            if (oldValue != null)
                oldValue.IsSelected = false;

            if (newValue != null)
                newValue.IsSelected = true;
        }

        // ── Comandos ─────────────────────────────────────────────────────────

        [RelayCommand]
        private void NewProject()
        {
            var project = new Project { Name = "Novo Projeto", StartDate = DateTime.Today };
            project.ApplySprintSettingsProfile(SprintSettingsStore.Load(_sprintSettingsStorageKey));
            Project = project;
            ApplyProjectSprintSettingsToViewModel(project);
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
                    ApplyProjectSprintSettingsToViewModel(project);
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
                ApplyProjectSprintSettingsToViewModel(project);
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
                ApplyProjectSprintSettingsToViewModel(project);
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
                ApplyProjectSprintSettingsToViewModel(project);
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

        [RelayCommand]
        private void SaveSprintSettingsAsDefault()
        {
            try
            {
                SprintSettingsStore.Save(Project.GetSprintSettingsProfile(), _sprintSettingsStorageKey);
                StatusMessage = "Configuracao de sprint gravada para novos projetos";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao salvar configuracao padrao de sprint:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
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
            var previousTask = SelectedTask?.Model.Parent == null
                ? Project.Tasks.LastOrDefault()
                : null;
            var task = new ProjectTask
            {
                Id = _nextId++,
                Name = "Nova Tarefa",
                Start = start,
                Finish = start.AddDays(1)
            };

            if (previousTask != null)
                task.PredecessorIds.Add(previousTask.Id);

            Project.Tasks.Add(task);
            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = previousTask != null
                ? "Tarefa adicionada com predecessora da tarefa anterior."
                : "Tarefa adicionada";
        }

        [RelayCommand]
        private void AddSubtask()
        {
            if (SelectedTask == null) { StatusMessage = "Selecione uma tarefa pai primeiro"; return; }

            var parent = SelectedTask.Model;
            var previousSibling = parent.Children.LastOrDefault();
            var task = new ProjectTask
            {
                Id = _nextId++,
                Name = "Nova Subtarefa",
                Start = parent.Start,
                Finish = parent.Start.AddDays(3),
                Level = parent.Level + 1,
                Parent = parent
            };

            if (previousSibling != null)
                task.PredecessorIds.Add(previousSibling.Id);

            parent.Children.Add(task);
            parent.IsSummary = true;
            parent.RecalcSummary();
            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = previousSibling != null
                ? "Subtarefa adicionada com predecessora da subtarefa anterior."
                : "Subtarefa adicionada";
        }

        [RelayCommand]
        private void DeleteTask()
        {
            if (SelectedTask == null)
            {
                StatusMessage = "Selecione uma tarefa para excluir.";
                return;
            }

            var task = SelectedTask.Model;
            var removedTasks = FlattenTask(task).ToList();

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

            foreach (var removedTask in removedTasks)
                _collapsedTaskIds.Remove(removedTask.Id);

            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = removedTasks.Count > 1
                ? "Tarefa e subtarefas excluidas."
                : "Tarefa excluida.";
        }

        [RelayCommand]
        private void IndentTask()
        {
            if (SelectedTask == null)
            {
                StatusMessage = "Selecione uma tarefa para alterar a hierarquia.";
                return;
            }

            var task = SelectedTask.Model;
            var allFlat = AllTasks().ToList();
            var idx = allFlat.IndexOf(task);
            if (idx <= 0)
            {
                StatusMessage = "A primeira tarefa nao pode virar subtarefa.";
                return;
            }

            var targetParentLevel = task.Level;
            var newParent = FindPreviousTaskAtLevel(allFlat, idx - 1, targetParentLevel);
            if (newParent == null)
            {
                StatusMessage = "Nao existe tarefa anterior no nivel necessario para identar mais um nivel.";
                return;
            }

            var oldParent = task.Parent;
            RemoveTaskFromCurrentCollection(task);

            task.Parent = newParent;
            newParent.Children.Add(task);
            newParent.IsSummary = true;
            UpdateTaskLevelRecursive(task, newParent.Level + 1);
            RecalcSummaryChain(oldParent);
            RecalcSummaryChain(newParent);
            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = "Tarefa identada um nivel.";
        }

        [RelayCommand]
        private void OutdentTask()
        {
            if (SelectedTask == null)
            {
                StatusMessage = "Selecione uma tarefa para alterar a hierarquia.";
                return;
            }

            var task = SelectedTask.Model;
            if (task.Parent == null)
            {
                StatusMessage = "A tarefa ja esta no nivel raiz.";
                return;
            }

            var oldParent = task.Parent;
            oldParent.Children.Remove(task);
            if (oldParent.Children.Count == 0)
                oldParent.IsSummary = false;

            var grandParent = oldParent.Parent;
            task.Parent = grandParent;
            UpdateTaskLevelRecursive(task, grandParent != null ? grandParent.Level + 1 : 0);

            var targetCollection = grandParent?.Children ?? Project.Tasks;
            var insertAfterIndex = targetCollection.IndexOf(oldParent);
            if (insertAfterIndex < 0)
                insertAfterIndex = targetCollection.Count - 1;
            targetCollection.Insert(insertAfterIndex + 1, task);

            RecalcSummaryChain(oldParent);
            RecalcSummaryChain(grandParent);
            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = "Tarefa promovida um nivel.";
        }

        [RelayCommand]
        private void MoveTaskUp(TaskViewModel? taskVm)
        {
            var task = (taskVm ?? SelectedTask)?.Model;
            if (task == null)
                return;

            if (MoveTaskByOffset(task, -1))
                StatusMessage = "Tarefa movida para cima";
        }

        [RelayCommand]
        private void MoveTaskDown(TaskViewModel? taskVm)
        {
            var task = (taskVm ?? SelectedTask)?.Model;
            if (task == null)
                return;

            if (MoveTaskByOffset(task, 1))
                StatusMessage = "Tarefa movida para baixo";
        }

        public bool MoveTaskByDrop(TaskViewModel sourceVm, TaskViewModel targetVm, bool insertAfter)
        {
            if (sourceVm.Model == targetVm.Model)
                return false;

            var sourceCollection = GetTaskCollection(sourceVm.Model);
            if (IsTaskInSubtree(sourceVm.Model, targetVm.Model))
            {
                StatusMessage = "Nao e possivel mover uma tarefa para dentro da propria hierarquia.";
                return false;
            }

            var targetTask = FindTaskInCollectionHierarchy(targetVm.Model, sourceCollection);
            if (targetTask == null)
            {
                StatusMessage = "O arrasto so pode reordenar tarefas dentro do mesmo nivel.";
                return false;
            }

            var currentIndex = sourceCollection.IndexOf(sourceVm.Model);
            var targetIndex = sourceCollection.IndexOf(targetTask);
            if (currentIndex < 0 || targetIndex < 0)
                return false;

            if (insertAfter)
                targetIndex++;

            if (targetIndex > currentIndex)
                targetIndex--;

            if (targetIndex == currentIndex)
                return false;

            MoveTask(sourceCollection, currentIndex, targetIndex);
            StatusMessage = "Tarefa reordenada";
            return true;
        }

        [RelayCommand]
        private void LinkTasksSequentially()
        {
            var orderedTasks = AllTasks()
                .Where(task => !task.IsSummary)
                .ToList();

            if (orderedTasks.Count < 2)
            {
                StatusMessage = "Adicione pelo menos duas atividades para encadear.";
                return;
            }

            ProjectTask? previousTask = null;
            foreach (var task in orderedTasks)
            {
                task.PredecessorIds.Clear();
                if (previousTask != null)
                    task.PredecessorIds.Add(previousTask.Id);

                previousTask = task;
            }

            Project.IsDirty = true;
            RebuildFlatTasks();
            StatusMessage = "Atividades encadeadas em sequência.";
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
        [RelayCommand] private void SprintSettings() { StatusMessage = "Abra Configurações de Sprint pelo menu Projeto."; }
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
                var sprintIndex = GetSprintIndex(vm.Start, Project.StartDate);
                if (sprintIndex < 0)
                    continue;

                if (!bySprint.ContainsKey(sprintIndex))
                    bySprint[sprintIndex] = new();
                bySprint[sprintIndex].Add(vm);
            }
            foreach (var kv in bySprint.OrderBy(x => x.Key))
            {
                var sprintIndex = kv.Key;
                var sprintStart = Project.StartDate.AddDays(sprintIndex * Math.Max(1, SprintDurationDays));
                var sprintEnd = sprintStart.AddDays(Math.Max(1, SprintDurationDays) - 1);
                var sprintNumber = GetSprintNumberFromIndex(sprintIndex);
                SprintGroups.Add(new SprintGroup
                {
                    Header = $"Sprint {sprintNumber}  ({sprintStart:dd/MM/yy} – {sprintEnd:dd/MM/yy})",
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
                var capacityHours = sprintCount * Math.Max(1, SprintDurationDays) * resource.MaxUnitsPerDay;
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

        private void ApplyProjectSprintSettingsToViewModel(Project project)
        {
            _isApplyingProjectSprintSettings = true;
            try
            {
                SprintDurationDays = project.SprintDurationDays;
                FirstSprintNumber = project.FirstSprintNumber;
                SprintNumberingMode = project.SprintNumberingMode;
                LowDaysPerSfp = project.LowDaysPerSfp;
                MediumDaysPerSfp = project.MediumDaysPerSfp;
                HighDaysPerSfp = project.HighDaysPerSfp;
            }
            finally
            {
                _isApplyingProjectSprintSettings = false;
            }
        }

        private bool MoveTaskByOffset(ProjectTask task, int offset)
        {
            var collection = GetTaskCollection(task);
            var currentIndex = collection.IndexOf(task);
            if (currentIndex < 0)
                return false;

            var targetIndex = currentIndex + offset;
            if (targetIndex < 0 || targetIndex >= collection.Count)
                return false;

            MoveTask(collection, currentIndex, targetIndex);
            return true;
        }

        private void MoveTask(ObservableCollection<ProjectTask> collection, int currentIndex, int targetIndex)
        {
            if (currentIndex == targetIndex)
                return;

            var task = collection[currentIndex];
            collection.RemoveAt(currentIndex);
            collection.Insert(targetIndex, task);

            Project.IsDirty = true;
            RebuildFlatTasks();
        }

        private ObservableCollection<ProjectTask> GetTaskCollection(ProjectTask task)
        {
            return task.Parent?.Children ?? Project.Tasks;
        }

        private static ProjectTask? FindTaskInCollectionHierarchy(
            ProjectTask task,
            ObservableCollection<ProjectTask> targetCollection)
        {
            var current = task;
            while (current != null)
            {
                if (ReferenceEquals(current.Parent?.Children ?? targetCollection, targetCollection) &&
                    targetCollection.Contains(current))
                {
                    return current;
                }

                current = current.Parent;
            }

            return null;
        }

        private static bool IsTaskInSubtree(ProjectTask root, ProjectTask candidate)
        {
            var current = candidate.Parent;
            while (current != null)
            {
                if (current == root)
                    return true;

                current = current.Parent;
            }

            return false;
        }

        private static ProjectTask? FindPreviousTaskAtLevel(IReadOnlyList<ProjectTask> tasks, int startIndex, int targetLevel)
        {
            for (var index = startIndex; index >= 0; index--)
            {
                var candidate = tasks[index];
                if (candidate.Level == targetLevel)
                    return candidate;
            }

            return null;
        }

        private void RemoveTaskFromCurrentCollection(ProjectTask task)
        {
            if (task.Parent != null)
            {
                task.Parent.Children.Remove(task);
                if (task.Parent.Children.Count == 0)
                    task.Parent.IsSummary = false;
                return;
            }

            Project.Tasks.Remove(task);
        }

        private static void UpdateTaskLevelRecursive(ProjectTask task, int level)
        {
            task.Level = level;
            foreach (var child in task.Children)
            {
                child.Parent = task;
                UpdateTaskLevelRecursive(child, level + 1);
            }
        }

        private static void RecalcSummaryChain(ProjectTask? task)
        {
            var current = task;
            while (current != null)
            {
                current.RecalcSummary();
                current = current.Parent;
            }
        }

        private int GetSprintIndex(DateTime taskStart, DateTime projectStart)
        {
            var sprintDays = Math.Max(1, SprintDurationDays);
            var dayOffset = (taskStart - projectStart).TotalDays;
            return dayOffset < 0 ? -1 : (int)(dayOffset / sprintDays);
        }

        private int GetSprintNumberFromIndex(int sprintIndex)
        {
            var firstSprint = Math.Max(1, FirstSprintNumber);
            return SprintNumberingMode switch
            {
                "Par" => NormalizeParity(firstSprint, even: true) + (sprintIndex * 2),
                "Impar" => NormalizeParity(firstSprint, even: false) + (sprintIndex * 2),
                _ => firstSprint + sprintIndex
            };
        }

        private static int NormalizeParity(int value, bool even)
        {
            var normalized = Math.Max(1, value);
            var isEven = normalized % 2 == 0;
            if (even == isEven)
                return normalized;

            return normalized + 1;
        }
    }
}
