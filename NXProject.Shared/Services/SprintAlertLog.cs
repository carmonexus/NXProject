// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.IO;

namespace NXProject.Services
{
    /// <summary>
    /// Log de diagnóstico para o alerta de sprint.
    /// Ativado via config_nxproject.json: "DebugLogEnabled": true
    /// Arquivo gerado em %LocalAppData%\NXProject.Community\sprint_alert_debug.log
    /// </summary>
    public static class SprintAlertLog
    {
        public static bool Enabled { get; set; } = false;

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXProject.Community",
            "sprint_alert_debug.log");

        private static readonly object _lock = new();

        public static void Write(string taskName, string taskDay, string sprintDay, bool paint, string? path)
        {
            if (!Enabled) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                lock (_lock)
                {
                    File.AppendAllText(LogPath,
                        $"{DateTime.Now:HH:mm:ss.fff} | Paint={paint} | Finish={taskDay} | SprintEnd={sprintDay} | Path={path} | Task={taskName}{Environment.NewLine}");
                }
            }
            catch { }
        }

        public static void Clear()
        {
            if (!Enabled) return;
            try { File.Delete(LogPath); } catch { }
        }

        public static string LogFilePath => LogPath;
    }
}
