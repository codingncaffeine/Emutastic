using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Emutastic.Services
{
    /// <summary>
    /// Curated catalog of known DOS games shipped as <c>Assets/dosgamelist.txt</c>.
    /// The file format is one "Title (Year).zip" entry per line — derived from a
    /// publicly-available DOS game index. Used during import to resolve a real
    /// game title from a folder name before falling back to heuristics.
    /// </summary>
    public static class DosGameListService
    {
        public readonly record struct Entry(string Title, int? Year);

        private static readonly Lazy<Dictionary<string, Entry>> _index =
            new(BuildIndex, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// Try to match <paramref name="folderName"/> against the catalog.
        /// Exact normalized match first, then prefix match (folder name is a
        /// prefix of a catalog title, with a word boundary).
        /// </summary>
        public static Entry? Match(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return null;
            var map = _index.Value;
            if (map.Count == 0) return null;

            string key = Normalize(folderName);
            if (map.TryGetValue(key, out var exact)) return exact;

            // Prefix fallback: folder "Sim City 2000 Deluxe" -> title "Sim City 2000"
            // Require a trailing space-or-punct boundary so "Warlords" doesn't match "War".
            Entry? best = null;
            int bestLen = 0;
            foreach (var kv in map)
            {
                string candidate = kv.Key;
                if (candidate.Length >= key.Length) continue;
                if (!key.StartsWith(candidate, StringComparison.Ordinal)) continue;
                char next = key[candidate.Length];
                if (next != ' ') continue;
                if (candidate.Length > bestLen)
                {
                    bestLen = candidate.Length;
                    best = kv.Value;
                }
            }
            if (best.HasValue) return best;

            // Reverse prefix: catalog title is a prefix of the folder name.
            // Handled above.  Also try folder name being a prefix of a title
            // (e.g. folder "Lemmings" -> "Lemmings 2 The Tribes" ambiguous —
            // skip; rely on exact match).
            return null;
        }

        /// <summary>
        /// Canonicalizes a title for matching: lowercase, strip punctuation,
        /// collapse whitespace, fold Roman numerals (I..X) to Arabic.
        /// </summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            string lower = s.ToLowerInvariant();
            // Replace punctuation / separators with spaces
            lower = Regex.Replace(lower, @"[_\-\.,:;!\?'""\(\)\[\]\{\}&/\\]+", " ");
            // Drop "the" as a leading or trailing particle (catalog has "11th Hour, The")
            lower = Regex.Replace(lower, @"^\s*the\s+", " ");
            lower = Regex.Replace(lower, @"\s+the\s*$", " ");
            // Collapse whitespace
            lower = Regex.Replace(lower, @"\s+", " ").Trim();
            // Fold Roman numerals to Arabic (word boundaries only)
            lower = Regex.Replace(lower, @"\b(viii|vii|iii|ix|iv|vi|ii|x|v|i)\b", m =>
            {
                return m.Value switch
                {
                    "i" => "1", "ii" => "2", "iii" => "3", "iv" => "4",
                    "v" => "5", "vi" => "6", "vii" => "7", "viii" => "8",
                    "ix" => "9", "x" => "10",
                    _ => m.Value
                };
            });
            return lower;
        }

        private static Dictionary<string, Entry> BuildIndex()
        {
            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "dosgamelist.txt");
            if (!File.Exists(path)) return map;

            var lineRegex = new Regex(@"^(?<title>.+?)\s*(?:\((?<year>\d{4})\))?\s*\.zip\s*$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                var m = lineRegex.Match(line);
                string title;
                int? year = null;
                if (m.Success)
                {
                    title = m.Groups["title"].Value.Trim();
                    if (m.Groups["year"].Success && int.TryParse(m.Groups["year"].Value, out int y))
                        year = y;
                }
                else
                {
                    // Accept bare lines too
                    title = Path.GetFileNameWithoutExtension(line);
                }

                if (string.IsNullOrWhiteSpace(title)) continue;

                string key = Normalize(title);
                if (key.Length == 0) continue;

                // Keep the first entry we see for a given normalized key (catalog
                // is sorted so this is usually the earliest release).
                if (!map.ContainsKey(key))
                    map[key] = new Entry(title, year);
            }

            return map;
        }
    }
}
