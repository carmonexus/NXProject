using System.Windows;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class DevOpsProjectEditWindow : Window
    {
        public DevOpsProject? Result { get; private set; }

        public DevOpsProjectEditWindow(string name = "", int id = 0,
                                       bool isOpex = true, string costCenter = "",
                                       string costCenterSource = "")
        {
            InitializeComponent();
            NameBox.Text = name;
            IdBox.Text   = id > 0 ? id.ToString() : "";

            TypeBox.Items.Add("OPEX");
            TypeBox.Items.Add("CAPEX");
            TypeBox.Items.Add("EPIC");

            var source = string.IsNullOrWhiteSpace(costCenterSource)
                ? (isOpex ? "OPEX" : "CAPEX")
                : costCenterSource.ToUpperInvariant();

            TypeBox.SelectedIndex = source switch { "CAPEX" => 1, "EPIC" => 2, _ => 0 };

            CcBox.Text = costCenter;

            Loaded += (_, _) => NameBox.Focus();
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(AppStrings.Get("PortEdit_NameRequired"), AppStrings.Get("Common_Validation"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(IdBox.Text?.Trim(), out var id) || id <= 0)
            {
                MessageBox.Show(AppStrings.Get("PortEdit_IdInvalid"), AppStrings.Get("Common_Validation"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var src = (TypeBox.SelectedItem as string) ?? "OPEX";
            Result = new DevOpsProject
            {
                Name             = name,
                RootWorkItemId   = id,
                IsOpex           = src != "CAPEX",
                CostCenter       = CcBox.Text?.Trim() ?? "",
                CostCenterSource = src
            };
            DialogResult = true;
            Close();
        }
    }
}
