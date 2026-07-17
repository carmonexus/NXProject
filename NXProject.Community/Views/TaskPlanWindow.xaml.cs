using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NXProject.Community.Services;
using NXProject.Models;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Views
{
    public partial class TaskPlanWindow : Window
    {
        private readonly MainViewModel? _vm;
        private string? _path;
        private TaskPlanData? _data;
        private string? _epicColumn;   // nome da coluna EPIC (se houver)
        private bool _suppressEpic;
        private bool _dirty;           // alterações não salvas no .xlsx
        private TaskPlanSettings _settings = new();

        // Arquivo base do primeiro teste (Downloads) — usado só se não houver configuração.
        private static readonly string DefaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "Plano de Tasks EPIC xxx Fase 1.xlsx");

        // Pasta inicial dos diálogos de abrir/salvar.
        private string? DialogFolder =>
            !string.IsNullOrWhiteSpace(_settings.DefaultFolder) && Directory.Exists(_settings.DefaultFolder)
                ? _settings.DefaultFolder
                : (File.Exists(_path) ? Path.GetDirectoryName(_path) : null);

        public TaskPlanWindow(MainViewModel? vm = null)
        {
            InitializeComponent();
            _vm = vm;
            // Ao fechar, devolve o foco à janela do cronograma (senão o app parece "sumir").
            Closed += (_, _) => Owner?.Activate();
            Closing += OnWindowClosing;
            // Ctrl+Z global na janela (funciona mesmo sem célula selecionada/foco na grade).
            PreviewKeyDown += (_, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Z
                    && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0
                    && args.OriginalSource is not TextBox
                    && args.OriginalSource is not ComboBox)
                {
                    PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
                    Undo();
                    args.Handled = true;
                }
            };
            PlanGrid.CellEditEnding += (_, _) => _dirty = true;
            // Atalho: digitar "+" na coluna DT_Registro vira a data de hoje.
            PlanGrid.CellEditEnding += (_, args) =>
            {
                if (args.EditAction != DataGridEditAction.Commit) return;
                if (!string.Equals(args.Column?.Header?.ToString(), RegisterDateCol, StringComparison.Ordinal)) return;
                if (args.EditingElement is TextBox tb && tb.Text.Trim() == "+")
                    tb.Text = FormatRegisterDate(DateTime.Today);
            };
            PlanGrid.RowEditEnding += (_, _) =>
            {
                _dirty = true;
                // Após terminar a edição da linha, revalida (atualiza ID Feature/ID Story
                // conforme a digitação e as cores de EPIC/Task).
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ValidateAgainstSchedule();
                    PlanGrid.Items.Refresh();
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
            // Snapshot antes de cada edição de célula — habilita o Ctrl+Z.
            PlanGrid.BeginningEdit += (_, _) => PushUndo();
            _settings = TaskPlanSettingsStore.Load();
            Loaded += (_, _) =>
            {
                // Com cronograma aberto: só abre a planilha ASSOCIADA a este projeto
                // (configuração local ou .nxp) — nunca a do cronograma anterior.
                if (_vm?.Project != null)
                {
                    var associated = _settings.GetProjectFile(_vm.Project.Name) ?? _vm.Project.PlanSheetPath;
                    if (!string.IsNullOrWhiteSpace(associated) && File.Exists(associated))
                        LoadFile(associated!);
                    else if (_vm.Project.Tasks.Count > 0)
                        BuildFromSchedule();   // sem planilha associada: carrega o cronograma na grade
                    else
                        StatusText.Text = AppStrings.Get("TaskPlan_PickFile");
                    return;
                }

                // Sem cronograma: reabre o último arquivo; senão o legado.
                if (!string.IsNullOrWhiteSpace(_settings.LastFile) && File.Exists(_settings.LastFile))
                    LoadFile(_settings.LastFile);
                else if (File.Exists(DefaultPath))
                    LoadFile(DefaultPath);
                else
                    StatusText.Text = AppStrings.Get("TaskPlan_PickFile");
            };
        }

        // Mostra "pasta-pai\arquivo.xlsx" (o path completo fica no hint); clique abre o Explorer.
        private void SetPathDisplay(string? fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                PathText.ToolTip = null;
                PathText.Cursor = null;
                return;
            }
            var parent = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? "");
            PathText.Text = parent.Length > 0 ? $"{parent}\\{Path.GetFileName(fullPath)}" : Path.GetFileName(fullPath);
            PathText.ToolTip = fullPath;
            PathText.Cursor = System.Windows.Input.Cursors.Hand;
        }

        private void OnPathTextClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });
            }
            catch { /* Explorer indisponível não interrompe a tela */ }
        }

        // Aplica o log lateral de sincronização (IDs :I → :T) na planilha recém-aberta,
        // atualizando as células de ID Task/ID Story/ID Feature e limpando o log.
        private void ApplyPendingSyncLog(string path)
        {
            if (_data == null) return;
            var entries = ExcelTaskPlanService.ReadPendingSidecar(path);
            if (entries == null || entries.Count == 0) return;

            var idCol      = FindColumn("ID Task", "IdTask", "ID_Task", "ID Devops", "ID DevOps", "IdDevops", "ID_Devops", "ID Dev Ops");
            if (idCol == null) return;
            var byKey = entries.GroupBy(e => e.TaskKey, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            int updated = 0;
            foreach (DataRow dr in _data.Table.Rows)
            {
                var cur = dr[idCol]?.ToString()?.Trim() ?? "";
                if (!byKey.TryGetValue(cur, out var e)) continue;
                dr[idCol] = e.NewTaskId;
                if (ApprovalCol is { } ac) dr[ac] = "Sim";
                if (StoryIdCol is { } sc && !string.IsNullOrEmpty(e.NewStoryId)) dr[sc] = e.NewStoryId;
                if (FeatureIdCol is { } fc && !string.IsNullOrEmpty(e.NewFeatureId)) dr[fc] = e.NewFeatureId;
                updated++;
            }

            if (updated > 0)
            {
                _dirty = true;
                ValidateAgainstSchedule();
                PlanGrid.Items.Refresh();
                StatusText.Foreground = System.Windows.Media.Brushes.Green;
                StatusText.Text = AppStrings.Get("TaskPlan_BackfillApplied", updated);
            }
            // Log aplicado (ou sem correspondência): remove.
            ExcelTaskPlanService.DeletePendingSidecar(path);
        }

        // ── carregar / salvar ────────────────────────────────────────────────
        private void LoadFile(string path)
        {
            try
            {
                _data = ExcelTaskPlanService.Load(path);
                _path = path;
                SetPathDisplay(path);
                // Baseline ANTES de ajustar a tabela: o que vier a seguir (coluna nova de
                // aprovação/cronograma, normalização de Sim/Nao) é diferença real contra o
                // .xlsx e precisa manter o plano sujo para ser gravado no próximo salvar.
                _dirty = false;
                EnsureScheduleColumns();
                UpdateFixedColumns();
                // Valida antes do bind: as colunas __m_* precisam existir quando o grid gerar as colunas.
                BuildEpicFilter();
                ValidateAgainstSchedule();
                BindTable();
                ApplyEpicFilter();
                ClearUndo();
                _settings.LastFile = path;
                TaskPlanSettingsStore.Save(_settings);
                StatusText.Foreground = System.Windows.Media.Brushes.Green;
                StatusText.Text = AppStrings.Get("TaskPlan_Loaded", _data.Table.Rows.Count);

                // Log de sincronização pendente (na pasta do Excel): aplica os IDs e limpa.
                ApplyPendingSyncLog(path);
            }
            catch (Exception ex)
            {
                // Biblioteca ausente (dependência nova sem o Setup atualizado): mensagem própria.
                if (CommunityApp.ShowMissingLibraryMessage(ex)) return;
                MessageBox.Show(this, AppStrings.Get("TaskPlan_LoadError", ex.Message),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Colunas vinculadas ao cronograma: sempre existem (criadas se faltarem) e não podem ser excluídas.
        private const string ApprovalColumn = "Aprovada";
        private const string RegisterDateColumn = "DT_Registro";
        private const string PercConclusaoColumn = "Perc_Conclusao";
        private static readonly string[] ApprovalValues = ["Nao", "Sim"];
        private static readonly string[] ScheduleColumns = { ApprovalColumn, RegisterDateColumn, "EPIC", "Feature", "ID Feature", "Story", "ID Story", "Task", "ID Task", "Prioridade", "Estimado HH", PercConclusaoColumn, "Status" };

        // Colunas de ID dos pais (preenchidas pelo Buscar/Merge/Aplicar/Ctrl+clique).
        private string? FeatureIdCol => FindColumn("ID Feature", "IdFeature", "ID_Feature");
        private string? StoryIdCol   => FindColumn("ID Story", "IdStory", "ID_Story");
        private string? ApprovalCol  => FindColumn(ApprovalColumn, "Aprovado", "Aprovacao", "Aprovação", "Approved");
        private string? RegisterDateCol => FindColumn(RegisterDateColumn, "DT Registro", "Data Registro",
            "Data de Registro", "Data de Inclusao", "Data de Inclusão", "Data Inclusao", "Register Date", "Registered");
        private string? PercConclusaoCol => FindColumn(PercConclusaoColumn, "Perc Conclusao", "Perc_Conclusão",
            "% Conclusão", "% Conclusao", "Percentual Conclusao", "Percent Complete", "PercentComplete");

        /// <summary>Data curta na cultura atual (pt-BR: dd/MM/yyyy; en-US: MM/dd/yyyy).</summary>
        private static string FormatRegisterDate(DateTime date) =>
            date.ToString("d", System.Globalization.CultureInfo.CurrentCulture);

        private static string DisplayIdOf(ProjectTask t) => t.TfsId is > 0 ? $"{t.TfsId.Value}:T" : $"{t.Id}:I";

        // Nó ancestral (não só o nome) — para preencher os IDs de Feature/Story.
        private static ProjectTask? AncestorNode(ProjectTask task, string tfsType)
        {
            for (var p = task; p != null; p = p.Parent)
                if (IsType(p, tfsType))
                    return p;
            return null;
        }

        private bool IsScheduleColumn(string colName)
            => HierarchyType(colName) != null
               || colName == FindColumn("ID Devops", "ID DevOps", "IdDevops", "ID_Devops", "ID Dev Ops", "ID Task", "IdTask", "ID_Task")
               || colName == FeatureIdCol
               || colName == StoryIdCol
               || colName == ApprovalCol
               || colName == RegisterDateCol
               || colName == PercConclusaoCol
               || colName == FindColumn("Prioridade", "Priority", "Prio", "Prioridade Task")
               || colName == FindColumn("Estimado HH", "Estimado", "Estimativa", "HH Estimado", "Estimated", "HH")
               || colName == FindColumn("Status", "Estado", "State");

        // Registra no serviço quais colunas têm nome/posição fixos (vinculadas ao cronograma).
        private void UpdateFixedColumns()
        {
            if (_data == null) return;
            _data.FixedNameColumns.Clear();
            foreach (var col in _data.Table.Columns.Cast<DataColumn>())
                if (!col.ColumnName.StartsWith("__", StringComparison.Ordinal) && IsScheduleColumn(col.ColumnName))
                    _data.FixedNameColumns.Add(col.ColumnName);
        }

        // Leva a ordem visual da grade (arrastar cabeçalhos) para a DataTable antes de salvar.
        private void SyncColumnOrderFromGrid()
        {
            if (_data == null || PlanGrid.Columns.Count == 0) return;
            var ordered = PlanGrid.Columns
                .OrderBy(c => c.DisplayIndex)
                .Select(c => c.Header?.ToString() ?? "")
                .Where(n => !n.StartsWith("__", StringComparison.Ordinal) && _data.Table.Columns.Contains(n))
                .ToList();
            for (int i = 0; i < ordered.Count; i++)
                _data.Table.Columns[ordered[i]]!.SetOrdinal(i);
        }

        // Mesmos estados da grid de Tasks (TechLeadTaskReviewWindow) — facilita a sincronização.
        private static readonly List<string> KnownStates = ["New", "Active", "Resolved", "Closed", "Blocked"];

        // Renomeia uma coluna exata para o nome canônico, preservando posição/mapa (migração).
        private void RenameColumnPreservingPosition(string oldName, string newName)
        {
            if (_data == null) return;
            if (!_data.Table.Columns.Contains(oldName) || _data.Table.Columns.Contains(newName)) return;
            _data.Table.Columns[oldName]!.ColumnName = newName;
            if (_data.ColumnSheetMap.TryGetValue(oldName, out var sheetIdx))
            {
                _data.ColumnSheetMap.Remove(oldName);
                _data.ColumnSheetMap[newName] = sheetIdx;
            }
            if (_data.AppendedColumns.Remove(oldName))
                _data.AppendedColumns.Add(newName);
            _dirty = true;
        }

        // Garante as colunas do cronograma no plano aberto (cria com o nome canônico se faltar).
        private void EnsureScheduleColumns()
        {
            if (_data == null) return;

            // Migra a coluna legada "Concluída (X)" para "Status" (estados do DevOps):
            // X → Closed; mantém a posição física na planilha.
            var statusExisting = FindColumn("Status", "Estado", "State");
            var doneCol = _data.Table.Columns.Cast<DataColumn>().Select(c => c.ColumnName)
                .FirstOrDefault(n => n.Trim().StartsWith("Concluída", StringComparison.OrdinalIgnoreCase)
                                  || n.Trim().StartsWith("Concluida", StringComparison.OrdinalIgnoreCase));
            if (doneCol != null && statusExisting == null)
            {
                foreach (DataRow row in _data.Table.Rows)
                {
                    var v = row[doneCol]?.ToString()?.Trim() ?? "";
                    row[doneCol] = string.IsNullOrEmpty(v) ? "" : "Closed";
                }
                _data.Table.Columns[doneCol]!.ColumnName = "Status";
                if (_data.ColumnSheetMap.TryGetValue(doneCol, out var sheetIdx))
                {
                    _data.ColumnSheetMap.Remove(doneCol);
                    _data.ColumnSheetMap["Status"] = sheetIdx;
                }
                if (_data.AppendedColumns.Remove(doneCol))
                    _data.AppendedColumns.Add("Status");
                _dirty = true;
            }

            // Renomeia a coluna legada "Estimado" para "Estimado HH" (mantém a posição).
            RenameColumnPreservingPosition("Estimado", "Estimado HH");
            EnsureApprovalColumn();
            EnsureRegisterDateColumn();

            string?[] found =
            {
                ApprovalCol,
                RegisterDateCol,
                _data.Table.Columns.Cast<DataColumn>().Select(c => c.ColumnName)
                    .FirstOrDefault(n => n.Trim().StartsWith("EPIC", StringComparison.OrdinalIgnoreCase)
                                      || n.Trim().StartsWith("Épic", StringComparison.OrdinalIgnoreCase)),
                FindColumn("Feature", "Nome da Feature"),
                FeatureIdCol,
                FindColumn("Story", "Nome da Story"),
                StoryIdCol,
                FindColumn("Task", "Tarefa", "Nome da Task"),
                FindColumn("ID Devops", "ID DevOps", "IdDevops", "ID_Devops", "ID Dev Ops", "ID Task", "IdTask", "ID_Task"),
                FindColumn("Prioridade", "Priority", "Prio", "Prioridade Task"),
                FindColumn("Estimado HH", "Estimado", "Estimativa", "HH Estimado", "Estimated", "HH"),
                PercConclusaoCol,
                FindColumn("Status", "Estado", "State"),
            };
            for (int i = 0; i < ScheduleColumns.Length; i++)
                if (found[i] == null && !_data.Table.Columns.Contains(ScheduleColumns[i]))
                {
                    _data.Table.Columns.Add(ScheduleColumns[i], typeof(string));
                    _dirty = true;   // coluna nova será gravada no .xlsx no próximo salvar
                }

            // Posição na visão: ID Feature logo após a Feature; ID Story logo após a Story.
            void PlaceAfter(string? idCol, string? parentCol)
            {
                if (idCol == null || parentCol == null) return;
                if (!_data.Table.Columns.Contains(idCol) || !_data.Table.Columns.Contains(parentCol)) return;
                var target = Math.Min(_data.Table.Columns[parentCol]!.Ordinal + 1, _data.Table.Columns.Count - 1);
                _data.Table.Columns[idCol]!.SetOrdinal(target);
            }
            PlaceAfter(FeatureIdCol, FindColumn("Feature", "Nome da Feature"));
            PlaceAfter(StoryIdCol, FindColumn("Story", "Nome da Story"));
            _data.Table.Columns[ApprovalCol!]!.SetOrdinal(0);
            if (RegisterDateCol is { } rc) _data.Table.Columns[rc]!.SetOrdinal(1);
            NormalizeApprovalValues();
        }

        private void EnsureApprovalColumn()   => EnsureLeadingControlColumn(ApprovalColumn, ApprovalCol, 0);
        private void EnsureRegisterDateColumn() => EnsureLeadingControlColumn(RegisterDateColumn, RegisterDateCol, 1);

        /// <summary>Garante uma coluna de controle fixa no início (posição de visão
        /// <paramref name="viewOrdinal"/>). Se a planilha já tem colunas físicas e esta é
        /// nova, agenda a inserção de uma coluna física em branco na posição correspondente
        /// e reindexa o mapa — assim ela nasce no início do .xlsx, não no fim com prefixo.</summary>
        private void EnsureLeadingControlColumn(string canonical, string? existing, int viewOrdinal)
        {
            if (_data == null) return;
            if (existing != null)
            {
                RenameColumnPreservingPosition(existing, canonical);
                _data.Table.Columns[canonical]!.SetOrdinal(viewOrdinal);
                return;
            }

            _data.Table.Columns.Add(canonical, typeof(string));
            _data.Table.Columns[canonical]!.SetOrdinal(viewOrdinal);
            if (_data.ColumnSheetMap.Count > 0)
            {
                int pos = viewOrdinal + 1;   // posição física (1-based) desejada no início
                foreach (var key in _data.ColumnSheetMap.Keys.ToList())
                    if (_data.ColumnSheetMap[key] >= pos)
                        _data.ColumnSheetMap[key]++;
                _data.ColumnSheetMap[canonical] = pos;
                _data.InsertBlankLeadingColumnsOnSave.Add(pos);
            }
            _dirty = true;
        }

        private static bool IsApprovalYes(string? value)
        {
            var v = (value ?? "").Trim();
            return v.Equals("Sim", StringComparison.OrdinalIgnoreCase)
                || v.Equals("S", StringComparison.OrdinalIgnoreCase)
                || v.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                || v.Equals("Y", StringComparison.OrdinalIgnoreCase)
                || v.Equals("True", StringComparison.OrdinalIgnoreCase)
                || v.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsApproved(DataRow row)
            => ApprovalCol is { } col && IsApprovalYes(row[col]?.ToString());

        private bool IsApprovedOrAlreadyTfs(DataRow row, string idCol)
        {
            if ((row[idCol]?.ToString()?.Trim() ?? "").EndsWith(":T", StringComparison.OrdinalIgnoreCase))
            {
                if (ApprovalCol is { } ac && !IsApprovalYes(row[ac]?.ToString()))
                {
                    row[ac] = "Sim";
                    _dirty = true;
                }
                return true;
            }
            return IsApproved(row);
        }

        private void NormalizeApprovalValues()
        {
            if (_data == null) return;
            EnsureRegisterDates();
            if (ApprovalCol is not { } approvalCol) return;
            var idCol = FindColumn("ID Devops", "ID DevOps", "IdDevops", "ID_Devops", "ID Dev Ops", "ID Task", "IdTask", "ID_Task");
            foreach (DataRow row in _data.Table.Rows)
            {
                var id = idCol != null ? row[idCol]?.ToString()?.Trim() ?? "" : "";
                var target = id.EndsWith(":T", StringComparison.OrdinalIgnoreCase)
                    ? "Sim"
                    : IsApprovalYes(row[approvalCol]?.ToString()) ? "Sim" : "Nao";
                if (!string.Equals(row[approvalCol]?.ToString(), target, StringComparison.Ordinal))
                {
                    row[approvalCol] = target;
                    _dirty = true;
                }
            }
        }

        /// <summary>Carimba a data de inclusão (hoje) nas linhas com Task e sem DT_Registro.
        /// As linhas :T aprovadas recebem a data real de criação do TFS no Load Task.</summary>
        private void EnsureRegisterDates()
        {
            if (_data == null || RegisterDateCol is not { } regCol) return;
            var taskCol = FindColumn("Task", "Tarefa", "Nome da Task");
            if (taskCol == null) return;
            var today = FormatRegisterDate(DateTime.Today);
            foreach (DataRow row in _data.Table.Rows)
            {
                if (string.IsNullOrWhiteSpace(row[taskCol]?.ToString())) continue;
                if (!string.IsNullOrWhiteSpace(row[regCol]?.ToString())) continue;
                row[regCol] = today;
                _dirty = true;
            }
        }

        // Monta o plano a partir do cronograma aberto (quando não há Excel para abrir).
        private void BuildFromSchedule()
        {
            if (_vm?.Project == null || _vm.Project.Tasks.Count == 0) return;

            var table = new DataTable();
            string[] cols = { ApprovalColumn, RegisterDateColumn, "EPIC", "Feature", "ID Feature", "Story", "ID Story", "Task", "ID Task", "Prioridade", "Estimado HH", PercConclusaoColumn, "Recurso", "Status", "Descrição da Task", "Observações" };
            foreach (var c in cols) table.Columns.Add(c, typeof(string));

            foreach (var t in Flatten(_vm.Project.Tasks).Where(t => IsType(t, "Task")))
            {
                var dr = table.NewRow();
                dr["EPIC"]    = Ancestor(t, "Epic");
                dr["Feature"] = Ancestor(t, "Feature");
                dr["ID Feature"] = AncestorNode(t, "Feature") is { } fn ? DisplayIdOf(fn) : "";
                dr["Story"]   = Ancestor(t, "Story");
                dr["ID Story"] = AncestorNode(t, "Story") is { } sn ? DisplayIdOf(sn) : "";
                dr["Task"]    = t.Name ?? "";
                dr["ID Task"]  = t.TfsId is > 0 ? $"{t.TfsId.Value}:T" : $"{t.Id}:I";
                dr[ApprovalColumn] = t.TfsId is > 0 ? "Sim" : "Nao";
                dr[RegisterDateColumn] = FormatRegisterDate(DateTime.Today);
                dr["Prioridade"] = t.Priority?.ToString() ?? "";
                dr["Estimado HH"] = t.EstimatedHours is > 0 ? t.EstimatedHours.Value.ToString("0.##") : "1";
                dr[PercConclusaoColumn] = Math.Round(t.PercentComplete).ToString("0");
                dr["Status"]     = t.TfsState ?? "";
                dr["Recurso"]    = string.Join(", ", t.Resources
                    .Select(r => r.Resource?.Name ?? "")
                    .Where(n => n.Length > 0));
                dr["Descrição da Task"] = t.Description ?? "";
                table.Rows.Add(dr);
            }

            _data = new TaskPlanData
            {
                Table = table,
                SheetName = "Tarefas",
                HeaderRow = 1
            };
            for (int i = 0; i < cols.Length; i++)
                _data.ColumnSheetMap[cols[i]] = i + 1;
            _path = null;
            SetPathDisplay(null);
            PathText.Text = AppStrings.Get("TaskPlan_FromSchedule");
            UpdateFixedColumns();
            BuildEpicFilter();
            ValidateAgainstSchedule();
            BindTable();
            ApplyEpicFilter();
            _dirty = true;   // ainda não existe .xlsx — salvar pedirá o arquivo
            ClearUndo();
            StatusText.Foreground = System.Windows.Media.Brushes.Green;
            StatusText.Text = AppStrings.Get("TaskPlan_Loaded", table.Rows.Count);
        }

        // Novo plano: vazio, do cronograma, ou do cronograma + Tasks do TFS.
        private async void OnNewClick(object sender, RoutedEventArgs e)
        {
            if (_dirty)
            {
                var r = MessageBox.Show(this, AppStrings.Get("TaskPlan_ConfirmSave"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes && !TrySave()) return;
            }

            var choice = AskNewPlanSource();
            if (choice == null) return;

            switch (choice)
            {
                case "schedule":
                    if (!HasSchedule()) return;
                    BuildFromSchedule();
                    break;
                case "tfs":
                    if (!HasSchedule()) return;
                    await BuildFromScheduleWithTfsAsync();
                    break;
                default:
                    BuildEmptyPlan();
                    break;
            }
        }

        // Carregar todo o cronograma (EPIC/Feature/Story + Tasks do TFS) em um arquivo novo.
        private async void OnLoadAllClick(object sender, RoutedEventArgs e)
        {
            if (_dirty)
            {
                var r = MessageBox.Show(this, AppStrings.Get("TaskPlan_ConfirmSave"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes && !TrySave()) return;
            }
            if (!HasSchedule()) return;
            await BuildFromScheduleWithTfsAsync();
        }

        private bool HasSchedule()
        {
            if (_vm?.Project != null && _vm.Project.Tasks.Count > 0) return true;
            MessageBox.Show(this, AppStrings.Get("TaskPlan_NoSchedule"),
                AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        // Escolha da base do plano novo: vazio / cronograma / cronograma + Tasks do TFS.
        private string? AskNewPlanSource()
        {
            var dlg = new Window
            {
                Title = AppStrings.Get("TaskPlan_New"),
                Owner = this,
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.White
            };
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = AppStrings.Get("TaskPlan_NewChoiceMsg"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            string? result = null;
            Button MakeOption(string key, string tag)
            {
                var b = new Button
                {
                    Content = AppStrings.Get(key),
                    Height = 32,
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 0, 10, 0)
                };
                b.Click += (_, _) => { result = tag; dlg.DialogResult = true; };
                return b;
            }
            panel.Children.Add(MakeOption("TaskPlan_NewEmpty", "empty"));
            panel.Children.Add(MakeOption("TaskPlan_NewFromSchedule", "schedule"));
            panel.Children.Add(MakeOption("TaskPlan_NewFromScheduleTfs", "tfs"));
            var cancel = new Button { Content = AppStrings.Get("Pred_Cancel"), Width = 96, Height = 28, HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true, Margin = new Thickness(0, 8, 0, 0) };
            panel.Children.Add(cancel);
            dlg.Content = panel;
            return dlg.ShowDialog() == true ? result : null;
        }

        private void BuildEmptyPlan()
        {
            var table = new DataTable();
            string[] cols = { ApprovalColumn, RegisterDateColumn, "EPIC", "Feature", "ID Feature", "Story", "ID Story", "Task", "ID Task", "Prioridade", "Estimado HH", PercConclusaoColumn, "Recurso", "Status", "Descrição da Task", "Observações" };
            foreach (var c in cols) table.Columns.Add(c, typeof(string));

            _data = new TaskPlanData { Table = table, SheetName = "Tarefas", HeaderRow = 1 };
            for (int i = 0; i < cols.Length; i++)
                _data.ColumnSheetMap[cols[i]] = i + 1;

            _path = null;
            SetPathDisplay(null);
            PathText.Text = AppStrings.Get("TaskPlan_NewFile");
            _columnFilters.Clear();
            UpdateFixedColumns();
            BuildEpicFilter();
            ValidateAgainstSchedule();
            BindTable();
            ApplyEpicFilter();
            _dirty = false;
            ClearUndo();
            StatusText.Foreground = System.Windows.Media.Brushes.Green;
            StatusText.Text = AppStrings.Get("TaskPlan_NewCreated");
        }

        // Cronograma + Tasks reais do TFS (por Story), com barra de progresso — base consistente.
        private async Task BuildFromScheduleWithTfsAsync()
        {
            BuildFromSchedule();   // base: hierarquia e tasks já presentes no cronograma
            if (_data == null) return;

            var options = TfsConnectionStore.Load("NXProject.Community");
            if (string.IsNullOrWhiteSpace(options.OrganizationUrl) || string.IsNullOrWhiteSpace(options.PersonalAccessToken))
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_PickNoDevOps"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var stories = Flatten(_vm!.Project.Tasks)
                .Where(t => IsType(t, "Story") && t.TfsId is > 0)
                .ToList();
            var existingIds = _data.Table.Rows.Cast<DataRow>()
                .Select(r => r["ID Task"]?.ToString()?.Trim() ?? "")
                .Where(v => v.EndsWith(":T", StringComparison.OrdinalIgnoreCase))
                .Select(v => int.TryParse(v[..^2], out var n) ? n : 0)
                .ToHashSet();

            int added = 0;
            MergeProgress.Visibility = Visibility.Visible;
            MergeProgress.IsIndeterminate = false;
            MergeProgress.Maximum = Math.Max(1, stories.Count);
            MergeProgress.Value = 0;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                for (int i = 0; i < stories.Count; i++)
                {
                    var story = stories[i];
                    StatusText.Foreground = System.Windows.Media.Brushes.Gray;
                    StatusText.Text = AppStrings.Get("TaskPlan_MergeStepStory", i + 1, stories.Count, story.Name ?? "");
                    var children = await TfsImportService.FetchChildTasksFromDevOpsAsync(options, story.TfsId!.Value);
                    MergeProgress.Value = i + 1;
                    if (children == null) continue;

                    foreach (var t in children)
                    {
                        if (existingIds.Contains(t.TfsId)) continue;
                        var dr = _data.Table.NewRow();
                        dr["EPIC"]    = Ancestor(story, "Epic");
                        dr["Feature"] = Ancestor(story, "Feature");
                        dr["ID Feature"] = AncestorNode(story, "Feature") is { } fn ? DisplayIdOf(fn) : "";
                        dr["Story"]   = story.Name ?? "";
                        dr["ID Story"] = DisplayIdOf(story);
                        dr["Task"]    = t.Title;
                        dr["ID Task"]  = $"{t.TfsId}:T";
                        dr[ApprovalColumn] = "Sim";
                        dr[RegisterDateColumn] = FormatRegisterDate(t.CreatedDate ?? DateTime.Today);
                        dr["Prioridade"] = t.Priority.ToString();
                        dr["Estimado HH"] = t.EstimatedHours > 0 ? t.EstimatedHours.ToString("0.##") : "1";
                        dr[PercConclusaoColumn] = Math.Round(t.PercentComplete).ToString("0");
                        dr["Status"]     = t.State ?? "";
                        _data.Table.Rows.Add(dr);
                        existingIds.Add(t.TfsId);
                        added++;
                    }
                }
            }
            finally
            {
                MergeProgress.Visibility = Visibility.Collapsed;
                System.Windows.Input.Mouse.OverrideCursor = null;
            }

            BuildEpicFilter();
            ValidateAgainstSchedule();
            PlanGrid.Items.Refresh();
            StatusText.Foreground = System.Windows.Media.Brushes.Green;
            StatusText.Text = AppStrings.Get("TaskPlan_NewTfsDone", _data.Table.Rows.Count, added);
        }

        private void OnOpenClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = AppStrings.Get("TaskPlan_Open"),
                Filter = ExcelTaskPlanService.FileFilter,
                InitialDirectory = DialogFolder,
                // URLs (ex.: SharePoint) não devem ser validadas pelo Windows (WebDAV/Acesso
                // Negado) — nós mesmos tratamos abaixo com orientação amigável.
                CheckFileExists = false,
                ValidateNames = false
            };
            if (dlg.ShowDialog(this) != true) return;

            var chosen = dlg.FileName?.Trim() ?? "";
            if (IsWebUrl(chosen))
            {
                ShowSharePointUrlGuidance();
                return;
            }
            if (!File.Exists(chosen))
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_LoadError", chosen),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            AskProjectAssociation(chosen);
            LoadFile(chosen);
        }

        // Pergunta a qual projeto (cronograma) a planilha pertence e grava a associação na
        // configuração local — assim, ao abrir esse projeto, a planilha certa já aparece.
        private void AskProjectAssociation(string path)
        {
            var known = _settings.ProjectFiles.Keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var current = _vm?.Project?.Name?.Trim();
            if (!string.IsNullOrEmpty(current) && !known.Contains(current, StringComparer.OrdinalIgnoreCase))
                known.Insert(0, current!);
            if (known.Count == 0) return;   // sem projeto aberto nem lista: nada a associar

            var dlg = new Window
            {
                Title = AppStrings.Get("TaskPlan_AssocTitle"),
                Owner = this,
                Width = 420, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                Text = AppStrings.Get("TaskPlan_AssocProject"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            var combo = new ComboBox { IsEditable = true, ItemsSource = known, Height = 28 };
            combo.Text = current ?? known[0];
            panel.Children.Add(combo);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var ok = new Button { Content = "OK", Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var skip = new Button { Content = AppStrings.Get("TaskPlan_AssocSkip"), Width = 110, Height = 28, IsCancel = true };
            ok.Click += (_, _) => dlg.DialogResult = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(skip);
            panel.Children.Add(buttons);
            dlg.Content = panel;

            if (dlg.ShowDialog() != true) return;
            var name = combo.Text?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            _settings.SetProjectFile(name!, path);
            TaskPlanSettingsStore.Save(_settings);
            // Se for o projeto aberto, persiste também no .nxp (backfill/associação existente).
            if (_vm?.Project != null && string.Equals(name, _vm.Project.Name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _vm.Project.PlanSheetPath = path;
                _vm.Project.IsDirty = true;
            }
        }

        // Endereço web (ex.: https://...sharepoint.com/...) colado no diálogo Abrir.
        private static bool IsWebUrl(string path)
            => path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || path.Contains(".sharepoint.com", StringComparison.OrdinalIgnoreCase);

        // Orientação para arquivos no SharePoint: sincronizar via OneDrive (recomendado)
        // ou aguardar a integração direta (Entra ID + Graph) das configurações.
        private void ShowSharePointUrlGuidance()
        {
            var r = MessageBox.Show(this, AppStrings.Get("TaskPlan_SharePointUrlMsg"),
                AppStrings.Get("TaskPlan_Title"), MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://support.microsoft.com/pt-br/office/sincronizar-arquivos-do-sharepoint-e-do-teams-com-seu-computador-6de9ede8-5b6e-4503-80b2-6190f3354a88")
                {
                    UseShellExecute = true
                });
            }
        }

        private void OnReloadClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_path)) LoadFile(_path);
        }

        private void OnSaveClick(object sender, RoutedEventArgs e) => TrySave();

        private bool TrySave()
        {
            if (_data == null) return false;
            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            NormalizeApprovalValues();
            // A ordem visual das colunas (arrastadas na grade) vale para a gravação.
            SyncColumnOrderFromGrid();
            UpdateFixedColumns();

            // Check de hierarquia antes de gravar: com cronograma aberto, células de
            // EPIC/Feature/Story/Task preenchidas mas não validadas (sem verde) pedem confirmação.
            if (_vm?.Project != null && _vm.Project.Tasks.Count > 0)
            {
                ValidateAgainstSchedule();
                int invalid = 0;
                var hierCols = _data.Table.Columns.Cast<DataColumn>()
                    .Select(c => c.ColumnName)
                    .Where(n => !n.StartsWith("__", StringComparison.Ordinal)
                             && HierarchyType(n) != null
                             && _data.Table.Columns.Contains(MatchColPrefix + n))
                    .ToList();
                foreach (DataRow dr in _data.Table.Rows)
                    foreach (var col in hierCols)
                        if (dr[MatchColPrefix + col]?.ToString() == "0")
                            invalid++;
                if (invalid > 0)
                {
                    var r = MessageBox.Show(this, AppStrings.Get("TaskPlan_SaveInvalidConfirm", invalid),
                        AppStrings.Get("TaskPlan_Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (r != MessageBoxResult.Yes) return false;
                }
            }

            // Plano gerado do cronograma (sem arquivo): pergunta onde criar o .xlsx.
            if (string.IsNullOrEmpty(_path))
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = AppStrings.Get("TaskPlan_Save"),
                    Filter = ExcelTaskPlanService.FileFilter,
                    FileName = "Plano de Tasks.xlsx",
                    InitialDirectory = DialogFolder
                };
                if (dlg.ShowDialog(this) != true) return false;
                try
                {
                    ExcelTaskPlanService.CreateNew(dlg.FileName, _data);
                    _path = dlg.FileName;
                    SetPathDisplay(_path);
                    // Mapeia as colunas para o arquivo recém-criado (ordem da tabela, linha 1).
                    _data.HeaderRow = 1;
                    _data.ColumnSheetMap.Clear();
                    _data.RemovedSheetColumns.Clear();
                    _data.AppendedColumns.Clear();   // arquivo novo: colunas já na posição real
                    int sheetCol = 1;
                    foreach (DataColumn c in _data.Table.Columns)
                        if (!c.ColumnName.StartsWith("__", StringComparison.Ordinal))
                            _data.ColumnSheetMap[c.ColumnName] = sheetCol++;
                    _settings.LastFile = _path;
                    TaskPlanSettingsStore.Save(_settings);
                    _dirty = false;
                    StatusText.Foreground = System.Windows.Media.Brushes.Green;
                    StatusText.Text = AppStrings.Get("TaskPlan_Saved");
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, AppStrings.Get("TaskPlan_SaveError", ex.Message),
                        AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }

            if (ExcelTaskPlanService.IsLockedForWrite(_path))
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_Locked"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            try
            {
                ExcelTaskPlanService.Save(_path, _data);
                _dirty = false;
                StatusText.Foreground = System.Windows.Media.Brushes.Green;
                StatusText.Text = AppStrings.Get("TaskPlan_Saved");
                return true;
            }
            catch (IOException)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_Locked"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_SaveError", ex.Message),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return false;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        // ── configurações (pasta padrão + SharePoint) ────────────────────────
        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Window
            {
                Title = AppStrings.Get("TaskPlan_Settings"),
                Owner = this,
                Width = 520,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.White
            };
            var panel = new StackPanel { Margin = new Thickness(16) };

            // Pasta padrão (usada pelos diálogos Abrir/Salvar)
            panel.Children.Add(new TextBlock
            {
                Text = AppStrings.Get("TaskPlan_DefaultFolder"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var folderRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var browse = new Button
            {
                Content = AppStrings.Get("TaskPlan_Browse"),
                Width = 100, Height = 28, Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(browse, Dock.Right);
            var folderBox = new TextBox
            {
                Text = _settings.DefaultFolder,
                Height = 28, Padding = new Thickness(6, 4, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            browse.Click += (_, _) =>
            {
                var fd = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = AppStrings.Get("TaskPlan_DefaultFolder"),
                    InitialDirectory = Directory.Exists(folderBox.Text) ? folderBox.Text : null
                };
                if (fd.ShowDialog(dlg) == true)
                    folderBox.Text = fd.FolderName;
            };
            folderRow.Children.Add(browse);
            folderRow.Children.Add(folderBox);
            panel.Children.Add(folderRow);

            // SharePoint (Entra ID + Graph) — configuração preparada; integração futura.
            var spGroup = new GroupBox
            {
                Header = AppStrings.Get("TaskPlan_SharePointGroup"),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var spPanel = new StackPanel();

            TextBox AddField(string labelKey, string value)
            {
                spPanel.Children.Add(new TextBlock { Text = AppStrings.Get(labelKey), Margin = new Thickness(0, 6, 0, 2), FontSize = 12 });
                var box = new TextBox { Text = value, Height = 26, Padding = new Thickness(4, 2, 4, 2) };
                spPanel.Children.Add(box);
                return box;
            }
            // Projeto da URL/pasta: a URL do SharePoint é POR projeto/cronograma;
            // o service principal (tenant/client) é o mesmo para todos.
            var knownProjects = _settings.ProjectFiles.Keys
                .Concat(_settings.ProjectSharePointUrls.Keys)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var openProject = _vm?.Project?.Name?.Trim();
            if (!string.IsNullOrEmpty(openProject) && !knownProjects.Contains(openProject, StringComparer.OrdinalIgnoreCase))
                knownProjects.Insert(0, openProject!);
            spPanel.Children.Add(new TextBlock { Text = AppStrings.Get("TaskPlan_AssocTitle"), Margin = new Thickness(0, 6, 0, 2), FontSize = 12 });
            var spProject = new ComboBox { IsEditable = true, ItemsSource = knownProjects, Height = 26 };
            spProject.Text = openProject ?? knownProjects.FirstOrDefault() ?? "";
            spPanel.Children.Add(spProject);

            var spUrl = AddField("TaskPlan_SpUrl",
                _settings.GetProjectSharePointUrl(spProject.Text) ?? _settings.SharePointUrl);

            // Troca de projeto na combo: guarda a URL digitada e carrega a do projeto escolhido.
            var editedUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lastProject = spProject.Text;
            spProject.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!string.IsNullOrWhiteSpace(lastProject))
                    editedUrls[lastProject.Trim()] = spUrl.Text?.Trim() ?? "";
                var sel = spProject.Text?.Trim() ?? "";
                spUrl.Text = editedUrls.TryGetValue(sel, out var pending)
                    ? pending
                    : _settings.GetProjectSharePointUrl(sel) ?? "";
                lastProject = sel;
            }), System.Windows.Threading.DispatcherPriority.Background);

            var spTenant = AddField("TaskPlan_SpTenant", _settings.SharePointTenantId);
            var spClient = AddField("TaskPlan_SpClient", _settings.SharePointClientId);

            // Guia didático: como usar a planilha do SharePoint (sincronizada via OneDrive).
            var spHelpBtn = new Button
            {
                Content = AppStrings.Get("TaskPlan_SpHelpButton"),
                Height = 26, Padding = new Thickness(10, 0, 10, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0)
            };
            spHelpBtn.Click += (_, _) => ShowSharePointHowTo();
            spPanel.Children.Add(spHelpBtn);
            spPanel.Children.Add(new TextBlock
            {
                Text = AppStrings.Get("TaskPlan_SpNote"),
                FontSize = 11,
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });

            // Link de ajuda: como registrar o App (public client/MSAL) no Azure.
            var helpLink = new TextBlock { Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
            var link = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(AppStrings.Get("TaskPlan_SpHelp")))
            {
                NavigateUri = new Uri("https://learn.microsoft.com/entra/identity-platform/quickstart-register-app")
            };
            link.RequestNavigate += (_, args) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri.ToString())
                {
                    UseShellExecute = true
                });
            };
            helpLink.Inlines.Add(link);
            spPanel.Children.Add(helpLink);
            spGroup.Content = spPanel;
            panel.Children.Add(spGroup);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var ok = new Button { Content = "OK", Width = 96, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = AppStrings.Get("Pred_Cancel"), Width = 96, Height = 30, IsCancel = true };
            ok.Click += (_, _) => { dlg.DialogResult = true; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            dlg.Content = panel;
            if (dlg.ShowDialog() != true) return;

            _settings.DefaultFolder = folderBox.Text?.Trim() ?? "";
            // URL do SharePoint é por projeto (combo); tenant/client são globais.
            if (!string.IsNullOrWhiteSpace(spProject.Text))
                editedUrls[spProject.Text.Trim()] = spUrl.Text?.Trim() ?? "";
            foreach (var kv in editedUrls)
                _settings.SetProjectSharePointUrl(kv.Key, kv.Value);
            _settings.SharePointTenantId = spTenant.Text?.Trim() ?? "";
            _settings.SharePointClientId = spClient.Text?.Trim() ?? "";
            TaskPlanSettingsStore.Save(_settings);
            StatusText.Foreground = System.Windows.Media.Brushes.Green;
            StatusText.Text = AppStrings.Get("TaskPlan_SettingsSaved");
        }

        // Guia passo a passo: usar a planilha do SharePoint sincronizada via OneDrive.
        private void ShowSharePointHowTo()
        {
            var win = new Window
            {
                Title = AppStrings.Get("TaskPlan_SpHelpTitle"),
                Owner = this,
                Width = 560, Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(18)
            };
            scroll.Content = new TextBlock
            {
                Text = AppStrings.Get("TaskPlan_SpHelpText"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                LineHeight = 20
            };
            win.Content = scroll;
            win.ShowDialog();
        }

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (!_dirty) return;

            var r = MessageBox.Show(this, AppStrings.Get("TaskPlan_ConfirmSave"),
                AppStrings.Get("TaskPlan_Title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel)
                e.Cancel = true;
            else if (r == MessageBoxResult.Yes && !TrySave())
                e.Cancel = true;   // não conseguiu salvar (ex.: arquivo aberto no Excel) → não fecha
        }

        // ── filtro por EPIC ──────────────────────────────────────────────────
        private void BuildEpicFilter()
        {
            _epicColumn = _data?.Table.Columns.Cast<DataColumn>()
                .FirstOrDefault(c => c.ColumnName.Trim().StartsWith("EPIC", StringComparison.OrdinalIgnoreCase)
                                  || c.ColumnName.Trim().StartsWith("Épic", StringComparison.OrdinalIgnoreCase))?.ColumnName;

            _suppressEpic = true;
            var items = new List<string> { AppStrings.Get("TaskPlan_EpicAll") };
            bool hasEpic = _epicColumn != null && _data != null;
            if (hasEpic)
            {
                items.AddRange(_data!.Table.Rows.Cast<DataRow>()
                    .Select(r => r[_epicColumn!]?.ToString()?.Trim() ?? "")
                    .Where(v => !string.IsNullOrEmpty(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase));
            }
            EpicFilterCombo.ItemsSource = items;
            EpicFilterCombo.SelectedIndex = 0;
            EpicFilterCombo.IsEnabled = hasEpic;
            _suppressEpic = false;
            BuildFeatureFilter();
            ApplyEpicFilter();
        }

        // Combo de Feature: "Todos" mostra a coluna; uma Feature específica filtra e oculta.
        // A lista respeita o EPIC selecionado.
        private void BuildFeatureFilter()
        {
            var featureCol = FindColumn("Feature", "Nome da Feature");
            _suppressEpic = true;
            var items = new List<string> { AppStrings.Get("TaskPlan_EpicAll") };
            bool hasFeature = featureCol != null && _data != null;
            if (hasFeature)
            {
                var allLabel = AppStrings.Get("TaskPlan_EpicAll");
                var epicSel = EpicFilterCombo.SelectedItem as string;
                var rows = _data!.Table.Rows.Cast<DataRow>();
                if (_epicColumn != null && !string.IsNullOrEmpty(epicSel) && epicSel != allLabel)
                    rows = rows.Where(r => string.Equals(r[_epicColumn]?.ToString()?.Trim(), epicSel, StringComparison.OrdinalIgnoreCase));
                items.AddRange(rows
                    .Select(r => r[featureCol!]?.ToString()?.Trim() ?? "")
                    .Where(v => !string.IsNullOrEmpty(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase));
            }
            FeatureFilterCombo.ItemsSource = items;
            FeatureFilterCombo.SelectedIndex = 0;
            FeatureFilterCombo.IsEnabled = hasFeature;
            _suppressEpic = false;
        }

        private void OnEpicFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEpic) return;
            BuildFeatureFilter();   // a lista de Features acompanha o EPIC
            ApplyEpicFilter();
        }

        private void OnFeatureFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEpic) return;
            ApplyEpicFilter();
        }

        // Bind central da tabela: limpa as colunas antes (as inseridas manualmente,
        // como a combo de Status, não são removidas pela regeneração automática).
        private void BindTable()
        {
            if (_data == null) return;
            PlanGrid.ItemsSource = null;
            PlanGrid.Columns.Clear();
            PlanGrid.ItemsSource = _data.Table.DefaultView;
        }

        // Rodapé: referência REAL da célula no Excel (ex.: E7), considerando a linha do
        // cabeçalho da planilha e a coluna física mapeada (ColumnSheetMap).
        private void OnSelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
        {
            if (_data == null || PlanGrid.SelectedCells.Count == 0)
            {
                CellRefText.Text = "";
                return;
            }

            var cell = PlanGrid.SelectedCells[^1];
            var colName = cell.Column?.Header?.ToString();
            if (colName == null || cell.Item is not DataRowView drv)
            {
                CellRefText.Text = "";
                return;
            }

            var rowIdx = _data.Table.Rows.IndexOf(drv.Row);
            var excelRow = _data.HeaderRow + 1 + (rowIdx < 0 ? _data.Table.Rows.Count : rowIdx);
            var text = _data.ColumnSheetMap.TryGetValue(colName, out var sheetCol)
                ? $"{ToExcelColumn(sheetCol)}{excelRow}"
                : $"{colName} — {AppStrings.Get("TaskPlan_NewColRef")}";

            CellRefText.Text = PlanGrid.SelectedCells.Count > 1
                ? $"{text}  ({PlanGrid.SelectedCells.Count})"
                : text;
        }

        // 1 → A, 2 → B, ..., 27 → AA (letras de coluna do Excel).
        private static string ToExcelColumn(int index)
        {
            var s = "";
            while (index > 0)
            {
                index--;
                s = (char)('A' + index % 26) + s;
                index /= 26;
            }
            return s;
        }

        // Numeração das linhas no cabeçalho, como no Excel.
        private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
            => e.Row.Header = (e.Row.GetIndex() + 1).ToString();

        private void OnColumnsGenerated(object? sender, EventArgs e)
        {
            var approvalName = ApprovalCol;
            if (approvalName != null && _data != null
                && !PlanGrid.Columns.Any(c => c is DataGridComboBoxColumn
                    && string.Equals(c.Header?.ToString(), approvalName, StringComparison.OrdinalIgnoreCase)))
            {
                var txtCol = PlanGrid.Columns.FirstOrDefault(c => c is DataGridTextColumn
                    && string.Equals(c.Header?.ToString(), approvalName, StringComparison.OrdinalIgnoreCase));
                if (txtCol != null)
                {
                    PlanGrid.Columns[PlanGrid.Columns.IndexOf(txtCol)] = new DataGridComboBoxColumn
                    {
                        Header = approvalName,
                        ItemsSource = ApprovalValues,
                        SelectedItemBinding = new System.Windows.Data.Binding($"[{approvalName}]")
                    };
                }
            }

            // Status vira combo com os estados do DevOps (mesmos da grid de Tasks).
            // Nunca duplica: só troca a coluna de texto gerada e se a combo ainda não existe.
            var statusName = FindColumn("Status", "Estado", "State");
            if (statusName != null && _data != null
                && !PlanGrid.Columns.Any(c => c is DataGridComboBoxColumn
                    && string.Equals(c.Header?.ToString(), statusName, StringComparison.OrdinalIgnoreCase)))
            {
                var txtCol = PlanGrid.Columns.FirstOrDefault(c => c is DataGridTextColumn
                    && string.Equals(c.Header?.ToString(), statusName, StringComparison.OrdinalIgnoreCase));
                if (txtCol != null)
                {
                    // Estados conhecidos + valores já presentes na planilha (não perde nada).
                    var items = KnownStates
                        .Concat(_data.Table.Rows.Cast<DataRow>()
                            .Select(r => r[statusName]?.ToString()?.Trim() ?? "")
                            .Where(v => v.Length > 0))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    PlanGrid.Columns[PlanGrid.Columns.IndexOf(txtCol)] = new DataGridComboBoxColumn
                    {
                        Header = statusName,
                        ItemsSource = items,
                        SelectedItemBinding = new System.Windows.Data.Binding($"[{statusName}]")
                    };
                }
            }

            // Oculta as colunas auxiliares de validação (__m_*) e pinta as validadas.
            foreach (var col in PlanGrid.Columns)
            {
                var name = col.Header?.ToString() ?? "";
                // Colunas auxiliares (__m_ validação, __c_ cor) nunca aparecem na grade.
                if (name.StartsWith("__", StringComparison.Ordinal))
                {
                    col.Visibility = Visibility.Collapsed;
                    continue;
                }
                if (col is DataGridTextColumn tc && _data != null)
                {
                    var style = new Style(typeof(System.Windows.Controls.TextBlock));

                    // Quebra de linha: permite o ajuste de altura ao texto (como no Excel).
                    style.Setters.Add(new Setter(System.Windows.Controls.TextBlock.TextWrappingProperty, TextWrapping.Wrap));

                    // Cor de fundo da célula (coluna auxiliar __c_*), como no Excel.
                    if (_data.Table.Columns.Contains(ExcelTaskPlanService.ColorColPrefix + name))
                    {
                        style.Setters.Add(new Setter(System.Windows.Controls.TextBlock.BackgroundProperty,
                            new System.Windows.Data.Binding($"[{ExcelTaskPlanService.ColorColPrefix + name}]")
                            {
                                Converter = HexBrushConverter.Instance
                            }));
                    }

                    // Colunas de hierarquia: verde = encontrado (EPIC/Task); vermelho =
                    // preenchido mas NÃO existe no pai (todas — fica até a edição corrigir).
                    bool isResourceCol = string.Equals(name, ResourceCol, StringComparison.Ordinal);
                    if ((HierarchyType(name) != null || isResourceCol) && _data.Table.Columns.Contains(MatchColPrefix + name))
                    {
                        if (HierarchyType(name) is "Epic" or "Task" || isResourceCol)
                        {
                            var okTrig = new System.Windows.DataTrigger
                            {
                                Binding = new System.Windows.Data.Binding($"[{MatchColPrefix + name}]"),
                                Value = "1"
                            };
                            okTrig.Setters.Add(new Setter(System.Windows.Controls.TextBlock.BackgroundProperty,
                                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC8, 0xE6, 0xC9))));
                            okTrig.Setters.Add(new Setter(System.Windows.Controls.TextBlock.ForegroundProperty,
                                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x5E, 0x20))));
                            style.Triggers.Add(okTrig);
                        }

                        var badTrig = new System.Windows.DataTrigger
                        {
                            Binding = new System.Windows.Data.Binding($"[{MatchColPrefix + name}]"),
                            Value = "0"
                        };
                        badTrig.Setters.Add(new Setter(System.Windows.Controls.TextBlock.BackgroundProperty,
                            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC7, 0xCE))));
                        badTrig.Setters.Add(new Setter(System.Windows.Controls.TextBlock.ForegroundProperty,
                            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x00, 0x06))));
                        style.Triggers.Add(badTrig);
                    }

                    if (style.Setters.Count > 0 || style.Triggers.Count > 0)
                        tc.ElementStyle = style;
                }
            }
            ApplyEpicColumnVisibility();
        }

        // Filtros por coluna estilo Excel (tela própria de filtro), combinados com o de EPIC.
        private readonly Dictionary<string, List<string>> _columnFilters = new(StringComparer.OrdinalIgnoreCase);

        private void ApplyEpicFilter()
        {
            if (_data == null) return;
            var allLabel = AppStrings.Get("TaskPlan_EpicAll");
            var sel = EpicFilterCombo.SelectedItem as string;

            var parts = new List<string>();
            if (_epicColumn != null && !string.IsNullOrEmpty(sel) && sel != allLabel)
                parts.Add($"[{_epicColumn}] = '{sel.Replace("'", "''")}'");

            var featureCol = FindColumn("Feature", "Nome da Feature");
            var featureSel = FeatureFilterCombo.SelectedItem as string;
            if (featureCol != null && !string.IsNullOrEmpty(featureSel) && featureSel != allLabel)
                parts.Add($"[{featureCol}] = '{featureSel.Replace("'", "''")}'");
            foreach (var kv in _columnFilters)
            {
                if (!_data.Table.Columns.Contains(kv.Key) || kv.Value.Count == 0) continue;
                var values = string.Join(",", kv.Value.Select(v => $"'{v.Replace("'", "''")}'"));
                parts.Add($"[{kv.Key}] IN ({values})");
            }

            _data.Table.DefaultView.RowFilter = string.Join(" AND ", parts);
            ApplyEpicColumnVisibility();
        }

        private void OnClearFiltersClick(object sender, RoutedEventArgs e)
        {
            _columnFilters.Clear();
            if (EpicFilterCombo.Items.Count > 0) EpicFilterCombo.SelectedIndex = 0;
            ApplyEpicFilter();
        }

        // Menu de filtro ao clicar com o botão direito no cabeçalho da coluna.
        private void ShowHeaderFilterMenu(System.Windows.Controls.Primitives.DataGridColumnHeader header)
        {
            if (_data == null) return;
            var colName = header.Column?.Header?.ToString() ?? "";
            if (string.IsNullOrEmpty(colName) || !_data.Table.Columns.Contains(colName)) return;

            var menu = new ContextMenu();
            var title = new MenuItem
            {
                Header = AppStrings.Get("TaskPlan_FilterColumn", colName),
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold
            };
            menu.Items.Add(title);
            menu.Items.Add(new Separator());

            // Filtro em tela própria (lista com pesquisa e checkboxes).
            var filterItem = new MenuItem
            {
                Header = AppStrings.Get("TaskPlan_FilterOption"),
                FontWeight = _columnFilters.ContainsKey(colName) ? FontWeights.SemiBold : FontWeights.Normal
            };
            filterItem.Click += (_, _) => ShowColumnFilterDialog(colName);
            menu.Items.Add(filterItem);
            var clear = new MenuItem { Header = AppStrings.Get("TaskPlan_FilterClearColumn") };
            clear.Click += (_, _) => { _columnFilters.Remove(colName); ApplyEpicFilter(); };
            menu.Items.Add(clear);

            // Operações de coluna (tipo Excel).
            menu.Items.Add(new Separator());
            var insLeft = new MenuItem { Header = AppStrings.Get("TaskPlan_InsertColLeft") };
            insLeft.Click += (_, _) => InsertColumn(colName, left: true);
            menu.Items.Add(insLeft);
            var insRight = new MenuItem { Header = AppStrings.Get("TaskPlan_InsertColRight") };
            insRight.Click += (_, _) => InsertColumn(colName, left: false);
            menu.Items.Add(insRight);
            var rename = new MenuItem { Header = AppStrings.Get("TaskPlan_RenameCol") };
            rename.Click += (_, _) => RenameColumn(colName);
            menu.Items.Add(rename);
            var del = new MenuItem { Header = AppStrings.Get("TaskPlan_DeleteCol") };
            del.Click += (_, _) => DeleteColumn(colName);
            menu.Items.Add(del);

            // Larguras (como no Excel: selecionar tudo e arrastar).
            menu.Items.Add(new Separator());
            var fitWidths = new MenuItem { Header = AppStrings.Get("TaskPlan_FitColWidths") };
            fitWidths.Click += (_, _) =>
            {
                foreach (var c in PlanGrid.Columns.Where(c => c.Visibility == Visibility.Visible))
                    c.Width = DataGridLength.Auto;
            };
            menu.Items.Add(fitWidths);
            var applyWidth = new MenuItem { Header = AppStrings.Get("TaskPlan_ApplyWidthAll") };
            applyWidth.Click += (_, _) =>
            {
                var w = header.Column?.ActualWidth ?? 0;
                if (w <= 0) return;
                foreach (var c in PlanGrid.Columns.Where(c => c.Visibility == Visibility.Visible))
                    c.Width = new DataGridLength(w);
            };
            menu.Items.Add(applyWidth);

            menu.PlacementTarget = header;
            menu.IsOpen = true;
        }

        // Tela de filtro da coluna (estilo Excel): pesquisa + checkboxes + marcar/desmarcar todos.
        private void ShowColumnFilterDialog(string colName)
        {
            if (_data == null) return;

            var values = _data.Table.Rows.Cast<DataRow>()
                .Select(r => r[colName]?.ToString()?.Trim() ?? "")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v.Length == 0 ? 0 : 1)   // "(vazio)" primeiro
                .ThenBy(v => v, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _columnFilters.TryGetValue(colName, out var current);
            var emptyLabel = AppStrings.Get("TaskPlan_FilterEmpty");

            var dlg = new Window
            {
                Title = AppStrings.Get("TaskPlan_FilterColumn", colName),
                Owner = this,
                Width = 420,
                Height = 520,
                MinWidth = 340,
                MinHeight = 380,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = System.Windows.Media.Brushes.White
            };
            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var search = new TextBox { Height = 28, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(search, 0);
            root.Children.Add(search);

            var checkAll = new CheckBox
            {
                Content = AppStrings.Get("TaskPlan_FilterSelectAll"),
                Margin = new Thickness(2, 0, 0, 6),
                IsThreeState = false
            };
            Grid.SetRow(checkAll, 1);
            root.Children.Add(checkAll);

            var boxes = values.Select(v => new CheckBox
            {
                Content = v.Length == 0 ? emptyLabel : v,
                Tag = v,
                Margin = new Thickness(2, 2, 0, 2),
                IsChecked = current == null || current.Contains(v, StringComparer.OrdinalIgnoreCase)
            }).ToList();

            var list = new ListBox
            {
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1)
            };
            foreach (var cb in boxes) list.Items.Add(cb);
            Grid.SetRow(list, 2);
            root.Children.Add(list);

            void RefreshList()
            {
                var q = search.Text?.Trim() ?? "";
                list.Items.Clear();
                foreach (var cb in boxes)
                    if (q.Length == 0 || (cb.Content?.ToString() ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                        list.Items.Add(cb);
            }
            search.TextChanged += (_, _) => RefreshList();

            checkAll.IsChecked = boxes.All(b => b.IsChecked == true);
            checkAll.Click += (_, _) =>
            {
                var mark = checkAll.IsChecked == true;
                foreach (var cb in list.Items.OfType<CheckBox>()) cb.IsChecked = mark;
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var clear = new Button
            {
                Content = AppStrings.Get("TaskPlan_FilterClearColumn"),
                Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 8, 0)
            };
            clear.Click += (_, _) =>
            {
                _columnFilters.Remove(colName);
                ApplyEpicFilter();
                dlg.DialogResult = false;
            };
            var ok = new Button { Content = "OK", Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = AppStrings.Get("Pred_Cancel"), Width = 90, Height = 28, IsCancel = true };
            ok.Click += (_, _) => { dlg.DialogResult = true; };
            buttons.Children.Add(clear);
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            dlg.Content = root;
            search.Focus();
            if (dlg.ShowDialog() != true) return;

            var selected = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
            if (selected.Count == 0 || selected.Count == boxes.Count)
                _columnFilters.Remove(colName);   // nada/tudo marcado = sem filtro
            else
                _columnFilters[colName] = selected;
            ApplyEpicFilter();
        }

        // ── ajustes de tamanho (como no Excel) ───────────────────────────────
        // Altura da(s) linha(s) selecionada(s) ajustada ao texto.
        private void OnFitRowHeightClick(object sender, RoutedEventArgs e)
        {
            var items = PlanGrid.SelectedCells.Select(c => c.Item).Distinct().ToList();
            if (items.Count == 0 && _ctxRow != null)
                items = PlanGrid.Items.Cast<object>()
                    .Where(i => i is DataRowView drv && drv.Row == _ctxRow).ToList();
            foreach (var item in items)
                if (PlanGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
                    row.Height = double.NaN;   // auto: acompanha o texto (com quebra de linha)
        }

        // Planilha inteira: altura de todas as linhas + largura das colunas ao conteúdo.
        private void OnFitAllClick(object sender, RoutedEventArgs e)
        {
            PlanGrid.RowHeight = double.NaN;
            for (int i = 0; i < PlanGrid.Items.Count; i++)
                if (PlanGrid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow row)
                    row.Height = double.NaN;
            foreach (var col in PlanGrid.Columns.Where(c => c.Visibility == Visibility.Visible))
                col.Width = DataGridLength.Auto;
        }

        // ── colunas: inserir / renomear / excluir ────────────────────────────
        private void InsertColumn(string refCol, bool left)
        {
            if (_data == null) return;
            var name = PromptForText(AppStrings.Get("TaskPlan_ColName"), "");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (_data.Table.Columns.Contains(name))
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_ColExists", name),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            PushUndo();
            var col = _data.Table.Columns.Add(name, typeof(string));
            var refIdx = _data.Table.Columns[refCol]!.Ordinal;
            col.SetOrdinal(left ? refIdx : refIdx + 1);
            // Sem entrada no ColumnSheetMap: o salvar grava após a última coluna usada da aba.
            _dirty = true;
            RebindGrid();
        }

        private void RenameColumn(string colName)
        {
            if (_data == null) return;
            var name = PromptForText(AppStrings.Get("TaskPlan_ColName"), colName);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == colName) return;
            name = name.Trim();
            if (_data.Table.Columns.Contains(name))
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_ColExists", name),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            PushUndo();
            _data.Table.Columns[colName]!.ColumnName = name;
            if (_data.Table.Columns.Contains(MatchColPrefix + colName))
                _data.Table.Columns[MatchColPrefix + colName]!.ColumnName = MatchColPrefix + name;
            // Mantém a posição na planilha (o salvar regrava o cabeçalho).
            if (_data.ColumnSheetMap.TryGetValue(colName, out var sheetIdx))
            {
                _data.ColumnSheetMap.Remove(colName);
                _data.ColumnSheetMap[name] = sheetIdx;
            }
            if (_data.AppendedColumns.Remove(colName))
                _data.AppendedColumns.Add(name);
            _columnFilters.Remove(colName);
            if (string.Equals(_epicColumn, colName, StringComparison.OrdinalIgnoreCase)) _epicColumn = name;
            _dirty = true;
            RebindGrid();
        }

        private void DeleteColumn(string colName)
        {
            if (_data == null) return;

            // Colunas vinculadas ao cronograma não podem ser excluídas.
            if (IsScheduleColumn(colName))
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_ColProtected", colName),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(this, AppStrings.Get("TaskPlan_DeleteColConfirm", colName),
                AppStrings.Get("TaskPlan_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            PushUndo();
            if (_data.ColumnSheetMap.TryGetValue(colName, out var sheetIdx))
            {
                _data.RemovedSheetColumns.Add(sheetIdx);
                _data.ColumnSheetMap.Remove(colName);
            }
            _data.AppendedColumns.Remove(colName);
            _data.Table.Columns.Remove(colName);
            if (_data.Table.Columns.Contains(MatchColPrefix + colName))
                _data.Table.Columns.Remove(MatchColPrefix + colName);
            _columnFilters.Remove(colName);
            _dirty = true;
            RebindGrid();
        }

        // Religa o grid após mudança de esquema (colunas) e reaplica filtros/validação.
        private void RebindGrid()
        {
            if (_data == null) return;
            ValidateAgainstSchedule();
            BindTable();
            BuildEpicFilter();
        }

        // Caixa simples de texto (nome de coluna).
        private string? PromptForText(string label, string initial)
        {
            var dlg = new Window
            {
                Title = AppStrings.Get("TaskPlan_Title"),
                Owner = this,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.White
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
            var box = new TextBox { Text = initial, Height = 26, Padding = new Thickness(4, 2, 4, 2) };
            panel.Children.Add(box);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var ok = new Button { Content = "OK", Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = AppStrings.Get("Pred_Cancel"), Width = 90, Height = 28, IsCancel = true };
            ok.Click += (_, _) => { dlg.DialogResult = true; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            box.Focus();
            box.SelectAll();
            return dlg.ShowDialog() == true ? box.Text : null;
        }

        // "Todos" mostra a coluna; um valor específico oculta a coluna (redundante) — EPIC e Feature.
        private void ApplyEpicColumnVisibility()
        {
            var allLabel = AppStrings.Get("TaskPlan_EpicAll");

            void SetVisibility(string? colName, ComboBox combo)
            {
                if (colName == null) return;
                bool showCol = (combo.SelectedItem as string) == allLabel || combo.SelectedIndex <= 0;
                var col = PlanGrid.Columns.FirstOrDefault(c => string.Equals(c.Header?.ToString(), colName, StringComparison.OrdinalIgnoreCase));
                if (col != null)
                    col.Visibility = showCol ? Visibility.Visible : Visibility.Collapsed;
            }

            SetVisibility(_epicColumn, EpicFilterCombo);
            SetVisibility(FindColumn("Feature", "Nome da Feature"), FeatureFilterCombo);
        }

        // ── Desfazer (Ctrl+Z): histórico de snapshots da tabela ─────────────
        private sealed record UndoState(DataTable Table, Dictionary<string, int> Map,
            HashSet<string> Appended, List<int> Removed, int HeaderRow, bool Dirty);

        private readonly List<UndoState> _undo = new();
        private const int MaxUndo = 10;

        // Guarda o estado ANTES de uma alteração (edição, colar, cor, linhas, colunas...).
        private void PushUndo()
        {
            if (_data == null) return;
            _undo.Add(new UndoState(
                _data.Table.Copy(),
                new Dictionary<string, int>(_data.ColumnSheetMap, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(_data.AppendedColumns, StringComparer.OrdinalIgnoreCase),
                new List<int>(_data.RemovedSheetColumns),
                _data.HeaderRow,
                _dirty));
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        }

        private void ClearUndo() => _undo.Clear();

        private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

        private void Undo()
        {
            if (_data == null || _undo.Count == 0) return;
            var state = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);

            var restored = new TaskPlanData
            {
                Table = state.Table,
                SheetName = _data.SheetName,
                HeaderRow = state.HeaderRow
            };
            foreach (var kv in state.Map) restored.ColumnSheetMap[kv.Key] = kv.Value;
            foreach (var a in state.Appended) restored.AppendedColumns.Add(a);
            restored.RemovedSheetColumns.AddRange(state.Removed);
            _data = restored;
            _dirty = state.Dirty;

            _columnFilters.Clear();
            UpdateFixedColumns();
            BuildEpicFilter();
            ValidateAgainstSchedule();
            BindTable();
            ApplyEpicFilter();
            StatusText.Foreground = System.Windows.Media.Brushes.Gray;
            StatusText.Text = AppStrings.Get("TaskPlan_Undone", _undo.Count);
        }

        // ── Ctrl+V cola nas células (como no Excel); Ctrl+C já copia nativo ──
        private void OnGridPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Delete: o grid age nativamente — exclui linha(s) inteira(s) selecionada(s)
            // ou LIMPA o conteúdo das células selecionadas. Nos dois casos, grava o
            // snapshot ANTES para o Ctrl+Z restaurar exatamente o estado anterior.
            if (e.Key == System.Windows.Input.Key.Delete && e.OriginalSource is not TextBox
                && (PlanGrid.SelectedItems.Count > 0 || PlanGrid.SelectedCells.Count > 0))
            {
                PushUndo();
                _dirty = true;
                return;   // não marca Handled: o grid segue com a ação normal
            }

            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
                return;

            // Ctrl+Z é tratado no PreviewKeyDown da janela (global).
            if (e.Key != System.Windows.Input.Key.V)
                return;

            // Dentro da edição de uma célula (F2/duplo-clique): deixa o TextBox colar no
            // ponto do cursor (como no Excel), sem substituir toda a célula com o bloco.
            if (e.OriginalSource is TextBox)
                return;

            e.Handled = PasteFromClipboard();
        }

        // Menu do botão direito: Copiar / Colar (mesmo comportamento de Ctrl+C / Ctrl+V).
        private void OnCopyClick(object sender, RoutedEventArgs e)
            => System.Windows.Input.ApplicationCommands.Copy.Execute(null, PlanGrid);

        private void OnPasteClick(object sender, RoutedEventArgs e) => PasteFromClipboard();

        // Cola o bloco da área de transferência a partir da célula atual. True se colou.
        private bool PasteFromClipboard()
        {
            if (_data == null || PlanGrid.CurrentCell.Column == null) return false;

            var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            if (string.IsNullOrEmpty(text)) return false;

            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            PushUndo();

            // Colunas visíveis na ordem exibida, a partir da coluna atual.
            var visibleCols = PlanGrid.Columns
                .Where(c => c.Visibility == Visibility.Visible)
                .OrderBy(c => c.DisplayIndex)
                .Select(c => c.Header?.ToString() ?? "")
                .Where(n => _data.Table.Columns.Contains(n))
                .ToList();
            var startColName = PlanGrid.CurrentCell.Column.Header?.ToString() ?? "";
            int startCol = visibleCols.IndexOf(startColName);
            if (startCol < 0) return false;

            int startRow = PlanGrid.Items.IndexOf(PlanGrid.CurrentCell.Item);
            if (startRow < 0) startRow = _data.Table.DefaultView.Count;

            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];

            for (int li = 0; li < lines.Length; li++)
            {
                DataRowView drv;
                int idx = startRow + li;
                if (idx < _data.Table.DefaultView.Count)
                    drv = _data.Table.DefaultView[idx];
                else
                    drv = _data.Table.DefaultView.AddNew();

                var cells = lines[li].Split('\t');
                for (int ci = 0; ci < cells.Length && startCol + ci < visibleCols.Count; ci++)
                    drv.Row[visibleCols[startCol + ci]] = cells[ci];
                drv.EndEdit();
            }

            _dirty = true;
            ValidateAgainstSchedule();
            PlanGrid.Items.Refresh();
            return true;
        }

        // ── merge com o cronograma (busca as Tasks de cada Story no TFS) ─────
        private sealed record MergeSource(TfsImportService.DevOpsTaskInfo Info, ProjectTask Story);
        private sealed record MergeChange(DataRow Row, int RowNumber, string From, MergeSource Dev, string Confidence);

        // Task Closed do DevOps.
        private static bool IsClosedTask(TfsImportService.DevOpsTaskInfo t)
            => string.Equals(t.State?.Trim(), "Closed", StringComparison.OrdinalIgnoreCase);

        // Merge: só atualiza/adiciona Task Closed se ela JÁ estiver na planilha.
        private async void OnMergeScheduleClick(object sender, RoutedEventArgs e)
            => await RunMergeAsync(includeNewClosed: false);

        // Load Task: carrega do cronograma/TFS como o Merge, perguntando se traz as Closed.
        private async void OnLoadTaskClick(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;
            if (_vm?.Project == null || _vm.Project.Tasks.Count == 0)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_NoSchedule"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // Padrão = Não (não traz Closed novas).
            var r = MessageBox.Show(this, AppStrings.Get("TaskPlan_LoadTaskClosed"),
                AppStrings.Get("TaskPlan_Title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            await RunMergeAsync(includeNewClosed: r == MessageBoxResult.Yes);
        }

        private async Task RunMergeAsync(bool includeNewClosed)
        {
            if (_data == null) return;
            if (_vm?.Project == null || _vm.Project.Tasks.Count == 0)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_NoSchedule"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var idCol      = FindColumn("ID Devops", "ID DevOps", "IdDevops", "ID_Devops", "ID Dev Ops", "ID Task", "IdTask", "ID_Task");
            var taskCol    = FindColumn("Task", "Tarefa", "Nome da Task");
            var storyCol   = FindColumn("Story", "Nome da Story");
            var featureCol = FindColumn("Feature", "Nome da Feature");
            var prioCol    = FindColumn("Prioridade", "Priority", "Prio", "Prioridade Task");
            var statusCol  = FindColumn("Status", "Estado", "State");
            var estCol     = FindColumn("Estimado HH", "Estimado", "Estimativa", "HH Estimado", "Estimated", "HH");
            var descCol    = FindColumn("Descrição da Task", "Descricao da Task", "Descrição", "Descricao", "Description");
            var regCol     = RegisterDateCol;
            var pctCol     = PercConclusaoCol;
            if (idCol == null || taskCol == null)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_FetchNeedCols"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var options = TfsConnectionStore.Load("NXProject.Community");
            if (string.IsNullOrWhiteSpace(options.OrganizationUrl) || string.IsNullOrWhiteSpace(options.PersonalAccessToken))
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_PickNoDevOps"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            NormalizeApprovalValues();
            PushUndo();
            ReviewDuplicateIds(idCol);
            var flat = Flatten(_vm.Project.Tasks).ToList();

            var log = new System.Text.StringBuilder();
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] Merge com Cronograma iniciado.");

            int updated = 0, added = 0;
            var matchedIds = new HashSet<int>();
            try
            {
            // 1) Busca as Tasks de cada Story direto no TFS — o cronograma não tem as Tasks por objetivo.
            var sources = new List<MergeSource>();
            var stories = flat.Where(t => IsType(t, "Story") && t.TfsId is > 0).ToList();
            MergeProgress.Visibility = Visibility.Visible;
            MergeProgress.IsIndeterminate = false;
            MergeProgress.Maximum = Math.Max(1, stories.Count);
            MergeProgress.Value = 0;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                for (int i = 0; i < stories.Count; i++)
                {
                    var story = stories[i];
                    StatusText.Foreground = System.Windows.Media.Brushes.Gray;
                    StatusText.Text = AppStrings.Get("TaskPlan_MergeStepStory", i + 1, stories.Count, story.Name ?? "");
                    log.AppendLine($"[{DateTime.Now:HH:mm:ss}] Buscando Tasks da Story {story.TfsId} — \"{story.Name}\"...");
                    var children = await TfsImportService.FetchChildTasksFromDevOpsAsync(options, story.TfsId!.Value);
                    log.AppendLine($"    → {(children?.Count ?? 0)} task(s).");
                    if (children != null)
                        sources.AddRange(children.Select(c => new MergeSource(c, story)));
                    MergeProgress.Value = i + 1;
                }
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
            }
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] Total de Tasks no DevOps: {sources.Count}.");

            // 2) Usar IA?
            var useAi = MessageBox.Show(this, AppStrings.Get("TaskPlan_MergeUseAI"),
                AppStrings.Get("TaskPlan_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] Merge com IA: {(useAi ? "sim" : "não")}.");

            if (useAi)
            {
                MergeProgress.IsIndeterminate = true;
                StatusText.Text = AppStrings.Get("TaskPlan_MergeAiRunning");
                var changes = await BuildAiMergeChangesAsync(sources, idCol, taskCol, storyCol, log);
                MergeProgress.IsIndeterminate = false;
                if (changes == null) return;   // erro/config já avisado (log oferecido)
                log.AppendLine($"[{DateTime.Now:HH:mm:ss}] IA devolveu {changes.Count} correspondência(s).");
                if (changes.Count > 0 && !ConfirmMergeChanges(changes))
                {
                    log.AppendLine("Usuário cancelou na confirmação de/para.");
                    StatusText.Text = "";
                    return;
                }

                StatusText.Text = AppStrings.Get("TaskPlan_MergeStepApply");
                foreach (var ch in changes)
                {
                    log.AppendLine($"    Linha {ch.RowNumber}: \"{ch.From}\" → {ch.Dev.Info.TfsId}:T \"{ch.Dev.Info.Title}\"");
                    ch.Row[idCol] = $"{ch.Dev.Info.TfsId}:T";
                    if (ApprovalCol is { } ac) ch.Row[ac] = "Sim";
                    if (regCol != null && ch.Dev.Info.CreatedDate is { } cd) ch.Row[regCol] = FormatRegisterDate(cd);
                    if (pctCol != null) ch.Row[pctCol] = Math.Round(ch.Dev.Info.PercentComplete).ToString("0");
                    if (prioCol != null) ch.Row[prioCol] = ch.Dev.Info.Priority.ToString();
                    if (estCol != null && ch.Dev.Info.EstimatedHours > 0) ch.Row[estCol] = ch.Dev.Info.EstimatedHours.ToString("0.##");
                    if (statusCol != null && !string.IsNullOrWhiteSpace(ch.Dev.Info.State)) ch.Row[statusCol] = ch.Dev.Info.State;
                    if (storyCol != null && string.IsNullOrWhiteSpace(ch.Row[storyCol]?.ToString()))
                        ch.Row[storyCol] = ch.Dev.Story.Name;
                    await FillObservationFromLastCommentAsync(options, ch.Row, ch.Dev.Info);
                    matchedIds.Add(ch.Dev.Info.TfsId);
                    updated++;
                }
            }
            else
            {
                StatusText.Text = AppStrings.Get("TaskPlan_MergeStepApply");
                // Determinístico: casa pelo ID :T ou pelo nome (com Story quando informada).
                foreach (var src in sources)
                {
                    var displayId = $"{src.Info.TfsId}:T";
                    var approvedRows = _data.Table.Rows.Cast<DataRow>().Where(r => IsApprovedOrAlreadyTfs(r, idCol));
                    var row = approvedRows.FirstOrDefault(r =>
                        string.Equals(r[idCol]?.ToString()?.Trim(), displayId, StringComparison.OrdinalIgnoreCase))
                        ?? approvedRows.FirstOrDefault(r =>
                            string.Equals(r[taskCol]?.ToString()?.Trim(), src.Info.Title.Trim(), StringComparison.OrdinalIgnoreCase)
                            && (storyCol == null
                                || string.IsNullOrWhiteSpace(r[storyCol]?.ToString())
                                || string.Equals(r[storyCol]?.ToString()?.Trim(), (src.Story.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)));
                    if (row == null) continue;

                    row[idCol] = displayId;
                    if (ApprovalCol is { } ac) row[ac] = "Sim";
                    if (regCol != null && src.Info.CreatedDate is { } cd) row[regCol] = FormatRegisterDate(cd);
                    if (pctCol != null) row[pctCol] = Math.Round(src.Info.PercentComplete).ToString("0");
                    if (storyCol != null && string.IsNullOrWhiteSpace(row[storyCol]?.ToString())) row[storyCol] = src.Story.Name;
                    if (prioCol != null) row[prioCol] = src.Info.Priority.ToString();
                    if (estCol != null && src.Info.EstimatedHours > 0) row[estCol] = src.Info.EstimatedHours.ToString("0.##");
                    if (statusCol != null && !string.IsNullOrWhiteSpace(src.Info.State)) row[statusCol] = src.Info.State;
                    await FillObservationFromLastCommentAsync(options, row, src.Info);
                    matchedIds.Add(src.Info.TfsId);
                    updated++;
                }
            }

            // 3) Tasks do DevOps que não estão na planilha viram linhas novas.
            var existingIds = _data.Table.Rows.Cast<DataRow>()
                .Select(r => r[idCol]?.ToString()?.Trim() ?? "")
                .Where(v => v.EndsWith(":T", StringComparison.OrdinalIgnoreCase))
                .Select(v => int.TryParse(v[..^2], out var n) ? n : 0)
                .Where(n => n > 0)
                .ToHashSet();

            foreach (var src in sources)
            {
                if (existingIds.Contains(src.Info.TfsId) || matchedIds.Contains(src.Info.TfsId)) continue;
                // Task Closed nova só entra quando o usuário pediu (Load Task → "sim").
                // No Merge (includeNewClosed=false) a Closed não listada não é recarregada.
                if (!includeNewClosed && IsClosedTask(src.Info))
                {
                    log.AppendLine($"    Ignorada (Closed não listada): {src.Info.TfsId}:T \"{src.Info.Title}\".");
                    continue;
                }
                var dr = _data.Table.NewRow();
                if (_epicColumn != null) dr[_epicColumn] = Ancestor(src.Story, "Epic");
                if (featureCol != null) dr[featureCol] = Ancestor(src.Story, "Feature");
                if (storyCol != null) dr[storyCol] = src.Story.Name ?? "";
                dr[taskCol] = src.Info.Title;
                dr[idCol] = $"{src.Info.TfsId}:T";
                if (ApprovalCol is { } ac) dr[ac] = "Sim";
                if (regCol != null) dr[regCol] = FormatRegisterDate(src.Info.CreatedDate ?? DateTime.Today);
                if (pctCol != null) dr[pctCol] = Math.Round(src.Info.PercentComplete).ToString("0");
                if (prioCol != null) dr[prioCol] = src.Info.Priority.ToString();
                if (estCol != null && src.Info.EstimatedHours > 0) dr[estCol] = src.Info.EstimatedHours.ToString("0.##");
                if (statusCol != null) dr[statusCol] = src.Info.State ?? "";
                await FillObservationFromLastCommentAsync(options, dr, src.Info);
                _data.Table.Rows.Add(dr);
                added++;
            }

            if (updated + added > 0) _dirty = true;
            BuildEpicFilter();
            ValidateAgainstSchedule();
            PlanGrid.Items.Refresh();
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] Merge concluído: {updated} atualizada(s), {added} adicionada(s).");
            StatusText.Foreground = System.Windows.Media.Brushes.Green;
            StatusText.Text = AppStrings.Get("TaskPlan_MergeDone", updated, added);
            }
            catch (Exception ex)
            {
                log.AppendLine($"[{DateTime.Now:HH:mm:ss}] ERRO: {ex}");
                ShowMergeError(ex.Message, log);
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
                StatusText.Text = AppStrings.Get("TaskPlan_MergeFailed");
            }
            finally
            {
                MergeProgress.Visibility = Visibility.Collapsed;
                MergeProgress.IsIndeterminate = false;
                System.Windows.Input.Mouse.OverrideCursor = null;
            }
        }

        // Erro do merge com opção de copiar o log (diagnóstico completo na área de transferência).
        private void ShowMergeError(string message, System.Text.StringBuilder log)
        {
            var r = MessageBox.Show(this,
                AppStrings.Get("TaskPlan_MergeErrorCopyLog", message),
                AppStrings.Get("TaskPlan_Title"), MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (r == MessageBoxResult.Yes)
                Clipboard.SetText(log.ToString());
        }

        // Chama a IA (ação "Merge de Arquivo Externo com Task" da tela IA Geral) e devolve o de/para.
        private async Task<List<MergeChange>?> BuildAiMergeChangesAsync(
            List<MergeSource> sources, string idCol, string taskCol, string? storyCol, System.Text.StringBuilder log)
        {
            var ws = AISettingsStore.LoadWorkspace("NXProject.Community");
            var settings = ws.ResolveActiveSettings();
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_MergeNoAI"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            var systemPrompt = ws.ActionTypes
                .FirstOrDefault(a => a.Name == AIActionType.MergeExternalActionName)?.Prompt;
            if (string.IsNullOrWhiteSpace(systemPrompt))
                systemPrompt = AISettingsStore.MergeExternalActionPrompt;

            // Linhas candidatas: sem ID DevOps (:T) e com nome de task.
            var rows = new List<(int Number, DataRow Row)>();
            int number = 1;
            foreach (DataRow r in _data!.Table.Rows)
            {
                var id = r[idCol]?.ToString()?.Trim() ?? "";
                var name = r[taskCol]?.ToString()?.Trim() ?? "";
                if (!id.EndsWith(":T", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(name) && IsApproved(r))
                    rows.Add((number, r));
                number++;
            }
            if (rows.Count == 0 || sources.Count == 0) return new List<MergeChange>();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("LINHAS:");
            foreach (var (n, r) in rows)
            {
                var story = storyCol != null ? r[storyCol]?.ToString()?.Trim() : "";
                sb.AppendLine($"{n}. Task=\"{r[taskCol]}\"; Story=\"{story}\"; ID=\"{r[idCol]}\"");
            }
            sb.AppendLine();
            sb.AppendLine("TASKS_DEVOPS:");
            foreach (var s in sources)
                sb.AppendLine($"- id={s.Info.TfsId}; titulo=\"{s.Info.Title}\"; story=\"{s.Story.Name}\"; estado={s.Info.State}; prioridade={s.Info.Priority}");

            string raw;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                StatusText.Text = AppStrings.Get("TaskPlan_MergeAiRunning");
                log.AppendLine($"[{DateTime.Now:HH:mm:ss}] Enviando para a IA ({rows.Count} linha(s), {sources.Count} task(s) DevOps)...");
                raw = await ProjectAIAssistantService.GenerateFreeTextAsync(
                    settings, systemPrompt, "Faça o merge das LINHAS com as TASKS_DEVOPS e devolva o JSON.", sb.ToString());
                log.AppendLine($"[{DateTime.Now:HH:mm:ss}] Resposta da IA:");
                log.AppendLine(raw);
            }
            catch (Exception ex)
            {
                log.AppendLine($"[{DateTime.Now:HH:mm:ss}] ERRO na chamada da IA: {ex}");
                ShowMergeError(ex.Message, log);
                return null;
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
            }

            try
            {
                var start = raw.IndexOf('[');
                var end = raw.LastIndexOf(']');
                if (start < 0 || end <= start) return new List<MergeChange>();
                using var doc = System.Text.Json.JsonDocument.Parse(raw[start..(end + 1)]);

                var byNumber = rows.ToDictionary(x => x.Number, x => x.Row);
                var byId = sources.ToDictionary(s => s.Info.TfsId, s => s);
                var changes = new List<MergeChange>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("linha", out var lp) || !lp.TryGetInt32(out var line)) continue;
                    if (!item.TryGetProperty("id_devops", out var ip) || !ip.TryGetInt32(out var devId)) continue;
                    if (!byNumber.TryGetValue(line, out var row) || !byId.TryGetValue(devId, out var src)) continue;

                    // Trava de hierarquia: se a linha tem Story, a Task só pode vir daquela Story
                    // (a IA é instruída, mas aqui é garantido — nunca associa Task de outra Feature/Story).
                    var rowStory = storyCol != null ? row[storyCol]?.ToString()?.Trim() : null;
                    if (!string.IsNullOrEmpty(rowStory)
                        && !string.Equals(rowStory, (src.Story.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        log.AppendLine($"    Linha {line}: descartada — a IA sugeriu a Task {devId} da Story \"{src.Story.Name}\", mas a linha é da Story \"{rowStory}\".");
                        continue;
                    }

                    var conf = item.TryGetProperty("confianca", out var cp) ? cp.GetString() ?? "" : "";
                    changes.Add(new MergeChange(row, line, row[taskCol]?.ToString()?.Trim() ?? "", src, conf));
                }
                return changes;
            }
            catch (Exception ex)
            {
                log.AppendLine($"[{DateTime.Now:HH:mm:ss}] ERRO ao interpretar a resposta da IA: {ex}");
                ShowMergeError(ex.Message, log);
                return null;
            }
        }

        // Lista o de/para encontrado pela IA e pede confirmação antes de concluir.
        private bool ConfirmMergeChanges(List<MergeChange> changes)
        {
            var dlg = new Window
            {
                Title = AppStrings.Get("TaskPlan_MergeConfirmTitle"),
                Owner = this,
                Width = 720,
                Height = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = System.Windows.Media.Brushes.White
            };
            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = AppStrings.Get("TaskPlan_MergeConfirmHeader", changes.Count),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var list = new ListBox
            {
                ItemsSource = changes.Select(c =>
                    $"Linha {c.RowNumber}: \"{c.From}\"  →  {c.Dev.Info.TfsId}:T \"{c.Dev.Info.Title}\"  (Story: {c.Dev.Story.Name}{(string.IsNullOrEmpty(c.Confidence) ? "" : $"; confiança: {c.Confidence}")})").ToList(),
                FontSize = 12
            };
            Grid.SetRow(list, 1);
            root.Children.Add(list);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var ok = new Button { Content = AppStrings.Get("TaskPlan_MergeConfirmApply"), Width = 120, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = AppStrings.Get("Pred_Cancel"), Width = 96, Height = 30, IsCancel = true };
            ok.Click += (_, _) => { dlg.DialogResult = true; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            dlg.Content = root;
            return dlg.ShowDialog() == true;
        }

        // ── validação contra o cronograma (pinta células encontradas) ────────
        private const string MatchColPrefix = "__m_";

        // Tipo do cronograma correspondente à coluna da planilha (ou null).
        private string? HierarchyType(string colName)
        {
            if (_epicColumn != null && string.Equals(colName, _epicColumn, StringComparison.OrdinalIgnoreCase))
                return "Epic";
            if (colName == FindColumn("Feature", "Nome da Feature")) return "Feature";
            if (colName == FindColumn("Story", "Nome da Story")) return "Story";
            if (colName == FindColumn("Task", "Tarefa", "Nome da Task")) return "Task";
            return null;
        }

        // Coluna do responsável/recurso na planilha (nome da pessoa do cronograma).
        private string? ResourceCol => FindColumn(
            "Recurso", "Recursos", "Responsável", "Responsavel",
            "Responsible", "Owner", "Atribuído a", "Atribuido a", "Assigned To", "AssignedTo");

        /// <summary>Pessoa (recurso Work) do cronograma cujo nome bate com <paramref name="name"/>.</summary>
        private Resource? FindScheduleResource(string? name)
        {
            var n = name?.Trim().TrimStart('*').Trim();
            if (string.IsNullOrEmpty(n) || _vm?.Project == null) return null;
            return _vm.Project.Resources.FirstOrDefault(r =>
                r.Type == ResourceType.Work
                && (string.Equals(r.Name?.Trim(), n, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.DisplayName?.TrimStart('*').Trim(), n, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>Nomes de pessoas em uma célula de Recurso (separados por vírgula/ponto-e-vírgula).</summary>
        private static List<string> SplitResourceNames(string? cell) =>
            (cell ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>Aloca as pessoas da planilha na Task (sem duplicar). Retorna true se mudou algo.</summary>
        private bool AssignResourceFromPlan(ProjectTask task, string? cell)
        {
            bool changed = false;
            foreach (var name in SplitResourceNames(cell))
            {
                var res = FindScheduleResource(name);
                if (res == null || task.Resources.Any(tr => tr.ResourceId == res.Id)) continue;
                task.Resources.Add(new TaskResource
                {
                    ResourceId = res.Id,
                    Resource = res,
                    EstimatedHours = task.EstimatedHours
                });
                changed = true;
            }
            return changed;
        }

        /// <summary>Marca (colunas __m_*) as células de EPIC/Feature/Story/Task encontradas no cronograma.</summary>
        private void ValidateAgainstSchedule()
        {
            if (_data == null || _vm?.Project == null || _vm.Project.Tasks.Count == 0) return;
            NormalizeApprovalValues();

            // Estados por célula: "1" = encontrado (verde em EPIC/Task; Story/Feature
            // mostram a validade pelos IDs); "0" = preenchido mas NÃO existe no pai
            // (vermelho até edição — ao aplicar vira interno :I pela cascata).
            var hierCols = _data.Table.Columns.Cast<DataColumn>()
                .Select(c => c.ColumnName)
                .Where(n => !n.StartsWith("__", StringComparison.Ordinal) && HierarchyType(n) != null)
                .ToList();
            var resourceCol = ResourceCol;
            if (hierCols.Count == 0 && resourceCol == null && StoryIdCol == null && FeatureIdCol == null) return;

            // Colunas validadas: hierarquia + Recurso (nome de pessoa do cronograma).
            var valCols = resourceCol != null && !hierCols.Contains(resourceCol)
                ? hierCols.Append(resourceCol).ToList()
                : hierCols;

            bool addedCols = false;
            foreach (var col in valCols)
                if (!_data.Table.Columns.Contains(MatchColPrefix + col))
                {
                    _data.Table.Columns.Add(MatchColPrefix + col, typeof(string));
                    addedCols = true;
                }

            // Colunas novas depois do bind (ex.: cronograma aberto após carregar o plano):
            // religa o ItemsSource para o grid regenerar as colunas com os estilos.
            if (addedCols && PlanGrid.ItemsSource != null)
            {
                BindTable();
                ApplyEpicFilter();
            }

            var flat = Flatten(_vm.Project.Tasks).ToList();
            var storyCol   = FindColumn("Story", "Nome da Story");
            var featureCol = FindColumn("Feature", "Nome da Feature");

            var taskColV = FindColumn("Task", "Tarefa", "Nome da Task");
            var estColV  = FindColumn("Estimado HH", "Estimado", "Estimativa", "HH Estimado", "Estimated", "HH");
            foreach (DataRow dr in _data.Table.Rows)
            {
                var story   = storyCol   != null ? dr[storyCol]?.ToString()?.Trim()   : null;
                var feature = featureCol != null ? dr[featureCol]?.ToString()?.Trim() : null;
                var epic    = _epicColumn != null ? dr[_epicColumn]?.ToString()?.Trim() : null;

                // Estimado HH: em linha com Task, vazio ou zero vira 1h (padrão).
                if (estColV != null && taskColV != null
                    && !string.IsNullOrWhiteSpace(dr[taskColV]?.ToString())
                    && TaskPlanScheduleRules.ParseEstimatedHours(dr[estColV]?.ToString()) == null)
                    dr[estColV] = "1";

                // Atualiza os IDs de Feature/Story conforme a digitação (hierarquia estrita).
                if (FeatureIdCol is { } fc)
                {
                    var featureNode = string.IsNullOrEmpty(feature) ? null
                        : flat.FirstOrDefault(x => IsType(x, "Feature")
                            && string.Equals((x.Name ?? "").Trim(), feature, StringComparison.OrdinalIgnoreCase)
                            && (string.IsNullOrEmpty(epic)
                                || string.Equals(Ancestor(x, "Epic").Trim(), epic, StringComparison.OrdinalIgnoreCase)));
                    dr[fc] = featureNode != null ? DisplayIdOf(featureNode) : "";
                }
                if (StoryIdCol is { } sc)
                {
                    var storyNode = FindStoryInSchedule(flat, story, feature, epic);
                    dr[sc] = storyNode != null ? DisplayIdOf(storyNode) : "";
                }

                foreach (var col in valCols)
                {
                    var value = dr[col]?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrEmpty(value))
                    {
                        dr[MatchColPrefix + col] = "";
                        continue;
                    }

                    // Recurso: valida cada nome contra as pessoas (recursos Work) do cronograma.
                    if (string.Equals(col, resourceCol, StringComparison.Ordinal))
                    {
                        var names = SplitResourceNames(value);
                        dr[MatchColPrefix + col] =
                            names.Count > 0 && names.All(n => FindScheduleResource(n) != null) ? "1" : "0";
                        continue;
                    }

                    // Validação hierárquica: Story exige Story+Feature (e EPIC);
                    // Feature exige Feature+EPIC — homônimo fora do pai NÃO valida.
                    bool ok = HierarchyType(col) switch
                    {
                        "Task"  => FindTaskInSchedule(flat, value, story, feature, epic) != null,
                        "Story" => FindStoryInSchedule(flat, value, feature, epic) != null,
                        "Feature" => flat.Any(x => IsType(x, "Feature")
                            && string.Equals((x.Name ?? "").Trim(), value, StringComparison.OrdinalIgnoreCase)
                            && (string.IsNullOrEmpty(epic)
                                || string.Equals(Ancestor(x, "Epic").Trim(), epic, StringComparison.OrdinalIgnoreCase))),
                        var t   => flat.Any(x => IsType(x, t!) && string.Equals((x.Name ?? "").Trim(), value, StringComparison.OrdinalIgnoreCase)),
                    };
                    dr[MatchColPrefix + col] = ok ? "1" : "0";
                }
            }
        }

        // ── Ctrl+clique na célula EPIC/Feature/Story/Task abre a busca ───────
        private async void OnGridPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
                return;
            if (_data == null) return;

            // Localiza a célula clicada (o clique pode cair em elemento de texto não-visual).
            var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell == null || cell.DataContext is not DataRowView drv) return;

            var colName = cell.Column?.Header?.ToString() ?? "";
            bool isResourceCol = string.Equals(colName, ResourceCol, StringComparison.Ordinal);
            var type = HierarchyType(colName);
            if (type == null && !isResourceCol) return;

            if (_vm?.Project == null || _vm.Project.Tasks.Count == 0)
            {
                e.Handled = true;
                MessageBox.Show(this, AppStrings.Get("TaskPlan_NoSchedule"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            e.Handled = true;
            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);

            // Recurso: lista as pessoas do cronograma (mesma tela da busca da Story).
            if (isResourceCol)
            {
                var people = _vm.Project.Resources
                    .Where(r => r.Type == ResourceType.Work)
                    .Select(r => new ProjectTask { Id = r.Id, Name = r.Name, TfsType = "Recurso" })
                    .ToList();
                if (people.Count == 0)
                {
                    MessageBox.Show(this, AppStrings.Get("TaskPlan_PickNoPeople"),
                        AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var curName = drv.Row[colName]?.ToString()?.Trim() ?? "";
                var peoplePicker = new PlanItemPickerWindow(colName, people, curName) { Owner = this };
                if (peoplePicker.ShowDialog() != true || peoplePicker.Selected == null) return;

                PushUndo();
                drv.Row[colName] = peoplePicker.Selected.Name ?? "";
                _dirty = true;
                ValidateAgainstSchedule();
                PlanGrid.Items.Refresh();
                return;
            }

            var flat = Flatten(_vm.Project.Tasks).ToList();
            var current = drv.Row[colName]?.ToString()?.Trim() ?? "";

            List<ProjectTask> candidates;
            if (type == "Task")
            {
                // Task não fica no cronograma: busca as filhas da Story direto no DevOps.
                candidates = await FetchDevOpsTaskCandidatesAsync(drv.Row, flat);
                if (candidates == null!) return; // mensagem já exibida
            }
            else
            {
                candidates = flat.Where(t => IsType(t, type!)).ToList();

                // Escopo pela hierarquia já associada na linha: Feature só do EPIC da
                // linha; Story só da Feature (ou, sem Feature, só do EPIC).
                var epicVal = _epicColumn != null ? drv.Row[_epicColumn]?.ToString()?.Trim() : null;
                var featureColName = FindColumn("Feature", "Nome da Feature");
                var featVal = featureColName != null ? drv.Row[featureColName]?.ToString()?.Trim() : null;

                List<ProjectTask> ScopeBy(string ancestorType, string value) =>
                    candidates.Where(t => string.Equals(Ancestor(t, ancestorType).Trim(), value, StringComparison.OrdinalIgnoreCase)).ToList();

                if (type == "Feature" && !string.IsNullOrEmpty(epicVal))
                {
                    var scoped = ScopeBy("Epic", epicVal!);
                    if (scoped.Count > 0) candidates = scoped;
                }
                else if (type == "Story")
                {
                    if (!string.IsNullOrEmpty(featVal))
                    {
                        var scoped = ScopeBy("Feature", featVal!);
                        if (scoped.Count > 0) candidates = scoped;
                    }
                    else if (!string.IsNullOrEmpty(epicVal))
                    {
                        var scoped = ScopeBy("Epic", epicVal!);
                        if (scoped.Count > 0) candidates = scoped;
                    }
                }
            }

            var picker = new PlanItemPickerWindow(colName, candidates, current) { Owner = this };
            if (picker.ShowDialog() != true || picker.Selected == null) return;

            PushUndo();
            ApplyPickedItem(drv.Row, colName, type!, picker.Selected);
            _dirty = true;
            ValidateAgainstSchedule();
            PlanGrid.Items.Refresh();
        }

        // Tasks candidatas para a linha: filhas (no DevOps) da Story indicada na linha.
        // Retorna null se não for possível buscar (mensagem já mostrada ao usuário).
        private async Task<List<ProjectTask>> FetchDevOpsTaskCandidatesAsync(DataRow dr, List<ProjectTask> flat)
        {
            var storyCol   = FindColumn("Story", "Nome da Story");
            var featureCol = FindColumn("Feature", "Nome da Feature");
            var story   = storyCol   != null ? dr[storyCol]?.ToString()?.Trim()   : null;
            var feature = featureCol != null ? dr[featureCol]?.ToString()?.Trim() : null;
            var epic    = _epicColumn != null ? dr[_epicColumn]?.ToString()?.Trim() : null;

            var storyNode = FindStoryInSchedule(flat, story, feature, epic);
            if (storyNode?.TfsId is not > 0)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_PickNoStory"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return null!;
            }

            var options = TfsConnectionStore.Load("NXProject.Community");
            if (string.IsNullOrWhiteSpace(options.OrganizationUrl) || string.IsNullOrWhiteSpace(options.PersonalAccessToken))
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_PickNoDevOps"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return null!;
            }

            List<TfsImportService.DevOpsTaskInfo>? children;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                children = await TfsImportService.FetchChildTasksFromDevOpsAsync(options, storyNode.TfsId.Value);
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
            }

            // Objetos temporários só para a tela de busca (não entram no cronograma);
            // Parent = Story para o preenchimento da hierarquia ao selecionar.
            return (children ?? new List<TfsImportService.DevOpsTaskInfo>())
                .Select(t => new ProjectTask
                {
                    TfsId = t.TfsId,
                    Name = t.Title,
                    TfsType = "Task",
                    Priority = t.Priority,
                    TfsState = t.State,
                    EstimatedHours = t.EstimatedHours > 0 ? t.EstimatedHours : null,
                    Parent = storyNode
                })
                .ToList();
        }

        // ── Ver no cronograma (botão direito na célula) ──────────────────────
        private DataRow? _ctxRow;
        private string? _ctxCol;

        private void OnGridPreviewMouseRightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Botão direito no cabeçalho: filtro estilo Excel da coluna.
            var header = FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(e.OriginalSource as DependencyObject);
            if (header != null)
            {
                e.Handled = true;
                ShowHeaderFilterMenu(header);
                return;
            }

            var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
            _ctxRow = (cell?.DataContext as DataRowView)?.Row;
            _ctxCol = cell?.Column?.Header?.ToString();
        }

        private void OnViewInScheduleClick(object sender, RoutedEventArgs e)
        {
            if (_data == null || _ctxRow == null || _ctxCol == null) return;
            if (_vm?.Project == null || _vm.Project.Tasks.Count == 0)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_NoSchedule"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dr = _ctxRow;
            var flat = Flatten(_vm.Project.Tasks).ToList();
            var idCol      = FindColumn("ID Devops", "ID DevOps", "IdDevops", "ID_Devops", "ID Dev Ops", "ID Task", "IdTask", "ID_Task");
            var taskCol    = FindColumn("Task", "Tarefa", "Nome da Task");
            var storyCol   = FindColumn("Story", "Nome da Story");
            var featureCol = FindColumn("Feature", "Nome da Feature");
            var story   = storyCol   != null ? dr[storyCol]?.ToString()?.Trim()   : null;
            var feature = featureCol != null ? dr[featureCol]?.ToString()?.Trim() : null;
            var epic    = _epicColumn != null ? dr[_epicColumn]?.ToString()?.Trim() : null;

            // Coluna do ID Devops conta como Task.
            var type = HierarchyType(_ctxCol)
                ?? (string.Equals(_ctxCol, idCol, StringComparison.OrdinalIgnoreCase) ? "Task" : null);
            if (type == null) return;

            ProjectTask? target = null;
            switch (type)
            {
                case "Task":
                    // Primeiro pelo ID (:T = TfsId, :I = Id interno), depois pelo nome.
                    var idText = idCol != null ? dr[idCol]?.ToString()?.Trim() ?? "" : "";
                    if (idText.EndsWith(":T", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(idText[..^2], out var tid))
                        target = flat.FirstOrDefault(t => t.TfsId == tid);
                    else if (idText.EndsWith(":I", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(idText[..^2], out var iid))
                        target = flat.FirstOrDefault(t => t.Id == iid);
                    if (target == null && taskCol != null)
                    {
                        var taskName = dr[taskCol]?.ToString()?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(taskName))
                            target = FindTaskInSchedule(flat, taskName, story, feature, epic);
                    }
                    break;
                case "Story":
                    target = FindStoryInSchedule(flat, story, feature, epic);
                    break;
                default: // Feature / Epic
                    var value = dr[_ctxCol]?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(value))
                        target = flat.FirstOrDefault(t => IsType(t, type)
                            && string.Equals((t.Name ?? "").Trim(), value, StringComparison.OrdinalIgnoreCase));
                    break;
            }

            if (target == null)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_ViewNotFound"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Encolhe o Task Plan e encosta na lateral direita, deixando o Gantt visível ao lado.
            DockAside();
            (Owner as CommunityMainWindow)?.FocusTaskInSchedule(target);
        }

        // Reduz a janela e posiciona na borda direita da área de trabalho.
        private void DockAside()
        {
            var wa = SystemParameters.WorkArea;
            WindowState = WindowState.Normal;
            Width = 420;
            Height = Math.Min(560, wa.Height);
            Left = wa.Right - Width;
            Top = wa.Top + (wa.Height - Height) / 2;
        }

        // Converte "#RRGGBB" (coluna __c_*) em pincel para o fundo da célula.
        private sealed class HexBrushConverter : System.Windows.Data.IValueConverter
        {
            public static readonly HexBrushConverter Instance = new();

            public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            {
                var s = value as string;
                if (string.IsNullOrWhiteSpace(s)) return DependencyProperty.UnsetValue;
                try
                {
                    var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s);
                    return new System.Windows.Media.SolidColorBrush(color);
                }
                catch { return DependencyProperty.UnsetValue; }
            }

            public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
                => System.Windows.Data.Binding.DoNothing;
        }

        // Aplica a cor (hex; vazio = sem cor) nas células selecionadas — como no Excel.
        private void OnCellColorClick(object sender, RoutedEventArgs e)
        {
            if (_data == null || sender is not MenuItem mi) return;
            var hex = mi.Tag as string ?? "";
            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            PushUndo();

            var cells = PlanGrid.SelectedCells.ToList();
            bool newColumns = false;
            foreach (var cell in cells)
            {
                if (cell.Item is not DataRowView drv) continue;
                var colName = cell.Column?.Header?.ToString();
                if (colName == null || colName.StartsWith("__", StringComparison.Ordinal)
                    || !_data.Table.Columns.Contains(colName)) continue;

                var cc = ExcelTaskPlanService.ColorColPrefix + colName;
                if (!_data.Table.Columns.Contains(cc))
                {
                    _data.Table.Columns.Add(cc, typeof(string));
                    newColumns = true;
                }
                drv.Row[cc] = hex;
            }

            _dirty = true;
            if (newColumns)
                RebindGrid();   // regenera as colunas para aplicar o estilo de cor
            else
                PlanGrid.Items.Refresh();
        }

        // ── operações tipo Excel (menu do botão direito) ─────────────────────
        private void InsertRow(bool above)
        {
            if (_data == null) return;
            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            PushUndo();
            var idx = _ctxRow != null ? _data.Table.Rows.IndexOf(_ctxRow) : -1;
            var newRow = _data.Table.NewRow();
            if (idx < 0)
                _data.Table.Rows.Add(newRow);
            else
                _data.Table.Rows.InsertAt(newRow, above ? idx : idx + 1);
            _dirty = true;
            PlanGrid.Items.Refresh();
        }

        private void OnInsertRowAboveClick(object sender, RoutedEventArgs e) => InsertRow(above: true);
        private void OnInsertRowBelowClick(object sender, RoutedEventArgs e) => InsertRow(above: false);

        private void OnClearCellsClick(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;
            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            PushUndo();
            foreach (var cell in PlanGrid.SelectedCells)
            {
                if (cell.Item is not DataRowView drv) continue;
                var col = cell.Column?.Header?.ToString();
                if (col != null && _data.Table.Columns.Contains(col))
                    drv.Row[col] = "";
            }
            _dirty = true;
            ValidateAgainstSchedule();
            PlanGrid.Items.Refresh();
        }

        private void OnDeleteRowsClick(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;
            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var rows = PlanGrid.SelectedCells
                .Select(c => (c.Item as DataRowView)?.Row)
                .Where(r => r != null)
                .Distinct()
                .ToList();
            if (rows.Count == 0 && _ctxRow != null) rows.Add(_ctxRow);
            if (rows.Count == 0) return;

            var confirm = MessageBox.Show(this, AppStrings.Get("TaskPlan_DeleteRowsConfirm", rows.Count),
                AppStrings.Get("TaskPlan_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            PushUndo();
            foreach (var r in rows)
                _data.Table.Rows.Remove(r!);
            _dirty = true;
            PlanGrid.Items.Refresh();
        }

        // Abrir no TFS: botão direito numa célula de ID (ID Feature, ID Story ou ID Devops)
        // com valor "{n}:T" abre o work item no navegador.
        private void OnOpenInTfsClick(object sender, RoutedEventArgs e)
        {
            if (_ctxRow == null || _ctxCol == null) return;

            var idCols = new[] { FeatureIdCol, StoryIdCol,
                FindColumn("ID Devops", "ID DevOps", "IdDevops", "ID_Devops", "ID Dev Ops", "ID Task", "IdTask", "ID_Task") };
            if (!idCols.Any(c => c != null && string.Equals(c, _ctxCol, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_OpenInTfsNoId"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var value = _ctxRow[_ctxCol]?.ToString()?.Trim() ?? "";
            if (!value.EndsWith(":T", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(value[..^2], out var tfsId) || tfsId <= 0)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_OpenInTfsNoId"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var conn = TfsConnectionStore.Load("NXProject.Community");
            if (string.IsNullOrWhiteSpace(conn.OrganizationUrl) || string.IsNullOrWhiteSpace(conn.TeamProject))
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_PickNoDevOps"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var url = $"{conn.OrganizationUrl.TrimEnd('/')}/{Uri.EscapeDataString(conn.TeamProject.Trim())}/_workitems/edit/{tfsId}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }

        // Sobe a árvore visual/lógica até encontrar o ancestral do tipo pedido.
        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private static T? FindDescendant<T>(DependencyObject? d) where T : DependencyObject
        {
            if (d == null) return null;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                if (child is T t) return t;
                if (FindDescendant<T>(child) is { } found) return found;
            }
            return null;
        }

        // Clique único nas colunas de combo (Aprovada/Status) já entra em edição e
        // abre o dropdown — sem isso a combo só aparece no duplo clique.
        private void OnGridPreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
                return;
            var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell is not { IsEditing: false, IsReadOnly: false } || cell.Column is not DataGridComboBoxColumn)
                return;

            PlanGrid.CurrentCell = new DataGridCellInfo(cell);
            PlanGrid.BeginEdit();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (FindDescendant<ComboBox>(cell) is { } combo)
                    combo.IsDropDownOpen = true;
            });
        }

        // Escreve o objeto escolhido na célula e traz os dados da hierarquia (pais; e ID/prioridade para Task).
        private void ApplyPickedItem(DataRow dr, string colName, string type, ProjectTask picked)
        {
            dr[colName] = picked.Name ?? "";

            // Preenche o pai; se a linha já tem outro valor, pergunta se muda para o do DevOps.
            void Fill(string tfsType, string? col)
            {
                if (col == null) return;
                var name = Ancestor(picked, tfsType);
                if (string.IsNullOrEmpty(name) || string.Equals(name, picked.Name, StringComparison.OrdinalIgnoreCase))
                    return;

                var current = dr[col]?.ToString()?.Trim() ?? "";
                if (current.Length > 0 && !string.Equals(current, name, StringComparison.OrdinalIgnoreCase))
                {
                    var r = MessageBox.Show(this,
                        AppStrings.Get("TaskPlan_PickChangeParent", col, current, name),
                        AppStrings.Get("TaskPlan_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) return;
                }
                dr[col] = name;
            }

            if (type is "Task" or "Story" or "Feature")
                Fill("Epic", _epicColumn);
            if (type is "Task" or "Story")
                Fill("Feature", FindColumn("Feature", "Nome da Feature"));
            if (type is "Task")
            {
                Fill("Story", FindColumn("Story", "Nome da Story"));
                var idCol = FindColumn("ID Devops", "ID DevOps", "IdDevops", "ID_Devops", "ID Dev Ops", "ID Task", "IdTask", "ID_Task");
                if (idCol != null)
                {
                    dr[idCol] = picked.TfsId is > 0 ? $"{picked.TfsId.Value}:T" : $"{picked.Id}:I";
                    if (picked.TfsId is > 0 && ApprovalCol is { } ac)
                        dr[ac] = "Sim";
                }
                var prioCol = FindColumn("Prioridade", "Priority", "Prio", "Prioridade Task");
                if (prioCol != null && picked.Priority is int prio)
                    dr[prioCol] = prio.ToString();
                var estCol = FindColumn("Estimado HH", "Estimado", "Estimativa", "HH Estimado", "Estimated", "HH");
                if (estCol != null && picked.EstimatedHours is > 0)
                    dr[estCol] = picked.EstimatedHours.Value.ToString("0.##");
            }
        }

        // ── buscar Task no DevOps (via cronograma aberto) ────────────────────
        private async void OnFetchDevOpsClick(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;

            if (_vm?.Project == null || _vm.Project.Tasks.Count == 0)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_NoSchedule"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var idCol      = FindColumn("ID Devops", "ID DevOps", "IdDevops", "ID_Devops", "ID Dev Ops", "ID Task", "IdTask", "ID_Task");
            var taskCol    = FindColumn("Task", "Tarefa", "Nome da Task");
            var storyCol   = FindColumn("Story", "Nome da Story");
            var featureCol = FindColumn("Feature", "Nome da Feature");
            var prioCol    = FindColumn("Prioridade", "Priority", "Prio", "Prioridade Task");
            var estCol     = FindColumn("Estimado HH", "Estimado", "Estimativa", "HH Estimado", "Estimated", "HH");
            if (idCol == null || taskCol == null)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_FetchNeedCols"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            PushUndo();
            ReviewDuplicateIds(idCol);

            var flat = Flatten(_vm.Project.Tasks).ToList();
            int matched = 0, internalAssigned = 0;
            int nextInternal = NextInternalId(idCol);

            // Cache das Tasks filhas por Story (buscadas no DevOps sob demanda —
            // no cronograma as Tasks normalmente não estão presentes).
            var devOpsCache = new Dictionary<int, List<TfsImportService.DevOpsTaskInfo>>();
            var options = TfsConnectionStore.Load("NXProject.Community");
            bool canQueryDevOps = !string.IsNullOrWhiteSpace(options.OrganizationUrl)
                               && !string.IsNullOrWhiteSpace(options.PersonalAccessToken);

            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                foreach (DataRow dr in _data.Table.Rows)
                {
                    var cur = dr[idCol]?.ToString()?.Trim() ?? "";
                    if (!IsApprovedOrAlreadyTfs(dr, idCol)) continue;
                    // :T já vinculada ao DevOps; vazia ou :I são reavaliadas — o interno
                    // pode ter virado DevOps desde a última busca (aí promove para :T).
                    if (cur.EndsWith(":T", StringComparison.OrdinalIgnoreCase)) continue;
                    bool hadInternal = cur.EndsWith(":I", StringComparison.OrdinalIgnoreCase);

                    var taskName = dr[taskCol]?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrEmpty(taskName)) continue;

                    var story   = storyCol   != null ? dr[storyCol]?.ToString()?.Trim()   : null;
                    var feature = featureCol != null ? dr[featureCol]?.ToString()?.Trim() : null;
                    var epic    = _epicColumn != null ? dr[_epicColumn]?.ToString()?.Trim() : null;

                    // 1) Task já no cronograma?
                    var match = FindTaskInSchedule(flat, taskName, story, feature, epic);
                    if (match?.TfsId is > 0)
                    {
                        // Padrão do cronograma: DevOps = "{TfsId}:T".
                        dr[idCol] = $"{match.TfsId!.Value}:T";
                        if (ApprovalCol is { } ac) dr[ac] = "Sim";
                        if (prioCol != null && match.Priority is int mp)
                            dr[prioCol] = mp.ToString();
                        if (estCol != null && match.EstimatedHours is > 0)
                            dr[estCol] = match.EstimatedHours.Value.ToString("0.##");
                        matched++;
                        continue;
                    }

                    // 2) Acha a Story no cronograma e busca as Tasks filhas direto no DevOps.
                    TfsImportService.DevOpsTaskInfo? devTask = null;
                    var storyNode = FindStoryInSchedule(flat, story, feature, epic);
                    if (canQueryDevOps && storyNode?.TfsId is > 0)
                    {
                        if (!devOpsCache.TryGetValue(storyNode.TfsId.Value, out var children))
                        {
                            children = await TfsImportService.FetchChildTasksFromDevOpsAsync(
                                options, storyNode.TfsId.Value) ?? new List<TfsImportService.DevOpsTaskInfo>();
                            devOpsCache[storyNode.TfsId.Value] = children;
                        }
                        devTask = children.FirstOrDefault(t =>
                            string.Equals(t.Title.Trim(), taskName, StringComparison.OrdinalIgnoreCase));
                    }

                    if (devTask != null)
                    {
                        dr[idCol] = $"{devTask.TfsId}:T";
                        if (ApprovalCol is { } ac) dr[ac] = "Sim";
                        if (PercConclusaoCol is { } pcc) dr[pcc] = Math.Round(devTask.PercentComplete).ToString("0");
                        if (prioCol != null)
                            dr[prioCol] = devTask.Priority.ToString();
                        if (estCol != null && devTask.EstimatedHours > 0)
                            dr[estCol] = devTask.EstimatedHours.ToString("0.##");
                        matched++;
                    }
                    else if (!hadInternal)
                    {
                        // Padrão do cronograma: interno = "{n}:I". Linha que JÁ tinha :I
                        // e continua sem correspondência no DevOps mantém o ID atual.
                        dr[idCol] = $"{nextInternal++}:I";
                        internalAssigned++;
                    }
                }
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
            }

            if (matched + internalAssigned > 0) _dirty = true;
            ValidateAgainstSchedule();
            PlanGrid.Items.Refresh();
            StatusText.Foreground = System.Windows.Media.Brushes.Green;
            StatusText.Text = AppStrings.Get("TaskPlan_FetchDone", matched, internalAssigned);
        }

        // ── aplicar ao cronograma (cria as tasks do plano no cronograma) ─────
        private async void OnApplyScheduleClick(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;

            if (_vm?.Project == null || _vm.Project.Tasks.Count == 0)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_NoSchedule"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var idCol      = FindColumn("ID Devops", "ID DevOps", "IdDevops", "ID_Devops", "ID Dev Ops", "ID Task", "IdTask", "ID_Task");
            var taskCol    = FindColumn("Task", "Tarefa", "Nome da Task");
            var storyCol   = FindColumn("Story", "Nome da Story");
            var featureCol = FindColumn("Feature", "Nome da Feature");
            var descCol    = FindColumn("Descrição da Task", "Descricao da Task", "Descrição", "Descricao", "Description");
            var prioCol    = FindColumn("Prioridade", "Priority", "Prio", "Prioridade Task");
            var estCol     = FindColumn("Estimado HH", "Estimado", "Estimativa", "HH Estimado", "Estimated", "HH");
            var pctCol     = PercConclusaoCol;
            var statusCol  = FindColumn("Status", "Estado", "State");
            var resourceCol = ResourceCol;
            if (idCol == null || taskCol == null || storyCol == null)
            {
                MessageBox.Show(this, AppStrings.Get("TaskPlan_SyncNeedCols"),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
            NormalizeApprovalValues();
            ReviewDuplicateIds(idCol);

            var flat = Flatten(_vm.Project.Tasks).ToList();
            var approvedPlanRows = _data.Table.Rows.Cast<DataRow>()
                .Where(r => IsApprovedOrAlreadyTfs(r, idCol))
                .ToList();

            // Trava: todo EPIC informado nas linhas precisa existir no cronograma (o EPIC
            // é criado no DevOps, não pela planilha). Se faltar, avisa e NÃO aplica.
            if (_epicColumn != null)
            {
                var missingEpics = approvedPlanRows
                    .Where(r => !string.IsNullOrWhiteSpace(r[taskCol]?.ToString()))
                    .Select(r => r[_epicColumn]?.ToString()?.Trim() ?? "")
                    .Where(v => v.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(v => !flat.Any(t => IsType(t, "Epic")
                        && string.Equals((t.Name ?? "").Trim(), v, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                if (missingEpics.Count > 0)
                {
                    MessageBox.Show(this,
                        AppStrings.Get("TaskPlan_ApplyMissingEpics", "• " + string.Join("\n• ", missingEpics)),
                        AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // Trava: a mesma Story não pode ter duas Tasks com o MESMO nome (a chave de
            // vínculo/merge é o nome dentro da Story). Considera EPIC+Feature+Story para
            // não confundir Stories homônimas de Features diferentes.
            var dupTasks = approvedPlanRows
                .Select(r => new
                {
                    Task    = r[taskCol]?.ToString()?.Trim() ?? "",
                    Story   = storyCol   != null ? r[storyCol]?.ToString()?.Trim() ?? "" : "",
                    Feature = featureCol != null ? r[featureCol]?.ToString()?.Trim() ?? "" : "",
                    Epic    = _epicColumn != null ? r[_epicColumn]?.ToString()?.Trim() ?? "" : ""
                })
                .Where(x => x.Task.Length > 0 && x.Story.Length > 0)
                .GroupBy(x => (x.Epic.ToLowerInvariant(), x.Feature.ToLowerInvariant(),
                               x.Story.ToLowerInvariant(), x.Task.ToLowerInvariant()))
                .Where(g => g.Count() > 1)
                .Select(g => $"Story \"{g.First().Story}\": Task \"{g.First().Task}\" (×{g.Count()})")
                .ToList();
            if (dupTasks.Count > 0)
            {
                MessageBox.Show(this,
                    AppStrings.Get("TaskPlan_ApplyDupTasks", "• " + string.Join("\n• ", dupTasks)),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Trava: todo Recurso informado precisa existir como pessoa do cronograma
            // (o nome é a chave da alocação). Se não bater, avisa e NÃO aplica.
            if (resourceCol != null)
            {
                var missingResources = approvedPlanRows
                    .Where(r => !string.IsNullOrWhiteSpace(r[taskCol]?.ToString()))
                    .SelectMany(r => SplitResourceNames(r[resourceCol]?.ToString()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(v => FindScheduleResource(v) == null)
                    .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                if (missingResources.Count > 0)
                {
                    MessageBox.Show(this,
                        AppStrings.Get("TaskPlan_ApplyMissingResources", "• " + string.Join("\n• ", missingResources)),
                        AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            int created = 0, existing = 0, noStory = 0;
            bool scheduleChanged = false;
            var touchedStories = new HashSet<ProjectTask>();
            var mainWin = Owner as CommunityMainWindow;

            // Tasks do DevOps por Story (para criar no cronograma com o ID :T correto,
            // pela mesma rotina da grid de Tasks).
            var devOpsCache = new Dictionary<int, List<TfsImportService.DevOpsTaskInfo>>();
            var pendingDevOps = new Dictionary<ProjectTask, List<TaskReviewRow>>();
            var options = TfsConnectionStore.Load("NXProject.Community");
            bool canQueryDevOps = !string.IsNullOrWhiteSpace(options.OrganizationUrl)
                               && !string.IsNullOrWhiteSpace(options.PersonalAccessToken);

            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                foreach (DataRow dr in approvedPlanRows)
                {
                    var taskName = dr[taskCol]?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrEmpty(taskName)) continue;

                    var story   = dr[storyCol]?.ToString()?.Trim();
                    var feature = featureCol != null ? dr[featureCol]?.ToString()?.Trim() : null;
                    var epic    = _epicColumn != null ? dr[_epicColumn]?.ToString()?.Trim() : null;

                    // Já existe no cronograma? Só garante o ID no padrão e segue.
                    var match = FindTaskInSchedule(flat, taskName, story, feature, epic);
                    if (match != null)
                    {
                        var description = descCol != null ? NormalizeTaskPlanDescription(dr[descCol]?.ToString()) : null;
                        if (description != null && !string.Equals(match.Description ?? "", description, StringComparison.Ordinal))
                        {
                            match.Description = description;
                            scheduleChanged = true;
                        }
                        if (resourceCol != null && AssignResourceFromPlan(match, dr[resourceCol]?.ToString()))
                            scheduleChanged = true;
                        if (ApplyTaskPlanStateAndPercent(match, dr, statusCol, pctCol))
                            scheduleChanged = true;
                        // Observação viaja com a Task: se ainda for interna (:I), o sync
                        // registra o tramite no DevOps assim que ela ganhar o ID do TFS.
                        var matchObs = ObservationCol is { } moCol ? dr[moCol]?.ToString()?.Trim() : null;
                        if (!string.IsNullOrEmpty(matchObs) && !string.Equals(match.PlanObservation ?? "", matchObs, StringComparison.Ordinal))
                        {
                            match.PlanObservation = matchObs;
                            scheduleChanged = true;
                        }
                        dr[idCol] = match.TfsId is > 0 ? $"{match.TfsId.Value}:T" : $"{match.Id}:I";
                        existing++;
                        continue;
                    }

                    // Localiza a Story de destino pela hierarquia; se não existir, cria a
                    // cadeia interna (Feature :I sob o EPIC → Story :I sob a Feature).
                    var storyNode = FindStoryInSchedule(flat, story, feature, epic)
                        ?? CreateStoryPath(flat, story, feature, epic);
                    if (storyNode == null) { noStory++; continue; }

                    // Task do DevOps? Pelo ID :T da célula ou pelo nome, entre as filhas da Story.
                    TfsImportService.DevOpsTaskInfo? devTask = null;
                    if (canQueryDevOps && storyNode.TfsId is > 0)
                    {
                        if (!devOpsCache.TryGetValue(storyNode.TfsId.Value, out var children))
                        {
                            children = await TfsImportService.FetchChildTasksFromDevOpsAsync(
                                options, storyNode.TfsId.Value) ?? new List<TfsImportService.DevOpsTaskInfo>();
                            devOpsCache[storyNode.TfsId.Value] = children;
                        }

                        var cur = dr[idCol]?.ToString()?.Trim() ?? "";
                        if (cur.EndsWith(":T", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(cur[..^2], out var tid))
                            devTask = children.FirstOrDefault(t => t.TfsId == tid);
                        devTask ??= children.FirstOrDefault(t =>
                            string.Equals(t.Title.Trim(), taskName, StringComparison.OrdinalIgnoreCase));
                    }

                    if (devTask != null)
                    {
                        dr[idCol] = $"{devTask.TfsId}:T";
                        if (prioCol != null)
                            dr[prioCol] = devTask.Priority.ToString();

                        if (storyNode.Children.Any(c => c.TfsId == devTask.TfsId))
                        {
                            existing++;
                        }
                        else
                        {
                            if (!pendingDevOps.TryGetValue(storyNode, out var list))
                                pendingDevOps[storyNode] = list = new List<TaskReviewRow>();
                            var planStatus = statusCol != null ? dr[statusCol]?.ToString()?.Trim() : null;
                            var planPercent = ParseTaskPlanPercent(pctCol != null ? dr[pctCol]?.ToString() : null);
                            if (planPercent == null && !string.IsNullOrWhiteSpace(planStatus))
                                planPercent = TfsImportService.PercentCompleteFromState(planStatus);
                            list.Add(new TaskReviewRow
                            {
                                StoryTask       = storyNode,
                                TaskId          = devTask.TfsId,
                                Title           = devTask.Title,
                                Description     = NormalizeTaskPlanDescription(descCol != null ? dr[descCol]?.ToString() : null)
                                                  ?? devTask.Description ?? "",
                                State           = !string.IsNullOrWhiteSpace(planStatus)
                                    ? planStatus
                                    : devTask.State ?? "New",
                                EstimatedHours  = devTask.EstimatedHours,
                                CompletedHours  = devTask.CompletedHours,
                                PercentComplete = planPercent ?? devTask.PercentComplete,
                                Priority        = devTask.Priority,
                                BacklogRank     = devTask.BacklogRank,
                                AssignedTo        = devTask.AssignedTo ?? "",
                                AssignedToDisplay = devTask.AssignedToDisplay ?? devTask.AssignedTo ?? "",
                            });
                            created++;
                        }
                        touchedStories.Add(storyNode);
                        continue;
                    }

                    // Não existe no DevOps: cria interna pelas regras compartilhadas
                    // (padrão do AddSubtask: TfsId=0 → "{Id}:I"; duração respeitando o estado da Story).
                    // Estimado HH zero/nulo → 1h padrão.
                    var hours = TaskPlanScheduleRules.ParseEstimatedHours(estCol != null ? dr[estCol]?.ToString() : null) ?? 1.0;
                    var task = TaskPlanScheduleRules.CreateInternalTask(
                        storyNode, _vm.NextId(), taskName,
                        descCol != null ? NormalizeTaskPlanDescription(dr[descCol]?.ToString()) : null, hours);
                    if (prioCol != null && int.TryParse(dr[prioCol]?.ToString()?.Trim(), out var prio))
                        task.Priority = prio;
                    if (resourceCol != null)
                        AssignResourceFromPlan(task, dr[resourceCol]?.ToString());
                    ApplyTaskPlanStateAndPercent(task, dr, statusCol, pctCol);
                    if (ObservationCol is { } noCol && dr[noCol]?.ToString()?.Trim() is { Length: > 0 } newObs)
                        task.PlanObservation = newObs;

                    storyNode.Children.Add(task);
                    storyNode.IsSummary = true;
                    touchedStories.Add(storyNode);
                    flat.Add(task);
                    dr[idCol] = $"{task.Id}:I";
                    // Marca a origem (planilha + ID interno) para, após sincronizar, atualizar
                    // o ID na planilha de origem (só quando a planilha já tem arquivo salvo).
                    if (!string.IsNullOrEmpty(_path))
                    {
                        task.SourcePlanPath = _path;
                        task.SourcePlanRowKey = $"{task.Id}:I";
                    }
                    created++;
                }
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
            }

            // Aplica as Tasks do DevOps pela mesma rotina da grid de Tasks do cronograma.
            foreach (var kv in pendingDevOps)
                mainWin?.AddDevOpsTasksToStory(kv.Value, kv.Key);

            // Só recalcula (podendo mudar a duração) as Stories em New/0%; as demais mantêm o período.
            foreach (var s in touchedStories.Where(TaskPlanScheduleRules.CanAdjustStoryDuration))
                s.RecalcSummary();

            if (created > 0 || scheduleChanged)
            {
                _vm.Project.IsDirty = true;
                // Associa a planilha ao cronograma (persistido no .nxp) para o backfill pós-sync.
                if (!string.IsNullOrEmpty(_path)) _vm.Project.PlanSheetPath = _path;
                _vm.RebuildFlatTasks();
            }

            if (created + existing > 0) _dirty = true;
            ValidateAgainstSchedule();
            PlanGrid.Items.Refresh();

            // Observações viram tramite (comentário) da Task no DevOps — sem duplicar:
            // só registra quando o texto difere do último comentário do work item.
            int obsPosted = 0;
            if (canQueryDevOps)
                obsPosted = await PostObservationsAsCommentsAsync(options, approvedPlanRows, idCol);

            StatusText.Foreground = System.Windows.Media.Brushes.Green;
            StatusText.Text = AppStrings.Get("TaskPlan_SyncDone", created, existing, noStory)
                + (obsPosted > 0 ? " " + AppStrings.Get("TaskPlan_ObsPosted", obsPosted) : "");
        }

        // Coluna de observações da planilha (registrada como tramite no DevOps ao aplicar).
        private string? ObservationCol => FindColumn(
            "Observações", "Observacoes", "Observação", "Observacao", "Obs", "Notes", "Comentário", "Comentario");

        private static double? ParseTaskPlanPercent(string? text)
        {
            var raw = (text ?? string.Empty).Trim().TrimEnd('%').Trim();
            if (raw.Length == 0) return null;
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture, out var value)
                && !double.TryParse(raw.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
                return null;
            return Math.Clamp(value, 0, 100);
        }

        private static bool ApplyTaskPlanStateAndPercent(ProjectTask task, DataRow row, string? statusCol, string? pctCol)
        {
            bool changed = false;
            var status = statusCol != null ? row[statusCol]?.ToString()?.Trim() : null;
            var pct = pctCol != null ? ParseTaskPlanPercent(row[pctCol]?.ToString()) : null;
            if (pct == null && !string.IsNullOrWhiteSpace(status))
                pct = TfsImportService.PercentCompleteFromState(status);

            if (!string.IsNullOrWhiteSpace(status)
                && !string.Equals(task.TfsState ?? "", status, StringComparison.Ordinal))
            {
                task.TfsState = status;
                changed = true;
            }
            if (pct.HasValue && Math.Abs(task.PercentComplete - pct.Value) > 0.0001)
            {
                task.PercentComplete = pct.Value;
                changed = true;
            }
            return changed;
        }

        /// <summary>Load Task/Merge: traz o último tramite (comentário) do DevOps para a coluna
        /// Observações — assim o Aplicar não registra de novo o que já é o último tramite.</summary>
        private async Task FillObservationFromLastCommentAsync(
            TfsConnectionOptions options, DataRow row, TfsImportService.DevOpsTaskInfo info)
        {
            if (ObservationCol is not { } obsCol || info.CommentCount <= 0) return;
            try
            {
                var last = TfsImportService.NormalizeCommentText(
                    await TfsImportService.GetLastWorkItemCommentAsync(options, info.TfsId));
                if (last.Length == 0) return;
                if (!string.Equals(row[obsCol]?.ToString()?.Trim() ?? "", last, StringComparison.Ordinal))
                {
                    row[obsCol] = last;
                    _dirty = true;
                }
            }
            catch { /* sem o tramite, a coluna fica como está */ }
        }

        /// <summary>Registra a Observação das linhas :T como comentário da Task no DevOps,
        /// pulando as que já são o último comentário. Retorna quantas foram registradas.</summary>
        private async Task<int> PostObservationsAsCommentsAsync(
            TfsConnectionOptions options, List<DataRow> rows, string idCol)
        {
            if (ObservationCol is not { } obsCol) return 0;

            int posted = 0;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                foreach (var dr in rows)
                {
                    var obs = dr[obsCol]?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(obs)) continue;

                    var id = dr[idCol]?.ToString()?.Trim() ?? "";
                    if (!id.EndsWith(":T", StringComparison.OrdinalIgnoreCase)
                        || !int.TryParse(id[..^2], out var tfsId) || tfsId <= 0)
                        continue;

                    try
                    {
                        if (await TfsImportService.AddWorkItemCommentIfChangedAsync(options, tfsId, obs))
                            posted++;
                    }
                    catch { /* falha pontual de rede não interrompe o Aplicar */ }
                }
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
            }
            return posted;
        }

        private static string? NormalizeTaskPlanDescription(string? value)
        {
            var text = value?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        // Cria a cadeia interna que faltar para receber a Task: Feature :I (sob o EPIC
        // existente) e Story :I (sob a Feature). Padrão do AddSubtask: TfsId=0 ("criar
        // no TFS" → DisplayId "{Id}:I"), estado New. Exige ao menos o EPIC no cronograma.
        private ProjectTask? CreateStoryPath(List<ProjectTask> flat, string? story, string? feature, string? epic)
        {
            if (_vm == null || string.IsNullOrWhiteSpace(story)) return null;

            ProjectTask NewNode(string name, string tfsType, ProjectTask parent)
            {
                var node = new ProjectTask
                {
                    Id = _vm.NextId(),
                    Name = name,
                    TfsType = tfsType,
                    TfsId = 0,
                    TfsState = "New",
                    Level = parent.Level + 1,
                    Parent = parent,
                    TfsIterationPath = parent.TfsIterationPath,
                    SprintNumber = parent.SprintNumber,
                    Start = parent.Start,
                    Finish = parent.Finish
                };
                parent.Children.Add(node);
                parent.IsSummary = true;
                flat.Add(node);
                return node;
            }

            bool NameEq(ProjectTask t, string? n) =>
                string.Equals((t.Name ?? "").Trim(), (n ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

            // EPIC precisa existir no cronograma (não criamos EPIC pela planilha).
            var epicNode = string.IsNullOrWhiteSpace(epic)
                ? null
                : flat.FirstOrDefault(t => IsType(t, "Epic") && NameEq(t, epic));

            // Feature: existente sob o EPIC, ou criada sob ele.
            ProjectTask? featureNode = null;
            if (!string.IsNullOrWhiteSpace(feature))
            {
                featureNode = flat.FirstOrDefault(t => IsType(t, "Feature") && NameEq(t, feature)
                    && (epicNode == null || string.Equals(Ancestor(t, "Epic").Trim(), epic!.Trim(), StringComparison.OrdinalIgnoreCase)));
                if (featureNode == null)
                {
                    if (epicNode == null) return null;   // sem EPIC não há onde pendurar a Feature
                    featureNode = NewNode(feature!.Trim(), "Feature", epicNode);
                }
            }

            var storyParent = featureNode ?? epicNode;
            if (storyParent == null) return null;

            return NewNode(story!.Trim(), "User Story", storyParent);
        }

        // Story de destino: nome igual E hierarquia respeitada — com Feature informada,
        // a Story TEM que estar naquela Feature (sem Feature, vale o EPIC). Sem fallback:
        // Story homônima de outra Feature/EPIC não casa.
        private static ProjectTask? FindStoryInSchedule(List<ProjectTask> flat, string? story, string? feature, string? epic)
        {
            if (string.IsNullOrWhiteSpace(story)) return null;
            bool Match(string a, string? b) =>
                string.Equals(a.Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

            var cands = flat.Where(t => IsType(t, "Story")
                && string.Equals((t.Name ?? "").Trim(), story!.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

            if (!string.IsNullOrWhiteSpace(feature))
                cands = cands.Where(t => Match(Ancestor(t, "Feature"), feature)).ToList();
            if (!string.IsNullOrWhiteSpace(epic))
                cands = cands.Where(t => Match(Ancestor(t, "Epic"), epic)).ToList();

            return cands.FirstOrDefault(t => t.TfsId is > 0) ?? cands.FirstOrDefault();
        }

        // Revisão de IDs de Task duplicados (ex.: copiar/colar): interno (:I) repetido
        // ganha um ID novo automaticamente; DevOps (:T) repetido só avisa (a mesma task
        // real não pode estar em duas linhas).
        private void ReviewDuplicateIds(string idCol)
        {
            if (_data == null) return;
            var seenInternal = new HashSet<int>();
            var devOpsCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int regenerated = 0;
            int next = NextInternalId(idCol);

            foreach (DataRow dr in _data.Table.Rows)
            {
                var v = dr[idCol]?.ToString()?.Trim() ?? "";
                if (v.EndsWith(":I", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(v[..^2], out var n))
                {
                    if (!seenInternal.Add(n))
                    {
                        dr[idCol] = $"{next}:I";
                        seenInternal.Add(next);
                        next++;
                        regenerated++;
                    }
                }
                else if (v.EndsWith(":T", StringComparison.OrdinalIgnoreCase))
                {
                    devOpsCount[v] = devOpsCount.TryGetValue(v, out var c) ? c + 1 : 1;
                }
            }

            if (regenerated > 0)
            {
                _dirty = true;
                StatusText.Foreground = System.Windows.Media.Brushes.Gray;
                StatusText.Text = AppStrings.Get("TaskPlan_DupInternalFixed", regenerated);
            }

            var dups = devOpsCount.Where(kv => kv.Value > 1).Select(kv => $"{kv.Key} (×{kv.Value})").ToList();
            if (dups.Count > 0)
                MessageBox.Show(this, AppStrings.Get("TaskPlan_DupDevOpsIds", string.Join("\n• ", dups)),
                    AppStrings.Get("TaskPlan_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private string? FindColumn(params string[] candidates)
        {
            if (_data == null) return null;
            string Norm(string s) => s.Replace(" ", "").Replace("_", "").ToLowerInvariant();
            foreach (var col in _data.Table.Columns.Cast<DataColumn>())
                foreach (var cand in candidates)
                    if (Norm(col.ColumnName) == Norm(cand))
                        return col.ColumnName;
            return null;
        }

        private int NextInternalId(string idCol)
        {
            int max = 0;
            foreach (DataRow dr in _data!.Table.Rows)
            {
                var v = dr[idCol]?.ToString()?.Trim() ?? "";
                if (v.EndsWith(":I", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(v.Substring(0, v.Length - 2), out var n) && n > max)
                    max = n;
            }
            return max + 1;
        }

        // Casa por nome seguindo a hierarquia EPIC → Feature → Story → Task.
        private static ProjectTask? FindTaskInSchedule(List<ProjectTask> flat, string task, string? story, string? feature, string? epic)
        {
            bool Eq(string? a, string? b) => string.IsNullOrWhiteSpace(b)
                || string.Equals((a ?? "").Trim(), b!.Trim(), StringComparison.OrdinalIgnoreCase);

            var cands = flat.Where(t => string.Equals((t.Name ?? "").Trim(), task, StringComparison.OrdinalIgnoreCase)).ToList();
            if (cands.Count == 0) return null;

            // Com Story informada na linha, a Task só casa DENTRO daquela Story — nunca
            // reutiliza uma task de mesmo nome de outra Story/Feature.
            if (!string.IsNullOrWhiteSpace(story))
            {
                var inStory = cands.Where(t => Eq(Ancestor(t, "Story"), story)).ToList();
                return inStory.FirstOrDefault(t => t.TfsId is > 0 && Eq(Ancestor(t, "Feature"), feature) && Eq(Ancestor(t, "Epic"), epic))
                    ?? inStory.FirstOrDefault(t => t.TfsId is > 0 && Eq(Ancestor(t, "Feature"), feature))
                    ?? inStory.FirstOrDefault(t => t.TfsId is > 0)
                    ?? inStory.FirstOrDefault();
            }

            return cands.FirstOrDefault(t => t.TfsId is > 0 && Eq(Ancestor(t, "Feature"), feature) && Eq(Ancestor(t, "Epic"), epic))
                ?? cands.FirstOrDefault(t => t.TfsId is > 0)
                ?? cands.FirstOrDefault();
        }

        private static string Ancestor(ProjectTask task, string tfsType)
        {
            for (var p = task; p != null; p = p.Parent)
                if (IsType(p, tfsType))
                    return p.Name;
            return string.Empty;
        }

        // "Story" também casa com "User Story" (nome usado pelo DevOps/cronograma).
        private static bool IsType(ProjectTask t, string tfsType)
        {
            var v = t.TfsType?.Trim() ?? "";
            if (string.Equals(v, tfsType, StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(tfsType, "Story", StringComparison.OrdinalIgnoreCase)
                && string.Equals(v, "User Story", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<ProjectTask> Flatten(IEnumerable<ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                yield return t;
                if (t.Children != null)
                    foreach (var c in Flatten(t.Children))
                        yield return c;
            }
        }
    }
}
