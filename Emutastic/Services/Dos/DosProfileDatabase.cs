using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Emutastic.Models.Dos;

namespace Emutastic.Services.Dos
{
    /// <summary>
    /// Loads <c>Resources/dos-profiles.json</c> (embedded) and provides a
    /// fast filename → profile lookup. Mirrors Boxer's
    /// <c>+detectedProfileForPath:searchSubfolders:</c> in
    /// <c>BXGameProfile.m</c>: build a hashtable from telltales at load time,
    /// then walk the dropped folder's filenames against it.
    ///
    /// Lookup is O(N) in the number of files in the dropped folder,
    /// O(1) per file. Sub-millisecond on any realistic input.
    /// </summary>
    public class DosProfileDatabase
    {
        private static DosProfileDatabase? _shared;
        public static DosProfileDatabase Shared => _shared ??= Load();

        private readonly Dictionary<string, DosGameProfile> _telltaleIndex;
        private readonly DosProfileDatabaseFile _data;

        public DosGameProfile? Generic => _data.Generic;
        public IReadOnlyDictionary<string, Dictionary<string, string>> Snippets => _data.Snippets;
        public IReadOnlyList<DosGameProfile> Profiles => _data.Profiles;

        private DosProfileDatabase(DosProfileDatabaseFile data)
        {
            _data = data;
            _telltaleIndex = new Dictionary<string, DosGameProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in data.Profiles)
            {
                foreach (var telltale in profile.Telltales)
                {
                    // First profile to claim a telltale wins. Profiles are
                    // ordered roughly by specificity in the JSON, so this
                    // matches Boxer's first-hit semantics.
                    _telltaleIndex.TryAdd(telltale, profile);
                }
            }
        }

        /// <summary>
        /// Returns the first matching profile for a folder's filenames, or null
        /// when no telltale matches. Caller falls back to <see cref="Generic"/>.
        /// </summary>
        public DosGameProfile? Match(IEnumerable<string> filenames)
        {
            foreach (var f in filenames)
            {
                string leaf = Path.GetFileName(f);
                if (_telltaleIndex.TryGetValue(leaf, out var profile))
                    return profile;
            }
            return null;
        }

        /// <summary>
        /// Composes the named snippets into a single core-options dictionary.
        /// Later snippets override earlier ones (matches Boxer's snippet
        /// composition in BXEmulatorConfiguration).
        /// </summary>
        public Dictionary<string, string> ResolveCoreOptions(IEnumerable<string> snippetNames)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in snippetNames)
            {
                if (!_data.Snippets.TryGetValue(name, out var snippet)) continue;
                foreach (var (k, v) in snippet) result[k] = v;
            }
            return result;
        }

        // ── Loader ────────────────────────────────────────────────────────────

        private static DosProfileDatabase Load()
        {
            try
            {
                using var stream = ResolveResourceStream();
                if (stream == null)
                {
                    Trace.WriteLine("[DosProfileDB] Embedded dos-profiles.json not found — using empty profile DB.");
                    return new DosProfileDatabase(EmptyData());
                }

                var data = JsonSerializer.Deserialize<DosProfileDatabaseFile>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

                if (data == null)
                {
                    Trace.WriteLine("[DosProfileDB] dos-profiles.json deserialized to null — using empty profile DB.");
                    return new DosProfileDatabase(EmptyData());
                }

                Trace.WriteLine($"[DosProfileDB] Loaded {data.Profiles.Count} profiles, {data.Snippets.Count} snippets.");
                return new DosProfileDatabase(data);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DosProfileDB] Load failed — using empty DB: {ex.Message}");
                return new DosProfileDatabase(EmptyData());
            }
        }

        private static Stream? ResolveResourceStream()
        {
            var asm = typeof(DosProfileDatabase).Assembly;
            // Embedded resource name format: <RootNamespace>.<RelativePath with dots>
            string[] candidates = asm.GetManifestResourceNames()
                .Where(n => n.EndsWith("dos-profiles.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (candidates.Length == 0) return null;
            return asm.GetManifestResourceStream(candidates[0]);
        }

        private static DosProfileDatabaseFile EmptyData() => new()
        {
            Generic = new DosGameProfile { Id = "generic", Title = "DOS Game", Snippets = new() { "Auto" } },
            Snippets = new() { ["Auto"] = new() { ["dosbox_pure_cycles"] = "auto" } },
        };
    }
}
