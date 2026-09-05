// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NXProject.Services
{
    /// <summary>
    /// Login com a conta CORPORATIVA (Microsoft Entra ID / Azure AD) pelo navegador, no fluxo
    /// de código de dispositivo: o NX mostra um código, abre a página da Microsoft e espera o
    /// usuário confirmar. A senha é digitada só na página da Microsoft — o NXProject nunca a vê
    /// nem a guarda (senha em app não funciona com MFA/Acesso Condicional e é bloqueada na
    /// maioria dos tenants).
    ///
    /// O que fica salvo em disco é o token, cifrado com DPAPI do usuário (mesma proteção do PAT
    /// do TFS), com o refresh token para renovar sem novo login.
    ///
    /// Exige um REGISTRO DE APLICATIVO no tenant da empresa (Client ID) com "public client /
    /// native" habilitado — é a TI quem cria; sem ele não há como emitir token.
    /// </summary>
    public static class EntraAuthService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

        private static string TokenFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXProject.Community", "entra-token.json");

        // ── Token salvo ──────────────────────────────────────────────────────
        private sealed class StoredToken
        {
            public string EncryptedAccessToken { get; set; } = string.Empty;
            public string EncryptedRefreshToken { get; set; } = string.Empty;
            public DateTime ExpiresAtUtc { get; set; }
            public string Account { get; set; } = string.Empty;
            public string TenantId { get; set; } = string.Empty;
            public string ClientId { get; set; } = string.Empty;
            public string Scope { get; set; } = string.Empty;
        }

        public sealed class TokenInfo
        {
            public string AccessToken { get; init; } = string.Empty;
            public DateTime ExpiresAtUtc { get; init; }
            public string Account { get; init; } = string.Empty;
            public bool IsValid => !string.IsNullOrWhiteSpace(AccessToken) && ExpiresAtUtc > DateTime.UtcNow;
        }

        /// <summary>Dados do login em andamento (código que o usuário digita na página da Microsoft).</summary>
        public sealed class DeviceCodePrompt
        {
            public string UserCode { get; init; } = string.Empty;
            public string VerificationUri { get; init; } = string.Empty;
            public string Message { get; init; } = string.Empty;
            internal string DeviceCode { get; init; } = string.Empty;
            internal int IntervalSeconds { get; init; } = 5;
            internal int ExpiresInSeconds { get; init; } = 900;
        }

        // ── Login ────────────────────────────────────────────────────────────
        /// <summary>Passo 1: pede o código de dispositivo e abre a página de login no navegador.</summary>
        public static async Task<DeviceCodePrompt> StartLoginAsync(
            string tenantId, string clientId, string scope, CancellationToken ct = default)
        {
            Require(tenantId, "Tenant ID");
            Require(clientId, "Client ID");
            var effectiveScope = string.IsNullOrWhiteSpace(scope) ? "openid profile offline_access" : scope.Trim();
            if (!effectiveScope.Contains("offline_access", StringComparison.OrdinalIgnoreCase))
                effectiveScope += " offline_access"; // sem isso não vem refresh token

            var url = $"https://login.microsoftonline.com/{tenantId.Trim()}/oauth2/v2.0/devicecode";
            using var response = await Http.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(),
                ["scope"] = effectiveScope,
            }), ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(DescribeError(doc.RootElement, response.StatusCode.ToString()));

            var root = doc.RootElement;
            var prompt = new DeviceCodePrompt
            {
                UserCode = root.GetProperty("user_code").GetString() ?? "",
                VerificationUri = root.GetProperty("verification_uri").GetString() ?? "",
                Message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "",
                DeviceCode = root.GetProperty("device_code").GetString() ?? "",
                IntervalSeconds = root.TryGetProperty("interval", out var i) ? i.GetInt32() : 5,
                ExpiresInSeconds = root.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 900,
            };

            // Abre a página no navegador padrão: se a conta da empresa já estiver logada,
            // o usuário só confirma (SSO), sem digitar e-mail e senha.
            try
            {
                Process.Start(new ProcessStartInfo(prompt.VerificationUri) { UseShellExecute = true });
            }
            catch { /* sem navegador: o usuário abre o link manualmente pela mensagem */ }

            return prompt;
        }

        /// <summary>Passo 2: aguarda o usuário confirmar no navegador e guarda o token cifrado.</summary>
        public static async Task<TokenInfo> CompleteLoginAsync(
            DeviceCodePrompt prompt, string tenantId, string clientId, string scope,
            CancellationToken ct = default)
        {
            var url = $"https://login.microsoftonline.com/{tenantId.Trim()}/oauth2/v2.0/token";
            var deadline = DateTime.UtcNow.AddSeconds(prompt.ExpiresInSeconds);
            var interval = TimeSpan.FromSeconds(Math.Max(1, prompt.IntervalSeconds));

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(interval, ct);

                using var response = await Http.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = clientId.Trim(),
                    ["device_code"] = prompt.DeviceCode,
                }), ct);

                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (response.IsSuccessStatusCode)
                    return SaveToken(root, tenantId, clientId, scope);

                var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                switch (error)
                {
                    case "authorization_pending":
                        continue;                        // usuário ainda não confirmou
                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        continue;
                    case "authorization_declined":
                        throw new InvalidOperationException("Login recusado na página da Microsoft.");
                    case "expired_token":
                        throw new InvalidOperationException("O código expirou antes da confirmação. Tente entrar de novo.");
                    default:
                        throw new InvalidOperationException(DescribeError(root, error ?? "erro desconhecido"));
                }
            }

            throw new InvalidOperationException("Tempo esgotado esperando a confirmação no navegador.");
        }

        // ── Uso do token ─────────────────────────────────────────────────────
        /// <summary>Token válido para chamar o serviço: renova pelo refresh token quando expirado.</summary>
        public static async Task<TokenInfo> GetValidTokenAsync(CancellationToken ct = default)
        {
            var stored = Load();
            if (stored == null)
                throw new InvalidOperationException(
                    "Nenhuma conta conectada. Use \"Entrar com a conta da empresa\" na tela do Assistente de IA.");

            var access = AISettingsStore.DecryptSecret(stored.EncryptedAccessToken);
            // Margem de 2 min: token que vence no meio da chamada falharia no servidor.
            if (!string.IsNullOrWhiteSpace(access) && stored.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2))
                return new TokenInfo { AccessToken = access, ExpiresAtUtc = stored.ExpiresAtUtc, Account = stored.Account };

            var refresh = AISettingsStore.DecryptSecret(stored.EncryptedRefreshToken);
            if (string.IsNullOrWhiteSpace(refresh))
                throw new InvalidOperationException("A sessão expirou. Entre de novo com a conta da empresa.");

            var url = $"https://login.microsoftonline.com/{stored.TenantId}/oauth2/v2.0/token";
            using var response = await Http.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = stored.ClientId,
                ["refresh_token"] = refresh,
                ["scope"] = stored.Scope,
            }), ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    "Não foi possível renovar a sessão — entre de novo com a conta da empresa.\n"
                    + DescribeError(doc.RootElement, response.StatusCode.ToString()));

            var renewed = SaveToken(doc.RootElement, stored.TenantId, stored.ClientId, stored.Scope);
            return renewed;
        }

        /// <summary>Conta conectada e validade do token (para exibir na tela); nulo sem login.</summary>
        public static (string Account, DateTime ExpiresAtUtc)? GetCurrentSession()
        {
            var stored = Load();
            return stored == null ? null : (stored.Account, stored.ExpiresAtUtc);
        }

        /// <summary>Apaga o token salvo (desconectar).</summary>
        public static void SignOut()
        {
            try { if (File.Exists(TokenFile)) File.Delete(TokenFile); }
            catch { /* arquivo em uso: proxima gravacao sobrescreve */ }
        }

        // ── Interno ──────────────────────────────────────────────────────────
        private static TokenInfo SaveToken(JsonElement root, string tenantId, string clientId, string scope)
        {
            var access = root.GetProperty("access_token").GetString() ?? "";
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "";
            var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            var account = ReadAccountFromIdToken(root);

            var stored = new StoredToken
            {
                EncryptedAccessToken = AISettingsStore.EncryptSecret(access),
                EncryptedRefreshToken = AISettingsStore.EncryptSecret(refresh),
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn),
                Account = account,
                TenantId = tenantId.Trim(),
                ClientId = clientId.Trim(),
                Scope = scope?.Trim() ?? "",
            };

            Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!);
            File.WriteAllText(TokenFile, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));

            return new TokenInfo { AccessToken = access, ExpiresAtUtc = stored.ExpiresAtUtc, Account = account };
        }

        private static StoredToken? Load()
        {
            try
            {
                return File.Exists(TokenFile)
                    ? JsonSerializer.Deserialize<StoredToken>(File.ReadAllText(TokenFile))
                    : null;
            }
            catch { return null; }
        }

        /// <summary>Nome/e-mail da conta lido do id_token (só para mostrar quem está conectado).</summary>
        private static string ReadAccountFromIdToken(JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("id_token", out var idTokenProp)) return string.Empty;
                var parts = (idTokenProp.GetString() ?? "").Split('.');
                if (parts.Length < 2) return string.Empty;

                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
                foreach (var claim in new[] { "preferred_username", "upn", "email", "name" })
                    if (claims.RootElement.TryGetProperty(claim, out var v))
                        return v.GetString() ?? string.Empty;
            }
            catch { /* id_token ausente ou fora do formato: so nao mostra a conta */ }
            return string.Empty;
        }

        private static string DescribeError(JsonElement root, string fallback)
        {
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            return string.IsNullOrWhiteSpace(description) ? (error ?? fallback) : description!;
        }

        private static void Require(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Informe o {field} do registro de aplicativo da empresa antes de entrar.");
        }
    }
}
