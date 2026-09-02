using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NXProject.Community.Services;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>
    /// Visão de Sprint (Taskboard): Stories em linhas, Tasks como cards nas colunas por estado,
    /// com filtro por pessoa e "somente do cronograma", e um resumo por estado. Só leitura.
    /// Ver TfsImportService.BuildSprintBoardAsync.
    /// </summary>
    public partial class TfsSprintWindow : Window
    {
        private readonly TfsConnectionOptions _options;
        private readonly IReadOnlySet<int> _scheduleIds;
        private readonly Action<int>? _openInSchedule;
        private readonly string? _preferredSprint;
        private List<TfsImportService.SprintInfo> _sprints = new();
        private TfsImportService.SprintBoard? _board;
        private const string AllPeople = "— Todos —";
        // Filtro múltiplo por Story (vazio = todas) e lookup id→Story.
        private readonly HashSet<int> _selectedStoryIds = new();
        // Filtro múltiplo por Pessoa (vazio = todas).
        private readonly HashSet<string> _selectedPeople = new(StringComparer.CurrentCultureIgnoreCase);
        private Dictionary<int, TfsImportService.SprintStoryRow> _storyById = new();
        // Filtro por estado (vazio = todos), nome do usuário atual e edição de cards.
        private readonly HashSet<string> _hiddenStates = new(StringComparer.OrdinalIgnoreCase);
        private int _closedDays = 30; // Closed exibe só os últimos N dias (0 = todos).
        private int _discoveredPrioMax; // máximo de Priority aceito pelo template (via validateOnly).
        private string? _currentUser;
        // Estado alterado localmente (arrasto), pendente de gravar; e o já gravado com sucesso.
        private readonly Dictionary<int, string> _pending = new();
        private readonly Dictionary<int, string> _applied = new();
        // Chave de ordenação por card (para inserir na posição solta, não no fim).
        private readonly Dictionary<int, double> _order = new();
        // Cards marcados "Doing" (localmente) e os que já têm a tag no TFS.
        private readonly HashSet<int> _doing = new();
        private readonly HashSet<int> _appliedDoing = new();
        // Descrição/trâmite editados (HTML), pendentes de gravar no TFS pelo botão Salvar TFS.
        private readonly Dictionary<int, string> _descPending = new();
        private readonly Dictionary<int, string> _tramitePending = new();
        // Responsável (System.AssignedTo) alterado (pendente) e o já gravado (baseline pós-Salvar).
        private readonly Dictionary<int, string> _ownerPending = new();
        private readonly Dictionary<int, string> _ownerApplied = new();
        // Nome/título (System.Title) alterado (pendente) e o já gravado (baseline pós-Salvar).
        private readonly Dictionary<int, string> _titlePending = new();
        private readonly Dictionary<int, string> _titleApplied = new();
        // HH estimado (OriginalEstimate) e HH realizado (CompletedWork) alterados, pendentes de gravar.
        private readonly Dictionary<int, double?> _estPending = new();
        private readonly Dictionary<int, double?> _donePending = new();
        // Tasks (New) marcadas para excluir; a exclusão no DevOps ocorre no Salvar TFS.
        private readonly HashSet<int> _deletePending = new();
        // Iteração (sprint) da Story alterada (pendente) e a já gravada (baseline pós-Salvar).
        private readonly Dictionary<int, string> _iterPending = new();
        private readonly Dictionary<int, string> _iterApplied = new();
        // Feature (pai) da Story alterada (pendente: novo FeatureId) e a já gravada.
        private readonly Dictionary<int, int> _featurePending = new();
        private readonly Dictionary<int, int> _featureApplied = new();
        // Bloqueio (tag "Blocked") alterado (pendente: novo valor) e conjuntos de tags já gravados.
        private const string BlockedTag = "Blocked";
        private readonly Dictionary<int, bool> _blockPending = new();
        private readonly Dictionary<int, string> _tagsApplied = new(); // tags após gravar (baseline)
        // Prioridade da Task alterada (pendente) e a já gravada (baseline após Salvar TFS).
        private readonly Dictionary<int, int> _prioPending = new();
        private readonly Dictionary<int, int> _prioApplied = new();
        // Rank (StackRank) efetivo das Stories e os ids com rank pendente de gravar (mover Story).
        private readonly Dictionary<int, double> _storyRank = new();
        private readonly HashSet<int> _storyRankPending = new();
        // Estado da Story alterado (arrasto entre colunas no StoryBoard), pendente de gravar.
        private readonly Dictionary<int, string> _storyStatePending = new();
        private readonly Dictionary<int, string> _storyStateApplied = new();
        // Rank (StackRank) efetivo das Tasks e pendências (mover a Task dentro do grupo de prioridade).
        private readonly Dictionary<int, double> _taskRank = new();
        private readonly HashSet<int> _taskRankPending = new();
        // Lookup dos cards efetivos por id (para saber prioridade/rank ao arrastar).
        private readonly Dictionary<int, TfsImportService.SprintTaskCard> _cardById = new();
        // Novos cards (Story/Task) criados localmente, sem ID do TFS ainda (id temporário negativo).
        private sealed class NewCard { public int TempId; public string Type = ""; public string Title = ""; public int ParentId; public string FeatureTitle = ""; public int FeatureId; public string AssignedTo = ""; public double? Effort; public string Description = ""; public string IterationPath = ""; }
        private readonly List<NewCard> _newCards = new();
        private int _nextTempId = -1;
        // Sprints selecionadas (1 = normal; várias = união; vazio = "todas"). O caminho "único"
        // só existe quando exatamente uma sprint específica está ativa (usado p/ criar itens/last).
        private List<string> _sprintPaths = new();
        private string _sprintPath => _sprintPaths.Count == 1 ? _sprintPaths[0] : "";

        private static string SprintSettingsPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXProject.Community", "sprintview.json");

        // Preferências persistidas da tela (últimos filtros do usuário).
        private sealed class SprintPrefs
        {
            public string LastSprintPath { get; set; } = "";
            public int? ClosedDays { get; set; }        // só > 0 (0/null = todos)
            public List<string>? Persons { get; set; }   // vazio/null = todas
            public int? View { get; set; }              // 0 = Por Story, 1 = Pessoa & Task
            public bool? OnlySchedule { get; set; }
            public bool? EditMode { get; set; }
            public List<string>? HiddenStates { get; set; }
            public List<string>? SprintPaths { get; set; }   // >1 = multi-seleção de sprints
            public Dictionary<string, string>? StateColors { get; set; } // estado(lower) -> #RRGGBB
            public bool AutoOpen { get; set; }   // abrir o TaskBoard ao iniciar o NX
            public bool WinMaximized { get; set; }
            public double WinLeft { get; set; }
            public double WinTop { get; set; }
            public double WinWidth { get; set; }
            public double WinHeight { get; set; }
        }
        private SprintPrefs _prefs = new();
        private bool _restoringPrefs;   // evita salvar enquanto restaura os controles
        private bool _firstBoardLoad = true; // aplica os filtros salvos só na 1ª carga
        private bool _boardEverLoaded;  // só salva prefs depois da 1ª carga (evita sobrescrever com defaults)

        private static SprintPrefs LoadPrefs()
        {
            try
            {
                var p = SprintSettingsPath;
                if (System.IO.File.Exists(p))
                    return System.Text.Json.JsonSerializer.Deserialize<SprintPrefs>(System.IO.File.ReadAllText(p)) ?? new();
            }
            catch { }
            return new();
        }

        /// <summary>Se o usuário pediu para abrir o TaskBoard automaticamente ao iniciar o NX.</summary>
        public static bool ShouldAutoOpenTaskBoard() => LoadPrefs().AutoOpen;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (_prefs.WinMaximized) WindowState = WindowState.Maximized;
        }

        private static string? LoadLastSprintPath() => LoadPrefs().LastSprintPath is { Length: > 0 } s ? s : null;
        private static int? LoadClosedDays() => LoadPrefs().ClosedDays is int d && d > 0 ? d : (int?)null;

        // Persiste o estado atual dos filtros (chamado a cada mudança). ClosedDays só grava se > 0.
        private void SavePrefs()
        {
            if (_restoringPrefs || !_boardEverLoaded) return;
            try
            {
                _prefs.LastSprintPath = _sprintPath ?? "";
                _prefs.SprintPaths = _sprintPaths.Count > 1 ? _sprintPaths.ToList() : null;
                // Geometria da janela (usa RestoreBounds p/ manter o tamanho normal mesmo maximizado).
                _prefs.WinMaximized = WindowState == WindowState.Maximized;
                var b = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
                if (b.Width > 200 && b.Height > 200)
                { _prefs.WinLeft = b.Left; _prefs.WinTop = b.Top; _prefs.WinWidth = b.Width; _prefs.WinHeight = b.Height; }
                _prefs.ClosedDays = _closedDays > 0 ? _closedDays : (int?)null;
                _prefs.Persons = _selectedPeople.Count > 0 ? _selectedPeople.ToList() : null;
                _prefs.View = ViewCombo.SelectedIndex;
                _prefs.OnlySchedule = OnlyScheduleCheck.IsChecked == true;
                _prefs.EditMode = EditModeCheck.IsChecked == true;
                _prefs.HiddenStates = _hiddenStates.ToList();
                var p = SprintSettingsPath;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
                System.IO.File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(_prefs));
            }
            catch { }
        }

        private static bool IsClosedState(string s) =>
            s.Equals("Closed", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Done", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Completed", StringComparison.OrdinalIgnoreCase);

        public TfsSprintWindow(IReadOnlySet<int>? scheduleIds = null, Action<int>? openInSchedule = null,
            string? preferredSprint = null)
        {
            InitializeComponent();
            _options = TfsConnectionStore.Load("NXProject.Community");
            _scheduleIds = scheduleIds ?? new HashSet<int>();
            _openInSchedule = openInSchedule;
            _preferredSprint = preferredSprint;
            ViewCombo.Items.Add(AppStrings.Get("Sprint_ViewBoard"));   // 0 = StoryBoard
            ViewCombo.Items.Add(AppStrings.Get("Sprint_ViewByPerson")); // 1 = TaskBoard (por pessoa)
            ViewCombo.SelectedIndex = 0;
            SearchScopeCombo.Items.Add(AppStrings.Get("Sprint_ScopeBoth"));  // 0
            SearchScopeCombo.Items.Add(AppStrings.Get("Sprint_ScopeTask"));  // 1
            SearchScopeCombo.Items.Add(AppStrings.Get("Sprint_ScopeStory")); // 2
            SearchScopeCombo.SelectedIndex = 0;
            // Carrega os últimos filtros salvos (aplicados na 1ª carga do board).
            _prefs = LoadPrefs();
            AutoOpenCheck.IsChecked = _prefs.AutoOpen;
            // Restaura geometria/estado da janela do TaskBoard.
            if (_prefs.WinWidth > 200 && _prefs.WinHeight > 200)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _prefs.WinLeft; Top = _prefs.WinTop; Width = _prefs.WinWidth; Height = _prefs.WinHeight;
            }
            Closing += (_, _) => SavePrefs();
            // Restaura a preferência de "dias anteriores" do Closed (0/ausente = padrão 30).
            if (_prefs.ClosedDays is int sd && sd > 0)
            {
                _closedDays = sd;
                ClosedDaysBox.Text = sd.ToString();
            }
            Loaded += async (_, _) => await LoadSprintsAsync();
        }

        private async Task LoadSprintsAsync()
        {
            StatusText.Text = AppStrings.Get("Sprint_Loading");
            try
            {
                _currentUser = await TfsImportService.GetCurrentUserDisplayNameAsync(_options);
                _sprints = await TfsImportService.ListSprintsAsync(_options);
                // Lista multi-seleção (estilo do filtro de pessoa): cada sprint com data início–fim.
                PopulateSprintList();
                // Seleção inicial: multi salva → última sprint salva → sprint atual (data)
                // → sugerida do cronograma → última da lista.
                var initial = new List<string>();
                if (_prefs.SprintPaths is { Count: > 1 } saved2
                    && saved2.All(p => _sprints.Any(s => string.Equals(s.Path, p, StringComparison.OrdinalIgnoreCase))))
                    initial = saved2.ToList();
                else
                {
                    string? one = null;
                    var saved = LoadLastSprintPath();
                    if (!string.IsNullOrWhiteSpace(saved) && _sprints.Any(s => string.Equals(s.Path, saved, StringComparison.OrdinalIgnoreCase)))
                        one = saved;
                    one ??= CurrentSprintPath();
                    if (one == null && !string.IsNullOrWhiteSpace(_preferredSprint)
                        && _sprints.Any(s => string.Equals(s.Path, _preferredSprint, StringComparison.OrdinalIgnoreCase)))
                        one = _preferredSprint;
                    one ??= _sprints.LastOrDefault()?.Path;
                    if (!string.IsNullOrEmpty(one)) initial.Add(one);
                }
                StatusText.Text = "";
                ApplySprintChecks(initial);
                await ReloadBoardAsync(initial);
            }
            catch (Exception ex)
            {
                StatusText.Text = "";
                MessageBox.Show(this, AppStrings.Get("Sprint_Error", ex.Message),
                    "NXProject", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Preenche os checkboxes das sprints com o rótulo "Nome (dd/MM/aa–dd/MM/aa)".
        private void PopulateSprintList()
        {
            SprintMultiList.Children.Clear();
            foreach (var sp in _sprints.Where(s => !string.IsNullOrEmpty(s.Path)))
                SprintMultiList.Children.Add(new CheckBox { Content = SprintLabel(sp), Tag = sp.Path,
                    IsChecked = _sprintPaths.Contains(sp.Path), Margin = new Thickness(0, 1, 0, 1) });
        }

        private static string SprintLabel(TfsImportService.SprintInfo s)
        {
            if (s.Start is { } st && s.End is { } en)
                return $"{s.Name}   ({st:dd/MM/yy}–{en:dd/MM/yy})";
            return s.Name;
        }

        private void ApplySprintChecks(List<string> paths)
        {
            foreach (var cb in SprintMultiList.Children.OfType<CheckBox>())
                cb.IsChecked = cb.Tag is string tp && paths.Contains(tp);
            UpdateSprintToggleText();
        }

        private void UpdateSprintToggleText()
        {
            if (_sprintPaths.Count == 0)
                SprintFilterToggle.Content = AppStrings.Get("Sprint_AllSprints");
            else if (_sprintPaths.Count == 1)
            {
                var sp = _sprints.FirstOrDefault(s => string.Equals(s.Path, _sprintPaths[0], StringComparison.OrdinalIgnoreCase));
                SprintFilterToggle.Content = sp != null ? SprintLabel(sp) : _sprintPaths[0];
            }
            else
                SprintFilterToggle.Content = AppStrings.Get("Sprint_MultiN", _sprintPaths.Count.ToString());
        }

        // Caminho da sprint atual pela data (a de menor duração que contém hoje).
        private string? CurrentSprintPath()
        {
            var today = DateTime.Today;
            return _sprints
                .Where(s => !string.IsNullOrEmpty(s.Path) && s.Start is { } st && s.End is { } en && today >= st && today <= en)
                .OrderBy(s => (s.End!.Value - s.Start!.Value).TotalDays)
                .FirstOrDefault()?.Path;
        }

        private async void OnCurrentSprintClick(object sender, RoutedEventArgs e)
        {
            var path = CurrentSprintPath();
            if (path == null) return;
            var sel = new List<string> { path };
            ApplySprintChecks(sel);
            await ReloadBoardAsync(sel);
            SavePrefs();
        }

        // Recarrega do TFS. Se houver mudanças pendentes, pede confirmação (o reload as descarta).
        private async void OnReloadClick(object sender, RoutedEventArgs e)
        {
            if (PendingCount() > 0 &&
                MessageBox.Show(this, AppStrings.Get("Sprint_ReloadConfirm"), "NXProject",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            await ReloadBoardAsync(_sprintPaths.ToList());
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            if (PendingCount() > 0 &&
                MessageBox.Show(this, AppStrings.Get("Sprint_CloseConfirm"), "NXProject",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            Close();
        }

        // Ao abrir o popup, reflete a seleção atual (evita "união" acidental com a sprint anterior).
        private void OnSprintFilterOpened(object sender, RoutedEventArgs e) => ApplySprintChecks(_sprintPaths.ToList());

        // Aplica a seleção de sprints (0 marcadas = todas as sprints).
        private async void OnSprintMultiApply(object sender, RoutedEventArgs e)
        {
            var paths = SprintMultiList.Children.OfType<CheckBox>()
                .Where(cb => cb.IsChecked == true && cb.Tag is string).Select(cb => (string)cb.Tag!).ToList();
            SprintFilterToggle.IsChecked = false;
            await ReloadBoardAsync(paths);
            SavePrefs();
        }

        private void OnSprintMultiNone(object sender, RoutedEventArgs e)
        {
            foreach (var cb in SprintMultiList.Children.OfType<CheckBox>()) cb.IsChecked = false;
        }

        // Compat: recarrega uma única sprint (ou "todas" com path vazio).
        private Task ReloadBoardAsync(string path) =>
            ReloadBoardAsync(string.IsNullOrEmpty(path) ? new List<string>() : new List<string> { path });

        // Carrega/recarrega o board (1+ sprints) e reseta o estado local (pendências, filtros).
        private async Task ReloadBoardAsync(List<string> paths)
        {
            StatusText.Text = AppStrings.Get("Sprint_Loading");
            try
            {
                _sprintPaths = paths;
                UpdateSprintToggleText();
                _board = await TfsImportService.BuildSprintBoardAsync(_options, paths);
                var people = _board.People.ToList();
                // Mantém só as pessoas ainda existentes no board (preserva a seleção múltipla).
                _selectedPeople.RemoveWhere(p => !people.Contains(p, StringComparer.CurrentCultureIgnoreCase));
                PopulatePersonFilter(people);
                _storyById = _board.Stories.Where(s => s.Id > 0).ToDictionary(s => s.Id);
                _selectedStoryIds.Clear();
                // Com projeto aberto: por padrão filtra só as Stories dele (Todo Portfólio desmarcado).
                var openIds = _board.Stories.Where(s => s.Id > 0 && _scheduleIds.Contains(s.Id)).Select(s => s.Id).ToList();
                if (openIds.Count > 0)
                    foreach (var oid in openIds) _selectedStoryIds.Add(oid);
                // Na PRIMEIRA carga: restaura os filtros salvos (ou o padrão: esconder Closed).
                // Nos reloads seguintes: PRESERVA a seleção do usuário (só descarta estados que
                // sumiram do board), para não voltar a esconder Closed a cada troca de sprint.
                if (_firstBoardLoad)
                {
                    _restoringPrefs = true;
                    try
                    {
                        _selectedPeople.Clear();
                        if (_prefs.Persons is { Count: > 0 } pl)
                            foreach (var p in pl.Where(x => people.Contains(x, StringComparer.CurrentCultureIgnoreCase)))
                                _selectedPeople.Add(p);
                        else if (MatchCurrentUser() is { } me) // padrão: o usuário atual
                            _selectedPeople.Add(me);
                        PopulatePersonFilter(people);
                        if (_prefs.View is int vw && vw >= 0 && vw < ViewCombo.Items.Count) ViewCombo.SelectedIndex = vw;
                        if (_prefs.OnlySchedule is bool os) OnlyScheduleCheck.IsChecked = os;
                        if (_prefs.EditMode is bool em) EditModeCheck.IsChecked = em;
                        _hiddenStates.Clear();
                        if (_prefs.HiddenStates is { } hs)
                            foreach (var st in hs) _hiddenStates.Add(st); // pode ser vazio (Closed visível)
                        else
                            foreach (var st in _board.States.Where(IsClosedState)) _hiddenStates.Add(st);
                    }
                    finally { _restoringPrefs = false; }
                    _firstBoardLoad = false;
                }
                else
                {
                    _hiddenStates.RemoveWhere(s => !_board.States.Contains(s, StringComparer.OrdinalIgnoreCase));
                }
                _boardEverLoaded = true; // a partir daqui, mudanças de filtro podem ser salvas
                _pending.Clear();
                _applied.Clear();
                _order.Clear();
                _doing.Clear();
                _appliedDoing.Clear();
                _descPending.Clear();
                _tramitePending.Clear();
                _ownerPending.Clear();
                _ownerApplied.Clear();
                _titlePending.Clear();
                _titleApplied.Clear();
                _estPending.Clear();
                _donePending.Clear();
                _deletePending.Clear();
                _iterPending.Clear();
                _iterApplied.Clear();
                _blockPending.Clear();
                _tagsApplied.Clear();
                _featurePending.Clear();
                _featureApplied.Clear();
                _newCards.Clear();
                _prioPending.Clear();
                _prioApplied.Clear();
                _storyRank.Clear();
                _storyRankPending.Clear();
                _storyStatePending.Clear();
                _storyStateApplied.Clear();
                _taskRank.Clear();
                _taskRankPending.Clear();
                double sr = 0;
                foreach (var s in _board.Stories.Where(s => s.Id > 0))
                    _storyRank[s.Id] = double.IsNaN(s.StackRank) ? (1e6 + sr++) : s.StackRank;
                double k = 0;
                foreach (var t in _board.Stories.SelectMany(s => s.Tasks))
                {
                    _order[t.Id] = k++;
                    _taskRank[t.Id] = double.IsNaN(t.StackRank) ? (1e6 + k) : t.StackRank;
                    if (HasTag(t.Tags, "Doing") || HasTag(t.Tags, "Done")) { _doing.Add(t.Id); _appliedDoing.Add(t.Id); }
                }
                UpdatePendingButton();
                PopulateStoryFilter();
                PopulateStateFilter();
                Render();

                // Descobre uma vez por sessão o máximo de Priority aceito pelo template (validateOnly).
                if (_discoveredPrioMax == 0)
                {
                    var sample = _board.Stories.SelectMany(s => s.Tasks).FirstOrDefault(t => t.Id > 0);
                    if (sample != null)
                    {
                        _discoveredPrioMax = await TfsImportService.DiscoverTaskPriorityMaxAsync(_options, sample.Id);
                        if (_discoveredPrioMax > 0) Render(); // re-render com a faixa correta
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "";
                MessageBox.Show(this, AppStrings.Get("Sprint_Error", ex.Message),
                    "NXProject", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Cards efetivos (do TFS + novos locais) para renderizar/filtrar.
        private List<(TfsImportService.SprintStoryRow Story, List<TfsImportService.SprintTaskCard> Tasks)> EffectiveStories()
        {
            var list = new List<(TfsImportService.SprintStoryRow, List<TfsImportService.SprintTaskCard>)>();
            if (_board == null) return list;
            foreach (var s in _board.Stories)
            {
                var tks = new List<TfsImportService.SprintTaskCard>(s.Tasks);
                tks.AddRange(_newCards.Where(n => n.Type == "Task" && n.ParentId == s.Id).Select(NewToCard));
                list.Add((s, tks));
            }
            foreach (var ns in _newCards.Where(n => n.Type == "Story"))
            {
                var row = new TfsImportService.SprintStoryRow(ns.TempId, ns.Title, "New", "", new())
                { FeatureId = ns.FeatureId, FeatureTitle = ns.FeatureTitle };
                var tks = _newCards.Where(n => n.Type == "Task" && n.ParentId == ns.TempId).Select(NewToCard).ToList();
                list.Add((row, tks));
            }
            return list;
        }

        private TfsImportService.SprintTaskCard NewToCard(NewCard n) =>
            new(n.TempId, n.Title, "New", n.AssignedTo ?? "", "", n.ParentId, "", null, 0, 0) { IterationPath = n.IterationPath };

        // Diálogo simples de uma linha (nome do novo card).
        private string? PromptText(string title)
        {
            var win = new Window { Title = title, Width = 460, Height = 150, Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var dock = new DockPanel { Margin = new Thickness(12) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var tb = new TextBox { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            var okBtn = new Button { Content = "OK", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            okBtn.Click += (_, _) => { win.DialogResult = true; };
            var cancel = new Button { Content = AppStrings.Get("Setup_Close"), Padding = new Thickness(14, 3, 14, 3), IsCancel = true };
            buttons.Children.Add(okBtn); buttons.Children.Add(cancel);
            dock.Children.Add(buttons);
            dock.Children.Add(tb);
            win.Content = dock;
            tb.Loaded += (_, _) => tb.Focus();
            return win.ShowDialog() == true && !string.IsNullOrWhiteSpace(tb.Text) ? tb.Text.Trim() : null;
        }

        // Cria um card NOVO vazio (editável no próprio card): Nome, Responsável, HH, Descrição.
        private void AddNewStory(int featureId, string featureTitle)
        {
            _newCards.Add(new NewCard { TempId = _nextTempId--, Type = "Story", ParentId = featureId, FeatureId = featureId, FeatureTitle = featureTitle, IterationPath = DefaultNewIterationPath() });
            UpdatePendingButton(); Render();
        }

        private void AddNewTask(int storyId, string? assignedTo = null)
        {
            // Já nasce na faixa da pessoa onde foi criado (fica no grupo da Story).
            var who = string.Equals(assignedTo, AppStrings.Get("Sprint_NoOwner"), StringComparison.Ordinal) ? "" : (assignedTo ?? "");
            _newCards.Add(new NewCard { TempId = _nextTempId--, Type = "Task", ParentId = storyId, AssignedTo = who, IterationPath = DefaultNewIterationPath() });
            UpdatePendingButton(); Render();
        }

        // Sprint sugerida p/ novos cards: a única aberta → a atual (se estiver entre as selecionadas)
        // → a 1ª selecionada → a atual → a 1ª sprint real.
        private string DefaultNewIterationPath()
        {
            if (!string.IsNullOrEmpty(_sprintPath)) return _sprintPath;
            var cur = CurrentSprintPath();
            if (cur != null && (_sprintPaths.Count == 0 || _sprintPaths.Contains(cur))) return cur;
            if (_sprintPaths.Count > 0) return _sprintPaths[0];
            return cur ?? _sprints.FirstOrDefault(s => !string.IsNullOrEmpty(s.Path))?.Path ?? "";
        }

        // Sprints oferecidas no card novo: as selecionadas (se houver) ou todas as reais.
        private List<TfsImportService.SprintInfo> NewCardSprintOptions() =>
            (_sprintPaths.Count > 0
                ? _sprints.Where(s => _sprintPaths.Contains(s.Path))
                : _sprints.Where(s => !string.IsNullOrEmpty(s.Path))).ToList();

        private void OnFilterChanged(object sender, RoutedEventArgs e)
        {
            // Ao entrar em "Pessoa & Task" sem ninguém marcado, já traz o usuário do NX.
            if (ViewCombo.SelectedIndex == 1 && _selectedPeople.Count == 0
                && _board != null && MatchCurrentUser() is { } me)
            {
                _selectedPeople.Add(me);
                PopulatePersonFilter(_board.People.ToList());
            }
            Render();
            SavePrefs();
        }

        // (Re)constrói a lista de checkboxes de pessoas e atualiza o texto do botão.
        private void PopulatePersonFilter(List<string> people)
        {
            PersonFilterList.Children.Clear();
            foreach (var p in people)
                PersonFilterList.Children.Add(new CheckBox { Content = p, Tag = p,
                    IsChecked = _selectedPeople.Contains(p), Margin = new Thickness(0, 1, 0, 1) });
            UpdatePersonToggleText();
        }

        private void UpdatePersonToggleText()
        {
            PersonFilterToggle.Content = _selectedPeople.Count == 0
                ? AppStrings.Get("Sprint_PersonAll")
                : _selectedPeople.Count == 1 ? _selectedPeople.First()
                : AppStrings.Get("Sprint_PersonN", _selectedPeople.Count.ToString());
        }

        private void OnPersonFilterApply(object sender, RoutedEventArgs e)
        {
            _selectedPeople.Clear();
            foreach (var cb in PersonFilterList.Children.OfType<CheckBox>())
                if (cb.IsChecked == true && cb.Tag is string p) _selectedPeople.Add(p);
            PersonFilterToggle.IsChecked = false;
            UpdatePersonToggleText();
            Render();
            SavePrefs();
        }

        private void OnPersonFilterNone(object sender, RoutedEventArgs e)
        {
            foreach (var cb in PersonFilterList.Children.OfType<CheckBox>()) cb.IsChecked = false;
        }

        private void OnAutoOpenChanged(object sender, RoutedEventArgs e)
        {
            _prefs.AutoOpen = AutoOpenCheck.IsChecked == true;
            SavePrefs();
        }

        // Casa o usuário autenticado (connectionData) com uma pessoa da sprint.
        private string? MatchCurrentUser()
        {
            if (string.IsNullOrWhiteSpace(_currentUser) || _board == null) return null;
            var me = _currentUser.Trim();
            return _board.People.FirstOrDefault(p =>
                string.Equals(p, me, StringComparison.CurrentCultureIgnoreCase)
                || p.IndexOf(me, StringComparison.CurrentCultureIgnoreCase) >= 0
                || me.IndexOf(p, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }

        // Estado efetivo: pendente (arrasto) → já gravado → original do DevOps.
        private string EffState(TfsImportService.SprintTaskCard t)
            => _pending.TryGetValue(t.Id, out var p) ? p
             : _applied.TryGetValue(t.Id, out var a) ? a : t.State;

        private void PopulateStateFilter()
        {
            StateFilterList.Children.Clear();
            if (_board == null) return;
            foreach (var st in _board.States)
                StateFilterList.Children.Add(new CheckBox
                {
                    Content = st, Tag = st, Margin = new Thickness(2),
                    IsChecked = !_hiddenStates.Contains(st)
                });
        }

        private void OnStateFilterAll(object sender, RoutedEventArgs e)
        {
            _hiddenStates.Clear();
            foreach (var cb in StateFilterList.Children.OfType<CheckBox>()) cb.IsChecked = true;
            Render();
        }

        private void OnStateFilterApply(object sender, RoutedEventArgs e)
        {
            _hiddenStates.Clear();
            foreach (var cb in StateFilterList.Children.OfType<CheckBox>())
                if (cb.IsChecked != true && cb.Tag is string s) _hiddenStates.Add(s);
            if (int.TryParse(ClosedDaysBox.Text?.Trim(), out var d) && d >= 0)
                _closedDays = d; // 0 = todos (não persiste)
            StateFilterToggle.IsChecked = false;
            Render();
            SavePrefs(); // grava estados ocultos + ClosedDays (>0)
        }

        // Botões ✎ (descrição) e 💬 (trâmite) para Story/Task — reusam o editor WebView do NX.
        private void AddEditButtons(Panel panel, int id, string title, string currentOwner = "", string kind = "Story", string currentIteration = "")
        {
            if (id <= 0) return;
            // ✎ marca "●" quando há descrição OU responsável/HH/sprint pendente.
            var descDirty = _descPending.ContainsKey(id) || _ownerPending.ContainsKey(id)
                || _titlePending.ContainsKey(id) || _estPending.ContainsKey(id) || _donePending.ContainsKey(id)
                || _iterPending.ContainsKey(id) || _featurePending.ContainsKey(id);
            var desc = new Button { Content = descDirty ? "✎●" : "✎", FontSize = 11,
                Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 4, 0),
                Foreground = descDirty ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Black,
                ToolTip = AppStrings.Get("Sprint_EditDesc") };
            desc.Click += async (_, _) => await EditDescriptionAsync(id, title, currentOwner, kind, currentIteration);
            panel.Children.Add(desc);
            var tram = new Button { Content = _tramitePending.ContainsKey(id) ? "💬●" : "💬", FontSize = 11,
                Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 4, 0),
                Foreground = _tramitePending.ContainsKey(id) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Black,
                ToolTip = AppStrings.Get("Sprint_EditTramite") };
            tram.Click += (_, _) => EditTramite(id, title);
            panel.Children.Add(tram);
        }

        // Descrição: abre o editor (WebView) com a descrição atual do DevOps (ou o rascunho pendente).
        private async Task EditDescriptionAsync(int id, string title, string currentOwner = "", string kind = "Story", string currentIteration = "")
        {
            // Nome efetivo (pendente > aplicado > título recebido), editável na mesma tela.
            var effName = EffTitle(id, title);
            var pt = new NXProject.Models.ProjectTask { TfsId = id, Name = effName };
            pt.Description = _descPending.TryGetValue(id, out var d) ? d
                : (await TfsImportService.LoadWorkItemDescriptionHtmlAsync(_options, id)) ?? string.Empty;
            // Responsável efetivo (pendente > aplicado > valor do board), editável na mesma tela.
            var owner = _ownerPending.TryGetValue(id, out var op) ? op
                : _ownerApplied.TryGetValue(id, out var oa) ? oa : currentOwner;
            var people = _board?.People.ToList() ?? new List<string>();
            // HH atuais do DevOps (com pendências locais sobrepostas) + estado p/ decidir HH Realizado.
            double? est = null, done = null; string hState = kind;
            if (id > 0)
            {
                var (e, c, st) = await TfsImportService.GetWorkItemHoursAsync(_options, id);
                est = _estPending.TryGetValue(id, out var ep) ? ep : e;
                done = _donePending.TryGetValue(id, out var dp) ? dp : c;
                // Estado EFETIVO (considera arrasto pendente p/ Closed) → libera o HH Realizado.
                hState = _cardById.TryGetValue(id, out var cardH) ? EffState(cardH) : st;
            }
            // Sprint editável para Story e Task (com ID). Oferece as sprints reais; valor efetivo (pendente).
            System.Collections.Generic.IReadOnlyList<(string Name, string Path)>? sprints = null;
            var effIter = "";
            if (id > 0)
            {
                // Para Task, a iteração-base vem do próprio card se não veio no parâmetro.
                var baseIterOrig = !string.IsNullOrEmpty(currentIteration) ? currentIteration
                    : (_cardById.TryGetValue(id, out var cIt) ? cIt.IterationPath : "");
                sprints = _sprints.Where(s => !string.IsNullOrEmpty(s.Path)).Select(s => (s.Name, s.Path)).ToList();
                effIter = _iterPending.TryGetValue(id, out var ip) ? ip
                    : _iterApplied.TryGetValue(id, out var ia) ? ia : baseIterOrig;
            }
            // Bloqueio (tag) editável para itens com ID.
            var curTags = kind == "Task"
                ? (_cardById.TryGetValue(id, out var cc) ? cc.Tags : "")
                : (StoryById(id)?.Tags ?? "");
            var curBlocked = id > 0 && EffBlocked(id, curTags);
            // Estado editável para Story (na visão Pessoa & Task não há coluna de estado da Story).
            System.Collections.Generic.IReadOnlyList<string>? storyStates = null;
            var effStState = "";
            if (kind == "Story" && id > 0 && StoryById(id) is { } srow)
            {
                storyStates = _board?.States?.ToList();
                effStState = EffStoryState(srow);
            }
            // Troca de Feature: só para Story em New (evita reparent de itens em andamento).
            System.Collections.Generic.IReadOnlyList<(string Title, int Id)>? features = null;
            var curFeatureId = 0;
            if (kind == "Story" && id > 0 && StoryById(id) is { } fsrow
                && string.Equals(EffStoryState(fsrow), "New", StringComparison.OrdinalIgnoreCase))
            {
                features = _board?.Stories.Where(s => s.FeatureId > 0)
                    .Select(s => (Title: s.FeatureTitle, Id: s.FeatureId))
                    .Distinct().OrderBy(f => f.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
                curFeatureId = _featurePending.TryGetValue(id, out var fp) ? fp
                    : _featureApplied.TryGetValue(id, out var fa) ? fa : fsrow.FeatureId;
            }
            var dlg = new TaskDescriptionEditWindow(pt, people, owner, enableNameEdit: id > 0, objectKind: kind,
                enableHours: id > 0, estimate: est, completed: done, state: hState,
                sprints: sprints, currentIteration: effIter,
                enableBlocked: id > 0, currentBlocked: curBlocked,
                states: storyStates, currentState: effStState,
                features: features, currentFeatureId: curFeatureId) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _descPending[id] = pt.Description ?? string.Empty;
                if (dlg.IterationChanged)
                {
                    var origIter = !string.IsNullOrEmpty(currentIteration) ? currentIteration
                        : (_cardById.TryGetValue(id, out var c2) ? c2.IterationPath : "");
                    var baseIter = _iterApplied.TryGetValue(id, out var bi) ? bi : origIter;
                    var chosenIter = dlg.SelectedIteration ?? string.Empty;
                    if (string.Equals(chosenIter, (baseIter ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                        _iterPending.Remove(id);
                    else
                        _iterPending[id] = chosenIter;
                }
                if (dlg.HoursChanged)
                {
                    _estPending[id] = dlg.EstimatedHours;
                    // HH Realizado só é enviado quando visível (estado Closed).
                    if (dlg.CompletedHours.HasValue || _donePending.ContainsKey(id))
                        _donePending[id] = dlg.CompletedHours;
                }
                if (dlg.NameChanged)
                {
                    var baseName = _titleApplied.TryGetValue(id, out var bn) ? bn : title;
                    var newName = dlg.EditedName ?? string.Empty;
                    if (string.Equals(newName.Trim(), (baseName ?? "").Trim(), StringComparison.Ordinal))
                        _titlePending.Remove(id);
                    else
                        _titlePending[id] = newName;
                }
                if (dlg.BlockedChanged)
                {
                    var baseBlocked = HasTag(EffTags(id, curTags), BlockedTag);
                    if (dlg.Blocked == baseBlocked) _blockPending.Remove(id); else _blockPending[id] = dlg.Blocked;
                }
                if (dlg.StateWasChanged && StoryById(id) is { } srow2)
                {
                    var baseState = _storyStateApplied.TryGetValue(id, out var bs) ? bs : srow2.State;
                    var chosen = dlg.SelectedState ?? string.Empty;
                    if (SameState(chosen, baseState)) _storyStatePending.Remove(id);
                    else _storyStatePending[id] = chosen;
                }
                if (dlg.FeatureChanged && StoryById(id) is { } srow3)
                {
                    var baseFeat = _featureApplied.TryGetValue(id, out var bf) ? bf : srow3.FeatureId;
                    if (dlg.SelectedFeatureId == baseFeat) _featurePending.Remove(id);
                    else _featurePending[id] = dlg.SelectedFeatureId;
                }
                if (dlg.OwnerChanged)
                {
                    var baseline = _ownerApplied.TryGetValue(id, out var b) ? b : currentOwner;
                    var chosen = dlg.SelectedOwner ?? string.Empty;
                    if (string.Equals(chosen.Trim(), (baseline ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                        _ownerPending.Remove(id);
                    else
                        _ownerPending[id] = chosen;
                }
                UpdatePendingButton();
                Render();
            }
        }

        // Trâmite: tela com HISTÓRICO dos comentários + editor rico (com imagem). Vale p/ Story e Task.
        // O novo trâmite fica pendente e é gravado como comentário no Salvar TFS.
        private void EditTramite(int id, string title)
        {
            var draft = _tramitePending.TryGetValue(id, out var t) ? t : string.Empty;
            var dlg = new TramiteWindow(id, AppStrings.Get("Sprint_EditTramite") + " — " + title, draft) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                if (string.IsNullOrWhiteSpace(TfsImportService.NormalizeCommentText(dlg.NewComment)))
                    _tramitePending.Remove(id);
                else
                    _tramitePending[id] = dlg.NewComment;
                UpdatePendingButton();
                Render();
            }
        }

        private static bool HasTag(string tags, string tag) =>
            !string.IsNullOrEmpty(tags) && tags.Split(';').Any(x => x.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase));

        // Diferença de marcações Doing pendentes (marcadas − já gravadas) + mudanças de estado.
        private int PendingCount()
        {
            var doingDiff = _doing.Except(_appliedDoing).Count() + _appliedDoing.Except(_doing).Count();
            return _pending.Count + doingDiff + _descPending.Count + _tramitePending.Count + _newCards.Count + _prioPending.Count + _storyRankPending.Count + _taskRankPending.Count + _storyStatePending.Count + _ownerPending.Count + _titlePending.Count + _estPending.Count + _donePending.Count + _deletePending.Count + _iterPending.Count + _blockPending.Count + _featurePending.Count;
        }

        private void UpdatePendingButton()
        {
            var n = PendingCount();
            UpdateTfsButton.IsEnabled = n > 0;
            RevertButton.IsEnabled = n > 0;
            UpdateTfsButton.Content = n > 0
                ? AppStrings.Get("Sprint_UpdateTfsN", n.ToString())
                : AppStrings.Get("Sprint_UpdateTfs");
        }

        // Reverte as mudanças pendentes (estado + Doing) antes de gravar no TFS.
        private void OnRevertClick(object sender, RoutedEventArgs e)
        {
            _pending.Clear();
            _descPending.Clear();
            _tramitePending.Clear();
            _ownerPending.Clear();
            _titlePending.Clear();
            _estPending.Clear();
            _donePending.Clear();
            _deletePending.Clear();
            _iterPending.Clear();
            _blockPending.Clear();
            _featurePending.Clear();
            _newCards.Clear();
            _prioPending.Clear();
            _storyRankPending.Clear();
            _storyStatePending.Clear();
            _taskRankPending.Clear();
            // re-semeia os ranks (Story e Task) a partir do TFS
            if (_board != null)
            {
                _storyRank.Clear();
                double sr = 0;
                foreach (var s in _board.Stories.Where(s => s.Id > 0))
                    _storyRank[s.Id] = double.IsNaN(s.StackRank) ? (1e6 + sr++) : s.StackRank;
                _taskRank.Clear();
                double tr = 0;
                foreach (var t in _board.Stories.SelectMany(s => s.Tasks))
                    _taskRank[t.Id] = double.IsNaN(t.StackRank) ? (1e6 + tr++) : t.StackRank;
            }
            _doing.Clear();
            foreach (var id in _appliedDoing) _doing.Add(id);
            UpdatePendingButton();
            Render();
        }

        // Grava no TFS os estados pendentes (arrastados). O DevOps aplica a permissão: 403 =
        // sem escrita (não é responsável, não está no grupo, ou o token não permite).
        private async void OnUpdateTfsClick(object sender, RoutedEventArgs e)
        {
            if (PendingCount() == 0) return;
            UpdateTfsButton.IsEnabled = false;
            var ok = 0;
            var fails = new List<string>();
            // Barra de progresso + texto da etapa durante a gravação.
            var total = PendingCount();
            SaveProgress.Maximum = Math.Max(1, total);
            SaveProgress.Value = 0;
            SaveProgress.Visibility = Visibility.Visible;
            void Phase(string key)
            {
                StatusText.Text = AppStrings.Get(key);
                SaveProgress.Value = Math.Min(SaveProgress.Maximum, ok + fails.Count);
            }
            try
            {
            var reload = false;

            // 1) Mudanças de estado (arrasto). Guarda os que mudaram para reavaliar a tag (Doing→Done).
            Phase("Sprint_PhState");
            var stateChanged = _pending.Keys.ToHashSet();
            foreach (var kv in _pending.ToList())
            {
                // Fechar (Closed) exige HH Realizado preenchido (> 0). Bloqueia e pede para informar.
                if (IsClosedState(kv.Value))
                {
                    double? completed = _donePending.TryGetValue(kv.Key, out var dv) ? dv : null;
                    if (!(completed > 0))
                    {
                        var (_, c, _) = await TfsImportService.GetWorkItemHoursAsync(_options, kv.Key);
                        completed = _donePending.TryGetValue(kv.Key, out var dv2) ? dv2 : c;
                    }
                    if (!(completed > 0))
                    {
                        fails.Add(AppStrings.Get("Sprint_ClosedNeedsHH", kv.Key.ToString()));
                        stateChanged.Remove(kv.Key);
                        continue; // não fecha sem HH Realizado
                    }
                }
                var (success, msg) = await TfsImportService.SetWorkItemStateAsync(_options, kv.Key, kv.Value);
                if (success) { _applied[kv.Key] = kv.Value; _pending.Remove(kv.Key); ok++; }
                else { fails.Add($"#{kv.Key}: {msg}"); stateChanged.Remove(kv.Key); }
            }

            // 2) Tags de andamento. Marcado + (novo OU mudou de estado) → grava Doing/Done conforme
            //    o estado atual (Closed vira Done). Desmarcado que já tinha a tag → remove.
            string? EffCard(int id) => _board?.Stories.SelectMany(s => s.Tasks)
                .FirstOrDefault(t => t.Id == id) is { } c ? EffState(c) : null;
            foreach (var id in _doing.ToList())
            {
                var isNew = !_appliedDoing.Contains(id);
                if (!isNew && !stateChanged.Contains(id)) continue; // já gravado e sem mudança de estado
                var tag = EffCard(id) is { } st && IsClosedState(st) ? "Done" : "Doing";
                var (success, msg) = await TfsImportService.SetDoingTagAsync(_options, id, tag);
                if (success) { _appliedDoing.Add(id); if (isNew) ok++; } else fails.Add($"#{id} (tag): {msg}");
            }
            foreach (var id in _appliedDoing.Except(_doing).ToList())
            {
                var (success, msg) = await TfsImportService.SetDoingTagAsync(_options, id, null);
                if (success) { _appliedDoing.Remove(id); ok++; } else fails.Add($"#{id} (tag): {msg}");
            }

            // 3) Descrição (grava direto no TFS; 403 = sem permissão).
            Phase("Sprint_PhFields");
            foreach (var kv in _descPending.ToList())
            {
                var (success, msg) = await TfsImportService.SetWorkItemDescriptionAsync(_options, kv.Key, kv.Value);
                if (success) { _descPending.Remove(kv.Key); ok++; } else fails.Add($"#{kv.Key} (descr): {msg}");
            }

            // 3b) Responsável (System.AssignedTo). 403 = sem permissão.
            foreach (var kv in _ownerPending.ToList())
            {
                var (success, msg) = await TfsImportService.SetWorkItemAssignedToAsync(_options, kv.Key, kv.Value);
                if (success) { _ownerApplied[kv.Key] = kv.Value; _ownerPending.Remove(kv.Key); ok++; }
                else fails.Add($"#{kv.Key} (responsável): {msg}");
            }

            // 3c) Nome/título (System.Title). 403 = sem permissão.
            foreach (var kv in _titlePending.ToList())
            {
                var (success, msg) = await TfsImportService.SetWorkItemTitleAsync(_options, kv.Key, kv.Value);
                if (success) { _titleApplied[kv.Key] = kv.Value; _titlePending.Remove(kv.Key); ok++; }
                else fails.Add($"#{kv.Key} (nome): {msg}");
            }

            // 3d) HH estimado (OriginalEstimate) e HH realizado (CompletedWork). 403 = sem permissão.
            foreach (var wid in _estPending.Keys.Union(_donePending.Keys).ToList())
            {
                double? eh = _estPending.TryGetValue(wid, out var ev) ? ev : null;
                double? ch = _donePending.TryGetValue(wid, out var cv) ? cv : null;
                var (success, msg) = await TfsImportService.SetWorkItemHoursAsync(_options, wid, eh, ch);
                if (success) { _estPending.Remove(wid); _donePending.Remove(wid); ok++; }
                else fails.Add($"#{wid} (HH): {msg}");
            }

            // 3f) Sprint/iteração da Story (System.IterationPath). 403 = sem permissão.
            foreach (var kv in _iterPending.ToList())
            {
                var (success, msg) = await TfsImportService.SetWorkItemIterationPathAsync(_options, kv.Key, kv.Value);
                if (success) { _iterApplied[kv.Key] = kv.Value; _iterPending.Remove(kv.Key); ok++; }
                else fails.Add($"#{kv.Key} (sprint): {msg}");
            }

            Phase("Sprint_PhFields");
            // 3h) Feature (pai) da Story. 403 = sem permissão.
            foreach (var kv in _featurePending.ToList())
            {
                var (success, msg) = await TfsImportService.SetWorkItemParentAsync(_options, kv.Key, kv.Value);
                if (success) { _featureApplied[kv.Key] = kv.Value; _featurePending.Remove(kv.Key); ok++; reload = true; }
                else fails.Add($"#{kv.Key} (feature): {msg}");
            }

            // 3g) Bloqueio (tag "Blocked"), preservando as demais tags. 403 = sem permissão.
            foreach (var kv in _blockPending.ToList())
            {
                var cur = _cardById.TryGetValue(kv.Key, out var cc2) ? cc2.Tags : (StoryById(kv.Key)?.Tags ?? "");
                var newTags = TfsImportService.ToggleTag(EffTags(kv.Key, cur), BlockedTag, kv.Value);
                var (success, msg) = await TfsImportService.SetWorkItemTagsAsync(_options, kv.Key, newTags);
                if (success) { _tagsApplied[kv.Key] = newTags; _blockPending.Remove(kv.Key); ok++; }
                else fails.Add($"#{kv.Key} (bloqueio): {msg}");
            }

            // 3e) Exclusão de Tasks marcadas (só New). Move para a lixeira do DevOps.
            foreach (var did in _deletePending.ToList())
            {
                var (success, msg) = await TfsImportService.TryDeleteWorkItemAsync(_options, did);
                if (success)
                {
                    _deletePending.Remove(did);
                    _pending.Remove(did); _descPending.Remove(did); _tramitePending.Remove(did);
                    _ownerPending.Remove(did); _titlePending.Remove(did); _prioPending.Remove(did);
                    _estPending.Remove(did); _donePending.Remove(did); _taskRankPending.Remove(did);
                    ok++; reload = true;
                }
                else fails.Add($"#{did} (excluir): {msg}");
            }

            // 4) Trâmite (comentário/discussão; aceita HTML com imagem).
            Phase("Sprint_PhTramite");
            foreach (var kv in _tramitePending.ToList())
            {
                try
                {
                    var posted = await TfsImportService.AddWorkItemCommentIfChangedAsync(_options, kv.Key, kv.Value);
                    if (posted) { _tramitePending.Remove(kv.Key); ok++; }
                    else fails.Add($"#{kv.Key} (trâmite): sem permissão ou sem alteração");
                }
                catch (Exception ex) { fails.Add($"#{kv.Key} (trâmite): {ex.Message}"); }
            }

            // 4b) Prioridade da Task (atributo, dentro da faixa configurada).
            foreach (var kv in _prioPending.ToList())
            {
                var (success, msg) = await TfsImportService.SetWorkItemPriorityAsync(_options, kv.Key, kv.Value);
                if (success) { _prioApplied[kv.Key] = kv.Value; _prioPending.Remove(kv.Key); ok++; } else fails.Add($"#{kv.Key} (prio): {msg}");
            }

            // 4c) Rank (StackRank) das Stories movidas.
            foreach (var id in _storyRankPending.ToList())
            {
                var (success, msg) = await TfsImportService.SetWorkItemStackRankAsync(_options, id, _storyRank.TryGetValue(id, out var r) ? r : 0);
                if (success) { _storyRankPending.Remove(id); ok++; } else fails.Add($"#{id} (rank): {msg}");
            }

            // 4d) Rank (StackRank) das Tasks reordenadas dentro do grupo de prioridade.
            foreach (var id in _taskRankPending.ToList())
            {
                var (success, msg) = await TfsImportService.SetWorkItemStackRankAsync(_options, id, _taskRank.TryGetValue(id, out var r) ? r : 0);
                if (success) { _taskRankPending.Remove(id); ok++; } else fails.Add($"#{id} (rank): {msg}");
            }

            // 4e) Estado das Stories movidas no StoryBoard.
            foreach (var kv in _storyStatePending.ToList())
            {
                var (success, msg) = await TfsImportService.SetWorkItemStateAsync(_options, kv.Key, kv.Value);
                if (success) { _storyStateApplied[kv.Key] = kv.Value; _storyStatePending.Remove(kv.Key); ok++; }
                else fails.Add($"#{kv.Key} (estado Story): {msg}");
            }

            Phase("Sprint_PhNew");
            // 5) Novos cards: cria Stories (recebem ID) e depois Tasks (usando o ID da story-pai).
            // Em "Todas as sprints" não há iteração-alvo: não permite criar itens novos.
            var tempToReal = new Dictionary<int, int>();
            // Iteração-alvo de cada card: a escolhida no card, senão a sprint única aberta.
            string IterOf(NewCard n) => !string.IsNullOrWhiteSpace(n.IterationPath) ? n.IterationPath : _sprintPath;
            foreach (var ns in _newCards.Where(n => n.Type == "Story").ToList())
            {
                if (string.IsNullOrWhiteSpace(ns.Title) || !(ns.Effort is > 0) || string.IsNullOrWhiteSpace(ns.AssignedTo))
                { fails.Add(AppStrings.Get("Sprint_NewIncomplete")); continue; }
                if (string.IsNullOrWhiteSpace(IterOf(ns))) { fails.Add(AppStrings.Get("Sprint_NewNeedsSprint")); continue; }
                bool dup = (_board?.Stories.Any(s => s.FeatureId == ns.FeatureId && s.Title.Trim().Equals(ns.Title.Trim(), StringComparison.CurrentCultureIgnoreCase)) ?? false)
                    || _newCards.Any(o => o != ns && o.Type == "Story" && o.FeatureId == ns.FeatureId && o.Title.Trim().Equals(ns.Title.Trim(), StringComparison.CurrentCultureIgnoreCase));
                if (dup) { fails.Add($"Story '{ns.Title}': {AppStrings.Get("Sprint_DupName")}"); continue; }
                var (nid, msg) = await TfsImportService.CreateChildWorkItemAsync(_options, "User Story", ns.Title.Trim(), ns.ParentId, IterOf(ns),
                    string.IsNullOrWhiteSpace(ns.Description) ? null : TfsImportService.PlainTextToSimpleHtml(ns.Description),
                    string.IsNullOrWhiteSpace(ns.AssignedTo) ? null : ns.AssignedTo, ns.Effort);
                if (nid > 0) { tempToReal[ns.TempId] = nid; _newCards.Remove(ns); ok++; reload = true; }
                else fails.Add($"Story '{ns.Title}': {msg}");
            }
            foreach (var nt in _newCards.Where(n => n.Type == "Task").ToList())
            {
                if (string.IsNullOrWhiteSpace(nt.Title) || !(nt.Effort is > 0) || string.IsNullOrWhiteSpace(nt.AssignedTo))
                { fails.Add(AppStrings.Get("Sprint_NewIncomplete")); continue; }
                if (string.IsNullOrWhiteSpace(IterOf(nt))) { fails.Add(AppStrings.Get("Sprint_NewNeedsSprint")); continue; }
                var parent = nt.ParentId < 0 ? (tempToReal.TryGetValue(nt.ParentId, out var r) ? r : 0) : nt.ParentId;
                if (parent <= 0) { fails.Add($"Task '{nt.Title}': story-pai não criada"); continue; }
                bool dup = (_board?.Stories.FirstOrDefault(s => s.Id == parent)?.Tasks.Any(t => t.Title.Trim().Equals(nt.Title.Trim(), StringComparison.CurrentCultureIgnoreCase)) ?? false)
                    || _newCards.Any(o => o != nt && o.Type == "Task" && o.ParentId == nt.ParentId && o.Title.Trim().Equals(nt.Title.Trim(), StringComparison.CurrentCultureIgnoreCase));
                if (dup) { fails.Add($"Task '{nt.Title}': {AppStrings.Get("Sprint_DupName")}"); continue; }
                var (nid, msg) = await TfsImportService.CreateChildWorkItemAsync(_options, "Task", nt.Title.Trim(), parent, IterOf(nt),
                    string.IsNullOrWhiteSpace(nt.Description) ? null : TfsImportService.PlainTextToSimpleHtml(nt.Description),
                    string.IsNullOrWhiteSpace(nt.AssignedTo) ? null : nt.AssignedTo, nt.Effort);
                if (nid > 0) { _newCards.Remove(nt); ok++; reload = true; }
                else fails.Add($"Task '{nt.Title}': {msg}");
            }

            Phase("Sprint_PhReload");
            SaveProgress.Value = SaveProgress.Maximum;
            if (reload)
                await ReloadBoardAsync(_sprintPaths.ToList()); // recarrega a seleção atual (não "todas")
            UpdatePendingButton();
            Render();
            if (fails.Count == 0)
                MessageBox.Show(this, AppStrings.Get("Sprint_UpdateDone", ok.ToString()),
                    "NXProject", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(this, AppStrings.Get("Sprint_UpdatePartial",
                        ok.ToString(), fails.Count.ToString(), string.Join("\n", fails)),
                    "NXProject", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                SaveProgress.Visibility = Visibility.Collapsed;
                StatusText.Text = "";
                UpdatePendingButton(); // reabilita o botão conforme pendências restantes
            }
        }

        // Filtros de recorte (pessoa, cronograma, story) — não mexem nas colunas.
        private bool PassesBaseFilters(TfsImportService.SprintTaskCard t)
        {
            if (OnlyScheduleCheck.IsChecked == true && !_scheduleIds.Contains(t.Id)) return false;
            if (_selectedPeople.Count > 0 && !_selectedPeople.Contains(t.AssignedTo ?? "")) return false;
            if (_selectedStoryIds.Count > 0 && !(t.ParentId is int p && _selectedStoryIds.Contains(p))) return false;
            // Busca ao vivo com escopo (Ambos / Task / Story).
            var q = SearchBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                var scope = SearchScope();
                var storyTitle = t.ParentId is int sp && _storyById.TryGetValue(sp, out var st) ? st.Title : "";
                bool Has(string? s) => !string.IsNullOrEmpty(s) && s.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
                bool taskMatch = Has(t.Title) || Has(t.AssignedTo) || t.Id.ToString().Contains(q);
                bool storyMatch = Has(storyTitle);
                bool match = scope switch { 1 => taskMatch, 2 => storyMatch, _ => taskMatch || storyMatch };
                if (!match) return false;
            }
            return true;
        }

        // 0 = Ambos, 1 = Task, 2 = Story.
        private int SearchScope() => SearchScopeCombo?.SelectedIndex is int i && i >= 0 ? i : 0;

        private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_board != null) Render();
        }

        // Filtros que escondem CARDS por estado (a coluna continua no board). Cards pendentes
        // (recém-arrastados) sempre aparecem, mesmo num estado escondido/Closed.
        private bool PassesFilters(TfsImportService.SprintTaskCard t)
        {
            if (t.Id < 0) return true; // card novo (local) sempre visível até salvar
            if (!PassesBaseFilters(t)) return false;
            if (_pending.ContainsKey(t.Id)) return true;
            var eff = EffState(t);
            if (_hiddenStates.Contains(eff)) return false;
            if (_closedDays > 0 && IsClosedState(eff)
                && t.ClosedDate is DateTime cd && cd.Date < DateTime.Today.AddDays(-_closedDays))
                return false;
            return true;
        }

        // Popup de filtro: raiz "Projeto Aberto" (Stories do cronograma aberto) — só se houver — e
        // "Todo Portfólio" (o restante). Em cada raiz: Features → Stories, com "sem Feature" à parte.
        private void PopulateStoryFilter()
        {
            StoryFilterList.Children.Clear();
            if (_board == null) return;
            var stories = _board.Stories.Where(s => s.Id > 0).ToList();
            var openStories = stories.Where(s => _scheduleIds.Contains(s.Id)).ToList();
            var others = openStories.Count > 0 ? stories.Where(s => !_scheduleIds.Contains(s.Id)).ToList() : stories;

            if (openStories.Count > 0)
                AddFilterRoot(AppStrings.Get("Sprint_FilterOpenProject"), openStories);
            AddFilterRoot(AppStrings.Get("Sprint_FilterAllPortfolio"), others);
        }

        private void AddFilterRoot(string label, List<TfsImportService.SprintStoryRow> stories)
        {
            var rootPanel = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
            var root = new CheckBox { Content = label, FontWeight = FontWeights.Bold, Margin = new Thickness(2) };
            // Marca/desmarca TUDO abaixo (Features + Stories), não só as Stories.
            root.Click += (_, _) => { foreach (var cb in AllCheckBoxesIn(rootPanel)) cb.IsChecked = root.IsChecked; };
            StoryFilterList.Children.Add(root);
            StoryFilterList.Children.Add(rootPanel);

            // Features (sem-Feature por último).
            foreach (var fg in stories
                         .GroupBy(s => string.IsNullOrWhiteSpace(s.FeatureTitle) ? "" : s.FeatureTitle)
                         .OrderBy(g => g.Key == "" ? 1 : 0).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var featTitle = fg.Key == "" ? AppStrings.Get("Sprint_NoFeature") : fg.Key;
                var container = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
                var childPanel = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
                foreach (var s in fg.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase))
                    childPanel.Children.Add(new CheckBox
                    {
                        Content = s.Title, Tag = s.Id, Margin = new Thickness(2),
                        IsChecked = _selectedStoryIds.Count == 0 || _selectedStoryIds.Contains(s.Id)
                    });
                var feat = new CheckBox { Content = "📦 " + featTitle, FontWeight = FontWeights.Bold,
                    IsChecked = childPanel.Children.OfType<CheckBox>().All(cb => cb.IsChecked == true) };
                feat.Click += (_, _) => { foreach (var cb in childPanel.Children.OfType<CheckBox>()) cb.IsChecked = feat.IsChecked; };
                container.Children.Add(feat);
                container.Children.Add(childPanel);
                rootPanel.Children.Add(container);
            }
            root.IsChecked = StoryCheckBoxesIn(rootPanel).All(cb => cb.IsChecked == true);
        }

        // Todas as checkboxes de Story (Tag = id) dentro de um painel (recursivo).
        private static List<CheckBox> StoryCheckBoxesIn(Panel root)
        {
            var list = new List<CheckBox>();
            void Walk(Panel p)
            {
                foreach (var child in p.Children)
                {
                    if (child is CheckBox cb && cb.Tag is int) list.Add(cb);
                    else if (child is Panel sub) Walk(sub);
                }
            }
            Walk(root);
            return list;
        }

        private IEnumerable<CheckBox> AllStoryCheckBoxes() => StoryCheckBoxesIn(StoryFilterList);

        // Todas as checkboxes (Features + Stories) de um painel, recursivo.
        private static List<CheckBox> AllCheckBoxesIn(Panel root)
        {
            var list = new List<CheckBox>();
            void Walk(Panel p)
            {
                foreach (var child in p.Children)
                {
                    if (child is CheckBox cb) list.Add(cb);
                    if (child is Panel sub) Walk(sub);
                }
            }
            Walk(root);
            return list;
        }

        private void OnStoryFilterAll(object sender, RoutedEventArgs e)
        {
            _selectedStoryIds.Clear();
            foreach (var cb in AllStoryCheckBoxes()) cb.IsChecked = true;
            PopulateStoryFilter(); // re-marca as raízes
            Render();
        }

        private void OnStoryFilterApply(object sender, RoutedEventArgs e)
        {
            var all = AllStoryCheckBoxes().ToList();
            var checkedIds = all.Where(cb => cb.IsChecked == true).Select(cb => (int)cb.Tag!).ToList();
            _selectedStoryIds.Clear();
            // Todas ou nenhuma marcada → sem filtro (mostra todas).
            if (checkedIds.Count > 0 && checkedIds.Count < all.Count)
                foreach (var id in checkedIds) _selectedStoryIds.Add(id);
            StoryFilterToggle.IsChecked = false;
            Render();
        }

        private void Render()
        {
            SummaryHost.Items.Clear();
            BoardHost.Children.Clear();
            if (_board == null) return;

            // Tasks visíveis após filtros (inclui os cards novos locais).
            var eff = EffectiveStories();
            _cardById.Clear();
            foreach (var c in eff.SelectMany(x => x.Tasks)) _cardById[c.Id] = c;
            var visibleByStory = eff
                .Select(x => (Story: x.Story, Tasks: x.Tasks.Where(PassesFilters).ToList()))
                .ToList();
            var allVisible = visibleByStory.SelectMany(x => x.Tasks).ToList();
            var states = _board.States; // as colunas de estado aparecem SEMPRE (alvo de arrasto)
            // Resumo conta todos os cards (só recorte pessoa/story/cronograma), inclusive Closed.
            var summarySet = eff.SelectMany(x => x.Tasks).Where(t => t.Id < 0 || PassesBaseFilters(t)).ToList();

            // ── Resumo por estado (contagem + barra) — usa o estado efetivo (arrasto/gravado) ──
            var maxCount = states.Select(st => summarySet.Count(t => SameState(EffState(t), st))).DefaultIfEmpty(0).Max();
            foreach (var st in states)
            {
                var c = summarySet.Count(t => SameState(EffState(t), st));
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                var lbl = new TextBlock { Text = st, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
                Grid.SetColumn(lbl, 0);
                var bar = new Border
                {
                    Height = 12, HorizontalAlignment = HorizontalAlignment.Left,
                    Background = StateBrush(st), CornerRadius = new CornerRadius(2),
                    Width = maxCount > 0 ? Math.Max(2, 300.0 * c / maxCount) : 2
                };
                Grid.SetColumn(bar, 1);
                var num = new TextBlock { Text = c.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(num, 2);
                row.Children.Add(lbl); row.Children.Add(bar); row.Children.Add(num);
                SummaryHost.Items.Add(row);
            }

            if (ViewCombo.SelectedIndex == 1)
            {
                // ── TaskBoard: Pessoa | Story | estados (agrupa por pessoa e story) ──
                RenderPersonBoard(allVisible, states);
            }
            else
            {
                // ── Por Story: Feature | Story | estados (agrupa as Stories por Feature/entrega) ──
                RenderStoryBoard(visibleByStory, states);
            }

            StatusText.Text = AppStrings.Get("Sprint_TaskCount",
                allVisible.Count.ToString(), _scheduleIds.Count == 0 ? "0"
                    : allVisible.Count(t => _scheduleIds.Contains(t.Id)).ToString());
            FilterSummary.Text = BuildFilterSummary();
        }

        // Linha-resumo dos filtros aplicados (mostrada ao abrir a tela e a cada Render).
        private string BuildFilterSummary()
        {
            var parts = new List<string>();
            if (_selectedPeople.Count == 1)
                parts.Add(AppStrings.Get("Sprint_FSPerson", _selectedPeople.First()));
            else if (_selectedPeople.Count > 1)
                parts.Add(AppStrings.Get("Sprint_FSPerson", AppStrings.Get("Sprint_PersonN", _selectedPeople.Count.ToString())));
            if (_selectedStoryIds.Count > 0)
                parts.Add(AppStrings.Get("Sprint_FSStories", _selectedStoryIds.Count.ToString()));
            if (_hiddenStates.Count > 0)
                parts.Add(AppStrings.Get("Sprint_FSHidden", _hiddenStates.Count.ToString()));
            parts.Add(_closedDays > 0
                ? AppStrings.Get("Sprint_FSClosedDays", _closedDays.ToString())
                : AppStrings.Get("Sprint_FSClosedAll"));
            if (OnlyScheduleCheck.IsChecked == true)
                parts.Add(AppStrings.Get("Sprint_FSOnlySchedule"));
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                parts.Add(AppStrings.Get("Sprint_FSSearch", SearchBox.Text.Trim()));
            return AppStrings.Get("Sprint_FSPrefix", string.Join(" · ", parts));
        }

        // Sub-visão "Por Task": lista plana das tasks (útil ao filtrar por pessoa), cada uma
        // com a conexão ↑ para a Story pai. Ordena por Story e depois por estado.
        private void RenderByTask(List<TfsImportService.SprintTaskCard> tasks)
        {
            var storyById = _board!.Stories.Where(s => s.Id > 0).ToDictionary(s => s.Id);
            string StoryTitle(int? pid) => pid is int p && storyById.TryGetValue(p, out var st)
                ? st.Title : AppStrings.Get("Sprint_NoStory");

            foreach (var t in tasks
                         .OrderBy(t => StoryTitle(t.ParentId), StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(t => t.State, StringComparer.CurrentCultureIgnoreCase))
            {
                var inSched = _scheduleIds.Contains(t.Id);
                var border = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(inSched ? Color.FromRgb(0x2B, 0x57, 0x9A) : Color.FromRgb(0xD0, 0xD7, 0xE0)),
                    BorderThickness = new Thickness(inSched ? 2 : 1),
                    CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(8, 5, 8, 5)
                };
                var sp = new StackPanel();

                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock { Text = "📋 ", VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(new TextBlock { Text = EffTitle(t.Id, t.Title), FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                    Foreground = _titlePending.ContainsKey(t.Id) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Black });
                head.Children.Add(new Border
                {
                    Background = StateBrush(t.State), CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 1, 6, 1),
                    Child = new TextBlock { Text = t.State, Foreground = Brushes.White, FontSize = 10 }
                });
                sp.Children.Add(head);

                var meta = "#" + t.Id
                    + (string.IsNullOrWhiteSpace(t.AssignedTo) ? "" : "  ·  " + t.AssignedTo)
                    + (string.IsNullOrWhiteSpace(t.Effort) ? "" : "  ·  " + t.Effort + "h");
                sp.Children.Add(new TextBlock { Text = meta, Foreground = Brushes.Gray, FontSize = 11 });

                // Conexão ↑ para a Story pai (clicável: abre a Story no DevOps).
                var up = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                up.Children.Add(new TextBlock { Text = "↑ " + AppStrings.Get("Sprint_UpStory") + " ",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)), VerticalAlignment = VerticalAlignment.Center });
                if (t.ParentId is int pid)
                {
                    var link = new Button
                    {
                        Content = StoryTitle(t.ParentId),
                        Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)),
                        Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                        Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0),
                        HorizontalContentAlignment = HorizontalAlignment.Left
                    };
                    link.Click += (_, _) => OpenInDevOps(pid);
                    up.Children.Add(link);
                    if (_scheduleIds.Contains(pid) && _openInSchedule != null)
                    {
                        var s = new Button { Content = "📅", FontSize = 11, Padding = new Thickness(5, 0, 5, 0),
                            Margin = new Thickness(6, 0, 0, 0), ToolTip = AppStrings.Get("Query_OpenInSchedule") };
                        s.Click += (_, _) => _openInSchedule!(pid);
                        up.Children.Add(s);
                    }
                }
                else
                {
                    up.Children.Add(new TextBlock { Text = AppStrings.Get("Sprint_NoStory"), Foreground = Brushes.Gray });
                }
                sp.Children.Add(up);

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Cursor = System.Windows.Input.Cursors.Arrow };
                var open = new Button { Content = "DevOps", FontSize = 10, Padding = new Thickness(5, 0, 5, 0) };
                open.Click += (_, _) => OpenInDevOps(t.Id);
                actions.Children.Add(open);
                if (inSched && _openInSchedule != null)
                {
                    var sc = new Button { Content = "📅", FontSize = 11, Padding = new Thickness(5, 0, 5, 0),
                        Margin = new Thickness(4, 0, 0, 0), ToolTip = AppStrings.Get("Query_OpenInSchedule") };
                    sc.Click += (_, _) => _openInSchedule!(t.Id);
                    actions.Children.Add(sc);
                }
                sp.Children.Add(actions);

                border.Child = sp;
                BoardHost.Children.Add(border);
            }
        }

        // Uma linha do board: coluna 0 = Story; demais = estados com cards.
        private FrameworkElement BuildBoardRow(TfsImportService.SprintStoryRow? story,
            List<TfsImportService.SprintTaskCard>? tasks, List<string> states, bool header,
            string firstColHeaderKey = "Sprint_ColStory", bool showStory = false)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, header ? 2 : 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            foreach (var _ in states)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });

            // Coluna 0
            if (header)
            {
                var h0 = new TextBlock { Text = AppStrings.Get(firstColHeaderKey), FontWeight = FontWeights.Bold, FontSize = 12,
                    Margin = new Thickness(4, 2, 4, 2) };
                Grid.SetColumn(h0, 0); grid.Children.Add(h0);
                for (int i = 0; i < states.Count; i++)
                {
                    var hs = new Border { Background = StateBrush(states[i]), CornerRadius = new CornerRadius(3),
                        Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(6, 2, 6, 2) };
                    hs.Child = new TextBlock { Text = states[i], Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 11 };
                    Grid.SetColumn(hs, i + 1); grid.Children.Add(hs);
                }
                return grid;
            }

            var storyBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF7)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xE0)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3), Margin = new Thickness(3), Padding = new Thickness(6) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = EffTitle(story!.Id, story.Title), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Foreground = _titlePending.ContainsKey(story.Id) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Black });
            if (story.Id > 0)
            {
                sp.Children.Add(new TextBlock { Text = $"#{story.Id}  ·  {story.State}", FontSize = 10, Foreground = Brushes.Gray });
                var sowner = EffOwner(story.Id, story.AssignedTo);
                sp.Children.Add(new TextBlock {
                    Text = "👤 " + (string.IsNullOrWhiteSpace(sowner) ? AppStrings.Get("Sprint_NoOwner") : sowner),
                    FontSize = 10, TextWrapping = TextWrapping.Wrap,
                    Foreground = _ownerPending.ContainsKey(story.Id) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.DimGray });
                var stActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Cursor = System.Windows.Input.Cursors.Arrow };
                AddEditButtons(stActions, story.Id, story.Title, story.AssignedTo, "Story", story.IterationPath); // ✎/💬 da Story
                sp.Children.Add(stActions);
            }
            storyBorder.Child = sp;
            Grid.SetColumn(storyBorder, 0); grid.Children.Add(storyBorder);

            for (int i = 0; i < states.Count; i++)
            {
                var el = BuildStateCell(states[i], tasks!, showStory);
                Grid.SetColumn(el, i + 1); grid.Children.Add(el);
            }
            return grid;
        }

        // Célula de um estado: pilha de cards (ordenada); no modo edição vira alvo de soltura.
        private FrameworkElement BuildStateCell(string state, IEnumerable<TfsImportService.SprintTaskCard> tasks, bool showStory)
        {
            var cell = new StackPanel { Margin = new Thickness(2) };
            foreach (var t in tasks.Where(t => SameState(EffState(t), state))
                         .OrderBy(t => EffPrio(t) > 0 ? EffPrio(t) : 99)  // grupo de prioridade primeiro
                         .ThenBy(EffTaskRank))                              // depois o rank (StackRank) dentro do grupo
                cell.Children.Add(BuildCard(t, showStory
                    ? (t.ParentId is int pid && _storyById.TryGetValue(pid, out var st) ? st.Title : null)
                    : null));
            if (EditModeCheck.IsChecked != true) return cell;
            var host = new Border
            {
                MinHeight = 44, Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF8, 0xFB)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE7, 0xEF)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Margin = new Thickness(1), Padding = new Thickness(2), AllowDrop = true, Tag = state, Child = cell
            };
            host.Drop += OnCardDrop;
            host.DragOver += (s, ev) => { ev.Effects = DragDropEffects.Move; ev.Handled = true; };
            return host;
        }

        // TaskBoard por Pessoa com coluna de Story: Pessoa | Story | estados. Agrupa os cards
        // por (pessoa → story) para visualizar melhor.
        private void RenderPersonBoard(List<TfsImportService.SprintTaskCard> allVisible, List<string> states)
        {
            var cols = new List<GridLength> { new(170), new(220) };
            foreach (var _ in states) cols.Add(new GridLength(210));

            // Cabeçalho.
            var head = MakeRowGrid(cols);
            AddCell(head, 0, MakeHeader(AppStrings.Get("Sprint_ColPerson")));
            AddCell(head, 1, MakeHeader(AppStrings.Get("Sprint_ColStory")));
            for (int i = 0; i < states.Count; i++) AddCell(head, i + 2, MakeStateHeader(states[i]));
            BoardHost.Children.Add(head);

            foreach (var pg in allVisible
                         .GroupBy(t => string.IsNullOrWhiteSpace(t.AssignedTo) ? AppStrings.Get("Sprint_NoOwner") : t.AssignedTo)
                         .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var firstRow = true;
                // Ordena as Stories da pessoa por StackRank (permite reordenar por arraste); título desempata.
                int GroupStoryId(IGrouping<string, TfsImportService.SprintTaskCard> g) =>
                    g.FirstOrDefault(t => t.ParentId is int)?.ParentId ?? 0;
                foreach (var sg in pg
                             .GroupBy(t => t.ParentId is int pid && _storyById.TryGetValue(pid, out var st) ? st.Title : AppStrings.Get("Sprint_NoStory"))
                             .OrderBy(g => StoryRankOf(GroupStoryId(g)))
                             .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
                {
                    var row = MakeRowGrid(cols);
                    if (firstRow)
                    {
                        var personCell = new TextBlock { Text = pg.Key, FontWeight = FontWeights.Bold,
                            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 2) };
                        if (EditModeCheck.IsChecked == true)
                        {
                            var personKey0 = pg.Key;
                            personCell.AllowDrop = true;
                            personCell.Drop += (s, ev) => OnStoryOwnerDrop(ev, personKey0);
                            personCell.DragOver += (s, ev) => { ev.Effects = DragDropEffects.Move; ev.Handled = true; };
                        }
                        AddCell(row, 0, personCell);
                    }
                    var tks = sg.ToList();
                    var storySp = new StackPanel();
                    var storyId = tks.FirstOrDefault(t => t.ParentId is int)?.ParentId ?? 0;
                    var sOwnerOrig = storyId > 0 ? (StoryById(storyId)?.AssignedTo ?? string.Empty) : string.Empty;
                    var sOwner = EffOwner(storyId, sOwnerOrig);
                    // "Ajudante": a pessoa desta faixa tem task na Story mas NÃO é a responsável dela.
                    var isHelper = storyId > 0 && !string.IsNullOrWhiteSpace(sOwner)
                        && !string.Equals(sOwner.Trim(), pg.Key.Trim(), StringComparison.OrdinalIgnoreCase);
                    storySp.Children.Add(new TextBlock { Text = EffTitle(storyId, sg.Key), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                        Foreground = _titlePending.ContainsKey(storyId) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Black });
                    if (storyId > 0)
                    {
                        // Estado da Story (#id · estado). Laranja quando há mudança de estado pendente.
                        var stRow = StoryById(storyId);
                        var stState = stRow != null ? EffStoryState(stRow) : "";
                        storySp.Children.Add(new TextBlock { Text = $"#{storyId}  ·  {stState}", FontSize = 10,
                            Foreground = _storyStatePending.ContainsKey(storyId) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Gray });
                        storySp.Children.Add(new TextBlock {
                            Text = "👤 " + (string.IsNullOrWhiteSpace(sOwner) ? AppStrings.Get("Sprint_NoOwner") : sOwner),
                            FontSize = 10, TextWrapping = TextWrapping.Wrap,
                            Foreground = _ownerPending.ContainsKey(storyId) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.DimGray });
                        if (isHelper)
                            storySp.Children.Add(new TextBlock { Text = AppStrings.Get("Sprint_Helping"),
                                FontSize = 10, FontStyle = FontStyles.Italic, Foreground = new SolidColorBrush(Color.FromRgb(0xB2, 0x6A, 0x00)) });
                        // Sprint da Story quando há mais de uma sprint no board.
                        var sIter = IterLeaf(EffIter(storyId, StoryById(storyId)?.IterationPath ?? ""));
                        if (_sprintPaths.Count != 1 && !string.IsNullOrEmpty(sIter))
                            storySp.Children.Add(new TextBlock { Text = "🗓 " + sIter, FontSize = 10, TextWrapping = TextWrapping.Wrap,
                                Foreground = _iterPending.ContainsKey(storyId) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.DimGray });
                        var stActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Cursor = System.Windows.Input.Cursors.Arrow };
                        AddEditButtons(stActions, storyId, sg.Key, sOwnerOrig, "Story", StoryById(storyId)?.IterationPath ?? "");
                        var addTask = new Button { Content = AppStrings.Get("Sprint_AddTask"), FontSize = 10, Padding = new Thickness(5, 0, 5, 0) };
                        var personKey = pg.Key;
                        addTask.Click += (_, _) => AddNewTask(storyId, personKey); // já nasce na faixa da pessoa
                        stActions.Children.Add(addTask);
                        storySp.Children.Add(stActions);
                    }
                    // Destaque laranja quando a Story tem alteração pendente (rank/responsável/nome).
                    var storyPend = storyId > 0 && (_storyRankPending.Contains(storyId) || _ownerPending.ContainsKey(storyId) || _titlePending.ContainsKey(storyId));
                    // Colaborador: cor customizável (paleta "Story Colaborador"); dono: azul-claro normal.
                    var storyBorder = new Border {
                        Background = isHelper ? StateBrush(HelperColorKey) : new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF7)),
                        BorderBrush = new SolidColorBrush(storyPend ? Color.FromRgb(0xE0, 0x8A, 0x00) : isHelper ? Color.FromRgb(0xE6, 0xC9, 0x8A) : Color.FromRgb(0xD0, 0xD7, 0xE0)),
                        BorderThickness = new Thickness(storyPend ? 2 : 1),
                        CornerRadius = new CornerRadius(3), Margin = new Thickness(3), Padding = new Thickness(6),
                        Child = storySp, Tag = storyId };
                    // Modo edição: arrastar a Story p/ outra pessoa troca o Responsável — só quando esta
                    // pessoa É a responsável (ajudante não move a Story de outro).
                    if (EditModeCheck.IsChecked == true && storyId > 0 && !isHelper)
                    {
                        var personKey2 = pg.Key;
                        storyBorder.Cursor = System.Windows.Input.Cursors.SizeAll;
                        storyBorder.PreviewMouseMove += (s, ev) =>
                        {
                            if (ev.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
                            if (IsInteractive(ev.OriginalSource as DependencyObject)) return;
                            DragDrop.DoDragDrop(storyBorder, "STORYOWNER:" + storyId, DragDropEffects.Move);
                        };
                        storyBorder.AllowDrop = true;
                        storyBorder.Drop += (s, ev) => OnStoryOwnerDrop(ev, personKey2, storyId);
                        storyBorder.DragOver += (s, ev) => { ev.Effects = DragDropEffects.Move; ev.Handled = true; };
                    }
                    AddCell(row, 1, storyBorder);
                    for (int i = 0; i < states.Count; i++)
                        AddCell(row, i + 2, BuildStateCell(states[i], tks, showStory: false));
                    BoardHost.Children.Add(row);
                    firstRow = false;
                }
                // separador entre pessoas
                BoardHost.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xD5, 0xDD, 0xD7)), Margin = new Thickness(0, 2, 0, 4) });
            }
        }

        // Soltou uma Story na visão Pessoa & Task:
        //  • na MESMA pessoa, sobre outra Story → reordena (troca o StackRank);
        //  • em OUTRA pessoa → troca o Responsável da Story.
        private void OnStoryOwnerDrop(DragEventArgs e, string targetPerson, int targetStoryId = 0)
        {
            if (!e.Data.GetDataPresent(DataFormats.StringFormat)) return;
            var payload = e.Data.GetData(DataFormats.StringFormat) as string;
            if (payload == null || !payload.StartsWith("STORYOWNER:")) return;
            if (!int.TryParse(payload.Substring("STORYOWNER:".Length), out var id) || id <= 0) return;
            var story = StoryById(id);
            if (story == null) return;
            var owner = EffOwner(id, story.AssignedTo);
            var samegroup = string.Equals((targetPerson ?? "").Trim(), (owner ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            if (samegroup)
            {
                // Mesma pessoa: reordena trocando o rank com a Story-alvo.
                if (targetStoryId > 0 && targetStoryId != id)
                {
                    var a = StoryRankOf(id); var b = StoryRankOf(targetStoryId);
                    _storyRank[id] = b; _storyRank[targetStoryId] = a;
                    _storyRankPending.Add(id); _storyRankPending.Add(targetStoryId);
                }
            }
            else
            {
                var baseline = _ownerApplied.TryGetValue(id, out var ap) ? ap : story.AssignedTo;
                if (string.Equals((targetPerson ?? "").Trim(), (baseline ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    _ownerPending.Remove(id);
                else
                    _ownerPending[id] = targetPerson ?? string.Empty;
            }
            UpdatePendingButton();
            Render();
        }

        // StoryBoard: a STORY é o card, posicionada na coluna do seu estado, agrupada por Feature.
        private void RenderStoryBoard(List<(TfsImportService.SprintStoryRow Story, List<TfsImportService.SprintTaskCard> Tasks)> visibleByStory,
            List<string> states)
        {
            var stories = EffectiveStories().Select(x => x.Story).Where(StoryPasses).ToList();
            var storyStates = TfsImportService.OrderTaskboardStates(states.Concat(stories.Select(EffStoryState)));
            if (storyStates.Count == 0) storyStates = states;

            var cols = new List<GridLength> { new(180) };
            foreach (var _ in storyStates) cols.Add(new GridLength(240));

            var head = MakeRowGrid(cols);
            AddCell(head, 0, MakeHeader(AppStrings.Get("Sprint_ColFeature")));
            for (int i = 0; i < storyStates.Count; i++) AddCell(head, i + 1, MakeStateHeader(storyStates[i]));
            BoardHost.Children.Add(head);

            foreach (var fg in stories
                         .GroupBy(s => string.IsNullOrWhiteSpace(s.FeatureTitle) ? AppStrings.Get("Sprint_NoFeature") : s.FeatureTitle)
                         .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var featureId = fg.Select(s => s.FeatureId).FirstOrDefault(id => id > 0);
                var groupStories = fg.OrderBy(s => StoryRankOf(s.Id))
                    .ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
                var realIds = groupStories.Where(s => s.Id > 0).Select(s => s.Id).ToList();

                var row = MakeRowGrid(cols);
                var featPanel = new StackPanel();
                featPanel.Children.Add(new TextBlock { Text = fg.Key, FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 2) });
                if (featureId > 0)
                {
                    var addStory = new Button { Content = AppStrings.Get("Sprint_AddStory"), FontSize = 10,
                        Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(4, 2, 4, 2), HorizontalAlignment = HorizontalAlignment.Left };
                    addStory.Click += (_, _) => AddNewStory(featureId, fg.Key);
                    featPanel.Children.Add(addStory);
                }
                AddCell(row, 0, featPanel);

                for (int i = 0; i < storyStates.Count; i++)
                {
                    var st = storyStates[i];
                    var cell = new StackPanel { Margin = new Thickness(2) };
                    foreach (var s in groupStories.Where(s => SameState(EffStoryState(s), st)))
                        cell.Children.Add(BuildStoryCard(s, realIds));
                    if (EditModeCheck.IsChecked == true)
                    {
                        var host = new Border
                        {
                            MinHeight = 44, Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF8, 0xFB)),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE7, 0xEF)), BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(3), Margin = new Thickness(1), Padding = new Thickness(2),
                            AllowDrop = true, Tag = st, Child = cell
                        };
                        host.Drop += OnStoryDrop;
                        host.DragOver += (s, ev) => { ev.Effects = DragDropEffects.Move; ev.Handled = true; };
                        AddCell(row, i + 1, host);
                    }
                    else AddCell(row, i + 1, cell);
                }
                BoardHost.Children.Add(row);
                BoardHost.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xD5, 0xDD, 0xD7)), Margin = new Thickness(0, 2, 0, 4) });
            }
        }

        private string EffStoryState(TfsImportService.SprintStoryRow s) =>
            _storyStatePending.TryGetValue(s.Id, out var p) ? p
            : _storyStateApplied.TryGetValue(s.Id, out var a) ? a : s.State;

        // Responsável efetivo de um work item (pendente > aplicado > valor original).
        private string EffOwner(int id, string original) =>
            _ownerPending.TryGetValue(id, out var p) ? p
            : _ownerApplied.TryGetValue(id, out var a) ? a : original;

        // Tags/estado de bloqueio efetivos (pendente > tags aplicadas > tags originais).
        private string EffTags(int id, string original) =>
            _tagsApplied.TryGetValue(id, out var a) ? a : (original ?? "");
        private bool EffBlocked(int id, string originalTags) =>
            _blockPending.TryGetValue(id, out var b) ? b : HasTag(EffTags(id, originalTags), BlockedTag);
        // Alterna o bloqueio (pendente) comparando com a baseline (tags gravadas/originais).
        private void ToggleBlockPending(int id, string originalTags)
        {
            var baseBlocked = HasTag(EffTags(id, originalTags), BlockedTag);
            var target = !EffBlocked(id, originalTags);
            if (target == baseBlocked) _blockPending.Remove(id); else _blockPending[id] = target;
            UpdatePendingButton(); Render();
        }

        // Iteração (sprint) efetiva da Story (pendente > aplicado > valor original).
        private string EffIter(int id, string original) =>
            _iterPending.TryGetValue(id, out var p) ? p
            : _iterApplied.TryGetValue(id, out var a) ? a : original;

        // Último segmento do IterationPath (nome curto da sprint).
        private static string IterLeaf(string path) =>
            string.IsNullOrEmpty(path) ? "" : path.Split('\\', '/').LastOrDefault() ?? path;

        // Nome/título efetivo de um work item (pendente > aplicado > valor original).
        private string EffTitle(int id, string original) =>
            id <= 0 ? original
            : _titlePending.TryGetValue(id, out var p) ? p
            : _titleApplied.TryGetValue(id, out var a) ? a : original;

        private bool StoryPasses(TfsImportService.SprintStoryRow s)
        {
            if (s.Id < 0) return true; // Story nova sempre visível
            if (_selectedStoryIds.Count > 0 && !_selectedStoryIds.Contains(s.Id)) return false;
            // Filtro de pessoa também na visão Por Story: mostra a Story se o responsável dela
            // ou alguma task sua for de uma das pessoas selecionadas.
            if (_selectedPeople.Count > 0)
            {
                var ownerMatch = !string.IsNullOrWhiteSpace(EffOwner(s.Id, s.AssignedTo))
                                 && _selectedPeople.Contains(EffOwner(s.Id, s.AssignedTo));
                var taskMatch = s.Tasks.Any(t => _selectedPeople.Contains(t.AssignedTo ?? ""));
                if (!ownerMatch && !taskMatch) return false;
            }
            var q = SearchBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                var scope = SearchScope();
                bool storyMatch = s.Title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || s.Id.ToString().Contains(q);
                bool taskMatch = s.Tasks.Any(t => t.Title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || (!string.IsNullOrEmpty(t.AssignedTo) && t.AssignedTo.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0));
                return scope switch { 1 => taskMatch, 2 => storyMatch, _ => storyMatch || taskMatch };
            }
            return true;
        }

        private TfsImportService.SprintStoryRow? StoryById(int id) =>
            EffectiveStories().Select(x => x.Story).FirstOrDefault(s => s.Id == id);

        // Card NOVO editável no próprio card: Nome, Responsável, HH Estimado e Descrição.
        private Border BuildNewCardBorder(NewCard nc)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE7, 0xF6, 0xE7)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)), BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(6)
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = "🆕 " + AppStrings.Get(nc.Type == "Story" ? "Sprint_NewStory" : "Sprint_NewTask"),
                FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)), FontSize = 11
            });

            sp.Children.Add(new TextBlock { Text = AppStrings.Get("Sprint_FldName"), FontSize = 10, Foreground = Brushes.Gray });
            var name = new TextBox { Text = nc.Title, FontSize = 12, Margin = new Thickness(0, 0, 0, 3) };
            name.TextChanged += (_, _) => nc.Title = name.Text;
            sp.Children.Add(name);

            sp.Children.Add(new TextBlock { Text = AppStrings.Get("Sprint_FldOwner"), FontSize = 10, Foreground = Brushes.Gray });
            var person = new ComboBox { IsEditable = true, FontSize = 11, Margin = new Thickness(0, 0, 0, 3), Text = nc.AssignedTo };
            if (_board != null) foreach (var p in _board.People) person.Items.Add(p);
            // Trocar a pessoa move o card para a faixa dela. O re-render é ADIADO (Dispatcher) para
            // não reconstruir a árvore no meio do evento do ComboBox — senão a 1ª troca não reagrupa.
            void RegroupDeferred() => Dispatcher.BeginInvoke(new Action(Render), System.Windows.Threading.DispatcherPriority.Background);
            person.SelectionChanged += (_, _) =>
            {
                var chosen = person.SelectedItem as string;
                if (string.IsNullOrEmpty(chosen) || string.Equals(chosen, nc.AssignedTo, StringComparison.CurrentCultureIgnoreCase)) return;
                nc.AssignedTo = chosen; UpdatePendingButton(); RegroupDeferred();
            };
            person.LostFocus += (_, _) => { if (nc.AssignedTo != person.Text) { nc.AssignedTo = person.Text; RegroupDeferred(); } };
            sp.Children.Add(person);

            var hhRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            hhRow.Children.Add(new TextBlock { Text = AppStrings.Get("Sprint_FldHH"), FontSize = 10, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            var hh = new TextBox { Width = 56, FontSize = 11, Text = nc.Effort?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) ?? "" };
            hh.TextChanged += (_, _) => nc.Effort = double.TryParse(hh.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out var v) ? v : (double?)null;
            hhRow.Children.Add(hh);
            sp.Children.Add(hhRow);

            // Sprint-alvo: só aparece quando há mais de uma opção (várias sprints/"Todas" selecionadas).
            var sprintOpts = NewCardSprintOptions();
            if (sprintOpts.Count > 1)
            {
                if (string.IsNullOrEmpty(nc.IterationPath) || sprintOpts.All(s => s.Path != nc.IterationPath))
                    nc.IterationPath = DefaultNewIterationPath();
                sp.Children.Add(new TextBlock { Text = AppStrings.Get("Sprint_FldSprint"), FontSize = 10, Foreground = Brushes.Gray });
                var spCombo = new ComboBox { FontSize = 11, Margin = new Thickness(0, 0, 0, 3),
                    ItemsSource = sprintOpts, DisplayMemberPath = nameof(TfsImportService.SprintInfo.Name) };
                spCombo.SelectedItem = sprintOpts.FirstOrDefault(s => s.Path == nc.IterationPath) ?? sprintOpts[0];
                spCombo.SelectionChanged += (_, _) => { if (spCombo.SelectedItem is TfsImportService.SprintInfo si) nc.IterationPath = si.Path; };
                sp.Children.Add(spCombo);
            }

            sp.Children.Add(new TextBlock { Text = AppStrings.Get("Sprint_FldDesc"), FontSize = 10, Foreground = Brushes.Gray });
            var desc = new TextBox { Text = nc.Description, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 46, FontSize = 11, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            desc.TextChanged += (_, _) => nc.Description = desc.Text;
            sp.Children.Add(desc);

            var rm = new Button { Content = AppStrings.Get("Sprint_RemoveNew"), FontSize = 10, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            rm.Click += (_, _) => { _newCards.Remove(nc); UpdatePendingButton(); Render(); };
            sp.Children.Add(rm);

            border.Child = sp;
            return border;
        }

        private Border BuildStoryCard(TfsImportService.SprintStoryRow story, List<int> realIds)
        {
            if (story.Id < 0 && _newCards.FirstOrDefault(n => n.TempId == story.Id) is { } ncs)
                return BuildNewCardBorder(ncs);
            var isNew = story.Id < 0;
            var pend = _storyStatePending.ContainsKey(story.Id) || _storyRankPending.Contains(story.Id)
                || _ownerPending.ContainsKey(story.Id) || _titlePending.ContainsKey(story.Id) || _iterPending.ContainsKey(story.Id)
                || _blockPending.ContainsKey(story.Id) || _featurePending.ContainsKey(story.Id);
            var storyBlocked = story.Id > 0 && EffBlocked(story.Id, story.Tags);
            var storyToDelete = _deletePending.Contains(story.Id);
            var border = new Border
            {
                Background = storyToDelete ? new SolidColorBrush(Color.FromRgb(0xFB, 0xE3, 0xE3))
                    : isNew ? new SolidColorBrush(Color.FromRgb(0xE7, 0xF6, 0xE7)) : pend ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xD6)) : StateTintBrush(EffStoryState(story)),
                BorderBrush = new SolidColorBrush(storyToDelete ? Color.FromRgb(0xC0, 0x30, 0x30) : isNew ? Color.FromRgb(0x10, 0x7C, 0x10) : pend ? Color.FromRgb(0xE0, 0x8A, 0x00) : Color.FromRgb(0xD0, 0xD7, 0xE0)),
                BorderThickness = new Thickness(storyToDelete || isNew || pend ? 2 : 1),
                CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(6), Tag = story.Id
            };
            if (storyBlocked && !pend && !storyToDelete) { border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)); border.BorderThickness = new Thickness(2); }
            var sp = new StackPanel();
            var storyTitleTb = new TextBlock { Text = (isNew ? "🆕 " : "") + EffTitle(story.Id, story.Title), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Foreground = storyToDelete ? new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)) : _titlePending.ContainsKey(story.Id) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Black };
            if (storyToDelete) storyTitleTb.TextDecorations = TextDecorations.Strikethrough;
            sp.Children.Add(storyTitleTb);
            if (story.Id > 0)
            {
                sp.Children.Add(new TextBlock { Text = $"#{story.Id}  ·  {EffStoryState(story)}", FontSize = 10, Foreground = Brushes.Gray });
                // Responsável da Story (👤). Fica laranja quando há troca pendente.
                var owner = EffOwner(story.Id, story.AssignedTo);
                var ownerDirty = _ownerPending.ContainsKey(story.Id);
                sp.Children.Add(new TextBlock {
                    Text = "👤 " + (string.IsNullOrWhiteSpace(owner) ? AppStrings.Get("Sprint_NoOwner") : owner),
                    FontSize = 10, TextWrapping = TextWrapping.Wrap,
                    Foreground = ownerDirty ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.DimGray });
                // Sprint da Story (🗓). Laranja quando há troca pendente.
                var iterLeaf = IterLeaf(EffIter(story.Id, story.IterationPath));
                if (!string.IsNullOrEmpty(iterLeaf))
                    sp.Children.Add(new TextBlock { Text = "🗓 " + iterLeaf, FontSize = 10, TextWrapping = TextWrapping.Wrap,
                        Foreground = _iterPending.ContainsKey(story.Id) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.DimGray });
                // StoryBoard = nível Story: aqui NÃO cria Task (isso é na visão Pessoa & Task).
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Cursor = System.Windows.Input.Cursors.Arrow };
                AddEditButtons(actions, story.Id, story.Title, story.AssignedTo, "Story", story.IterationPath);
                var openStory = new Button { Content = "🔗", FontSize = 11, Padding = new Thickness(5, 0, 5, 0),
                    Margin = new Thickness(0, 0, 4, 0), ToolTip = AppStrings.Get("Sprint_OpenDevOps") };
                openStory.Click += (_, _) => OpenInDevOps(story.Id);
                actions.Children.Add(openStory);
                // Excluir Story: só quando está em New e SEM Tasks (ou todas as Tasks em New).
                var tasksAllNew = story.Tasks.Count == 0 || story.Tasks.All(t => SameState(EffState(t), "New"));
                if (string.Equals(EffStoryState(story), "New", StringComparison.OrdinalIgnoreCase) && tasksAllNew)
                {
                    var delSt = new Button { Content = storyToDelete ? "↩" : "🗑", FontSize = 11, Padding = new Thickness(5, 0, 5, 0),
                        Margin = new Thickness(0, 0, 4, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)),
                        ToolTip = AppStrings.Get(storyToDelete ? "Sprint_UndoDelete" : "Sprint_DeleteTask") };
                    delSt.Click += (_, _) =>
                    {
                        if (!_deletePending.Remove(story.Id)) _deletePending.Add(story.Id);
                        UpdatePendingButton(); Render();
                    };
                    actions.Children.Add(delSt);
                }
                sp.Children.Add(actions);
            }
            else
                sp.Children.Add(new TextBlock { Text = AppStrings.Get("Sprint_New"), FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)) });
            border.Child = sp;

            // Arrastar a Story entre colunas muda o ESTADO da Story (só existentes, no modo edição).
            if (EditModeCheck.IsChecked == true && story.Id > 0)
            {
                border.Cursor = System.Windows.Input.Cursors.SizeAll;
                border.PreviewMouseMove += (s, ev) =>
                {
                    if (ev.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
                    if (IsInteractive(ev.OriginalSource as DependencyObject)) return;
                    DragDrop.DoDragDrop(border, story.Id, DragDropEffects.Move);
                };
            }
            return border;
        }

        // Arrasta a caixa da Story: na MESMA coluna reordena o rank (StackRank); em OUTRA muda o estado.
        private void OnStoryDrop(object sender, DragEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string newState) return;
            if (!e.Data.GetDataPresent(typeof(int))) return;
            var id = (int)e.Data.GetData(typeof(int))!;
            var story = StoryById(id);
            if (story == null || id <= 0) return;
            var cell = (fe as Border)?.Child as StackPanel;

            if (SameState(EffStoryState(story), newState))
            {
                // Mesma coluna → reordena o rank pela posição solta (entre as Stories da coluna).
                if (cell != null) ReorderStoryRankInCell(cell, id, e.GetPosition(cell).Y);
            }
            else
            {
                var baseline = _storyStateApplied.TryGetValue(id, out var a) ? a : story.State;
                if (SameState(baseline, newState)) _storyStatePending.Remove(id);
                else _storyStatePending[id] = newState;
            }
            UpdatePendingButton();
            Render();
        }

        private void ReorderStoryRankInCell(StackPanel cell, int id, double y)
        {
            var others = cell.Children.OfType<Border>()
                .Where(b => b.Tag is int bid && bid != id && bid > 0)
                .Select(b => (Rank: StoryRankOf((int)b.Tag!),
                              Center: b.TranslatePoint(new Point(0, b.ActualHeight / 2), cell).Y))
                .OrderBy(x => x.Center).ToList();
            if (others.Count == 0) return;
            var insertAt = others.Count(x => x.Center < y);
            double newRank;
            if (insertAt == 0) newRank = others[0].Rank - 1;
            else if (insertAt >= others.Count) newRank = others[^1].Rank + 1;
            else newRank = (others[insertAt - 1].Rank + others[insertAt].Rank) / 2.0;
            _storyRank[id] = newRank;
            _storyRankPending.Add(id);
        }

        private double StoryRankOf(int id) => _storyRank.TryGetValue(id, out var r) ? r : double.MaxValue;

        private double EffTaskRank(TfsImportService.SprintTaskCard t) =>
            _taskRank.TryGetValue(t.Id, out var r) ? r
            : _order.TryGetValue(t.Id, out var o) ? o : t.Id;

        // Move a Story trocando o StackRank com a vizinha do grupo (▲/▼). Só reordena dentro do grupo.
        private void MoveStoryRank(List<int> orderedIds, int id, int dir)
        {
            var idx = orderedIds.IndexOf(id);
            var nb = idx + dir;
            if (idx < 0 || nb < 0 || nb >= orderedIds.Count) return;
            var other = orderedIds[nb];
            var a = StoryRankOf(id);
            var b = StoryRankOf(other);
            _storyRank[id] = b;
            _storyRank[other] = a;
            _storyRankPending.Add(id);
            _storyRankPending.Add(other);
            UpdatePendingButton();
            Render();
        }

        private static Grid MakeRowGrid(List<GridLength> cols)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            foreach (var c in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
            return g;
        }
        private static void AddCell(Grid g, int col, UIElement el) { Grid.SetColumn(el, col); g.Children.Add(el); }
        private static TextBlock MakeHeader(string text) => new()
        { Text = text, FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(4, 2, 4, 2) };
        private Border MakeStateHeader(string state) => new()
        {
            Background = StateBrush(state), CornerRadius = new CornerRadius(3),
            Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(6, 2, 6, 2),
            Child = new TextBlock { Text = state, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 11 }
        };

        private Border BuildCard(TfsImportService.SprintTaskCard t, string? storyTitle = null)
        {
            if (t.Id < 0 && _newCards.FirstOrDefault(n => n.TempId == t.Id) is { } nct)
                return BuildNewCardBorder(nct);
            var inSched = _scheduleIds.Contains(t.Id);
            var isPending = _pending.ContainsKey(t.Id) || _taskRankPending.Contains(t.Id);
            var isNew = t.Id < 0; // card novo (sem ID do TFS) → destaque verde até salvar
            var toDelete = _deletePending.Contains(t.Id); // marcada p/ excluir no Salvar TFS
            var border = new Border
            {
                Background = toDelete ? new SolidColorBrush(Color.FromRgb(0xFB, 0xE3, 0xE3))
                    : isNew ? new SolidColorBrush(Color.FromRgb(0xE7, 0xF6, 0xE7))
                    : isPending ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xD6)) : StateTintBrush(EffState(t)),
                BorderBrush = new SolidColorBrush(toDelete ? Color.FromRgb(0xC0, 0x30, 0x30)
                    : isNew ? Color.FromRgb(0x10, 0x7C, 0x10)
                    : isPending ? Color.FromRgb(0xE0, 0x8A, 0x00)
                    : inSched ? Color.FromRgb(0x2B, 0x57, 0x9A) : Color.FromRgb(0xCF, 0xD8, 0xE3)),
                BorderThickness = new Thickness(toDelete || isNew || isPending || inSched ? 2 : 1),
                CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(6),
                Tag = t.Id
            };
            // Bloqueada: apenas borda vermelha (sem chip escuro), exceto se marcada p/ excluir.
            var blocked = !isNew && EffBlocked(t.Id, t.Tags);
            if (blocked && !toDelete)
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30));
                border.BorderThickness = new Thickness(2);
            }
            var sp = new StackPanel();
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
            if (isNew)
                titleLine.Children.Add(new TextBlock { Text = "🆕 ", VerticalAlignment = VerticalAlignment.Center });
            // Prioridade EDITÁVEL via ComboBox (picklist P{min}..P{max}, igual ao TFS). A mudança
            // entra na fila do Salvar TFS. (Só para Task existente.)
            if (!isNew)
            {
                var eff = EffPrio(t);
                var (pmin, pmax) = PriorityRange();
                var prioPend = _prioPending.ContainsKey(t.Id);
                var combo = new ComboBox
                {
                    Width = 52, FontSize = 10, FontWeight = FontWeights.Bold, Height = 20,
                    Cursor = System.Windows.Input.Cursors.Arrow,
                    Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
                    Background = eff > 0 ? PriorityBrush(eff) : new SolidColorBrush(Color.FromRgb(0xB0, 0xB8, 0xC0)),
                    BorderBrush = prioPend ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Gray,
                    BorderThickness = new Thickness(prioPend ? 2 : 1),
                    ToolTip = AppStrings.Get("Sprint_PriorityTip")
                };
                for (int v = pmin; v <= pmax; v++)
                    combo.Items.Add(new ComboBoxItem { Content = "P" + v, Tag = v });
                combo.SelectedIndex = eff >= pmin && eff <= pmax ? eff - pmin : -1;
                // handler só depois de definir o índice inicial (evita disparar na montagem)
                combo.SelectionChanged += (s, _) =>
                {
                    if (combo.SelectedItem is not ComboBoxItem ci || ci.Tag is not int vv) return;
                    if (vv == t.Priority) _prioPending.Remove(t.Id); else _prioPending[t.Id] = vv;
                    UpdatePendingButton();
                    Render();
                };
                titleLine.Children.Add(combo);
            }
            if (isPending)
                titleLine.Children.Add(new TextBlock { Text = "● ", Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)),
                    FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = AppStrings.Get("Sprint_UpdateTfs") });
            var titleTb = new TextBlock { Text = EffTitle(t.Id, t.Title), TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Foreground = toDelete ? new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30))
                    : _titlePending.ContainsKey(t.Id) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Black };
            if (toDelete) titleTb.TextDecorations = TextDecorations.Strikethrough; // marcada p/ excluir
            titleLine.Children.Add(titleTb);
            sp.Children.Add(titleLine);
            var line = new TextBlock { FontSize = 10, Foreground = Brushes.Gray };
            line.Text = (isNew ? AppStrings.Get("Sprint_New") : $"#{t.Id}")
                + (string.IsNullOrWhiteSpace(t.AssignedTo) ? "" : $"  ·  {t.AssignedTo}")
                + (string.IsNullOrWhiteSpace(t.Effort) ? "" : $"  ·  {t.Effort}h");
            sp.Children.Add(line);
            // Sprint da Task: mostra quando há mais de uma sprint no board (várias/"Todas").
            var tIter = IterLeaf(EffIter(t.Id, t.IterationPath));
            if (_sprintPaths.Count != 1 && !string.IsNullOrEmpty(tIter))
                sp.Children.Add(new TextBlock { Text = "🗓 " + tIter, FontSize = 10, TextWrapping = TextWrapping.Wrap,
                    Foreground = _iterPending.ContainsKey(t.Id) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.DimGray });
            if (!string.IsNullOrWhiteSpace(storyTitle))
                sp.Children.Add(new TextBlock
                {
                    Text = "📖 " + storyTitle, FontSize = 10, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)), Margin = new Thickness(0, 2, 0, 0)
                });

            if (isNew)
            {
                // Card novo: sem ID/ações do TFS ainda; será criado no Salvar TFS.
                border.Child = sp;
                return border;
            }

            // Ao mover para Closed (arrasto pendente), aparece um campo HH Realizado direto no card,
            // para não precisar abrir o editor. O valor entra na fila (CompletedWork).
            if (_pending.TryGetValue(t.Id, out var pendState) && IsClosedState(pendState))
            {
                var hhRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Arrow };
                hhRow.Children.Add(new TextBlock { Text = AppStrings.Get("Desc_DoneHours"), FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
                var hhBox = new TextBox { Width = 56, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                    Text = _donePending.TryGetValue(t.Id, out var dpv) && dpv.HasValue ? dpv.Value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) : "" };
                hhBox.TextChanged += (_, _) =>
                {
                    var txt = (hhBox.Text ?? "").Trim().Replace(',', '.');
                    if (string.IsNullOrEmpty(txt)) _donePending.Remove(t.Id);
                    else if (double.TryParse(txt, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0)
                        _donePending[t.Id] = v;
                    UpdatePendingButton();
                };
                hhRow.Children.Add(hhBox);
                sp.Children.Add(hhRow);
            }

            // Marca "Doing" (o que está atuando agora): chip azul; se o card estiver Closed vira "Done".
            var isDoing = _doing.Contains(t.Id);
            var closed = IsClosedState(EffState(t));
            if (isDoing)
            {
                border.BorderBrush = new SolidColorBrush(closed ? Color.FromRgb(0x10, 0x7C, 0x10) : Color.FromRgb(0x00, 0x78, 0xD4));
                border.BorderThickness = new Thickness(2);
                var chip = new Border
                {
                    Background = new SolidColorBrush(closed ? Color.FromRgb(0x10, 0x7C, 0x10) : Color.FromRgb(0x00, 0x78, 0xD4)),
                    CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = "🔵 " + AppStrings.Get(closed ? "Sprint_Done" : "Sprint_Doing"),
                        Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.SemiBold }
                };
                sp.Children.Add(chip);
            }

            var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0), Cursor = System.Windows.Input.Cursors.Arrow };
            var open = new Button { Content = "DevOps", FontSize = 10, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 4, 2) };
            open.Click += (_, _) => OpenInDevOps(t.Id);
            actions.Children.Add(open);
            // Botão Doing: só faz sentido nos abertos (não-Closed) para marcar; e para tirar em qualquer estado.
            if (!closed || isDoing)
            {
                // Só o sinal (+/-) fica maior; o texto "Doing" mantém a fonte padrão.
                var doingLabel = isDoing ? AppStrings.Get("Sprint_RemoveDoing") : AppStrings.Get("Sprint_MarkDoing");
                var doingContent = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                doingContent.Inlines.Add(new System.Windows.Documents.Run(doingLabel.Length > 0 ? doingLabel[..1] : "") { FontSize = 14, FontWeight = FontWeights.Bold });
                doingContent.Inlines.Add(new System.Windows.Documents.Run(doingLabel.Length > 1 ? doingLabel[1..] : "") { FontSize = 10 });
                var doingBtn = new Button
                {
                    Content = doingContent,
                    Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 4, 0)
                };
                doingBtn.Click += (_, _) =>
                {
                    if (_doing.Contains(t.Id)) _doing.Remove(t.Id); else _doing.Add(t.Id);
                    UpdatePendingButton();
                    Render();
                };
                actions.Children.Add(doingBtn);
            }
            if (inSched && _openInSchedule != null)
            {
                var sched = new Button { Content = "📅", FontSize = 11, Padding = new Thickness(5, 0, 5, 0),
                    ToolTip = AppStrings.Get("Query_OpenInSchedule") };
                sched.Click += (_, _) => _openInSchedule!(t.Id);
                actions.Children.Add(sched);
            }
            // Bloquear/desbloquear (tag "Blocked"): entra na fila do Salvar TFS.
            var blk = new Button { Content = EffBlocked(t.Id, t.Tags) ? "🔒" : "🔓", FontSize = 11,
                Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 4, 2),
                ToolTip = AppStrings.Get(EffBlocked(t.Id, t.Tags) ? "Sprint_Unblock" : "Sprint_Block") };
            blk.Click += (_, _) => ToggleBlockPending(t.Id, t.Tags);
            actions.Children.Add(blk);
            AddEditButtons(actions, t.Id, t.Title, t.AssignedTo, "Task"); // ✎ descrição e 💬 trâmite da Task
            // Excluir: só Tasks reais no estado New (evita apagar itens já em andamento/encerrados).
            // Marca para excluir (pendente); a exclusão no DevOps ocorre no Salvar TFS.
            if (t.Id > 0 && string.Equals(EffState(t), "New", StringComparison.OrdinalIgnoreCase))
            {
                var marked = _deletePending.Contains(t.Id);
                var del = new Button { Content = marked ? "↩" : "🗑", FontSize = 11, Padding = new Thickness(5, 0, 5, 0),
                    Margin = new Thickness(4, 0, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)),
                    ToolTip = AppStrings.Get(marked ? "Sprint_UndoDelete" : "Sprint_DeleteTask") };
                del.Click += (_, _) =>
                {
                    if (!_deletePending.Remove(t.Id)) _deletePending.Add(t.Id);
                    UpdatePendingButton(); Render();
                };
                actions.Children.Add(del);
            }
            sp.Children.Add(actions);
            border.Child = sp;

            // No modo edição, o card pode ser arrastado para outra coluna de estado.
            if (EditModeCheck.IsChecked == true)
            {
                border.Cursor = System.Windows.Input.Cursors.SizeAll;
                border.PreviewMouseMove += (s, ev) =>
                {
                    if (ev.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
                    // Não iniciar arrasto quando o mouse está sobre um controle do card (combo/botão),
                    // senão o clique (abrir a prioridade, editar, etc.) é engolido pelo drag.
                    if (IsInteractive(ev.OriginalSource as DependencyObject)) return;
                    DragDrop.DoDragDrop(border, t.Id, DragDropEffects.Move);
                };
            }
            return border;
        }

        // Soltou um card numa coluna de estado: marca a mudança como pendente (grava com "Atualizar TFS").
        private async void OnCardDrop(object sender, DragEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string newState) return;
            if (!e.Data.GetDataPresent(typeof(int))) return;
            var id = (int)e.Data.GetData(typeof(int))!;
            if (!_cardById.TryGetValue(id, out var dragged)) return;
            var cell = (fe as Border)?.Child as StackPanel;
            var y = cell != null ? e.GetPosition(cell).Y : 0;

            if (SameState(EffState(dragged), newState))
            {
                // Mesma coluna → reordena o RANK (StackRank) DENTRO do grupo de prioridade.
                if (id > 0 && cell != null) ReorderTaskRankInColumn(cell, id, y);
            }
            else
            {
                // Outra coluna → muda de estado (a prioridade/rank não muda pelo arrasto).
                var baseline = _applied.TryGetValue(id, out var a) ? a : dragged.State;
                if (SameState(baseline, newState)) _pending.Remove(id);
                else _pending[id] = newState;

                // Ao fechar (Closed) e SEM HH Realizado (nem pendente nem no DevOps), sugere o HH
                // Estimado como padrão no campo do card.
                if (id > 0 && IsClosedState(newState) && !_donePending.ContainsKey(id))
                {
                    var (est, comp, _) = await TfsImportService.GetWorkItemHoursAsync(_options, id);
                    if (!(comp > 0))
                    {
                        double? def = est is > 0 ? est
                            : (double.TryParse(dragged.Effort, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out var eh) && eh > 0 ? eh : (double?)null);
                        if (def is > 0) _donePending[id] = def;
                    }
                }
            }
            RepositionOrder(cell, id, y);
            UpdatePendingButton();
            Render();
        }

        // Reordena o rank (StackRank) do card arrastado apenas entre os da MESMA prioridade na coluna.
        private void ReorderTaskRankInColumn(StackPanel cell, int id, double y)
        {
            if (!_cardById.TryGetValue(id, out var dragged)) return;
            var p = EffPrio(dragged);
            var samePrio = cell.Children.OfType<Border>()
                .Where(b => b.Tag is int bid && bid != id && bid > 0
                            && _cardById.TryGetValue(bid, out var c) && EffPrio(c) == p)
                .Select(b => (Rank: EffTaskRank(_cardById[(int)b.Tag!]),
                              Center: b.TranslatePoint(new Point(0, b.ActualHeight / 2), cell).Y))
                .OrderBy(x => x.Center).ToList();
            if (samePrio.Count == 0) return; // sozinho na prioridade → nada a reordenar
            var insertAt = samePrio.Count(x => x.Center < y);
            double newRank;
            if (insertAt == 0) newRank = samePrio[0].Rank - 1;
            else if (insertAt >= samePrio.Count) newRank = samePrio[^1].Rank + 1;
            else newRank = (samePrio[insertAt - 1].Rank + samePrio[insertAt].Rank) / 2.0;
            _taskRank[id] = newRank;
            _taskRankPending.Add(id);
        }

        // Ordem visual (fallback dentro da célula) pela posição solta.
        private void RepositionOrder(StackPanel? cell, int id, double y)
        {
            if (cell == null) return;
            var others = cell.Children.OfType<Border>()
                .Where(b => b.Tag is int bid && bid != id)
                .Select(b => (Key: _order.TryGetValue((int)b.Tag!, out var o) ? o : (double)(int)b.Tag!,
                              Center: b.TranslatePoint(new Point(0, b.ActualHeight / 2), cell).Y))
                .OrderBy(x => x.Center).ToList();
            var insertAt = others.Count(x => x.Center < y);
            _order[id] = others.Count == 0 ? 0
                : insertAt == 0 ? others[0].Key - 1
                : insertAt >= others.Count ? others[^1].Key + 1
                : (others[insertAt - 1].Key + others[insertAt].Key) / 2.0;
        }

        private int EffPrio(TfsImportService.SprintTaskCard t) =>
            _prioPending.TryGetValue(t.Id, out var p) ? p
            : _prioApplied.TryGetValue(t.Id, out var a) ? a : t.Priority;

        // Faixa de prioridade. O campo Priority é Integer e o DevOps NÃO expõe allowedValues (nem
        // no campo, nem no processo, nem em rules — só setDefaultValue). O formulário do DevOps usa
        // o padrão 1–9; então usamos 1–9 por padrão (ou a faixa de Configurar DevOps, se habilitada),
        // AMPLIADA pelas prioridades já em uso no board.
        private (int Min, int Max) PriorityRange()
        {
            // Base: classe central (config + máximo descoberto no template). Ampliada pelo observado.
            var baseRange = TaskPriorityRange.FromOptions(_options, _discoveredPrioMax);
            var observed = _board?.Stories.SelectMany(s => s.Tasks).Select(t => t.Priority).Where(p => p > 0).ToList()
                           ?? new List<int>();
            var min = observed.Count > 0 ? Math.Min(baseRange.Min, observed.Min()) : baseRange.Min;
            var max = observed.Count > 0 ? Math.Max(baseRange.Max, observed.Max()) : baseRange.Max;
            return (Math.Max(1, min), Math.Max(min, max));
        }

        private static Brush PriorityBrush(int p) => p switch
        {
            1 => new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)), // P1 vermelho
            2 => new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)), // P2 laranja
            3 => new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)), // P3 azul
            _ => new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A))  // P4+ cinza
        };

        // Verdadeiro se o elemento (ou um ancestral) é um controle interativo — para não confundir
        // clique em combo/botão com início de arrasto do card.
        private static bool IsInteractive(DependencyObject? o)
        {
            while (o != null)
            {
                if (o is System.Windows.Controls.Primitives.ButtonBase || o is ComboBox || o is System.Windows.Controls.Primitives.Selector)
                    return true;
                o = o is System.Windows.Media.Visual || o is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(o)
                    : System.Windows.LogicalTreeHelper.GetParent(o);
            }
            return false;
        }

        private static bool SameState(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // Cor do estado: usa a cor customizada (prefs) se houver; senão a paleta padrão.
        private Brush StateBrush(string state)
        {
            if (_prefs.StateColors != null && _prefs.StateColors.TryGetValue(state.ToLowerInvariant(), out var hex)
                && TryParseColor(hex, out var c))
                return new SolidColorBrush(c);
            return new SolidColorBrush(DefaultStateColor(state));
        }

        // Cor padrão do estado: primeiro as cores que o usuário salvou como padrão; senão a de fábrica.
        private static Color DefaultStateColor(string state)
        {
            if (SavedDefaultColors.TryGetValue(state.ToLowerInvariant(), out var hex) && TryParseColor(hex, out var c))
                return c;
            return FactoryStateColor(state);
        }

        // Chave da cor do "Story Colaborador" (pessoa ajuda na task, mas não é responsável da Story).
        private const string HelperColorKey = "story-colaborador";

        private static Color FactoryStateColor(string state) => state.ToLowerInvariant() switch
        {
            HelperColorKey => Color.FromRgb(0xFD, 0xF3, 0xE0), // âmbar bem claro
            "new" or "to do" or "approved" => Color.FromRgb(0x6B, 0x7A, 0x8A),
            // Active = cor de DESTAQUE (accent forte); é o estado que precisa de atenção.
            "active" or "committed" or "in progress" or "doing" or "open" => Color.FromRgb(0x00, 0x78, 0xD4),
            "resolved" => Color.FromRgb(0xB2, 0x6A, 0x00),
            // Closed = concluído, cor suave (verde acinzentado) para não roubar o destaque.
            "done" or "closed" or "completed" => Color.FromRgb(0x8A, 0xA5, 0x95),
            _ => Color.FromRgb(0x8A, 0x8A, 0x8A)
        };

        // Cores salvas como padrão (compartilhadas por todos os boards). Arquivo em LocalAppData.
        private static string DefaultColorsFile => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXProject.Community", "statecolors-default.json");
        private static Dictionary<string, string>? _savedDefaultColors;
        private static Dictionary<string, string> SavedDefaultColors
        {
            get
            {
                if (_savedDefaultColors != null) return _savedDefaultColors;
                try
                {
                    if (System.IO.File.Exists(DefaultColorsFile))
                        _savedDefaultColors = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                            System.IO.File.ReadAllText(DefaultColorsFile));
                }
                catch { }
                return _savedDefaultColors ??= new();
            }
        }
        private static void SaveDefaultColors(IReadOnlyDictionary<string, string> map)
        {
            try
            {
                _savedDefaultColors = new Dictionary<string, string>(map.ToDictionary(k => k.Key.ToLowerInvariant(), v => v.Value));
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DefaultColorsFile)!);
                System.IO.File.WriteAllText(DefaultColorsFile, System.Text.Json.JsonSerializer.Serialize(_savedDefaultColors));
            }
            catch { }
        }

        // Fundo claro do card tingido pela cor do estado (mistura com branco).
        private Brush StateTintBrush(string state)
        {
            var c = (StateBrush(state) as SolidColorBrush)?.Color ?? DefaultStateColor(state);
            byte Mix(byte v) => (byte)(v + (255 - v) * 0.82); // ~18% da cor sobre branco
            return new SolidColorBrush(Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B)));
        }

        private static bool TryParseColor(string? hex, out Color color)
        {
            color = Colors.Gray;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            try { color = (Color)ColorConverter.ConvertFromString(hex.Trim()); return true; }
            catch { return false; }
        }

        // Abre o editor de cores por estado; ao confirmar, salva nas prefs e re-renderiza.
        private void OnColorsClick(object sender, RoutedEventArgs e)
        {
            var states = _board?.States?.ToList() ?? new List<string>();
            if (states.Count == 0) return;
            var dlg = new StateColorsWindow(states, _prefs.StateColors, DefaultStateColor,
                onSaveDefault: all =>
                {
                    // "Definir como padrão": vira o padrão de todos os boards e limpa o override local.
                    SaveDefaultColors(all);
                    _prefs.StateColors = null;
                    SavePrefs();
                    Render();
                },
                extraRows: new[] { (AppStrings.Get("Colors_StoryHelp"), HelperColorKey) }) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _prefs.StateColors = dlg.Result.Count > 0 ? dlg.Result : null;
                SavePrefs();
                Render();
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
