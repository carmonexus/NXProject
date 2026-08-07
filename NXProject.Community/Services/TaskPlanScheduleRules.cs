using System;
using System.Globalization;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Community.Services
{
    /// <summary>
    /// Regras do Task Plan para aplicar tasks ao cronograma (sem IA):
    /// padrão de criação interna (igual ao AddSubtask) e ajuste de duração da Story.
    /// </summary>
    public static class TaskPlanScheduleRules
    {
        /// <summary>Story em New (ou com 0% de conclusão) pode ter a duração ajustada pelas tasks.</summary>
        public static bool CanAdjustStoryDuration(ProjectTask story)
            => string.Equals(story.TfsState?.Trim(), "New", StringComparison.OrdinalIgnoreCase)
               || story.PercentComplete <= 0;

        /// <summary>
        /// Estimado: aceita horas ("8", "6,5", "4h", "4 horas") ou dias ("2d", "2 dias"),
        /// convertidos pelo calendário do cronograma (mesma regra da digitação de duração
        /// na grade). O sufixo de hora é descartado; o de dia converte pelo expediente.
        /// </summary>
        public static double? ParseEstimatedHours(string? text)
        {
            var v = text?.Trim();
            if (string.IsNullOrEmpty(v)) return null;

            // Sufixos de unidade (a IA e o texto de reunião citam "4h", "3 horas", "2 dias").
            var m = System.Text.RegularExpressions.Regex.Match(
                v, @"^(?<num>.+?)\s*(?<unit>h|hs|hora|horas|d|dia|dias)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool days = m.Groups["unit"].Value.StartsWith("d", StringComparison.OrdinalIgnoreCase);
            if (m.Groups["unit"].Success) v = m.Groups["num"].Value.Trim();

            if (!double.TryParse(v, NumberStyles.Any, CultureInfo.CurrentCulture, out var num)
                && !double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out num))
                return null;
            if (num <= 0) return null;

            return days ? num * ProjectCalendarService.WorkingHoursPerDay : num;
        }

        /// <summary>
        /// Extrai o array JSON da resposta da IA, RECUPERANDO resposta truncada pelo teto
        /// de tokens: sem o "]" final, corta no fim do último objeto completo ("}") e fecha
        /// o array — os itens completos são aproveitados em vez de descartar tudo.
        /// Devolve (json, truncado); json nulo quando não há array recuperável.
        /// </summary>
        public static (string? Json, bool Truncated) ExtractJsonArray(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (null, false);
            var start = raw.IndexOf('[');
            if (start < 0) return (null, false);

            var end = raw.LastIndexOf(']');
            if (end > start)
            {
                var slice = raw[start..(end + 1)];
                try { using var _ = System.Text.Json.JsonDocument.Parse(slice); return (slice, false); }
                catch { /* array malformado: tenta o reparo abaixo */ }
            }

            var lastObj = raw.LastIndexOf('}');
            if (lastObj > start)
            {
                var slice = raw[start..(lastObj + 1)].TrimEnd().TrimEnd(',') + "]";
                try { using var _ = System.Text.Json.JsonDocument.Parse(slice); return (slice, true); }
                catch { /* irrecuperável */ }
            }
            return (null, false);
        }

        /// <summary>
        /// Lê uma propriedade string do item JSON da IA aceitando nomes alternativos —
        /// o prompt pede chaves sem acento ("esforco"), mas os modelos às vezes devolvem
        /// acentuado ("esforço"); sem o fallback o valor era perdido silenciosamente.
        /// </summary>
        public static string? GetJsonString(System.Text.Json.JsonElement item, params string[] names)
        {
            foreach (var name in names)
                if (item.TryGetProperty(name, out var p)
                    && p.ValueKind == System.Text.Json.JsonValueKind.String)
                    return p.GetString();
            return null;
        }

        /// <summary>
        /// Cria a Task interna sob a Story no mesmo padrão do AddSubtask do cronograma:
        /// TfsId=0 ("criar no TFS" → DisplayId "{Id}:I"), estado New, herdando sprint/iteração.
        /// Se a Story não puder ter a duração ajustada, o fim fica contido no período dela.
        /// </summary>
        public static ProjectTask CreateInternalTask(ProjectTask story, int id, string name, string? description, double hours)
        {
            var start = story.Start;
            var calcFinish = ProjectCalendarService.AddWorkingHours(start, hours);
            return new ProjectTask
            {
                Id = id,
                Name = name,
                Level = story.Level + 1,
                TfsType = "Task",
                TfsId = 0,
                TfsState = "New",
                TfsIterationPath = story.TfsIterationPath,
                SprintNumber = story.SprintNumber,
                Parent = story,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                EstimatedHours = hours,
                Start = start,
                Finish = CanAdjustStoryDuration(story) || calcFinish <= story.Finish
                    ? calcFinish
                    : story.Finish
            };
        }
    }
}
