// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Globalization;
using System.Windows;

namespace NXProject.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Idioma do instalador segue a cultura do Windows: pt → português, senão inglês.
        var code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("pt", StringComparison.OrdinalIgnoreCase) ? "pt-BR" : "en-US";

        var dict = new ResourceDictionary
        {
            Source = new Uri($"Strings/Strings.{code}.xaml", UriKind.Relative)
        };
        Resources.MergedDictionaries.Add(dict);
    }

    /// <summary>Obtém uma string localizada do dicionário ativo, com format opcional.</summary>
    public static string Str(string key, params object[] args)
    {
        var val = Current?.TryFindResource(key) as string ?? key;
        return args.Length > 0 ? string.Format(val, args) : val;
    }
}
