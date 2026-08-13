using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NXProject.Models;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Views
{
    /// <summary>
    /// Chat de análise do cronograma com IA: conversa contínua sobre o projeto aberto.
    /// Por padrão abre em "Conversa aberta (análise livre)"; o combo permite escolher uma
    /// ação específica (mesmos prompts da tela IA Geral). Usa o provedor padrão configurado
    /// e liga o indicador global "IA em execução" (mostra no cronograma qual IA está rodando).
    /// </summary>
    public partial class CommunityAIChatWindow : Window
    {
        private const string StorageKey = "NXProject.Community";

        private readonly MainViewModel _viewModel;

        // Uma mensagem: papel, texto e (para respostas da IA) quando chegou e quanto demorou.
        private sealed class ChatMsg
        {
            public string Role { get; set; } = string.Empty;
            public string Text { get; set; } = string.Empty;
            public DateTime? Time { get; set; }        // carimbo de data/hora do retorno
            public double? DurationSec { get; set; }   // tempo de resposta da IA (segundos)
        }

        // Cada conversa tem seu título (lado esquerdo, estilo navegador) e histórico próprio.
        private sealed class Conversation
        {
            public string Title { get; set; } = string.Empty;
            public List<ChatMsg> History { get; } = new();
            // "Compress": resumo das mensagens antigas + índice já resumido (evita reenviar tudo).
            public string Summary { get; set; } = string.Empty;
            public int SummarizedFrom { get; set; }
        }
        private readonly System.Collections.ObjectModel.ObservableCollection<Conversation> _conversations = new();
        private Conversation _conv = new();
        private List<ChatMsg> _history => _conv.History;

        private string _projectContext = string.Empty;
        private CancellationTokenSource? _cts;
        private bool _chatWebReady;
        private string _pendingChatHtml = string.Empty;
        private bool _thinking;   // exibe a bolha "analisando..." enquanto a IA responde
        private AIScheduleResponse? _lastSchedule;   // última proposta de cronograma (modo cronograma)

        // Relógio de contagem regressiva (ETA por histórico + estimativa da IA):
        private DispatcherTimer? _countdown;
        private int _etaRemaining;                   // segundos restantes exibidos no badge
        private string _runProvider = string.Empty;  // contexto do run em andamento (p/ gravar histórico)
        private string _runAction = string.Empty;
        private string _runSchedule = string.Empty;
        private long _runPayloadBytes;
        private readonly Stopwatch _runWatch = new();

        // Item do combo de ação: "Label" para a UI, prompt e nome interno.
        private sealed class ActionItem
        {
            public string Label { get; init; } = string.Empty;
            public string Prompt { get; init; } = string.Empty;
            public bool IsOpenChat { get; init; }
        }

        // Persona de análise para o modo "Conversa aberta".
        private const string AnalystSystemPrompt =
            "Você é um analista de cronogramas de projeto do NXProject. Responda em português, de forma " +
            "objetiva e prática, ajudando a analisar o cronograma fornecido em CONTEXTO: prazos, caminho " +
            "crítico, riscos de atraso, sobrecarga e disponibilidade de recursos, dependências, esforço e " +
            "distribuição de HH, marcos e coerência da estrutura EPIC/Feature/Story/Task. Use os dados do " +
            "CONTEXTO e diga claramente quando algo não estiver no contexto em vez de inventar. Quando fizer " +
            "sentido, sugira ações concretas. Este chat NÃO altera o cronograma — apenas analisa e recomenda.";

        public CommunityAIChatWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;

            LoadActions();
            RefreshProviderLabel();

            ConvList.ItemsSource = _conversations;
            LoadHistory();   // recupera as conversas gravadas deste cronograma

            Loaded += async (_, _) =>
            {
                _projectContext = _viewModel.BuildFullScheduleContext();
                await InitChatWebAsync();
                if (_conv.History.Count == 0) AddSystemNote(AppStrings.Get("AIChat_Welcome"));
                RenderConversation();
                InputBox.Focus();
            };
            Closing += (_, args) =>
            {
                if (_cts != null) { _cts.Cancel(); args.Cancel = true; return; }
                SaveHistory();
            };
        }

        private void LoadActions()
        {
            var items = new List<ActionItem>
            {
                new() { Label = AppStrings.Get("AIChat_OpenConversation"), IsOpenChat = true }
            };
            try
            {
                var ws = AISettingsStore.LoadWorkspace(StorageKey);
                foreach (var a in ws.ActionTypes)
                    items.Add(new ActionItem { Label = a.Name, Prompt = a.Prompt });
            }
            catch { /* sem ações salvas: fica só a conversa aberta */ }

            ActionCombo.ItemsSource = items;
            ActionCombo.SelectedIndex = 0; // conversa aberta como padrão
        }

        private ActionItem CurrentAction =>
            ActionCombo.SelectedItem as ActionItem
            ?? new ActionItem { Label = "", IsOpenChat = true };

        private void OnActionChanged(object sender, SelectionChangedEventArgs e) => UpdateHint();

        private void OnScheduleModeChanged(object sender, RoutedEventArgs e)
        {
            // Ao ligar/desligar o modo cronograma, o combo de ação fica irrelevante.
            ActionCombo.IsEnabled = ScheduleModeCheck.IsChecked != true;
            if (ScheduleModeCheck.IsChecked != true)
                ApplyScheduleButton.Visibility = Visibility.Collapsed;
            UpdateHint();
        }

        private void UpdateHint()
        {
            ActionHint.Text = ScheduleModeCheck.IsChecked == true
                ? AppStrings.Get("AIChat_ScheduleHint")
                : CurrentAction.IsOpenChat
                    ? AppStrings.Get("AIChat_OpenHint")
                    : AppStrings.Get("AIChat_ActionHint");
        }

        private void RefreshProviderLabel()
        {
            try
            {
                var settings = AISettingsStore.LoadWorkspace(StorageKey).ResolveActiveSettings();
                ProviderText.Text = AppStrings.Get("AIChat_Provider", AIProviderDefaults.DescribeActive(settings));
            }
            catch { ProviderText.Text = string.Empty; }
        }

        // ── Envio ────────────────────────────────────────────────────────────
        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            // Enter envia; Shift+Enter quebra linha.
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                _ = SendAsync();
            }
        }

        private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();

        private void OnStopClick(object sender, RoutedEventArgs e)
        {
            // Cancela a execução em andamento (o token vai para o provedor/CLI e encerra a chamada).
            if (_cts is { IsCancellationRequested: false })
            {
                StopButton.IsEnabled = false;
                _cts.Cancel();
                AddSystemNote(AppStrings.Get("AIChat_Stopping"));
            }
        }

        private async System.Threading.Tasks.Task SendAsync()
        {
            var question = InputBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(question) || _cts != null) return;

            var settings = AISettingsStore.LoadWorkspace(StorageKey).ResolveActiveSettings();
            if (!AIProviderDefaults.IsConfigured(settings))
            {
                MessageBox.Show(this, AppStrings.Get("AIChat_NotConfigured"),
                    AppStrings.Get("AIChat_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            InputBox.Clear();
            var isFirst = _history.Count == 0;
            _history.Add(new ChatMsg { Role = "Usuário", Text = question, Time = DateTime.Now });
            if (isFirst) UpdateConversationTitle(question);

            _thinking = true;
            RenderConversation();
            ChatStatus.Text = string.Empty;
            SetBusy(true, settings);

            _cts = new CancellationTokenSource();
            var scheduleMode = ScheduleModeCheck.IsChecked == true;

            // Contexto do run p/ o relógio + histórico: provedor, ação, cronograma e bytes enviados.
            _runProvider = AIProviderDefaults.DescribeActive(settings);
            _runAction = scheduleMode ? "schedule" : (CurrentAction.IsOpenChat ? "chat" : CurrentAction.Label);
            _runSchedule = _viewModel.Project?.Name ?? "?";
            // Volume do payload medido por PALAVRAS DE CONTEÚDO (substantivos/verbos aprox.),
            // não por caracteres: descarta palavras vazias (artigos, preposições, pronomes...),
            // o que dá uma medida mais estável do "tamanho" real da tarefa enviada à IA.
            _runPayloadBytes = CountContentWords(_projectContext) + CountContentWords(question);

            // "Compress": se o histórico passou do limite, resume o trecho antigo ANTES de enviar.
            await MaybeCompactHistoryAsync(settings);

            await StartCountdownAsync(settings, scheduleMode);

            try
            {
                if (scheduleMode)
                {
                    // Proposta de cronograma: a IA devolve a hierarquia EPIC/Feature/Story/Task
                    // (mesmo formato do Assistente); nada é aplicado aqui — só o preview.
                    var schedPrompt = ProjectAIAssistantService.BuildScheduleDeveloperPrompt(untilTask: true)
                        + " Considere o CRONOGRAMA COMPLETO no CONTEXTO como base. "
                        + "No cronograma do NX a FOLHA é normalmente a própria Story (um RESUMO da task); o nível "
                        + "Task é OPCIONAL e a task detalhada fica no TFS/DevOps. NÃO invente Tasks onde o contexto "
                        + "não as tem — mantenha a Story como folha. Preserve a hierarquia do contexto e não remova "
                        + "itens existentes; o mais importante é repetir o mesmo \"id\" de cada item (o NX corrige o "
                        + "tipo/nível pelo id). "
                        + "PRESERVE AS CHAVES: cada item existente no CONTEXTO tem um campo id=... (ex.: 123:T ou 45:I) "
                        + "no seu nível da hierarquia (EPIC/Feature/Story/Task). Ao devolver um item que já existe, "
                        + "repita EXATAMENTE o mesmo id no campo \"id\" do JSON, e devolva também, inalterados, os campos "
                        + "\"predecessors\" (lista de ids, como no pred=... do contexto), \"percent\", \"startFixed\"/\"finishFixed\" "
                        + "e \"fixedStart\"/\"fixedFinish\" (datas AAAA-MM-DD) quando o item os tiver. "
                        + "Se o item tiver hhOriginal=... no contexto, devolva-o inalterado no campo \"originalHours\" "
                        + "(e o HH atual em \"currentHours\"): itens 100% concluídos NÃO podem perder o HH original planejado. "
                        + "SOMENTE itens NOVOS (que não existem no contexto) devem vir com \"id\" vazio. "
                        + "Nunca invente id: se não existe no contexto, é novo. "
                        + "No campo \"summary\" descreva O QUE MUDOU vs. o cronograma atual (itens novos, removidos, "
                        + "renomeados, mudanças de HH/responsável/estrutura); se for do zero, diga isso.";
                    var raw = await ProjectAIAssistantService.GenerateFreeTextAsync(
                        settings, schedPrompt, BuildConversation(question), _projectContext, _cts.Token);
                    _lastSchedule = ProjectAIAssistantService.ParseScheduleResponse(raw ?? string.Empty);

                    var leaves = CountLeaves(_lastSchedule.Roots);
                    var warns = _lastSchedule.Warnings.Count > 0
                        ? "\n\n⚠ " + string.Join("\n⚠ ", _lastSchedule.Warnings)
                        : "";
                    var display = _lastSchedule.Roots.Count == 0
                        ? AppStrings.Get("AIChat_ScheduleEmpty") + warns
                        : (string.IsNullOrWhiteSpace(_lastSchedule.Summary) ? "" : _lastSchedule.Summary + "\n\n")
                          + BuildOutline(_lastSchedule.Roots, 0)
                          + "\n" + AppStrings.Get("AIChat_ScheduleReady", leaves) + warns;
                    ApplyScheduleButton.Visibility = _lastSchedule.Roots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    AddAiMessage(display);
                }
                else
                {
                    var action = CurrentAction;
                    var systemPrompt = action.IsOpenChat || string.IsNullOrWhiteSpace(action.Prompt)
                        ? AnalystSystemPrompt
                        : action.Prompt;

                    var answer = await ProjectAIAssistantService.GenerateFreeTextAsync(
                        settings, systemPrompt, BuildConversation(question), _projectContext, _cts.Token);

                    answer = (answer ?? string.Empty).Trim();
                    AddAiMessage(answer.Length > 0 ? answer : AppStrings.Get("AIChat_Empty"));
                }
            }
            catch (OperationCanceledException)
            {
                AddAiMessage(AppStrings.Get("AIChat_Cancelled"));
            }
            catch (Exception ex)
            {
                AddAiMessage(ex.Message);
            }
            finally
            {
                _thinking = false;
                RenderConversation();
                StopCountdown();
                // Grava a duração real (só se concluiu de verdade — cancelamento não conta).
                if (_runWatch.IsRunning) _runWatch.Stop();
                if (_cts != null && !_cts.IsCancellationRequested && _runWatch.Elapsed.TotalSeconds > 0.5)
                    AiRunStatsStore.Record(StorageKey, _runProvider, _runAction, _runSchedule,
                        _runWatch.Elapsed.TotalSeconds, _runPayloadBytes);

                _cts?.Dispose();
                _cts = null;
                SetBusy(false, settings);
                InputBox.Focus();
                SaveHistory();   // persiste a conversa deste cronograma (aplica o limite)
            }
        }

        // Resposta da IA com carimbo de data/hora e tempo de resposta (do cronômetro do run).
        private void AddAiMessage(string text)
        {
            _history.Add(new ChatMsg
            {
                Role = "IA",
                Text = CollapseRepeats(text),   // não grava repetição feia de modelo em loop
                Time = DateTime.Now,
                DurationSec = _runWatch.Elapsed.TotalSeconds > 0.05 ? _runWatch.Elapsed.TotalSeconds : null
            });
        }

        // Janela deslizante de continuidade: nº de mensagens recentes reenviadas. Vem do
        // Assistente de IA (clampada 2..20 no store); padrão 8. Enviar o histórico INTEIRO
        // estoura tokens em conversas longas.
        private int HistoryWindowMessages()
        {
            try { return AISettingsStore.LoadWorkspace(StorageKey).ChatHistoryWindow; }
            catch { return 8; }
        }
        // Corta cada mensagem antiga muito longa (ex.: um outline gigante) ao reenviar.
        private const int HistoryMessageMaxChars = 1200;

        private int CompressThreshold()
        {
            try { return AISettingsStore.LoadWorkspace(StorageKey).ChatCompressThreshold; }
            catch { return 0; }
        }

        /// <summary>Monta a pergunta com RESUMO (compress) + a JANELA recente (a API é stateless).</summary>
        private string BuildConversation(string newQuestion)
        {
            if (_history.Count <= 1 && string.IsNullOrEmpty(_conv.Summary))
                return newQuestion; // primeira pergunta: sem histórico

            var win = HistoryWindowMessages();
            // Mensagens ainda NÃO resumidas (após o ponto de compress), fora a atual.
            var from = System.Math.Clamp(_conv.SummarizedFrom, 0, System.Math.Max(0, _history.Count - 1));
            var prior = _history.Skip(from).Take(_history.Count - 1 - from).ToList();
            var window = prior.Count > win ? prior.Skip(prior.Count - win).ToList() : prior;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(_conv.Summary))
            {
                sb.AppendLine("RESUMO DA CONVERSA ANTERIOR (compactado):");
                sb.AppendLine(_conv.Summary.Trim());
                sb.AppendLine();
            }
            if (prior.Count > window.Count)
                sb.AppendLine($"(há mais {prior.Count - window.Count} mensagens antes; abaixo as {window.Count} mais recentes)");
            sb.AppendLine("HISTÓRICO RECENTE DA CONVERSA (para continuidade):");
            foreach (var m in window)
            {
                var text = m.Text?.Length > HistoryMessageMaxChars
                    ? m.Text[..HistoryMessageMaxChars] + " […]"
                    : m.Text;
                sb.AppendLine($"[{m.Role}]: {text}");
            }
            sb.AppendLine();
            sb.AppendLine("NOVA PERGUNTA DO USUÁRIO:");
            sb.Append(newQuestion);
            return sb.ToString();
        }

        // "Compress": quando o texto do histórico não-resumido passa do limite (caracteres),
        // pede à IA um resumo compacto do trecho ANTIGO (fora a janela recente) e guarda-o,
        // avançando o ponto resumido. Assim as próximas perguntas mandam resumo + recentes.
        private async System.Threading.Tasks.Task MaybeCompactHistoryAsync(AISettings settings)
        {
            var threshold = CompressThreshold();
            if (threshold <= 0) return;

            var win = HistoryWindowMessages();
            var from = _conv.SummarizedFrom;
            // Fecha o trecho a resumir logo antes da janela recente (deixa as recentes cruas).
            var foldTo = _history.Count - win;
            if (foldTo <= from) return;   // nada novo suficiente para resumir

            // Só resume se o volume não-resumido realmente passou do limite.
            var sizeChars = _conv.Summary.Length
                + _history.Skip(from).Sum(m => (m.Text?.Length ?? 0) + m.Role.Length + 4);
            if (sizeChars < threshold) return;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(_conv.Summary))
                sb.AppendLine("Resumo anterior: " + _conv.Summary.Trim()).AppendLine();
            foreach (var m in _history.Skip(from).Take(foldTo - from))
                sb.AppendLine($"[{m.Role}]: {m.Text}");

            const string sys = "Você resume conversas de planejamento de projeto. Faça um RESUMO COMPACTO em "
                + "português preservando decisões, números (HH, prazos, sprints), nomes de itens e pendências. "
                + "Não invente nada. Responda só o resumo, sem preâmbulo.";
            try
            {
                ChatStatus.Text = AppStrings.Get("AIChat_Compressing");
                var summary = await ProjectAIAssistantService.GenerateFreeTextAsync(
                    settings, sys, sb.ToString(), string.Empty, _cts!.Token);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    _conv.Summary = summary.Trim();
                    _conv.SummarizedFrom = foldTo;   // esse trecho agora vive no resumo
                    SaveHistory();
                }
            }
            catch { /* se o resumo falhar, segue com a janela normal */ }
            finally { ChatStatus.Text = string.Empty; }
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            _history.Clear();
            _conv.Title = AppStrings.Get("AIChat_UntitledConversation");
            _conv.Summary = string.Empty;
            _conv.SummarizedFrom = 0;
            ConvList.Items.Refresh();
            _lastSchedule = null;
            ApplyScheduleButton.Visibility = Visibility.Collapsed;
            _projectContext = _viewModel.BuildFullScheduleContext(); // recarrega o cronograma atual
            AddSystemNote(AppStrings.Get("AIChat_Cleared"));
            RenderConversation();
            SaveHistory();   // conversa esvaziada sai do histórico gravado
        }

        // ── Histórico persistido por cronograma (Work Item Project) ───────────
        // Chave: CÓDIGO do TFS quando existe (renomear no TFS não perde o histórico);
        // só o nome quando é ID interno; projeto novo/sem definição -> "NXProject".
        private string ProjectHistoryKey()
        {
            var p = _viewModel.Project;
            if (p.DevOpsRootWorkItemId > 0) return "TFS-" + p.DevOpsRootWorkItemId;
            var name = !string.IsNullOrWhiteSpace(p.DevOpsProjectName) ? p.DevOpsProjectName!.Trim()
                     : (p.Name ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(name) || string.Equals(name, "Novo Projeto", StringComparison.OrdinalIgnoreCase)
                ? "NXProject" : name;
        }

        private int HistoryLimit()
        {
            try { return AISettingsStore.LoadWorkspace(StorageKey).ChatHistoryLimit; }
            catch { return 10; }
        }

        private void LoadHistory()
        {
            _conversations.Clear();
            foreach (var sc in AiChatHistoryStore.Load(StorageKey, ProjectHistoryKey()))
            {
                var c = new Conversation { Title = sc.Title, Summary = sc.Summary, SummarizedFrom = sc.SummarizedFrom };
                foreach (var m in sc.Messages)
                    c.History.Add(new ChatMsg { Role = m.Role, Text = m.Text, Time = m.Time, DurationSec = m.DurationSec });
                _conversations.Add(c);
            }
            if (_conversations.Count > 0)
            {
                _conv = _conversations[0];
                ConvList.SelectedItem = _conv;
                RenderHistory();
            }
            else
            {
                StartNewConversation(welcome: false);
            }
        }

        private void SaveHistory()
        {
            var stored = _conversations
                .Where(c => c.History.Count > 0)
                .Select(c => new AiChatHistoryStore.StoredConversation
                {
                    Title = c.Title,
                    Summary = c.Summary,
                    SummarizedFrom = c.SummarizedFrom,
                    Messages = c.History.Select(h => new AiChatHistoryStore.StoredMessage
                    {
                        Role = h.Role, Text = h.Text, Time = h.Time, DurationSec = h.DurationSec
                    }).ToList()
                });
            AiChatHistoryStore.Save(StorageKey, ProjectHistoryKey(), stored, HistoryLimit());
        }

        // ── Conversas (sidebar estilo navegador) ──────────────────────────────
        private void StartNewConversation(bool welcome = true)
        {
            if (_cts != null) return; // não troca no meio de uma execução
            SaveHistory();            // persiste a conversa atual antes de abrir outra
            // Reaproveita uma conversa vazia no topo em vez de empilhar várias em branco.
            var empty = _conversations.FirstOrDefault(c => c.History.Count == 0);
            _conv = empty ?? new Conversation { Title = AppStrings.Get("AIChat_UntitledConversation") };
            if (empty == null) _conversations.Insert(0, _conv);
            ConvList.SelectedItem = _conv;
            _lastSchedule = null;
            ApplyScheduleButton.Visibility = Visibility.Collapsed;
            AddSystemNote(welcome ? AppStrings.Get("AIChat_Welcome") : string.Empty);
            RenderConversation();
            InputBox.Focus();
        }

        private void OnNewConversationClick(object sender, RoutedEventArgs e) => StartNewConversation();

        private void OnConversationSelected(object sender, SelectionChangedEventArgs e)
        {
            if (ConvList.SelectedItem is not Conversation c || ReferenceEquals(c, _conv)) return;
            if (_cts != null) { ConvList.SelectedItem = _conv; return; } // não troca durante execução
            SaveHistory();   // grava a conversa atual antes de trocar
            _conv = c;
            RenderHistory();
        }

        // Redesenha a conversa a partir do histórico da conversa selecionada.
        private void RenderHistory()
        {
            _lastSchedule = null;
            ApplyScheduleButton.Visibility = Visibility.Collapsed;
            AddSystemNote(_history.Count == 0 ? AppStrings.Get("AIChat_Welcome") : string.Empty);
            RenderConversation();
        }

        // Título da conversa = primeira pergunta do usuário (resumida).
        private void UpdateConversationTitle(string firstQuestion)
        {
            if (!string.Equals(_conv.Title, AppStrings.Get("AIChat_UntitledConversation"), StringComparison.Ordinal))
                return;
            var t = firstQuestion.Trim().Replace('\n', ' ').Replace('\r', ' ');
            if (t.Length > 40) t = t[..40].TrimEnd() + "…";
            _conv.Title = string.IsNullOrWhiteSpace(t) ? AppStrings.Get("AIChat_UntitledConversation") : t;
            ConvList.Items.Refresh();
        }

        // ── UI da conversa (WebView2: HTML/imagem, seleção/cópia, somente leitura) ──
        private async System.Threading.Tasks.Task InitChatWebAsync()
        {
            try { await ChatWebView.EnsureCoreWebView2Async(); }
            catch { /* segue com pendência; render tenta de novo quando pronto */ }
            _chatWebReady = true;
            if (!string.IsNullOrEmpty(_pendingChatHtml))
            {
                ChatWebView.NavigateToString(_pendingChatHtml);
                _pendingChatHtml = string.Empty;
            }
        }

        private void RenderConversation()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RenderConversation); return; }
            var html = BuildConversationHtml();
            if (_chatWebReady && ChatWebView?.CoreWebView2 != null)
                ChatWebView.NavigateToString(html);
            else
                _pendingChatHtml = html;   // guarda até o WebView2 ficar pronto
        }

        private string BuildConversationHtml()
        {
            var sb = new StringBuilder();
            sb.Append(@"<!DOCTYPE html><html><head><meta charset='utf-8'><style>
 body{font-family:'Segoe UI',Arial,sans-serif;font-size:13px;margin:0;padding:10px;background:#FAFBFD;color:#1c2733}
 .row{display:flex;margin:6px 0}.row.me{justify-content:flex-end}
 .b{max-width:80%;padding:8px 11px;border:1px solid #D0D7E0;border-radius:10px;overflow-wrap:anywhere;box-shadow:0 1px 1px rgba(0,0,0,.03)}
 .me .b{background:#DDE9F7}.ai .b{background:#FFFFFF}
 .b.plain{white-space:pre-wrap}.b img{max-width:100%;height:auto}
 .b table{border-collapse:collapse}.b td,.b th{border:1px solid #ccc;padding:3px 6px}
 .b pre{white-space:pre-wrap;margin:0}.think{color:#8a94a2;font-style:italic}
 .meta{font-size:10.5px;color:#8a94a2;margin:1px 2px 0}.row.me .meta{text-align:right}
</style></head><body>");
            foreach (var m in _history)
            {
                var mine = string.Equals(m.Role, "Usuário", StringComparison.OrdinalIgnoreCase);
                var asHtml = !mine && LooksLikeHtml(m.Text);
                sb.Append("<div class='row ").Append(mine ? "me" : "ai").Append("'>");
                sb.Append("<div style='display:flex;flex-direction:column'>");
                sb.Append("<div class='b ").Append(asHtml ? "" : "plain").Append("'>")
                  .Append(asHtml ? m.Text : System.Net.WebUtility.HtmlEncode(m.Text))
                  .Append("</div>");
                var meta = FormatMeta(m);
                if (meta.Length > 0) sb.Append("<div class='meta'>").Append(meta).Append("</div>");
                sb.Append("</div></div>");
            }
            if (_thinking)
                sb.Append("<div class='row ai'><div class='b plain think'>")
                  .Append(System.Net.WebUtility.HtmlEncode(AppStrings.Get("AIChat_Thinking")))
                  .Append("</div></div>");
            sb.Append("<div id='end'></div><script>document.getElementById('end').scrollIntoView();</script></body></html>");
            return sb.ToString();
        }

        // Rodapé da bolha: data/hora no formato do Windows (cultura atual) + tempo de resposta m:ss.
        private static string FormatMeta(ChatMsg m)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (m.Time.HasValue)
                parts.Add(m.Time.Value.ToString("g", System.Globalization.CultureInfo.CurrentCulture));
            if (m.DurationSec is > 0)
                parts.Add("⏱ " + TimeSpan.FromSeconds(m.DurationSec.Value).ToString(@"m\:ss"));
            return System.Net.WebUtility.HtmlEncode(string.Join("  ·  ", parts));
        }

        // Versão texto puro do rodapé (para a cópia da conversa).
        private static string FormatMetaPlain(ChatMsg m)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (m.Time.HasValue)
                parts.Add(m.Time.Value.ToString("g", System.Globalization.CultureInfo.CurrentCulture));
            if (m.DurationSec is > 0)
                parts.Add(TimeSpan.FromSeconds(m.DurationSec.Value).ToString(@"m\:ss"));
            return string.Join("  ·  ", parts);
        }

        // Corta repetição de modelo em loop: cada linha (não vazia) pode aparecer no máximo 2x;
        // da 3ª vez em diante é descartada (é o padrão de loop, que "fica feio" no histórico).
        private static string CollapseRepeats(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var count = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            var cut = false;
            foreach (var line in lines)
            {
                var key = line.Trim();
                if (key.Length == 0) { sb.AppendLine(line); continue; }
                count.TryGetValue(key, out var c);
                count[key] = c + 1;
                if (c + 1 > 2) { cut = true; continue; }   // 3ª ocorrência ou mais: loop -> corta
                sb.AppendLine(line);
            }
            var result = sb.ToString().TrimEnd();
            if (cut) result += "\n\n… (itens repetidos pela IA foram omitidos)";
            return result;
        }

        // Heurística simples: a resposta da IA já veio como HTML? (renderiza; senão texto puro).
        private static bool LooksLikeHtml(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var tag in new[] { "<p", "<div", "<table", "<img", "<ul", "<ol", "<li", "<br", "<span", "<h1", "<h2", "<h3", "<strong", "<em", "<b>", "<a " })
                if (s.Contains(tag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void OnCopyConversationClick(object sender, RoutedEventArgs e)
        {
            if (_history.Count == 0) { AddSystemNote(AppStrings.Get("AIChat_CopyEmpty")); return; }
            var sb = new StringBuilder();
            foreach (var m in _history)
            {
                sb.Append('[').Append(m.Role).Append("]: ").AppendLine(m.Text);
                var meta = FormatMetaPlain(m);
                if (meta.Length > 0) sb.AppendLine(meta);
                sb.AppendLine();
            }
            if (TrySetClipboard(sb.ToString().TrimEnd()))
                AddSystemNote(AppStrings.Get("AIChat_CopiedConversation", _history.Count));
        }

        // Clipboard pode falhar transitoriamente (aberto por outro app); tenta e não quebra.
        private static bool TrySetClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try { Clipboard.SetText(text); return true; }
            catch { return false; }
        }

        // Notas do sistema (boas-vindas, limpou, copiou, avisos) vão para a linha de status
        // abaixo da conversa — não poluem o transcript renderizado no WebView2.
        private void AddSystemNote(string text)
        {
            if (Dispatcher.CheckAccess()) ChatStatus.Text = text ?? string.Empty;
            else Dispatcher.Invoke(() => ChatStatus.Text = text ?? string.Empty);
        }

        private static int CountLeaves(System.Collections.Generic.List<AIScheduleNode> nodes)
        {
            var n = 0;
            foreach (var node in nodes)
                n += node.Children.Count == 0 ? 1 : CountLeaves(node.Children);
            return n;
        }

        private static string BuildOutline(System.Collections.Generic.List<AIScheduleNode> nodes, int level)
        {
            var sb = new StringBuilder();
            foreach (var node in nodes)
            {
                sb.Append(new string(' ', level * 2)).Append("• ").Append(node.Name);
                if (node.Children.Count == 0 && node.EstimatedHours > 0)
                    sb.Append($" ({node.EstimatedHours:0.##}h)");
                sb.Append('\n');
                if (node.Children.Count > 0)
                    sb.Append(BuildOutline(node.Children, level + 1));
            }
            return sb.ToString();
        }

        /// <summary>Aplica a proposta como CRONOGRAMA NOVO: monta num VM próprio, salva em .xml
        /// escolhido pelo usuário e abre numa janela nova — o cronograma aberto fica intacto.</summary>
        private void OnApplyScheduleClick(object sender, RoutedEventArgs e)
        {
            if (_lastSchedule == null || _lastSchedule.Roots.Count == 0) return;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Projeto NXProject (*.xml)|*.xml",
                Title = AppStrings.Get("AIChat_SaveTitle"),
                FileName = "Cronograma-IA.xml",
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var newVm = new MainViewModel(StorageKey);
                // Herda a IDENTIDADE do projeto atual (Work Item Project): nome, DevOps,
                // work item raiz, sprint e calendário — isso não vem da IA, é copiado.
                var src = _viewModel.Project;
                var dst = newVm.Project;
                dst.Name = src.Name;
                dst.Description = src.Description;
                dst.StartDate = src.StartDate;
                dst.SprintDurationDays = src.SprintDurationDays;
                dst.FirstSprintNumber = src.FirstSprintNumber;
                dst.SprintNumberingMode = src.SprintNumberingMode;
                // Sprints reais (nome + janela do DevOps): copia a definição para o cronograma novo.
                dst.Sprints.Clear();
                foreach (var sp in src.Sprints)
                    dst.Sprints.Add(new Sprint
                    {
                        Number = sp.Number, DisplayName = sp.DisplayName, Path = sp.Path,
                        Start = sp.Start, End = sp.End
                    });
                dst.DevOpsProjectName = src.DevOpsProjectName;
                dst.DevOpsRootWorkItemId = src.DevOpsRootWorkItemId;
                dst.DevOpsProjectOwner = src.DevOpsProjectOwner;
                dst.DevOpsOrganizationUrl = src.DevOpsOrganizationUrl;
                dst.DevOpsTeamProject = src.DevOpsTeamProject;
                dst.PepElement = src.PepElement;
                dst.PepProjectName = src.PepProjectName;
                // Calendário: copia só se o cronograma tiver o SEU PRÓPRIO (embutido). Se estiver
                // usando o calendário GERAL/ativo (Calendar == null), não copia — o novo também usa
                // o geral. Clone profundo para não compartilhar o mesmo objeto com o original.
                if (src.Calendar != null) dst.Calendar = ProjectCalendarService.Clone(src.Calendar);

                // Copia a LISTA DE PESSOAS/RECURSOS do projeto de origem para o cronograma novo,
                // ANTES de aplicar: assim os responsáveis casam com o recurso existente (mesmo Id,
                // disponibilidade, custo, e-mail...) e a Story/Task mostra o recurso igual ao original,
                // em vez de criar um recurso "cru" só com o nome.
                dst.Resources.Clear();
                foreach (var r in src.Resources)
                    dst.Resources.Add(new Resource
                    {
                        Id = r.Id, Name = r.Name, Type = r.Type, Kind = r.Kind,
                        MaxUnitsPerDay = r.MaxUnitsPerDay, CostPerHour = r.CostPerHour, CostType = r.CostType,
                        MonthlyRate = r.MonthlyRate, Email = r.Email, Notes = r.Notes, Team = r.Team,
                        IsImportedFromTfs = r.IsImportedFromTfs, AvailabilityPercent = r.AvailabilityPercent
                    });

                // Mapa CHAVE(DisplayId) -> item de ORIGEM, para o apply corrigir tipo/sprint e
                // preservar os detalhes lidos do TFS (bloqueio, estado, descrição/resumo).
                var sourceByKey = new Dictionary<string, ProjectTask>(StringComparer.OrdinalIgnoreCase);
                void MapSource(IEnumerable<ProjectTask> tasks)
                {
                    foreach (var t in tasks)
                    {
                        sourceByKey[t.DisplayId] = t;
                        if (t.Children.Count > 0) MapSource(t.Children);
                    }
                }
                MapSource(src.Tasks);

                var created = newVm.ApplyAiSchedule(_lastSchedule.Roots, untilTask: true, markPendingTfs: false, sourceByKey);

                // Alerta: Stories que existiam no cronograma e NÃO voltaram na proposta da IA
                // (some algo importante) — lista os nomes para o usuário conferir se perdeu.
                var proposedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void CollectIds(System.Collections.Generic.List<AIScheduleNode> nodes)
                {
                    foreach (var n in nodes)
                    {
                        if (!string.IsNullOrWhiteSpace(n.Id)) proposedIds.Add(n.Id.Trim());
                        if (n.Children.Count > 0) CollectIds(n.Children);
                    }
                }
                CollectIds(_lastSchedule.Roots);
                var missingStories = sourceByKey.Values
                    .Where(t => string.Equals(t.TfsType, "Story", StringComparison.OrdinalIgnoreCase))
                    .Where(t => !proposedIds.Contains(t.DisplayId))
                    .Select(t => t.Name)
                    .ToList();
                if (created == 0)
                {
                    MessageBox.Show(this, AppStrings.Get("AIChat_ScheduleEmpty"),
                        AppStrings.Get("AIChat_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                XmlProjectService.Save(newVm.Project, dlg.FileName);

                // Avisa se alguma Story ficou de fora da proposta (ajuda a perceber perdas).
                if (missingStories.Count > 0)
                {
                    var lista = string.Join("\n• ", missingStories.Take(30));
                    var extra = missingStories.Count > 30 ? $"\n… (+{missingStories.Count - 30})" : "";
                    MessageBox.Show(this,
                        AppStrings.Get("AIChat_MissingStories", missingStories.Count) + "\n\n• " + lista + extra,
                        AppStrings.Get("AIChat_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    AddSystemNote(AppStrings.Get("AIChat_MissingStoriesNote", missingStories.Count));
                }

                // Abre o cronograma sugerido numa janela nova (o aberto não é tocado).
                new CommunityMainWindow(dlg.FileName).Show();
                AddSystemNote(AppStrings.Get("AIChat_ScheduleApplied", created, dlg.FileName));
                ApplyScheduleButton.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, AppStrings.Get("AIChat_Title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetBusy(bool busy, AISettings settings)
        {
            SendButton.IsEnabled = !busy;
            StopButton.IsEnabled = busy;   // só dá para parar enquanto está rodando
            ActionCombo.IsEnabled = !busy;
            // Indicador global: acende no cronograma com o nome do provedor.
            Community.Services.AiRunIndicator.Set(busy, busy ? AIProviderDefaults.DescribeActive(settings) : null);
            // Indicador local nesta janela (mesmo badge "IA em execução" do cronograma).
            if (busy)
            {
                AiRunningLabel.Text = AppStrings.Get("Main_AiRunningNamed", AIProviderDefaults.DescribeActive(settings));
                AiRunningBadge.Visibility = Visibility.Visible;
            }
            else
            {
                AiRunningBadge.Visibility = Visibility.Collapsed;
                // Ao terminar, traz a janela de volta ao foco (mesmo comportamento do Task Plan).
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            }
        }

        // ── Relógio de contagem regressiva (ETA) ──────────────────────────────
        // Combina histórico (por provedor+ação+cronograma, escalado pelos bytes enviados)
        // com uma estimativa da própria IA quando ainda não há histórico.
        private async System.Threading.Tasks.Task StartCountdownAsync(AISettings settings, bool scheduleMode)
        {
            _runWatch.Restart();
            var eta = AiRunStatsStore.EstimateSeconds(StorageKey, _runProvider, _runAction, _runSchedule, _runPayloadBytes)
                      ?? await TryEstimateFromAiAsync(settings, scheduleMode);
            _etaRemaining = Math.Max(3, eta ?? 30);
            UpdateCountdownLabel();

            _countdown?.Stop();
            _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdown.Tick += (_, _) => { _etaRemaining--; UpdateCountdownLabel(); };
            _countdown.Start();
        }

        private void UpdateCountdownLabel()
        {
            var clock = _etaRemaining > 0
                ? AppStrings.Get("AIChat_Eta", TimeSpan.FromSeconds(_etaRemaining).ToString(@"m\:ss"))
                : AppStrings.Get("AIChat_EtaOver", _runWatch.Elapsed.ToString(@"m\:ss"));
            AiRunningLabel.Text = $"{_runProvider}  {clock}";
        }

        private void StopCountdown()
        {
            _countdown?.Stop();
            _countdown = null;
        }

        // Palavras vazias (PT/EN) descartadas ao medir o "volume" do payload — sobram
        // aproximadamente os substantivos/verbos, que representam o tamanho real da tarefa.
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a","o","as","os","um","uma","uns","umas","de","do","da","dos","das","em","no","na","nos","nas",
            "por","para","com","sem","e","ou","que","se","ao","aos","à","às","é","ser","the","of","to","in",
            "on","for","and","or","a","an","is","are","be","with","at","by","as","this","that","id","hh"
        };

        private static long CountContentWords(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            long n = 0;
            foreach (var raw in text.Split(new[] { ' ', '\t', '\r', '\n', '|', ',', ';', ':', '(', ')', '[', ']', '/', '-', '.', '%' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var w = raw.Trim();
                if (w.Length < 3) continue;          // muito curto: descarta
                if (StopWords.Contains(w)) continue; // palavra vazia
                if (w.All(char.IsDigit)) continue;   // só número (ids/horas) não é conteúdo
                n++;
            }
            return n;
        }

        // Pergunta à IA um tempo estimado (só quando não há histórico). Best-effort, com
        // timeout curto e contexto vazio para ser barato; devolve null se falhar.
        private async System.Threading.Tasks.Task<int?> TryEstimateFromAiAsync(AISettings settings, bool scheduleMode)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var what = scheduleMode ? "gerar um cronograma novo a partir" : "analisar";
                var prompt = "Você estima tempos de resposta. Responda APENAS com um número inteiro de segundos, sem texto.";
                var q = $"Quanto tempo, em segundos, você levará para {what} de um cronograma com cerca de "
                        + $"{_runPayloadBytes} bytes de contexto? Responda só o número.";
                var raw = await ProjectAIAssistantService.GenerateFreeTextAsync(settings, prompt, q, string.Empty, cts.Token);
                var digits = new string((raw ?? string.Empty).Where(char.IsDigit).Take(4).ToArray());
                if (int.TryParse(digits, out var s) && s > 0) return Math.Clamp(s, 3, 600);
            }
            catch { /* estimativa é opcional */ }
            return null;
        }
    }
}
