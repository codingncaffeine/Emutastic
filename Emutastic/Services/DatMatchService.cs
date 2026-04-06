using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace Emutastic.Services
{
    /// <summary>
    /// Loads Redump/No-Intro DAT files from [exe]\DATs\ and provides hash-based
    /// game identification.  DAT files must be in standard Redump/No-Intro XML format
    /// and named after their console tag (e.g. Saturn.dat, SegaCD.dat, PS1.dat, TGCD.dat).
    ///
    /// DATs are loaded lazily on first lookup and cached for the session.
    /// </summary>
    public class DatMatchService
    {
        public record DatMatch(string Console, string Title);

        // sha1 (lowercase hex) → DatMatch
        private readonly Dictionary<string, DatMatch> _sha1Index = new(StringComparer.OrdinalIgnoreCase);

        // Arcade short ROM name (e.g. "mslug") → full title (e.g. "Metal Slug - Super Vehicle-001")
        private readonly Dictionary<string, string> _arcadeNameIndex = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _datsFolder;
        private bool _loaded = false;

        public DatMatchService()
        {
            _datsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DATs");
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

            foreach (string datPath in Directory.EnumerateFiles(_datsFolder, "*.dat"))
            {
                string console = Path.GetFileNameWithoutExtension(datPath);
                LoadDat(datPath, console);
            }

            System.Diagnostics.Trace.WriteLine(
                $"[DatMatchService] Loaded {_sha1Index.Count} entries from {_datsFolder}");
        }

        /// <summary>
        /// Parses a standard Redump/No-Intro XML DAT file.
        /// Indexes every &lt;rom&gt; element's sha1 attribute.
        /// </summary>
        private void LoadDat(string path, string console)
        {
            try
            {
                bool isArcade = console.Equals("Arcade", StringComparison.OrdinalIgnoreCase);
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
                using var reader = XmlReader.Create(path, settings);

                string? currentGame = null;
                string? currentDescription = null;

                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        if (reader.Name == "game" || reader.Name == "machine")
                        {
                            currentGame = reader.GetAttribute("name");
                            currentDescription = null;
                            continue;
                        }

                        if (reader.Name == "description" && isArcade && currentGame != null)
                        {
                            currentDescription = reader.ReadElementContentAsString();
                            // Index short name → full title for Libretro thumbnail lookup
                            if (!string.IsNullOrWhiteSpace(currentDescription))
                                _arcadeNameIndex.TryAdd(currentGame, currentDescription);
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
        /// Maps a short FBNeo ROM name (e.g. "mslug") to its full description title
        /// (e.g. "Metal Slug - Super Vehicle-001") from the Arcade DAT file.
        /// Returns null if no Arcade DAT is loaded or the name isn't found.
        /// </summary>
        public string? LookupArcadeTitle(string romName)
        {
            EnsureLoaded();
            return _arcadeNameIndex.TryGetValue(romName, out var title) ? title : null;
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
