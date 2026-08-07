using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NXProject.Community.Services
{
    /// <summary>
    /// Uso da GPU NVIDIA via nvidia-smi.exe (instalado junto com o driver) — sem
    /// dependência nova. A consulta roda em background e o valor fica em cache:
    /// o monitor da IA Local pede a atualização num tick e lê o resultado no seguinte,
    /// sem travar a UI. Em máquinas sem NVIDIA (ou Vulkan/AMD/Intel), fica nulo.
    /// </summary>
    public static class NvidiaGpuProbe
    {
        private static string? _exePath;
        private static bool _resolved;
        private static volatile string? _last;
        private static volatile bool _running;

        /// <summary>Última leitura formatada ("GPU 87% · VRAM 2.100 MB") ou nulo sem leitura.</summary>
        public static string? Last => _last;

        /// <summary>Dispara uma leitura em background (no-op sem nvidia-smi ou com leitura em andamento).</summary>
        public static void RequestUpdate()
        {
            if (!_resolved)
            {
                _resolved = true;
                _exePath = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
                    @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
                }.FirstOrDefault(File.Exists);
            }
            if (_exePath == null || _running) return;

            _running = true;
            Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo(_exePath,
                        "--query-gpu=utilization.gpu,memory.used --format=csv,noheader,nounits")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var p = Process.Start(psi);
                    var line = p?.StandardOutput.ReadLine();
                    p?.WaitForExit(3000);
                    var parts = line?.Split(',');
                    _last = parts is { Length: >= 2 }
                        && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var util)
                        && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mem)
                        ? $"GPU {util}% · VRAM {mem:N0} MB"
                        : null;
                }
                catch { _last = null; }
                finally { _running = false; }
            });
        }
    }
}
