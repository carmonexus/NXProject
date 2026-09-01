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
        private sealed class NewCard { public int TempId; public string Type = ""; public string Title = ""; public int ParentId; public string FeatureTitle = ""; public int FeatureId; public string AssignedTo = ""; public double? Effort; public string Description = ""; }
        private readonly List<NewCard> _newCards = new();
        private int _nextTempId = -1;
        private string _sprintPath = "";
        private bool _initialSelection = true;

        private static string SprintSettingsPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXProject.Community", "sprintview.json");

        private static string? LoadLastSprintPath()
        {
            try
            {
                var p = SprintSettingsPath;
                if (!System.IO.File.Exists(p)) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(p));
                return doc.RootElement.TryGetProperty("LastSprintPath", out var v) ? v.GetString() : null;
            }
            catch { return null; }
        }

        // Dias anteriores para exibir Closed. Só é gravado quando > 0 (0 = "todos", não persiste).
        private static int? LoadClosedDays()
        {
            try
            {
                var p = SprintSettingsPath;
                if (!System.IO.File.Exists(p)) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(p));
                return doc.RootElement.TryGetProperty("ClosedDays", out var v)
                    && v.TryGetInt32(out var d) && d > 0 ? d : (int?)null;
            }
            catch { return null; }
        }

        private static void SaveLastSprintPath(string path) => WriteSprintSettings(path, null);

        // Grava ClosedDays só se > 0; zero remove a chave (volta a "todos" ao reabrir).
        private static void SaveClosedDays(int days) => WriteSprintSettings(null, days);

        private static void WriteSprintSettings(string? path, int? closedDays)
        {
            try
            {
                var p = SprintSettingsPath;
                string? curPath = LoadLastSprintPath();
                int? curDays = LoadClosedDays();
                var finalPath = path ?? curPath ?? "";
                var finalDays = closedDays ?? curDays;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
                object payload = finalDays is int fd && fd > 0
                    ? new { LastSprintPath = finalPath, ClosedDays = fd }
                    : new { LastSprintPath = finalPath };
                System.IO.File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(payload));
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
            // Restaura a preferência de "dias anteriores" do Closed (0/ausente = padrão 30).
            var savedDays = LoadClosedDays();
            if (savedDays is int sd && sd > 0)
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
                // Opção "Todas as sprints" (Path vazio): board sem filtro de iteração — útil
                // quando as Stories/Tasks não estão bem distribuídas nas sprints.
                _sprints.Insert(0, new TfsImportService.SprintInfo(
                    AppStrings.Get("Sprint_AllSprints"), "", null, null));
                SprintCombo.ItemsSource = _sprints;
                SprintCombo.DisplayMemberPath = nameof(TfsImportService.SprintInfo.Name);
                // Na ENTRADA: última sprint salva → sprint atual (por data) → sugerida do cronograma
                // → última da lista. Depois, o usuário troca à vontade (e a escolha é salva).
                int idx = -1;
                var saved = LoadLastSprintPath();
                if (!string.IsNullOrWhiteSpace(saved))
                    idx = _sprints.FindIndex(s => string.Equals(s.Path, saved, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) idx = CurrentSprintIndex();
                if (idx < 0 && !string.IsNullOrWhiteSpace(_preferredSprint))
                    idx = _sprints.FindIndex(s => string.Equals(s.Path, _preferredSprint, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) idx = _sprints.Count - 1;
                StatusText.Text = "";
                _initialSelection = true;
                if (idx >= 0) SprintCombo.SelectedIndex = idx; // dispara OnSprintChanged
            }
            catch (Exception ex)
            {
                StatusText.Text = "";
                MessageBox.Show(this, AppStrings.Get("Sprint_Error", ex.Message),
                    "NXProject", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Índice da sprint atual pela data (a de menor duração que contém hoje = a folha, não o container).
        private int CurrentSprintIndex()
        {
            var today = DateTime.Today;
            var candidates = _sprints
                .Select((s, i) => (s, i))
                .Where(x => x.s.Start is { } st && x.s.End is { } en && today >= st && today <= en)
                .OrderBy(x => (x.s.End!.Value - x.s.Start!.Value).TotalDays)
                .ToList();
            return candidates.Count > 0 ? candidates[0].i : -1;
        }

        private void OnCurrentSprintClick(object sender, RoutedEventArgs e)
        {
            var idx = CurrentSprintIndex();
            if (idx >= 0) SprintCombo.SelectedIndex = idx;
        }

        // Recarrega a sprint atual do TFS. Se houver mudanças pendentes, pede confirmação (o reload as descarta).
        private async void OnReloadClick(object sender, RoutedEventArgs e)
        {
            if (SprintCombo.SelectedItem is not TfsImportService.SprintInfo) return;
            if (PendingCount() > 0 &&
                MessageBox.Show(this, AppStrings.Get("Sprint_ReloadConfirm"), "NXProject",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            await ReloadBoardAsync(_sprintPath);
        }

        private async void OnSprintChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SprintCombo.SelectedItem is not TfsImportService.SprintInfo sp) return;
            // Memoriza a sprint escolhida (exceto o posicionamento automático da entrada).
            if (!_initialSelection) SaveLastSprintPath(sp.Path);
            _initialSelection = false;
            await ReloadBoardAsync(sp.Path);
        }

        // Carrega/recarrega o board da sprint e reseta o estado local (pendências, filtros).
        private async Task ReloadBoardAsync(string path)
        {
            StatusText.Text = AppStrings.Get("Sprint_Loading");
            try
            {
                _sprintPath = path;
                _board = await TfsImportService.BuildSprintBoardAsync(_options, path);
                var people = new List<string> { AllPeople };
                people.AddRange(_board.People);
                var keep = PersonCombo.SelectedItem as string;
                PersonCombo.ItemsSource = people;
                PersonCombo.SelectedItem = keep != null && people.Contains(keep) ? keep : AllPeople;
                _storyById = _board.Stories.Where(s => s.Id > 0).ToDictionary(s => s.Id);
                _selectedStoryIds.Clear();
                // Com projeto aberto: por padrão filtra só as Stories dele (Todo Portfólio desmarcado).
                var openIds = _board.Stories.Where(s => s.Id > 0 && _scheduleIds.Contains(s.Id)).Select(s => s.Id).ToList();
                if (openIds.Count > 0)
                    foreach (var oid in openIds) _selectedStoryIds.Add(oid);
                _hiddenStates.Clear();
                foreach (var st in _board.States.Where(IsClosedState)) _hiddenStates.Add(st);
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
            new(n.TempId, n.Title, "New", n.AssignedTo ?? "", "", n.ParentId, "", null, 0, 0);

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
            _newCards.Add(new NewCard { TempId = _nextTempId--, Type = "Story", ParentId = featureId, FeatureId = featureId, FeatureTitle = featureTitle });
            UpdatePendingButton(); Render();
        }

        private void AddNewTask(int storyId, string? assignedTo = null)
        {
            // Já nasce na faixa da pessoa onde foi criado (fica no grupo da Story).
            var who = string.Equals(assignedTo, AppStrings.Get("Sprint_NoOwner"), StringComparison.Ordinal) ? "" : (assignedTo ?? "");
            _newCards.Add(new NewCard { TempId = _nextTempId--, Type = "Task", ParentId = storyId, AssignedTo = who });
            UpdatePendingButton(); Render();
        }

        private void OnFilterChanged(object sender, RoutedEventArgs e)
        {
            // Ao entrar em "Task por Pessoa" com o filtro em Todos, já traz o usuário do NX.
            if (ViewCombo.SelectedIndex == 1 && (PersonCombo.SelectedItem as string) == AllPeople)
            {
                var me = MatchCurrentUser();
                if (me != null) { PersonCombo.SelectedItem = me; return; } // re-dispara → Render
            }
            Render();
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
            {
                _closedDays = d;
                SaveClosedDays(d); // grava só se > 0; zero remove a preferência
            }
            StateFilterToggle.IsChecked = false;
            Render();
        }

        // Botões ✎ (descrição) e 💬 (trâmite) para Story/Task — reusam o editor WebView do NX.
        private void AddEditButtons(Panel panel, int id, string title, string currentOwner = "", string kind = "Story")
        {
            if (id <= 0) return;
            // ✎ marca "●" quando há descrição OU responsável pendente.
            var descDirty = _descPending.ContainsKey(id) || _ownerPending.ContainsKey(id)
                || _titlePending.ContainsKey(id) || _estPending.ContainsKey(id) || _donePending.ContainsKey(id);
            var desc = new Button { Content = descDirty ? "✎●" : "✎", FontSize = 11,
                Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 4, 0),
                Foreground = descDirty ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Black,
                ToolTip = AppStrings.Get("Sprint_EditDesc") };
            desc.Click += async (_, _) => await EditDescriptionAsync(id, title, currentOwner, kind);
            panel.Children.Add(desc);
            var tram = new Button { Content = _tramitePending.ContainsKey(id) ? "💬●" : "💬", FontSize = 11,
                Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 4, 0),
                Foreground = _tramitePending.ContainsKey(id) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Black,
                ToolTip = AppStrings.Get("Sprint_EditTramite") };
            tram.Click += (_, _) => EditTramite(id, title);
            panel.Children.Add(tram);
        }

        // Descrição: abre o editor (WebView) com a descrição atual do DevOps (ou o rascunho pendente).
        private async Task EditDescriptionAsync(int id, string title, string currentOwner = "", string kind = "Story")
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
                hState = st;
            }
            var dlg = new TaskDescriptionEditWindow(pt, people, owner, enableNameEdit: id > 0, objectKind: kind,
                enableHours: id > 0, estimate: est, completed: done, state: hState) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _descPending[id] = pt.Description ?? string.Empty;
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
            return _pending.Count + doingDiff + _descPending.Count + _tramitePending.Count + _newCards.Count + _prioPending.Count + _storyRankPending.Count + _taskRankPending.Count + _storyStatePending.Count + _ownerPending.Count + _titlePending.Count + _estPending.Count + _donePending.Count;
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

            // 1) Mudanças de estado (arrasto). Guarda os que mudaram para reavaliar a tag (Doing→Done).
            var stateChanged = _pending.Keys.ToHashSet();
            foreach (var kv in _pending.ToList())
            {
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

            // 4) Trâmite (comentário/discussão; aceita HTML com imagem).
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

            // 5) Novos cards: cria Stories (recebem ID) e depois Tasks (usando o ID da story-pai).
            // Em "Todas as sprints" não há iteração-alvo: não permite criar itens novos.
            var reload = false;
            var tempToReal = new Dictionary<int, int>();
            var canCreate = !(string.IsNullOrWhiteSpace(_sprintPath) && _newCards.Count > 0);
            if (!canCreate) fails.Add(AppStrings.Get("Sprint_NewNeedsSprint"));
            foreach (var ns in _newCards.Where(n => canCreate && n.Type == "Story").ToList())
            {
                if (string.IsNullOrWhiteSpace(ns.Title) || !(ns.Effort is > 0) || string.IsNullOrWhiteSpace(ns.AssignedTo))
                { fails.Add(AppStrings.Get("Sprint_NewIncomplete")); continue; }
                bool dup = (_board?.Stories.Any(s => s.FeatureId == ns.FeatureId && s.Title.Trim().Equals(ns.Title.Trim(), StringComparison.CurrentCultureIgnoreCase)) ?? false)
                    || _newCards.Any(o => o != ns && o.Type == "Story" && o.FeatureId == ns.FeatureId && o.Title.Trim().Equals(ns.Title.Trim(), StringComparison.CurrentCultureIgnoreCase));
                if (dup) { fails.Add($"Story '{ns.Title}': {AppStrings.Get("Sprint_DupName")}"); continue; }
                var (nid, msg) = await TfsImportService.CreateChildWorkItemAsync(_options, "User Story", ns.Title.Trim(), ns.ParentId, _sprintPath,
                    string.IsNullOrWhiteSpace(ns.Description) ? null : TfsImportService.PlainTextToSimpleHtml(ns.Description),
                    string.IsNullOrWhiteSpace(ns.AssignedTo) ? null : ns.AssignedTo, ns.Effort);
                if (nid > 0) { tempToReal[ns.TempId] = nid; _newCards.Remove(ns); ok++; reload = true; }
                else fails.Add($"Story '{ns.Title}': {msg}");
            }
            foreach (var nt in _newCards.Where(n => canCreate && n.Type == "Task").ToList())
            {
                if (string.IsNullOrWhiteSpace(nt.Title) || !(nt.Effort is > 0) || string.IsNullOrWhiteSpace(nt.AssignedTo))
                { fails.Add(AppStrings.Get("Sprint_NewIncomplete")); continue; }
                var parent = nt.ParentId < 0 ? (tempToReal.TryGetValue(nt.ParentId, out var r) ? r : 0) : nt.ParentId;
                if (parent <= 0) { fails.Add($"Task '{nt.Title}': story-pai não criada"); continue; }
                bool dup = (_board?.Stories.FirstOrDefault(s => s.Id == parent)?.Tasks.Any(t => t.Title.Trim().Equals(nt.Title.Trim(), StringComparison.CurrentCultureIgnoreCase)) ?? false)
                    || _newCards.Any(o => o != nt && o.Type == "Task" && o.ParentId == nt.ParentId && o.Title.Trim().Equals(nt.Title.Trim(), StringComparison.CurrentCultureIgnoreCase));
                if (dup) { fails.Add($"Task '{nt.Title}': {AppStrings.Get("Sprint_DupName")}"); continue; }
                var (nid, msg) = await TfsImportService.CreateChildWorkItemAsync(_options, "Task", nt.Title.Trim(), parent, _sprintPath,
                    string.IsNullOrWhiteSpace(nt.Description) ? null : TfsImportService.PlainTextToSimpleHtml(nt.Description),
                    string.IsNullOrWhiteSpace(nt.AssignedTo) ? null : nt.AssignedTo, nt.Effort);
                if (nid > 0) { _newCards.Remove(nt); ok++; reload = true; }
                else fails.Add($"Task '{nt.Title}': {msg}");
            }

            if (reload)
                await ReloadBoardAsync(_sprintPath); // recarrega: os novos aparecem com ID do TFS e cor normal
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

        // Filtros de recorte (pessoa, cronograma, story) — não mexem nas colunas.
        private bool PassesBaseFilters(TfsImportService.SprintTaskCard t)
        {
            if (OnlyScheduleCheck.IsChecked == true && !_scheduleIds.Contains(t.Id)) return false;
            var person = PersonCombo.SelectedItem as string;
            if (!string.IsNullOrEmpty(person) && person != AllPeople
                && !string.Equals(t.AssignedTo, person, StringComparison.CurrentCultureIgnoreCase)) return false;
            if (_selectedStoryIds.Count > 0 && !(t.ParentId is int p && _selectedStoryIds.Contains(p))) return false;
            // Busca ao vivo: casa no título da task, no responsável ou no título da Story.
            var q = SearchBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                var storyTitle = t.ParentId is int sp && _storyById.TryGetValue(sp, out var st) ? st.Title : "";
                bool match = t.Title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || (!string.IsNullOrEmpty(t.AssignedTo) && t.AssignedTo.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    || (!string.IsNullOrEmpty(storyTitle) && storyTitle.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    || t.Id.ToString().Contains(q);
                if (!match) return false;
            }
            return true;
        }

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
            var person = PersonCombo.SelectedItem as string;
            if (!string.IsNullOrEmpty(person) && person != AllPeople)
                parts.Add(AppStrings.Get("Sprint_FSPerson", person));
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

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
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
                var stActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                AddEditButtons(stActions, story.Id, story.Title, story.AssignedTo); // ✎/💬 da Story
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
                foreach (var sg in pg
                             .GroupBy(t => t.ParentId is int pid && _storyById.TryGetValue(pid, out var st) ? st.Title : AppStrings.Get("Sprint_NoStory"))
                             .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
                {
                    var row = MakeRowGrid(cols);
                    if (firstRow)
                        AddCell(row, 0, new TextBlock { Text = pg.Key, FontWeight = FontWeights.Bold,
                            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 2) });
                    var tks = sg.ToList();
                    var storySp = new StackPanel();
                    var storyId = tks.FirstOrDefault(t => t.ParentId is int)?.ParentId ?? 0;
                    storySp.Children.Add(new TextBlock { Text = EffTitle(storyId, sg.Key), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                        Foreground = _titlePending.ContainsKey(storyId) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Black });
                    if (storyId > 0)
                    {
                        var sOwnerOrig = StoryById(storyId)?.AssignedTo ?? string.Empty;
                        var sOwner = EffOwner(storyId, sOwnerOrig);
                        storySp.Children.Add(new TextBlock {
                            Text = "👤 " + (string.IsNullOrWhiteSpace(sOwner) ? AppStrings.Get("Sprint_NoOwner") : sOwner),
                            FontSize = 10, TextWrapping = TextWrapping.Wrap,
                            Foreground = _ownerPending.ContainsKey(storyId) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.DimGray });
                        var stActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                        AddEditButtons(stActions, storyId, sg.Key, sOwnerOrig);
                        var addTask = new Button { Content = AppStrings.Get("Sprint_AddTask"), FontSize = 10, Padding = new Thickness(5, 0, 5, 0) };
                        var personKey = pg.Key;
                        addTask.Click += (_, _) => AddNewTask(storyId, personKey); // já nasce na faixa da pessoa
                        stActions.Children.Add(addTask);
                        storySp.Children.Add(stActions);
                    }
                    var storyBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF7)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xE0)), BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3), Margin = new Thickness(3), Padding = new Thickness(6),
                        Child = storySp };
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

        // Nome/título efetivo de um work item (pendente > aplicado > valor original).
        private string EffTitle(int id, string original) =>
            id <= 0 ? original
            : _titlePending.TryGetValue(id, out var p) ? p
            : _titleApplied.TryGetValue(id, out var a) ? a : original;

        private bool StoryPasses(TfsImportService.SprintStoryRow s)
        {
            if (s.Id < 0) return true; // Story nova sempre visível
            if (_selectedStoryIds.Count > 0 && !_selectedStoryIds.Contains(s.Id)) return false;
            var q = SearchBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(q))
                return s.Title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || s.Id.ToString().Contains(q);
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
            // Trocar a pessoa move o card para a faixa dela (re-renderiza).
            person.SelectionChanged += (_, _) => { nc.AssignedTo = (person.SelectedItem as string) ?? person.Text; UpdatePendingButton(); Render(); };
            person.LostFocus += (_, _) => { if (nc.AssignedTo != person.Text) { nc.AssignedTo = person.Text; Render(); } };
            sp.Children.Add(person);

            var hhRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            hhRow.Children.Add(new TextBlock { Text = AppStrings.Get("Sprint_FldHH"), FontSize = 10, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            var hh = new TextBox { Width = 56, FontSize = 11, Text = nc.Effort?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) ?? "" };
            hh.TextChanged += (_, _) => nc.Effort = double.TryParse(hh.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out var v) ? v : (double?)null;
            hhRow.Children.Add(hh);
            sp.Children.Add(hhRow);

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
            var pend = _storyStatePending.ContainsKey(story.Id) || _storyRankPending.Contains(story.Id);
            var border = new Border
            {
                Background = new SolidColorBrush(isNew ? Color.FromRgb(0xE7, 0xF6, 0xE7) : pend ? Color.FromRgb(0xFF, 0xF4, 0xD6) : Color.FromRgb(0xEE, 0xF2, 0xF7)),
                BorderBrush = new SolidColorBrush(isNew ? Color.FromRgb(0x10, 0x7C, 0x10) : pend ? Color.FromRgb(0xE0, 0x8A, 0x00) : Color.FromRgb(0xD0, 0xD7, 0xE0)),
                BorderThickness = new Thickness(isNew || pend ? 2 : 1),
                CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(6), Tag = story.Id
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = (isNew ? "🆕 " : "") + EffTitle(story.Id, story.Title), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Foreground = _titlePending.ContainsKey(story.Id) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Black });
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
                // StoryBoard = nível Story: aqui NÃO cria Task (isso é na visão Pessoa & Task).
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                AddEditButtons(actions, story.Id, story.Title, story.AssignedTo);
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
            var border = new Border
            {
                Background = isNew ? new SolidColorBrush(Color.FromRgb(0xE7, 0xF6, 0xE7))
                    : isPending ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xD6)) : Brushes.White,
                BorderBrush = new SolidColorBrush(isNew ? Color.FromRgb(0x10, 0x7C, 0x10)
                    : isPending ? Color.FromRgb(0xE0, 0x8A, 0x00)
                    : inSched ? Color.FromRgb(0x2B, 0x57, 0x9A) : Color.FromRgb(0xCF, 0xD8, 0xE3)),
                BorderThickness = new Thickness(isNew || isPending || inSched ? 2 : 1),
                CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(6),
                Tag = t.Id
            };
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
            titleLine.Children.Add(new TextBlock { Text = EffTitle(t.Id, t.Title), TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Foreground = _titlePending.ContainsKey(t.Id) ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)) : Brushes.Black });
            sp.Children.Add(titleLine);
            var line = new TextBlock { FontSize = 10, Foreground = Brushes.Gray };
            line.Text = (isNew ? AppStrings.Get("Sprint_New") : $"#{t.Id}")
                + (string.IsNullOrWhiteSpace(t.AssignedTo) ? "" : $"  ·  {t.AssignedTo}")
                + (string.IsNullOrWhiteSpace(t.Effort) ? "" : $"  ·  {t.Effort}h");
            sp.Children.Add(line);
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

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var open = new Button { Content = "DevOps", FontSize = 10, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 4, 0) };
            open.Click += (_, _) => OpenInDevOps(t.Id);
            actions.Children.Add(open);
            // Botão Doing: só faz sentido nos abertos (não-Closed) para marcar; e para tirar em qualquer estado.
            if (!closed || isDoing)
            {
                var doingBtn = new Button
                {
                    Content = isDoing ? AppStrings.Get("Sprint_RemoveDoing") : AppStrings.Get("Sprint_MarkDoing"),
                    FontSize = 10, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 4, 0)
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
            AddEditButtons(actions, t.Id, t.Title, t.AssignedTo, "Task"); // ✎ descrição e 💬 trâmite da Task
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
        private void OnCardDrop(object sender, DragEventArgs e)
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

        private static Brush StateBrush(string state) => state.ToLowerInvariant() switch
        {
            "new" or "to do" or "approved" => new SolidColorBrush(Color.FromRgb(0x6B, 0x7A, 0x8A)),
            "active" or "committed" or "in progress" or "doing" or "open" => new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)),
            "resolved" => new SolidColorBrush(Color.FromRgb(0xB2, 0x6A, 0x00)),
            "done" or "closed" or "completed" => new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)),
            _ => new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A))
        };

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
