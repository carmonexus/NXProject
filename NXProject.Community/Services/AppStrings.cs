// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;

namespace NXProject.Services
{
    /// <summary>Gets localized strings from the active ResourceDictionary (Strings.*.xaml).</summary>
    public static class AppStrings
    {
        public static string Get(string key, params object[] args)
        {
            var val = Application.Current?.TryFindResource(key) as string ?? key;
            return args.Length > 0 ? string.Format(val, args) : val;
        }
    }
}
