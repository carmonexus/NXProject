using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>Uma linha da lista de stories/tasks do popup de composição.</summary>
    public sealed record StoryListRow(
        string Name,
        string Type,
        string StoryName,
        string TaskCount,
        string TotalHours,
        string PeriodHours,
        string PercentDone,
        string Start,
        string Finish,
        string? DevOpsUrl,
        Action? OnOpenTasks = null,
        string Responsible = "",
        // Hint da hierarquia (EPIC / Feature) mostrado ao passar o mouse no nome.
        string? Hierarchy = null);

    /// <summary>
    /// Popup "quais atividades compõem este número", usado pelo Mapa de Alocação (ao clicar nas
    /// horas da Story) e pela Curva S (ao clicar num ponto da semana). Mesma grade e mesmo visual.
    /// </summary>
    public static class StoryListPopup
    {
        public static void Show(Window owner, string title, string headerText,
                                string periodColumnHeader, IReadOnlyList<StoryListRow> rows,
                                string footerText)
        {
            var win = new Window
            {
                Title                 = title,
                Width                 = 920,
                Height                = 440,
                MinWidth              = 600,
                MinHeight             = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = owner,
                Background            = Brushes.White,
                ResizeMode            = ResizeMode.CanResize
            };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text         = headerText,
                FontSize     = 13,
                FontWeight   = FontWeights.SemiBold,
                Foreground   = new SolidColorBrush(Color.FromRgb(43, 87, 154)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            var panel = new StackPanel();
            panel.Children.Add(MakeRow(new StoryListRow(
                AppStrings.Get("PMap_ColStory"), AppStrings.Get("PMap_SrColType"),
                AppStrings.Get("PMap_SrColStoryName"), AppStrings.Get("PMap_SrColQtdTasks"),
                AppStrings.Get("PMap_SrColHHTotal"), periodColumnHeader,
                AppStrings.Get("PMap_SrColPctDone"), AppStrings.Get("PMap_SrColStart"),
                AppStrings.Get("PMap_SrColFinish"), null, null,
                AppStrings.Get("PMap_SrColResponsible")), isHeader: true,
                devOpsFallback: AppStrings.Get("PMap_SrColDevOps")));

            foreach (var row in rows)
                panel.Children.Add(MakeRow(row, isHeader: false));

            if (rows.Count == 0)
                panel.Children.Add(new TextBlock
                {
                    Text       = AppStrings.Get("PMap_NoStories"),
                    Margin     = new Thickness(8, 6, 8, 0),
                    FontSize   = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120))
                });

            var sv = new ScrollViewer
            {
                Content                       = panel,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(sv, 1);
            grid.Children.Add(sv);

            var footer = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(235, 240, 252)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(180, 200, 230)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding         = new Thickness(6, 4, 6, 4),
                Margin          = new Thickness(0, 2, 0, 0),
                Child = new TextBlock
                {
                    Text                = footerText,
                    FontSize            = 12,
                    FontWeight          = FontWeights.SemiBold,
                    Foreground          = new SolidColorBrush(Color.FromRgb(20, 60, 140)),
                    HorizontalAlignment = HorizontalAlignment.Right
                }
            };
            Grid.SetRow(footer, 2);
            grid.Children.Add(footer);

            win.Content = grid;
            win.ShowDialog();
        }

        /// <summary>Linha da grade — usada também pelo popup do Mapa de Alocação.</summary>
        public static UIElement MakeRow(StoryListRow row, bool isHeader, string? devOpsFallback = null)
        {
            var bg = isHeader
                ? new SolidColorBrush(Color.FromRgb(43, 87, 154))
                : (Brush)Brushes.Transparent;
            var fgColor = isHeader ? Colors.White : Color.FromRgb(30, 30, 30);
            var fw = isHeader ? FontWeights.SemiBold : FontWeights.Normal;

            var border = new Border
            {
                Background      = bg,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(210, 220, 240)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };

            UIElement Cell(string t, double w, HorizontalAlignment ha = HorizontalAlignment.Left,
                           string? tip = null) => new Border
            {
                Width = w,
                Padding = new Thickness(6, 4, 6, 4),
                Child = new TextBlock
                {
                    Text = t, FontSize = 11, FontWeight = fw,
                    Foreground = new SolidColorBrush(fgColor),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = ha,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = string.IsNullOrEmpty(tip) ? t : $"{t}\n\n{tip}"
                }
            };

            sp.Children.Add(Cell(row.Name, 220, HorizontalAlignment.Left, row.Hierarchy));
            sp.Children.Add(Cell(row.Type, 52));
            sp.Children.Add(Cell(row.StoryName, 200, HorizontalAlignment.Left, row.Hierarchy));
            sp.Children.Add(Cell(row.Responsible, 150));
            sp.Children.Add(Cell(row.TaskCount, 64, HorizontalAlignment.Right));
            sp.Children.Add(Cell(row.TotalHours, 64, HorizontalAlignment.Right));
            sp.Children.Add(Cell(row.PeriodHours, 64, HorizontalAlignment.Right));
            sp.Children.Add(Cell(row.PercentDone, 56, HorizontalAlignment.Right));
            sp.Children.Add(Cell(row.Start, 72));
            sp.Children.Add(Cell(row.Finish, 72));

            if (!isHeader)
            {
                if (row.OnOpenTasks != null)
                {
                    var tbtn = new Button
                    {
                        Content  = AppStrings.Get("PMap_OpenStoryTasks"),
                        FontSize = 10,
                        Padding  = new Thickness(6, 2, 6, 2),
                        Margin   = new Thickness(4, 2, 2, 2),
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor   = System.Windows.Input.Cursors.Hand
                    };
                    tbtn.Click += (_, _) => row.OnOpenTasks();
                    sp.Children.Add(tbtn);
                }
                if (!string.IsNullOrEmpty(row.DevOpsUrl))
                {
                    var btn = new Button
                    {
                        Content  = "↗ DevOps",
                        FontSize = 10,
                        Padding  = new Thickness(6, 2, 6, 2),
                        Margin   = new Thickness(2, 2, 4, 2),
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor   = System.Windows.Input.Cursors.Hand
                    };
                    btn.Click += (_, _) =>
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(
                                new System.Diagnostics.ProcessStartInfo(row.DevOpsUrl) { UseShellExecute = true });
                        }
                        catch { /* sem navegador disponível: ignora */ }
                    };
                    sp.Children.Add(btn);
                }
            }
            else
            {
                sp.Children.Add(Cell(devOpsFallback ?? "", 90));
            }

            border.Child = sp;
            return border;
        }
    }
}
