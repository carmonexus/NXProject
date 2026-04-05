using System.Collections.Generic;
using System.IO;
using System.Text;
using NXProject.Models;

namespace NXProject.Services
{
    public static class CsvService
    {
        public static void Export(List<ProjectTask> tasks, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ID;Nome;Nível;Início;Fim;Duração(d);%Completo;Predecessoras;Recursos;Sprint;Horas Est.");

            foreach (var t in tasks)
            {
                var preds = string.Join(",", t.PredecessorIds);
                var resources = string.Join(" | ", t.Resources.ConvertAll(r => r.ToString()));
                sb.AppendLine(string.Join(";",
                    t.Id,
                    Quote(t.Name),
                    t.Level,
                    t.Start.ToString("dd/MM/yyyy"),
                    t.Finish.ToString("dd/MM/yyyy"),
                    (int)(t.Finish - t.Start).TotalDays,
                    t.PercentComplete.ToString("0"),
                    preds,
                    Quote(resources),
                    t.SprintNumber,
                    t.EstimatedHours?.ToString("0.0") ?? ""
                ));
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        private static string Quote(string value)
            => value.Contains(';') || value.Contains('"') || value.Contains('\n')
               ? $"\"{value.Replace("\"", "\"\"")}\""
               : value;
    }
}
