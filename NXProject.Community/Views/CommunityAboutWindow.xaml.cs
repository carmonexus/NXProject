// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class CommunityAboutWindow : Window
    {
        private const string ContactEmail = "comercial.nexus.xdata@gmail.com";
        private const string NxStoreUrl = "https://github.com/nexusxdata/NXProject";

        public CommunityAboutWindow()
        {
            InitializeComponent();
            CompanyLogoImage.Source = ProtectedLogoProvider.GetLogoImage();

            var v = Assembly.GetExecutingAssembly().GetName().Version;
            var ver = v != null ? $"{v.Major}.{v.Minor}.{v.Build} build({v.Revision})" : "?";
            Title = $"{AppStrings.Get("About_Title")} {ver}";
            VersionText.Text = AppStrings.Get("About_VersionLabel", ver);
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
                    AppStrings.Get("About_EmailError", ex.Message),
                    AppStrings.Get("About_ContactTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void OnNxStoreClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(NxStoreUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    AppStrings.Get("About_NxStoreError", NxStoreUrl, ex.Message),
                    AppStrings.Get("About_NxStoreTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
    }
}
