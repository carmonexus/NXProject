// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NXProject.Models;

namespace NXProject.Services
{
    /// <summary>
    /// Persiste configurações de Kind (Project/Internal) dos recursos em LocalAppData,
    /// independente do arquivo .nxp. Usado para preservar Kind ao re-importar do TFS.
    /// </summary>
    public static class ResourceKindConfigService
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXProject", "resource-kinds.json");

        /// <summary>Salva o mapeamento nome→Kind de todos os recursos fornecidos.</summary>
        public static void Save(IEnumerable<Resource> resources)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in resources)
                if (!string.IsNullOrWhiteSpace(r.Name))
                    dict[r.Name.Trim()] = r.Kind.ToString();

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            // LGPD: nomes de pessoas são dados pessoais — grava cifrado (DPAPI, escopo do usuário).
            File.WriteAllText(FilePath, WindowsDataProtection.Encrypt(json, "NXProject.ResourceKinds"));
        }

        /// <summary>Carrega o mapeamento nome→Kind salvo localmente.</summary>
        public static Dictionary<string, ResourceKind> Load()
        {
            var result = new Dictionary<string, ResourceKind>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FilePath)) return result;
            try
            {
                var content = File.ReadAllText(FilePath);
                // Cifrado (DPAPI) → decifra; arquivo antigo em texto plano → usa como está
                // (migra para cifrado na próxima gravação).
                var json = WindowsDataProtection.Decrypt(content);
                if (string.IsNullOrEmpty(json))
                    json = content;
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict == null) return result;
                foreach (var (name, kindStr) in dict)
                    if (Enum.TryParse<ResourceKind>(kindStr, out var kind))
                        result[name] = kind;
            }
            catch { /* arquivo corrompido → ignora */ }
            return result;
        }

        /// <summary>
        /// Aplica os Kinds salvos localmente sobre os recursos do projeto.
        /// Chamado após importação do TFS para preservar configurações manuais.
        /// </summary>
        public static void ApplyTo(IEnumerable<Resource> resources)
        {
            var local = Load();
            if (local.Count == 0) return;
            foreach (var r in resources)
                if (!string.IsNullOrWhiteSpace(r.Name) && local.TryGetValue(r.Name.Trim(), out var kind))
                    r.Kind = kind;
        }
    }
}
