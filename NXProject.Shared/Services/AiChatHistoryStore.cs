// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NXProject.Services
{
    /// <summary>
    /// Histórico persistido das conversas do chat de IA, POR CRONOGRAMA (Work Item Project).
    /// A chave do projeto é o CÓDIGO do TFS quando existe (renomear no TFS não perde o histórico);
    /// só usa o nome quando é ID interno; projeto novo/sem definição cai em "NXProject".
    /// Mantém as últimas N conversas (N configurável no Assistente; 0 = infinito).
    /// </summary>
    public static class AiChatHistoryStore
    {
        public sealed class StoredMessage
        {
            public string Role { get; set; } = string.Empty;
            public string Text { get; set; } = string.Empty;
            public DateTime? Time { get; set; }        // quando a mensagem chegou
            public double? DurationSec { get; set; }   // tempo de resposta da IA (segundos)
        }

        public sealed class StoredConversation
        {
            public string Title { get; set; } = string.Empty;
            public List<StoredMessage> Messages { get; set; } = new();
            // "Compress": resumo das mensagens antigas e até qual índice elas já foram resumidas.
            public string Summary { get; set; } = string.Empty;
            public int SummarizedFrom { get; set; }
        }

        private static string FileFor(string storageKey) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            string.IsNullOrWhiteSpace(storageKey) ? "NXProject.Community" : storageKey.Trim(),
            "ai-chat-history.json");

        private static Dictionary<string, List<StoredConversation>> LoadAll(string storageKey)
        {
            try
            {
                var f = FileFor(storageKey);
                if (!File.Exists(f)) return new(StringComparer.OrdinalIgnoreCase);
                var d = JsonSerializer.Deserialize<Dictionary<string, List<StoredConversation>>>(File.ReadAllText(f));
                return d != null ? new(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            }
            catch { return new(StringComparer.OrdinalIgnoreCase); }
        }

        /// <summary>Conversas guardadas para o cronograma (mais recentes primeiro).</summary>
        public static List<StoredConversation> Load(string storageKey, string projectKey)
        {
            var all = LoadAll(storageKey);
            return all.TryGetValue(Norm(projectKey), out var list) ? list : new List<StoredConversation>();
        }

        /// <summary>
        /// Grava as conversas do cronograma, aplicando o limite (0 = infinito, não corta).
        /// A lista recebida deve vir com as MAIS RECENTES PRIMEIRO.
        /// </summary>
        public static void Save(string storageKey, string projectKey, IEnumerable<StoredConversation> conversations, int limit)
        {
            try
            {
                var all = LoadAll(storageKey);
                var list = conversations
                    .Where(c => c.Messages.Count > 0)   // conversa vazia não é guardada
                    .ToList();
                if (limit > 0 && list.Count > limit)
                    list = list.Take(limit).ToList();

                if (list.Count == 0) all.Remove(Norm(projectKey));
                else all[Norm(projectKey)] = list;

                var f = FileFor(storageKey);
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllText(f, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* histórico é best-effort, nunca quebra a conversa */ }
        }

        private static string Norm(string key)
            => string.IsNullOrWhiteSpace(key) ? "NXProject" : key.Trim();
    }
}
