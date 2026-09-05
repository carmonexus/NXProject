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
    /// Salva e carrega a configuração de custo dos recursos em um arquivo .nxcost criptografado.
    ///
    /// Formato do arquivo:
    ///   [4]  magic "NXCT"
    ///   [1]  versão (1)
    ///   [16] salt PBKDF2
    ///   [12] nonce AES-GCM
    ///   [16] tag de autenticação GCM
    ///   [*]  ciphertext (JSON criptografado)
    ///
    /// Algoritmo: AES-256-GCM + PBKDF2-SHA256 (100 000 iterações).
    /// Sem a senha correta, o arquivo é inútil e indecifrável.
    /// </summary>
    public static class ResourceCostConfigService
    {
        public const string FileExtension   = ".nxcost";
        public const string FileFilter      = "Configuração de Custo (*.nxcost)|*.nxcost";
        public const string DefaultFileName = "recursos-custo.nxcost";

        private static readonly byte[] Magic       = Encoding.ASCII.GetBytes("NXCT");
        private const           byte   FileVersion = 1;
        private const           int    KeySize     = 32;   // AES-256
        private const           int    SaltSize    = 16;
        private const           int    NonceSize   = 12;   // GCM standard
        private const           int    TagSize     = 16;
        private const           int    Iterations  = 100_000;

        private sealed record CostEntry(
            string  Name,
            string  CostType,
            decimal HourlyRate,
            decimal MonthlyRate);

        // ── Save ──────────────────────────────────────────────────────────────

        public static void Save(string filePath, IEnumerable<Resource> resources, string password)
        {
            var entries = resources
                .Select(r => new CostEntry(r.Name, r.CostType.ToString(), r.CostPerHour, r.MonthlyRate))
                .ToList();

            var json      = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            var plaintext = Encoding.UTF8.GetBytes(json);

            var salt  = RandomNumberGenerator.GetBytes(SaltSize);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var key   = DeriveKey(password, salt);

            var ciphertext = new byte[plaintext.Length];
            var tag        = new byte[TagSize];

            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            fs.Write(Magic);
            fs.WriteByte(FileVersion);
            fs.Write(salt);
            fs.Write(nonce);
            fs.Write(tag);
            fs.Write(ciphertext);
        }

        // ── Load ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Tenta descriptografar e carregar o arquivo.
        /// Lança <see cref="CryptographicException"/> se a senha for errada ou o arquivo estiver corrompido.
        /// Retorna o número de recursos atualizados (e incluídos, se <paramref name="addMissing"/>).
        /// </summary>
        /// <param name="addMissing">
        /// Quando true, pessoas presentes no arquivo mas nao cadastradas no projeto
        /// sao incluidas (com nome e custo do arquivo).
        /// </param>
        public static int Load(string filePath, ICollection<Resource> resources, string password, bool addMissing = false)
        {
            var data = File.ReadAllBytes(filePath);

            // Valida magic + versão
            if (data.Length < Magic.Length + 1 + SaltSize + NonceSize + TagSize)
                throw new InvalidDataException("Arquivo de custo inválido ou corrompido.");

            int pos = 0;
            for (int i = 0; i < Magic.Length; i++)
                if (data[pos++] != Magic[i])
                    throw new InvalidDataException("Arquivo não é um .nxcost válido.");

            byte version = data[pos++];
            if (version != FileVersion)
                throw new InvalidDataException($"Versão {version} não suportada.");

            var salt       = data[pos..(pos + SaltSize)];   pos += SaltSize;
            var nonce      = data[pos..(pos + NonceSize)];   pos += NonceSize;
            var tag        = data[pos..(pos + TagSize)];     pos += TagSize;
            var ciphertext = data[pos..];

            var key       = DeriveKey(password, salt);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, TagSize);
            // Lança CryptographicException automaticamente se a senha for errada
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            var json    = Encoding.UTF8.GetString(plaintext);
            var entries = JsonSerializer.Deserialize<List<CostEntry>>(json)
                          ?? throw new InvalidDataException("Conteúdo do arquivo inválido.");

            var map   = entries.ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);
            int count = 0;

            // Nomes ja cadastrados (para atualizar e detectar ausentes).
            var existingNames = new HashSet<string>(
                resources.Where(r => !string.IsNullOrWhiteSpace(r.Name)).Select(r => r.Name.Trim()),
                StringComparer.OrdinalIgnoreCase);

            foreach (var r in resources)
            {
                if (!map.TryGetValue(r.Name, out var e)) continue;
                r.CostType    = e.CostType == "Monthly" ? ResourceCostType.Monthly : ResourceCostType.Hourly;
                r.CostPerHour  = e.HourlyRate;
                r.MonthlyRate  = e.MonthlyRate;
                count++;
            }

            if (addMissing)
            {
                var nextId = resources.Count > 0 ? resources.Max(r => r.Id) + 1 : 1;
                foreach (var e in entries)
                {
                    if (string.IsNullOrWhiteSpace(e.Name) || existingNames.Contains(e.Name.Trim()))
                        continue;

                    resources.Add(new Resource
                    {
                        Id = nextId++,
                        Name = e.Name.Trim(),
                        Type = ResourceType.Work,
                        Kind = ResourceKind.Project,
                        AvailabilityPercent = 100.0,
                        MaxUnitsPerDay = 8.0,
                        CostType = e.CostType == "Monthly" ? ResourceCostType.Monthly : ResourceCostType.Hourly,
                        CostPerHour = e.HourlyRate,
                        MonthlyRate = e.MonthlyRate
                    });
                    existingNames.Add(e.Name.Trim());
                    count++;
                }
            }

            return count;
        }

        // ── Key derivation ────────────────────────────────────────────────────

        private static byte[] DeriveKey(string password, byte[] salt)
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                KeySize);
        }
    }
}
