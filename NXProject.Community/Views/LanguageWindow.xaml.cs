// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using System.Windows.Controls;
using NXProject.Community.Services;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class LanguageWindow : Window
    {
        private string _selectedLanguage;

        public LanguageWindow()
        {
            InitializeComponent();
            _selectedLanguage = LanguageService.CurrentLanguage;

            // Seleciona o item correspondente ao idioma atual
            foreach (ComboBoxItem item in LanguageCombo.Items)
            {
                if (item.Tag?.ToString() == _selectedLanguage)
                {
                    LanguageCombo.SelectedItem = item;
                    break;
                }
            }

            if (LanguageCombo.SelectedItem == null)
                LanguageCombo.SelectedIndex = 0;
        }

        private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LanguageCombo.SelectedItem is ComboBoxItem item)
                _selectedLanguage = item.Tag?.ToString() ?? "pt-BR";
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            // Aplica e persiste
            LanguageService.Apply(_selectedLanguage);

            var opts = TfsConnectionStore.Load();
            opts.Language = _selectedLanguage;
            // rememberToken: keep existing - we only change language
            var rememberToken = !string.IsNullOrEmpty(opts.PersonalAccessToken);
            TfsConnectionStore.Save(opts, rememberToken);

            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
