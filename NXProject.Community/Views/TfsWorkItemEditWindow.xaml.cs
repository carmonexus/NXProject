using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Views
{
    public partial class TfsWorkItemEditWindow : Window
    {
        private readonly TaskViewModel _task;
        private string? _devOpsItemUrl;

        public bool ShouldImport { get; private set; }
        public bool ShouldDelete { get; private set; }

        public TfsWorkItemEditWindow(TaskViewModel task)
        {
            InitializeComponent();
            _task = task ?? throw new ArgumentNullException(nameof(task));

            TaskNameText.Text = task.Name;
            IdBox.Text = task.TfsId is > 0 || task.TfsId == 0
                ? task.TfsId.Value.ToString()
                : string.Empty;
            SelectByText(TypeBox, task.TfsType);
            SelectByText(StateBox, task.TfsState);
            RefreshFeatureTypePanel(task.TfsType, task.TfsClassification);

            TypeBox.SelectionChanged += OnTypeBoxChanged;
            TagsBox.Text = task.Tags ?? string.Empty;
            _suppressBlockToggle = true;
            BlockCheck.IsChecked = HasTag(TagsBox.Text, "Block");
            _suppressBlockToggle = false;

            // Mostra TipoCentroCusto para Epic ou segundo nível de hierarquia (depth == 1)
            bool isEpicOrLevel2 = string.Equals(task.TfsType, "Epic", StringComparison.OrdinalIgnoreCase)
                                   || task.Depth == 1;
            if (isEpicOrLevel2)
            {
                CentroCustoPanel.Visibility = Visibility.Visible;
                var current = task.Model.TipoCentroCusto?.ToUpperInvariant() ?? "DEFINIDO_NO_PROJETO";
                foreach (ComboBoxItem item2 in TipoCentroCustoBox.Items)
                {
                    if (string.Equals(item2.Content?.ToString(), current, StringComparison.OrdinalIgnoreCase))
                    {
                        TipoCentroCustoBox.SelectedItem = item2;
                        break;
                    }
                }
                if (TipoCentroCustoBox.SelectedIndex < 0) TipoCentroCustoBox.SelectedIndex = 0;
            }

            if (task.HasSyncConflict)
                ConflictBanner.Visibility = Visibility.Visible;

            LoadDevOpsExtras(task);
        }

        private void LoadDevOpsExtras(TaskViewModel task)
        {
            if (task.TfsId is > 0)
            {
                try
                {
                    var conn = TfsConnectionStore.Load();
                    if (!string.IsNullOrWhiteSpace(conn.OrganizationUrl) && !string.IsNullOrWhiteSpace(conn.TeamProject))
                    {
                        var org = conn.OrganizationUrl.TrimEnd('/');
                        var proj = Uri.EscapeDataString(conn.TeamProject.Trim());
                        _devOpsItemUrl = $"{org}/{proj}/_workitems/edit/{task.TfsId}";
                        OpenInDevOpsButton.Visibility = Visibility.Visible;

                        // Botão de exclusão apenas para Stories com ID real
                        if (TfsImportService.IsStoryTypePublic(task.TfsType))
                            DeleteStoryButton.Visibility = Visibility.Visible;
                    }
                }
                catch { }
            }

            var children = task.ChildrenViewModels
                .Where(c => c.TfsId is > 0)
                .Select(c => new ChildTaskRow(c.TfsId!.Value, c.Name, c.TfsState ?? ""))
                .ToList();

            if (children.Count > 0)
            {
                ChildTasksList.ItemsSource = children;
                ChildTasksPanel.Visibility = Visibility.Visible;
            }
        }

        private async void OnDeleteStoryClick(object sender, RoutedEventArgs e)
        {
            if (_task.TfsId is not > 0) return;

            // Confirmação com destaque visual
            var confirm = new Window
            {
                Title = "Confirmar Exclusão",
                Width = 480, Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.White
            };
            var result = false;
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            panel.Children.Add(new TextBlock
            {
                Text = $"⚠ Excluir Story #{_task.TfsId} do Azure DevOps?",
                FontSize = 15, FontWeight = FontWeights.Bold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28)),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"\"{_task.Name}\"\n\nEsta ação é irreversível. O item será excluído permanentemente do DevOps.",
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x00, 0x00)),
                Margin = new Thickness(0, 0, 0, 16)
            });
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnConfirm = new Button
            {
                Content = "Excluir permanentemente", Width = 180, Height = 30,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28)),
                Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0),
                FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand
            };
            var btnCancel = new Button { Content = "Cancelar", Width = 90, Height = 30, Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
            btnConfirm.Click += (_, _) => { result = true; confirm.Close(); };
            btnCancel.Click  += (_, _) => confirm.Close();
            btnPanel.Children.Add(btnConfirm);
            btnPanel.Children.Add(btnCancel);
            panel.Children.Add(btnPanel);
            confirm.Content = panel;
            confirm.ShowDialog();

            if (!result) return;

            try
            {
                DeleteStoryButton.IsEnabled = false;
                StatusText.Text = "Excluindo Story no DevOps...";
                StatusText.Visibility = Visibility.Visible;
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44));

                var options = TfsConnectionStore.Load("NXProject.Community");
                await TfsImportService.DeleteWorkItemAsync(options, _task.TfsId.Value);

                ShouldDelete = true;
                DialogResult = false;
                Close();
            }
            catch (Exception ex)
            {
                DeleteStoryButton.IsEnabled = true;
                StatusText.Text = $"Erro ao excluir: {ex.Message}";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB0, 0x00, 0x20));
                StatusText.Visibility = Visibility.Visible;
            }
        }

        private void OnOpenInDevOpsClick(object sender, RoutedEventArgs e)
        {
            if (_devOpsItemUrl is null) return;
            try { Process.Start(new ProcessStartInfo(_devOpsItemUrl) { UseShellExecute = true }); }
            catch { }
        }

        private void OnImportClick(object sender, RoutedEventArgs e)
        {
            ShouldImport = true;
            DialogResult = false;
            Close();
        }

        private sealed record ChildTaskRow(int TfsId, string Name, string State)
        {
            public string TfsIdText => $"#{TfsId}";
        }

        private bool _suppressBlockToggle;

        private void OnBlockToggled(object sender, RoutedEventArgs e)
        {
            if (_suppressBlockToggle)
                return;

            var tags = SplitTags(TagsBox.Text);
            tags.RemoveAll(t => string.Equals(t, "Block", StringComparison.OrdinalIgnoreCase));
            if (BlockCheck.IsChecked == true)
                tags.Add("Block");
            TagsBox.Text = string.Join("; ", tags);
        }

        private void OnTypeBoxChanged(object sender, SelectionChangedEventArgs e)
        {
            var type = (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            RefreshFeatureTypePanel(type, null);
        }

        private void RefreshFeatureTypePanel(string? tfsType, string? currentClassification)
        {
            bool isFeature = string.Equals(tfsType, "Feature", StringComparison.OrdinalIgnoreCase);
            FeatureTypePanel.Visibility = isFeature ? Visibility.Visible : Visibility.Collapsed;
            if (isFeature)
                SelectByText(FeatureTypeBox, currentClassification ?? "Feature");
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            StatusText.Visibility = Visibility.Collapsed;

            var selectedType = (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var isNoDevOps = TaskViewModel.IsNoDevOpsType(selectedType);

            int? id = null;
            var idText = ExtractId(IdBox.Text);
            if (!isNoDevOps && !string.IsNullOrEmpty(idText))
            {
                if (!int.TryParse(idText, out var parsed) || parsed < 0)
                {
                    StatusText.Text = "Informe um ID válido: número do DevOps, 0 para criar na sincronização, ou vazio para desvincular.";
                    StatusText.Visibility = Visibility.Visible;
                    return;
                }
                id = parsed;
                IdBox.Text = parsed.ToString(); // normaliza para só o número
            }

            // 0 = criar no DevOps: exige um tipo selecionado.
            if (!isNoDevOps && id == 0 && StateBoxTypeMissing())
            {
                StatusText.Text = "Para criar (ID = 0), selecione o Tipo DevOps (Epic, Feature ou Story).";
                StatusText.Visibility = Visibility.Visible;
                return;
            }

            if (isNoDevOps)
            {
                _task.TfsId = null;
                _task.Model.IsPendingTfsCreate = false;
                _task.Model.TfsParentId = null;
                _task.Model.SyncVersion = null;
                _task.Model.HasSyncConflict = false;
                _task.Model.DevopsTaskCount = null;
                _task.TfsType = "No DevOps";
                _task.TfsState = null;
            }
            else
            {
                var isPendingCreate = !id.HasValue || id.Value == 0;
                _task.TfsId = id is > 0 ? id : null;
                _task.Model.IsPendingTfsCreate = isPendingCreate;
                if (isPendingCreate)
                {
                    _task.Model.TfsParentId = null;
                    _task.Model.SyncVersion = null;
                    _task.Model.HasSyncConflict = false;
                }
                _task.TfsType = selectedType;
                _task.TfsState = (StateBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            }

            _task.Tags = string.Join("; ", SplitTags(TagsBox.Text));

            if (FeatureTypePanel.Visibility == Visibility.Visible)
                _task.TfsClassification = (FeatureTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString();

            if (CentroCustoPanel.Visibility == Visibility.Visible)
            {
                var selected = (TipoCentroCustoBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
                _task.Model.TipoCentroCusto = selected == "DEFINIDO_NO_PROJETO" ? null : selected;
            }

            DialogResult = true;
            Close();
        }

        // Extrai o ID numérico de um texto que pode ser um número puro ou uma URL do DevOps.
        // Ex: "https://dev.azure.com/org/proj/_workitems/edit/12345" → "12345"
        private static string? ExtractId(string? text)
        {
            var raw = text?.Trim();
            if (string.IsNullOrEmpty(raw)) return raw;
            if (!raw.Contains('/') && !raw.Contains('?')) return raw; // já é número ou texto simples

            var match = System.Text.RegularExpressions.Regex.Match(raw, @"(?:^|[/?&=])(\d{3,7})(?:[/?&=]|$)");
            return match.Success ? match.Groups[1].Value : raw;
        }

        private static System.Collections.Generic.List<string> SplitTags(string? tags) =>
            string.IsNullOrWhiteSpace(tags)
                ? new System.Collections.Generic.List<string>()
                : tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        private static bool HasTag(string? tags, string tag) =>
            SplitTags(tags).Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

        private bool StateBoxTypeMissing() =>
            (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() is not { Length: > 0 };

        private static void SelectByText(ComboBox combo, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                combo.SelectedIndex = -1;
                return;
            }

            foreach (var item in combo.Items)
            {
                if (item is ComboBoxItem cbi &&
                    string.Equals(cbi.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = cbi;
                    return;
                }
            }

            // Tipo/estado não previsto: adiciona e seleciona.
            var added = new ComboBoxItem { Content = value };
            combo.Items.Add(added);
            combo.SelectedItem = added;
        }
    }
}
