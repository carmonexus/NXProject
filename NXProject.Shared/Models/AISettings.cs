namespace NXProject.Models
{
    public class AISettings
    {
        public AIProvider Provider { get; set; } = AIProvider.None;
        public string ApiKey { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
    }

    public enum AIProvider
    {
        None,
        Claude,         // Anthropic Claude
        OpenAI,         // OpenAI / Codex
        GitHubCopilot   // Azure OpenAI via Copilot
    }

    public static class AIProviderDefaults
    {
        public static string GetDefaultEndpoint(AIProvider provider) => provider switch
        {
            AIProvider.Claude => "https://api.anthropic.com/v1/messages",
            AIProvider.OpenAI => "https://api.openai.com/v1/chat/completions",
            AIProvider.GitHubCopilot => "https://api.githubcopilot.com/chat/completions",
            _ => string.Empty
        };

        public static string GetDefaultModel(AIProvider provider) => provider switch
        {
            AIProvider.Claude => "claude-sonnet-4-6",
            AIProvider.OpenAI => "gpt-4o",
            AIProvider.GitHubCopilot => "gpt-4o",
            _ => string.Empty
        };
    }
}
