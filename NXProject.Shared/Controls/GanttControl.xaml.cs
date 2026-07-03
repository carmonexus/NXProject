using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Controls
{
    public partial class GanttControl : UserControl
    {
        public static readonly DependencyProperty TasksProperty =
            DependencyProperty.Register(nameof(Tasks), typeof(ObservableCollection<TaskViewModel>),
                typeof(GanttControl), new PropertyMetadata(null, OnTasksChanged));

        public static readonly DependencyProperty ProjectStartProperty =
            DependencyProperty.Register(nameof(ProjectStart), typeof(DateTime),
                typeof(GanttControl), new PropertyMetadata(DateTime.Today, OnLayoutChanged));

        public static readonly DependencyProperty ZoomLevelProperty =
            DependencyProperty.Register(nameof(ZoomLevel), typeof(string),
                typeof(GanttControl), new PropertyMetadata("Semana", OnLayoutChanged));

        public static readonly DependencyProperty SprintDurationDaysProperty =
            DependencyProperty.Register(nameof(SprintDurationDays), typeof(int),
                typeof(GanttControl), new PropertyMetadata(14, OnLayoutChanged));

        public static readonly DependencyProperty FirstSprintNumberProperty =
            DependencyProperty.Register(nameof(FirstSprintNumber), typeof(int),
                typeof(GanttControl), new PropertyMetadata(1, OnLayoutChanged));

        public static readonly DependencyProperty SprintNumberingModeProperty =
            DependencyProperty.Register(nameof(SprintNumberingMode), typeof(string),
                typeof(GanttControl), new PropertyMetadata("Sequencial", OnLayoutChanged));

        public static readonly DependencyProperty SprintsProperty =
            DependencyProperty.Register(nameof(Sprints), typeof(ObservableCollection<NXProject.Models.Sprint>),
                typeof(GanttControl), new PropertyMetadata(null, OnSprintsChanged));

        public static readonly DependencyProperty HeaderHeightProperty =
            DependencyProperty.Register(nameof(HeaderHeight), typeof(double),
                typeof(GanttControl), new PropertyMetadata(40.0, OnLayoutChanged));

        public static readonly DependencyProperty SelectedTaskProperty =
            DependencyProperty.Register(nameof(SelectedTask), typeof(TaskViewModel),
                typeof(GanttControl), new PropertyMetadata(null, OnSelectedTaskChanged));

        public static readonly DependencyProperty ShowDayHeaderProperty =
            DependencyProperty.Register(nameof(ShowDayHeader), typeof(bool),
                typeof(GanttControl), new PropertyMetadata(false, OnLayoutChanged));

        public static readonly DependencyProperty DayHeaderModeProperty =
            DependencyProperty.Register(nameof(DayHeaderMode), typeof(int),
                typeof(GanttControl), new PropertyMetadata(0, OnLayoutChanged));

        public static readonly DependencyProperty CriticalTaskIdsProperty =
            DependencyProperty.Register(nameof(CriticalTaskIds), typeof(HashSet<int>),
                typeof(GanttControl), new PropertyMetadata(null, OnLayoutChanged));

        public static readonly DependencyProperty CriticalPathRiskTaskIdsProperty =
            DependencyProperty.Register(nameof(CriticalPathRiskTaskIds), typeof(HashSet<int>),
                typeof(GanttControl), new PropertyMetadata(null, OnLayoutChanged));

        public static readonly DependencyProperty ShowCriticalPathProperty =
            DependencyProperty.Register(nameof(ShowCriticalPath), typeof(bool),
                typeof(GanttControl), new PropertyMetadata(false, OnLayoutChanged));

        private const double RowHeight = 22;
        private const double BarPadding = 4;
        private const double LeftPadding = 16;
        private const double DependencyMargin = 8;
        private const double MagnifierSize = 220;
        private const double MagnifierSourceSize = 110;
        private const double MagnifierOffset = 18;

        private bool _renderScheduled;
        private bool _resetScrollOnNextRender;
        private bool _suppressScrollNotification;
        private IReadOnlyList<double>? _rowTops;
        private TaskDragState? _dragState;
        private TaskResizeState? _resizeState;
        private bool _magnifierEnabled = false;
        private string? _blockedDragReason;

        public bool MagnifierEnabled
        {
            get => _magnifierEnabled;
            set
            {
                _magnifierEnabled = value;
                SetToolTipsEnabled(GanttCanvas, !value);
                if (value)
                    CloseToolTips(GanttCanvas);
                else
                    HideMagnifier();
            }
        }

        public event Action<TaskViewModel>? TaskClicked;
        public event Action<double>? VerticalScrollChanged;

        public GanttControl()
        {
            InitializeComponent();
            SizeChanged += (_, _) => ScheduleRender();
        }

        public double HeaderHeight
        {
            get => (double)GetValue(HeaderHeightProperty);
            set => SetValue(HeaderHeightProperty, value);
        }

        public ObservableCollection<TaskViewModel>? Tasks
        {
            get => (ObservableCollection<TaskViewModel>?)GetValue(TasksProperty);
            set => SetValue(TasksProperty, value);
        }

        public DateTime ProjectStart
        {
            get => (DateTime)GetValue(ProjectStartProperty);
            set => SetValue(ProjectStartProperty, value);
        }

        public string ZoomLevel
        {
            get => (string)GetValue(ZoomLevelProperty);
            set => SetValue(ZoomLevelProperty, value);
        }

        public int SprintDurationDays
        {
            get => (int)GetValue(SprintDurationDaysProperty);
            set => SetValue(SprintDurationDaysProperty, value);
        }

        public int FirstSprintNumber
        {
            get => (int)GetValue(FirstSprintNumberProperty);
            set => SetValue(FirstSprintNumberProperty, value);
        }

        public string SprintNumberingMode
        {
            get => (string)GetValue(SprintNumberingModeProperty);
            set => SetValue(SprintNumberingModeProperty, value);
        }

        public ObservableCollection<NXProject.Models.Sprint>? Sprints
        {
            get => (ObservableCollection<NXProject.Models.Sprint>?)GetValue(SprintsProperty);
            set => SetValue(SprintsProperty, value);
        }

        public TaskViewModel? SelectedTask
        {
            get => (TaskViewModel?)GetValue(SelectedTaskProperty);
            set => SetValue(SelectedTaskProperty, value);
        }

        public bool ShowDayHeader
        {
            get => (bool)GetValue(ShowDayHeaderProperty);
            set => SetValue(ShowDayHeaderProperty, value);
        }

        // 0 = off, 1 = day1 (seg/qua/sex), 2 = day2 (dígito por dia)
        public int DayHeaderMode
        {
            get => (int)GetValue(DayHeaderModeProperty);
            set
            {
                SetValue(DayHeaderModeProperty, value);
                SetValue(ShowDayHeaderProperty, value > 0);
            }
        }

        // IDs de tarefas no caminho crítico (calculados em CriticalPathService)
        public HashSet<int>? CriticalTaskIds
        {
            get => (HashSet<int>?)GetValue(CriticalTaskIdsProperty);
            set => SetValue(CriticalTaskIdsProperty, value);
        }

        public HashSet<int>? CriticalPathRiskTaskIds
        {
            get => (HashSet<int>?)GetValue(CriticalPathRiskTaskIdsProperty);
            set => SetValue(CriticalPathRiskTaskIdsProperty, value);
        }

        public bool ShowCriticalPath
        {
            get => (bool)GetValue(ShowCriticalPathProperty);
            set => SetValue(ShowCriticalPathProperty, value);
        }

        // IDs de tarefas destacadas (predecessoras da task selecionada via botão)
        public HashSet<int> HighlightedPredecessorIds { get; } = new();

        public void HighlightPredecessors(IEnumerable<int> ids)
        {
            HighlightedPredecessorIds.Clear();
            foreach (var id in ids)
                HighlightedPredecessorIds.Add(id);
            ForceRender();
        }

        private double DayWidth => ZoomLevel switch
        {
            "Dia"        => 22.0,
            "Semana"     => 14.0,
            "Sprint"     => 10.0,
            "Mês"        => 7.0,
            "Trimestre"  => 3.5,
            "Semestre"   => 1.8,
            _            => 14.0
        };

        public void SetHeaderHeight(double height)
        {
            HeaderRow.Height = new GridLength(height);
            HeaderHeight = height;
        }

        public void SetRowTops(IReadOnlyList<double> rowTops)
        {
            _rowTops = rowTops;
            ScheduleRender();
        }

        public void SyncVerticalOffset(double offset)
        {
            var wasSuppressed = _suppressScrollNotification;
            _suppressScrollNotification = true;
            try
            {
                GanttScroll.ScrollToVerticalOffset(offset);
            }
            finally
            {
                _suppressScrollNotification = wasSuppressed;
            }
        }

        public void ScrollToProjectStart() => ScheduleRender(resetScroll: true);

        public void ForceRender()
        {
            _renderScheduled = false;
            ScheduleRender();
        }

        private void ScheduleRender(bool resetScroll = false)
        {
            if (resetScroll) _resetScrollOnNextRender = true;
            if (_renderScheduled) return;

            _renderScheduled = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                _renderScheduled = false;
                Render();
            });
        }

        private static void OnTasksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (GanttControl)d;
            if (e.OldValue is ObservableCollection<TaskViewModel> old)
            {
                old.CollectionChanged -= ctrl.OnCollectionChanged;
                ctrl.UnsubscribeTaskEvents(old);
            }
            if (e.NewValue is ObservableCollection<TaskViewModel> nw)
            {
                nw.CollectionChanged += ctrl.OnCollectionChanged;
                ctrl.SubscribeTaskEvents(nw);
            }

            ctrl.ScheduleRender(resetScroll: true);
        }

        private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((GanttControl)d).ScheduleRender();

        private static void OnSprintsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (GanttControl)d;
            if (e.OldValue is ObservableCollection<NXProject.Models.Sprint> old)
                old.CollectionChanged -= ctrl.OnSprintsCollectionChanged;
            if (e.NewValue is ObservableCollection<NXProject.Models.Sprint> nw)
                nw.CollectionChanged += ctrl.OnSprintsCollectionChanged;
            ctrl.ScheduleRender();
        }

        private void OnSprintsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => ScheduleRender();

        private static void OnSelectedTaskChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((GanttControl)d).ScheduleRender();

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (var item in e.OldItems)
                    if (item is TaskViewModel task)
                        task.PropertyChanged -= OnTaskPropertyChanged;

            if (e.NewItems != null)
                foreach (var item in e.NewItems)
                    if (item is TaskViewModel task)
                        task.PropertyChanged += OnTaskPropertyChanged;

            if (e.Action == NotifyCollectionChangedAction.Reset && sender is ObservableCollection<TaskViewModel> tasks)
            {
                UnsubscribeTaskEvents(tasks);
                SubscribeTaskEvents(tasks);
            }

            // RebuildFlatTasks limpa e repopula a mesma coleção. Resetar o scroll aqui
            // faz o Gantt ir ao topo e sincronizar esse salto com a grade antes de voltar
            // para a tarefa selecionada. A troca completa de projeto continua resetando
            // pelo OnTasksChanged; mudanças na coleção preservam a posição atual.
            ScheduleRender();
        }

        private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            ScheduleRender();
        }

        private void OnCanvasMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source)
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    var dragTask = FindDragTaskFromVisual(source);
                    if (dragTask != null)
                    {
                        bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                        if (!ctrlHeld)
                        {
                            _blockedDragReason = "Segure Ctrl ao clicar para arrastar a atividade.";
                        }
                        else if (dragTask.IsSummary)
                        {
                            _blockedDragReason = "Sumários não podem ser arrastados diretamente.";
                        }
                        else if (dragTask.Start.Date < DateTime.Today)
                        {
                            _blockedDragReason = "Atividades já iniciadas não podem ser movidas.";
                        }
                        else
                        {
                            _blockedDragReason = null;
                            _dragState = new TaskDragState(
                                dragTask,
                                e.GetPosition(GanttCanvas),
                                dragTask.Start,
                                dragTask.DurationDays);

                            TaskClicked?.Invoke(dragTask);
                            GanttCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                    }
                }


                var clickedTask = FindTaskFromVisual(source);
                if (clickedTask != null)
                {
                    TaskClicked?.Invoke(clickedTask);
                    e.Handled = true;
                    return;
                }
            }

            var pos = e.GetPosition(GanttCanvas);
            var clickY = pos.Y;
            if (Tasks == null || Tasks.Count == 0) return;

            if (_rowTops != null)
            {
                for (int i = 0; i < _rowTops.Count && i < Tasks.Count; i++)
                {
                    var rowTop = _rowTops[i];
                    var rowBottom = i + 1 < _rowTops.Count ? _rowTops[i + 1] : rowTop + RowHeight;
                    if (clickY >= rowTop && clickY < rowBottom)
                    {
                        TaskClicked?.Invoke(Tasks[i]);
                        e.Handled = true;
                        return;
                    }
                }
                return;
            }

            var clickedIndex = (int)(clickY / RowHeight);
            if (clickedIndex >= 0 && clickedIndex < Tasks.Count)
            {
                TaskClicked?.Invoke(Tasks[clickedIndex]);
                e.Handled = true;
            }
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            // Atualiza coordenada de data no overlay do cabeçalho
            var pos = e.GetPosition(GanttCanvas);
            UpdateMagnifier(e, pos);

            var dayIndex = (int)Math.Floor((pos.X - LeftPadding) / DayWidth);
            if (dayIndex >= 0 && ProjectStart != default)
            {
                var hoverDate = ProjectStart.AddDays(dayIndex);

                if (_magnifierEnabled && Tasks != null)
                {
                    // Lupa ativa: mostra nome + duração da task sob o cursor no label de data
                    TaskViewModel? hoverTask = null;
                    if (_rowTops != null)
                    {
                        for (int i = 0; i < _rowTops.Count && i < Tasks.Count; i++)
                        {
                            var bot = i + 1 < _rowTops.Count ? _rowTops[i + 1] : _rowTops[i] + RowHeight;
                            if (pos.Y >= _rowTops[i] && pos.Y < bot) { hoverTask = Tasks[i]; break; }
                        }
                    }
                    else
                    {
                        var idx = (int)(pos.Y / RowHeight);
                        if (idx >= 0 && idx < Tasks.Count) hoverTask = Tasks[idx];
                    }

                    DateCoordLabel.Text = hoverTask != null
                        ? $"{hoverDate:ddd, dd/MM/yyyy}  |  {hoverTask.Name}  " +
                          $"{hoverTask.Model.Start:dd/MM/yy}–{ProjectCalendarService.GetInclusiveFinishDate(hoverTask.Model.Start, hoverTask.Model.Finish):dd/MM/yy}  " +
                          $"{hoverTask.DurationHours:0.#}h  {hoverTask.PercentComplete:0.#}%"
                        : hoverDate.ToString("ddd, dd/MM/yyyy", CultureInfo.CurrentCulture);
                }
                else
                {
                    DateCoordLabel.Text = hoverDate.ToString("ddd, dd/MM/yyyy", CultureInfo.CurrentCulture);
                }

                // Posiciona o overlay próximo ao cursor, dentro dos limites do cabeçalho
                double labelX = pos.X - LeftPadding < 10 ? 4
                              : Math.Min(pos.X - 10, ActualWidth - 140);
                DateCoordBorder.Margin = new Thickness(labelX, 0, 0, 0);
                DateCoordBorder.Visibility = Visibility.Visible;
            }

            // Resize de data fim (botão direito + arrastar)
            if (_resizeState != null)
            {
                if (e.RightButton != MouseButtonState.Pressed)
                {
                    // Botão solto fora do MouseUp — cancela sem fixar
                    _resizeState = null;
                    if (Mouse.Captured == GanttCanvas) Mouse.Capture(null);
                    GanttCanvas.Cursor = null;
                    return;
                }

                var rPos = e.GetPosition(GanttCanvas);
                var finishDayIndex = (int)Math.Round((rPos.X - LeftPadding) / DayWidth);
                var inclusiveFinish = ProjectStart.AddDays(Math.Max(finishDayIndex, 0));
                var newFinish = inclusiveFinish.Date.AddDays(1);
                // Finish deve ser pelo menos o dia seguinte ao Start
                if (newFinish <= _resizeState.Task.Start)
                    newFinish = _resizeState.Task.Start.AddDays(1);
                if (newFinish != _resizeState.Task.Finish)
                    _resizeState.Task.Finish = newFinish;

                e.Handled = true;
                return;
            }

            if (_dragState == null)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
                return;
            }

            var currentPosition = e.GetPosition(GanttCanvas);
            var dayDelta = (int)Math.Round((currentPosition.X - _dragState.StartPoint.X) / DayWidth, MidpointRounding.AwayFromZero);
            var newStart = _dragState.OriginalStart.AddDays(dayDelta);
            if (newStart == _dragState.Task.Start)
                return;

            _dragState.Task.Start = newStart;
            e.Handled = true;
        }

        private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (_blockedDragReason != null)
            {
                ShowDragHint(_blockedDragReason, e.GetPosition(GanttCanvas));
                _blockedDragReason = null;
            }

            if (_dragState == null)
                return;

            EndDrag();
            e.Handled = true;
        }

        private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
        {
            DateCoordBorder.Visibility = Visibility.Collapsed;
            HideMagnifier();

            if (_dragState == null || e.LeftButton == MouseButtonState.Pressed)
                return;

            EndDrag();
        }

        private void OnCanvasRightMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;

            // Usa FindTaskFromVisual para encontrar qualquer barra (incl. tarefas com início no passado)
            var task = FindTaskFromVisual(source) ?? FindDragTaskFromVisual(source);
            if (task == null || task.IsSummary) return;
            if (SelectedTask == null || task.Id != SelectedTask.Id) return;

            _resizeState = new TaskResizeState(task, task.Finish);
            GanttCanvas.CaptureMouse();
            GanttCanvas.Cursor = System.Windows.Input.Cursors.SizeWE;
            e.Handled = true;
        }

        private void OnCanvasRightMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_resizeState == null) return;

            // Marca data fim como fixada
            _resizeState.Task.FinishFixed = true;
            _resizeState = null;

            if (Mouse.Captured == GanttCanvas)
                Mouse.Capture(null);

            GanttCanvas.Cursor = null;
            e.Handled = true;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!_suppressScrollNotification &&
                (e.VerticalChange != 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0))
                VerticalScrollChanged?.Invoke(GanttScroll.VerticalOffset);

            RenderHeader(GanttScroll.HorizontalOffset);
        }

        public void Render()
        {
            GanttCanvas.Children.Clear();
            if (Tasks == null || Tasks.Count == 0) return;

            var totalDays = ZoomLevel is "Semestre" ? 730 : 365;
            var canvasWidth = LeftPadding + (totalDays * DayWidth);
            GanttCanvas.Width = canvasWidth;
            GanttCanvas.Height = GetCanvasHeight();

            if (_resetScrollOnNextRender)
            {
                _resetScrollOnNextRender = false;
                GanttScroll.ScrollToTop();
                GanttScroll.ScrollToLeftEnd();
            }

            RenderHeader(GanttScroll.HorizontalOffset);
            RenderGrid(totalDays, canvasWidth);
            RenderTodayLine();
            RenderBars();
            RenderDependencies();
        }

        private void RenderHeader(double scrollOffset)
        {
            HeaderCanvas.Children.Clear();
            if (DayHeaderMode == 2) { RenderCompactDayHeader(scrollOffset); return; }
            if (ShowDayHeader) { RenderDayHeader(scrollOffset); return; }

            var totalDays = ZoomLevel is "Semestre" ? 730 : 365;
            var topBandHeight = Math.Max(18, HeaderHeight * 0.48);
            var sprintBandTop = topBandHeight;
            var sprintBandHeight = Math.Max(14, HeaderHeight - sprintBandTop);
            var sprintDays = Math.Max(1, SprintDurationDays);
            var firstSprint = Math.Max(1, FirstSprintNumber);

            HeaderCanvas.Children.Add(new Rectangle
            {
                Width = Math.Max(ActualWidth, LeftPadding + totalDays * DayWidth),
                Height = topBandHeight,
                Fill = new SolidColorBrush(Color.FromRgb(232, 232, 232))
            });

            HeaderCanvas.Children.Add(new Rectangle
            {
                Width = Math.Max(ActualWidth, LeftPadding + totalDays * DayWidth),
                Height = sprintBandHeight,
                Fill = new SolidColorBrush(Color.FromRgb(220, 228, 240))
            });

            HeaderCanvas.Children.Add(new Line
            {
                X1 = 0,
                Y1 = sprintBandTop,
                X2 = Math.Max(ActualWidth, LeftPadding + totalDays * DayWidth),
                Y2 = sprintBandTop,
                Stroke = new SolidColorBrush(Color.FromRgb(190, 200, 215)),
                StrokeThickness = 1
            });

            // Com sprints reais do DevOps, desenha uma faixa por sprint usando a
            // janela e o NOME real; senão, cai na grade sintética numerada.
            var realSprints = Sprints;
            if (realSprints != null && realSprints.Count > 0)
            {
                foreach (var sprint in realSprints)
                {
                    var dayOffset = (sprint.Start - ProjectStart).TotalDays;
                    // Largura = janela da sprint (mínimo 1 dia), em dias corridos.
                    var spanDays = Math.Max(1, (sprint.End - sprint.Start).TotalDays + 1);
                    var x = LeftPadding + (dayOffset * DayWidth) - scrollOffset;
                    var sprintWidth = spanDays * DayWidth;
                    if (x + sprintWidth < -80 || x > ActualWidth + 80)
                        continue;

                    var background = new Rectangle
                    {
                        Width = sprintWidth,
                        Height = sprintBandHeight,
                        Fill = new SolidColorBrush(sprint.Number % 2 == 0
                            ? Color.FromRgb(210, 221, 236)
                            : Color.FromRgb(222, 231, 243)),
                        Stroke = new SolidColorBrush(Color.FromRgb(180, 194, 214)),
                        StrokeThickness = 1
                    };
                    Canvas.SetLeft(background, x);
                    Canvas.SetTop(background, sprintBandTop);
                    HeaderCanvas.Children.Add(background);

                    var sprintLabel = new TextBlock
                    {
                        Text = sprint.Name,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(43, 87, 154)),
                        Width = Math.Max(40, sprintWidth - 6),
                        TextAlignment = TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = $"{sprint.Name}  ({sprint.Start:dd/MM/yy} – {sprint.End:dd/MM/yy})"
                    };
                    Canvas.SetLeft(sprintLabel, x + 3);
                    Canvas.SetTop(sprintLabel, sprintBandTop + Math.Max(0, (sprintBandHeight - 14) / 2));
                    HeaderCanvas.Children.Add(sprintLabel);
                }
            }
            else
            for (int sprintOffset = 0; sprintOffset < totalDays; sprintOffset += sprintDays)
            {
                var sprintStart = ProjectStart.AddDays(sprintOffset);
                var sprintIndex = sprintOffset / sprintDays;
                var sprintNumber = GetSprintNumberFromIndex(sprintIndex, firstSprint);
                var x = LeftPadding + (sprintOffset * DayWidth) - scrollOffset;
                var sprintWidth = sprintDays * DayWidth;
                if (x + sprintWidth < -80 || x > ActualWidth + 80)
                    continue;

                var background = new Rectangle
                {
                    Width = sprintWidth,
                    Height = sprintBandHeight,
                    Fill = new SolidColorBrush(sprintNumber % 2 == 0
                        ? Color.FromRgb(210, 221, 236)
                        : Color.FromRgb(222, 231, 243)),
                    Stroke = new SolidColorBrush(Color.FromRgb(180, 194, 214)),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(background, x);
                Canvas.SetTop(background, sprintBandTop);
                HeaderCanvas.Children.Add(background);

                var sprintLabel = new TextBlock
                {
                    Text = $"Sprint {sprintNumber}",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(43, 87, 154)),
                    Width = Math.Max(40, sprintWidth - 6),
                    TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(sprintLabel, x + 3);
                Canvas.SetTop(sprintLabel, sprintBandTop + Math.Max(0, (sprintBandHeight - 14) / 2));
                HeaderCanvas.Children.Add(sprintLabel);
            }

            for (int d = 0; d < totalDays; d++)
            {
                var date = ProjectStart.AddDays(d);
                var x = LeftPadding + (d * DayWidth) - scrollOffset;
                if (x < -60 || x > ActualWidth + 60) continue;

                bool showLabel = ZoomLevel switch
                {
                    "Dia" => true,
                    "Semana" => date.DayOfWeek == DayOfWeek.Monday,
                    "Mês" => date.Day == 1,
                    "Trimestre" => date.Day == 1 && (date.Month == 1 || date.Month == 4 || date.Month == 7 || date.Month == 10),
                    _ => date.DayOfWeek == DayOfWeek.Monday
                };

                if (!showLabel) continue;

                var label = new TextBlock
                {
                    Text = ZoomLevel switch
                    {
                        "Dia" => date.ToString("d", CultureInfo.CurrentCulture),
                        "Semana" => date.ToString("d", CultureInfo.CurrentCulture),
                        "Mês" => date.ToString("MMM/yy"),
                        "Trimestre" => $"T{(date.Month - 1) / 3 + 1}/{date.Year}",
                        _ => date.ToString("dd/MM")
                    },
                    FontSize = 10,
                    Foreground = Brushes.DimGray,
                    Width = 60
                };
                Canvas.SetLeft(label, x + 1);
                Canvas.SetTop(label, 2);
                HeaderCanvas.Children.Add(label);

                HeaderCanvas.Children.Add(new Line
                {
                    X1 = x,
                    Y1 = 0,
                    X2 = x,
                    Y2 = HeaderHeight,
                    Stroke = Brushes.LightGray,
                    StrokeThickness = 1
                });
            }

            HeaderCanvas.Children.Add(new Line
            {
                X1 = 0,
                Y1 = HeaderHeight - 1,
                X2 = Math.Max(ActualWidth, LeftPadding + totalDays * DayWidth),
                Y2 = HeaderHeight - 1,
                Stroke = Brushes.LightGray,
                StrokeThickness = 1
            });
        }

        private void RenderDayHeader(double scrollOffset)
        {
            var totalDays = ZoomLevel is "Semestre" ? 730 : 365;
            double tierH = Math.Floor(HeaderHeight / 3.0);
            double dayTop = tierH;       // Tier 2: dias
            double sprintTop = tierH * 2; // Tier 3: sprints
            double canvasW = Math.Max(ActualWidth, LeftPadding + totalDays * DayWidth);

            // ── Background bands
            HeaderCanvas.Children.Add(new Rectangle { Width = canvasW, Height = tierH, Fill = new SolidColorBrush(Color.FromRgb(232, 232, 232)) });
            var dayBgFull = new Rectangle { Width = canvasW, Height = tierH, Fill = new SolidColorBrush(Color.FromRgb(245, 247, 251)) };
            Canvas.SetTop(dayBgFull, dayTop);
            HeaderCanvas.Children.Add(dayBgFull);
            var sprintBgFull = new Rectangle { Width = canvasW, Height = tierH, Fill = new SolidColorBrush(Color.FromRgb(220, 228, 240)) };
            Canvas.SetTop(sprintBgFull, sprintTop);
            HeaderCanvas.Children.Add(sprintBgFull);

            // Separators between tiers
            foreach (var ty in new[] { dayTop, sprintTop })
                HeaderCanvas.Children.Add(new Line { X1 = 0, Y1 = ty, X2 = canvasW, Y2 = ty, Stroke = new SolidColorBrush(Color.FromRgb(190, 200, 215)), StrokeThickness = 1 });

            // ── Tier 1: Month spans
            int? curMonthKey = null;
            double monthStartX = 0;
            for (int d = 0; d <= totalDays; d++)
            {
                var date = ProjectStart.AddDays(d < totalDays ? d : d - 1);
                int monthKey = date.Year * 100 + date.Month;
                bool flush = d == totalDays || (curMonthKey.HasValue && monthKey != curMonthKey);
                if (flush && curMonthKey.HasValue)
                {
                    double endX = LeftPadding + (d * DayWidth) - scrollOffset;
                    double w = endX - monthStartX;
                    if (w > 2 && monthStartX < canvasW + 80 && endX > -80)
                    {
                        var prevDate = ProjectStart.AddDays(d - 1);
                        var lbl = new TextBlock
                        {
                            Text = prevDate.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
                            FontSize = 10,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = Brushes.DimGray,
                            Width = Math.Max(8, w - 6),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        };
                        Canvas.SetLeft(lbl, monthStartX + 4);
                        Canvas.SetTop(lbl, (tierH - 13) / 2);
                        HeaderCanvas.Children.Add(lbl);
                        if (d < totalDays)
                            HeaderCanvas.Children.Add(new Line { X1 = endX, Y1 = 0, X2 = endX, Y2 = dayTop, Stroke = new SolidColorBrush(Color.FromRgb(180, 190, 210)), StrokeThickness = 1 });
                    }
                }
                if (d < totalDays && (!curMonthKey.HasValue || monthKey != curMonthKey))
                {
                    curMonthKey = monthKey;
                    monthStartX = LeftPadding + (d * DayWidth) - scrollOffset;
                }
            }

            // ── Tier 2: Sprint spans
            var realSprints = Sprints;
            if (realSprints != null && realSprints.Count > 0)
            {
                foreach (var sprint in realSprints)
                {
                    var dayOffset = (sprint.Start - ProjectStart).TotalDays;
                    var spanDays = Math.Max(1, (sprint.End - sprint.Start).TotalDays + 1);
                    var x = LeftPadding + (dayOffset * DayWidth) - scrollOffset;
                    var sw = spanDays * DayWidth;
                    if (x + sw < -80 || x > ActualWidth + 80) continue;
                    var bg = new Rectangle { Width = sw, Height = tierH, Fill = new SolidColorBrush(sprint.Number % 2 == 0 ? Color.FromRgb(210, 221, 236) : Color.FromRgb(222, 231, 243)), Stroke = new SolidColorBrush(Color.FromRgb(180, 194, 214)), StrokeThickness = 1 };
                    Canvas.SetLeft(bg, x); Canvas.SetTop(bg, sprintTop); HeaderCanvas.Children.Add(bg);
                    var lbl = new TextBlock { Text = sprint.Name, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(43, 87, 154)), Width = Math.Max(16, sw - 4), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = $"{sprint.Name}  ({sprint.Start:dd/MM/yy} – {sprint.End:dd/MM/yy})" };
                    Canvas.SetLeft(lbl, x + 2); Canvas.SetTop(lbl, sprintTop + (tierH - 12) / 2); HeaderCanvas.Children.Add(lbl);
                }
            }
            else
            {
                var sprintDays = Math.Max(1, SprintDurationDays);
                var firstSprint = Math.Max(1, FirstSprintNumber);
                for (int so = 0; so < totalDays; so += sprintDays)
                {
                    var sn = GetSprintNumberFromIndex(so / sprintDays, firstSprint);
                    var x = LeftPadding + (so * DayWidth) - scrollOffset;
                    var sw = sprintDays * DayWidth;
                    if (x + sw < -80 || x > ActualWidth + 80) continue;
                    var bg = new Rectangle { Width = sw, Height = tierH, Fill = new SolidColorBrush(sn % 2 == 0 ? Color.FromRgb(210, 221, 236) : Color.FromRgb(222, 231, 243)), Stroke = new SolidColorBrush(Color.FromRgb(180, 194, 214)), StrokeThickness = 1 };
                    Canvas.SetLeft(bg, x); Canvas.SetTop(bg, sprintTop); HeaderCanvas.Children.Add(bg);
                    var lbl = new TextBlock { Text = $"Sprint {sn}", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(43, 87, 154)), Width = Math.Max(16, sw - 4), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                    Canvas.SetLeft(lbl, x + 2); Canvas.SetTop(lbl, sprintTop + (tierH - 12) / 2); HeaderCanvas.Children.Add(lbl);
                }
            }

            // ── Tier 3: Working day numbers + weekend shading
            for (int d = 0; d < totalDays; d++)
            {
                var date = ProjectStart.AddDays(d);
                var x = LeftPadding + (d * DayWidth) - scrollOffset;
                if (x + DayWidth < -4 || x > ActualWidth + 4) continue;

                bool isWeekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
                bool isToday = date.Date == DateTime.Today;

                bool isMonday    = date.DayOfWeek == DayOfWeek.Monday;
                bool isWednesday = date.DayOfWeek == DayOfWeek.Wednesday;
                bool isFriday    = date.DayOfWeek == DayOfWeek.Friday;

                // Cell background: seg=azul suave, qua/sex=azul mais vivo, ter/qui=neutro, fim semana=cinza
                Color cellFill = isWeekend                ? Color.FromRgb(220, 220, 224)
                    : isToday                             ? Color.FromRgb(198, 225, 255)
                    : isMonday                            ? Color.FromRgb(220, 232, 250)
                    : isWednesday || isFriday             ? Color.FromRgb(170, 200, 235)
                    :                                       Color.FromRgb(245, 247, 251);
                var cell = new Rectangle { Width = DayWidth, Height = tierH, Fill = new SolidColorBrush(cellFill) };
                Canvas.SetLeft(cell, x); Canvas.SetTop(cell, dayTop);
                HeaderCanvas.Children.Add(cell);

                // Segunda: número do dia — sempre visível, pode invadir a célula da terça
                if (isMonday)
                {
                    var dayLbl = new TextBlock
                    {
                        Text = date.Day.ToString(),
                        FontSize = 9,
                        Foreground = isToday ? new SolidColorBrush(Color.FromRgb(0, 80, 180))
                                             : new SolidColorBrush(Color.FromRgb(40, 70, 130)),
                        FontWeight = FontWeights.SemiBold,
                        Width = 20,   // largura fixa — invade a terça quando DayWidth < 20
                        TextAlignment = TextAlignment.Left,
                        TextTrimming = TextTrimming.None
                    };
                    Canvas.SetLeft(dayLbl, x + 1); Canvas.SetTop(dayLbl, dayTop + (tierH - 12) / 2);
                    Panel.SetZIndex(dayLbl, 1); // fica sobre a célula da terça
                    HeaderCanvas.Children.Add(dayLbl);
                }

                // Separador: mostra a cada dia se ≥8px, senão só nas segundas
                var drawSep = DayWidth >= 8 || date.DayOfWeek == DayOfWeek.Monday;
                if (drawSep)
                    HeaderCanvas.Children.Add(new Line { X1 = x, Y1 = dayTop, X2 = x, Y2 = HeaderHeight, Stroke = new SolidColorBrush(Color.FromRgb(218, 220, 225)), StrokeThickness = 0.5 });
            }

            // Bottom border
            HeaderCanvas.Children.Add(new Line { X1 = 0, Y1 = HeaderHeight - 1, X2 = canvasW, Y2 = HeaderHeight - 1, Stroke = Brushes.LightGray, StrokeThickness = 1 });
        }

        private void RenderCompactDayHeader(double scrollOffset)
        {
            var totalDays = ZoomLevel is "Semestre" ? 730 : 365;
            double tierH = Math.Floor(HeaderHeight / 3.0);
            double dayTop    = tierH;
            double sprintTop = tierH * 2;
            double canvasW = Math.Max(ActualWidth, LeftPadding + totalDays * DayWidth);

            // Background bands
            HeaderCanvas.Children.Add(new Rectangle { Width = canvasW, Height = tierH, Fill = new SolidColorBrush(Color.FromRgb(232, 232, 232)) });
            var dayBg = new Rectangle { Width = canvasW, Height = tierH, Fill = new SolidColorBrush(Color.FromRgb(240, 244, 250)) };
            Canvas.SetTop(dayBg, dayTop); HeaderCanvas.Children.Add(dayBg);
            var sprintBg = new Rectangle { Width = canvasW, Height = tierH, Fill = new SolidColorBrush(Color.FromRgb(220, 228, 240)) };
            Canvas.SetTop(sprintBg, sprintTop); HeaderCanvas.Children.Add(sprintBg);

            foreach (var ty in new[] { dayTop, sprintTop })
                HeaderCanvas.Children.Add(new Line { X1 = 0, Y1 = ty, X2 = canvasW, Y2 = ty, Stroke = new SolidColorBrush(Color.FromRgb(190, 200, 215)), StrokeThickness = 1 });

            // Tier 1: Month spans (igual ao RenderDayHeader)
            int? curMonthKey = null;
            double monthStartX = 0;
            for (int d = 0; d <= totalDays; d++)
            {
                var date = ProjectStart.AddDays(d < totalDays ? d : d - 1);
                int monthKey = date.Year * 100 + date.Month;
                bool flush = d == totalDays || (curMonthKey.HasValue && monthKey != curMonthKey);
                if (flush && curMonthKey.HasValue)
                {
                    double endX = LeftPadding + (d * DayWidth) - scrollOffset;
                    double w = endX - monthStartX;
                    if (w > 2 && monthStartX < canvasW + 80 && endX > -80)
                    {
                        var prevDate = ProjectStart.AddDays(d - 1);
                        var lbl = new TextBlock { Text = prevDate.ToString("MMMM yyyy", CultureInfo.CurrentCulture), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.DimGray, Width = Math.Max(8, w - 6), TextTrimming = TextTrimming.CharacterEllipsis };
                        Canvas.SetLeft(lbl, monthStartX + 4); Canvas.SetTop(lbl, (tierH - 13) / 2);
                        HeaderCanvas.Children.Add(lbl);
                        if (d < totalDays)
                            HeaderCanvas.Children.Add(new Line { X1 = endX, Y1 = 0, X2 = endX, Y2 = dayTop, Stroke = new SolidColorBrush(Color.FromRgb(180, 190, 210)), StrokeThickness = 1 });
                    }
                }
                if (d < totalDays && (!curMonthKey.HasValue || monthKey != curMonthKey))
                {
                    curMonthKey = monthKey;
                    monthStartX = LeftPadding + (d * DayWidth) - scrollOffset;
                }
            }

            // Tier 2: Sprint spans (igual ao RenderDayHeader)
            var realSprints = Sprints;
            if (realSprints != null && realSprints.Count > 0)
            {
                foreach (var sprint in realSprints)
                {
                    var dayOffset = (sprint.Start - ProjectStart).TotalDays;
                    var spanDays = Math.Max(1, (sprint.End - sprint.Start).TotalDays + 1);
                    var x = LeftPadding + (dayOffset * DayWidth) - scrollOffset;
                    var sw = spanDays * DayWidth;
                    if (x + sw < -80 || x > ActualWidth + 80) continue;
                    var bg = new Rectangle { Width = sw, Height = tierH, Fill = new SolidColorBrush(sprint.Number % 2 == 0 ? Color.FromRgb(210, 221, 236) : Color.FromRgb(222, 231, 243)), Stroke = new SolidColorBrush(Color.FromRgb(180, 194, 214)), StrokeThickness = 1 };
                    Canvas.SetLeft(bg, x); Canvas.SetTop(bg, sprintTop); HeaderCanvas.Children.Add(bg);
                    var lbl = new TextBlock { Text = sprint.Name, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(43, 87, 154)), Width = Math.Max(16, sw - 4), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                    Canvas.SetLeft(lbl, x + 2); Canvas.SetTop(lbl, sprintTop + (tierH - 12) / 2); HeaderCanvas.Children.Add(lbl);
                }
            }
            else
            {
                var sprintDays = Math.Max(1, SprintDurationDays);
                var firstSprint = Math.Max(1, FirstSprintNumber);
                for (int so = 0; so < totalDays; so += sprintDays)
                {
                    var sn = GetSprintNumberFromIndex(so / sprintDays, firstSprint);
                    var x = LeftPadding + (so * DayWidth) - scrollOffset;
                    var sw = sprintDays * DayWidth;
                    if (x + sw < -80 || x > ActualWidth + 80) continue;
                    var bg = new Rectangle { Width = sw, Height = tierH, Fill = new SolidColorBrush(sn % 2 == 0 ? Color.FromRgb(210, 221, 236) : Color.FromRgb(222, 231, 243)), Stroke = new SolidColorBrush(Color.FromRgb(180, 194, 214)), StrokeThickness = 1 };
                    Canvas.SetLeft(bg, x); Canvas.SetTop(bg, sprintTop); HeaderCanvas.Children.Add(bg);
                    var lbl = new TextBlock { Text = $"Sprint {sn}", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(43, 87, 154)), Width = Math.Max(16, sw - 4), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                    Canvas.SetLeft(lbl, x + 2); Canvas.SetTop(lbl, sprintTop + (tierH - 12) / 2); HeaderCanvas.Children.Add(lbl);
                }
            }

            // Tier 3: dígito compacto por dia
            // Dias 10, 20, 30: dígito das dezenas (0, 2, 3) com cores especiais
            // Demais dias: dígito das unidades (1-9)
            static (string ch, bool isDec) DayChar(int day)
            {
                if (day % 10 == 0 && day <= 30) return (day == 10 ? "0" : (day / 10).ToString(), true);
                return ((day % 10).ToString(), false);
            }

            // cores dos marcadores de dezena
            var color10 = Color.FromRgb(43, 87, 154);   // azul — dia 10
            var color20 = Color.FromRgb(160, 60, 10);   // laranja escuro — dia 20
            var color30 = Color.FromRgb(20, 120, 60);   // verde escuro — dia 30
            static Color DecColor(int day) => day == 10 ? Color.FromRgb(43, 87, 154) : day == 20 ? Color.FromRgb(160, 60, 10) : Color.FromRgb(20, 120, 60);

            for (int d = 0; d < totalDays; d++)
            {
                var date = ProjectStart.AddDays(d);
                var x = LeftPadding + (d * DayWidth) - scrollOffset;
                if (x + DayWidth < -4 || x > ActualWidth + 4) continue;

                var (ch, isDec) = DayChar(date.Day);
                bool isToday = date.Date == DateTime.Today;
                bool isWeekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;

                // fundo da célula
                Color cellFill = isToday    ? Color.FromRgb(198, 225, 255)
                    : isDec && date.Day == 10 ? Color.FromRgb(220, 232, 250)
                    : isDec && date.Day == 20 ? Color.FromRgb(255, 235, 215)
                    : isDec && date.Day == 30 ? Color.FromRgb(210, 240, 220)
                    : isWeekend               ? Color.FromRgb(220, 220, 224)
                    :                           Color.FromRgb(240, 244, 250);
                var cell = new Rectangle { Width = DayWidth, Height = tierH, Fill = new SolidColorBrush(cellFill) };
                Canvas.SetLeft(cell, x); Canvas.SetTop(cell, dayTop);
                HeaderCanvas.Children.Add(cell);

                // separador vertical
                if (DayWidth >= 6 || isDec)
                    HeaderCanvas.Children.Add(new Line { X1 = x, Y1 = dayTop, X2 = x, Y2 = HeaderHeight, Stroke = new SolidColorBrush(isDec ? Color.FromRgb(180, 190, 210) : Color.FromRgb(218, 220, 225)), StrokeThickness = isDec ? 0.8 : 0.4 });

                // dígito
                if (DayWidth >= 5)
                {
                    var fg = isToday ? new SolidColorBrush(Color.FromRgb(0, 80, 180))
                           : isDec   ? new SolidColorBrush(DecColor(date.Day))
                           :           new SolidColorBrush(Color.FromRgb(80, 90, 110));
                    var dayLbl = new TextBlock
                    {
                        Text = ch,
                        FontSize = isDec ? 9 : 8,
                        FontWeight = isDec ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = fg,
                        Width = DayWidth,
                        TextAlignment = TextAlignment.Center
                    };
                    Canvas.SetLeft(dayLbl, x); Canvas.SetTop(dayLbl, dayTop + (tierH - (isDec ? 12 : 11)) / 2);
                    if (isDec) Panel.SetZIndex(dayLbl, 1);
                    HeaderCanvas.Children.Add(dayLbl);
                }
            }

            HeaderCanvas.Children.Add(new Line { X1 = 0, Y1 = HeaderHeight - 1, X2 = canvasW, Y2 = HeaderHeight - 1, Stroke = Brushes.LightGray, StrokeThickness = 1 });
        }

        private void RenderGrid(int totalDays, double canvasWidth)
        {
            // Weekend column shading in day view (drawn first, below grid lines)
            if (ShowDayHeader)
            {
                for (int d = 0; d < totalDays; d++)
                {
                    var date = ProjectStart.AddDays(d);
                    if (date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday) continue;
                    var wkRect = new Rectangle
                    {
                        Width = DayWidth,
                        Height = GanttCanvas.Height,
                        Fill = new SolidColorBrush(Color.FromRgb(242, 242, 244))
                    };
                    Canvas.SetLeft(wkRect, LeftPadding + (d * DayWidth));
                    GanttCanvas.Children.Add(wkRect);
                }
            }

            // Row hierarchy background
            if (Tasks != null)
            {
                for (int i = 0; i < Tasks.Count; i++)
                {
                    var bg = Tasks[i].HierarchyBackground;
                    if (bg == null) continue;
                    var rowBg = new Rectangle { Width = canvasWidth, Height = RowHeight, Fill = bg };
                    Canvas.SetLeft(rowBg, 0);
                    Canvas.SetTop(rowBg, GetRowTop(i));
                    GanttCanvas.Children.Add(rowBg);
                }
            }

            var rowCount = Tasks?.Count ?? 0;
            for (int i = 0; i <= rowCount; i++)
            {
                var y = GetRowTop(i);
                GanttCanvas.Children.Add(new Line
                {
                    X1 = 0,
                    Y1 = y,
                    X2 = canvasWidth,
                    Y2 = y,
                    Stroke = Brushes.WhiteSmoke,
                    StrokeThickness = 1
                });
            }

            for (int d = 0; d < totalDays; d++)
            {
                var date = ProjectStart.AddDays(d);
                var sprintDaysG = Math.Max(1, SprintDurationDays);
                bool drawLine = ZoomLevel switch
                {
                    "Dia"       => date.DayOfWeek == DayOfWeek.Monday,
                    "Semana"    => date.DayOfWeek == DayOfWeek.Monday,
                    "Sprint"    => d % sprintDaysG == 0,
                    "Mês"       => date.Day == 1,
                    "Trimestre" => date.Day == 1 && date.Month % 3 == 1,
                    "Semestre"  => date.Day == 1 && date.Month % 6 == 1,
                    _           => date.DayOfWeek == DayOfWeek.Monday
                };

                if (!drawLine) continue;

                GanttCanvas.Children.Add(new Line
                {
                    X1 = LeftPadding + (d * DayWidth),
                    Y1 = 0,
                    X2 = LeftPadding + (d * DayWidth),
                    Y2 = GanttCanvas.Height,
                    Stroke = ZoomLevel == "Dia" ? new SolidColorBrush(Color.FromRgb(220, 220, 220)) : Brushes.WhiteSmoke,
                    StrokeThickness = 1
                });
            }
        }

        private void RenderTodayLine()
        {
            var todayOffset = (DateTime.Today - ProjectStart).TotalDays;
            if (todayOffset < 0) return;

            var x = LeftPadding + (todayOffset * DayWidth);
            GanttCanvas.Children.Add(new Line
            {
                X1 = x,
                Y1 = 0,
                X2 = x,
                Y2 = GanttCanvas.Height,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 69, 0)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 2 }
            });
        }

        private void RenderBars()
        {
            if (Tasks == null) return;

            for (int i = 0; i < Tasks.Count; i++)
            {
                var vm = Tasks[i];
                var isSelected = SelectedTask != null && SelectedTask.Id == vm.Id;
                var isPredecessor = HighlightedPredecessorIds.Contains(vm.Model.Id);
                var y = GetRowTop(i);
                var startOffset = (vm.Start - ProjectStart).TotalDays;
                var endOffset = (vm.Finish - ProjectStart).TotalDays;
                var x = LeftPadding + (startOffset * DayWidth);
                var width = Math.Max(1, (endOffset - startOffset) * DayWidth);

                RenderRowHitArea(vm, y);

                if (vm.DisplayAsMilestone)
                    RenderMilestone(vm, x, y, isSelected);
                else if (vm.IsSummary)
                    RenderSummaryBar(vm, x, y, width, isSelected, isPredecessor);
                else
                    RenderTaskBar(vm, x, y, width, vm.PercentComplete, isSelected, isPredecessor);

                if (vm.HasBaseline && !vm.DisplayAsMilestone)
                    RenderBaselineBar(vm, y);
            }
        }

        private static readonly SolidColorBrush BaselineBrush =
            new(Color.FromArgb(220, 30, 120, 210));  // azul claro, distinto do cinza do Gantt

        private void RenderBaselineBar(TaskViewModel vm, double y)
        {
            if (vm.BaselineStart == null || vm.BaselineFinish == null) return;

            var bStartOffset = (vm.BaselineStart.Value - ProjectStart).TotalDays;
            var bEndOffset   = (vm.BaselineFinish.Value - ProjectStart).TotalDays;
            var bx    = LeftPadding + bStartOffset * DayWidth;
            var bw    = Math.Max(2, (bEndOffset - bStartOffset) * DayWidth);
            var barH  = RowHeight - BarPadding * 2;

            var bar = new Rectangle
            {
                Width           = bw,
                Height          = 3,
                Fill            = BaselineBrush,
                RadiusX         = 1,
                RadiusY         = 1,
                IsHitTestVisible = false,
                ToolTip         = new ToolTip
                {
                    Content = $"Baseline: {vm.BaselineStart.Value:dd/MM/yyyy} → {vm.BaselineFinish.Value:dd/MM/yyyy}" +
                              (vm.BaselineHours.HasValue ? $"  ({vm.BaselineHours.Value:0.#}h)" : "")
                }
            };
            Canvas.SetLeft(bar, bx);
            Canvas.SetTop(bar, y + BarPadding + barH);   // logo abaixo da barra principal
            GanttCanvas.Children.Add(bar);
        }

        private void RenderDependencies()
        {
            if (Tasks == null || Tasks.Count == 0)
                return;

            var layouts = BuildTaskLayouts();
            var dependencyBrush = new SolidColorBrush(Color.FromRgb(120, 120, 120));

            foreach (var successor in Tasks)
            {
                foreach (var predecessorId in successor.Model.PredecessorIds)
                {
                    if (!layouts.TryGetValue(predecessorId, out var predecessorLayout))
                        continue;

                    if (!layouts.TryGetValue(successor.Id, out var successorLayout))
                        continue;

                    if (predecessorLayout.Task.Id == successorLayout.Task.Id)
                        continue;

                    RenderDependencyArrow(predecessorLayout, successorLayout, dependencyBrush);
                }
            }
        }

        private void RenderRowHitArea(TaskViewModel task, double y)
        {
            var hitArea = new Rectangle
            {
                Width = GanttCanvas.Width,
                Height = RowHeight,
                Fill = Brushes.Transparent
            };
            AttachTaskMetadata(hitArea, task, allowDrag: false);
            Canvas.SetLeft(hitArea, 0);
            Canvas.SetTop(hitArea, y);
            GanttCanvas.Children.Add(hitArea);
        }

        private void RenderTaskBar(TaskViewModel task, double x, double y, double width, double percent, bool isSelected, bool isPredecessor = false)
        {
            if (width < 3)
            {
                RenderTaskMarker(task, x, y, isSelected);
                return;
            }

            bool durationOverrun = task.StartFixed && task.CalculatedFinish.HasValue;

            // Estimativa original maior que restante → overrun → barra vermelha
            double? origHours = task.Model.OriginalEstimatedHours;
            double? estHours  = task.Model.EstimatedHours;
            bool origOverrun  = task.UseOriginalHoursView
                                && origHours is > 0
                                && estHours is > 0
                                && estHours > origHours;

            var isCriticalPath = ShowCriticalPath && CriticalTaskIds != null && CriticalTaskIds.Contains(task.Model.Id);
            var isCriticalPathRisk = ShowCriticalPath && CriticalPathRiskTaskIds != null && CriticalPathRiskTaskIds.Contains(task.Model.Id);

            var bgColor = isCriticalPath      ? Color.FromRgb(192, 57, 43)
                        : isCriticalPathRisk  ? Color.FromRgb(245, 158, 11)
                        : isSelected          ? Color.FromRgb(220, 124, 0)
                        : isPredecessor       ? Color.FromRgb(200, 100, 20)
                        : task.UseOriginalHoursView && origHours is > 0 ? Color.FromRgb(185, 28, 28)
                        : task.HasSyncConflict || durationOverrun ? Color.FromRgb(196, 43, 43)
                        :                      Color.FromRgb(91, 155, 213);
            var bg = new Rectangle
            {
                Width = width,
                Height = RowHeight - BarPadding * 2,
                Fill = new SolidColorBrush(bgColor),
                RadiusX = 2,
                RadiusY = 2
            };
            if (durationOverrun && task.CalculatedFinish.HasValue)
            {
                var calcDays = ProjectCalendarService.CountWorkingDays(task.Model.Start, task.CalculatedFinish.Value);
                var negDays  = ProjectCalendarService.CountWorkingDays(task.Model.Start, task.Model.Finish);
                var diff = calcDays - negDays;
                var hint = diff > 0
                    ? $"⚠ Duração negociada: {negDays} dia(s) úteis\nDuração calculada (HH/alocação): {calcDays} dia(s) úteis (+{diff}d)"
                    : $"⚠ Duração negociada: {negDays} dia(s) úteis\nDuração calculada (HH/alocação): {calcDays} dia(s) úteis ({diff}d)";
                bg.ToolTip = new ToolTip { Content = hint };
            }
            AttachTaskMetadata(bg, task);
            Canvas.SetLeft(bg, x);
            Canvas.SetTop(bg, y + BarPadding);
            GanttCanvas.Children.Add(bg);

            // Borda vermelha do caminho crítico
            if (isCriticalPath)
            {
                var critBorder = new Rectangle
                {
                    Width           = width,
                    Height          = RowHeight - BarPadding * 2,
                    Stroke          = new SolidColorBrush(Color.FromRgb(192, 57, 43)),
                    StrokeThickness = 2,
                    Fill            = Brushes.Transparent,
                    RadiusX         = 2,
                    RadiusY         = 2,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(critBorder, x);
                Canvas.SetTop(critBorder, y + BarPadding);
                GanttCanvas.Children.Add(critBorder);
            }

            var dotColor = isSelected ? Color.FromRgb(255, 165, 0) : Color.FromRgb(100, 100, 100);
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = new SolidColorBrush(dotColor),
                Stroke = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                StrokeThickness = 1
            };
            AttachTaskMetadata(dot, task);
            Canvas.SetLeft(dot, x - 2.5);
            Canvas.SetTop(dot, y + RowHeight / 2 - 2.5);
            GanttCanvas.Children.Add(dot);

            // Linha discreta: HH Atual proporcional à duração total (HH Atual + HH Restante).
            double? realizedH = task.Model.CurrentHours;
            double  totalH    = task.Model.CurrentHours is > 0
                                ? task.Model.CurrentHours.Value + (task.Model.EstimatedHours ?? 0)
                                : ProjectCalendarService.CountWorkingHours(task.Model.Start, task.Model.Finish);
            if (realizedH is > 0 && totalH > 0)
            {
                double realizedW = Math.Min(width, width * (realizedH.Value / totalH));
                if (realizedW > 1)
                {
                    var realBar = new Rectangle
                    {
                        Width = realizedW,
                        Height = 2,
                        Fill = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                        Opacity = 0.9,
                        ToolTip = new ToolTip { Content = $"HH Atual: {realizedH.Value:0.#}h | HH Restante: {(task.Model.EstimatedHours ?? 0):0.#}h | Total: {totalH:0.#}h" }
                    };
                    AttachTaskMetadata(realBar, task);
                    Canvas.SetLeft(realBar, x);
                    Canvas.SetTop(realBar, y + RowHeight - BarPadding - 3);
                    GanttCanvas.Children.Add(realBar);
                }
            }

            if (percent > 0)
            {
                var barHeight = RowHeight - BarPadding * 2;
                var progressW = Math.Min(width, width * percent / 100.0);

                // Faixa de progresso — ocupa metade superior da barra (estilo MS Project)
                var progress = new Rectangle
                {
                    Width   = progressW,
                    Height  = barHeight / 2,
                    Fill    = new SolidColorBrush(Color.FromArgb(180, 17, 24, 39)),
                    RadiusX = 2,
                    RadiusY = 2
                };
                AttachTaskMetadata(progress, task);
                Canvas.SetLeft(progress, x);
                Canvas.SetTop(progress, y + BarPadding);
                GanttCanvas.Children.Add(progress);

                // Texto de % centralizado na barra (visível se a barra for larga o suficiente)
                if (width >= 30)
                {
                    var pctLabel = new TextBlock
                    {
                        Text       = $"{percent:0}%",
                        FontSize   = 8,
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.SemiBold
                    };
                    AttachTaskMetadata(pctLabel, task);
                    Canvas.SetLeft(pctLabel, x + 3);
                    Canvas.SetTop(pctLabel, y + BarPadding + 1);
                    GanttCanvas.Children.Add(pctLabel);
                }
            }


            if (!isSelected) return;

            var border = new Rectangle
            {
                Width = width,
                Height = RowHeight - BarPadding * 2,
                Fill = null,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 165, 0)),
                StrokeThickness = 2,
                RadiusX = 2,
                RadiusY = 2
            };
            AttachTaskMetadata(border, task);
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y + BarPadding);
            GanttCanvas.Children.Add(border);
        }

        private void RenderTaskMarker(TaskViewModel task, double x, double y, bool isSelected)
        {
            var markerColor = isSelected ? Color.FromRgb(220, 124, 0) : Color.FromRgb(68, 114, 196);
            var marker = new Line
            {
                X1 = x,
                Y1 = y + 2,
                X2 = x,
                Y2 = y + RowHeight - 2,
                Stroke = new SolidColorBrush(markerColor),
                StrokeThickness = isSelected ? 4 : 3
            };
            AttachTaskMetadata(marker, task);
            GanttCanvas.Children.Add(marker);

            var circle = new Ellipse
            {
                Width = isSelected ? 8 : 6,
                Height = isSelected ? 8 : 6,
                Fill = new SolidColorBrush(markerColor),
                Stroke = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                StrokeThickness = 1
            };
            AttachTaskMetadata(circle, task);
            Canvas.SetLeft(circle, x - (isSelected ? 4 : 3));
            Canvas.SetTop(circle, y + 2);
            GanttCanvas.Children.Add(circle);
        }

        private void RenderSummaryBar(TaskViewModel task, double x, double y, double width, bool isSelected, bool isPredecessor = false)
        {
            var barColor = isSelected           ? Color.FromRgb(220, 124, 0)
                         : isPredecessor        ? Color.FromRgb(200, 100, 20)
                         : task.HasSyncConflict ? Color.FromRgb(180, 30, 30)
                         :                        Color.FromRgb(148, 163, 184);
            var bar = new Rectangle
            {
                Width = width,
                Height = RowHeight - BarPadding * 2 - 2,
                Fill = new SolidColorBrush(barColor),
                RadiusX = 1,
                RadiusY = 1
            };
            AttachTaskMetadata(bar, task);
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, y + BarPadding);
            GanttCanvas.Children.Add(bar);

            var dotColor = isSelected ? Color.FromRgb(255, 165, 0) : Color.FromRgb(100, 100, 100);
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = new SolidColorBrush(dotColor),
                Stroke = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                StrokeThickness = 1
            };
            AttachTaskMetadata(dot, task);
            Canvas.SetLeft(dot, x - 2.5);
            Canvas.SetTop(dot, y + RowHeight / 2 - 2.5);
            GanttCanvas.Children.Add(dot);

            if (isSelected)
            {
                var border = new Rectangle
                {
                    Width = width,
                    Height = RowHeight - BarPadding * 2 - 2,
                    Fill = null,
                    Stroke = new SolidColorBrush(Color.FromRgb(255, 165, 0)),
                    StrokeThickness = 2,
                    RadiusX = 1,
                    RadiusY = 1
                };
                AttachTaskMetadata(border, task);
                Canvas.SetLeft(border, x);
                Canvas.SetTop(border, y + BarPadding);
                GanttCanvas.Children.Add(border);
            }
        }

        private void RenderMilestone(TaskViewModel task, double x, double y, bool isSelected)
        {
            var size = RowHeight - BarPadding * 2;
            var fillColor = isSelected ? Brushes.Orange : Brushes.Goldenrod;
            var strokeColor = isSelected ? Brushes.DarkOrange : Brushes.DarkGoldenrod;
            var strokeThickness = isSelected ? 2.0 : 1.0;

            var diamond = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(x, y + RowHeight / 2),
                    new Point(x + size / 2, y + BarPadding),
                    new Point(x + size, y + RowHeight / 2),
                    new Point(x + size / 2, y + RowHeight - BarPadding)
                },
                Fill = fillColor,
                Stroke = strokeColor,
                StrokeThickness = strokeThickness
            };
            AttachTaskMetadata(diamond, task);
            GanttCanvas.Children.Add(diamond);
        }

        private void AttachTaskMetadata(FrameworkElement element, TaskViewModel task, bool allowDrag = true)
        {
            GanttTaskElements.SetTask(element, task);
            element.Tag = allowDrag ? "task-drag" : "task-hit";
            element.ToolTip = BuildTaskToolTip(task);
            element.ToolTipOpening += OnGanttCanvasToolTipOpening;
            ToolTipService.SetIsEnabled(element, !_magnifierEnabled);
            ToolTipService.SetShowDuration(element, 30000);
            ToolTipService.SetInitialShowDelay(element, 250);
        }

        private static ToolTip BuildTaskToolTip(TaskViewModel task)
        {
            var content = new StackPanel
            {
                Margin = new Thickness(2)
            };

            content.Children.Add(new TextBlock
            {
                Text = task.Name,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            content.Children.Add(CreateHintLine("ID", task.Id.ToString(CultureInfo.CurrentCulture)));
            content.Children.Add(CreateHintLine("Inicio", task.Start.ToString("g", CultureInfo.CurrentCulture)));
            content.Children.Add(CreateHintLine("Fim", task.FinishDisplay));
            content.Children.Add(CreateHintLine("Duracao", $"{task.DurationHours:0} h"));
            content.Children.Add(CreateHintLine("Concluido", $"{task.PercentComplete:0}%"));

            if (!string.IsNullOrWhiteSpace(task.PredecessorsText))
                content.Children.Add(CreateHintLine("Predecessoras", task.PredecessorsText));

            if (!string.IsNullOrWhiteSpace(task.ResourcesText))
                content.Children.Add(CreateHintLine("Recursos", task.ResourcesText));

            if (task.SfpPoints.HasValue && task.SfpPoints.Value > 0)
                content.Children.Add(CreateHintLine("SFP", task.SfpPoints.Value.ToString("0.##", CultureInfo.CurrentCulture)));

            return new ToolTip
            {
                Content = content,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
                HasDropShadow = true
            };
        }

        private static TextBlock CreateHintLine(string label, string value)
        {
            return new TextBlock
            {
                Text = $"{label}: {value}",
                FontSize = 11
            };
        }

        private void RenderDependencyArrow(TaskLayout predecessor, TaskLayout successor, Brush stroke)
        {
            var startX = predecessor.EndX;
            var startY = predecessor.CenterY;
            var endX = successor.StartX;
            var endY = successor.CenterY;
            var elbowX = Math.Max(startX + DependencyMargin, endX - DependencyMargin);
            var targetX = Math.Max(LeftPadding, endX - 5);

            var path = new Polyline
            {
                Stroke = stroke,
                StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Round
            };
            path.Points.Add(new Point(startX, startY));
            path.Points.Add(new Point(elbowX, startY));
            path.Points.Add(new Point(elbowX, endY));
            path.Points.Add(new Point(targetX, endY));
            GanttCanvas.Children.Add(path);

            var arrow = new Polygon
            {
                Fill = stroke,
                Points = new PointCollection
                {
                    new Point(endX, endY),
                    new Point(endX - 5, endY - 3),
                    new Point(endX - 5, endY + 3)
                }
            };
            GanttCanvas.Children.Add(arrow);
        }

        private Dictionary<int, TaskLayout> BuildTaskLayouts()
        {
            var layouts = new Dictionary<int, TaskLayout>();

            if (Tasks == null)
                return layouts;

            for (int i = 0; i < Tasks.Count; i++)
            {
                var task = Tasks[i];
                var y = GetRowTop(i);
                var startOffset = (task.Start - ProjectStart).TotalDays;
                var endOffset = (task.Finish - ProjectStart).TotalDays;
                var startX = LeftPadding + (startOffset * DayWidth);
                var width = Math.Max(1, (endOffset - startOffset) * DayWidth);
                var endX = task.DisplayAsMilestone
                    ? startX + ((RowHeight - BarPadding * 2) / 2.0)
                    : startX + width;

                layouts[task.Id] = new TaskLayout(
                    task,
                    startX,
                    endX,
                    y + (RowHeight / 2.0));
            }

            return layouts;
        }

        private double GetRowTop(int index)
        {
            if (_rowTops != null && index >= 0 && index < _rowTops.Count)
                return _rowTops[index];

            return index * RowHeight;
        }

        private double GetCanvasHeight()
        {
            if (Tasks == null || Tasks.Count == 0)
                return 0;

            if (_rowTops != null && _rowTops.Count >= Tasks.Count)
                return _rowTops[Tasks.Count - 1] + RowHeight;

            return Tasks.Count * RowHeight;
        }

        private void UpdateMagnifier(MouseEventArgs e, Point canvasPosition)
        {
            if (!_magnifierEnabled
                || _dragState != null
                || _resizeState != null
                || GanttCanvas.ActualWidth <= 0
                || GanttCanvas.ActualHeight <= 0)
            {
                HideMagnifier();
                return;
            }

            CloseToolTipFromSource(e.OriginalSource as DependencyObject);

            var viewportPosition = e.GetPosition(GanttScroll);
            // Centraliza a lupa no cursor
            var left = Math.Clamp(viewportPosition.X - MagnifierSize / 2.0, 0, Math.Max(0, GanttScroll.ViewportWidth  - MagnifierSize));
            var top  = Math.Clamp(viewportPosition.Y - MagnifierSize / 2.0, 0, Math.Max(0, GanttScroll.ViewportHeight - MagnifierSize));

            // VisualBrush aponta para GanttScroll (viewport), portanto Viewbox em coordenadas da viewport
            MagnifierBrush.Viewbox = new Rect(
                Math.Clamp(viewportPosition.X - MagnifierSourceSize / 2.0, 0, Math.Max(0, GanttScroll.ViewportWidth  - MagnifierSourceSize)),
                Math.Clamp(viewportPosition.Y - MagnifierSourceSize / 2.0, 0, Math.Max(0, GanttScroll.ViewportHeight - MagnifierSourceSize)),
                MagnifierSourceSize,
                MagnifierSourceSize);

            MagnifierOverlay.Margin = new Thickness(left, top, 0, 0);
            MagnifierOverlay.Visibility = Visibility.Visible;
        }

        private void OnGanttCanvasToolTipOpening(object sender, ToolTipEventArgs e)
        {
            if (!_magnifierEnabled)
                return;

            CloseToolTipFromSource(e.OriginalSource as DependencyObject);
            e.Handled = true;
        }

        private void HideMagnifier()
        {
            if (MagnifierOverlay.Visibility == Visibility.Visible)
                MagnifierOverlay.Visibility = Visibility.Collapsed;
        }

        private static void SetToolTipsEnabled(DependencyObject root, bool enabled)
        {
            ToolTipService.SetIsEnabled(root, enabled);

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
                SetToolTipsEnabled(VisualTreeHelper.GetChild(root, i), enabled);
        }

        private static void CloseToolTips(DependencyObject root)
        {
            if (root is FrameworkElement element && element.ToolTip is ToolTip tooltip)
                tooltip.IsOpen = false;

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
                CloseToolTips(VisualTreeHelper.GetChild(root, i));
        }

        private static void CloseToolTipFromSource(DependencyObject? source)
        {
            var current = source;
            while (current != null)
            {
                if (current is FrameworkElement element && element.ToolTip is ToolTip tooltip)
                    tooltip.IsOpen = false;

                current = VisualTreeHelper.GetParent(current);
            }
        }

        private static TaskViewModel? FindTaskFromVisual(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                var task = GanttTaskElements.GetTask(current);
                if (task != null)
                    return task;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static TaskViewModel? FindDragTaskFromVisual(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is FrameworkElement element &&
                    Equals(element.Tag, "task-drag"))
                {
                    var task = GanttTaskElements.GetTask(element);
                    if (task != null)
                        return task;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void ShowDragHint(string message, Point canvasPos)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xCD)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xAC, 0x00)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Child = new TextBlock
                {
                    Text = message,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0x2B, 0x00))
                }
            };

            Canvas.SetLeft(border, Math.Max(0, canvasPos.X - 10));
            Canvas.SetTop(border, Math.Max(0, canvasPos.Y - 40));
            Canvas.SetZIndex(border, 9999);
            GanttCanvas.Children.Add(border);

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                GanttCanvas.Children.Remove(border);
            };
            timer.Start();
        }

        private void EndDrag()
        {
            if (_dragState != null)
            {
                var dayDelta = (_dragState.Task.Start - _dragState.OriginalStart).Days;
                if (dayDelta != 0)
                    MoveDependentTasks(_dragState.Task, dayDelta);
            }

            _dragState = null;
            if (Mouse.Captured == GanttCanvas)
                Mouse.Capture(null);
        }

        private void MoveDependentTasks(TaskViewModel predecessor, int dayDelta)
        {
            if (Tasks == null || Tasks.Count == 0 || dayDelta == 0)
                return;

            var tasksById = Tasks.ToDictionary(task => task.Id);
            var movedTaskIds = new HashSet<int> { predecessor.Id };
            ShiftSuccessorsRecursive(predecessor.Id, dayDelta, tasksById, movedTaskIds);
        }

        private void ShiftSuccessorsRecursive(
            int predecessorId,
            int dayDelta,
            IReadOnlyDictionary<int, TaskViewModel> tasksById,
            ISet<int> movedTaskIds)
        {
            foreach (var successor in tasksById.Values.Where(task => task.Model.PredecessorIds.Contains(predecessorId)))
            {
                if (!movedTaskIds.Add(successor.Id))
                    continue;

                successor.ShiftSchedule(dayDelta);
                ShiftSuccessorsRecursive(successor.Id, dayDelta, tasksById, movedTaskIds);
            }
        }

        private void SubscribeTaskEvents(ObservableCollection<TaskViewModel> tasks)
        {
            foreach (var task in tasks)
                task.PropertyChanged += OnTaskPropertyChanged;
        }

        private void UnsubscribeTaskEvents(ObservableCollection<TaskViewModel> tasks)
        {
            foreach (var task in tasks)
                task.PropertyChanged -= OnTaskPropertyChanged;
        }

        private int GetSprintNumberFromIndex(int sprintIndex, int firstSprint)
        {
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
            if (isEven == even)
                return normalized;

            return normalized + 1;
        }

        private sealed record TaskLayout(TaskViewModel Task, double StartX, double EndX, double CenterY);
        private sealed record TaskDragState(TaskViewModel Task, Point StartPoint, DateTime OriginalStart, int OriginalDuration);
        private sealed record TaskResizeState(TaskViewModel Task, DateTime OriginalFinish);
    }
}
