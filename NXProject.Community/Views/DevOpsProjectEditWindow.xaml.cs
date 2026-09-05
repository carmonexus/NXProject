// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class DevOpsProjectEditWindow : Window
    {
        public DevOpsProject? Result { get; private set; }

        private readonly string _process;
        private readonly bool? _readOnly;
        private readonly string _admGroup;

        public DevOpsProjectEditWindow(string name = "", int id = 0,
                                       bool isOpex = true, string costCenter = "",
                                       string costCenterSource = "", string process = "",
                                       bool? readOnly = null, string admGroup = "")
        {
            InitializeComponent();
            NameBox.Text = name;
            IdBox.Text   = id > 0 ? id.ToString() : "";
            _process = process ?? "";
            ProcessBox.Text = string.IsNullOrWhiteSpace(_process)
                ? AppStrings.Get("PortEdit_ProcessUnknown") : _process;
            _readOnly = readOnly;
            _admGroup = admGroup ?? "";
            AdmGroupBox.Text = string.IsNullOrWhiteSpace(_admGroup)
                ? AppStrings.Get("PortEdit_AdmGroupNone") : _admGroup;

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
                CostCenterSource = src,
                Process          = _process,   // read-only nesta tela; preserva o lido do DevOps
                ReadOnly         = _readOnly,   // preservado (compat.); não editável nesta tela
                AdmGroupName     = _admGroup    // read-only; vem do campo Adm_NX na importação
            };
            DialogResult = true;
            Close();
        }
    }
}
