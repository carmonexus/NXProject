using System;
using System.IO;
using System.Text.Json;

namespace NXProject.Community.Services
{
    /// <summary>Configurações da tela Task Plan (pasta padrão, último arquivo, SharePoint).</summary>
    public sealed class TaskPlanSettings
    {
        public string DefaultFolder { get; set; } = string.Empty;
        public string LastFile { get; set; } = string.Empty;

        /// <summary>Planilha de Task Plan associada a cada projeto (nome do cronograma → caminho
        /// do .xlsx). Evita abrir a planilha do cronograma anterior ao trocar de projeto.</summary>
        public System.Collections.Generic.Dictionary<string, string> ProjectFiles { get; set; } = new();

        /// <summary>URL/pasta do SharePoint por projeto (o service principal — tenant/client — é global).</summary>
        public System.Collections.Generic.Dictionary<string, string> ProjectSharePointUrls { get; set; } = new();

        public string? GetProjectFile(string? projectName) => GetFor(ProjectFiles, projectName);
        public void SetProjectFile(string projectName, string path) => SetFor(ProjectFiles, projectName, path);

        public string? GetProjectSharePointUrl(string? projectName) => GetFor(ProjectSharePointUrls, projectName);
        public void SetProjectSharePointUrl(string projectName, string url) => SetFor(ProjectSharePointUrls, projectName, url);

        private static string? GetFor(System.Collections.Generic.Dictionary<string, string> map, string? projectName)
        {
            var key = projectName?.Trim();
            if (string.IsNullOrEmpty(key)) return null;
            foreach (var kv in map)
                if (string.Equals(kv.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrWhiteSpace(kv.Value) ? null : kv.Value;
            return null;
        }

        private static void SetFor(System.Collections.Generic.Dictionary<string, string> map, string projectName, string value)
        {
            var key = projectName.Trim();
            foreach (var existing in map.Keys)
                if (string.Equals(existing?.Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    map[existing!] = value;
                    return;
                }
            map[key] = value;
        }

        // SharePoint (Entra ID + Graph) — campos preparados; integração futura.
        public string SharePointTenantId { get; set; } = string.Empty;
        public string SharePointClientId { get; set; } = string.Empty;
        public string SharePointUrl { get; set; } = string.Empty;
    }

    public static class TaskPlanSettingsStore
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXProject", "taskplan-settings.json");

        public static TaskPlanSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<TaskPlanSettings>(File.ReadAllText(FilePath)) ?? new TaskPlanSettings();
            }
            catch { /* volta ao padrão */ }
            return new TaskPlanSettings();
        }

        public static void Save(TaskPlanSettings settings)
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
