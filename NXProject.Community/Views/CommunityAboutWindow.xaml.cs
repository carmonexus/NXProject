using System;
using System.Diagnostics;
using System.Windows;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class CommunityAboutWindow : Window
    {
        private const string ContactEmail = "comercial.nexus.xdata@gamail.com";

        public CommunityAboutWindow()
        {
            InitializeComponent();
            CompanyLogoImage.Source = ProtectedLogoProvider.GetLogoImage();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnEmailClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo($"mailto:{ContactEmail}") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Nao foi possivel abrir o cliente de e-mail.\n\n{ex.Message}",
                    "Contato",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
    }
}
