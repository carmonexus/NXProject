// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class CustomDevOpsEditWindow : Window
    {
        /// <summary>Valores preenchidos pelo usuário: chave = field ref, valor = string escolhido.</summary>
        public Dictionary<string, string> FieldValues { get; } = new(StringComparer.OrdinalIgnoreCase);

        private readonly record struct FieldRow(string FieldRef, string FieldType, Control Input);
        private readonly List<FieldRow> _rows = [];

        public CustomDevOpsEditWindow(string tfsType, Dictionary<string, string> currentValues)
        {
            InitializeComponent();

            var options = TfsConnectionStore.Load("NXProject.Community");

            // Resolve campos mapeados para este tipo (ou wildcard "*")
            options.TypeFieldMappings.TryGetValue(tfsType, out var cfg);
            if (cfg == null || cfg.CustomDevopsFields.Count == 0)
                options.TypeFieldMappings.TryGetValue("*", out cfg);

            var fields = cfg?.CustomDevopsFields ?? [];

            TitleText.Text = AppStrings.Get("CustomDO_TitleFormat", tfsType);

            foreach (var fd in fields)
            {
                var label = new TextBlock
                {
                    Text = fd.Field,
                    FontSize = 11,
                    Foreground = System.Windows.Media.Brushes.DimGray,
                    Margin = new Thickness(0, 8, 0, 2)
                };
                FieldsPanel.Children.Add(label);

                currentValues.TryGetValue(fd.Field, out var current);
                Control input = BuildInput(fd, current);
                FieldsPanel.Children.Add(input);
                _rows.Add(new FieldRow(fd.Field, fd.FieldType, input));
            }

            if (_rows.Count == 0)
            {
                FieldsPanel.Children.Add(new TextBlock
                {
                    Text = AppStrings.Get("CustomDO_NoFields"),
                    FontSize = 11,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 6)
                });

                var link = new System.Windows.Documents.Hyperlink(
                    new System.Windows.Documents.Run(AppStrings.Get("CustomDO_OpenConfig")));
                link.Click += (_, _) =>
                {
                    var cfg = new TfsDevOpsConfigWindow { Owner = this };
                    cfg.ShowDialog();
                    Close();
                };
                FieldsPanel.Children.Add(new TextBlock(link) { FontSize = 11 });

                OkButton.IsEnabled = false;
            }
        }

        private static Control BuildInput(ClassificationFieldDef fd, string? current)
        {
            if (fd.FieldType == "Integer")
            {
                return new TextBox
                {
                    Height = 28, Padding = new Thickness(6, 3, 6, 3), FontSize = 12,
                    Text = current ?? "",
                    ToolTip = AppStrings.Get("CustomDO_IntTip", fd.Field)
                };
            }

            if (fd.FieldType == "Date")
            {
                return new DatePicker
                {
                    Height = 28, FontSize = 12,
                    SelectedDate = DateTime.TryParse(current, out var dt) ? dt : null,
                    ToolTip = AppStrings.Get("CustomDO_DateTip", fd.Field)
                };
            }

            // Picklist ou Text → ComboBox editável
            var combo = new ComboBox
            {
                IsEditable = true, Height = 28,
                Padding = new Thickness(4, 2, 4, 2), FontSize = 12,
                ToolTip = AppStrings.Get("CustomDO_ValueTip", fd.Field)
            };

            if (!string.IsNullOrWhiteSpace(fd.Values))
            {
                foreach (var v in fd.Values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    combo.Items.Add(v);
            }

            combo.Text = !string.IsNullOrWhiteSpace(current) ? current : string.Empty;
            return combo;
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows)
            {
                var val = row.Input switch
                {
                    ComboBox cb    => cb.Text?.Trim() ?? "",
                    TextBox  tb    => tb.Text?.Trim() ?? "",
                    DatePicker dp  => dp.SelectedDate?.ToString("yyyy-MM-dd") ?? "",
                    _              => ""
                };
                if (!string.IsNullOrEmpty(val))
                    FieldValues[row.FieldRef] = val;
            }
            DialogResult = true;
        }
    }
}
