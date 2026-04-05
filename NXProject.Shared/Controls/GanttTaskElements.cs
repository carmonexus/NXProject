using System.Windows;
using NXProject.ViewModels;

namespace NXProject.Controls
{
    public static class GanttTaskElements
    {
        public static readonly DependencyProperty TaskProperty =
            DependencyProperty.RegisterAttached(
                "Task",
                typeof(TaskViewModel),
                typeof(GanttTaskElements),
                new PropertyMetadata(null));

        public static void SetTask(DependencyObject element, TaskViewModel value) =>
            element.SetValue(TaskProperty, value);

        public static TaskViewModel? GetTask(DependencyObject element) =>
            (TaskViewModel?)element.GetValue(TaskProperty);
    }
}
