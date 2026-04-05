using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NXProject.Models;

namespace NXProject.Services
{
    public static class ProjectAIAssistantService
    {
        private static readonly HttpClient HttpClient = new();

        public static async Task<AIAssistantResponse> GenerateTaskSuggestionsAsync(
            AISettings settings,
            string userRequest,
            string projectContext,
            CancellationToken cancellationToken = default)
        {
            var apiKey = AISettingsStore.SanitizeSecret(settings.ApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Informe um token de IA antes de gerar sugestoes.");

            var endpoint = string.IsNullOrWhiteSpace(settings.Endpoint)
                ? AIProviderDefaults.GetDefaultEndpoint(AIProvider.OpenRouter)
                : settings.Endpoint.Trim();

            var model = string.IsNullOrWhiteSpace(settings.Model)
                ? AIProviderDefaults.GetDefaultModel(AIProvider.OpenRouter)
                : settings.Model.Trim();
            var timeoutSeconds = settings.TimeoutSeconds <= 0 ? 120 : settings.TimeoutSeconds;

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var developerPrompt = """
Voce e um assistente do NXProject Community focado apenas em planejamento e execucao de projetos.

Regras obrigatorias:
- Aceite apenas pedidos sobre criacao de tarefas, decomposicao de atividades, cronograma, dependencias, estimativas e distribuicao de trabalho por pessoa ou recurso.
- Recuse qualquer pedido que envolva dados pessoais, dados sensiveis, itens de LGPD, informacoes de cliente, documentos, saude, financeiro pessoal ou qualquer assunto fora de projeto.
- Nao solicite nem repita dados pessoais.
- Quando recusar, explique brevemente o motivo e nao gere tarefas.
- Quando aceitar, gere sugestoes objetivas que possam ser usadas em um plano de projeto.
- Cada tarefa precisa obrigatoriamente ter nome, durationDays e predecessorTaskName.
- Se a tarefa nao tiver predecessora, use predecessorTaskName vazio.
- Pense as tarefas ja prontas para inclusao em um grafico de Gantt.
- Responda somente em JSON valido.

Formato JSON esperado:
{
  "refused": false,
  "summary": "resumo curto",
  "warnings": ["aviso opcional"],
  "tasks": [
    {
      "name": "Nome da tarefa",
      "durationDays": 3,
      "predecessorTaskName": "Nome exato da tarefa predecessora ou vazio",
      "assignee": "Nome do responsavel ou vazio",
      "notes": "descricao curta"
    }
  ]
}
""";

            var userPrompt = $"""
Contexto atual do projeto:
{projectContext}

Pedido do usuario:
{userRequest}
""";

            var payload = new
            {
                model,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "developer", content = developerPrompt },
                    new { role = "user", content = userPrompt }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var response = await HttpClient.SendAsync(request, timeoutCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Falha ao chamar a IA: {(int)response.StatusCode} {response.ReasonPhrase}\n{responseBody}");

            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var content = root
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("A IA nao retornou conteudo.");

            return ParseAssistantResponse(content);
        }

        private static AIAssistantResponse ParseAssistantResponse(string content)
        {
            var cleanJson = content.Trim();
            if (cleanJson.StartsWith("```"))
            {
                var firstBrace = cleanJson.IndexOf('{');
                var lastBrace = cleanJson.LastIndexOf('}');
                if (firstBrace >= 0 && lastBrace > firstBrace)
                    cleanJson = cleanJson[firstBrace..(lastBrace + 1)];
            }

            using var document = JsonDocument.Parse(cleanJson);
            var root = document.RootElement;
            var result = new AIAssistantResponse
            {
                Refused = root.TryGetProperty("refused", out var refused) && refused.GetBoolean(),
                Summary = root.TryGetProperty("summary", out var summary) ? summary.GetString() ?? string.Empty : string.Empty
            };

            if (root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in warnings.EnumerateArray())
                {
                    var warning = item.GetString();
                    if (!string.IsNullOrWhiteSpace(warning))
                        result.Warnings.Add(warning);
                }
            }

            if (root.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tasks.EnumerateArray())
                {
                    var suggestion = new AITaskSuggestion
                    {
                        Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                        DurationDays = item.TryGetProperty("durationDays", out var duration) && duration.TryGetInt32(out var days)
                            ? Math.Max(days, 1)
                            : 1,
                        PredecessorTaskName = item.TryGetProperty("predecessorTaskName", out var predecessorTaskName)
                            ? predecessorTaskName.GetString() ?? string.Empty
                            : string.Empty,
                        Assignee = item.TryGetProperty("assignee", out var assignee) ? assignee.GetString() ?? string.Empty : string.Empty,
                        Notes = item.TryGetProperty("notes", out var notes) ? notes.GetString() ?? string.Empty : string.Empty
                    };

                    if (!string.IsNullOrWhiteSpace(suggestion.Name))
                        result.Tasks.Add(suggestion);
                }
            }

            if (result.Refused)
            {
                result.Tasks.Clear();
                if (result.Warnings.Count == 0)
                    result.Warnings.Add("Pedido recusado pelas regras de seguranca da IA.");
            }

            return result;
        }
    }
}
