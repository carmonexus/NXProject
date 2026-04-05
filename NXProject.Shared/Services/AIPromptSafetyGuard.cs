using System.Globalization;
using System.Text;

namespace NXProject.Services
{
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

        public static bool TryValidate(string prompt, out string error, out string warning)
        {
            var normalized = Normalize(prompt);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = "Descreva um pedido de projeto antes de enviar para a IA.";
                warning = string.Empty;
                return false;
            }

            foreach (var keyword in BlockedKeywords)
            {
                if (normalized.Contains(keyword))
                {
                    error = "O pedido contem termos sensiveis ou dados pessoais que nao podem ser enviados. Limite o uso da IA a tarefas, cronograma, recursos e distribuicao de atividades.";
                    warning = string.Empty;
                    return false;
                }
            }

            var hasAllowedTopic = AllowedKeywords.Any(normalized.Contains);
            if (!hasAllowedTopic)
            {
                warning = "O pedido nao bateu com os termos esperados de projeto, mas sera enviado assim mesmo. Se precisar, descreva melhor as atividades, entregas ou recursos desejados.";
            }
            else
            {
                warning = string.Empty;
            }

            error = string.Empty;
            return true;
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
