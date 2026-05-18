using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace Emutastic.Services
{
    /// <summary>
    /// Loads Redump/No-Intro DAT files from [DataRoot]/DATs/ and provides hash-based
    /// game identification.  DAT files must be in standard Redump/No-Intro XML format
    /// and named after their console tag (e.g. Saturn.dat, SegaCD.dat, PS1.dat, TGCD.dat).
    ///
    /// DATs are loaded lazily on first lookup and cached for the session.
    /// </summary>
    public class DatMatchService
    {
        public record DatMatch(string Console, string Title);

        /// <summary>Per-game arcade metadata pulled from the FBNeo DAT.</summary>
        public record ArcadeMeta(string Title, string? Year, string? Manufacturer);

        // sha1 (lowercase hex) → DatMatch
        private readonly Dictionary<string, DatMatch> _sha1Index = new(StringComparer.OrdinalIgnoreCase);

        // Arcade short ROM name (e.g. "mslug") → ArcadeMeta(title, year, manufacturer)
        // Populated from whichever Arcade DAT loads first; used for metadata lookup.
        private readonly Dictionary<string, ArcadeMeta> _arcadeMetaIndex = new(StringComparer.OrdinalIgnoreCase);

        // Per-DAT name sets for arcade core routing. A given ROM name (e.g. "polepos")
        // may live in only one of these (mame2003-plus has Pole Position; FBNeo doesn't),
        // both (Street Fighter II), or neither. GetPreferredArcadeCore prefers FBNeo when
        // both are present because FBNeo has better-tuned controls + save state support.
        private readonly HashSet<string> _fbneoArcadeNames        = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _mame2003plusArcadeNames = new(StringComparer.OrdinalIgnoreCase);

        // NeoGeo ROM filename (e.g. "samsho") → full title (e.g. "Samurai Shodown / Samurai Spirits")
        private readonly Dictionary<string, string> _neoGeoNameIndex = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _datsFolder;
        private bool _loaded = false;

        public DatMatchService()
        {
            _datsFolder = AppPaths.GetDatsFolder();
        }

        /// <summary>
        /// Attempts to identify a game by its SHA1 hash.
        /// Returns a DatMatch if found in any loaded DAT, otherwise null.
        /// </summary>
        public DatMatch? LookupBySha1(string sha1)
        {
            EnsureLoaded();
            return _sha1Index.TryGetValue(sha1, out var match) ? match : null;
        }

        private void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            if (!Directory.Exists(_datsFolder)) return;

            // Scan .dat (Redump/No-Intro/clrmamepro) and .xml (MAME listinfo) files.
            // The mame2003-plus DAT ships as .xml from libretro/mame2003-plus-libretro;
            // FBNeo's DAT is .dat. Filename (case-insensitive) tells us the source core
            // for arcade routing.
            var files = new List<string>();
            files.AddRange(Directory.EnumerateFiles(_datsFolder, "*.dat"));
            files.AddRange(Directory.EnumerateFiles(_datsFolder, "*.xml"));
            foreach (string datPath in files)
            {
                string fileBase = Path.GetFileNameWithoutExtension(datPath);
                string console = fileBase;
                // NGPC games use the same core and sidebar entry as NGP
                if (console.Equals("NGPC", StringComparison.OrdinalIgnoreCase))
                    console = "NGP";

                // Arcade DAT routing: filename pattern identifies source core.
                //   "Arcade.dat"             → FBNeo arcade games
                //   "mame2003plus.*"         → MAME 2003-Plus arcade games
                //   "mame2003-plus.*"        → MAME 2003-Plus arcade games (alt spelling)
                ArcadeSource? arcadeSource = null;
                if (fileBase.StartsWith("mame2003plus", StringComparison.OrdinalIgnoreCase) ||
                    fileBase.StartsWith("mame2003-plus", StringComparison.OrdinalIgnoreCase))
                {
                    arcadeSource = ArcadeSource.Mame2003Plus;
                    console = "Arcade";
                }
                else if (console.Equals("Arcade", StringComparison.OrdinalIgnoreCase))
                {
                    arcadeSource = ArcadeSource.FBNeo;
                }

                if (console.Equals("NeoGeo", StringComparison.OrdinalIgnoreCase) ||
                    console.Equals("NGP",    StringComparison.OrdinalIgnoreCase) ||
                    console.Equals("NeoCD",  StringComparison.OrdinalIgnoreCase))
                    LoadClrmameproDat(datPath, console);
                else
                    LoadDat(datPath, console, arcadeSource);
            }

            System.Diagnostics.Trace.WriteLine(
                $"[DatMatchService] Loaded {_sha1Index.Count} SHA1 entries, {_neoGeoNameIndex.Count} NeoGeo titles, " +
                $"{_fbneoArcadeNames.Count} FBNeo arcade names, {_mame2003plusArcadeNames.Count} MAME 2003-Plus arcade names from {_datsFolder}");
        }

        /// <summary>Identifies which arcade DAT a game came from, for core routing.</summary>
        private enum ArcadeSource { FBNeo, Mame2003Plus }

        /// <summary>
        /// Parses a standard Redump/No-Intro XML DAT file.
        /// Indexes every &lt;rom&gt; element's sha1 attribute.
        /// </summary>
        private void LoadDat(string path, string console, ArcadeSource? arcadeSource = null)
        {
            try
            {
                bool isArcade = console.Equals("Arcade", StringComparison.OrdinalIgnoreCase);
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
                using var reader = XmlReader.Create(path, settings);

                string? currentGame = null;
                string? currentDescription = null;
                string? currentYear = null;
                string? currentManufacturer = null;

                // Helper to upsert/refresh the ArcadeMeta entry for the current
                // game as each field is parsed. Idempotent — later writes pick up
                // fields that were null on earlier writes.
                void UpsertArcadeMeta()
                {
                    if (!isArcade || currentGame == null
                        || string.IsNullOrWhiteSpace(currentDescription)) return;
                    // Use TryAdd so the first DAT loaded wins for metadata
                    // (FBNeo's descriptions are generally cleaner than MAME's).
                    _arcadeMetaIndex.TryAdd(currentGame, new ArcadeMeta(
                        currentDescription, currentYear, currentManufacturer));
                }

                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        if (reader.Name == "game" || reader.Name == "machine")
                        {
                            currentGame = reader.GetAttribute("name");
                            // Track which arcade DAT each game came from for core routing.
                            if (isArcade && currentGame != null && arcadeSource is ArcadeSource src)
                            {
                                if (src == ArcadeSource.FBNeo)
                                    _fbneoArcadeNames.Add(currentGame);
                                else
                                    _mame2003plusArcadeNames.Add(currentGame);
                            }
                            currentDescription = null;
                            currentYear = null;
                            currentManufacturer = null;
                            continue;
                        }

                        if (reader.Name == "description" && isArcade && currentGame != null)
                        {
                            currentDescription = reader.ReadElementContentAsString();
                            UpsertArcadeMeta();
                            continue;
                        }

                        if (reader.Name == "year" && isArcade && currentGame != null)
                        {
                            currentYear = reader.ReadElementContentAsString();
                            UpsertArcadeMeta();
                            continue;
                        }

                        if (reader.Name == "manufacturer" && isArcade && currentGame != null)
                        {
                            currentManufacturer = reader.ReadElementContentAsString();
                            UpsertArcadeMeta();
                            continue;
                        }

                        if (reader.Name == "rom" && currentGame != null)
                        {
                            string? sha1 = reader.GetAttribute("sha1");
                            if (!string.IsNullOrEmpty(sha1))
                            {
                                // Prefer full description as title for arcade; fall back to game name
                                string title = isArcade && !string.IsNullOrWhiteSpace(currentDescription)
                                    ? currentDescription
                                    : Path.GetFileNameWithoutExtension(currentGame);
                                _sha1Index.TryAdd(sha1, new DatMatch(console, title));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DatMatchService] Failed to load {path}: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses a clrmamepro-format DAT file (used by the SNK - Neo Geo DAT).
        /// Indexes ROM filename (without extension) → game description for title lookup.
        /// </summary>
        private void LoadClrmameproDat(string path, string console)
        {
            try
            {
                bool isNeoGeo = console.Equals("NeoGeo", StringComparison.OrdinalIgnoreCase);
                int sha1Count = 0;
                string? currentDescription = null;
                foreach (string rawLine in File.ReadLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("description ", StringComparison.Ordinal))
                    {
                        int q1 = line.IndexOf('"');
                        int q2 = line.LastIndexOf('"');
                        if (q1 >= 0 && q2 > q1)
                            currentDescription = line.Substring(q1 + 1, q2 - q1 - 1);
                    }
                    else if (line.StartsWith("rom (", StringComparison.Ordinal) ||
                             line.StartsWith("rom(", StringComparison.Ordinal))
                    {
                        // Extract name field: rom ( name "filename.neo" ... )
                        int nameIdx = line.IndexOf("name \"", StringComparison.Ordinal);
                        if (nameIdx >= 0)
                        {
                            int q1 = line.IndexOf('"', nameIdx);
                            int q2 = line.IndexOf('"', q1 + 1);
                            if (q1 >= 0 && q2 > q1)
                            {
                                string romFile = line.Substring(q1 + 1, q2 - q1 - 1);
                                string romName = Path.GetFileNameWithoutExtension(romFile);
                                string title = currentDescription ?? romName;
                                if (isNeoGeo)
                                    _neoGeoNameIndex.TryAdd(romName, title);
                            }
                        }

                        // Extract SHA1 for hash-based identification (NGP, NGPC, etc.)
                        int sha1Idx = line.IndexOf("sha1 ", StringComparison.OrdinalIgnoreCase);
                        if (sha1Idx >= 0)
                        {
                            int start = sha1Idx + 5;
                            int end = start;
                            while (end < line.Length && char.IsLetterOrDigit(line[end])) end++;
                            if (end > start)
                            {
                                string sha1 = line[start..end];
                                string title = currentDescription ?? Path.GetFileNameWithoutExtension(path);
                                _sha1Index.TryAdd(sha1, new DatMatch(console, title));
                                sha1Count++;
                            }
                        }
                    }
                }
                System.Diagnostics.Trace.WriteLine(
                    $"[DatMatchService] Loaded {(isNeoGeo ? $"{_neoGeoNameIndex.Count} NeoGeo titles" : $"{sha1Count} SHA1 entries for {console}")} from {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DatMatchService] Failed to load {path}: {ex.Message}");
            }
        }

        /// <summary>
        /// Maps a short FBNeo ROM name (e.g. "mslug") to its full description title
        /// (e.g. "Metal Slug - Super Vehicle-001") from the Arcade DAT file.
        /// Returns null if no Arcade DAT is loaded or the name isn't found.
        /// </summary>
        public string? LookupArcadeTitle(string romName)
        {
            EnsureLoaded();
            if (!_arcadeMetaIndex.TryGetValue(romName, out var meta)) return null;
            return CleanArcadeTitle(meta.Title);
        }

        /// <summary>
        /// Full arcade metadata (title cleaned, year, manufacturer) for a given
        /// FBNeo shortname. Use this when populating a Game's metadata fields
        /// during import. Returns null if the DAT isn't loaded or the romName
        /// isn't a known FBNeo shortname.
        /// </summary>
        public ArcadeMeta? LookupArcadeMeta(string romName)
        {
            EnsureLoaded();
            if (!_arcadeMetaIndex.TryGetValue(romName, out var meta)) return null;
            return new ArcadeMeta(CleanArcadeTitle(meta.Title), meta.Year, meta.Manufacturer);
        }

        /// <summary>
        /// Returns the preferred libretro core DLL filename for an arcade ROM,
        /// based on which DAT the ROM name appears in. FBNeo wins when both
        /// DATs claim the ROM (better-tuned controls and save state support);
        /// MAME 2003-Plus fills FBNeo's gaps (Atari vector, Sega G-80, Williams
        /// pre-MK, Cinematronics, Gottlieb, Nintendo arcade, etc.).
        ///
        /// Returns null when neither DAT recognises the name — callers should
        /// fall back to the user's preferred-core setting or CoreManager's
        /// default priority order.
        /// </summary>
        public string? GetPreferredArcadeCore(string romName)
        {
            if (string.IsNullOrEmpty(romName)) return null;
            EnsureLoaded();
            if (_fbneoArcadeNames.Contains(romName))        return "fbneo_libretro.dll";
            if (_mame2003plusArcadeNames.Contains(romName)) return "mame2003_plus_libretro.dll";
            return null;
        }

        // FBNeo descriptions are verbose: "Foo / Bar (Region PCB-Code)".
        // For library display we drop the alternate name after " / " and the
        // trailing parenthetical region/PCB info. Subtitle separators like
        // " - The World Warrior" stay — those are part of the actual game name.
        private static string CleanArcadeTitle(string desc)
        {
            int paren = desc.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) desc = desc.Substring(0, paren);
            int slash = desc.IndexOf(" / ", StringComparison.Ordinal);
            if (slash > 0) desc = desc.Substring(0, slash);
            return desc.Trim();
        }

        /// <summary>
        /// Maps a NeoGeo ROM filename (e.g. "samsho") to its full description title
        /// (e.g. "Samurai Shodown / Samurai Spirits") from the NeoGeo DAT file.
        /// Returns null if no NeoGeo DAT is loaded or the name isn't found.
        /// </summary>
        public string? LookupNeoGeoTitle(string romName)
        {
            EnsureLoaded();
            return _neoGeoNameIndex.TryGetValue(romName, out var title) ? title : null;
        }

        /// <summary>True if any DAT files were found and loaded.</summary>
        public bool HasDats
        {
            get
            {
                EnsureLoaded();
                return _sha1Index.Count > 0;
            }
        }
    }
}
