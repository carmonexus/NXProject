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

        /// <summary>
        /// Situação atual da inferência para a UI (fase + tokens + velocidade),
        /// atualizada durante a geração. Nulo quando não há inferência em andamento.
        /// </summary>
        public static string? CurrentStatus => _status;
        private static volatile string? _status;

        /// <summary>Threads de inferência configuradas no motor (núcleos físicos); 0 = modelo ainda não carregado.</summary>
        public static int ConfiguredThreads { get; private set; }

        /// <summary>Backend em uso na inferência ("CPU", "GPU CUDA 12", "GPU Vulkan"); nulo antes da carga.</summary>
        public static string? ActiveBackendLabel { get; private set; }

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
                _status = "aguardando/carregando modelo (primeira vez pode levar ~1 min)";
                EnsureModelLoaded(folder, check.ModelFile!);
                return await Task.Run(() => InferAsync(systemPrompt, userPrompt, ct), ct);
            }
            finally
            {
                _status = null;
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
            // Backend registrado na pasta (radio do Gerenciador): GPU descarrega TODAS as
            // camadas do modelo para a placa (999 = "todas"); CPU mantém tudo na memória.
            // Sem registro (instalação manual/antiga), assume CPU.
            var kind = LocalAIResourceStore.GetInstalledBackendKind(folder) ?? LocalAIResourceStore.BackendKind.Cpu;
            ActiveBackendLabel = LocalAIResourceStore.BackendDisplayName(kind);
            _modelParams = new ModelParams(modelPath)
            {
                ContextSize = 8192,
                GpuLayerCount = kind == LocalAIResourceStore.BackendKind.Cpu ? 0 : 999,
                // llama.cpp rende melhor com os núcleos FÍSICOS (metade dos lógicos com HT).
                Threads = Math.Max(1, Environment.ProcessorCount / 2),
                BatchSize = 512
            };
            ConfiguredThreads = Math.Max(1, Environment.ProcessorCount / 2);
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
                // Teto de saída: um loop do modelo não pode custar minutos. 2048 comporta
                // listas de reunião grandes (1024 truncava o JSON de ~13 tasks em produção);
                // resposta ainda truncada é recuperada pelo reparo de JSON no Task Plan.
                MaxTokens = 2048,
                AntiPrompts = new[] { "<|im_end|>", "<|im_start|>" }.ToList(),
                // Extração estruturada pede decodificação DETERMINÍSTICA (greedy): mesma
                // entrada → mesma saída, e menos loops/deriva que amostragem com temperatura.
                SamplingPipeline = new GreedySamplingPipeline()
            };

            // O primeiro token só sai depois do modelo "ler" todo o contexto (prompt eval) —
            // é a fase mais longa quando o cronograma tem muitas Stories.
            _status = "processando o contexto (ainda sem gerar resposta)";
            var sb = new StringBuilder();
            var tokens = 0;
            var start = DateTime.UtcNow;
            double prefillSecs = 0;
            var genStart = start;
            await foreach (var token in executor.InferAsync(prompt, inference, ct))
            {
                if (tokens == 0)
                {
                    // Primeiro token: o prefill (leitura do contexto) terminou aqui.
                    prefillSecs = (DateTime.UtcNow - start).TotalSeconds;
                    genStart = DateTime.UtcNow;
                }
                sb.Append(token);
                tokens++;
                var secs = (DateTime.UtcNow - genStart).TotalSeconds;
                _status = $"contexto lido em {prefillSecs:0}s · gerando resposta: {tokens} token(s)"
                          + (secs > 1 ? $" ({tokens / secs:0.0} tok/s)" : "");
            }

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
