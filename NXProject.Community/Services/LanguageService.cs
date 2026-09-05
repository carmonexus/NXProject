// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace NXProject.Community.Services
{
    public static class LanguageService
    {
        private const string PtBR = "pt-BR";
        private const string EnUS = "en-US";

        public static string CurrentLanguage { get; private set; } = PtBR;

        public static event Action? LanguageChanged;

        public static string DetectFromWindows()
        {
            var culture = CultureInfo.CurrentUICulture;
            return culture.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase)
                ? PtBR
                : EnUS;
        }

        public static string Str(string key, params object[] args)
        {
            var val = Application.Current?.TryFindResource(key) as string ?? key;
            return args.Length > 0 ? string.Format(val, args) : val;
        }

        public static void Apply(string languageCode)
        {
            var code = languageCode == EnUS ? EnUS : PtBR;
            var culture = CultureInfo.GetCultureInfo(code);
            CurrentLanguage = code;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;

            if (Application.Current?.MainWindow is FrameworkElement mainWindow)
                mainWindow.Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);

            var uri = new Uri($"Strings/Strings.{code}.xaml", UriKind.Relative);
            var dict = new ResourceDictionary { Source = uri };

            var app = Application.Current;
            if (app == null)
                return;

            // Remove qualquer dicionário de strings anterior
            for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                var src = app.Resources.MergedDictionaries[i].Source?.OriginalString ?? "";
                if (src.Contains("Strings/Strings."))
                    app.Resources.MergedDictionaries.RemoveAt(i);
            }
            app.Resources.MergedDictionaries.Add(dict);
            LanguageChanged?.Invoke();
        }
    }
}
