// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

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
