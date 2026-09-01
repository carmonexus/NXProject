using System;
using System.Collections.Generic;

namespace NXProject.Services
{
    /// <summary>
    /// Gerencia a criação de chaves de recurso (x:Key de Strings.*.xaml, etc.) evitando
    /// duplicatas — uma chave duplicada num ResourceDictionary estoura em runtime
    /// ("DeferrableContent iniciou uma exceção"). Ao tentar registrar uma chave já usada,
    /// qualifica-a com o nome da tela/fonte (ex.: "Sprint_Count" → "Taskboard_Sprint_Count").
    /// Use uma instância por dicionário/idioma.
    /// </summary>
    public sealed class ResourceKeyRegistry
    {
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        private readonly List<(string Key, string Source)> _collisions = new();

        /// <summary>Chaves já registradas.</summary>
        public IReadOnlyCollection<string> Keys => _used;

        /// <summary>Colisões encontradas (chave desejada + a tela/fonte que a qualificou).</summary>
        public IReadOnlyList<(string Key, string Source)> Collisions => _collisions;

        public bool Contains(string key) => _used.Contains(key);

        /// <summary>
        /// Registra <paramref name="key"/> e devolve uma chave única. Se já existir, qualifica
        /// com <paramref name="source"/> (nome da tela/fonte): "{source}_{key}"; se ainda assim
        /// colidir, acrescenta um contador. Fonte vazia cai só no contador.
        /// </summary>
        public string Add(string key, string source = "")
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Chave de recurso não pode ser vazia.", nameof(key));

            if (_used.Add(key))
                return key;

            _collisions.Add((key, source));
            var baseKey = string.IsNullOrWhiteSpace(source) ? key : $"{Sanitize(source)}_{key}";
            var candidate = baseKey;
            var n = 2;
            while (!_used.Add(candidate))
                candidate = $"{baseKey}_{n++}";
            return candidate;
        }

        /// <summary>Registra várias chaves de uma fonte; devolve o mapa chave-desejada → chave-final.</summary>
        public Dictionary<string, string> AddRange(IEnumerable<string> keys, string source = "")
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var k in keys)
                map[k] = Add(k, source);
            return map;
        }

        private static string Sanitize(string source)
        {
            var chars = source.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_')
                    chars[i] = '_';
            return new string(chars);
        }
    }
}
