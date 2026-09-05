// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NXProject.Community.Services;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>
    /// Executa as Shared Queries do Azure DevOps dentro do NXProject. A WIQL roda no
    /// servidor (qualquer campo/filtro custom funciona) e o resultado é exibido com
    /// colunas dinâmicas vindas da própria query — campos que o NX não modela aparecem
    /// como texto, sem mapeamento. Ver TfsImportService.RunSavedQueryAsync.
    /// </summary>
    public partial class TfsQueryWindow : Window
    {
        private readonly TfsConnectionOptions _options;
        // TfsIds presentes no cronograma aberto e ação para focar a task lá. Quando um item
        // do resultado está no cronograma, a linha ganha um botão "ver no cronograma".
        private readonly IReadOnlySet<int> _scheduleIds;
        private readonly Action<int>? _openInSchedule;
        // Nome de exibição da coluna System.Id (para ler o ID da linha selecionada).
        private string _idColumnName = "ID";
        // View atual do resultado (para aplicar o filtro "somente do cronograma").
        private DataView? _view;
        private int _lastTotal;
        private int _lastInSchedule;

        public TfsQueryWindow(IReadOnlySet<int>? scheduleIds = null, Action<int>? openInSchedule = null)
        {
            InitializeComponent();
            _options = TfsConnectionStore.Load("NXProject.Community");
            _scheduleIds = scheduleIds ?? new HashSet<int>();
            _openInSchedule = openInSchedule;
            Loaded += async (_, _) => await LoadQueriesAsync();
        }

        private async Task LoadQueriesAsync()
        {
            QueriesTree.Items.Clear();
            StatusText.Text = AppStrings.Get("Query_Loading");
            try
            {
                var roots = await TfsImportService.ListQueriesAsync(_options);
                foreach (var node in roots)
                    QueriesTree.Items.Add(BuildTreeItem(node));
                StatusText.Text = "";
            }
            catch (Exception ex)
            {
                StatusText.Text = "";
                MessageBox.Show(this, AppStrings.Get("Query_LoadError", ex.Message),
                    "NXProject", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Monta o item da árvore: pasta expansível ou query (folha) com o id no Tag.
        private static TreeViewItem BuildTreeItem(TfsImportService.DevOpsQueryNode node)
        {
            var label = (node.IsFolder ? "📁 " : "🔎 ") + node.Name;
            if (!node.IsFolder && !string.IsNullOrWhiteSpace(node.Author))
                label += $"   ·   {node.Author}";
            var item = new TreeViewItem
            {
                Header = label,
                Tag = node,
                IsExpanded = node.IsFolder,
                ToolTip = node.IsFolder ? null
                    : (string.IsNullOrWhiteSpace(node.Author) ? node.Name
                       : AppStrings.Get("Query_Author", node.Author))
            };
            if (node.IsFolder)
                foreach (var child in node.Children)
                    item.Items.Add(BuildTreeItem(child));
            return item;
        }

        private static TfsImportService.DevOpsQueryNode? SelectedNode(TreeView tree)
            => (tree.SelectedItem as TreeViewItem)?.Tag as TfsImportService.DevOpsQueryNode;

        private async void OnRefreshQueriesClick(object sender, RoutedEventArgs e)
            => await LoadQueriesAsync();

        private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var node = SelectedNode(QueriesTree);
            RunBtn.IsEnabled = node is { IsFolder: false };
        }

        private async void OnTreeDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var node = SelectedNode(QueriesTree);
            if (node is { IsFolder: false })
                await RunSelectedAsync(node);
        }

        private async void OnRunClick(object sender, RoutedEventArgs e)
        {
            var node = SelectedNode(QueriesTree);
            if (node is { IsFolder: false })
                await RunSelectedAsync(node);
            else
                StatusText.Text = AppStrings.Get("Query_PickOne");
        }

        private async Task RunSelectedAsync(TfsImportService.DevOpsQueryNode node)
        {
            StatusText.Text = AppStrings.Get("Query_Running");
            RunBtn.IsEnabled = false;
            try
            {
                var result = await TfsImportService.RunSavedQueryAsync(_options, node.Id);
                var table = BuildTable(result);
                _view = table.DefaultView;
                _lastTotal = result.Rows.Count;
                _lastInSchedule = table.Rows.Cast<DataRow>().Count(r => r["InSchedule"] is bool b && b);
                ApplyScheduleFilter();
                ResultsGrid.ItemsSource = _view;
            }
            catch (Exception ex)
            {
                StatusText.Text = "";
                MessageBox.Show(this, AppStrings.Get("Query_RunError", ex.Message),
                    "NXProject", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                RunBtn.IsEnabled = true;
            }
        }

        // Constrói o DataTable com colunas dinâmicas (nomes vindos da query, únicos) e uma
        // coluna auxiliar booleana "InSchedule" (oculta) que liga o botão "ver no cronograma".
        private DataTable BuildTable(TfsImportService.DevOpsQueryRunResult result)
        {
            var table = new DataTable();
            var displayNames = new string[result.Columns.Count];
            _idColumnName = "ID";
            for (int i = 0; i < result.Columns.Count; i++)
            {
                var col = result.Columns[i];
                var header = string.IsNullOrWhiteSpace(col.Name) ? col.ReferenceName : col.Name;
                var unique = header;
                var n = 2;
                while (table.Columns.Contains(unique)) unique = $"{header} ({n++})";
                table.Columns.Add(unique);
                displayNames[i] = unique;
                if (string.Equals(col.ReferenceName, "System.Id", StringComparison.OrdinalIgnoreCase))
                    _idColumnName = unique;
            }
            var inSchedule = table.Columns.Add("InSchedule", typeof(bool));

            foreach (var row in result.Rows)
            {
                var r = table.NewRow();
                for (int i = 0; i < result.Columns.Count; i++)
                    r[i] = row.TryGetValue(result.Columns[i].ReferenceName, out var v) ? v : "";
                var inSched = row.TryGetValue("System.Id", out var idStr)
                              && int.TryParse(idStr, out var wid) && _scheduleIds.Contains(wid);
                r[inSchedule] = inSched;
                table.Rows.Add(r);
            }
            return table;
        }

        private void OnOnlyScheduleToggle(object sender, RoutedEventArgs e) => ApplyScheduleFilter();

        // Aplica (ou remove) o filtro "somente IDs do cronograma" e atualiza o rodapé.
        private void ApplyScheduleFilter()
        {
            if (_view == null) return;
            _view.RowFilter = OnlyScheduleCheck.IsChecked == true ? "InSchedule = true" : "";
            StatusText.Text = AppStrings.Get("Query_CountSched",
                _lastTotal.ToString(), _lastInSchedule.ToString());
        }

        // Esconde a coluna auxiliar "InSchedule" das colunas auto-geradas.
        private void OnAutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (string.Equals(e.PropertyName, "InSchedule", StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;
        }

        // Botão da linha: foca o work item correspondente no cronograma aberto.
        private void OnOpenInScheduleClick(object sender, RoutedEventArgs e)
        {
            if (_openInSchedule == null) return;
            if ((sender as FrameworkElement)?.DataContext is not DataRowView drv) return;
            var idText = drv.Row.Table.Columns.Contains(_idColumnName) ? drv.Row[_idColumnName]?.ToString() : null;
            if (int.TryParse(idText, out var id) && id > 0)
                _openInSchedule(id);
        }

        private void OnGridDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => OpenSelectedInDevOps();

        private void OnOpenInDevOpsClick(object sender, RoutedEventArgs e)
            => OpenSelectedInDevOps();

        // Abre o work item selecionado no navegador (coluna System.Id do resultado).
        private void OpenSelectedInDevOps()
        {
            if (ResultsGrid.SelectedItem is not DataRowView drv) return;
            var idText = drv.Row.Table.Columns.Contains(_idColumnName) ? drv.Row[_idColumnName]?.ToString()
                       : drv.Row.Table.Columns.Count > 0 ? drv.Row[0]?.ToString() : null;
            if (!int.TryParse(idText, out var id) || id <= 0)
            {
                StatusText.Text = AppStrings.Get("Query_NoId");
                return;
            }
            try
            {
                if (string.IsNullOrWhiteSpace(_options.OrganizationUrl) || string.IsNullOrWhiteSpace(_options.TeamProject))
                    return;
                var url = $"{_options.OrganizationUrl.TrimEnd('/')}/{Uri.EscapeDataString(_options.TeamProject.Trim())}/_workitems/edit/{id}";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
