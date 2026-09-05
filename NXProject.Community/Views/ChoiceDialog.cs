// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NXProject.Views
{
    /// <summary>
    /// Diálogo de escolha com um botão por opção, empilhados na vertical — para perguntas em
    /// que os botões Sim/Não/Cancelar do MessageBox não dizem o que cada um faz.
    /// </summary>
    public static class ChoiceDialog
    {
        public sealed record Option(string Title, string? Description, int Result, bool IsPrimary = false);

        /// <summary>Mostra as opções e devolve o Result da escolhida; -1 se fechar sem escolher.</summary>
        public static int Ask(Window? owner, string title, string message, IReadOnlyList<Option> options)
        {
            var win = new Window
            {
                Title                 = title,
                Width                 = 460,
                SizeToContent         = SizeToContent.Height,
                ResizeMode            = ResizeMode.NoResize,
                ShowInTaskbar         = false,
                Owner                 = owner,
                WindowStartupLocation = owner == null
                    ? WindowStartupLocation.CenterScreen
                    : WindowStartupLocation.CenterOwner,
                Background            = Brushes.White
            };

            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock
            {
                Text         = message,
                FontSize     = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 14)
            });

            int result = -1;
            foreach (var option in options)
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text       = option.Title,
                    FontSize   = 13,
                    FontWeight = option.IsPrimary ? FontWeights.SemiBold : FontWeights.Normal
                });
                if (!string.IsNullOrWhiteSpace(option.Description))
                    content.Children.Add(new TextBlock
                    {
                        Text         = option.Description,
                        FontSize     = 11,
                        Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                        TextWrapping = TextWrapping.Wrap,
                        Margin       = new Thickness(0, 2, 0, 0)
                    });

                var button = new Button
                {
                    Content             = content,
                    Padding             = new Thickness(12, 8, 12, 8),
                    Margin              = new Thickness(0, 0, 0, 8),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background          = option.IsPrimary
                        ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFC))
                        : Brushes.White,
                    BorderBrush         = new SolidColorBrush(Color.FromRgb(0xB4, 0xC8, 0xE6)),
                    Cursor              = System.Windows.Input.Cursors.Hand
                };
                var captured = option.Result;
                button.Click += (_, _) => { result = captured; win.Close(); };
                root.Children.Add(button);
            }

            win.Content = root;
            win.ShowDialog();
            return result;
        }
    }
}
