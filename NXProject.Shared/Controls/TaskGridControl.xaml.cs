using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NXProject.ViewModels;

namespace NXProject.Controls
{
    public partial class TaskGridControl : UserControl
    {
        public static readonly DependencyProperty TasksProperty =
            DependencyProperty.Register(nameof(Tasks), typeof(ObservableCollection<TaskViewModel>),
                typeof(TaskGridControl), new PropertyMetadata(null, OnTasksChanged));

        public ObservableCollection<TaskViewModel>? Tasks
        {
            get => (ObservableCollection<TaskViewModel>?)GetValue(TasksProperty);
            set
            {
                SetValue(TasksProperty, value);
                // Resetar cache quando tasks mudam, para forçar re-cálculo de layout
                _lastRowLayoutSignature = null;
            }
        }

        /// <summary>Disparado quando o DataGrid rola verticalmente.</summary>
        public event Action<double>? VerticalScrollChanged;

        /// <summary>Disparado quando a altura real do header do DataGrid e conhecida.</summary>
        public event Action<double>? HeaderHeightMeasured;

        /// <summary>Disparado quando as linhas reais do DataGrid mudam de posição.</summary>
        public event Action<IReadOnlyList<double>>? RowTopsMeasured;
        public event Action<TaskViewModel, TaskViewModel, bool>? TaskMoveRequested;

        private bool _headerMeasured;
        private ScrollViewer? _scrollViewer;
        private bool _suppressScrollNotification;
        private string? _lastRowLayoutSignature;
        private Point _dragStartPoint;
        private TaskViewModel? _dragSourceTask;

        public TaskGridControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            TaskGrid.LayoutUpdated += OnLayoutUpdated;
        }

        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            var header = FindChild<DataGridColumnHeadersPresenter>(TaskGrid);
            if (header == null || header.ActualHeight == 0) return;

            if (!_headerMeasured)
            {
                _headerMeasured = true;
                HeaderHeightMeasured?.Invoke(header.ActualHeight);
                // Publicar RowTops apenas na primeira vez que o header é medido
                PublishRowTops(header.ActualHeight);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _scrollViewer = FindChild<ScrollViewer>(TaskGrid);
            if (_scrollViewer == null) return;

            _scrollViewer.ScrollChanged += (_, args) =>
            {
                if (_suppressScrollNotification) return;
                if (args.VerticalChange != 0 || args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0)
                    VerticalScrollChanged?.Invoke(_scrollViewer.VerticalOffset);
            };
        }

        public void SyncVerticalOffset(double offset)
        {
            if (_scrollViewer == null) return;
            
            _suppressScrollNotification = true;
            _scrollViewer.ScrollToVerticalOffset(offset);
            _suppressScrollNotification = false;
        }

        private void PublishRowTops(double headerHeight)
        {
            var itemCount = TaskGrid.Items.Count;
            if (itemCount == 0) return;

            var rowTops = new double[itemCount];
            var measuredAnyRow = false;

            for (int i = 0; i < itemCount; i++)
            {
                var row = TaskGrid.ItemContainerGenerator.ContainerFromIndex(i) as DataGridRow;
                if (row != null)
                {
                    rowTops[i] = row.TranslatePoint(new Point(0, 0), TaskGrid).Y - headerHeight;
                    measuredAnyRow = true;
                }
                else
                {
                    rowTops[i] = i * TaskGrid.RowHeight;
                }
            }

            if (!measuredAnyRow) return;

            // Apenas comparar assinatura para evitar renders redundantes
            var signature = string.Join("|", rowTops.Select(v => Math.Round(v, 2).ToString("0.##")));
            if (signature == _lastRowLayoutSignature) return;

            _lastRowLayoutSignature = signature;
            RowTopsMeasured?.Invoke(rowTops);
        }

        private static void OnTasksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (TaskGridControl)d;
            ctrl._lastRowLayoutSignature = null;
            // Agendar recalcular RowTops após o layout ser atualizado
            ctrl.Dispatcher.BeginInvoke(() =>
            {
                var header = FindChild<DataGridColumnHeadersPresenter>(ctrl.TaskGrid);
                if (header != null && header.ActualHeight > 0)
                    ctrl.PublishRowTops(header.ActualHeight);
            });
        }

        private void OnTaskGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(TaskGrid);
            _dragSourceTask = FindTaskViewModel(e.OriginalSource as DependencyObject);
        }

        private void OnTaskGridPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragSourceTask == null)
                return;

            var currentPosition = e.GetPosition(TaskGrid);
            if (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            var dragTask = _dragSourceTask;
            _dragSourceTask = null;
            DragDrop.DoDragDrop(TaskGrid, new DataObject(typeof(TaskViewModel), dragTask), DragDropEffects.Move);
        }

        private void OnTaskGridDragOver(object sender, DragEventArgs e)
        {
            var sourceTask = e.Data.GetData(typeof(TaskViewModel)) as TaskViewModel;
            var targetTask = FindTaskViewModel(e.OriginalSource as DependencyObject);
            e.Effects = sourceTask != null && targetTask != null && sourceTask != targetTask
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnTaskGridDrop(object sender, DragEventArgs e)
        {
            var sourceTask = e.Data.GetData(typeof(TaskViewModel)) as TaskViewModel;
            var targetTask = FindTaskViewModel(e.OriginalSource as DependencyObject);
            if (sourceTask == null || targetTask == null || sourceTask == targetTask)
                return;

            var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
            var insertAfter = false;
            if (row != null)
            {
                var rowPosition = e.GetPosition(row);
                insertAfter = rowPosition.Y > row.ActualHeight / 2;
            }

            TaskMoveRequested?.Invoke(sourceTask, targetTask, insertAfter);
            e.Handled = true;
        }

        private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) return t;
                var found = FindChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private static TaskViewModel? FindTaskViewModel(DependencyObject? source)
        {
            return FindParent<DataGridRow>(source)?.Item as TaskViewModel;
        }

        private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
        {
            var current = source;
            while (current != null)
            {
                if (current is T match)
                    return match;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
