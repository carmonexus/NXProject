using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace NXProject.Community.Services
{
    /// <summary>
    /// Inferência da IA Local: carrega o modelo GGUF da pasta validada (Gerenciar IA Local)
    /// usando a llama.dll da mesma pasta e responde às ações do Assistente/Task Plan.
    /// Tudo roda na máquina — nenhum dado sai para a nuvem.
    /// </summary>
    public static class LocalLlamaService
    {
        // Uma inferência por vez (modelo em CPU); as demais aguardam.
        private static readonly SemaphoreSlim Gate = new(1, 1);

        private static LLamaWeights? _weights;
        private static ModelParams? _modelParams;
        private static string? _loadedModelPath;
        private static bool _nativeConfigured;

        /// <summary>Descarta o modelo carregado (ex.: troca de pasta/modelo). A DLL nativa permanece.</summary>
        public static void ResetModel()
        {
            _weights?.Dispose();
            _weights = null;
            _modelParams = null;
            _loadedModelPath = null;
        }

        /// <summary>
        /// Gera a resposta para (prompt de sistema, prompt do usuário). Registrado em
        /// <see cref="NXProject.Services.ProjectAIAssistantService.LocalGenerator"/> na inicialização.
        /// </summary>
        public static async Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            var folder = LocalAIResourceStore.LoadFolder();
            var check = LocalAIResourceStore.Validate(folder);
            if (!check.Ok)
                throw new InvalidOperationException(
                    "IA Local não está pronta: " + (check.NativeMessage ?? check.ModelMessage ?? "instalação incompleta")
                    + " Use o menu IA → Gerenciar IA Local.");

            await Gate.WaitAsync(ct);
            try
            {
                EnsureModelLoaded(folder, check.ModelFile!);
                return await Task.Run(() => InferAsync(systemPrompt, userPrompt, ct), ct);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static void EnsureModelLoaded(string folder, string modelFile)
        {
            var modelPath = Path.Combine(folder, modelFile);

            // A biblioteca nativa precisa ser apontada ANTES do primeiro uso do LLamaSharp
            // (uma vez por processo; trocar a DLL depois exige reiniciar o NX).
            if (!_nativeConfigured)
            {
                NativeLibraryConfig.All.WithLibrary(
                    Path.Combine(folder, LocalAIResourceStore.NativeDllName),
                    Path.Combine(folder, "mtmd.dll"));
                _nativeConfigured = true;
                // Silencia o log verboso do llama.cpp no console/saída padrão.
                NativeLogConfig.llama_log_set((_, _) => { });
            }

            if (_weights != null && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
                return;

            ResetModel();
            _modelParams = new ModelParams(modelPath)
            {
                ContextSize = 8192,
                GpuLayerCount = 0
            };
            _weights = LLamaWeights.LoadFromFile(_modelParams);
            _loadedModelPath = modelPath;
        }

        private static async Task<string> InferAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            // Formato ChatML (Qwen2.5 Instruct). Outros modelos GGUF instruct em ChatML
            // também funcionam; o anti-prompt encerra a resposta.
            var prompt = new StringBuilder()
                .Append("<|im_start|>system\n").Append(systemPrompt).Append("<|im_end|>\n")
                .Append("<|im_start|>user\n").Append(userPrompt).Append("<|im_end|>\n")
                .Append("<|im_start|>assistant\n")
                .ToString();

            var executor = new StatelessExecutor(_weights!, _modelParams!);
            var inference = new InferenceParams
            {
                MaxTokens = 2048,
                AntiPrompts = new[] { "<|im_end|>", "<|im_start|>" }.ToList(),
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f }
            };

            var sb = new StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inference, ct))
                sb.Append(token);

            var text = sb.ToString();
            foreach (var stop in new[] { "<|im_end|>", "<|im_start|>" })
            {
                var i = text.IndexOf(stop, StringComparison.Ordinal);
                if (i >= 0) text = text[..i];
            }
            return text.Trim();
        }
    }
}
