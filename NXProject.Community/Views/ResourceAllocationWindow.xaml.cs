using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NXProject.Models;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Views
{
    public partial class ResourceAllocationWindow : Window, INotifyPropertyChanged
    {
        private readonly MainViewModel _vm;
        private Resource? _selectedResource;
        private SprintColumn? _selectedSprint;
        // Quando marcado, considera Stories com % de conclusão = 0 (e tasks/resumos "New"),
        // para ver a distribuição da sprint planejada.
        private bool _includeZeroPct;

        public ResourceAllocationWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            AvailableResources = _vm.Project.Resources;
            SelectedDetails.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedDetails));
            DataContext = this;
            BuildMatrix();
        }

        // Motor de cálculo compartilhado com a tela Pessoas (mesma base de horas e capacidade).
        private SprintAllocationEngine Engine => new(_vm, _includeZeroPct);

        private List<SprintColumn> BuildSprintColumns() => Engine.BuildSprintColumns();
        private List<Resource> MatrixResources() => Engine.MatrixResources();
        private List<SprintCell> ComputeSprintCells(Resource r, List<SprintColumn> sprints)
            => Engine.ComputeSprintCells(r, sprints);
        private DateTime? GetLastActivityDate(Resource r) => Engine.GetLastActivityDate(r);
        private double SummaryHoursForResource(string name, SprintColumn s)
            => Engine.SummaryHoursForResource(name, s);
        private bool IsChargeableNode(TaskViewModel t) => Engine.IsChargeableNode(t);
        private bool StoryCountsForSummary(TaskViewModel t) => Engine.StoryCountsForSummary(t);
        private IEnumerable<TaskAllocationSummary> ChargeableAllocations(ProjectTask story)
            => Engine.ChargeableAllocations(story);
        private double DecompositionFactor(TaskViewModel t) => Engine.DecompositionFactor(t);
        private double StoryTaskSum(ProjectTask story) => Engine.StoryTaskSum(story);
        private static double StoryTotalHours(ProjectTask story) => SprintAllocationEngine.StoryTotalHours(story);
        private static bool IsStoryNode(TaskViewModel t) => SprintAllocationEngine.IsStoryNode(t);
        private static bool IsLeafTask(TaskViewModel t) => t.Model.Children.Count == 0;
        private static bool OverlapsWithSprint(TaskViewModel t, SprintColumn s)
            => SprintAllocationEngine.OverlapsWithSprint(t, s);
        private static bool BelongsToSprint(TaskViewModel t, SprintColumn s)
            => SprintAllocationEngine.BelongsToSprint(t, s);
        private static double ProportionalHours(ProjectTask task, double hours, SprintColumn s)
            => SprintAllocationEngine.ProportionalHours(task, hours, s);

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<Resource> AvailableResources { get; }
        public ObservableCollection<AllocationDetailRow> SelectedDetails { get; } = new();
        public ObservableCollection<GapJustificationRow> GapJustRows { get; } = new();

        private void OnIncludeZeroPctChanged(object sender, RoutedEventArgs e)
        {
            _includeZeroPct = IncludeZeroPctBox.IsChecked == true;
            BuildMatrix();
            if (_selectedResource != null && _selectedSprint != null)
                ShowDetails(_selectedResource, _selectedSprint);
            if (MainTabControl.SelectedIndex == 1)
                BuildCapacityMatrix();
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            BuildMatrix();
            if (_selectedResource != null && _selectedSprint != null)
                ShowDetails(_selectedResource, _selectedSprint);
            if (MainTabControl.SelectedIndex == 1)
                BuildCapacityMatrix();
            else if (MainTabControl.SelectedIndex == 2)
                BuildGapTimeline();
        }

        private void OnTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            var index = MainTabControl.SelectedIndex;      // 0 = Alocação, 1 = % Capacidade, 2 = Gaps
            var showDetails = index == 0;

            // Colapsar/expandir também a RowDefinition — height fixa não some só com Visibility.Collapsed
            var outerGrid = (Grid)DetailsPanel.Parent;
            outerGrid.RowDefinitions[2].Height = showDetails
                ? new GridLength(220)
                : new GridLength(0);
            DetailsPanel.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;

            if (index == 1)
                BuildCapacityMatrix();
            else if (index == 2)
                BuildGapTimeline();
        }

        private void OnAddResourceClick(object sender, RoutedEventArgs e)
        {
            var name = PromptResourceName();
            if (string.IsNullOrWhiteSpace(name))
                return;

            var normalizedName = NormalizeManualResourceName(name);
            if (string.IsNullOrWhiteSpace(normalizedName))
                return;
            if (AvailableResources.Any(r => string.Equals(r.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    AppStrings.Get("RAlloc_DupNameMsg"),
                    AppStrings.Get("RAlloc_AddResourceTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var nextId = AvailableResources.Select(r => r.Id).DefaultIfEmpty(0).Max() + 1;
            AvailableResources.Add(new Resource
            {
                Id = nextId,
                Name = normalizedName,
                MaxUnitsPerDay = ProjectCalendarService.WorkingHoursPerDay,
                IsImportedFromTfs = false
            });

            _vm.Project.IsDirty = true;
            BuildMatrix();
        }

        private void OnDeleteResourceClick(object sender, RoutedEventArgs e)
        {
            if (_selectedResource == null)
            {
                MessageBox.Show(
                    AppStrings.Get("RAlloc_SelectToDelete"),
                    AppStrings.Get("RAlloc_DeleteResourceTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var resource = _selectedResource;
            var assignmentCount = _vm.FlatTasks.Count(t => t.Model.Resources.Any(r => r.ResourceId == resource.Id));
            var confirm = MessageBox.Show(
                assignmentCount > 0
                    ? AppStrings.Get("RAlloc_DeleteWithAssignments", resource.DisplayName, assignmentCount)
                    : AppStrings.Get("RAlloc_DeleteConfirm", resource.DisplayName),
                AppStrings.Get("RAlloc_DeleteResourceTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;

            foreach (var task in _vm.FlatTasks)
                task.Model.Resources.RemoveAll(r => r.ResourceId == resource.Id);

            AvailableResources.Remove(resource);
            _selectedResource = null;
            SelectedDetails.Clear();
            DetailsTitle.Text = AppStrings.Get("RAlloc_SelectCell");
            _vm.Project.IsDirty = true;
            _vm.RefreshTasks();
            BuildMatrix();
        }

        private void BuildMatrix()
        {
            AllocationGrid.Children.Clear();
            AllocationGrid.RowDefinitions.Clear();
            AllocationGrid.ColumnDefinitions.Clear();

            var sprints = BuildSprintColumns().ToList();
            // col 0 = Recurso, col 1 = Última Ativ., col 2..N = sprints
            AllocationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            AllocationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            foreach (var _ in sprints)
                AllocationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });

            AllocationGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            AddCell(AppStrings.Get("RAlloc_CellResource"), 0, 0, true);
            AddCell(AppStrings.Get("RAlloc_CellRelease"), 0, 1, true);
            for (int i = 0; i < sprints.Count; i++)
                AddSprintHeaderCell(sprints[i], 0, i + 2);

            var resources = MatrixResources();
            for (int row = 0; row < resources.Count; row++)
            {
                var resource = resources[row];
                AllocationGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                AddResourceNameCell(resource, row + 1);

                var lastFinish = GetLastActivityDate(resource);
                AddCell(lastFinish.HasValue ? lastFinish.Value.ToString("dd/MM/yy") : "-", row + 1, 1, false);

                var cells = ComputeSprintCells(resource, sprints);
                for (int col = 0; col < sprints.Count; col++)
                {
                    var cell = cells[col];
                    var isOverAllocated = cell.Hours > cell.CapacityHours + 0.0001;
                    AddHoursButton(resource, sprints[col], cell.Hours, cell.OccupancyPercent,
                                   cell.CapacityHours, isOverAllocated, cell.AfterDeadline,
                                   row + 1, col + 2);
                }
            }
        }

        // Recursos das matrizes = os do projeto + pessoas que só aparecem no resumo de tasks
        // (linhas sintéticas, para que as horas das tasks apareçam para o dono da task).

        // ── Aba "% Capacidade": horas alocadas x capacidade TOTAL da pessoa na sprint ──────
        private void BuildCapacityMatrix()
        {
            CapacityGrid.Children.Clear();
            CapacityGrid.RowDefinitions.Clear();
            CapacityGrid.ColumnDefinitions.Clear();

            var sprints = BuildSprintColumns().ToList();
            CapacityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            foreach (var _ in sprints)
                CapacityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });

            CapacityGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            AddCapacityCell(AppStrings.Get("RAlloc_CellResource"), null, 0, 0, header: true);
            for (int i = 0; i < sprints.Count; i++)
                AddSprintHeaderCell(sprints[i], 0, i + 1, CapacityGrid);

            var resources = MatrixResources();
            for (int row = 0; row < resources.Count; row++)
            {
                var resource = resources[row];
                CapacityGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                AddCapacityCell(resource.Name ?? "", null, row + 1, 0, header: false,
                                align: HorizontalAlignment.Left);

                var cells = ComputeSprintCells(resource, sprints);
                for (int col = 0; col < sprints.Count; col++)
                {
                    var cell = cells[col];
                    if (cell.FullCapacityHours <= 0 && cell.Hours <= 0)
                    {
                        AddCapacityCell("-", null, row + 1, col + 1, header: false);
                        continue;
                    }

                    double pct = cell.OccupancyPercent ?? 0;
                    var tip = AppStrings.Get("RAlloc_CapacityTip", cell.Hours, cell.FullCapacityHours);
                    if (cell.AfterDeadline)
                        tip += " " + AppStrings.Get("RAlloc_AfterDeadlineTip");

                    AddCapacityCell(
                        cell.Hours > 0 ? (cell.AfterDeadline ? $"{pct:0}% ⚠" : $"{pct:0}%") : "-",
                        tip,
                        row + 1, col + 1, header: false, pct: cell.Hours > 0 ? pct : (double?)null);
                }
            }
        }

        private void AddCapacityCell(
            string text, string? tooltip, int row, int col, bool header,
            double? pct = null, HorizontalAlignment align = HorizontalAlignment.Center)
        {
            var background = header
                ? new SolidColorBrush(Color.FromRgb(235, 239, 246))
                : pct switch
                {
                    > 100.0001 => new SolidColorBrush(Color.FromRgb(253, 231, 233)), // vermelho claro
                    > 85       => new SolidColorBrush(Color.FromRgb(255, 244, 226)), // laranja claro
                    > 0        => new SolidColorBrush(Color.FromRgb(233, 243, 253)), // azul claro
                    _          => Brushes.White
                };

            var foreground = pct switch
            {
                > 100.0001 => new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
                > 85       => new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x00)),
                _          => new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))
            };

            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(219, 225, 234)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = background,
                ToolTip = tooltip,
                Child = new TextBlock
                {
                    Text = text,
                    FontWeight = header || pct > 100.0001 ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = foreground,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = align,
                    Margin = new Thickness(8, 0, 8, 0)
                }
            };

            Grid.SetRow(border, row);
            Grid.SetColumn(border, col);
            CapacityGrid.Children.Add(border);
        }


        private void AddCell(
            string text,
            int row,
            int col,
            bool header,
            HorizontalAlignment horizontalAlignment = HorizontalAlignment.Center)
        {
            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(219, 225, 234)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = header
                    ? new SolidColorBrush(Color.FromRgb(235, 239, 246))
                    : Brushes.White,
                Child = new TextBlock
                {
                    Text = text,
                    FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = horizontalAlignment,
                    Margin = new Thickness(8, 0, 8, 0)
                }
            };

            Grid.SetRow(border, row);
            Grid.SetColumn(border, col);
            AllocationGrid.Children.Add(border);
        }

        private void AddSprintHeaderCell(SprintColumn sprint, int row, int col, Grid? target = null)
        {
            var hasDates = sprint.Start != default && sprint.End != default;
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = sprint.Header,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 0)
            });
            if (hasDates)
                content.Children.Add(new TextBlock
                {
                    Text = $"{sprint.Start:dd/MM} – {sprint.End:dd/MM}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0)
                });

            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(219, 225, 234)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = new SolidColorBrush(Color.FromRgb(235, 239, 246)),
                Child = content
            };
            Grid.SetRow(border, row);
            Grid.SetColumn(border, col);
            (target ?? AllocationGrid).Children.Add(border);
        }

        private void AddResourceNameCell(Resource resource, int row)
        {
            bool isInternal = resource.Kind == ResourceKind.Internal;

            var badge = new Border
            {
                Background = new SolidColorBrush(isInternal ? Color.FromRgb(120, 81, 169) : Color.FromRgb(43, 87, 154)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = isInternal ? "INT" : "PRJ",
                    FontSize = 9, FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            var nameText = new TextBlock
            {
                Text = resource.DisplayName,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0) };
            stack.Children.Add(badge);
            stack.Children.Add(nameText);

            var menu = new ContextMenu();
            var toggleItem = new MenuItem
            {
                Header = isInternal ? AppStrings.Get("RAlloc_SwitchToProject") : AppStrings.Get("RAlloc_SwitchToInternal")
            };
            toggleItem.Click += (_, _) =>
            {
                resource.Kind = resource.Kind == ResourceKind.Internal ? ResourceKind.Project : ResourceKind.Internal;
                _vm.Project.IsDirty = true;
                BuildMatrix();
            };
            menu.Items.Add(toggleItem);

            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(219, 225, 234)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = new SolidColorBrush(Color.FromRgb(235, 239, 246)),
                ContextMenu = menu,
                Child = stack
            };

            Grid.SetRow(border, row);
            Grid.SetColumn(border, 0);
            AllocationGrid.Children.Add(border);
        }

        private void AddHoursButton(
            Resource resource,
            SprintColumn sprint,
            double hours,
            double? occupancyPercent,
            double capacityHours,
            bool isOverAllocated,
            bool afterDeadline,
            int row,
            int col)
        {
            var normalForeground = new SolidColorBrush(Color.FromRgb(31, 78, 161));
            var overAllocatedForeground = new SolidColorBrush(Color.FromRgb(178, 34, 34));
            var button = new Button
            {
                Content = hours > 0
                    ? new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = afterDeadline ? $"{hours:0.##} h ⚠" : $"{hours:0.##} h",
                                HorizontalAlignment = HorizontalAlignment.Center,
                                Foreground = isOverAllocated ? overAllocatedForeground : normalForeground
                            },
                            new TextBlock
                            {
                                Text = AppStrings.Get("RAlloc_CellOccupancy", occupancyPercent ?? 0),
                                FontSize = 10,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                Foreground = normalForeground
                            }
                        }
                    }
                    : "-",
                Tag = (resource, sprint),
                BorderThickness = new Thickness(0),
                Background = hours > 0
                    ? isOverAllocated
                        ? new SolidColorBrush(Color.FromRgb(255, 235, 235))
                        : new SolidColorBrush(Color.FromRgb(230, 242, 255))
                    : Brushes.White,
                Foreground = hours > 0
                    ? isOverAllocated ? overAllocatedForeground : normalForeground
                    : new SolidColorBrush(Color.FromRgb(130, 130, 130)),
                FontWeight = hours > 0 ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = hours > 0
                    ? (isOverAllocated
                        ? AppStrings.Get("RAlloc_Overallocated", hours, capacityHours)
                        : AppStrings.Get("RAlloc_ViewActivities", hours, capacityHours))
                      + (afterDeadline ? " " + AppStrings.Get("RAlloc_AfterDeadlineTip") : "")
                    : AppStrings.Get("RAlloc_NoActivities")
            };
            button.Click += OnHoursCellClick;

            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(219, 225, 234)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = button
            };

            Grid.SetRow(border, row);
            Grid.SetColumn(border, col);
            AllocationGrid.Children.Add(border);
        }

        private void OnHoursCellClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ValueTuple<Resource, SprintColumn> tuple })
                ShowDetails(tuple.Item1, tuple.Item2);
        }

        private void ShowDetails(Resource resource, SprintColumn sprint)
        {
            _selectedResource = resource;
            _selectedSprint = sprint;
            DetailsTitle.Text = AppStrings.Get("RAlloc_DetailsTitle", resource.DisplayName, sprint.Header);
            if (DetailsGrid.Columns.Count > 7)
                DetailsGrid.Columns[7].Header = sprint.Header;
            SelectedDetails.Clear();

            var hasDates = sprint.Start != default && sprint.End != default;
            var filter = hasDates
                ? (Func<TaskViewModel, bool>)(t => OverlapsWithSprint(t, sprint))
                : t => BelongsToSprint(t, sprint);

            foreach (var task in _vm.FlatTasks.Where(t => IsChargeableNode(t) && filter(t)))
            {
                var assignment = task.Model.Resources.FirstOrDefault(r => r.ResourceId == resource.Id);
                if (assignment == null)
                    continue;
                SelectedDetails.Add(new AllocationDetailRow(this, task, assignment, resource, sprint));
            }

            // Composição vinda do RESUMO de tasks: stories onde este recurso é dono de tasks
            // (não é o responsável da story). São linhas Tipo=Task, só leitura, com as horas
            // rateadas para a sprint — para o total da célula fechar com a grade.
            if (!string.IsNullOrWhiteSpace(resource.Name))
            {
                foreach (var t in _vm.FlatTasks)
                {
                    if (!StoryCountsForSummary(t) || t.Model.TaskAllocations.Count == 0) continue;
                    if (hasDates ? !OverlapsWithSprint(t, sprint) : !BelongsToSprint(t, sprint)) continue;
                    var alloc = ChargeableAllocations(t.Model).FirstOrDefault(a =>
                        string.Equals(a.Resource, resource.Name, StringComparison.OrdinalIgnoreCase));
                    if (alloc == null) continue;
                    double cut = TaskScheduleService.StoryTaskCutFactor(StoryTotalHours(t.Model), StoryTaskSum(t.Model));
                    double sprintH = hasDates ? ProportionalHours(t.Model, alloc.Hours * cut, sprint) : alloc.Hours * cut;
                    if (sprintH <= 0.0001) continue;
                    SelectedDetails.Add(new AllocationDetailRow(this, t, resource, sprint, alloc.Hours, alloc.Tasks, sprintH));
                }
            }

            // Total da coluna H. Sprint
            var total = SelectedDetails.Sum(r =>
                double.TryParse(r.SprintHours, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.CurrentCulture, out var h) ? h : 0);
            if (SelectedDetails.Count > 0)
            {
                DetailsTotalText.Text = AppStrings.Get("RAlloc_SprintTotal", sprint.Header, total);
                DetailsTotalText.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                DetailsTotalText.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void MoveAssignment(AllocationDetailRow row, Resource newResource)
        {
            var task = row.Task.Model;
            var oldAssignment = row.Assignment;
            if (oldAssignment.ResourceId == newResource.Id)
                return;

            task.Resources.Remove(oldAssignment);
            var existing = task.Resources.FirstOrDefault(r => r.ResourceId == newResource.Id);
            if (existing != null)
            {
                existing.EstimatedHours = (existing.EstimatedHours ?? 0) + row.Hours;
                existing.AllocationPercent = Math.Max(existing.AllocationPercent, oldAssignment.AllocationPercent);
                existing.Resource = newResource;
            }
            else
            {
                task.Resources.Add(new TaskResource
                {
                    ResourceId = newResource.Id,
                    Resource = newResource,
                    AllocationPercent = oldAssignment.AllocationPercent,
                    EstimatedHours = oldAssignment.EstimatedHours
                });
            }

            TaskScheduleService.RecalculateFinishFromAssignments(task);
            RecalcSummaryChain(task.Parent);
            _vm.Project.IsDirty = true;
            _vm.RefreshTasks();
            BuildMatrix();
            if (_selectedResource != null && _selectedSprint != null)
                ShowDetails(_selectedResource, _selectedSprint);
        }


        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));


        private void OnDetailsResourceComboDropDownClosed(object? sender, EventArgs e)
        {
            if (sender is not ComboBox { DataContext: AllocationDetailRow row } combo)
                return;

            var selected = combo.SelectedItem as Resource;
            var typed = NormalizeManualResourceName(combo.Text);

            if (selected == null)
            {
                if (string.IsNullOrEmpty(typed))
                    return;

                var existing = AvailableResources.FirstOrDefault(r => string.Equals(r.Name, typed, StringComparison.OrdinalIgnoreCase));
                Resource res;
                if (existing == null)
                {
                    var nextId = AvailableResources.Select(r => r.Id).DefaultIfEmpty(0).Max() + 1;
                    res = new Resource
                    {
                        Id = nextId,
                        Name = typed,
                        MaxUnitsPerDay = ProjectCalendarService.WorkingHoursPerDay,
                        IsImportedFromTfs = false
                    };
                    AvailableResources.Add(res);
                }
                else
                {
                    res = existing;
                }

                row.Resource = res;
            }
            else
            {
                row.Resource = selected;
            }
        }

        private void OnDetailsResourceComboKeyDown(object? sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            if (sender is not ComboBox) return;
            OnDetailsResourceComboDropDownClosed(sender, EventArgs.Empty);
        }

        public sealed class AllocationDetailRow : INotifyPropertyChanged
        {
            private readonly ResourceAllocationWindow _owner;
            private Resource _resource;
            private readonly SprintColumn _sprint;
            private readonly double _summaryHours;
            private readonly double _summarySprintHours;

            internal AllocationDetailRow(
                ResourceAllocationWindow owner,
                TaskViewModel task,
                TaskResource assignment,
                Resource resource,
                SprintColumn sprint)
            {
                _owner = owner;
                Task = task;
                Assignment = assignment;
                _resource = resource;
                _sprint = sprint;
            }

            // Linha vinda do RESUMO de tasks (Tipo=Task, só leitura).
            internal AllocationDetailRow(
                ResourceAllocationWindow owner,
                TaskViewModel story,
                Resource resource,
                SprintColumn sprint,
                double summaryHours,
                int summaryTasks,
                double summarySprintHours)
            {
                _owner = owner;
                Task = story;
                _resource = resource;
                _sprint = sprint;
                IsSummary = true;
                _summaryHours = summaryHours;
                SummaryTasks = summaryTasks;
                _summarySprintHours = summarySprintHours;
                Assignment = new TaskResource { ResourceId = resource.Id, EstimatedHours = summaryHours };
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public TaskViewModel Task { get; }
            public TaskResource Assignment { get; }
            public bool IsSummary { get; }
            public int SummaryTasks { get; }
            // Tipo mostrado na coluna: linhas de resumo são "Task"; senão a tag do work item.
            public string TypeLabel => IsSummary ? AppStrings.Get("PMap_SrTypeTask") : Task.DevOpsTag;
            public bool IsEditable => !IsSummary;
            public double Hours
            {
                get => IsSummary ? _summaryHours : TaskScheduleService.GetAssignmentHours(Task.Model, Assignment);
                set
                {
                    if (IsSummary) return;   // resumo é só leitura
                    var normalized = double.IsNaN(value) || value < 0 ? 0 : value;
                    Assignment.EstimatedHours = normalized;
                    TaskScheduleService.RecalculateFinishFromAssignments(Task.Model);
                    RecalcSummaryChain(Task.Model.Parent);
                    _owner._vm.Project.IsDirty = true;
                    _owner._vm.RefreshTasks();
                    _owner.BuildMatrix();
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hours)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SprintHours)));
                }
            }

            public string SprintHours =>
                IsSummary
                    ? (_summarySprintHours > 0 ? $"{_summarySprintHours:0.##}" : "-")
                    : ProportionalHours(Task.Model, TaskScheduleService.GetAssignmentHours(Task.Model, Assignment), _sprint)
                        * _owner.DecompositionFactor(Task)
                        is var h && h > 0 ? $"{h:0.##}" : "-";

            public double AllocationPercent
            {
                get => IsSummary ? 100 : TaskScheduleService.NormalizeAllocationPercent(Assignment.AllocationPercent);
                set
                {
                    if (IsSummary) return;   // resumo é só leitura
                    Assignment.AllocationPercent = TaskScheduleService.NormalizeAllocationPercent(value);
                    TaskScheduleService.RecalculateFinishFromAssignments(Task.Model);
                    RecalcSummaryChain(Task.Model.Parent);
                    _owner._vm.Project.IsDirty = true;
                    _owner._vm.RefreshTasks();
                    _owner.BuildMatrix();
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AllocationPercent)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hours)));
                }
            }

            public Resource Resource
            {
                get => _resource;
                set
                {
                    if (IsSummary) return;   // resumo é só leitura (não move recurso)
                    if (value == null || value.Id == _resource.Id)
                        return;

                    _resource = value;
                    _owner.MoveAssignment(this, value);
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Resource)));
                }
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

        private string? PromptResourceName()
        {
            var dialog = new Window
            {
                Title = AppStrings.Get("RAlloc_AddResourceTitle"),
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Width = 360,
                Height = 150,
                Background = Brushes.White
            };

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = AppStrings.Get("RAlloc_ResourceNameLabel"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(label, 0);
            root.Children.Add(label);

            var textBox = new TextBox
            {
                MinWidth = 300,
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid.SetRow(textBox, 1);
            root.Children.Add(textBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var ok = new Button { Content = "OK", Width = 82, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = AppStrings.Get("RAlloc_Cancel"), Width = 82, IsCancel = true };
            ok.Click += (_, _) =>
            {
                dialog.DialogResult = true;
                dialog.Close();
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            dialog.Content = root;
            textBox.Focus();
            return dialog.ShowDialog() == true ? textBox.Text : null;
        }

        private static string NormalizeManualResourceName(string? name) =>
            (name ?? string.Empty).Trim().TrimStart('*').Trim();

        // ── Gap / Timeline ───────────────────────────────────────────────────

        private sealed class GapBarTag
        {
            public Resource Resource { get; }
            public DateTime GapStart { get; }
            public DateTime GapEnd { get; }
            public int WorkDays { get; }
            public GapBarTag(Resource r, DateTime s, DateTime e, int wd)
            { Resource = r; GapStart = s; GapEnd = e; WorkDays = wd; }
        }

        private void OnGapBarClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: GapBarTag tag }) return;

            var res = tag.Resource;
            var gapStart = tag.GapStart;
            var gapEnd = tag.GapEnd;
            var workDays = tag.WorkDays;

            // find or create justification in the project model
            var just = _vm.Project.GapJustifications
                .FirstOrDefault(j => j.ResourceId == res.Id
                                     && j.GapStart == gapStart
                                     && j.GapEnd == gapEnd);
            if (just == null)
            {
                just = new GapJustification { ResourceId = res.Id, GapStart = gapStart, GapEnd = gapEnd };
                _vm.Project.GapJustifications.Add(just);
            }

            // open inline dialog to enter/edit justification
            var text = PromptJustification(res.DisplayName, gapStart, gapEnd, workDays, just.Justification);
            if (text == null) return; // cancelled

            just.Justification = text;
            _vm.Project.IsDirty = true;

            // rebuild list and timeline to reflect new state
            BuildGapTimeline();

            // select the row in the grid
            var row = GapJustRows.FirstOrDefault(r => r.Model == just);
            if (row != null)
            {
                GapJustGrid.SelectedItem = row;
                GapJustGrid.ScrollIntoView(row);
            }
        }

        private string? PromptJustification(string resourceName, DateTime gapStart, DateTime gapEnd,
            int workDays, string currentText)
        {
            var dlg = new Window
            {
                Title = "Justificar gap",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Width = 420,
                Height = 230,
                Background = Brushes.White
            };

            var root = new Grid { Margin = new Thickness(16) };
            for (int i = 0; i < 4; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = AppStrings.Get("RAlloc_GapHeader", resourceName, gapStart, gapEnd,
                    AppStrings.Get(workDays != 1 ? "RAlloc_WorkDayPlural" : "RAlloc_WorkDaySingular", workDays)),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 78, 161)),
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var label = new TextBlock { Text = AppStrings.Get("RAlloc_JustificationLabel"), Margin = new Thickness(0, 0, 0, 4) };
            Grid.SetRow(label, 1);
            root.Children.Add(label);

            var textBox = new TextBox
            {
                Text = currentText,
                MinHeight = 64,
                MaxHeight = 64,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(textBox, 2);
            root.Children.Add(textBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var ok = new Button { Content = AppStrings.Get("RAlloc_Save"), Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = AppStrings.Get("RAlloc_Cancel"), Width = 90, IsCancel = true };
            ok.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            dlg.Content = root;
            textBox.Focus();
            textBox.SelectAll();

            return dlg.ShowDialog() == true ? textBox.Text.Trim() : null;
        }

        private void BuildGapTimeline()
        {
            GapCanvas.Children.Clear();

            // Sync GapJustRows from project model (rebuild without losing justification text)
            GapJustRows.Clear();
            foreach (var j in _vm.Project.GapJustifications)
            {
                var res = _vm.Project.Resources.FirstOrDefault(r => r.Id == j.ResourceId);
                if (res == null) continue;
                int wd = ProjectCalendarService.CountWorkingDays(j.GapStart, j.GapEnd);
                GapJustRows.Add(new GapJustificationRow(j, res.DisplayName, wd, () =>
                {
                    _vm.Project.IsDirty = true;
                    BuildGapTimeline();
                }));
            }

            var resources = _vm.Project.Resources.OrderBy(r => r.Name).ToList();
            var allLeaf = _vm.FlatTasks.Where(IsLeafTask).ToList();
            if (allLeaf.Count == 0 || resources.Count == 0) return;

            var minDate = allLeaf.Min(t => t.Model.Start).Date;
            var maxDate = allLeaf.Max(t => t.Model.Finish).Date;
            if (maxDate <= minDate) return;

            const double leftCol = 250;
            const double rowH = 46;
            const double headerH = 34;
            const double barPad = 7;
            const double pxPerDay = 14;

            double totalDays = (maxDate - minDate).TotalDays + 2;
            double timelineW = totalDays * pxPerDay;
            double canvasW = leftCol + timelineW + 24;
            double canvasH = headerH + resources.Count * rowH + 4;

            GapCanvas.Width = canvasW;
            GapCanvas.Height = canvasH;

            // ── Background
            var bg = new Rectangle { Width = canvasW, Height = canvasH, Fill = Brushes.White };
            Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0);
            GapCanvas.Children.Add(bg);

            // ── Week gridlines + date labels
            var d = minDate;
            // align to nearest Monday on or before minDate
            while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(-1);
            while (d <= maxDate.AddDays(7))
            {
                double x = leftCol + (d - minDate).TotalDays * pxPerDay;
                if (x >= leftCol)
                {
                    var line = new Line
                    {
                        X1 = x, Y1 = headerH - 6,
                        X2 = x, Y2 = canvasH,
                        Stroke = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                        StrokeThickness = 1
                    };
                    GapCanvas.Children.Add(line);

                    var lbl = new TextBlock
                    {
                        Text = d.ToString("dd/MM"),
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120))
                    };
                    Canvas.SetLeft(lbl, x + 2);
                    Canvas.SetTop(lbl, 6);
                    GapCanvas.Children.Add(lbl);
                }
                d = d.AddDays(7);
            }

            // ── Today line
            if (DateTime.Today >= minDate && DateTime.Today <= maxDate)
            {
                double todayX = leftCol + (DateTime.Today - minDate).TotalDays * pxPerDay;
                var todayLine = new Line
                {
                    X1 = todayX, Y1 = 0,
                    X2 = todayX, Y2 = canvasH,
                    Stroke = new SolidColorBrush(Color.FromRgb(229, 57, 53)),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 5, 3 }
                };
                GapCanvas.Children.Add(todayLine);

                var todayLbl = new TextBlock
                {
                    Text = AppStrings.Get("RAlloc_TodayMark"),
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(229, 57, 53)),
                    FontWeight = FontWeights.SemiBold
                };
                Canvas.SetLeft(todayLbl, todayX + 2);
                Canvas.SetTop(todayLbl, 4);
                GapCanvas.Children.Add(todayLbl);
            }

            // ── Per-resource rows
            for (int ri = 0; ri < resources.Count; ri++)
            {
                var resource = resources[ri];
                double rowY = headerH + ri * rowH;

                // Row background (alternating)
                var rowBg = new Rectangle
                {
                    Width = canvasW,
                    Height = rowH,
                    Fill = ri % 2 == 0
                        ? new SolidColorBrush(Color.FromRgb(250, 251, 253))
                        : Brushes.White
                };
                Canvas.SetLeft(rowBg, 0); Canvas.SetTop(rowBg, rowY);
                GapCanvas.Children.Add(rowBg);

                // Bottom border of row
                var rowLine = new Line
                {
                    X1 = 0, Y1 = rowY + rowH,
                    X2 = canvasW, Y2 = rowY + rowH,
                    Stroke = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                    StrokeThickness = 1
                };
                GapCanvas.Children.Add(rowLine);

                // Resource name cell
                var nameBorder = new Border
                {
                    Width = leftCol - 1,
                    Height = rowH,
                    Background = new SolidColorBrush(Color.FromRgb(235, 239, 246)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(210, 218, 230)),
                    BorderThickness = new Thickness(0, 0, 1, 0),
                    Child = new TextBlock
                    {
                        Text = resource.DisplayName,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(8, 0, 8, 0),
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 12
                    }
                };
                Canvas.SetLeft(nameBorder, 0); Canvas.SetTop(nameBorder, rowY);
                GapCanvas.Children.Add(nameBorder);

                // Tasks for this resource
                var resTasks = allLeaf
                    .Where(t => t.Model.Resources.Any(r => r.ResourceId == resource.Id))
                    .OrderBy(t => t.Model.Start)
                    .ToList();

                if (resTasks.Count == 0) continue;

                // Task bars (blue)
                foreach (var task in resTasks)
                {
                    double barX = leftCol + (task.Model.Start.Date - minDate).TotalDays * pxPerDay;
                    double barW = Math.Max(6, (task.Model.Finish.Date - task.Model.Start.Date).TotalDays * pxPerDay);
                    var taskBar = new Border
                    {
                        Width = barW,
                        Height = rowH - barPad * 2,
                        Background = new SolidColorBrush(Color.FromRgb(74, 144, 217)),
                        CornerRadius = new CornerRadius(3),
                        ToolTip = $"{task.Model.Name}\n{task.Model.Start:dd/MM/yy} → {task.Model.Finish:dd/MM/yy}"
                    };
                    Canvas.SetLeft(taskBar, barX);
                    Canvas.SetTop(taskBar, rowY + barPad);
                    GapCanvas.Children.Add(taskBar);
                }

                // Gap bars (orange) — between merged task intervals
                var intervals = MergeIntervals(
                    resTasks.Select(t => (t.Model.Start.Date, t.Model.Finish.Date)).ToList());

                for (int i = 0; i < intervals.Count - 1; i++)
                {
                    var gapStart = intervals[i].End.AddDays(1);
                    var gapEnd = intervals[i + 1].Start.AddDays(-1);
                    if (gapEnd < gapStart) continue;

                    int workDays = ProjectCalendarService.CountWorkingDays(gapStart, gapEnd);
                    if (workDays <= 0) continue;

                    bool hasJust = _vm.Project.GapJustifications.Any(j =>
                        j.ResourceId == resource.Id && j.GapStart == gapStart && j.GapEnd == gapEnd
                        && !string.IsNullOrWhiteSpace(j.Justification));

                    double gapX = leftCol + (gapStart - minDate).TotalDays * pxPerDay;
                    double gapW = Math.Max(8, (gapEnd - gapStart).TotalDays * pxPerDay + pxPerDay);

                    var gapFill = hasJust
                        ? new SolidColorBrush(Color.FromRgb(123, 94, 167))
                        : new SolidColorBrush(Color.FromRgb(245, 166, 35));

                    var capturedRes = resource;
                    var capturedStart = gapStart;
                    var capturedEnd = gapEnd;
                    var capturedWd = workDays;

                    var gapButton = new Button
                    {
                        Width = gapW,
                        Height = rowH - barPad * 2,
                        Background = gapFill,
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(2, 0, 2, 0),
                        Content = new TextBlock
                        {
                            Text = $"{workDays}d",
                            FontSize = 9,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = Brushes.White,
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Center
                        },
                        Tag = new GapBarTag(capturedRes, capturedStart, capturedEnd, capturedWd),
                        ToolTip = hasJust
                            ? AppStrings.Get("RAlloc_GapTipJustified", gapStart, gapEnd, workDays)
                            : AppStrings.Get("RAlloc_GapTipUnjustified", gapStart, gapEnd, workDays),
                        Cursor = System.Windows.Input.Cursors.Hand
                    };
                    gapButton.Click += OnGapBarClick;

                    Canvas.SetLeft(gapButton, gapX);
                    Canvas.SetTop(gapButton, rowY + barPad);
                    GapCanvas.Children.Add(gapButton);
                }
            }

            // ── Left column top-left header cell
            var headerCorner = new Border
            {
                Width = leftCol - 1,
                Height = headerH,
                Background = new SolidColorBrush(Color.FromRgb(235, 239, 246)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(210, 218, 230)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = AppStrings.Get("RAlloc_ColResource"),
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                }
            };
            Canvas.SetLeft(headerCorner, 0); Canvas.SetTop(headerCorner, 0);
            GapCanvas.Children.Add(headerCorner);
        }

        public sealed class GapJustificationRow : INotifyPropertyChanged
        {
            private readonly Action _onChanged;

            public GapJustificationRow(GapJustification model, string resourceName, int workDays, Action onChanged)
            {
                Model = model;
                ResourceName = resourceName;
                WorkDays = workDays;
                _onChanged = onChanged;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public GapJustification Model { get; }
            public string ResourceName { get; }
            public int WorkDays { get; }
            public string StartDisplay => Model.GapStart.ToString("dd/MM/yy");
            public string EndDisplay => Model.GapEnd.ToString("dd/MM/yy");

            public string Justification
            {
                get => Model.Justification;
                set
                {
                    if (Model.Justification == value) return;
                    Model.Justification = value ?? string.Empty;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Justification)));
                    _onChanged();
                }
            }
        }

        private static List<(DateTime Start, DateTime End)> MergeIntervals(
            List<(DateTime Start, DateTime End)> intervals)
        {
            if (intervals.Count == 0) return intervals;
            var sorted = intervals.OrderBy(i => i.Start).ToList();
            var merged = new List<(DateTime Start, DateTime End)> { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                var last = merged[^1];
                if (sorted[i].Start <= last.End.AddDays(1))
                    merged[^1] = (last.Start, sorted[i].End > last.End ? sorted[i].End : last.End);
                else
                    merged.Add(sorted[i]);
            }
            return merged;
        }
    }
}
