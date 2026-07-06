using System;
using System.IO;
using System.Windows;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class SaveCostConfigDialog : Window
    {
        public string FilePath { get; private set; } = "";
        public string Password { get; private set; } = "";

        public SaveCostConfigDialog()
        {
            InitializeComponent();
            FolderBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            FileNameBox.TextChanged += (_, _) => Validate();
            Loaded += (_, _) => Validate();
        }

        private void OnChangeFolderClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = AppStrings.Get("SaveCost_FolderDlgTitle"),
                InitialDirectory = FolderBox.Text
            };
            if (dlg.ShowDialog() == true)
                FolderBox.Text = dlg.FolderName;
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e) => Validate();

        private void Validate()
        {
            if (OkButton == null) return;
            string p1 = PasswordBox1.Password, p2 = PasswordBox2.Password;
            bool hasPass = p1.Length > 0;
            bool match   = p1 == p2;
            bool hasName = !string.IsNullOrWhiteSpace(FileNameBox.Text);

            MismatchText.Visibility = (p2.Length > 0 && !match) ? Visibility.Visible : Visibility.Collapsed;
            OkButton.IsEnabled = hasPass && match && hasName;
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (PasswordBox1.Password.Length == 0)
            {
                MessageBox.Show(AppStrings.Get("SaveCost_PasswordRequired"), AppStrings.Get("SaveCost_InvalidPassword"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var name = FileNameBox.Text.Trim();
            if (!name.EndsWith(".nxcost", StringComparison.OrdinalIgnoreCase))
                name += ".nxcost";
            FilePath     = Path.Combine(FolderBox.Text, name);
            Password     = PasswordBox1.Password;
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
