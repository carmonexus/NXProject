// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.IO;
using System.Text.Json;

namespace NXProject.Services
{
    /// <summary>
    /// Registro do aceite da licenca nesta maquina (por usuario Windows).
    ///
    /// O aceite e um clickwrap: a tela de licenca aparece na PRIMEIRA execucao e o app
    /// so segue depois do "Aceitar e continuar". Instalar, por si so, nao aceita nada.
    ///
    /// O arquivo guarda quando foi aceito e qual versao do app registrou o aceite —
    /// antes gravava apenas a palavra "accepted", sem data, o que impedia mostrar
    /// "Termo aceito em ..." e nao deixava rastro de QUAL texto foi aceito. Arquivos no
    /// formato antigo continuam validos (a data cai para a data do arquivo).
    /// </summary>
    public static class LicenseAcceptanceStore
    {
        private static string Directory_ => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXProject.Community");

        public static string FilePath => Path.Combine(Directory_, "license.accepted");

        private sealed class Record
        {
            public string? AcceptedAt { get; set; }   // ISO 8601 (local)
            public string? AppVersion { get; set; }   // versao do app que registrou o aceite
        }

        public static bool HasAccepted() => File.Exists(FilePath);

        /// <summary>Data do aceite; null quando o arquivo nao existe ou nao pode ser lido.</summary>
        public static DateTime? AcceptedOn()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;

                var raw = File.ReadAllText(FilePath).Trim();
                if (raw.StartsWith("{", StringComparison.Ordinal))
                {
                    var rec = JsonSerializer.Deserialize<Record>(raw);
                    if (rec?.AcceptedAt is { Length: > 0 } s && DateTime.TryParse(s, out var dt))
                        return dt;
                }

                // Formato antigo ("accepted"): a data do arquivo e a melhor evidencia.
                return File.GetLastWriteTime(FilePath);
            }
            catch
            {
                return null;
            }
        }

        public static void Persist(string? appVersion = null)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                var rec = new Record
                {
                    AcceptedAt = DateTime.Now.ToString("o"),
                    AppVersion = appVersion
                };
                File.WriteAllText(FilePath, JsonSerializer.Serialize(rec));
            }
            catch
            {
                // Sem o registro o app volta a pedir o aceite na proxima abertura —
                // preferivel a impedir o uso por falha de escrita no perfil.
            }
        }
    }
}
