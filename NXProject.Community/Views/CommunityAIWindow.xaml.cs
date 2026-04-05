using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using NXProject.Models;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Views
{
    public partial class CommunityAIWindow : Window
    {
        private const string OpenAIApiKeyGuideUrl = "https://openrouter.ai/docs/api-keys";
        private const int DefaultTimeoutSeconds = 120;

        private readonly MainViewModel _viewModel;
        private AIAssistantResponse? _lastResponse;
        private bool _currentSuggestionsApplied;
        private readonly DispatcherTimer _progressTimer;
        private int _elapsedSeconds;

        public CommunityAIWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _progressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _progressTimer.Tick += OnProgressTimerTick;
            LoadSettings();
            ProjectContextTextBox.Text = _viewModel.BuildAiProjectContext();
        }

        private void LoadSettings()
        {
            var settings = AISettingsStore.Load();
            ApiKeyPasswordBox.Password = settings.ApiKey;
            EndpointTextBox.Text = settings.Endpoint;
            ModelTextBox.Text = settings.Model;
            TimeoutTextBox.Text = settings.TimeoutSeconds.ToString();
        }

        private AISettings BuildSettings()
        {
            var sanitizedApiKey = AISettingsStore.SanitizeSecret(ApiKeyPasswordBox.Password);
            if (!string.Equals(sanitizedApiKey, ApiKeyPasswordBox.Password, StringComparison.Ordinal))
                ApiKeyPasswordBox.Password = sanitizedApiKey;

            var timeoutSeconds = ParseTimeoutSeconds();
            TimeoutTextBox.Text = timeoutSeconds.ToString();

            return new AISettings
            {
                Provider = AIProvider.OpenRouter,
                ApiKey = sanitizedApiKey,
                Endpoint = EndpointTextBox.Text,
                Model = ModelTextBox.Text,
                TimeoutSeconds = timeoutSeconds
            };
        }

        private async void OnGenerateClick(object sender, RoutedEventArgs e)
        {
            var prompt = PromptTextBox.Text.Trim();
            if (!AIPromptSafetyGuard.TryValidate(prompt, out var error, out var warning))
            {
                MessageBox.Show(error, "Pedido bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var settings = BuildSettings();
            AISettingsStore.Save(settings);

            try
            {
                var initialStatus = string.IsNullOrWhiteSpace(warning)
                    ? "Gerando sugestoes com IA..."
                    : warning;
                SetBusy(true, initialStatus, settings.TimeoutSeconds);
                var response = await ProjectAIAssistantService.GenerateTaskSuggestionsAsync(
                    settings,
                    prompt,
                    _viewModel.BuildAiProjectContext());

                _lastResponse = response;
                _currentSuggestionsApplied = false;
                SummaryTextBox.Text = BuildSummary(response) +
                    (response.Tasks.Count > 0 && !response.Refused
                        ? Environment.NewLine + Environment.NewLine + BuildTaskConfirmationMessage(response.Tasks)
                        : string.Empty);
                TasksDataGrid.ItemsSource = response.Tasks;
                ApplyButton.IsEnabled = response.Tasks.Count > 0 && !response.Refused;
                StatusTextBlock.Text = response.Refused
                    ? "Pedido recusado pelas regras de seguranca."
                    : $"{response.Tasks.Count} tarefa(s) sugerida(s) pela IA.";
            }
            catch (Exception ex)
            {
                _lastResponse = null;
                _currentSuggestionsApplied = false;
                SummaryTextBox.Text = string.Empty;
                TasksDataGrid.ItemsSource = Array.Empty<AITaskSuggestion>();
                ApplyButton.IsEnabled = false;
                MessageBox.Show(ex.Message, "Erro na integracao com IA", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusTextBlock.Text = "Falha ao consultar a IA.";
            }
            finally
            {
                StopProgress();
                SetBusy(false, StatusTextBlock.Text);
            }
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            if (_currentSuggestionsApplied)
            {
                StatusTextBlock.Text = "Essas atividades ja foram aplicadas ao projeto.";
                Close();
                return;
            }

            if (_lastResponse == null || _lastResponse.Tasks.Count == 0)
                return;

            var createdCount = _viewModel.ApplyAiTaskSuggestions(_lastResponse.Tasks);
            _currentSuggestionsApplied = createdCount > 0;
            ApplyButton.IsEnabled = false;
            StatusTextBlock.Text = createdCount > 0
                ? $"{createdCount} tarefa(s) aplicada(s) ao projeto e exibida(s) no Gantt."
                : "Nenhuma tarefa valida foi aplicada.";
            Close();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnOpenApiKeyGuideClick(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = OpenAIApiKeyGuideUrl,
                UseShellExecute = true
            });
        }

        private void SetBusy(bool isBusy, string status, int timeoutSeconds = DefaultTimeoutSeconds)
        {
            GenerateButton.IsEnabled = !isBusy;
            ApiKeyPasswordBox.IsEnabled = !isBusy;
            EndpointTextBox.IsEnabled = !isBusy;
            ModelTextBox.IsEnabled = !isBusy;
            TimeoutTextBox.IsEnabled = !isBusy;
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
            ProgressTextBlock.Text = $"Tempo restante estimado: {safeTimeout}s";
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
            ProgressTextBlock.Text = $"Tempo restante estimado: {remaining}s";
        }

        private int ParseTimeoutSeconds()
        {
            if (int.TryParse(TimeoutTextBox.Text?.Trim(), out var timeoutSeconds))
                return Math.Clamp(timeoutSeconds, 15, 300);

            return DefaultTimeoutSeconds;
        }

        private static string BuildSummary(AIAssistantResponse response)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(response.Summary))
                lines.Add(response.Summary.Trim());

            if (response.Warnings.Count > 0)
                lines.Add("Avisos: " + string.Join(" | ", response.Warnings.Where(w => !string.IsNullOrWhiteSpace(w))));

            if (response.Refused)
                lines.Add("O pedido foi recusado pelas regras de seguranca da integracao.");

            return string.Join(Environment.NewLine + Environment.NewLine, lines);
        }

        private static string BuildTaskConfirmationMessage(IEnumerable<AITaskSuggestion> tasks)
        {
            var lines = new List<string>
            {
                "As atividades abaixo estao prontas para inclusao no Gantt:"
            };

            string? previousTaskName = null;
            foreach (var task in tasks)
            {
                var predecessor = string.IsNullOrWhiteSpace(task.PredecessorTaskName)
                    ? (string.IsNullOrWhiteSpace(previousTaskName) ? "nenhuma" : $"{previousTaskName} (automatica)")
                    : task.PredecessorTaskName.Trim();
                lines.Add($"- {task.Name} | duracao: {Math.Max(task.DurationDays, 1)} dia(s) | predecessora: {predecessor}");
                previousTaskName = task.Name;
            }

            lines.Add(string.Empty);
            lines.Add("Clique em 'Aplicar ao projeto' para incluir essas atividades.");
            return string.Join(Environment.NewLine, lines);
        }
    }
}
