using System.Collections.Generic;

namespace Emutastic.Models.Dos
{
    /// <summary>
    /// One curated DOS game profile, ported from Boxer's GameProfiles.plist
    /// (BXProfileIdentifier / BXProfileTelltales / BXProfileConfigurations).
    ///
    /// Detection is filename-only against <see cref="Telltales"/> — sub-millisecond
    /// hashtable lookup. <see cref="Snippets"/> name entries in the snippets table
    /// of dos-profiles.json; each snippet is a dictionary of DOSBox Pure core option
    /// overrides. Snippets are merged in declaration order so later ones can override
    /// earlier values.
    /// </summary>
    public class DosGameProfile
    {
        /// <summary>Stable id (e.g. "doom-1.9"). Used in gamebox.json so future schema migrations can find this profile again.</summary>
        public string Id { get; set; } = "";

        /// <summary>Human-friendly title used as a fallback when ScreenScraper / folder name don't yield a name.</summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Filename signatures (case-insensitive) used for detection. Hit any one
        /// of these in the dropped folder and this profile is selected.
        /// </summary>
        public List<string> Telltales { get; set; } = new();

        /// <summary>Names of snippets (from <see cref="DosProfileDatabaseFile.Snippets"/>) to merge into the per-game core options.</summary>
        public List<string> Snippets { get; set; } = new();

        /// <summary>
        /// Optional explicit main exe filename. When set, scanner uses this instead
        /// of the heuristic largest-non-utility pick.
        /// </summary>
        public string? PreferredExe { get; set; }

        /// <summary>
        /// Optional list of installer-named filenames in this game's distribution
        /// that should NOT trigger the installer flow (e.g. a game that ships
        /// with a SETUP.EXE that's actually an in-game audio configurator).
        /// </summary>
        public List<string>? IgnoredInstallers { get; set; }
    }

    /// <summary>
    /// Root structure of the embedded dos-profiles.json resource. Validated on load;
    /// any parse failure falls back to <see cref="Generic"/> behavior.
    /// </summary>
    public class DosProfileDatabaseFile
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Named config snippets — key is snippet name, value is DOSBox-Pure-core-option-key → value.</summary>
        public Dictionary<string, Dictionary<string, string>> Snippets { get; set; } = new();

        public List<DosGameProfile> Profiles { get; set; } = new();

        /// <summary>Catch-all profile applied when no telltale matches.</summary>
        public DosGameProfile? Generic { get; set; }
    }

    /// <summary>
    /// Output of <see cref="Services.Dos.DosImporter.Scan(string)"/>. The importer
    /// uses this to decide whether to take the silent fast-path (a known game,
    /// already installed) or surface the installer flow.
    /// </summary>
    public class DosScanResult
    {
        /// <summary>The folder that was scanned (or, for archives, the extracted root).</summary>
        public string ScannedRoot { get; set; } = "";

        /// <summary>
        /// Profile matched by telltale lookup. Null when no profile matched —
        /// importer will use the generic profile and folder-name as the title.
        /// </summary>
        public DosGameProfile? Profile { get; set; }

        /// <summary>
        /// The chosen main exe path (full path under <see cref="ScannedRoot"/>),
        /// or null when only installers exist.
        /// </summary>
        public string? MainExePath { get; set; }

        /// <summary>
        /// Any junk-pattern files we deliberately ignored (DirectX redists,
        /// GOG launcher DLLs, etc.). Useful for diagnostics — not used downstream.
        /// </summary>
        public List<string> IgnoredFiles { get; set; } = new();

        /// <summary>
        /// Installers found in the folder, ordered by preference rank
        /// (best match first). Empty when no installer-shaped exe was seen.
        /// </summary>
        public List<string> DetectedInstallers { get; set; } = new();

        /// <summary>
        /// True when we found installer-shaped exes but no clear "already installed"
        /// telltales — surfaces the installer-pick UI panel for the user.
        /// </summary>
        public bool LooksLikeInstaller => DetectedInstallers.Count > 0 && Profile == null && MainExePath == null;

        /// <summary>
        /// Suggested title for the imported game. Profile title if matched,
        /// else the folder name (cleaned).
        /// </summary>
        public string SuggestedTitle { get; set; } = "";

        /// <summary>
        /// Sibling files / subfolders that look like additional drives to mount
        /// (cd1.iso, disk2.img, etc.). Frontend passes them to DOSBox Pure as
        /// extra mounts when launching.
        /// </summary>
        public List<string> SuggestedMounts { get; set; } = new();
    }
}
