// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using NXProject.Community.Services;
using NXProject.Services;

namespace NXProject
{
    public partial class CommunityApp : Application
    {
        public CommunityApp()
        {
            var culture = CultureInfo.CurrentCulture;

            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        }

        /// <summary>
        /// Biblioteca ausente (ex.: dependência nova que ainda não está na instalação):
        /// orienta a rodar o NXProject-Setup em vez de mostrar o erro técnico. True se tratou.
        /// </summary>
        public static bool ShowMissingLibraryMessage(Exception ex)
        {
            for (Exception? cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is System.IO.FileNotFoundException or System.IO.FileLoadException
                    && (cur.Message.Contains("assembly", StringComparison.OrdinalIgnoreCase)
                        || cur.Message.Contains("Could not load", StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show(
                        "Uma biblioteca necessária não foi encontrada nesta instalação.\n\n" +
                        "Isso acontece quando uma atualização traz uma dependência nova. " +
                        "Baixe e execute o NXProject-Setup mais recente para completar a instalação:\n" +
                        "https://github.com/nexusxdata/NXProject/releases\n\n" +
                        $"Detalhe técnico: {cur.Message}",
                        "NXProject — reinstalação necessária",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Setup pediu para já baixar a IA Local (LLaMA) ao abrir? (arg --install-llama)</summary>
        public static bool InstallLlamaOnStart { get; private set; }

        /// <summary>Lê e ZERA o pedido de instalar a IA Local (executa uma única vez).</summary>
        public static bool ConsumeInstallLlama()
        {
            var v = InstallLlamaOnStart;
            InstallLlamaOnStart = false;
            return v;
        }

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            base.OnStartup(e);

            if (e.Args != null)
                foreach (var a in e.Args)
                    if (string.Equals(a?.Trim(), "--install-llama", StringComparison.OrdinalIgnoreCase))
                        InstallLlamaOnStart = true;

            // Captura exceções não tratadas para exibir mensagem em vez de fechar silenciosamente
            DispatcherUnhandledException += (_, args) =>
            {
                args.Handled = true;
                if (ShowMissingLibraryMessage(args.Exception)) return;
                MessageBox.Show(
                    $"Erro inesperado:\n\n{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                    "Erro — NXProject",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex && ShowMissingLibraryMessage(ex)) return;
                var msg = args.ExceptionObject?.ToString() ?? "(sem detalhes)";
                MessageBox.Show($"Erro crítico:\n\n{msg}", "Erro — NXProject", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            // Carrega idioma salvo; se vazio, detecta pelo Windows
            var saved = TfsConnectionStore.Load();

            SprintAlertLog.Enabled = saved.DebugLogEnabled;

            var lang = string.IsNullOrWhiteSpace(saved.Language)
                ? LanguageService.DetectFromWindows()
                : saved.Language;

            LanguageService.Apply(lang);
        }
    }
}
