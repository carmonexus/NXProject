using System;

namespace NXProject.Community.Services
{
    /// <summary>
    /// Sinal global de "IA em execução": o Task Plan liga/desliga ao rodar Incluir/Consultar,
    /// e as janelas interessadas (cronograma) mostram um indicador animado no canto.
    /// </summary>
    public static class AiRunIndicator
    {
        public static event Action<bool>? Changed;

        public static bool IsRunning { get; private set; }

        /// <summary>Nome do provedor de IA que está rodando (para o indicador do cronograma).</summary>
        public static string ProviderLabel { get; private set; } = string.Empty;

        public static void Set(bool running, string? providerLabel = null)
        {
            if (running) ProviderLabel = providerLabel ?? string.Empty;
            if (IsRunning == running) return;
            IsRunning = running;
            Changed?.Invoke(running);
        }
    }
}
