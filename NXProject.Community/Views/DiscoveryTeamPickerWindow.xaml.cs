// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using NXProject.Services;
using static NXProject.Services.TfsImportService;

namespace NXProject.Views
{
    public partial class DiscoveryTeamPickerWindow : Window
    {
        private readonly List<DevOpsTeamInfo> _all;

        public DevOpsTeamInfo? SelectedTeam { get; private set; }

        public DiscoveryTeamPickerWindow(List<DevOpsTeamInfo> teams)
        {
            InitializeComponent();
            _all = teams ?? new List<DevOpsTeamInfo>();
            ApplyFilter(string.Empty);
            Loaded += (_, _) => FilterBox.Focus();
        }

        private void ApplyFilter(string term)
        {
            IEnumerable<DevOpsTeamInfo> filtered = _all;
            if (!string.IsNullOrWhiteSpace(term))
                filtered = _all.Where(t => t.Name.Contains(term, System.StringComparison.OrdinalIgnoreCase));

            var list = filtered.ToList();
            TeamGrid.ItemsSource = list;
            CountLabel.Text = AppStrings.Get("TeamPick_Count", list.Count, _all.Count);
        }

        private void OnFilterChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => ApplyFilter(FilterBox.Text);

        private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (TeamGrid.SelectedItem is DevOpsTeamInfo)
                Confirm();
        }

        private void OnImportClick(object sender, RoutedEventArgs e) => Confirm();

        private void Confirm()
        {
            if (TeamGrid.SelectedItem is not DevOpsTeamInfo team)
            {
                MessageBox.Show(this, AppStrings.Get("TeamPick_SelectOne"), AppStrings.Get("TeamPick_Title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SelectedTeam = team;
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
