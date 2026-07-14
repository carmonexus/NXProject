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
        /// Estimado: aceita horas ("8", "6,5") ou dias ("2d"), convertidos pelo calendário
        /// do cronograma (mesma regra da digitação de duração na grade do cronograma).
        /// </summary>
        public static double? ParseEstimatedHours(string? text)
        {
            var v = text?.Trim();
            if (string.IsNullOrEmpty(v)) return null;

            bool days = v.EndsWith("d", StringComparison.OrdinalIgnoreCase);
            if (days) v = v[..^1].Trim();

            if (!double.TryParse(v, NumberStyles.Any, CultureInfo.CurrentCulture, out var num)
                && !double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out num))
                return null;
            if (num <= 0) return null;

            return days ? num * ProjectCalendarService.WorkingHoursPerDay : num;
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
