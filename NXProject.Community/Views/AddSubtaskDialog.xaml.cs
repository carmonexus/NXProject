// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using NXProject.Services;

namespace NXProject.Views
{
    public enum AddSubtaskResult { Fetch, CreateTask, CreateInternal, Cancel }

    public partial class AddSubtaskDialog : Window
    {
        public AddSubtaskResult Result { get; private set; } = AddSubtaskResult.Cancel;

        public AddSubtaskDialog(string storyName, bool hasDevOpsLink)
        {
            InitializeComponent();
            SubtitleText.Text = AppStrings.Get("AddSub_Story", storyName);
            // Oculta "Buscar Tasks" se não tiver vínculo DevOps
            BtnFetch.Visibility = hasDevOpsLink ? Visibility.Visible : Visibility.Collapsed;
            BtnTask.Visibility  = hasDevOpsLink ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnFetchClick(object sender, RoutedEventArgs e)
        {
            Result = AddSubtaskResult.Fetch;
            Close();
        }

        private void OnCreateTaskClick(object sender, RoutedEventArgs e)
        {
            Result = AddSubtaskResult.CreateTask;
            Close();
        }

        private void OnCreateInternalClick(object sender, RoutedEventArgs e)
        {
            Result = AddSubtaskResult.CreateInternal;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Result = AddSubtaskResult.Cancel;
            Close();
        }
    }
}
