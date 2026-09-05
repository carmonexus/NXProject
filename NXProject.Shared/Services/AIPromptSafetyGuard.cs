// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Globalization;
using System.Text;

namespace NXProject.Services
{
    public sealed class AIPromptValidationResult
    {
        public bool IsValid { get; init; }
        public string Error { get; init; } = string.Empty;
        public string Warning { get; init; } = string.Empty;
        public bool RequiresAcknowledgement { get; init; }
    }

    public static class AIPromptSafetyGuard
    {
        private static readonly string[] AllowedKeywords =
        {
            "atividade", "atividades", "tarefa", "tarefas", "projeto", "projetos", "cronograma",
            "prazo", "sprint", "backlog", "entrega", "entregas", "recurso", "recursos", "equipe",
            "planejamento", "wbs", "marco", "marcos", "dependencia", "dependencias", "estimativa",
            "estimativas", "alocacao", "alocar", "distribuicao", "distribuir", "responsavel",
            "responsaveis", "openproj", "openproject"
        };

        private static readonly string[] BlockedKeywords =
        {
            "cpf", "cnpj", "rg", "passaporte", "cartao", "cartao de credito", "senha", "salario",
            "endereco", "telefone", "celular", "email pessoal", "e-mail pessoal", "nascimento",
            "prontuario", "saude", "doenca", "diagnostico", "biometria", "racial", "religiao",
            "orientacao sexual", "sindicato", "pix", "conta bancaria", "banco", "cliente final",
            "dados pessoais", "lgpd", "documento pessoal"
        };

        public static AIPromptValidationResult Validate(string prompt)
        {
            var normalized = Normalize(prompt);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return new AIPromptValidationResult
                {
                    Error = "Descreva um pedido de projeto antes de enviar para a IA."
                };
            }

            foreach (var keyword in BlockedKeywords)
            {
                if (normalized.Contains(keyword))
                {
                    return new AIPromptValidationResult
                    {
                        IsValid = true,
                        Warning = "Aviso LGPD: voce esta enviando termos potencialmente sensiveis para a IA. O responsavel pelas informacoes enviadas e o proprio usuario. Revise o texto antes de continuar.",
                        RequiresAcknowledgement = true
                    };
                }
            }

            var hasAllowedTopic = AllowedKeywords.Any(normalized.Contains);
            return new AIPromptValidationResult
            {
                IsValid = true,
                Warning = hasAllowedTopic
                    ? string.Empty
                    : "O pedido nao bateu com os termos esperados de projeto, mas sera enviado assim mesmo. Se precisar, descreva melhor as atividades, entregas ou recursos desejados."
            };
        }

        private static string Normalize(string value)
        {
            var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder();
            foreach (var c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    builder.Append(c);
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
