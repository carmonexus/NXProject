using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>
    /// Tratamento CENTRAL dos erros das chamadas ao Azure DevOps/TFS.
    /// Quando o erro é de autenticação (PAT vencido/revogado/sem escopo) mostra uma mensagem
    /// clara com atalho para a configuração do token, em vez do stack trace da API.
    /// O DevOps responde 404 + TF401232 quando o token não pode ler o work item, então
    /// "não existe ou você não tem permissão" também cai aqui.
    /// </summary>
    public static class TfsErrorDialog
    {
        private static readonly string[] AuthMarkers =
        {
            "TF401232",                              // work item não existe OU sem permissão
            "TF400813",                              // usuário não autorizado
            "WorkItemUnauthorizedAccessException",
            "UnauthorizedRequestException",
            "Unauthorized",
            "401",
            "403",
            "Access Denied",
            "não foi possível autenticar",
            "Falha de autenticação",
            "VS30063"                                // não autorizado a acessar a organização
        };

        /// <summary>Identifica erro de token vencido / sem permissão nas respostas do DevOps.</summary>
        public static bool IsAuthError(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var msg = e.Message ?? "";
                foreach (var marker in AuthMarkers)
                    if (msg.Contains(marker, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            return false;
        }

        /// <summary>
        /// Mostra o erro de uma operação TFS. <paramref name="action"/> descreve o que estava
        /// sendo feito ("Sincronizar com o TFS", "Importar do TFS"...).
        /// </summary>
        public static void Show(Window? owner, string action, Exception ex)
        {
            if (!IsAuthError(ex))
            {
                MessageBox.Show(owner!,
                    AppStrings.Get("Tfs_GenericError", action, ex.Message),
                    "NXProject", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ShowTokenExpired(owner, action, ex);
        }

        private static void ShowTokenExpired(Window? owner, string action, Exception ex)
        {
            var win = new Window
            {
                Title                 = AppStrings.Get("Tfs_TokenExpiredTitle"),
                Width                 = 560,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = owner == null
                    ? WindowStartupLocation.CenterScreen
                    : WindowStartupLocation.CenterOwner,
                Owner                 = owner,
                ResizeMode            = ResizeMode.NoResize,
                Background            = Brushes.White
            };

            var root = new StackPanel { Margin = new Thickness(18) };

            root.Children.Add(new TextBlock
            {
                Text         = AppStrings.Get("Tfs_TokenExpiredHeader"),
                FontSize     = 15,
                FontWeight   = FontWeights.SemiBold,
                Foreground   = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 10)
            });

            root.Children.Add(new TextBlock
            {
                Text         = AppStrings.Get("Tfs_TokenExpiredBody", action),
                FontSize     = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 12)
            });

            // Detalhe técnico recolhido: útil no suporte, fora do caminho do usuário.
            var details = new StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
                details.AppendLine($"{e.GetType().Name}: {e.Message}");

            root.Children.Add(new Expander
            {
                Header  = AppStrings.Get("Tfs_TokenExpiredDetails"),
                FontSize = 11,
                Margin  = new Thickness(0, 0, 0, 12),
                Content = new TextBox
                {
                    Text         = details.ToString().TrimEnd(),
                    IsReadOnly   = true,
                    FontSize     = 11,
                    MaxHeight    = 180,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Margin       = new Thickness(0, 6, 0, 0)
                }
            });

            var buttons = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var configure = new Button
            {
                Content    = AppStrings.Get("Tfs_TokenExpiredConfigure"),
                Width      = 190,
                Height     = 32,
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)),
                Foreground = Brushes.White,
                Margin     = new Thickness(0, 0, 8, 0)
            };
            configure.Click += (_, _) =>
            {
                win.DialogResult = true;
                win.Close();
                new TfsDevOpsConfigWindow("NXProject.Community") { Owner = owner }.ShowDialog();
            };
            buttons.Children.Add(configure);

            var close = new Button
            {
                Content  = AppStrings.Get("Tfs_TokenExpiredClose"),
                Width    = 100,
                Height   = 32,
                IsCancel = true
            };
            close.Click += (_, _) => win.Close();
            buttons.Children.Add(close);

            root.Children.Add(buttons);
            win.Content = root;
            win.ShowDialog();
        }
    }
}
