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

        public static readonly DependencyProperty HeaderHeightProperty =
            DependencyProperty.Register(nameof(HeaderHeight), typeof(double),
                typeof(GanttControl), new PropertyMetadata(40.0, OnLayoutChanged));

        public static readonly DependencyProperty SelectedTaskProperty =
            DependencyProperty.Register(nameof(SelectedTask), typeof(TaskViewModel),
                typeof(GanttControl), new PropertyMetadata(null, OnSelectedTaskChanged));

        private const double RowHeight = 22;
        private const double BarPadding = 4;
        private const double LeftPadding = 16;
        private const double DependencyMargin = 8;

        private bool _renderScheduled;
        private bool _resetScrollOnNextRender;
        private bool _suppressScrollNotification;
        private IReadOnlyList<double>? _rowTops;
        private TaskDragState? _dragState;

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

        public TaskViewModel? SelectedTask
        {
            get => (TaskViewModel?)GetValue(SelectedTaskProperty);
            set => SetValue(SelectedTaskProperty, value);
        }

        private double DayWidth => ZoomLevel switch
        {
            "Dia" => 30,
            "Semana" => 8,
            "Mês" => 3,
            "Trimestre" => 1.2,
            _ => 8
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
            _suppressScrollNotification = true;
            GanttScroll.ScrollToVerticalOffset(offset);
            _suppressScrollNotification = false;
        }

        public void ScrollToProjectStart() => ScheduleRender(resetScroll: true);

        public void ForceRender() => ScheduleRender();

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

            ScheduleRender(resetScroll: e.Action == NotifyCollectionChangedAction.Reset);
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
                    if (dragTask != null && !dragTask.IsSummary)
                    {
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
            if (_dragState == null || e.ChangedButton != MouseButton.Left)
                return;

            EndDrag();
            e.Handled = true;
        }

        private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
        {
            if (_dragState == null || e.LeftButton == MouseButtonState.Pressed)
                return;

            EndDrag();
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

            var totalDays = 365;
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
            var totalDays = 365;

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
        }

        private void RenderGrid(int totalDays, double canvasWidth)
        {
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
                bool drawLine = ZoomLevel switch
                {
                    "Dia" => true,
                    "Semana" => date.DayOfWeek == DayOfWeek.Monday,
                    "Mês" => date.Day == 1,
                    "Trimestre" => date.Day == 1 && (date.Month % 3 == 1),
                    _ => date.DayOfWeek == DayOfWeek.Monday
                };

                if (!drawLine) continue;

                GanttCanvas.Children.Add(new Line
                {
                    X1 = LeftPadding + (d * DayWidth),
                    Y1 = 0,
                    X2 = LeftPadding + (d * DayWidth),
                    Y2 = GanttCanvas.Height,
                    Stroke = Brushes.WhiteSmoke,
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
                var y = GetRowTop(i);
                var startOffset = (vm.Start - ProjectStart).TotalDays;
                var endOffset = (vm.Finish - ProjectStart).TotalDays;
                var x = LeftPadding + (startOffset * DayWidth);
                var width = Math.Max(1, (endOffset - startOffset) * DayWidth);

                RenderRowHitArea(vm, y);

                if (vm.DisplayAsMilestone)
                    RenderMilestone(vm, x, y, isSelected);
                else if (vm.IsSummary)
                    RenderSummaryBar(vm, x, y, width, isSelected);
                else
                    RenderTaskBar(vm, x, y, width, vm.PercentComplete, isSelected);
            }
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

        private void RenderTaskBar(TaskViewModel task, double x, double y, double width, double percent, bool isSelected)
        {
            if (width < 3)
            {
                RenderTaskMarker(task, x, y, isSelected);
                return;
            }

            var bgColor = isSelected ? Color.FromRgb(220, 124, 0) : Color.FromRgb(68, 114, 196);
            var bg = new Rectangle
            {
                Width = width,
                Height = RowHeight - BarPadding * 2,
                Fill = new SolidColorBrush(bgColor),
                RadiusX = 2,
                RadiusY = 2
            };
            AttachTaskMetadata(bg, task);
            Canvas.SetLeft(bg, x);
            Canvas.SetTop(bg, y + BarPadding);
            GanttCanvas.Children.Add(bg);

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

            if (percent > 0)
            {
                var progress = new Rectangle
                {
                    Width = width * percent / 100.0,
                    Height = RowHeight - BarPadding * 2,
                    Fill = new SolidColorBrush(Color.FromRgb(33, 115, 70)),
                    RadiusX = 2,
                    RadiusY = 2
                };
                AttachTaskMetadata(progress, task);
                Canvas.SetLeft(progress, x);
                Canvas.SetTop(progress, y + BarPadding);
                GanttCanvas.Children.Add(progress);
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

        private void RenderSummaryBar(TaskViewModel task, double x, double y, double width, bool isSelected)
        {
            var barColor = isSelected ? Color.FromRgb(220, 124, 0) : Color.FromRgb(43, 87, 154);
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
            content.Children.Add(CreateHintLine("Inicio", task.Start.ToString("d", CultureInfo.CurrentCulture)));
            content.Children.Add(CreateHintLine("Fim", task.Finish.ToString("d", CultureInfo.CurrentCulture)));
            content.Children.Add(CreateHintLine("Duracao", $"{task.DurationDays} dia(s)"));
            content.Children.Add(CreateHintLine("Concluido", $"{task.PercentComplete:0}%"));

            if (!string.IsNullOrWhiteSpace(task.PredecessorsText))
                content.Children.Add(CreateHintLine("Predecessoras", task.PredecessorsText));

            if (!string.IsNullOrWhiteSpace(task.ResourcesText))
                content.Children.Add(CreateHintLine("Recursos", task.ResourcesText));

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

        private sealed record TaskLayout(TaskViewModel Task, double StartX, double EndX, double CenterY);
        private sealed record TaskDragState(TaskViewModel Task, Point StartPoint, DateTime OriginalStart, int OriginalDuration);
    }
}
