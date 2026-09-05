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
    public static class DevOpsProjectListService
    {
        public static List<DevOpsProject> Load(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return new List<DevOpsProject>();
            try
            {
                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<List<DevOpsProject>>(json) ?? new List<DevOpsProject>();
            }
            catch
            {
                return new List<DevOpsProject>();
            }
        }

        public static void Save(IEnumerable<DevOpsProject> projects, string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(projects, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
    }
}
