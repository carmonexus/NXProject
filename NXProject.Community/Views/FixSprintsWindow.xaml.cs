// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Collections.Generic;
using System.Windows;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Views
{
    public partial class FixSprintsWindow : Window
    {
        private readonly IReadOnlyList<MainViewModel.SprintPeriodFix> _fixes;

        public FixSprintsWindow(IReadOnlyList<MainViewModel.SprintPeriodFix> fixes)
        {
            InitializeComponent();
            _fixes = fixes;
            IntroText.Text = AppStrings.Get("FixSprints_Confirm", fixes.Count);
            FixGrid.ItemsSource = fixes;
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
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
