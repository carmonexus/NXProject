// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NXProject.Services
{
    /// <summary>
    /// Histórico de duração das execuções de IA, por provedor. Guarda uma média
    /// móvel (EWMA) em segundos para servir de ETA do relógio de contagem regressiva.
    /// Combinado com a estimativa da própria IA quando ainda não há histórico.
    /// </summary>
    public static class AiRunStatsStore
    {
        // Público para o System.Text.Json (de)serializar sem depender de acesso a tipo aninhado privado.
        public sealed class Entry
        {
            public double AvgSeconds { get; set; }
            public double AvgBytes { get; set; }   // volume médio do payload enviado (contexto+prompt)
            public int Samples { get; set; }
        }

        private static string FileFor(string storageKey) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            string.IsNullOrWhiteSpace(storageKey) ? "NXProject.Community" : storageKey.Trim(),
            "ai-run-stats.json");

        private static Dictionary<string, Entry> Load(string storageKey)
        {
            try
            {
                var f = FileFor(storageKey);
                if (!File.Exists(f)) return new(StringComparer.OrdinalIgnoreCase);
                var d = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(f));
                return d != null ? new(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            }
            catch { return new(StringComparer.OrdinalIgnoreCase); }
        }

        // Chave por PROVEDOR + AÇÃO + CRONOGRAMA: o tempo varia com o tamanho do
        // cronograma (contexto enviado) e com a ação (analisar vs. gerar cronograma).
        private static string Key(string? provider, string? action, string? schedule)
        {
            static string N(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s.Trim().ToLowerInvariant();
            return $"{N(provider)}|{N(action)}|{N(schedule)}";
        }

        /// <summary>
        /// ETA em segundos com base no histórico, escalado pelo volume de bytes enviado
        /// (payloadBytes) em relação ao volume médio já observado. Null se não há amostras.
        /// </summary>
        public static int? EstimateSeconds(string storageKey, string? provider, string? action, string? schedule, long payloadBytes)
        {
            var map = Load(storageKey);
            if (!map.TryGetValue(Key(provider, action, schedule), out var e) || e.Samples <= 0 || e.AvgSeconds <= 0)
                return null;

            var eta = e.AvgSeconds;
            if (payloadBytes > 0 && e.AvgBytes > 0)
            {
                // Escala linear pelo tamanho do payload, limitada a 0,25x–4x para não estourar.
                var ratio = Math.Clamp(payloadBytes / e.AvgBytes, 0.25, 4.0);
                eta *= ratio;
            }
            return Math.Max(1, (int)Math.Round(eta));
        }

        /// <summary>Registra a duração real e o volume enviado (EWMA, peso 0,4 nas novas).</summary>
        public static void Record(string storageKey, string? provider, string? action, string? schedule, double seconds, long payloadBytes)
        {
            if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return;
            try
            {
                var map = Load(storageKey);
                var k = Key(provider, action, schedule);
                if (map.TryGetValue(k, out var e) && e.Samples > 0)
                {
                    e.AvgSeconds = e.AvgSeconds * 0.6 + seconds * 0.4;
                    if (payloadBytes > 0)
                        e.AvgBytes = e.AvgBytes > 0 ? e.AvgBytes * 0.6 + payloadBytes * 0.4 : payloadBytes;
                    e.Samples++;
                }
                else
                {
                    map[k] = new Entry { AvgSeconds = seconds, AvgBytes = Math.Max(0, payloadBytes), Samples = 1 };
                }
                var f = FileFor(storageKey);
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllText(f, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* estatística é best-effort, nunca quebra a execução */ }
        }
    }
}
