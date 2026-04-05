using System;
using System.IO;
using System.Text.Json;
using NXProject.Models;

namespace NXProject.Services
{
    public static class SprintSettingsStore
    {
        public static SprintSettingsProfile Load(string storageKey = "NXProject.Community")
        {
            var settingsFile = GetSettingsFile(storageKey);
            if (!File.Exists(settingsFile))
                return new SprintSettingsProfile();

            try
            {
                var json = File.ReadAllText(settingsFile);
                var profile = JsonSerializer.Deserialize<SprintSettingsProfile>(json);
                return Normalize(profile);
            }
            catch
            {
                return new SprintSettingsProfile();
            }
        }

        public static void Save(SprintSettingsProfile profile, string storageKey = "NXProject.Community")
        {
            var settingsDirectory = GetSettingsDirectory(storageKey);
            Directory.CreateDirectory(settingsDirectory);
            var json = JsonSerializer.Serialize(Normalize(profile), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetSettingsFile(storageKey), json);
        }

        private static SprintSettingsProfile Normalize(SprintSettingsProfile? profile)
        {
            var normalized = profile ?? new SprintSettingsProfile();
            normalized.SprintDurationDays = Math.Max(1, normalized.SprintDurationDays);
            normalized.FirstSprintNumber = Math.Max(1, normalized.FirstSprintNumber);
            normalized.SprintNumberingMode = string.IsNullOrWhiteSpace(normalized.SprintNumberingMode)
                ? "Sequencial"
                : normalized.SprintNumberingMode.Trim();
            normalized.LowDaysPerSfp = Math.Max(0, normalized.LowDaysPerSfp);
            normalized.MediumDaysPerSfp = Math.Max(0, normalized.MediumDaysPerSfp);
            normalized.HighDaysPerSfp = Math.Max(0, normalized.HighDaysPerSfp);
            return normalized;
        }

        private static string GetSettingsDirectory(string storageKey)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                string.IsNullOrWhiteSpace(storageKey) ? "NXProject.Community" : storageKey.Trim());
        }

        private static string GetSettingsFile(string storageKey)
        {
            return Path.Combine(GetSettingsDirectory(storageKey), "sprint-settings.json");
        }
    }
}
