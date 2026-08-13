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
        // Timeout infinito no HttpClient: quem controla o tempo é o CancellationTokenSource
        // com o TimeoutSeconds configurado por provedor (senão o padrão de 100s do HttpClient
        // cancelaria antes de timeouts maiores, ex.: OpenRouter em 240s).
        private static readonly HttpClient HttpClient = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        /// <summary>
        /// Gerador da IA Local (LLaMA), registrado pelo app na inicialização. Recebe
        /// (prompt de sistema, prompt do usuário já com contexto) e devolve o texto da
        /// resposta. Quando o provedor é LocalLlama, as chamadas usam este gerador no
        /// lugar do HTTP — nada sai da máquina.
        /// </summary>
        public static Func<string, string, CancellationToken, Task<string>>? LocalGenerator { get; set; }

        private static Task<string> RunLocalAsync(string systemPrompt, string userRequest, string projectContext, CancellationToken ct)
        {
            if (LocalGenerator == null)
                throw new InvalidOperationException("IA Local não inicializada. Use o menu IA → Gerenciar IA Local para instalar e validar os recursos.");
            return LocalGenerator(systemPrompt, BuildUserPrompt(userRequest, projectContext), ct);
        }

        public static async Task<AIAssistantResponse> GenerateTaskSuggestionsAsync(
            AISettings settings,
            string userRequest,
            string projectContext,
            string? customDeveloperPrompt = null,
            CancellationToken cancellationToken = default)
        {
            if (settings.Provider == AIProvider.LocalLlama)
            {
                var localPrompt = string.IsNullOrWhiteSpace(customDeveloperPrompt) ? TaskDeveloperPrompt : customDeveloperPrompt;
                var localContent = await RunLocalAsync(localPrompt, userRequest, projectContext, cancellationToken);
                return ParseAssistantResponse(localContent);
            }

            // Codex instalado na maquina: CLI local, sem chave e sem endpoint HTTP.
            if (settings.Provider is AIProvider.CodexCli or AIProvider.ClaudeCli)
            {
                var codexPrompt = string.IsNullOrWhiteSpace(customDeveloperPrompt) ? TaskDeveloperPrompt : customDeveloperPrompt;
                var codexContent = await CodexCliService.GenerateAsync(
                    codexPrompt, BuildUserPrompt(userRequest, projectContext),
                    settings.Endpoint, settings.TimeoutSeconds, cancellationToken);
                return ParseAssistantResponse(codexContent);
            }

            var apiKey = AISettingsStore.SanitizeSecret(settings.ApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Informe um token de IA antes de gerar sugestoes.");

            var endpoint = string.IsNullOrWhiteSpace(settings.Endpoint)
                ? AIProviderDefaults.GetDefaultEndpoint(settings.Provider)
                : settings.Endpoint.Trim();
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new InvalidOperationException($"Configure o endpoint do provedor {AIProviderDefaults.GetDisplayName(settings.Provider)} antes de gerar sugestoes.");

            var model = string.IsNullOrWhiteSpace(settings.Model)
                ? AIProviderDefaults.GetDefaultModel(settings.Provider)
                : settings.Model.Trim();
            var timeoutSeconds = settings.TimeoutSeconds <= 0 ? 120 : settings.TimeoutSeconds;

            var developerPrompt = string.IsNullOrWhiteSpace(customDeveloperPrompt)
                ? TaskDeveloperPrompt
                : customDeveloperPrompt;

            var userPrompt = BuildUserPrompt(userRequest, projectContext);

            using var request = BuildChatRequest(
                settings, endpoint, apiKey, model, developerPrompt, userPrompt, 4000);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var response = await HttpClient.SendAsync(request, timeoutCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Falha ao chamar a IA: {(int)response.StatusCode} {response.ReasonPhrase}\n{responseBody}");

            using var document = JsonDocument.Parse(responseBody);
            var content = ExtractChatContent(settings.Provider, document.RootElement);

            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("A IA nao retornou conteudo.");

            return ParseAssistantResponse(content);
        }

        /// <summary>
        /// Modo livre: envia um prompt de sistema arbitrario e devolve o texto
        /// da resposta, sem formato JSON e sem interpretar como tarefas.
        /// </summary>

        // ── Protocolo por provedor ───────────────────────────────────────────
        // Claude (Anthropic) NAO e OpenAI-compatible: autentica por x-api-key,
        // manda o prompt de sistema em campo proprio ("system", fora de messages)
        // e devolve o texto em content[0].text — nao em choices[0].message.content.
        private static bool IsAnthropic(AIProvider provider) => provider == AIProvider.Claude;

        private const string AnthropicVersion = "2023-06-01";

        private static HttpRequestMessage BuildChatRequest(
            AISettings settings, string endpoint, string apiKey, string model,
            string systemPrompt, string userPrompt, int maxTokens)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Identificação de ORIGEM do app (NXProject / Nexus Xdata). O OpenRouter usa
            // "HTTP-Referer" e "X-Title" para atribuição/ranking de apps; os demais provedores
            // apenas veem o User-Agent. Não envia dado sensível — só a origem da ferramenta.
            request.Headers.TryAddWithoutValidation("User-Agent", "NXProject/1.0 (Nexus Xdata)");
            request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://www.nexusxdata.com");
            request.Headers.TryAddWithoutValidation("X-Title", "NXProject - Nexus Xdata");

            object payload;
            if (IsAnthropic(settings.Provider))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                payload = new
                {
                    model,
                    max_tokens = maxTokens,
                    system = systemPrompt,
                    messages = new object[] { new { role = "user", content = userPrompt } }
                };
            }
            else if (settings.Provider == AIProvider.AzureOpenAI)
            {
                // Azure OpenAI autentica pelo header "api-key" (nao Bearer) e usa o
                // MESMO corpo do OpenAI. O endpoint ja e a URL do deployment com api-version.
                request.Headers.TryAddWithoutValidation("api-key", apiKey);
                payload = new
                {
                    max_tokens = maxTokens,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                };
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                payload = new
                {
                    model,
                    max_tokens = maxTokens,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                };
            }

            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return request;
        }

        private static string? ExtractChatContent(AIProvider provider, JsonElement root)
        {
            if (!IsAnthropic(provider))
                return root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

            // Anthropic: content e uma LISTA de blocos; o texto pode nao ser o primeiro
            // (blocos de raciocinio vem antes). Concatena todos os blocos de texto.
            if (!root.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                return null;
            var sb = new StringBuilder();
            foreach (var block in blocks.EnumerateArray())
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                    sb.Append(txt.GetString());
            return sb.Length > 0 ? sb.ToString() : null;
        }

        public static async Task<string> GenerateFreeTextAsync(
            AISettings settings,
            string systemPrompt,
            string userRequest,
            string projectContext,
            CancellationToken cancellationToken = default)
        {
            if (settings.Provider == AIProvider.LocalLlama)
                return await RunLocalAsync(
                    string.IsNullOrWhiteSpace(systemPrompt) ? "Voce e um assistente util." : systemPrompt,
                    userRequest, projectContext, cancellationToken);

            // Codex instalado na maquina: CLI local, sem chave e sem endpoint HTTP.
            if (settings.Provider is AIProvider.CodexCli or AIProvider.ClaudeCli)
                return await CodexCliService.GenerateAsync(
                    string.IsNullOrWhiteSpace(systemPrompt) ? "Voce e um assistente util." : systemPrompt,
                    BuildUserPrompt(userRequest, projectContext),
                    settings.Endpoint, settings.TimeoutSeconds, cancellationToken);

            var apiKey = AISettingsStore.SanitizeSecret(settings.ApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Informe um token de IA antes de executar.");

            var endpoint = string.IsNullOrWhiteSpace(settings.Endpoint)
                ? AIProviderDefaults.GetDefaultEndpoint(settings.Provider)
                : settings.Endpoint.Trim();
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new InvalidOperationException($"Configure o endpoint do provedor {AIProviderDefaults.GetDisplayName(settings.Provider)} antes de executar.");

            var model = string.IsNullOrWhiteSpace(settings.Model)
                ? AIProviderDefaults.GetDefaultModel(settings.Provider)
                : settings.Model.Trim();
            var timeoutSeconds = settings.TimeoutSeconds <= 0 ? 120 : settings.TimeoutSeconds;

            using var request = BuildChatRequest(
                settings, endpoint, apiKey, model,
                string.IsNullOrWhiteSpace(systemPrompt) ? "Voce e um assistente util." : systemPrompt,
                BuildUserPrompt(userRequest, projectContext), 4000);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var response = await HttpClient.SendAsync(request, timeoutCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Falha ao chamar a IA: {(int)response.StatusCode} {response.ReasonPhrase}\n{responseBody}");

            using var document = JsonDocument.Parse(responseBody);
            var content = ExtractChatContent(settings.Provider, document.RootElement);

            return content ?? string.Empty;
        }

        /// <summary>
        /// Prompt do "Fazer Cronograma" hierarquico (Epic → Feature → Story [→ Task]).
        /// Quando <paramref name="untilTask"/> e true, pede tambem o nivel Task.
        /// </summary>
        public static string BuildScheduleDeveloperPrompt(bool untilTask, bool includeSprint = false)
        {
            var levelList = untilTask
                ? "\"Assunto Geral\", \"Grupo de Task\", \"Macro Task\" e \"Task\""
                : "\"Assunto Geral\", \"Grupo de Task\" e \"Macro Task\"";
            var leaf = untilTask ? "Task" : "Macro Task";
            var taskLine = untilTask
                ? "- Ordem dos niveis: Assunto Geral (topo) > Grupo de Task > Macro Task > Task (folha).\n"
                : "- Ordem dos niveis: Assunto Geral (topo) > Grupo de Task > Macro Task (folha).\n";
            var sprintLine = includeSprint
                ? "- Atribua \"sprint\" (inteiro, comecando em 1) a cada folha. A sprint 1 comeca na data de inicio do projeto (ver contexto) e cada sprint dura a 'Duracao do sprint' em dias (ver contexto). Itens em sequencia avancam de sprint conforme consomem os dias.\n"
                : string.Empty;
            var sprintField = includeSprint ? ", \"sprint\": 1" : string.Empty;

            return $$"""
Voce e um assistente do NXProject Community que monta CRONOGRAMAS de projeto.

Gere uma LISTA LINEAR (nao aninhada) de itens, na ordem em que aparecem no cronograma.
Cada item tem "level", "name" e, nas folhas, "estimatedHours" e "assignee".

Regras obrigatorias:
- "level" deve ser um de: {{levelList}}.
{{taskLine}}{{sprintLine}}- A hierarquia e deduzida pela ordem: cada item pertence ao ultimo item de nivel imediatamente superior.
- estimatedHours = horas de trabalho estimadas da folha ({{leaf}}). Itens acima da folha NAO precisam de horas.
- Organize o trabalho em FASES/ETAPAS logicas do ciclo de vida do projeto, do inicio ate a entrega
  (ex.: planejamento/projeto, preparacao, execucao, validacao/entrega) — mas escolha as fases que
  fazem sentido para ESTE trabalho especifico; NAO force nomes de fase que nao se apliquem.
- assignee: informe o responsavel APENAS se ele existir no CONTEXTO/pedido; se nao houver, deixe
  vazio ("") — NAO invente nomes de pessoas (ex.: nao crie "Joao"/"Maria" do nada).
- NAO repita o mesmo nome como grupo e como folha; cada item tem um nome distinto e o grupo agrega
  folhas com nomes diferentes dele.
- NAO repita itens: cada grupo/tarefa aparece UMA UNICA VEZ na lista; jamais gere a mesma atividade
  (ou o mesmo bloco de atividades) mais de uma vez. Se ja listou um item, siga para o proximo.
- Aceite apenas planejamento de projeto (atividades, cronograma, estimativas, distribuicao por pessoa/recurso).
- Recuse pedidos com dados pessoais/sensiveis/LGPD ou fora de projeto; ao recusar, use refused=true e items vazio.
- Responda SOMENTE com JSON valido, sem texto fora do JSON.

Formato JSON esperado:
{
  "refused": false,
  "summary": "resumo curto",
  "warnings": [],
  "items": [
    { "level": "Assunto Geral", "name": "Nome do assunto" },
    { "level": "Grupo de Task", "name": "Nome do grupo" },
    { "level": "Macro Task", "name": "Nome da macro task", "estimatedHours": 40, "assignee": "Nome"{{sprintField}} }{{(untilTask ? ",\n    { \"level\": \"Task\", \"name\": \"Nome da task\", \"estimatedHours\": 8, \"assignee\": \"Nome\"" + sprintField + " }" : "")}}
  ]
}
""";
        }

        /// <summary>Faz o parse da resposta hierarquica do cronograma.</summary>
        public static AIScheduleResponse ParseScheduleResponse(string content)
        {
            var cleanJson = (content ?? string.Empty).Trim();
            if (cleanJson.StartsWith("```"))
            {
                var firstBrace = cleanJson.IndexOf('{');
                var lastBrace = cleanJson.LastIndexOf('}');
                if (firstBrace >= 0 && lastBrace > firstBrace)
                    cleanJson = cleanJson[firstBrace..(lastBrace + 1)];
            }

            var result = new AIScheduleResponse();

            JsonDocument doc;
            var truncated = false;
            try
            {
                doc = JsonDocument.Parse(cleanJson);
            }
            catch (JsonException)
            {
                // Resposta truncada (limite de tokens do modelo). Tenta aproveitar a parte
                // válida fechando o JSON no último item completo; senão, avisa amigável.
                var repaired = TryRepairTruncatedJson(cleanJson, "items")
                               ?? TryRepairTruncatedJson(cleanJson, "roots");
                if (repaired == null)
                {
                    result.Warnings.Add(
                        "A IA retornou um cronograma incompleto (JSON truncado — provavelmente o limite de tokens do modelo). " +
                        "Tente de novo, reduza o escopo, aumente o MaxTokens do modelo local ou use um modelo mais forte.");
                    return result;
                }
                try { doc = JsonDocument.Parse(repaired); truncated = true; }
                catch
                {
                    result.Warnings.Add(
                        "A IA retornou um cronograma inválido/incompleto. Tente de novo ou use um modelo mais forte.");
                    return result;
                }
            }

            using (doc)
            {
            var root = doc.RootElement;

            result.Refused = root.TryGetProperty("refused", out var refused) && refused.ValueKind == JsonValueKind.True;
            result.Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            if (root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
                foreach (var w in warnings.EnumerateArray())
                    if (w.GetString() is { Length: > 0 } ws) result.Warnings.Add(ws);

            // Formato preferido: lista LINEAR "items" (remontada em arvore pela ordem/nivel).
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                var flat = new List<AIScheduleNode>();
                foreach (var it in items.EnumerateArray())
                    if (ParseScheduleNode(it) is { } fn) flat.Add(fn);
                result.Roots.AddRange(BuildTreeFromLinear(flat));
            }
            // Compatibilidade: formato aninhado "roots".
            else if (root.TryGetProperty("roots", out var roots) && roots.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in roots.EnumerateArray())
                    if (ParseScheduleNode(node) is { } n) result.Roots.Add(n);
            }

            if (result.Refused)
                result.Roots.Clear();

            if (truncated && result.Roots.Count > 0)
                result.Warnings.Add(
                    "Aviso: a resposta da IA veio truncada; o cronograma foi montado só com a parte válida — " +
                    "confira se ficou completo (aumente o MaxTokens do modelo para respostas maiores).");

            return result;
            }
        }

        /// <summary>Rank do nivel: 0=Assunto Geral/Epic ... 3=Task. Menor = mais alto.</summary>
        private static int LevelRank(string? level)
        {
            var t = (level ?? string.Empty).Trim();
            if (t.Equals("Assunto Geral", StringComparison.OrdinalIgnoreCase) || t.Equals("Epic", StringComparison.OrdinalIgnoreCase) || t.Equals("N1", StringComparison.OrdinalIgnoreCase)) return 0;
            if (t.Equals("Grupo de Task", StringComparison.OrdinalIgnoreCase) || t.Equals("Feature", StringComparison.OrdinalIgnoreCase) || t.Equals("N2", StringComparison.OrdinalIgnoreCase)) return 1;
            if (t.Equals("Macro Task", StringComparison.OrdinalIgnoreCase) || t.Equals("Story", StringComparison.OrdinalIgnoreCase) || t.Equals("N3", StringComparison.OrdinalIgnoreCase)) return 2;
            if (t.Equals("Task", StringComparison.OrdinalIgnoreCase) || t.Equals("N4", StringComparison.OrdinalIgnoreCase)) return 3;
            return 2; // desconhecido: trata como Macro Task/Story
        }

        /// <summary>Remonta a árvore a partir da lista linear, usando o rank de cada nível.</summary>
        private static List<AIScheduleNode> BuildTreeFromLinear(List<AIScheduleNode> flat)
        {
            var roots = new List<AIScheduleNode>();
            var lastAt = new AIScheduleNode?[4];

            foreach (var raw in flat)
            {
                var node = raw;
                var rank = LevelRank(node.Type);
                AIScheduleNode? parent = null;
                for (var r = rank - 1; r >= 0; r--)
                    if (lastAt[r] != null) { parent = lastAt[r]; break; }

                var siblings = parent == null ? roots : parent.Children;

                // Dedup: se já existe um irmão de MESMO nível e MESMO nome, reutiliza-o em vez
                // de duplicar. Protege contra modelos que entram em loop repetindo a atividade
                // (o item repetido e seus filhos repetidos são absorvidos no já existente).
                AIScheduleNode? twin = null;
                foreach (var sib in siblings)
                    if (LevelRank(sib.Type) == rank
                        && string.Equals(sib.Name?.Trim(), node.Name?.Trim(), StringComparison.OrdinalIgnoreCase))
                    { twin = sib; break; }

                if (twin != null) node = twin;
                else siblings.Add(node);

                if (rank >= 0 && rank < lastAt.Length)
                {
                    lastAt[rank] = node;
                    for (var r = rank + 1; r < lastAt.Length; r++) lastAt[r] = null;
                }
            }
            return roots;
        }

        private static AIScheduleNode? ParseScheduleNode(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return null;

            // Aceita "level" (N1..N4) ou "type" (Epic/Feature/Story/Task) como indicador de nivel.
            var levelOrType = el.TryGetProperty("level", out var lv) ? lv.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(levelOrType) && el.TryGetProperty("type", out var t))
                levelOrType = t.GetString() ?? string.Empty;

            var node = new AIScheduleNode
            {
                Name = name.Trim(),
                Type = levelOrType,
                Code = el.TryGetProperty("code", out var cd) ? cd.GetString() ?? string.Empty : string.Empty,
                Assignee = el.TryGetProperty("assignee", out var a) ? a.GetString() ?? string.Empty : string.Empty,
                Notes = el.TryGetProperty("notes", out var no) ? no.GetString() ?? string.Empty : string.Empty
            };
            if (el.TryGetProperty("estimatedHours", out var eh) && eh.ValueKind == JsonValueKind.Number && eh.TryGetDouble(out var hours))
                node.EstimatedHours = System.Math.Max(0.0, hours);
            if (el.TryGetProperty("durationDays", out var dd) && dd.ValueKind == JsonValueKind.Number && dd.TryGetDouble(out var days))
                node.DurationDays = System.Math.Max(0.0, days);
            if (el.TryGetProperty("sprint", out var sp) && sp.ValueKind == JsonValueKind.Number && sp.TryGetInt32(out var sprint))
                node.Sprint = System.Math.Max(0, sprint);

            // Âncoras do item existente (preservadas quando a IA as devolve).
            if (el.TryGetProperty("id", out var idp)) node.Id = idp.GetString()?.Trim() ?? string.Empty;
            if (el.TryGetProperty("percent", out var pc) && pc.ValueKind == JsonValueKind.Number && pc.TryGetDouble(out var pct))
                node.PercentComplete = System.Math.Clamp(pct, 0, 100);
            if (el.TryGetProperty("startFixed", out var sf) && sf.ValueKind is JsonValueKind.True or JsonValueKind.False)
                node.StartFixed = sf.GetBoolean();
            if (el.TryGetProperty("finishFixed", out var ff) && ff.ValueKind is JsonValueKind.True or JsonValueKind.False)
                node.FinishFixed = ff.GetBoolean();
            if (el.TryGetProperty("fixedStart", out var fs) && fs.ValueKind == JsonValueKind.String
                && System.DateTime.TryParse(fs.GetString(), out var fsd)) node.FixedStart = fsd;
            if (el.TryGetProperty("fixedFinish", out var fe) && fe.ValueKind == JsonValueKind.String
                && System.DateTime.TryParse(fe.GetString(), out var fed)) node.FixedFinish = fed;
            if (el.TryGetProperty("predecessors", out var pr) && pr.ValueKind == JsonValueKind.Array)
                foreach (var d in pr.EnumerateArray())
                    if (d.GetString()?.Trim() is { Length: > 0 } dep) node.Predecessors.Add(dep);
            if (el.TryGetProperty("currentHours", out var ch) && ch.ValueKind == JsonValueKind.Number && ch.TryGetDouble(out var chv))
                node.CurrentHours = System.Math.Max(0, chv);
            if (el.TryGetProperty("originalHours", out var oh) && oh.ValueKind == JsonValueKind.Number && oh.TryGetDouble(out var ohv))
                node.OriginalEstimatedHours = System.Math.Max(0, ohv);

            if (el.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var c in children.EnumerateArray())
                    if (ParseScheduleNode(c) is { } cn) node.Children.Add(cn);

            return node;
        }

        /// <summary>Instrucoes do sistema/developer para geracao de tarefas.</summary>
        public const string TaskDeveloperPrompt = """
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

        public const string ScheduleAnalysisPrompt = """
Voce e um assistente do NXProject Community especializado em analisar cronogramas de projeto.

Regras obrigatorias:
- Analise o cronograma fornecido, valide estimativas, dependencias, sequencia e alocacao de recursos.
- Retorne um feedback claro sobre riscos, constrangimentos, aceleracoes possiveis, tarefas sobrecarregadas, dependencias mal definidas e comentarios de melhoria.
- Nao gere tarefas nem altere o cronograma; apenas ofereca analise e comentarios.
- Se o cronograma tiver problemas de consistencia, destaque-os.
- Responda idealmente usando HTML com secoes, tabelas e listas, para facilitar a visualizacao no WebView2. Se nao puder gerar HTML, responda em texto estruturado.

Formato desejado:
- Uma breve conclusao no topo.
- Uma tabela ou lista com observacoes por tarefa.
- Um resumo de pontos de atencao.
""";

        /// <summary>Monta o prompt do usuario com contexto do projeto.</summary>
        public static string BuildUserPrompt(string userRequest, string projectContext) => $"""
Contexto atual do projeto:
{projectContext}

Pedido do usuario:
{userRequest}
""";

        /// <summary>Prompt unico (developer + user) para canais sem separacao de papeis, como o chat web.</summary>
        public static string BuildCombinedPrompt(string userRequest, string projectContext) =>
            TaskDeveloperPrompt + "\n\n" + BuildUserPrompt(userRequest, projectContext);

        public static AIAssistantResponse ParseAssistantResponse(string content)
        {
            var cleanJson = content.Trim();
            if (cleanJson.StartsWith("```"))
            {
                var firstBrace = cleanJson.IndexOf('{');
                var lastBrace = cleanJson.LastIndexOf('}');
                if (firstBrace >= 0 && lastBrace > firstBrace)
                    cleanJson = cleanJson[firstBrace..(lastBrace + 1)];
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(cleanJson);
            }
            catch (JsonException)
            {
                // Resposta possivelmente truncada (limite de tokens do modelo).
                // Tenta reparar fechando o JSON no ultimo objeto de tarefa completo.
                var repaired = TryRepairTruncatedJson(cleanJson);
                if (repaired == null)
                    throw new InvalidOperationException(
                        "A IA retornou um JSON incompleto (resposta provavelmente truncada pelo limite de tokens do modelo). " +
                        "Reduza o escopo do pedido, aumente o timeout, ou use um modelo com saida maior.");
                document = JsonDocument.Parse(repaired);
            }

            using (document)
            {
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
                        HasDurationHours = false,
                        DurationHours = 0.0,
                        DurationDays = item.TryGetProperty("durationDays", out var duration) && duration.TryGetInt32(out var days)
                            ? Math.Max(days, 1)
                            : 1,
                        PredecessorTaskName = item.TryGetProperty("predecessorTaskName", out var predecessorTaskName)
                            ? predecessorTaskName.GetString() ?? string.Empty
                            : string.Empty,
                        Assignee = item.TryGetProperty("assignee", out var assignee) ? assignee.GetString() ?? string.Empty : string.Empty,
                        Notes = item.TryGetProperty("notes", out var notes) ? notes.GetString() ?? string.Empty : string.Empty
                    };
                    if (item.TryGetProperty("durationHours", out var durationHours) && durationHours.ValueKind == JsonValueKind.Number && durationHours.TryGetDouble(out var hours))
                    {
                        suggestion.HasDurationHours = true;
                        suggestion.DurationHours = Math.Max(0.0, hours);
                    }
                    else if (item.TryGetProperty("durationDays", out var durationDays) && durationDays.TryGetInt32(out var parsedDays))
                    {
                        suggestion.HasDurationHours = true;
                        suggestion.DurationHours = Math.Max(parsedDays, 1) * ProjectCalendarService.WorkingHoursPerDay;
                    }

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

        /// <summary>
        /// Tenta reparar um JSON de tarefas truncado: corta ate o ultimo objeto
        /// de tarefa completo e fecha o array/objeto. Retorna null se nao der.
        /// </summary>
        private static string? TryRepairTruncatedJson(string json, string arrayProp = "tasks")
        {
            var tasksIdx = json.IndexOf("\"" + arrayProp + "\"", StringComparison.OrdinalIgnoreCase);
            if (tasksIdx < 0) return null;

            var arrStart = json.IndexOf('[', tasksIdx);
            if (arrStart < 0) return null;

            // Acha o fim do ultimo objeto '}' completo dentro do array (equilibra chaves).
            int depth = 0, lastComplete = -1;
            var inString = false;
            var escape = false;
            for (var i = arrStart + 1; i < json.Length; i++)
            {
                var c = json[i];
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) lastComplete = i; }
            }

            if (lastComplete < 0) return null;
            var head = json[..(lastComplete + 1)];
            return head + "]}";
        }
    }
}
