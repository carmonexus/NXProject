using System;
using System.ComponentModel;
using System.Windows;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class CommunityLicenseWindow : Window
    {
        /// <summary>true = primeira execucao (o termo precisa ser aceito para usar o app);
        /// false = consulta pelo menu Ajuda, com o termo ja aceito nesta maquina.</summary>
        public bool RequireAcceptance { get; set; }

        public CommunityLicenseWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => { ApplyLicenseLink(); ApplyMode(); };
        }

        // O link do texto completo e montado em codigo: NavigateUri e do tipo Uri e nao
        // aceita DynamicResource (a string do recurso nao e convertida e o XAML estoura
        // ao carregar). A URL vem das Strings, entao acompanha o idioma ativo.
        private void ApplyLicenseLink()
        {
            var label = AppStrings.Get("Lic_FullText");
            var url = AppStrings.Get("Lic_FullTextUrl");

            FullTextLabel.Text = label + " ";
            FullTextUrlRun.Text = url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                FullTextLink.NavigateUri = uri;
            else
                FullTextLink.IsEnabled = false;   // sem URL valida o texto fica so informativo
        }

        // Em consulta a tela nao pode pedir aceite de novo: mostra o selo "Termo aceito"
        // (com a data do aceite, quando conhecida) e apenas o botao Fechar.
        private void ApplyMode()
        {
            if (RequireAcceptance) return;

            AcceptNote.Visibility = Visibility.Collapsed;   // "Ao clicar em Aceitar..." nao cabe em consulta
            DeclineButton.Visibility = Visibility.Collapsed;
            AcceptButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;

            var acceptedOn = LicenseAcceptanceStore.AcceptedOn();
            AcceptedSeal.Text = acceptedOn is { } when
                ? AppStrings.Get("Lic_AcceptedOn", when.ToString("dd/MM/yyyy"))
                : AppStrings.Get("Lic_Accepted");
            AcceptedSeal.Visibility = Visibility.Visible;
        }

        private void OnAcceptClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void OnDeclineClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        // Abre o LICENSE.txt no GitHub no navegador padrao. O resumo desta tela e
        // apenas explicativo; o texto que vale e o do repositorio oficial.
        private void OnLicenseLinkNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Sem navegador disponivel o link apenas nao abre; a licenca tambem
                // acompanha o app em LICENSE.txt, ao lado do executavel.
            }
            e.Handled = true;
        }

        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            // X button when acceptance is required = treat as decline (DialogResult stays null → false)
            if (RequireAcceptance && DialogResult == null)
                DialogResult = false;
        }
    }
}
