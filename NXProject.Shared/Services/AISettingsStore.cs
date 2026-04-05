using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NXProject.Models;

namespace NXProject.Services
{
    public static class AISettingsStore
    {
        private sealed class StoredAISettings
        {
            public string Provider { get; set; } = "OpenRouter";
            public string EncryptedApiKey { get; set; } = string.Empty;
            public string Endpoint { get; set; } = string.Empty;
            public string Model { get; set; } = string.Empty;
            public int TimeoutSeconds { get; set; } = 120;
        }

        private static readonly string SettingsDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXProject.Community");

        private static readonly string SettingsFile =
            Path.Combine(SettingsDirectory, "ai-settings.json");

        public static AISettings Load()
        {
            if (!File.Exists(SettingsFile))
            {
                return new AISettings
                {
                    Provider = AIProvider.OpenRouter,
                    Endpoint = AIProviderDefaults.GetDefaultEndpoint(AIProvider.OpenRouter),
                    Model = AIProviderDefaults.GetDefaultModel(AIProvider.OpenRouter),
                    TimeoutSeconds = 120
                };
            }

            try
            {
                var json = File.ReadAllText(SettingsFile);
                var stored = JsonSerializer.Deserialize<StoredAISettings>(json);
                if (stored == null)
                    throw new InvalidOperationException("Configuracao de IA invalida.");

                var settings = new AISettings
                {
                    Provider = AIProvider.OpenRouter,
                    ApiKey = Decrypt(stored.EncryptedApiKey),
                    Endpoint = stored.Endpoint,
                    Model = stored.Model,
                    TimeoutSeconds = stored.TimeoutSeconds <= 0 ? 120 : stored.TimeoutSeconds
                };

                if (string.IsNullOrWhiteSpace(settings.Endpoint) ||
                    string.Equals(settings.Endpoint.Trim(), AIProviderDefaults.GetDefaultEndpoint(AIProvider.OpenAI), StringComparison.OrdinalIgnoreCase))
                    settings.Endpoint = AIProviderDefaults.GetDefaultEndpoint(AIProvider.OpenRouter);

                if (string.IsNullOrWhiteSpace(settings.Model) ||
                    string.Equals(settings.Model.Trim(), AIProviderDefaults.GetDefaultModel(AIProvider.OpenAI), StringComparison.OrdinalIgnoreCase))
                    settings.Model = AIProviderDefaults.GetDefaultModel(AIProvider.OpenRouter);

                settings.Provider = AIProvider.OpenRouter;
                return settings;
            }
            catch
            {
                return new AISettings
                {
                    Provider = AIProvider.OpenRouter,
                    Endpoint = AIProviderDefaults.GetDefaultEndpoint(AIProvider.OpenRouter),
                    Model = AIProviderDefaults.GetDefaultModel(AIProvider.OpenRouter),
                    TimeoutSeconds = 120
                };
            }
        }

        public static void Save(AISettings settings)
        {
            Directory.CreateDirectory(SettingsDirectory);
            var sanitizedApiKey = SanitizeSecret(settings.ApiKey);
            var payload = new StoredAISettings
            {
                Provider = nameof(AIProvider.OpenRouter),
                EncryptedApiKey = Encrypt(sanitizedApiKey),
                Endpoint = settings.Endpoint?.Trim() ?? AIProviderDefaults.GetDefaultEndpoint(AIProvider.OpenRouter),
                Model = settings.Model?.Trim() ?? AIProviderDefaults.GetDefaultModel(AIProvider.OpenRouter),
                TimeoutSeconds = settings.TimeoutSeconds <= 0 ? 120 : settings.TimeoutSeconds
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }

        public static string SanitizeSecret(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var trimmed = value.Trim();
            var builder = new StringBuilder(trimmed.Length);
            foreach (var c in trimmed)
            {
                if (char.IsWhiteSpace(c))
                    continue;

                if (c == '"' || c == '\'' || c == '`')
                    continue;

                if (c == '\u200B' || c == '\u200C' || c == '\u200D' || c == '\uFEFF')
                    continue;

                builder.Append(c);
            }

            return builder.ToString();
        }

        private static string Encrypt(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(value);
            var protectedBytes = ProtectForCurrentUser(bytes);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string Decrypt(string encryptedValue)
        {
            if (string.IsNullOrWhiteSpace(encryptedValue))
                return string.Empty;

            try
            {
                var protectedBytes = Convert.FromBase64String(encryptedValue);
                var bytes = UnprotectForCurrentUser(protectedBytes);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static byte[] ProtectForCurrentUser(byte[] plainBytes)
        {
            var input = CreateBlob(plainBytes);
            DATA_BLOB output = default;

            try
            {
                if (!CryptProtectData(ref input, "NXProject.Community.AI", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                    throw new InvalidOperationException("Nao foi possivel criptografar o token localmente.");

                return CopyBlob(output);
            }
            finally
            {
                FreeBlob(input);
                FreeProtectedBlob(output);
            }
        }

        private static byte[] UnprotectForCurrentUser(byte[] protectedBytes)
        {
            var input = CreateBlob(protectedBytes);
            DATA_BLOB output = default;

            try
            {
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                    throw new InvalidOperationException("Nao foi possivel descriptografar o token localmente.");

                return CopyBlob(output);
            }
            finally
            {
                FreeBlob(input);
                FreeProtectedBlob(output);
            }
        }

        private static DATA_BLOB CreateBlob(byte[] bytes)
        {
            var blob = new DATA_BLOB
            {
                cbData = bytes.Length,
                pbData = Marshal.AllocHGlobal(bytes.Length)
            };

            Marshal.Copy(bytes, 0, blob.pbData, bytes.Length);
            return blob;
        }

        private static byte[] CopyBlob(DATA_BLOB blob)
        {
            if (blob.pbData == IntPtr.Zero || blob.cbData <= 0)
                return Array.Empty<byte>();

            var bytes = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, bytes, 0, blob.cbData);
            return bytes;
        }

        private static void FreeBlob(DATA_BLOB blob)
        {
            if (blob.pbData != IntPtr.Zero)
                Marshal.FreeHGlobal(blob.pbData);
        }

        private static void FreeProtectedBlob(DATA_BLOB blob)
        {
            if (blob.pbData != IntPtr.Zero)
                LocalFree(blob.pbData);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn,
            string szDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
