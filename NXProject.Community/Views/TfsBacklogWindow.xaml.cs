using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NXProject.Community.Services;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>
    /// Backlog (só leitura) como árvore: portfólio (nível Project) → Epic → Feature → Story → Task,
    /// ordenado pela prioridade do DevOps, com expandir/recolher e ícone colorido por tipo (cores
    /// configuradas no DevOps). Ver TfsImportService.BuildBacklogAsync / ListWorkItemTypeColorsAsync.
    /// </summary>
    public partial class TfsBacklogWindow : Window
    {
        private sealed class BNode
        {
            public TfsImportService.BacklogItem Item = null!;
            public List<BNode> Children = new();
            public bool InScheduleSubtree;
        }

        private readonly TfsConnectionOptions _options;
        private readonly int _scopedRootId;
        private readonly IReadOnlySet<int> _scheduleIds;
        private readonly Action<int>? _openInSchedule;
        private List<BNode> _roots = new();
        private Dictionary<string, (string Color, string Icon)> _typeColors = new();
        private int _total, _inSchedule;

        public TfsBacklogWindow(int rootId, IReadOnlySet<int>? scheduleIds = null, Action<int>? openInSchedule = null)
        {
            InitializeComponent();
            _options = TfsConnectionStore.Load("NXProject.Community");
            _scopedRootId = rootId;
            _scheduleIds = scheduleIds ?? new HashSet<int>();
            _openInSchedule = openInSchedule;
            // Abre no portfólio do NX quando houver projetos cadastrados; senão, no cronograma aberto.
            var hasPortfolio = (_options.PortfolioProjectConfigs?.Count ?? 0) > 0;
            PortfolioCheck.IsChecked = hasPortfolio || rootId <= 0;
            Loaded += async (_, _) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            StatusText.Text = AppStrings.Get("Backlog_Loading");
            RefreshBtn.IsEnabled = false;
            try
            {
                if (_typeColors.Count == 0)
                    _typeColors = await TfsImportService.ListWorkItemTypeColorsAsync(_options);

                // Portfólio = SÓ os projetos cadastrados no portfólio do NX (por título); se não
                // houver nenhum resolvido, cai no cronograma aberto. Nunca varre o TFS inteiro.
                List<int> roots;
                if (PortfolioCheck.IsChecked == true)
                {
                    var names = (_options.PortfolioProjectConfigs ?? new())
                        .Select(c => c.ProjectName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                    roots = await TfsImportService.ResolvePortfolioRootIdsAsync(_options, names);
                    if (roots.Count == 0 && _scopedRootId > 0) roots = new List<int> { _scopedRootId };
                }
                else
                {
                    roots = _scopedRootId > 0 ? new List<int> { _scopedRootId } : new List<int>();
                }

                var items = await TfsImportService.BuildBacklogAsync(_options, roots);
                _roots = BuildForest(items);
                _total = items.Count;
                _inSchedule = items.Count(i => _scheduleIds.Contains(i.Id));
                RenderTree();
            }
            catch (Exception ex)
            {
                StatusText.Text = "";
                MessageBox.Show(this, AppStrings.Get("Backlog_Error", ex.Message),
                    "NXProject", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                RefreshBtn.IsEnabled = true;
            }
        }

        // Reconstrói a floresta a partir da lista achatada por profundidade.
        private List<BNode> BuildForest(List<TfsImportService.BacklogItem> items)
        {
            var roots = new List<BNode>();
            var stack = new List<BNode>();
            foreach (var it in items)
            {
                var node = new BNode { Item = it };
                if (it.Depth == 0) roots.Add(node);
                else if (it.Depth - 1 < stack.Count) stack[it.Depth - 1].Children.Add(node);
                if (it.Depth < stack.Count) stack[it.Depth] = node;
                else stack.Add(node);
                stack.RemoveRange(Math.Min(it.Depth + 1, stack.Count), Math.Max(0, stack.Count - (it.Depth + 1)));
            }
            ComputeInSchedule(roots);
            return roots;
        }

        private bool ComputeInSchedule(List<BNode> nodes)
        {
            var any = false;
            foreach (var n in nodes)
            {
                var self = _scheduleIds.Contains(n.Item.Id);
                var kids = ComputeInSchedule(n.Children);
                n.InScheduleSubtree = self || kids;
                any |= n.InScheduleSubtree;
            }
            return any;
        }

        private void RenderTree()
        {
            Tree.Items.Clear();
            var onlySched = OnlyScheduleCheck.IsChecked == true;
            foreach (var n in _roots)
            {
                if (onlySched && !n.InScheduleSubtree) continue;
                Tree.Items.Add(BuildTvi(n, onlySched));
            }
            StatusText.Text = AppStrings.Get("Query_CountSched", _total.ToString(), _inSchedule.ToString());
        }

        private TreeViewItem BuildTvi(BNode node, bool onlySched)
        {
            // Abre recolhido por padrão: mostra os projetos/raízes; o usuário expande o que quiser
            // (ou usa "Expandir tudo").
            var tvi = new TreeViewItem { IsExpanded = false, Header = BuildHeader(node.Item) };
            foreach (var c in node.Children)
            {
                if (onlySched && !c.InScheduleSubtree) continue;
                tvi.Items.Add(BuildTvi(c, onlySched));
            }
            return tvi;
        }

        private FrameworkElement BuildHeader(TfsImportService.BacklogItem it)
        {
            var inSched = _scheduleIds.Contains(it.Id);
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // Ícone do tipo conforme configurado no DevOps (mapeado do icon.id) — cai no
            // losango colorido pela cor do tipo quando o id não é conhecido.
            var glyph = IconGlyphFor(it.Type);
            if (glyph != null)
                sp.Children.Add(new TextBlock { Text = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0) });
            else
                sp.Children.Add(new Path
                {
                    Data = Geometry.Parse("M 6,0 L 12,6 L 6,12 L 0,6 Z"),
                    Fill = TypeBrush(it.Type),
                    Width = 12, Height = 12, Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });

            sp.Children.Add(new TextBlock { Text = it.Title, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = it.Depth <= 1 ? FontWeights.SemiBold : FontWeights.Normal });

            var details = $"  #{it.Id} · {it.Type}"
                + (string.IsNullOrWhiteSpace(it.State) ? "" : $" · {it.State}")
                + (string.IsNullOrWhiteSpace(it.AssignedTo) ? "" : $" · {it.AssignedTo}")
                + (string.IsNullOrWhiteSpace(it.Effort) ? "" : $" · {it.Effort}h")
                + (string.IsNullOrWhiteSpace(it.Iteration) ? "" : $" · {ShortSprint(it.Iteration)}");
            sp.Children.Add(new TextBlock { Text = details, Foreground = Brushes.Gray, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });

            var open = new Button { Content = "DevOps", FontSize = 10, Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(8, 0, 0, 0) };
            open.Click += (_, _) => OpenInDevOps(it.Id);
            sp.Children.Add(open);
            if (inSched && _openInSchedule != null)
            {
                var sched = new Button { Content = "📅", FontSize = 11, Padding = new Thickness(5, 0, 5, 0),
                    Margin = new Thickness(4, 0, 0, 0), ToolTip = AppStrings.Get("Query_OpenInSchedule") };
                sched.Click += (_, _) => _openInSchedule!(it.Id);
                sp.Children.Add(sched);
            }
            return sp;
        }

        // Ícones do Azure DevOps (icon.id lido de _apis/wit/workitemtypes) → glifo equivalente.
        private static readonly Dictionary<string, string> IconGlyphs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["icon_crown"] = "👑", ["icon_trophy"] = "🏆", ["icon_book"] = "📖",
            ["icon_clipboard"] = "📋", ["icon_clipboard_issue"] = "📋", ["icon_list"] = "📝",
            ["icon_insect"] = "🐞", ["icon_chat_bubble"] = "💬", ["icon_traffic_cone"] = "🚧",
            ["icon_megaphone"] = "📣", ["icon_diamond"] = "🔷", ["icon_test_beaker"] = "🧪",
            ["icon_test_plan"] = "🧪", ["icon_test_step"] = "🧪", ["icon_review"] = "🔍",
            ["icon_flame"] = "🔥", ["icon_key"] = "🔑", ["icon_gear"] = "⚙", ["icon_chart"] = "📊",
            ["icon_pie_chart"] = "📊", ["icon_government"] = "🏛", ["icon_broken_lightbulb"] = "💡",
            ["icon_check_box"] = "☑", ["icon_asterisk"] = "✳", ["icon_car"] = "🚗",
            ["icon_park"] = "🌳", ["icon_camera_video"] = "🎥", ["icon_headphone"] = "🎧",
            ["icon_test_case"] = "🧪", ["icon_test_suite"] = "🧪", ["icon_test_parameter"] = "🧪",
            ["icon_code_review"] = "🔍", ["icon_code_response"] = "💬", ["icon_response"] = "💬"
        };

        private string? IconGlyphFor(string type)
        {
            if (_typeColors.TryGetValue(type, out var c) && !string.IsNullOrEmpty(c.Icon)
                && IconGlyphs.TryGetValue(c.Icon, out var g))
                return g;
            return null;
        }

        private Brush TypeBrush(string type)
        {
            if (_typeColors.TryGetValue(type, out var c) && !string.IsNullOrWhiteSpace(c.Color))
            {
                try { return (Brush)new BrushConverter().ConvertFromString(c.Color)!; }
                catch { }
            }
            return new SolidColorBrush(Color.FromRgb(0x6B, 0x7A, 0x8A));
        }

        private static string ShortSprint(string iteration)
        {
            if (string.IsNullOrWhiteSpace(iteration)) return "";
            var idx = iteration.LastIndexOf('\\');
            return idx >= 0 && idx < iteration.Length - 1 ? iteration[(idx + 1)..] : iteration;
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadAsync();
        private void OnFilterChanged(object sender, RoutedEventArgs e) => RenderTree();

        private void OnExpandAllClick(object sender, RoutedEventArgs e) => SetExpanded(Tree.Items, true);
        private void OnCollapseAllClick(object sender, RoutedEventArgs e) => SetExpanded(Tree.Items, false);

        private static void SetExpanded(ItemCollection items, bool expanded)
        {
            foreach (var o in items)
                if (o is TreeViewItem tvi)
                {
                    tvi.IsExpanded = expanded;
                    SetExpanded(tvi.Items, expanded);
                }
        }

        private void OpenInDevOps(int id)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_options.OrganizationUrl) || string.IsNullOrWhiteSpace(_options.TeamProject)) return;
                var url = $"{_options.OrganizationUrl.TrimEnd('/')}/{Uri.EscapeDataString(_options.TeamProject.Trim())}/_workitems/edit/{id}";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
