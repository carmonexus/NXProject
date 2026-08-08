using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NXProject.Models;

namespace NXProject.Community.Services
{
    /// <summary>
    /// Casa o responsável citado na reunião (texto livre ou resposta da IA) com um recurso
    /// Work do cronograma. Vai além da igualdade exata porque o nome citado quase nunca vem
    /// idêntico ao cadastro "Sobrenome, Nome (Contractor)": pode vir invertido, sem o sufixo
    /// entre parênteses, sem acento, com pontuação ou apenas o primeiro nome.
    /// </summary>
    public static class TaskPlanResourceMatcher
    {
        /// <summary>Nome do recurso como vai para a planilha (Name, ou DisplayName sem o '*').</summary>
        public static string PlanName(Resource r) =>
            !string.IsNullOrWhiteSpace(r.Name) ? r.Name.Trim() : r.DisplayName?.TrimStart('*').Trim() ?? "";

        /// <summary>
        /// Recurso Work correspondente ao nome citado, em camadas: exato → normalizado
        /// (sem acento/pontuação/sufixo "(Contractor)") → tokens do citado contidos no
        /// cadastro → nome do cadastro citado dentro de um texto maior. Nulo sem
        /// correspondência ou quando há EMPATE (dois recursos igualmente prováveis).
        /// </summary>
        public static Resource? Find(IEnumerable<Resource>? resources, string? cited)
        {
            var value = cited?.Trim().TrimStart('*').Trim();
            if (string.IsNullOrEmpty(value) || resources == null) return null;

            var people = resources.Where(r => r.Type == ResourceType.Work)
                .Where(r => Keys(r).Any())
                .ToList();
            if (people.Count == 0) return null;

            // 1) Igualdade exata em qualquer chave do cadastro (Name/DisplayName).
            var exact = people.FirstOrDefault(r => Keys(r)
                .Any(k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase)));
            if (exact != null) return exact;

            // 2) Normalizado: sem acento, sem pontuação, sem sufixo "(Contractor)".
            var wanted = Normalize(value);
            if (wanted.Length == 0) return null;
            var norm = people.Where(r => Keys(r).Any(k => Normalize(k) == wanted)).ToList();
            if (norm.Count == 1) return norm[0];

            // 3) Tokens: todos os tokens do citado presentes no cadastro (ordem livre —
            //    "Alice Oliveira" casa com "Oliveira, Alice De Muylder"). Um único token
            //    (ex.: só o primeiro nome) só vale se identificar UM recurso.
            var wantedTokens = Tokens(value);
            if (wantedTokens.Count > 0)
            {
                var byTokens = people
                    .Where(r => Keys(r).Any(k => wantedTokens.IsSubsetOf(Tokens(k))))
                    .Distinct()
                    .ToList();
                if (byTokens.Count == 1) return byTokens[0];
                if (byTokens.Count > 1)
                {
                    // Empate: só resolve se um cadastro tiver MENOS tokens extras que os demais.
                    var ranked = byTokens
                        .Select(r => (Resource: r, Extra: Keys(r).Min(k => Tokens(k).Count) - wantedTokens.Count))
                        .OrderBy(x => x.Extra)
                        .ToList();
                    if (ranked.Count > 1 && ranked[0].Extra < ranked[1].Extra) return ranked[0].Resource;
                    return null; // ambíguo: melhor deixar como observação do que errar a pessoa
                }
            }

            // 4) Texto livre: o nome do cadastro aparece dentro da frase citada.
            return people
                .SelectMany(r => Keys(r).Select(k => (Resource: r, Key: k)))
                .Where(x => x.Key.Length >= 3 && ContainsName(value, x.Key))
                .OrderByDescending(x => x.Key.Length)
                .Select(x => x.Resource)
                .FirstOrDefault();
        }

        private static IEnumerable<string> Keys(Resource r) =>
            new[] { r.Name?.Trim(), r.DisplayName?.TrimStart('*').Trim() }
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k!)
                .Distinct(StringComparer.OrdinalIgnoreCase);

        // "Oliveira, Alice De Muylder (Contractor)" → "oliveira alice de muylder"
        private static string Normalize(string value)
        {
            var noSuffix = System.Text.RegularExpressions.Regex.Replace(value, @"\([^)]*\)", " ");
            var decomposed = noSuffix.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
            }
            return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        // Partículas de nome não identificam ninguém: fora do conjunto comparado.
        private static readonly HashSet<string> NameParticles =
            new(StringComparer.Ordinal) { "de", "da", "do", "das", "dos", "e", "del", "di", "contractor" };

        private static HashSet<string> Tokens(string value) =>
            new(Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length >= 2 && !NameParticles.Contains(t)),
                StringComparer.Ordinal);

        private static bool ContainsName(string text, string name)
        {
            int start = 0;
            while (start < text.Length)
            {
                var index = text.IndexOf(name, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0) return false;
                var beforeOk = index == 0 || IsBoundary(text[index - 1]);
                var afterIndex = index + name.Length;
                var afterOk = afterIndex >= text.Length || IsBoundary(text[afterIndex]);
                if (beforeOk && afterOk) return true;
                start = index + 1;
            }
            return false;
        }

        private static bool IsBoundary(char ch)
            => !char.IsLetterOrDigit(ch) && ch != '@' && ch != '.' && ch != '_';
    }
}
