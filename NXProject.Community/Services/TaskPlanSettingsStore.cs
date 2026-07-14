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
