// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Threading;
using System.Threading.Tasks;

namespace NXProject.Services
{
    /// <summary>
    /// Faixa de prioridade da Task (Microsoft.VSTS.Common.Priority). Centraliza a configuração
    /// usada tanto no <b>import do projeto</b> (para "clampar" o valor gravado) quanto na tela do
    /// <b>TaskBoard</b> (para ciclar o chip dentro da faixa válida).
    ///
    /// O campo Priority é Integer com "limitedToValues" — a API NÃO lista os valores permitidos.
    /// A faixa é resolvida a partir de: config global do NX (Configurar DevOps → TaskPriorityMin/Max)
    /// e, quando possível, do MÁXIMO descoberto no template via validateOnly
    /// (<see cref="TfsImportService.DiscoverTaskPriorityMaxAsync"/>). Padrão 1–9 (o do formulário do DevOps).
    /// </summary>
    public sealed class TaskPriorityRange
    {
        public int Min { get; }
        public int Max { get; }

        public TaskPriorityRange(int min, int max)
        {
            Min = Math.Max(1, min);
            Max = Math.Max(Min, max);
        }

        /// <summary>Ajusta um valor para dentro da faixa.</summary>
        public int Clamp(int priority) => Math.Clamp(priority, Min, Max);

        /// <summary>Verdadeiro se o valor está na faixa.</summary>
        public bool Contains(int priority) => priority >= Min && priority <= Max;

        /// <summary>Próximo valor ao ciclar (usado ao clicar no chip): min..max e volta ao min.</summary>
        public int Next(int current) => current < Min ? Min : current >= Max ? Min : current + 1;

        /// <summary>
        /// Resolve a faixa pela configuração do NX. Se <paramref name="discoveredMax"/> &gt; 0
        /// (descoberto no template), ele manda no máximo. Padrão 1–9 quando a faixa não está
        /// habilitada em Configurar DevOps.
        /// </summary>
        public static TaskPriorityRange FromOptions(TfsConnectionOptions? options, int discoveredMax = 0)
        {
            int min = options is { TaskPriorityRangeEnabled: true } ? Math.Max(1, options.TaskPriorityMin) : 1;
            int max = options is { TaskPriorityRangeEnabled: true } ? Math.Max(min, options.TaskPriorityMax) : 9;
            if (discoveredMax > 0) max = discoveredMax;
            return new TaskPriorityRange(min, max);
        }

        /// <summary>
        /// Descobre a faixa consultando o TFS (validateOnly numa Task de amostra) e combina com a
        /// config. Best-effort: se não conseguir descobrir, cai na config/padrão.
        /// </summary>
        public static async Task<TaskPriorityRange> DiscoverAsync(
            TfsConnectionOptions options, int sampleTaskId, CancellationToken ct = default)
        {
            int discovered = 0;
            try { discovered = await TfsImportService.DiscoverTaskPriorityMaxAsync(options, sampleTaskId, ct); }
            catch { /* best-effort: cai na config */ }
            return FromOptions(options, discovered);
        }
    }
}
