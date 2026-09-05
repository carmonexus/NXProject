// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NXProject.Models;

namespace NXProject.Services
{
    /// <summary>
    /// Registro LOCAL das ausências das pessoas (férias, folga, feriado municipal de outra
    /// cidade). Fica em LocalAppData e cifrado (DPAPI), como os demais dados pessoais — LGPD.
    ///
    /// O .nxp também leva as ausências, para que outra pessoa que receba o arquivo calcule as
    /// datas corretamente. Como o arquivo é uma CÓPIA, ele envelhece: por isso, ao abrir na
    /// MESMA máquina que salvou, o registro local é a fonte da verdade e reconcilia o arquivo
    /// (ausência cancelada aqui some do cronograma). Em outra máquina, vale o que veio no arquivo.
    /// </summary>
    public static class ResourceAbsenceConfigService
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXProject", "resource-absences.json");

        private sealed class AbsenceDto
        {
            public string Date { get; set; } = "";
            public string Reason { get; set; } = "";
        }

        /// <summary>
        /// Identificador estável desta máquina+usuário. É um HASH (não guarda nome de máquina
        /// nem de usuário em claro) usado só para saber se o arquivo foi salvo aqui.
        /// </summary>
        public static string MachineId
        {
            get
            {
                var raw = Environment.MachineName + "|" + Environment.UserName;
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
                return Convert.ToHexString(bytes, 0, 8);   // 16 chars, suficiente e curto
            }
        }

        /// <summary>Grava no registro local as ausências das pessoas informadas.</summary>
        public static void Save(IEnumerable<Resource> resources)
        {
            var dict = new Dictionary<string, List<AbsenceDto>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in resources)
            {
                if (string.IsNullOrWhiteSpace(r.Name)) continue;
                dict[r.Name.Trim()] = (r.Absences ?? new List<ResourceAbsence>())
                    .OrderBy(a => a.Date)
                    .Select(a => new AbsenceDto
                    {
                        Date = a.Date.ToString("yyyy-MM-dd"),
                        Reason = a.Reason ?? ""
                    })
                    .ToList();
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, WindowsDataProtection.Encrypt(json, "NXProject.ResourceAbsences"));
            }
            catch { /* registro local é conveniência: nunca derruba o salvamento do cronograma */ }
        }

        /// <summary>Lê o registro local nome→ausências.</summary>
        public static Dictionary<string, List<ResourceAbsence>> Load()
        {
            var result = new Dictionary<string, List<ResourceAbsence>>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FilePath)) return result;
            try
            {
                var content = File.ReadAllText(FilePath);
                var json = WindowsDataProtection.Decrypt(content);
                if (string.IsNullOrEmpty(json)) json = content;   // arquivo antigo em texto plano
                var dict = JsonSerializer.Deserialize<Dictionary<string, List<AbsenceDto>>>(json);
                if (dict == null) return result;
                foreach (var (name, list) in dict)
                    result[name] = (list ?? new List<AbsenceDto>())
                        .Select(a => new ResourceAbsence
                        {
                            Date = DateTime.TryParse(a.Date, out var d) ? d.Date : DateTime.Today,
                            Reason = a.Reason ?? ""
                        })
                        .ToList();
            }
            catch { /* arquivo corrompido → ignora */ }
            return result;
        }

        /// <summary>
        /// Reconcilia as ausências do projeto com o registro local. Só deve ser chamado quando o
        /// arquivo foi salvo NESTA máquina (senão o registro local não descreve aquelas pessoas).
        /// Pessoa presente no registro tem suas ausências substituídas pelo valor local (inclusive
        /// lista vazia = ausência cancelada). Pessoa ausente do registro mantém o que veio no arquivo.
        /// </summary>
        public static void ApplyTo(IEnumerable<Resource> resources)
        {
            var local = Load();
            if (local.Count == 0) return;
            foreach (var r in resources)
            {
                if (string.IsNullOrWhiteSpace(r.Name)) continue;
                if (local.TryGetValue(r.Name.Trim(), out var list))
                    r.Absences = list.Select(a => new ResourceAbsence { Date = a.Date, Reason = a.Reason }).ToList();
            }
        }
    }
}
