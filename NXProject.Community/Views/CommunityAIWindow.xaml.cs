using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Threading;
using NXProject.Models;
using NXProject.Services;
using NXProject.ViewModels;
using Microsoft.Web.WebView2.Wpf;

namespace NXProject.Views
{
    public partial class CommunityAIWindow : Window
    {
        private const string OpenAIApiKeyGuideUrl = "https://platform.openai.com/account/api-keys";
        private const string OpenRouterApiKeyGuideUrl = "https://openrouter.ai/settings/keys";
        private const int DefaultTimeoutSeconds = 120;
        private const string SettingsStorageKey = "NXProject.Community";

        private readonly MainViewModel _viewModel;
        private AIAssistantResponse? _lastResponse;
        private bool _currentSuggestionsApplied;
        private readonly DispatcherTimer _progressTimer;
        private int _elapsedSeconds;
        private AIWorkspaceSettings _workspace = new();
        private bool _resultWebViewReady;
        private string _pendingResultHtml = string.Empty;
        private string _lastResultText = string.Empty;

        public CommunityAIWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            Loaded += CommunityAIWindow_Loaded;
            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _progressTimer.Tick += OnProgressTimerTick;
            Closing += OnWindowClosing;
            LoadSettings();
            ProjectContextTextBox.Text = _viewModel.BuildAiProjectContext();
        }

        // ── Carga / gravacao ─────────────────────────────────────────────

        private void LoadSettings()
        {
            _workspace = AISettingsStore.LoadWorkspace(SettingsStorageKey);

            // OpenRouter free e lento: garante ao menos 240s se ainda estiver no antigo padrao de 120s.
            var openRouterProfile = _workspace.GetOrCreate(AIProvider.OpenRouter);
            if (openRouterProfile.TimeoutSeconds <= 120)
                openRouterProfile.TimeoutSeconds = AIProviderDefaults.GetDefaultTimeoutSeconds(AIProvider.OpenRouter);

            PopulateProviderTab(AIProvider.OpenAI, OpenAiApiKeyBox, OpenAiEndpointBox, OpenAiModelBox, OpenAiTimeoutBox);
            PopulateProviderTab(AIProvider.AzureOpenAI, AzureApiKeyBox, AzureEndpointBox, AzureModelBox, AzureTimeoutBox);
            PopulateProviderTab(AIProvider.OpenRouter, OpenRouterApiKeyBox, OpenRouterEndpointBox, OpenRouterModelBox, OpenRouterTimeoutBox);
            PopulateProviderTab(AIProvider.Claude, ClaudeApiKeyBox, ClaudeEndpointBox, ClaudeModelBox, ClaudeTimeoutBox);
            PopulateCliTab(AIProvider.CodexCli, CodexCommandBox, CodexTimeoutBox, CodexLocationCombo, CodexLocationStatus);
            PopulateCliTab(AIProvider.ClaudeCli, ClaudeCliCommandBox, ClaudeCliTimeoutBox, ClaudeCliLocationCombo, ClaudeCliLocationStatus);
            PopulateMicrosoftTab();

            // Provedor padrao
            SelectDefaultProvider(_workspace.DefaultProvider);
            PromptTextBox.Text = _workspace.LastPrompt ?? string.Empty;

            // Profundidade do cronograma DevOps (Story vs Task)
            CreateTasksCheck.IsChecked = _workspace.CreateTasks;
            AnalysisTaskLimitBox.Text = (_workspace.AnalysisTaskLimit <= 0 ? 30 : _workspace.AnalysisTaskLimit).ToString();
            ChatHistoryLimitBox.Text = (_workspace.ChatHistoryLimit < 0 ? 10 : _workspace.ChatHistoryLimit).ToString();
            ChatHistoryWindowBox.Text = _workspace.ChatHistoryWindow.ToString();
            ChatCompressBox.Text = _workspace.ChatCompressThreshold.ToString();

            // Tipos de acao com IA
            LoadActionTypes();
        }

        private void PopulateProviderTab(AIProvider provider, PasswordBox apiKey, TextBox endpoint, TextBox model, TextBox timeout)
        {
            var profile = _workspace.GetOrCreate(provider);
            apiKey.Password = profile.ApiKey;
            endpoint.Text = string.IsNullOrWhiteSpace(profile.Endpoint)
                ? AIProviderDefaults.GetDefaultEndpoint(provider)
                : profile.Endpoint;
            model.Text = string.IsNullOrWhiteSpace(profile.Model)
                ? AIProviderDefaults.GetDefaultModel(provider)
                : profile.Model;
            timeout.Text = (profile.TimeoutSeconds <= 0 ? DefaultTimeoutSeconds : profile.TimeoutSeconds).ToString();
        }

        private void SelectDefaultProvider(AIProvider provider)
        {
            foreach (var item in DefaultProviderCombo.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is string tag && Enum.TryParse<AIProvider>(tag, out var p) && p == provider)
                {
                    DefaultProviderCombo.SelectedItem = item;
                    return;
                }
            }
            DefaultProviderCombo.SelectedIndex = 0;
        }

        private AIProvider GetSelectedDefaultProvider()
        {
            if (DefaultProviderCombo.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag && Enum.TryParse<AIProvider>(tag, out var p))
                return p;
            return AIProvider.OpenRouter;
        }

        /// <summary>Le a UI para o workspace em memoria.</summary>
        private void CollectWorkspace()
        {
            CollectProviderTab(AIProvider.OpenAI, OpenAiApiKeyBox, OpenAiEndpointBox, OpenAiModelBox, OpenAiTimeoutBox, AIAuthMode.ApiKey);
            CollectProviderTab(AIProvider.AzureOpenAI, AzureApiKeyBox, AzureEndpointBox, AzureModelBox, AzureTimeoutBox, AIAuthMode.ApiKey);
            CollectProviderTab(AIProvider.OpenRouter, OpenRouterApiKeyBox, OpenRouterEndpointBox, OpenRouterModelBox, OpenRouterTimeoutBox, AIAuthMode.ApiKey);
            CollectProviderTab(AIProvider.Claude, ClaudeApiKeyBox, ClaudeEndpointBox, ClaudeModelBox, ClaudeTimeoutBox, AIAuthMode.ApiKey);
            CollectCliTab(AIProvider.CodexCli, CodexCommandBox, CodexTimeoutBox);
            CollectCliTab(AIProvider.ClaudeCli, ClaudeCliCommandBox, ClaudeCliTimeoutBox);
            CollectMicrosoftTab();

            _workspace.DefaultProvider = GetSelectedDefaultProvider();
            _workspace.CreateTasks = CreateTasksCheck.IsChecked == true;
            _workspace.AnalysisTaskLimit =
                int.TryParse(AnalysisTaskLimitBox.Text?.Trim(), out var lim) && lim > 0 ? lim : 30;
            // Histórico de conversas por cronograma: 0 = infinito (não limpa); negativo -> padrão 10.
            _workspace.ChatHistoryLimit =
                int.TryParse(ChatHistoryLimitBox.Text?.Trim(), out var ch) && ch >= 0 ? ch : 10;
            // Janela de continuidade (mensagens reenviadas): clampada 2..20 no store.
            _workspace.ChatHistoryWindow =
                int.TryParse(ChatHistoryWindowBox.Text?.Trim(), out var cw) && cw > 0 ? cw : 8;
            // Limite do "compress" (caracteres): 0 = desligado; clampado 20mil..500mil no store.
            _workspace.ChatCompressThreshold =
                int.TryParse(ChatCompressBox.Text?.Trim(), out var cp) && cp >= 0 ? cp : 350_000;

            if (ActionTypeCombo.SelectedItem is AIActionType selectedAction)
                _workspace.SelectedAction = selectedAction.Name;

            _workspace.LastPrompt = PromptTextBox.Text ?? string.Empty;
        }

        private void CollectProviderTab(AIProvider provider, PasswordBox apiKey, TextBox endpoint, TextBox model, TextBox timeout, AIAuthMode authMode)
        {
            var profile = _workspace.GetOrCreate(provider);
            var sanitized = AISettingsStore.SanitizeSecret(apiKey.Password);
            if (!string.Equals(sanitized, apiKey.Password, StringComparison.Ordinal))
                apiKey.Password = sanitized;

            profile.AuthMode = authMode;
            profile.ApiKey = sanitized;
            profile.Endpoint = endpoint.Text?.Trim() ?? string.Empty;
            profile.Model = model.Text?.Trim() ?? string.Empty;
            profile.TimeoutSeconds = ParseTimeoutSeconds(timeout);
            timeout.Text = profile.TimeoutSeconds.ToString();
        }

        /// <summary>Coleta a UI, persiste e devolve a configuracao efetiva do provedor padrao.</summary>
        private AISettings BuildActiveSettings()
        {
            CollectWorkspace();
            AISettingsStore.SaveWorkspace(_workspace, SettingsStorageKey);
            return _workspace.ResolveActiveSettings();
        }

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                CollectWorkspace();
                AISettingsStore.SaveWorkspace(_workspace, SettingsStorageKey);
            }
            catch
            {
                // Fechar a janela nao deve ser bloqueado por falha de persistencia local.
            }
        }

        // ── Geracao ──────────────────────────────────────────────────────

        private async void OnGenerateClick(object sender, RoutedEventArgs e)
        {
            var prompt = PromptTextBox.Text.Trim();
            var validation = AIPromptSafetyGuard.Validate(prompt);
            if (!validation.IsValid)
            {
                MessageBox.Show(validation.Error, AppStrings.Get("AI_InvalidRequestTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (validation.RequiresAcknowledgement)
            {
                var acknowledgement = MessageBox.Show(
                    validation.Warning + Environment.NewLine + Environment.NewLine +
                    AppStrings.Get("AI_AckBody"),
                    AppStrings.Get("AI_AckTitle"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                if (acknowledgement != MessageBoxResult.OK)
                {
                    StatusTextBlock.Text = AppStrings.Get("AI_SendCanceled");
                    return;
                }

                AIAuditLogService.RegisterUserAcknowledgement(SettingsStorageKey, "lgpd", prompt);
            }

            var settings = BuildActiveSettings();
            var action = _workspace.GetSelectedAction();
            var context = action.Name == AIActionType.AnalysisActionName
                ? _viewModel.BuildAiScheduleAnalysisContext(_workspace.AnalysisTaskLimit)
                : _viewModel.BuildAiProjectContext();

            _lastSchedule = null;

            try
            {
                // O prompt do tipo (guard-rail) e sempre juntado ao texto da pergunta.
                var systemPrompt = string.IsNullOrWhiteSpace(action.Prompt)
                    ? ProjectAIAssistantService.TaskDeveloperPrompt
                    : action.Prompt;

                string rawContent;
                {
                    var providerName = AIProviderDefaults.GetDisplayName(settings.Provider);
                    SetBusy(true, string.IsNullOrWhiteSpace(validation.Warning)
                        ? AppStrings.Get("AI_ExecutingFmt", action.Name, providerName)
                        : validation.Warning, settings.TimeoutSeconds);

                    if (action.Name == AIActionType.AnalysisActionName)
                    {
                        rawContent = await ProjectAIAssistantService.GenerateFreeTextAsync(
                            settings, systemPrompt, prompt, context);
                        _lastResponse = null;
                        _currentSuggestionsApplied = false;
                        _lastResultText = rawContent;
                        LoadResultHtml(rawContent);
                        ResultTabControl.SelectedIndex = 0;
                        ApplyButton.IsEnabled = false;
                        CopyResultButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastResultText);
                        StatusTextBlock.Text = AppStrings.Get("AI_AnalysisReceived");
                        return;
                    }

                    if (action.Name == AIActionType.ScheduleDevOpsActionName ||
                        action.Name == AIActionType.ScheduleNoDevOpsActionName)
                    {
                        // Cronograma hierarquico linear (Assunto Geral > Grupo de Task > Macro Task [> Task]).
                        var isDevops = action.Name == AIActionType.ScheduleDevOpsActionName;
                        var untilTask = isDevops && CreateTasksCheck.IsChecked == true;
                        var schedPrompt = ProjectAIAssistantService.BuildScheduleDeveloperPrompt(untilTask, includeSprint: isDevops);
                        var raw = await ProjectAIAssistantService.GenerateFreeTextAsync(
                            settings, schedPrompt, prompt, context);
                        ShowScheduleResponse(ProjectAIAssistantService.ParseScheduleResponse(raw), untilTask, isDevops);
                        return;
                    }

                    if (action.CreatesTasks)
                    {
                        var resp = await ProjectAIAssistantService.GenerateTaskSuggestionsAsync(
                            settings, prompt, context, systemPrompt);
                        rawContent = string.Empty; // ja veio parseado
                        ShowTaskResponse(resp);
                        return;
                    }

                    rawContent = await ProjectAIAssistantService.GenerateFreeTextAsync(
                        settings, systemPrompt, prompt, context);
                }

                if (action.CreatesTasks)
                {
                    // Modo cronograma: interpreta o retorno como tarefas.
                    ShowTaskResponse(ProjectAIAssistantService.ParseAssistantResponse(rawContent));
                }
                else
                {
                    // Modo livre: apenas exibe o texto, sem criar tarefas.
                    _lastResponse = null;
                    _currentSuggestionsApplied = false;
                    _lastResultText = rawContent;
                    LoadResultHtml(rawContent);
                    ResultTabControl.SelectedIndex = 0;
                    ApplyButton.IsEnabled = false;
                    CopyResultButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastResultText);
                    StatusTextBlock.Text = AppStrings.Get("AI_FreeReceived");
                }
            }
            catch (Exception ex)
            {
                _lastResponse = null;
                _currentSuggestionsApplied = false;
                _lastResultText = string.Empty;
                CopyResultButton.IsEnabled = false;
                ApplyButton.IsEnabled = false;
                MessageBox.Show(ex.Message, AppStrings.Get("AI_IntegrationErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                StatusTextBlock.Text = AppStrings.Get("AI_QueryFailed");
            }
            finally
            {
                StopProgress();
                SetBusy(false, StatusTextBlock.Text);
            }
        }

        private void ShowTaskResponse(AIAssistantResponse response)
        {
            _lastResponse = response;
            _currentSuggestionsApplied = false;
            _lastResultText = BuildSummary(response) +
                (response.Tasks.Count > 0 && !response.Refused
                    ? Environment.NewLine + Environment.NewLine + BuildTaskConfirmationMessage(response.Tasks)
                    : string.Empty);
            LoadResultHtml("<pre>" + System.Net.WebUtility.HtmlEncode(_lastResultText) + "</pre>");
            ResultTabControl.SelectedIndex = 0;
            ApplyButton.IsEnabled = response.Tasks.Count > 0 && !response.Refused;
            CopyResultButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastResultText);
            StatusTextBlock.Text = response.Refused
                ? AppStrings.Get("AI_RequestRefused")
                : AppStrings.Get("AI_TasksSuggestedFmt", response.Tasks.Count);
        }

        // ── Tipos de acao com IA ─────────────────────────────────────────

        private bool _loadingAction;
        private AIActionType? _currentAction;

        private void LoadActionTypes()
        {
            ActionTypeCombo.ItemsSource = _workspace.ActionTypes;
            ActionTypeCombo.SelectedItem = _workspace.GetSelectedAction();
        }

        private void OnActionTypeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _currentAction = ActionTypeCombo.SelectedItem as AIActionType;
            if (_currentAction == null) return;

            _loadingAction = true;
            ActionNameBox.Text = _currentAction.Name;
            ActionPromptBox.Text = _currentAction.Prompt;
            ActionCreatesTasksCheck.IsChecked = _currentAction.CreatesTasks;
            // Todas as acoes sao editaveis (nome, prompt, tipo) e excluiveis.
            _loadingAction = false;
        }

        private void OnActionFieldChanged(object sender, RoutedEventArgs e)
        {
            if (_loadingAction || _currentAction == null) return;
            _currentAction.Name = ActionNameBox.Text?.Trim() ?? string.Empty;
            _currentAction.Prompt = ActionPromptBox.Text ?? string.Empty;
            _currentAction.CreatesTasks = ActionCreatesTasksCheck.IsChecked == true;
            ActionTypeCombo.Items.Refresh();
        }

        private void OnNewActionClick(object sender, RoutedEventArgs e)
        {
            var novo = new AIActionType
            {
                Name = "Novo tipo",
                Prompt = "Você é um assistente de projetos. Responda de forma objetiva ao pedido.",
                CreatesTasks = false
            };
            _workspace.ActionTypes.Add(novo);
            ActionTypeCombo.Items.Refresh();
            ActionTypeCombo.SelectedItem = novo;
            ActionNameBox.Focus();
            ActionNameBox.SelectAll();
        }

        private void OnDeleteActionClick(object sender, RoutedEventArgs e)
        {
            if (_currentAction == null) return;

            var confirm = MessageBox.Show(
                AppStrings.Get("AI_DeleteActionConfirmFmt", _currentAction.Name),
                AppStrings.Get("AI_DeleteActionTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _workspace.ActionTypes.Remove(_currentAction);
            ActionTypeCombo.Items.Refresh();
            ActionTypeCombo.SelectedItem = _workspace.ActionTypes.FirstOrDefault();
        }

        private void OnRestoreDefaultActionsClick(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                AppStrings.Get("AI_RestoreConfirm"),
                AppStrings.Get("AI_RestoreConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            foreach (var def in AISettingsStore.GetDefaultActions())
            {
                var existing = _workspace.ActionTypes.FirstOrDefault(a => a.Name == def.Name);
                if (existing != null)
                {
                    existing.Prompt = def.Prompt;
                    existing.CreatesTasks = def.CreatesTasks;
                }
                else
                {
                    _workspace.ActionTypes.Add(def);
                }
            }

            ActionTypeCombo.Items.Refresh();
            if (_currentAction != null)
                OnActionTypeSelectionChanged(ActionTypeCombo, null!);
            StatusTextBlock.Text = AppStrings.Get("AI_RestoreDone");
        }

        // Restaura o PADRÃO completo (ao lado da combo): provedor -> Codex (local), e prompts/ações.
        // Para cada ação que foi ALTERADA, pergunta antes de reverter; se o usuário recusar, pula
        // para a próxima e restaura só as que ele concordar. Ações padrão ausentes são readicionadas.
        private void OnRestoreAllDefaultsClick(object sender, RoutedEventArgs e)
        {
            // 1) Provedor padrão: Codex (local).
            SelectDefaultProvider(AIProvider.CodexCli);
            _workspace.DefaultProvider = AIProvider.CodexCli;

            // 2) Prompts/ações, com confirmação individual do que mudou.
            int restored = 0, skipped = 0, added = 0;
            foreach (var def in AISettingsStore.GetDefaultActions())
            {
                var existing = _workspace.ActionTypes.FirstOrDefault(a => a.Name == def.Name);
                if (existing == null)
                {
                    _workspace.ActionTypes.Add(def);   // ação padrão que não existe mais: readiciona
                    added++;
                    continue;
                }

                var changed = !string.Equals(existing.Prompt, def.Prompt, StringComparison.Ordinal)
                              || existing.CreatesTasks != def.CreatesTasks;
                if (!changed) continue;   // igual ao padrão: nada a fazer

                var r = MessageBox.Show(
                    AppStrings.Get("AI_RestoreActionAsk", existing.Name),
                    AppStrings.Get("AI_RestoreConfirmTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) { skipped++; continue; }   // recusou: próxima

                existing.Prompt = def.Prompt;
                existing.CreatesTasks = def.CreatesTasks;
                restored++;
            }

            ActionTypeCombo.Items.Refresh();
            if (_currentAction != null)
                OnActionTypeSelectionChanged(ActionTypeCombo, null!);
            AISettingsStore.SaveWorkspace(_workspace, SettingsStorageKey);
            StatusTextBlock.Text = AppStrings.Get("AI_RestoreAllDone", restored, added, skipped);
        }

        // Cronograma hierarquico pendente de aplicacao.
        private AIScheduleResponse? _lastSchedule;
        private bool _lastScheduleUntilTask;
        private bool _lastScheduleDevops;

        private void ShowScheduleResponse(AIScheduleResponse schedule, bool untilTask, bool isDevops)
        {
            _lastResponse = null;
            _lastSchedule = schedule;
            _lastScheduleUntilTask = untilTask;
            _lastScheduleDevops = isDevops;
            _currentSuggestionsApplied = false;

            var count = CountLeaves(schedule.Roots);
            _lastResultText = (string.IsNullOrWhiteSpace(schedule.Summary) ? "" : schedule.Summary + Environment.NewLine + Environment.NewLine)
                + BuildScheduleOutline(schedule.Roots, 0);
            LoadResultHtml("<pre>" + System.Net.WebUtility.HtmlEncode(_lastResultText) + "</pre>");
            ResultTabControl.SelectedIndex = 0;
            CopyResultButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastResultText);
            ApplyButton.IsEnabled = !schedule.Refused && schedule.Roots.Count > 0;
            StatusTextBlock.Text = schedule.Refused
                ? AppStrings.Get("AI_RequestRefused")
                : AppStrings.Get("AI_ScheduleGeneratedFmt", schedule.Roots.Count, count);
        }

        private static int CountLeaves(System.Collections.Generic.List<AIScheduleNode> nodes)
        {
            var n = 0;
            foreach (var node in nodes)
                n += node.Children.Count == 0 ? 1 : CountLeaves(node.Children);
            return n;
        }

        private static string BuildScheduleOutline(System.Collections.Generic.List<AIScheduleNode> nodes, int level)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var node in nodes)
            {
                sb.Append(new string(' ', level * 2));
                sb.Append("- ");
                if (!string.IsNullOrWhiteSpace(node.Type)) sb.Append('[').Append(node.Type).Append("] ");
                sb.Append(node.Name);
                if (node.EstimatedHours > 0) sb.Append(" (").Append(node.EstimatedHours.ToString("0.#")).Append("h)");
                if (node.Sprint > 0) sb.Append(" [Sprint ").Append(node.Sprint.ToString("00")).Append(']');
                if (!string.IsNullOrWhiteSpace(node.Assignee)) sb.Append(" — ").Append(node.Assignee);
                sb.Append(Environment.NewLine);
                if (node.Children.Count > 0)
                    sb.Append(BuildScheduleOutline(node.Children, level + 1));
            }
            return sb.ToString();
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            if (_currentSuggestionsApplied)
            {
                StatusTextBlock.Text = AppStrings.Get("AI_AlreadyApplied");
                Close();
                return;
            }

            // Cronograma hierarquico (Fazer Cronograma DevOps)
            if (_lastSchedule != null && _lastSchedule.Roots.Count > 0)
            {
                var createdSched = _viewModel.ApplyAiSchedule(_lastSchedule.Roots, _lastScheduleUntilTask, markPendingTfs: _lastScheduleDevops);
                _currentSuggestionsApplied = createdSched > 0;
                ApplyButton.IsEnabled = false;
                var note = _lastScheduleDevops
                    ? AppStrings.Get("AI_NoteDevops")
                    : AppStrings.Get("AI_NoteLocal");
                StatusTextBlock.Text = createdSched > 0
                    ? AppStrings.Get("AI_SchedAppliedFmt", createdSched, note)
                    : AppStrings.Get("AI_NothingApplied");
                Close();
                return;
            }

            if (_lastResponse == null || _lastResponse.Tasks.Count == 0)
                return;

            var createdCount = _viewModel.ApplyAiTaskSuggestions(_lastResponse.Tasks);
            _currentSuggestionsApplied = createdCount > 0;
            ApplyButton.IsEnabled = false;
            StatusTextBlock.Text = createdCount > 0
                ? AppStrings.Get("AI_TasksAppliedFmt", createdCount)
                : AppStrings.Get("AI_NoValidTasks");
            Close();
        }

        private async void CommunityAIWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= CommunityAIWindow_Loaded;
            await InitializeResultWebViewAsync();
        }

        private async Task InitializeResultWebViewAsync()
        {
            try
            {
                if (ResultWebView != null)
                    await ResultWebView.EnsureCoreWebView2Async();
            }
            catch
            {
                // Ignora falha inicial e continua com o texto.
            }
            _resultWebViewReady = true;
            if (!string.IsNullOrWhiteSpace(_pendingResultHtml) && ResultWebView != null)
            {
                ResultWebView.NavigateToString(_pendingResultHtml);
                _pendingResultHtml = string.Empty;
            }
        }

        private void LoadResultHtml(string rawContent)
        {
            var html = BuildResultHtml(rawContent);
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => LoadResultHtml(rawContent));
                return;
            }

            if (_resultWebViewReady && ResultWebView != null)
            {
                ResultWebView.NavigateToString(html);
            }
            else
            {
                _pendingResultHtml = html;
            }
        }

        private static string BuildResultHtml(string rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
                rawContent = "<p>Sem retorno.</p>";

            if (rawContent.Contains("<html", StringComparison.OrdinalIgnoreCase)
                || rawContent.Contains("<body", StringComparison.OrdinalIgnoreCase)
                || rawContent.Contains("<table", StringComparison.OrdinalIgnoreCase)
                || rawContent.Contains("<div", StringComparison.OrdinalIgnoreCase)
                || rawContent.Contains("<span", StringComparison.OrdinalIgnoreCase)
                || rawContent.Contains("<p", StringComparison.OrdinalIgnoreCase))
            {
                return WrapHtml(rawContent);
            }

            var rows = rawContent
                .Replace("\r\n", "\n")
                .Split(new[] { '\n' }, StringSplitOptions.None)
                .Select(line => line.TrimEnd())
                .ToList();

            if (TryBuildHtmlTable(rows, out var tableHtml))
            {
                return BuildHtmlDocument(tableHtml);
            }

            var escaped = System.Net.WebUtility.HtmlEncode(rawContent)
                .Replace("\r\n", "\n")
                .Replace("\n", "<br />");

            return BuildHtmlDocument($"<pre>{escaped}</pre>");
        }

        private static string WrapHtml(string html)
        {
            // Sempre normaliza para o nosso documento (fundo branco + texto escuro),
            // mesmo quando a IA devolve um HTML completo com estilo proprio (as vezes escuro).
            if (html.Contains("<html", StringComparison.OrdinalIgnoreCase)
                || html.Contains("<body", StringComparison.OrdinalIgnoreCase))
                return BuildHtmlDocument(ExtractBodyInner(html));
            return BuildHtmlDocument(html);
        }

        /// <summary>Extrai o conteudo interno de &lt;body&gt; (ou o proprio HTML sem html/head).</summary>
        private static string ExtractBodyInner(string html)
        {
            var bodyStart = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (bodyStart >= 0)
            {
                var gt = html.IndexOf('>', bodyStart);
                var bodyEnd = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (gt >= 0 && bodyEnd > gt)
                    return html.Substring(gt + 1, bodyEnd - gt - 1);
            }
            // Sem <body>: remove <html>/<head>...</head> se houver.
            var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (headEnd >= 0) html = html[(headEnd + "</head>".Length)..];
            return html.Replace("<html>", string.Empty, StringComparison.OrdinalIgnoreCase)
                       .Replace("</html>", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildHtmlDocument(string bodyHtml)
        {
            return string.Concat(
                "<!DOCTYPE html>\n",
                "<html>\n",
                "<head>\n",
                "<meta charset=\"utf-8\" />\n",
                "<style>\n",
                "    html, body { background: #ffffff !important; color: #111 !important; }\n",
                "    body { font-family: Segoe UI, Arial, sans-serif; margin: 16px; font-size: 11px; }\n",
                "    h1, h2, h3, h4 { color: #2b579a; }\n",
                "    table { border-collapse: collapse; width: 100%; margin-top: 16px; font-size: 11px; }\n",
                "    td, th { border: 1px solid #ddd; padding: 8px; }\n",
                "    th { background: #f4f6f8; text-align: left; }\n",
                "    ul, ol { margin: 12px 0 12px 20px; }\n",
                "    p { margin: 8px 0; }\n",
                "    pre { white-space: pre-wrap; word-wrap: break-word; font-size: 11px; line-height: 1.4; }\n",
                "</style>\n",
                "</head>\n",
                "<body>\n",
                bodyHtml,
                "\n</body>\n",
                "</html>");
        }

        private static bool TryBuildHtmlTable(IReadOnlyList<string> rows, out string tableHtml)
        {
            tableHtml = string.Empty;
            if (rows.Count < 2)
                return false;

            var pipeRows = rows.Where(r => r.Contains("|")).ToList();
            if (pipeRows.Count >= 2)
            {
                var header = pipeRows[0].Trim('|');
                var columns = header.Split('|').Select(c => c.Trim()).ToList();
                if (columns.Count < 2)
                    return false;

                var htmlRows = new List<string>
                {
                    "<table>\n<tr>" + string.Join(string.Empty, columns.Select(c => $"<th>{System.Net.WebUtility.HtmlEncode(c)}</th>")) + "</tr>"
                };

                foreach (var row in pipeRows.Skip(1))
                {
                    var cells = row.Trim('|').Split('|').Select(c => c.Trim()).ToList();
                    if (cells.Count != columns.Count)
                        continue;

                    htmlRows.Add("<tr>" + string.Join(string.Empty, cells.Select(c => $"<td>{System.Net.WebUtility.HtmlEncode(c)}</td>")) + "</tr>");
                }

                if (htmlRows.Count > 1)
                {
                    htmlRows.Add("</table>");
                    tableHtml = string.Join("\n", htmlRows);
                    return true;
                }
            }

            return false;
        }

        // Provedor padrão sempre visível no topo: mudou, salvou (sem precisar do botão Salvar).
        private void OnDefaultProviderChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _workspace == null) return;
            _workspace.DefaultProvider = GetSelectedDefaultProvider();
            AISettingsStore.SaveWorkspace(_workspace, SettingsStorageKey);
            StatusTextBlock.Text = AppStrings.Get("AI_ProviderSaved",
                AIProviderDefaults.GetDisplayName(_workspace.DefaultProvider));
        }

        // Botões que substituem as abas: cada botão seleciona a visão correspondente.
        private void OnTabButtonChecked(object sender, RoutedEventArgs e)
        {
            if (MainTabControl == null || sender is not System.Windows.Controls.RadioButton rb) return;
            if (int.TryParse(rb.Tag?.ToString(), out var index)
                && index >= 0 && index < MainTabControl.Items.Count)
                MainTabControl.SelectedIndex = index;
        }

        private void OnCopyResultClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastResultText))
                return;

            Clipboard.SetText(_lastResultText);
            StatusTextBlock.Text = AppStrings.Get("AI_ResultCopied");
        }

        private void OnSaveConfigClick(object sender, RoutedEventArgs e)
        {
            try
            {
                CollectWorkspace();
                AISettingsStore.SaveWorkspace(_workspace, SettingsStorageKey);
                StatusTextBlock.Text = AppStrings.Get("AI_ConfigSaved");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, AppStrings.Get("AI_SaveConfigErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();


        // ── Codex instalado na maquina ───────────────────────────────────
        // Nao tem chave nem modelo: so o COMANDO do CLI (guardado no campo
        // Endpoint do perfil) e o timeout.
        // Provedores de CLI local (Codex, Claude Code): nao tem chave nem modelo,
        // so o COMANDO do CLI (guardado no campo Endpoint do perfil) e o timeout.
        private bool _loadingCliTabs;

        private void PopulateCliTab(AIProvider provider, TextBox command, TextBox timeout,
            ComboBox location, TextBlock status)
        {
            var profile = _workspace.GetOrCreate(provider);
            command.Text = string.IsNullOrWhiteSpace(profile.Endpoint)
                ? CodexCliService.GetDefaultCommand(provider)
                : profile.Endpoint;
            timeout.Text = (profile.TimeoutSeconds <= 0
                ? AIProviderDefaults.GetDefaultTimeoutSeconds(provider)
                : profile.TimeoutSeconds).ToString();

            // Combo reflete o comando salvo (Windows x WSL) sem disparar o handler.
            _loadingCliTabs = true;
            var wantWindows = CodexCliService.IsWindowsCommand(command.Text);
            foreach (var item in location.Items.OfType<ComboBoxItem>())
                if ((item.Tag as string == "win") == wantWindows) { location.SelectedItem = item; break; }
            _loadingCliTabs = false;
            status.Text = string.Empty;
        }

        // Escolha Windows/WSL monta o comando; no Windows, procura o executável no PATH.
        private void OnCodexLocationChanged(object sender, SelectionChangedEventArgs e)
            => ApplyCliLocation(AIProvider.CodexCli, CodexLocationCombo, CodexCommandBox, CodexLocationStatus);

        private void OnClaudeCliLocationChanged(object sender, SelectionChangedEventArgs e)
            => ApplyCliLocation(AIProvider.ClaudeCli, ClaudeCliLocationCombo, ClaudeCliCommandBox, ClaudeCliLocationStatus);

        private void ApplyCliLocation(AIProvider provider, ComboBox location, TextBox command, TextBlock status)
        {
            if (_loadingCliTabs) return;
            var windows = (location.SelectedItem as ComboBoxItem)?.Tag as string == "win";
            command.Text = CodexCliService.BuildCommand(provider, windows);
            if (!windows)
            {
                status.Foreground = System.Windows.Media.Brushes.DimGray;
                status.Text = AppStrings.Get("AI_CliWslNote");
                return;
            }
            var cli = CodexCliService.CliName(provider);
            var path = CodexCliService.FindOnWindowsPath(cli);
            status.Inlines.Clear();
            if (path != null)
            {
                status.Foreground = System.Windows.Media.Brushes.Green;
                status.Inlines.Add(new Run(AppStrings.Get("AI_CliFoundAt", path)));
                return;
            }
            // Nao achou no PATH: mostra aviso + link para a pagina de download do CLI.
            status.Foreground = System.Windows.Media.Brushes.DarkOrange;
            status.Inlines.Add(new Run(AppStrings.Get("AI_CliNotOnPath", cli) + " "));
            var url = provider == AIProvider.ClaudeCli
                ? "https://code.claude.com/docs/en/quickstart"
                : "https://developers.openai.com/codex/cli/";
            var link = new Hyperlink(new Run(AppStrings.Get("AI_CliDownloadLink"))) { NavigateUri = new Uri(url) };
            link.Click += (_, _) => OpenExternal(url);
            status.Inlines.Add(link);
        }

        private void CollectCliTab(AIProvider provider, TextBox command, TextBox timeout)
        {
            var profile = _workspace.GetOrCreate(provider);
            profile.Endpoint = command.Text?.Trim() ?? string.Empty;
            profile.Model = string.Empty;
            profile.ApiKey = string.Empty;
            profile.AuthMode = AIAuthMode.ApiKey;
            profile.TimeoutSeconds = int.TryParse(timeout.Text?.Trim(), out var t) && t > 0
                ? t
                : AIProviderDefaults.GetDefaultTimeoutSeconds(provider);
        }

        /// <summary>Testa o Codex local com uma pergunta minima — valida comando, PATH e login.</summary>
        private async void OnCodexTestClick(object sender, RoutedEventArgs e)
        {
            var command = CodexCommandBox.Text?.Trim();
            if (CodexCliService.LooksLikeServerScript(command))
            {
                CodexTestResult.Foreground = System.Windows.Media.Brushes.Firebrick;
                CodexTestResult.Text = AppStrings.Get("AI_CodexServerScript");
                return;
            }

            CodexTestButton.IsEnabled = false;
            CodexTestResult.Foreground = System.Windows.Media.Brushes.DimGray;
            CodexTestResult.Text = AppStrings.Get("AI_CodexTesting");
            try
            {
                var timeout = int.TryParse(CodexTimeoutBox.Text?.Trim(), out var t) && t > 0 ? t : 120;
                await RunCliTest(CodexCommandBox, CodexTestResult, command, Math.Min(timeout, 120));
            }
            finally
            {
                CodexTestButton.IsEnabled = true;
            }
        }

        /// <summary>Testa um comando de CLI local; se ele nem iniciar, tenta a forma alternativa
        /// (com/sem "wsl.exe -- ") e, se essa funcionar, ajusta o campo para ela.</summary>
        private static async Task RunCliTest(TextBox commandBox, TextBlock result, string? command, int timeoutSeconds)
        {
            try
            {
                var answer = await CodexCliService.GenerateAsync(
                    "Responda SEMPRE com uma unica palavra: OK", "Diga OK.", command, timeoutSeconds);
                result.Foreground = System.Windows.Media.Brushes.Green;
                result.Text = AppStrings.Get("AI_CodexTestOk", answer.Length > 60 ? answer[..60] + "..." : answer);
            }
            catch (NXProject.Services.CliStartException)
            {
                // Comando nao iniciou: tenta a outra forma (nativo <-> WSL).
                var alt = CodexCliService.AlternateCommand(command);
                if (alt == null)
                {
                    result.Foreground = System.Windows.Media.Brushes.Firebrick;
                    result.Text = AppStrings.Get("AI_CliNotFound", command ?? "");
                    return;
                }
                try
                {
                    var answer = await CodexCliService.GenerateAsync(
                        "Responda SEMPRE com uma unica palavra: OK", "Diga OK.", alt, timeoutSeconds);
                    commandBox.Text = alt;   // deixa salvo o comando que realmente funciona
                    result.Foreground = System.Windows.Media.Brushes.Green;
                    result.Text = AppStrings.Get("AI_CliAdjusted", alt,
                        answer.Length > 40 ? answer[..40] + "..." : answer);
                }
                catch (Exception ex2)
                {
                    result.Foreground = System.Windows.Media.Brushes.Firebrick;
                    result.Text = AppStrings.Get("AI_CliBothFailed", command ?? "", alt) + "\n" + ex2.Message;
                }
            }
            catch (Exception ex)
            {
                result.Foreground = System.Windows.Media.Brushes.Firebrick;
                result.Text = ex.Message;
            }
        }

        /// <summary>Guia oficial de instalacao do WSL (a Microsoft mantem o passo a passo atualizado).</summary>
        private void OnWslInstallGuideClick(object sender, RoutedEventArgs e)
            => OpenExternal("https://learn.microsoft.com/windows/wsl/install");

        /// <summary>Abre a conta OpenAI no navegador padrao ANTES do `codex login`: com a sessao
        /// ja aberta, o fluxo do CLI so pede a confirmacao em vez de e-mail + senha + 2FA.</summary>
        private void OnOpenAiAccountClick(object sender, RoutedEventArgs e)
            => OpenExternal("https://auth.openai.com/log-in");

        /// <summary>Documentacao do Codex CLI (instalacao, login e o comando exec).</summary>
        /// <summary>Instalação nativa do Codex no Windows (npm/binário), para quem não quer WSL.</summary>
        private void OnCodexWindowsInstallClick(object sender, RoutedEventArgs e)
            => OpenExternal("https://developers.openai.com/codex/cli/");

        private void OnCodexDocsClick(object sender, RoutedEventArgs e)
            => OpenExternal("https://developers.openai.com/codex/cli/");

        /// <summary>Copia os tres comandos da preparacao para colar no terminal.</summary>
        private void OnCodexCopySetupClick(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(
                "# 1) No PowerShell do Windows COMO ADMINISTRADOR (reinicia se pedir):" + Environment.NewLine
                + CodexWslCommandBox.Text + Environment.NewLine + Environment.NewLine
                + "# 2) Dentro do WSL (Ubuntu) - instalar o Node.js 22 (o npm vem junto):" + Environment.NewLine
                + CodexNodeCommandBox.Text + Environment.NewLine + Environment.NewLine
                + "# 3) Ainda no WSL - instalar o Codex CLI:" + Environment.NewLine
                + CodexInstallCommandBox.Text + Environment.NewLine + Environment.NewLine
                + "# 4) Ainda no WSL, autentique o Codex:" + Environment.NewLine
                + CodexLoginCommandBox.Text + Environment.NewLine);
            CodexTestResult.Foreground = System.Windows.Media.Brushes.DimGray;
            CodexTestResult.Text = AppStrings.Get("AI_CodexSetupCopied");
        }

        private static void OpenExternal(string url)
            => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

        /// <summary>Testa o Claude Code local com uma pergunta minima.</summary>
        private async void OnClaudeCliTestClick(object sender, RoutedEventArgs e)
        {
            ClaudeCliTestButton.IsEnabled = false;
            ClaudeCliTestResult.Foreground = System.Windows.Media.Brushes.DimGray;
            ClaudeCliTestResult.Text = AppStrings.Get("AI_ClaudeCliTesting");
            try
            {
                var timeout = int.TryParse(ClaudeCliTimeoutBox.Text?.Trim(), out var t) && t > 0 ? t : 120;
                await RunCliTest(ClaudeCliCommandBox, ClaudeCliTestResult, ClaudeCliCommandBox.Text?.Trim(), Math.Min(timeout, 120));
            }
            finally
            {
                ClaudeCliTestButton.IsEnabled = true;
            }
        }

        /// <summary>Conta Anthropic no navegador: com a sessao aberta, o login do CLI so pede confirmacao.</summary>
        private void OnClaudeAccountClick(object sender, RoutedEventArgs e)
            => OpenExternal("https://claude.ai/login");

        private void OnClaudeCodeDocsClick(object sender, RoutedEventArgs e)
            => OpenExternal("https://code.claude.com/docs/");

        private void OnClaudeCliCopySetupClick(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(
                "# 1) Instalar o Claude Code (precisa do Node.js 18+):" + Environment.NewLine
                + ClaudeCliInstallCommandBox.Text + Environment.NewLine + Environment.NewLine
                + "# 2) Autenticar (abre o navegador; saia com /exit depois de logar):" + Environment.NewLine
                + ClaudeCliLoginCommandBox.Text + Environment.NewLine);
            ClaudeCliTestResult.Foreground = System.Windows.Media.Brushes.DimGray;
            ClaudeCliTestResult.Text = AppStrings.Get("AI_CodexSetupCopied");
        }

        // ── Microsoft 365 / Entra ID ─────────────────────────────────────
        // Login pelo NAVEGADOR (codigo de dispositivo): a senha e digitada so na
        // pagina da Microsoft. O NX guarda o token cifrado (DPAPI) e o renova pelo
        // refresh token. Tenant/Client ID vem do registro de aplicativo da empresa.
        // O perfil guarda Tenant no Model e Client ID + escopo no LoginUrl
        // ("clientId|scope"), reaproveitando os campos existentes do provedor.
        private void PopulateMicrosoftTab()
        {
            var profile = _workspace.GetOrCreate(AIProvider.Microsoft365);
            MsTenantBox.Text = profile.Model;
            var parts = (profile.LoginUrl ?? string.Empty).Split('|');
            MsClientIdBox.Text = parts.Length > 0 ? parts[0] : string.Empty;
            MsScopeBox.Text = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                ? parts[1]
                : "openid profile offline_access";
            MsEndpointBox.Text = profile.Endpoint;
            MsTimeoutBox.Text = (profile.TimeoutSeconds <= 0 ? DefaultTimeoutSeconds : profile.TimeoutSeconds).ToString();
            ShowMicrosoftSession();
        }

        private void CollectMicrosoftTab()
        {
            var profile = _workspace.GetOrCreate(AIProvider.Microsoft365);
            profile.Model = MsTenantBox.Text?.Trim() ?? string.Empty;
            profile.LoginUrl = (MsClientIdBox.Text?.Trim() ?? string.Empty) + "|" + (MsScopeBox.Text?.Trim() ?? string.Empty);
            profile.Endpoint = MsEndpointBox.Text?.Trim() ?? string.Empty;
            profile.AuthMode = AIAuthMode.BrowserLogin;
            profile.ApiKey = string.Empty;
            profile.TimeoutSeconds = int.TryParse(MsTimeoutBox.Text?.Trim(), out var t) && t > 0 ? t : DefaultTimeoutSeconds;
        }

        private void ShowMicrosoftSession()
        {
            var session = EntraAuthService.GetCurrentSession();
            if (session == null)
            {
                MsStatusText.Foreground = System.Windows.Media.Brushes.DimGray;
                MsStatusText.Text = AppStrings.Get("AI_MsNotConnected");
                return;
            }
            var (account, expires) = session.Value;
            var local = expires.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            MsStatusText.Foreground = expires > DateTime.UtcNow
                ? System.Windows.Media.Brushes.Green
                : System.Windows.Media.Brushes.DarkOrange;
            MsStatusText.Text = AppStrings.Get("AI_MsConnected", account, local);
        }

        private async void OnMsLoginClick(object sender, RoutedEventArgs e)
        {
            MsLoginButton.IsEnabled = false;
            try
            {
                var tenant = MsTenantBox.Text?.Trim() ?? string.Empty;
                var clientId = MsClientIdBox.Text?.Trim() ?? string.Empty;
                var scope = MsScopeBox.Text?.Trim() ?? string.Empty;

                var prompt = await EntraAuthService.StartLoginAsync(tenant, clientId, scope);
                MsStatusText.Foreground = System.Windows.Media.Brushes.DimGray;
                MsStatusText.Text = AppStrings.Get("AI_MsLoginPending", prompt.UserCode, prompt.VerificationUri);
                Clipboard.SetText(prompt.UserCode);   // o codigo ja vai colado para a pagina

                var token = await EntraAuthService.CompleteLoginAsync(prompt, tenant, clientId, scope);
                MsStatusText.Foreground = System.Windows.Media.Brushes.Green;
                MsStatusText.Text = AppStrings.Get("AI_MsConnected",
                    token.Account, token.ExpiresAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
            }
            catch (Exception ex)
            {
                MsStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
                MsStatusText.Text = ex.Message;
            }
            finally
            {
                MsLoginButton.IsEnabled = true;
            }
        }

        /// <summary>Testa a conexao: renova/valida o token e, com endpoint informado, chama o servico.</summary>
        private async void OnMsTestClick(object sender, RoutedEventArgs e)
        {
            MsTestButton.IsEnabled = false;
            MsStatusText.Foreground = System.Windows.Media.Brushes.DimGray;
            MsStatusText.Text = AppStrings.Get("AI_MsTesting");
            try
            {
                var token = await EntraAuthService.GetValidTokenAsync();
                var endpoint = MsEndpointBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    MsStatusText.Foreground = System.Windows.Media.Brushes.Green;
                    MsStatusText.Text = AppStrings.Get("AI_MsTokenOk",
                        token.Account, token.ExpiresAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
                    return;
                }

                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
                using var response = await http.GetAsync(endpoint);
                var body = await response.Content.ReadAsStringAsync();

                MsStatusText.Foreground = response.IsSuccessStatusCode
                    ? System.Windows.Media.Brushes.Green
                    : System.Windows.Media.Brushes.Firebrick;
                MsStatusText.Text = AppStrings.Get("AI_MsTestResult",
                    (int)response.StatusCode, response.ReasonPhrase ?? "",
                    body.Length > 200 ? body[..200] + "..." : body);
            }
            catch (Exception ex)
            {
                MsStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
                MsStatusText.Text = ex.Message;
            }
            finally
            {
                MsTestButton.IsEnabled = true;
            }
        }

        private void OnMsSignOutClick(object sender, RoutedEventArgs e)
        {
            EntraAuthService.SignOut();
            ShowMicrosoftSession();
        }

        private void OnClaudeApiKeyGuideClick(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://platform.claude.com/settings/keys",
                UseShellExecute = true
            });
        }

        private void OnOpenRouterApiKeyGuideClick(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = OpenRouterApiKeyGuideUrl,
                UseShellExecute = true
            });
        }

        // ── OpenAI: modo de autenticacao ─────────────────────────────────

        private void OnOpenAiApiKeyGuideClick(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = OpenAIApiKeyGuideUrl,
                UseShellExecute = true
            });
        }

        // ── Progresso / estado ───────────────────────────────────────────

        private void SetBusy(bool isBusy, string status, int timeoutSeconds = DefaultTimeoutSeconds)
        {
            GenerateButton.IsEnabled = !isBusy;
            PromptTextBox.IsEnabled = !isBusy;
            StatusTextBlock.Text = status;

            if (isBusy)
                StartProgress(timeoutSeconds);
        }

        private void StartProgress(int timeoutSeconds)
        {
            _elapsedSeconds = 0;
            var safeTimeout = timeoutSeconds <= 0 ? DefaultTimeoutSeconds : timeoutSeconds;
            RequestProgressBar.Maximum = safeTimeout;
            RequestProgressBar.Value = 0;
            RequestProgressBar.Visibility = Visibility.Visible;
            ProgressTextBlock.Text = AppStrings.Get("AI_TimeRemainingFmt", safeTimeout);
            ProgressTextBlock.Visibility = Visibility.Visible;
            _progressTimer.Start();
        }

        private void StopProgress()
        {
            _progressTimer.Stop();
            RequestProgressBar.Visibility = Visibility.Collapsed;
            ProgressTextBlock.Visibility = Visibility.Collapsed;
        }

        private void OnProgressTimerTick(object? sender, EventArgs e)
        {
            var maxSeconds = (int)RequestProgressBar.Maximum;
            _elapsedSeconds = Math.Min(_elapsedSeconds + 1, maxSeconds);
            RequestProgressBar.Value = _elapsedSeconds;
            var remaining = Math.Max(maxSeconds - _elapsedSeconds, 0);
            ProgressTextBlock.Text = AppStrings.Get("AI_TimeRemainingFmt", remaining);
        }

        private static int ParseTimeoutSeconds(TextBox box)
        {
            if (int.TryParse(box.Text?.Trim(), out var timeoutSeconds))
                return Math.Clamp(timeoutSeconds, 15, 300);
            return DefaultTimeoutSeconds;
        }

        private static string BuildSummary(AIAssistantResponse response)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(response.Summary))
                lines.Add(response.Summary.Trim());

            if (response.Warnings.Count > 0)
                lines.Add(AppStrings.Get("AI_WarningsPrefix") + string.Join(" | ", response.Warnings.Where(w => !string.IsNullOrWhiteSpace(w))));

            if (response.Refused)
                lines.Add(AppStrings.Get("AI_RefusedSummary"));

            return string.Join(Environment.NewLine + Environment.NewLine, lines);
        }

        private static string BuildTaskConfirmationMessage(IEnumerable<AITaskSuggestion> tasks)
        {
            var lines = new List<string>
            {
                AppStrings.Get("AI_TasksReadyHeader")
            };

            string? previousTaskName = null;
            foreach (var task in tasks)
            {
                var predecessor = string.IsNullOrWhiteSpace(task.PredecessorTaskName)
                    ? (string.IsNullOrWhiteSpace(previousTaskName) ? AppStrings.Get("AI_PredNone") : AppStrings.Get("AI_PredAutoFmt", previousTaskName))
                    : task.PredecessorTaskName.Trim();
                lines.Add(AppStrings.Get("AI_TaskLineFmt", task.Name, Math.Max(task.DurationHours, 1.0).ToString("0.#"), predecessor));
                previousTaskName = task.Name;
            }

            lines.Add(string.Empty);
            lines.Add(AppStrings.Get("AI_ClickApplyHint"));
            return string.Join(Environment.NewLine, lines);
        }
    }
}
