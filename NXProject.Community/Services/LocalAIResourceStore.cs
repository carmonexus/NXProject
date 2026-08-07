using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NXProject.Community.Services
{
    /// <summary>
    /// Recursos da IA Local (LLaMA): pasta escolhida pelo usuário com a biblioteca nativa
    /// (backend CPU do LlamaSharp, baixado do NuGet) e o modelo GGUF (Hugging Face).
    /// Nada é instalado no sistema — são só arquivos na pasta, com validação local
    /// (existência, assinatura GGUF e teste de carga da DLL), pensado para ambientes
    /// corporativos onde instalação de .exe é bloqueada. Se o download também for
    /// bloqueado, os arquivos podem ser copiados manualmente para a pasta.
    /// </summary>
    public static class LocalAIResourceStore
    {
        // Backend do LlamaSharp — o .nupkg é um zip com as DLLs nativas em runtimes/win-x64/native.
        // BackendVersion = versão HOMOLOGADA pelo NXProject (testada com o app). O wrapper
        // gerenciado (LLamaSharp.dll, no Setup) é o MESMO para todos os backends — CPU ou GPU
        // muda só o pacote de DLLs nativas baixado para a pasta de recursos.
        public const string BackendVersion = "0.27.0";
        public const string NativeDllName = "llama.dll";
        private const string BackendVersionFile = "backend-version.txt";
        private const string BackendKindFile = "backend-kind.txt";

        /// <summary>Tipo de processamento da IA Local (define o pacote de DLLs nativas).</summary>
        public enum BackendKind { Cpu, Cuda12, Vulkan }

        /// <summary>Id do pacote NuGet com as DLLs nativas win-x64 do backend.</summary>
        public static string BackendPackageId(BackendKind kind) => kind switch
        {
            BackendKind.Cuda12 => "LLamaSharp.Backend.Cuda12.Windows",
            BackendKind.Vulkan => "LLamaSharp.Backend.Vulkan.Windows",
            _ => "LLamaSharp.Backend.Cpu",
        };

        /// <summary>Nome curto do backend para logs e status.</summary>
        public static string BackendDisplayName(BackendKind kind) => kind switch
        {
            BackendKind.Cuda12 => "GPU CUDA 12",
            BackendKind.Vulkan => "GPU Vulkan",
            _ => "CPU",
        };

        public static string BackendNupkgUrl(BackendKind kind, string? version = null)
            => $"https://www.nuget.org/api/v2/package/{BackendPackageId(kind)}/{(string.IsNullOrWhiteSpace(version) ? BackendVersion : version)}";

        private static string BackendVersionsIndexUrl(BackendKind kind)
            => $"https://api.nuget.org/v3-flatcontainer/{BackendPackageId(kind).ToLowerInvariant()}/index.json";

        // Modelo padrão: Qwen2.5 3B Instruct quantizado (~2 GB), bom custo/benefício em CPU.
        public const string ModelFileName = "Qwen2.5-3B-Instruct-Q4_K_M.gguf";
        public const string ModelUrl = "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/" + ModelFileName;

        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        // ── Configuração (pasta dos recursos) ────────────────────────────────
        private sealed class StoredSettings
        {
            public string ResourcesFolder { get; set; } = string.Empty;
            public string Backend { get; set; } = string.Empty; // "Cpu" | "Cuda12" | "Vulkan"
            public int MaxResponseTokens { get; set; } = DefaultMaxResponseTokens;
        }

        // Teto de tokens da RESPOSTA da IA Local (configurável no Gerenciador). O contexto
        // total do modelo é 8192 (ContextSize), dividido entre prompt e resposta — por isso
        // o máximo fica em 6144, sobrando espaço para o prompt.
        public const int DefaultMaxResponseTokens = 2048;
        public const int MinResponseTokens = 256;
        public const int MaxResponseTokensLimit = 6144;

        public static int LoadMaxResponseTokens()
            => Math.Clamp(LoadSettings().MaxResponseTokens is var t && t > 0 ? t : DefaultMaxResponseTokens,
                MinResponseTokens, MaxResponseTokensLimit);

        public static void SaveMaxResponseTokens(int tokens)
        {
            var s = LoadSettings();
            s.MaxResponseTokens = Math.Clamp(tokens, MinResponseTokens, MaxResponseTokensLimit);
            SaveSettings(s);
        }

        private static string SettingsFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXProject.Community", "local-ai.json");

        private static StoredSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                    return JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsFile)) ?? new StoredSettings();
            }
            catch { /* volta ao default */ }
            return new StoredSettings();
        }

        private static void SaveSettings(StoredSettings s)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(s,
                new JsonSerializerOptions { WriteIndented = true }));
        }

        public static string LoadFolder() => LoadSettings().ResourcesFolder;

        public static void SaveFolder(string folder)
        {
            var s = LoadSettings();
            s.ResourcesFolder = folder?.Trim() ?? "";
            SaveSettings(s);
        }

        /// <summary>Backend escolhido pelo usuário (CPU quando nunca escolheu — compatível com instalações antigas).</summary>
        public static BackendKind LoadBackendKind()
            => Enum.TryParse<BackendKind>(LoadSettings().Backend, ignoreCase: true, out var k) ? k : BackendKind.Cpu;

        public static void SaveBackendKind(BackendKind kind)
        {
            var s = LoadSettings();
            s.Backend = kind.ToString();
            SaveSettings(s);
        }

        // ── Validação da instalação ──────────────────────────────────────────
        public sealed class ValidationResult
        {
            public bool NativePresent;
            public bool NativeLoads;
            public string? NativeMessage;
            public bool ModelPresent;
            public bool ModelValid;
            public string? ModelFile;
            public string? ModelMessage;
            public bool Ok => NativePresent && NativeLoads && ModelPresent && ModelValid;
        }

        /// <summary>
        /// Valida a pasta: llama.dll presente e carregável (LoadLibrary — detecta bloqueio
        /// de política/AppLocker) e um .gguf com a assinatura "GGUF" nos 4 primeiros bytes.
        /// </summary>
        public static ValidationResult Validate(string folder)
        {
            var r = new ValidationResult();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                r.NativeMessage = r.ModelMessage = "Pasta não encontrada.";
                return r;
            }

            var dll = Path.Combine(folder, NativeDllName);
            r.NativePresent = File.Exists(dll);
            if (!r.NativePresent)
                r.NativeMessage = $"{NativeDllName} não encontrada na pasta.";
            else
            {
                // LOAD_WITH_ALTERED_SEARCH_PATH: as dependências (ggml*.dll) são procuradas
                // na pasta da própria llama.dll — o LoadLibrary padrão só olha exe/sistema/PATH.
                // A DLL fica carregada no processo (nunca FreeLibrary: o descarregamento do
                // ggml pode derrubar o processo, e a inferência vai reaproveitar a carga).
                if (_loadedNativePath != null && string.Equals(_loadedNativePath, dll, StringComparison.OrdinalIgnoreCase))
                    r.NativeLoads = true;
                else
                {
                    var handle = LoadLibraryExW(dll, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                    if (handle != IntPtr.Zero)
                    {
                        r.NativeLoads = true;
                        _loadedNativePath = dll;
                    }
                    else
                    {
                        var err = Marshal.GetLastWin32Error();
                        r.NativeMessage = err == 1260 // ERROR_ACCESS_DISABLED_BY_POLICY
                            ? "A política de segurança da máquina bloqueou o carregamento da DLL (AppLocker). Peça à TI para liberar a pasta."
                            : $"Falha ao carregar {NativeDllName} (erro Win32 {err}). Verifique se todas as DLLs do backend estão na pasta.";
                    }
                }
            }

            var gguf = Directory.EnumerateFiles(folder, "*.gguf").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            r.ModelPresent = gguf != null;
            if (gguf == null)
                r.ModelMessage = "Nenhum arquivo .gguf (modelo) encontrado na pasta.";
            else
            {
                r.ModelFile = Path.GetFileName(gguf);
                try
                {
                    Span<byte> magic = stackalloc byte[4];
                    using var fs = File.OpenRead(gguf);
                    r.ModelValid = fs.Read(magic) == 4
                        && magic[0] == (byte)'G' && magic[1] == (byte)'G' && magic[2] == (byte)'U' && magic[3] == (byte)'F';
                    if (!r.ModelValid)
                        r.ModelMessage = $"{r.ModelFile} não tem a assinatura GGUF — download incompleto ou arquivo errado.";
                }
                catch (Exception ex)
                {
                    r.ModelMessage = $"Não foi possível ler {r.ModelFile}: {ex.Message}";
                }
            }

            return r;
        }

        // ── Download dos recursos ────────────────────────────────────────────
        /// <summary>Versão do backend registrada na pasta (gravada pelo download do NX; instalação manual fica sem registro).</summary>
        public static string? GetInstalledBackendVersion(string folder)
        {
            try
            {
                var file = Path.Combine(folder, BackendVersionFile);
                return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// A máquina suporta o backend escolhido? Checagem por DISPOSITIVO REAL, não só
        /// pela DLL: vulkan-1.dll (loader) existe em quase todo Windows mesmo sem GPU
        /// utilizável, então CUDA pergunta ao driver (cuInit + cuDeviceGetCount) e Vulkan
        /// enumera os dispositivos físicos (vkEnumeratePhysicalDevices) — sem GPU, a DLL
        /// falta (DllNotFoundException) ou a contagem vem zero. CPU sempre suporta.
        /// </summary>
        public static bool IsBackendSupported(BackendKind kind)
        {
            switch (kind)
            {
                case BackendKind.Cuda12:
                    try { return cuInit(0) == 0 && cuDeviceGetCount(out var n) == 0 && n > 0; }
                    catch { return false; } // sem nvcuda.dll = sem driver NVIDIA
                case BackendKind.Vulkan:
                    try
                    {
                        var info = new VkInstanceCreateInfo { sType = 1 }; // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
                        if (vkCreateInstance(ref info, IntPtr.Zero, out var instance) != 0)
                            return false;
                        uint count = 0;
                        var ok = vkEnumeratePhysicalDevices(instance, ref count, IntPtr.Zero) == 0 && count > 0;
                        vkDestroyInstance(instance, IntPtr.Zero);
                        return ok;
                    }
                    catch { return false; } // sem vulkan-1.dll ou loader quebrado
                default:
                    return true;
            }
        }

        [DllImport("nvcuda.dll", ExactSpelling = true)]
        private static extern int cuInit(uint flags);

        [DllImport("nvcuda.dll", ExactSpelling = true)]
        private static extern int cuDeviceGetCount(out int count);

        [StructLayout(LayoutKind.Sequential)]
        private struct VkInstanceCreateInfo
        {
            public int sType;
            public IntPtr pNext;
            public uint flags;
            public IntPtr pApplicationInfo;
            public uint enabledLayerCount;
            public IntPtr ppEnabledLayerNames;
            public uint enabledExtensionCount;
            public IntPtr ppEnabledExtensionNames;
        }

        [DllImport("vulkan-1.dll", ExactSpelling = true)]
        private static extern int vkCreateInstance(ref VkInstanceCreateInfo createInfo, IntPtr allocator, out IntPtr instance);

        [DllImport("vulkan-1.dll", ExactSpelling = true)]
        private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr devices);

        [DllImport("vulkan-1.dll", ExactSpelling = true)]
        private static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);

        /// <summary>Backend registrado na pasta pelo download do NX (null em instalação manual/antiga).</summary>
        public static BackendKind? GetInstalledBackendKind(string folder)
        {
            try
            {
                var file = Path.Combine(folder, BackendKindFile);
                return File.Exists(file) && Enum.TryParse<BackendKind>(File.ReadAllText(file).Trim(), ignoreCase: true, out var k)
                    ? k : null;
            }
            catch { return null; }
        }

        /// <summary>Última versão estável do backend publicada no NuGet.</summary>
        public static async Task<string?> GetLatestBackendVersionAsync(CancellationToken ct, BackendKind kind = BackendKind.Cpu)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var json = await Http.GetStringAsync(BackendVersionsIndexUrl(kind), cts.Token);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("versions").EnumerateArray()
                .Select(v => v.GetString() ?? "")
                .Where(v => v.Length > 0 && !v.Contains('-')) // ignora pré-releases
                .LastOrDefault();
        }

        /// <summary>Compara versões "a.b.c" (true quando <paramref name="a"/> é mais nova que <paramref name="b"/>).</summary>
        public static bool IsNewerVersion(string? a, string? b)
            => System.Version.TryParse(a, out var va) && System.Version.TryParse(b, out var vb) && va > vb;

        /// <summary>A llama.dll já foi carregada neste processo (arquivos ficam travados até fechar o NX).</summary>
        public static bool IsNativeLoaded => _loadedNativePath != null;

        /// <summary>
        /// Baixa o .nupkg do backend (CPU/CUDA/Vulkan) e extrai as DLLs nativas para a pasta.
        /// Sem <paramref name="version"/>, usa a versão homologada pelo NX. A subpasta preferida
        /// dentro de runtimes/win-x64/native depende do backend (avx2 | cuda12 | vulkan).
        /// </summary>
        public static async Task DownloadBackendAsync(string folder, IProgress<string> status, CancellationToken ct,
            string? version = null, BackendKind kind = BackendKind.Cpu)
        {
            version = string.IsNullOrWhiteSpace(version) ? BackendVersion : version.Trim();
            Directory.CreateDirectory(folder);
            status.Report($"Baixando backend {BackendDisplayName(kind)} (LLamaSharp {version}) do NuGet...");
            var tmp = Path.Combine(folder, "_backend.nupkg.tmp");
            await DownloadFileAsync(BackendNupkgUrl(kind, version), tmp, null, status, ct);

            status.Report("Extraindo DLLs nativas...");
            using (var zip = ZipFile.OpenRead(tmp))
            {
                var native = zip.Entries
                    .Where(e => e.FullName.Replace('\\', '/').Contains("runtimes/win-x64/native/", StringComparison.OrdinalIgnoreCase)
                                && e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (native.Count == 0)
                    throw new InvalidOperationException("O pacote do backend não contém DLLs nativas win-x64.");

                // Preferência por backend: CPU → variante AVX2; CUDA → cuda12; Vulkan → vulkan.
                // As DLLs na raiz do native entram como base e a subpasta preferida sobrepõe.
                var preferredToken = kind switch
                {
                    BackendKind.Cuda12 => "/cuda12/",
                    BackendKind.Vulkan => "/vulkan/",
                    _ => "/avx2/",
                };
                var preferred = native.Where(e => e.FullName.Replace('\\', '/').Contains(preferredToken, StringComparison.OrdinalIgnoreCase)).ToList();
                var flat = native.Where(e =>
                {
                    var p = e.FullName.Replace('\\', '/');
                    var idx = p.IndexOf("native/", StringComparison.OrdinalIgnoreCase) + "native/".Length;
                    return !p[idx..].Contains('/');
                }).ToList();

                var chosen = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in flat) chosen[e.Name] = e;
                foreach (var e in preferred) chosen[e.Name] = e; // subpasta preferida sobrepõe a raiz
                // Pacote sem raiz nem a subpasta esperada (layout novo): pega tudo, dedup por nome.
                if (chosen.Count == 0)
                    foreach (var e in native) chosen[e.Name] = e;
                foreach (var e in chosen.Values)
                {
                    ct.ThrowIfCancellationRequested();
                    e.ExtractToFile(Path.Combine(folder, e.Name), overwrite: true);
                }
                status.Report($"Backend extraído: {chosen.Count} DLL(s).");
            }
            File.Delete(tmp);
            File.WriteAllText(Path.Combine(folder, BackendVersionFile), version);
            File.WriteAllText(Path.Combine(folder, BackendKindFile), kind.ToString());
        }

        /// <summary>Baixa o modelo GGUF (~2 GB) para a pasta, com progresso por MB.</summary>
        public static async Task DownloadModelAsync(string folder, IProgress<string> status, IProgress<double>? percent, CancellationToken ct)
        {
            Directory.CreateDirectory(folder);
            status.Report($"Baixando modelo {ModelFileName} (~2 GB) do Hugging Face...");
            var target = Path.Combine(folder, ModelFileName);
            var tmp = target + ".tmp";
            await DownloadFileAsync(ModelUrl, tmp, percent, status, ct);
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            status.Report("Modelo baixado.");
        }

        private static async Task DownloadFileAsync(string url, string destination, IProgress<double>? percent, IProgress<string> status, CancellationToken ct)
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var dest = File.Create(destination);
            var buffer = new byte[1024 * 1024];
            long read = 0;
            int n;
            var lastLoggedMb = -1L;
            const long logStepMb = 25; // uma linha de log a cada 25 MB (a barra segue por MB)
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                var mb = read / (1024 * 1024);
                if (total is > 0)
                    percent?.Report(read * 100.0 / total.Value);
                if (mb / logStepMb != lastLoggedMb)
                {
                    lastLoggedMb = mb / logStepMb;
                    status.Report(total is > 0
                        ? $"Baixando... {mb} MB de {total.Value / (1024 * 1024)} MB"
                        : $"Baixando... {mb} MB");
                }
            }
        }

        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        // Caminho da llama.dll já carregada neste processo (fica carregada até o app fechar).
        private static string? _loadedNativePath;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);
    }
}
