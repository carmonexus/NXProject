// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Collections.Generic;
using System.Linq;

namespace NXProject.Models
{
    public class AISettings
    {
        public AIProvider Provider { get; set; } = AIProvider.None;
        public AIAuthMode AuthMode { get; set; } = AIAuthMode.ApiKey;
        public string ApiKey { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 120;
    }

    public enum AIProvider
    {
        None,
        Claude,         // Anthropic Claude
        OpenAI,         // OpenAI / Codex
        OpenRouter,     // OpenRouter OpenAI-compatible API
        GitHubCopilot,  // Azure OpenAI via Copilot
        AzureOpenAI,    // Azure OpenAI da empresa (chat/completions, header api-key)
        LocalLlama,     // IA Local (llama.cpp/LLamaSharp, recursos na pasta do usuario)
        CodexCli,       // Codex instalado na maquina (CLI via WSL, sem chave de API)
        ClaudeCli,      // Claude Code instalado na maquina (CLI, sem chave de API)
        Microsoft365    // Conta corporativa (Entra ID): Copilot Studio / Azure OpenAI / Graph
    }

    /// <summary>Como o token de acesso e obtido para o provedor.</summary>
    public enum AIAuthMode
    {
        ApiKey,        // usuario cola a chave manualmente
        BrowserLogin   // login OAuth no navegador (estilo VS Code)
    }

    /// <summary>Como o cronograma sugerido pela IA e materializado.</summary>
    public enum ScheduleCreationMode
    {
        DevOps,    // cria a hierarquia ate Story sincronizando com o Azure DevOps
        NoDevops   // cria a mesma hierarquia ate Story, porem apenas local
    }

    /// <summary>
    /// Tipo de acao com IA: um nome + o prompt (instrucoes) que sera usado.
    /// Quando <see cref="CreatesTasks"/> e true (modo cronograma), o retorno
    /// e interpretado como tarefas para o Gantt. Quando false (modo livre),
    /// a resposta e apenas exibida, sem criar tarefas.
    /// </summary>
    public class AIActionType
    {
        public string Name { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public bool CreatesTasks { get; set; }

        // Ação hierarquica (Epic→Feature→Story[→Task]) que monta o JSON modelo.
        public const string ScheduleDevOpsActionName = "Fazer Cronograma DevOps";
        // Ação plana (lista de tarefas encadeadas), somente local.
        public const string ScheduleNoDevOpsActionName = "Cronograma NoDevOps";
        // Nome legado (migrado para NoDevops).
        public const string ScheduleActionName = "Fazer Cronograma";
        public const string FreeActionName = "Livre";
        public const string AnalysisActionName = "Analisar Cronograma";
        // Merge de planilha externa (Task Plan) com as Tasks do DevOps.
        public const string MergeExternalActionName = "Merge de Arquivo Externo com Task";
        // Inclusão de tasks na planilha do Task Plan a partir de texto de reunião.
        public const string PlanIncludeActionName = "Incluir Tasks na Planilha";
        // Consulta/localização de tasks na planilha do Task Plan a partir de texto.
        public const string PlanFindActionName = "Consultar Task na Planilha";
        // Ajuste de nomes de task para iniciar com verbo no infinitivo (parser do Task Plan).
        public const string TaskNameFixActionName = "Ajustar Nome de Task (verbo)";
    }

    /// <summary>Configuracao persistida de um provedor especifico.</summary>
    public class AIProviderProfile
    {
        public AIProvider Provider { get; set; } = AIProvider.OpenRouter;
        public AIAuthMode AuthMode { get; set; } = AIAuthMode.ApiKey;
        public string ApiKey { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        /// <summary>Para CLI local (Codex/Claude Code): comando guardado SEPARADAMENTE por local.
        /// A combo Windows/WSL só escolhe qual está valendo (vira o Endpoint ativo).</summary>
        public string CliWindowsCommand { get; set; } = string.Empty;
        public string CliWslCommand { get; set; } = string.Empty;
        /// <summary>URL do site para login no navegador (distinta do endpoint da API).</summary>
        public string LoginUrl { get; set; } = string.Empty;
        /// <summary>Horas ate a sessao do navegador expirar e exigir novo login.</summary>
        public int SessionExpirationHours { get; set; } = 24;
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>Converte o perfil em configuracao efetiva para uma chamada.</summary>
        public AISettings ToSettings() => new()
        {
            Provider = Provider,
            AuthMode = AuthMode,
            ApiKey = ApiKey,
            Endpoint = string.IsNullOrWhiteSpace(Endpoint)
                ? AIProviderDefaults.GetDefaultEndpoint(Provider)
                : Endpoint.Trim(),
            Model = string.IsNullOrWhiteSpace(Model)
                ? AIProviderDefaults.GetDefaultModel(Provider)
                : Model.Trim(),
            TimeoutSeconds = TimeoutSeconds <= 0 ? 120 : TimeoutSeconds
        };
    }

    /// <summary>
    /// Configuracao completa do Assistente de IA: perfis por provedor,
    /// provedor padrao e modo de criacao do cronograma.
    /// </summary>
    public class AIWorkspaceSettings
    {
        public AIProvider DefaultProvider { get; set; } = AIProvider.CodexCli;
        public ScheduleCreationMode ScheduleMode { get; set; } = ScheduleCreationMode.DevOps;
        /// <summary>No modo DevOps: se true, a hierarquia vai ate Task; se false, para em Story.</summary>
        public bool CreateTasks { get; set; }
        /// <summary>Quantidade de tarefas enviadas no contexto da analise de cronograma.</summary>
        public int AnalysisTaskLimit { get; set; } = 30;
        /// <summary>Quantas conversas do chat guardar por cronograma (Work Item Project). 0 = infinito.</summary>
        public int ChatHistoryLimit { get; set; } = 10;
        /// <summary>Mensagens recentes reenviadas à IA por pergunta (janela deslizante). Teto p/ não estourar token.</summary>
        public int ChatHistoryWindow { get; set; } = 8;
        /// <summary>Tamanho (caracteres) que dispara o "compress" do histórico em um resumo. 0 = desligado. Teto 500000.</summary>
        public int ChatCompressThreshold { get; set; } = 350_000;
        /// <summary>Ultimo prompt digitado pelo usuario no assistente.</summary>
        public string LastPrompt { get; set; } = string.Empty;
        public List<AIProviderProfile> Providers { get; set; } = new();

        /// <summary>Tipos de acao com IA (o built-in "Fazer Cronograma" + livres/customizados).</summary>
        public List<AIActionType> ActionTypes { get; set; } = new();
        /// <summary>Nome do tipo de acao selecionado como padrao.</summary>
        public string SelectedAction { get; set; } = AIActionType.ScheduleDevOpsActionName;
        /// <summary>Versao do schema de acoes, para migracoes unicas.</summary>
        public int ActionsSchemaVersion { get; set; }

        public AIActionType GetSelectedAction()
            => ActionTypes.FirstOrDefault(a => a.Name == SelectedAction)
               ?? ActionTypes.FirstOrDefault()
               ?? new AIActionType { Name = AIActionType.ScheduleActionName, CreatesTasks = true };

        /// <summary>Provedores que a UI expoe em abas de configuracao.</summary>
        public static readonly AIProvider[] ConfigurableProviders =
            { AIProvider.OpenAI, AIProvider.AzureOpenAI, AIProvider.OpenRouter };

        public AIProviderProfile GetOrCreate(AIProvider provider)
        {
            var profile = Providers.FirstOrDefault(p => p.Provider == provider);
            if (profile == null)
            {
                profile = new AIProviderProfile
                {
                    Provider = provider,
                    Endpoint = AIProviderDefaults.GetDefaultEndpoint(provider),
                    Model = AIProviderDefaults.GetDefaultModel(provider),
                    LoginUrl = AIProviderDefaults.GetDefaultLoginUrl(provider),
                    AuthMode = AIAuthMode.ApiKey,
                    TimeoutSeconds = AIProviderDefaults.GetDefaultTimeoutSeconds(provider)
                };
                Providers.Add(profile);
            }
            return profile;
        }

        /// <summary>Configuracao efetiva do provedor padrao selecionado.</summary>
        public AISettings ResolveActiveSettings() => GetOrCreate(DefaultProvider).ToSettings();
    }

    public static class AIProviderDefaults
    {
        public static string GetDefaultEndpoint(AIProvider provider) => provider switch
        {
            AIProvider.Claude => "https://api.anthropic.com/v1/messages",
            AIProvider.OpenAI => "https://api.openai.com/v1/chat/completions",
            AIProvider.OpenRouter => "https://openrouter.ai/api/v1/chat/completions",
            AIProvider.GitHubCopilot => "https://api.githubcopilot.com/chat/completions",
            AIProvider.AzureOpenAI => string.Empty, // URL do deployment da empresa (com api-version)
            _ => string.Empty
        };

        /// <summary>Timeout padrao por provedor. OpenRouter (free costuma ser lento) usa 240s;
        /// IA Local em CPU pode levar minutos.</summary>
        public static int GetDefaultTimeoutSeconds(AIProvider provider) => provider switch
        {
            AIProvider.OpenRouter => 240,
            AIProvider.LocalLlama => 600,
            AIProvider.CodexCli => 600,   // CLI local: a resposta pode levar minutos
            AIProvider.ClaudeCli => 600,
            _ => 120
        };

        public static string GetDefaultModel(AIProvider provider) => provider switch
        {
            AIProvider.Claude => "claude-opus-5",
            AIProvider.OpenAI => "gpt-4o",
            AIProvider.OpenRouter => "openrouter/free",
            AIProvider.GitHubCopilot => "gpt-4o",
            AIProvider.AzureOpenAI => string.Empty,
            _ => string.Empty
        };

        /// <summary>Provedor que roda NA MAQUINA (IA Local, Codex CLI) nao usa chave de API —
        /// as telas nao podem exigir token nem mandar "configure a IA Geral" para eles.</summary>
        public static bool IsLocalProvider(AIProvider provider)
            => provider is AIProvider.LocalLlama or AIProvider.CodexCli or AIProvider.ClaudeCli;

        /// <summary>A configuracao esta pronta para chamar a IA? (chave, quando o provedor exige).</summary>
        public static bool IsConfigured(AISettings settings)
            => settings != null
               && settings.Provider != AIProvider.None
               && (IsLocalProvider(settings.Provider) || !string.IsNullOrWhiteSpace(settings.ApiKey));

        /// <summary>Rótulo curto do provedor ATIVO para logs e indicadores: nome + modelo/comando
        /// quando ajuda a identificar (ex.: "Claude (claude-opus-5)", "Codex (local)").</summary>
        public static string DescribeActive(AISettings settings)
        {
            if (settings == null || settings.Provider == AIProvider.None)
                return "IA";
            var name = GetDisplayName(settings.Provider);
            var model = settings.Model?.Trim();
            if (string.IsNullOrWhiteSpace(model))
                model = GetDefaultModel(settings.Provider);
            return settings.Provider switch
            {
                AIProvider.LocalLlama => name,                            // o modelo é o GGUF na pasta
                AIProvider.CodexCli or AIProvider.ClaudeCli => name,      // "(local)" já está no nome
                AIProvider.Microsoft365 => name,
                _ => string.IsNullOrWhiteSpace(model) ? name : $"{name} ({model})",
            };
        }

        public static string GetDefaultLoginUrl(AIProvider provider) => provider switch
        {
            AIProvider.OpenAI => "https://chatgpt.com/",
            AIProvider.Claude => "https://claude.ai/",
            AIProvider.OpenRouter => "https://openrouter.ai/",
            AIProvider.GitHubCopilot => "https://github.com/login",
            _ => string.Empty
        };

        public static string GetDisplayName(AIProvider provider) => provider switch
        {
            AIProvider.OpenAI => "OpenAI",
            AIProvider.OpenRouter => "OpenRouter",
            AIProvider.AzureOpenAI => "Azure OpenAI",
            AIProvider.LocalLlama => "IA Local (LLaMA)",
            AIProvider.Claude => "Claude",
            AIProvider.GitHubCopilot => "GitHub Copilot",
            AIProvider.CodexCli => "Codex (local)",
            AIProvider.ClaudeCli => "Claude Code (local)",
            AIProvider.Microsoft365 => "Microsoft 365 (conta da empresa)",
            _ => provider.ToString()
        };
    }
}
