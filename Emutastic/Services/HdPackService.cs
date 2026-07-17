using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Emutastic.Models;

namespace Emutastic.Services
{
    public record HdPackInstallResult(bool Ok, string Message, Game? Entry)
    {
        public static HdPackInstallResult Fail(string message) => new(false, message, null);
    }

    /// <summary>
    /// Enhancement-pack support: Mesen HD packs (NES/FDS) and per-core texture
    /// packs (GameCube/N64/PSP). Installing a pack places its files where the
    /// capable core actually reads them and marks the game itself
    /// (HdPackPath + PreferredCore pin + HdPackEnabled) — no separate library
    /// entry; the pack is a per-game toggle, flipped in-game via the overlay
    /// cog and persisted. Unlike ROM hacks (different game ⇒ own entry), a
    /// pack is the same game with a visual/audio overlay.
    /// </summary>
    public static class HdPackService
    {
        // Consoles whose packs are Mesen HD packs, auto-matchable via the
        // SHA-1 hashes the pack itself declares in hires.txt.
        public static bool IsMesenConsole(string console) =>
            console.Equals("NES", StringComparison.OrdinalIgnoreCase) ||
            console.Equals("FDS", StringComparison.OrdinalIgnoreCase);

        public static bool IsTexturePackConsole(string console) =>
            console.Equals("GameCube", StringComparison.OrdinalIgnoreCase) ||
            console.Equals("N64", StringComparison.OrdinalIgnoreCase) ||
            console.Equals("PSP", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Core options forced at launch for "(HD)" entries. Values verified
        /// against each core's own option definitions (2026-07-16): Mesen and
        /// PPSSPP and our Dolphin fork use enabled/disabled; mupen64plus-next
        /// uses True/False.
        /// </summary>
        public static Dictionary<string, string> ForcedOptionsFor(string console) => console switch
        {
            "NES" or "FDS" => new() { ["mesen_hdpacks"] = "enabled" },
            "GameCube"     => new() { ["dolphin_load_custom_textures"]  = "enabled",
                                      ["dolphin_cache_custom_textures"] = "enabled" },
            "N64"          => new() { ["mupen64plus-txHiresEnable"]     = "True",
                                      ["mupen64plus-EnableTextureCache"] = "True" },
            "PSP"          => new() { ["ppsspp_texture_replacement"]    = "enabled" },
            _              => new()
        };

        /// <summary>Core DLL a pack-installed game gets pinned to (empty = keep console default).</summary>
        public static string PreferredCoreFor(string console) => console switch
        {
            "NES" or "FDS" => "mesen_libretro.dll",
            "N64"          => "mupen64plus_next_libretro.dll", // parallel (default) can't do packs
            _              => ""                               // GameCube/PSP: single core already
        };

        // ── Per-game mod library (Mesen consoles) ────────────────────────────
        // Multiple packs can be installed per game. The ACTIVE one lives where
        // Mesen reads it — System\HdPacks\<rom stem>\ — with a "pack.name"
        // marker; inactive ones are parked at System\HdPacks\_mods\<stem>\<name>\
        // (never scanned by the core: it only checks the <rom stem> folder and
        // loose .zip/.hdn files at the HdPacks root). Switching = folder rename,
        // instant on the same volume; Mesen reloads packs from disk when the
        // mesen_hdpacks flag flips, so the overlay picker can swap mods live.

        private const string ActiveMarker = "pack.name";

        /// <summary>
        /// Highest HD-pack format version the stock libretro Mesen core
        /// understands (HdNesPack::CurrentVersion in the classic 0.9.9 lineage
        /// the buildbot ships). Packs built for Mesen 2 declare &lt;ver&gt;107+
        /// and are SILENTLY rejected by the core at load — surface that at
        /// install/selection time instead of letting them "not work".
        /// </summary>
        public const int MaxSupportedPackVersion = 106;

        private static readonly Regex VerRegex = new(@"<ver>\s*(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static int ParsePackVersion(string hiresText)
            => VerRegex.Match(hiresText) is { Success: true } m
               && int.TryParse(m.Groups[1].Value, out int v) ? v : 0;

        /// <summary>
        /// True when the game's ACTIVE mod declares &lt;patch&gt; entries. Mesen
        /// applies those IPS patches to the ROM internally at load, so the
        /// running game differs from the file rcheevos hashed — RA hardcore
        /// must not credit base-game unlocks from patched code.
        /// </summary>
        public static bool ActiveModHasRomPatch(Game game)
        {
            if (!IsMesenConsole(game.Console)) return false;
            string? stem = RomStemFor(game);
            if (stem == null || ReadActiveName(stem) == null) return false;
            try
            {
                string p = Path.Combine(ActiveModDir(stem), "hires.txt");
                return File.Exists(p) &&
                       File.ReadAllText(p).IndexOf("<patch>", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>Pack format version of an installed mod (0 = no version tag).</summary>
        public static int GetModVersion(Game game, string modName)
        {
            string? stem = RomStemFor(game);
            if (stem == null) return 0;
            string dir = string.Equals(ReadActiveName(stem), modName, StringComparison.OrdinalIgnoreCase)
                ? ActiveModDir(stem)
                : Path.Combine(ModsLibraryDir(stem), SanitizeName(modName));
            try
            {
                string p = Path.Combine(dir, "hires.txt");
                return File.Exists(p) ? ParsePackVersion(File.ReadAllText(p)) : 0;
            }
            catch { return 0; }
        }

        // Wrapper zips are commonly named "UnZipMeFirst<PackName>" — strip that
        // noise from default mod names (the user can rename from the "…" menu).
        private static string CleanModName(string raw)
        {
            string name = Regex.Replace(raw,
                @"^un[\s_\-]*zip[\s_\-]*me[\s_\-]*first[\s_\-]*", "",
                RegexOptions.IgnoreCase);
            name = name.Trim(' ', '_', '-');
            return name.Length == 0 ? raw : name;
        }

        /// <summary>Renames an installed mod (marker rewrite when active, folder
        /// rename in the library otherwise). False on collision or IO failure.</summary>
        public static bool RenameMod(Game game, string oldName, string newName)
        {
            string? stem = RomStemFor(game);
            if (stem == null || string.IsNullOrWhiteSpace(newName)) return false;
            string clean = SanitizeName(newName);
            try
            {
                if (string.Equals(ReadActiveName(stem), oldName, StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(Path.Combine(ActiveModDir(stem), ActiveMarker), clean);
                    return true;
                }
                string src = Path.Combine(ModsLibraryDir(stem), SanitizeName(oldName));
                string dst = Path.Combine(ModsLibraryDir(stem), clean);
                if (!Directory.Exists(src) || Directory.Exists(dst)) return false;
                Directory.Move(src, dst);
                string marker = Path.Combine(dst, ActiveMarker);
                if (File.Exists(marker)) File.WriteAllText(marker, clean);
                return true;
            }
            catch { return false; }
        }

        private static string ActiveModDir(string stem) =>
            Path.Combine(AppPaths.GetFolder("System"), "HdPacks", stem);
        private static string ModsLibraryDir(string stem) =>
            Path.Combine(AppPaths.GetFolder("System"), "HdPacks", "_mods", stem);

        private static string SanitizeName(string name)
        {
            string clean = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            return string.IsNullOrWhiteSpace(clean) ? "Installed pack" : clean.Trim();
        }

        private static string? RomStemFor(Game game)
        {
            string? loadable = ResolveLoadableRom(game);
            return loadable == null ? null : Path.GetFileNameWithoutExtension(loadable);
        }

        private static string? ReadActiveName(string stem)
        {
            string dir = ActiveModDir(stem);
            try
            {
                if (!File.Exists(Path.Combine(dir, "hires.txt"))) return null;
                string marker = Path.Combine(dir, ActiveMarker);
                if (File.Exists(marker))
                {
                    string name = File.ReadAllText(marker).Trim();
                    if (name.Length > 0) return name;
                }
                return "Installed pack"; // pre-marker installs / hand-placed packs
            }
            catch { return null; }
        }

        /// <summary>Active mod name (null = none) and every installed mod for this game.</summary>
        public static (string? Active, List<string> All) ListMods(Game game)
        {
            var all = new List<string>();
            string? stem = RomStemFor(game);
            if (stem == null) return (null, all);

            string? active = ReadActiveName(stem);
            try
            {
                string lib = ModsLibraryDir(stem);
                if (Directory.Exists(lib))
                    all.AddRange(Directory.EnumerateDirectories(lib).Select(d => Path.GetFileName(d)!));
            }
            catch { }
            if (active != null && !all.Contains(active, StringComparer.OrdinalIgnoreCase))
                all.Add(active);
            all.Sort(StringComparer.OrdinalIgnoreCase);
            return (active, all);
        }

        /// <summary>
        /// Makes <paramref name="name"/> the active mod (null = none). Pure
        /// folder renames; call only while the core has the pack UNLOADED
        /// (mesen_hdpacks disabled) or the game closed, so no files are held open.
        /// </summary>
        public static bool ActivateMod(Game game, string? name)
        {
            string? stem = RomStemFor(game);
            return stem != null && ActivateByStem(stem, name);
        }

        private static bool ActivateByStem(string stem, string? name)
        {
            try
            {
                string active = ActiveModDir(stem);
                string lib = ModsLibraryDir(stem);
                string? current = ReadActiveName(stem);

                if (current != null &&
                    string.Equals(current, name, StringComparison.OrdinalIgnoreCase))
                    return true; // already active

                // Park the current pack back into the library.
                if (current != null)
                {
                    Directory.CreateDirectory(lib);
                    string dest = Path.Combine(lib, SanitizeName(current));
                    if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                    Directory.Move(active, dest);
                }
                else if (Directory.Exists(active))
                {
                    Directory.Delete(active, recursive: true); // leftover junk, no hires.txt
                }

                // Promote the chosen one.
                if (name != null)
                {
                    string src = Path.Combine(lib, SanitizeName(name));
                    if (!Directory.Exists(src)) return false;
                    Directory.Move(src, active);
                    File.WriteAllText(Path.Combine(active, ActiveMarker), name);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[HdPack] ActivateMod failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Any mods installed for this game? Mesen consoles answer from the
        /// FILESYSTEM (mod library + active folder) — deliberately not from DB
        /// columns, so packs installed by older builds, by hand, or against a
        /// since-rearranged library still light everything up. Texture consoles
        /// answer from the DB column (their files live in core-owned folders).
        /// </summary>
        public static bool ModsExist(Game game)
        {
            if (!IsMesenConsole(game.Console)) return game.HasHdPack;
            var (active, all) = ListMods(game);
            return active != null || all.Count > 0;
        }

        /// <summary>
        /// Whether this game should launch on its pack-capable core with the pack
        /// options on: Mesen consoles → a mod is active on disk; texture consoles
        /// → the persisted per-game toggle.
        /// </summary>
        public static bool WantsPackCore(Game game)
        {
            if (IsMesenConsole(game.Console))
            {
                string? stem = RomStemFor(game);
                return stem != null && ReadActiveName(stem) != null;
            }
            return game.HasHdPack && game.HdPackEnabled;
        }

        /// <summary>
        /// Core options to force at launch for a pack-installed game. Mesen
        /// consoles always force the pack flag ON when mods exist on disk — the
        /// active-folder's presence decides whether anything renders, and keeping
        /// the flag on lets the overlay picker swap mods live. Texture consoles
        /// force on/off from the per-game toggle (their cores default some
        /// options on).
        /// </summary>
        public static Dictionary<string, string> GetLaunchForcedOptions(Game game)
        {
            if (IsMesenConsole(game.Console))
                return ModsExist(game) ? ForcedOptionsFor(game.Console) : new();
            if (!game.HasHdPack) return new();
            var forced = ForcedOptionsFor(game.Console);
            if (game.HdPackEnabled) return forced;
            return forced.ToDictionary(kv => kv.Key,
                kv => kv.Value == "True" ? "False" : "disabled");
        }

        // ── Archive sniffing ─────────────────────────────────────────────────

        private static readonly string[] ArchiveExts = { ".zip", ".7z", ".rar", ".hdn" };

        /// <summary>
        /// True when the archive contains a Mesen HD pack — hires.txt at any
        /// depth, or (packs are commonly distributed as a release zip wrapping
        /// the real pack zip plus a readme) inside one nested archive level.
        /// </summary>
        public static bool IsMesenHdPackArchive(string archivePath)
        {
            string? resolved = ResolvePackArchive(archivePath);
            if (resolved == null) return false;
            CleanupIfTemp(resolved, archivePath);
            return true;
        }

        /// <summary>
        /// Returns a path to an archive that DIRECTLY contains hires.txt: the
        /// input itself, or a temp-extracted inner archive (one nesting level).
        /// Callers must CleanupIfTemp() the result. Real distributions wrap the
        /// pack zip alongside dozens of loose extras ("UnZipMeFirst…" style), so
        /// the nested probe is gated on the count/size of INNER ARCHIVES only
        /// (≤4 probed, ≤512 MB each) — never on the outer file count. Archives
        /// with no nested archive entries (the bulk-import common case) pay
        /// nothing beyond the entry listing.
        /// </summary>
        private static string? ResolvePackArchive(string archivePath)
        {
            try
            {
                using var archive = Archives.RomArchive.Open(archivePath);
                var files = archive.Entries.Where(e => !e.IsDirectory && e.Key != null).ToList();
                if (files.Any(e => NormalizeKey(e.Key!).EndsWith("hires.txt", StringComparison.OrdinalIgnoreCase)))
                    return archivePath;

                var inners = files
                    .Select(f => (Key: NormalizeKey(f.Key!), f.Size))
                    .Where(f => ArchiveExts.Any(x => f.Key.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                    .Where(f => f.Size is > 0 and < 512L * 1024 * 1024)
                    .OrderByDescending(f => f.Size) // the pack zip dwarfs readme-sized extras
                    .Take(4)
                    .ToList();

                foreach (var inner in inners)
                {
                    string temp = Path.Combine(Path.GetTempPath(),
                        "Emutastic-pack-" + Path.GetFileName(inner.Key.Split('/')[^1]));
                    try
                    {
                        var entry = archive.Entries.First(e => !e.IsDirectory && e.Key != null &&
                            NormalizeKey(e.Key!) == inner.Key);
                        using (var dst = File.Create(temp))
                            entry.ExtractTo(dst);
                        using var innerArc = Archives.RomArchive.Open(temp);
                        if (innerArc.Entries.Any(e => !e.IsDirectory && e.Key != null &&
                            NormalizeKey(e.Key!).EndsWith("hires.txt", StringComparison.OrdinalIgnoreCase)))
                            return temp;
                    }
                    catch { }
                    try { File.Delete(temp); } catch { }
                }
            }
            catch { }
            return null;
        }

        private static void CleanupIfTemp(string resolved, string original)
        {
            if (!string.Equals(resolved, original, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(resolved); } catch { }
        }

        // ── Mesen HD pack install (NES/FDS) ──────────────────────────────────

        public static Task<HdPackInstallResult> InstallMesenPackAsync(
            string archivePath, DatabaseService db, IReadOnlyList<Game> library,
            Game? explicitTarget = null)
            => Task.Run(() => InstallMesenPack(archivePath, db, library, explicitTarget));

        private static HdPackInstallResult InstallMesenPack(
            string archivePath, DatabaseService db, IReadOnlyList<Game> library,
            Game? explicitTarget)
        {
            // Distribution zips often wrap the actual pack zip — resolve to the
            // archive that directly contains hires.txt (may be a temp file).
            string? packArchive = ResolvePackArchive(archivePath);
            if (packArchive == null)
                return HdPackInstallResult.Fail("No hires.txt found — this isn't a Mesen HD pack.");
            try
            {
                // Mod display name: the inner pack zip's own name when the user
                // grabbed a wrapper ("UnZipMeFirst…" distributions), else the
                // archive they picked. Temp inner files carry an "Emutastic-pack-"
                // prefix — strip it back off for display.
                string displayName = Path.GetFileNameWithoutExtension(packArchive);
                if (!string.Equals(packArchive, archivePath, StringComparison.OrdinalIgnoreCase) &&
                    displayName.StartsWith("Emutastic-pack-", StringComparison.Ordinal))
                    displayName = displayName["Emutastic-pack-".Length..];
                displayName = CleanModName(displayName);
                return InstallMesenPackCore(packArchive, db, library, explicitTarget, displayName);
            }
            finally
            {
                CleanupIfTemp(packArchive, archivePath);
            }
        }

        private static HdPackInstallResult InstallMesenPackCore(
            string archivePath, DatabaseService db, IReadOnlyList<Game> library,
            Game? explicitTarget, string displayName)
        {
            try
            {
                using var archive = Archives.RomArchive.Open(archivePath);
                var files = archive.Entries.Where(e => !e.IsDirectory && e.Key != null).ToList();

                // Shallowest hires.txt wins; everything alongside it is the pack.
                var hiresEntry = files
                    .Where(e => NormalizeKey(e.Key!).EndsWith("hires.txt", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => NormalizeKey(e.Key!).Count(c => c == '/'))
                    .FirstOrDefault();
                if (hiresEntry == null)
                    return HdPackInstallResult.Fail("No hires.txt found — this isn't a Mesen HD pack.");

                string hiresKey = NormalizeKey(hiresEntry.Key!);
                string prefix = hiresKey.Length > "hires.txt".Length
                    ? hiresKey[..^"hires.txt".Length]   // e.g. "Zelda Remastered/"
                    : "";

                string hiresText;
                using (var hs = hiresEntry.OpenEntryStream())
                using (var reader = new StreamReader(hs))
                    hiresText = reader.ReadToEnd();

                // Packs built for Mesen 2 (format v107+) can't render on the classic
                // libretro core — they run through the external MesenCE emulator
                // instead (launch routing handles it). Install them either way and
                // say which path they'll take.
                int packVer = ParsePackVersion(hiresText);
                string mesen2Note = "";
                if (packVer > MaxSupportedPackVersion)
                    mesen2Note = MesenCe.MesenCeRuntime.IsInstalled()
                        ? " This is a Mesen 2 pack — the game launches through Mesen 2 while it's active."
                        : " This is a Mesen 2 pack — install Mesen 2 (Preferences → Cores → NES — Mesen 2) to use it.";

                // The pack declares the ROMs it supports as full-file SHA-1 hashes
                // (Mesen convention: SHA1 of the complete file, iNES header included).
                var supported = ParseSupportedRomHashes(hiresText);

                // Resolve the target game + the file the core will actually load.
                Game? target = explicitTarget;
                string? loadable = null;
                string mismatchNote = "";
                if (target != null)
                {
                    loadable = ResolveLoadableRom(target);
                    if (loadable == null)
                        return HdPackInstallResult.Fail($"The ROM file for '{target.Title}' couldn't be found.");

                    // Parity with the ROM-hack flow's source-CRC validation: when
                    // the pack declares the ROMs it supports and this game's dump
                    // isn't among them, install anyway (folder-form packs load
                    // regardless, and authors don't always list every revision)
                    // but say so — mismatched revisions show broken tiles.
                    if (supported.Count > 0)
                    {
                        string? sha1 = Sha1OfFile(loadable);
                        if (sha1 == null || !supported.Contains(sha1))
                            mismatchNote = " Note: the pack declares support for a different ROM dump — if graphics look wrong, this ROM revision may not match.";
                    }
                }
                else
                {
                    // Games that already have a pack stay in the candidate set so
                    // re-importing a newer pack version updates them in place.
                    foreach (var g in library.Where(g => IsMesenConsole(g.Console)))
                    {
                        string? candidate = ResolveLoadableRom(g);
                        if (candidate == null) continue;
                        string? sha1 = Sha1OfFile(candidate);
                        if (sha1 != null && supported.Contains(sha1))
                        {
                            target = g;
                            loadable = candidate;
                            break;
                        }
                    }
                    if (target == null || loadable == null)
                        return HdPackInstallResult.Fail(supported.Count > 0
                            ? "This HD pack doesn't match any NES/FDS game in your library. Import the base ROM first, or right-click the game and choose Install HD Pack."
                            : "This HD pack doesn't declare which ROM it supports. Right-click the game it belongs to and choose Install HD Pack.");
                }

                // Install into this game's mod library, then make it the active
                // mod (Mesen matches the active folder by the loaded file's name:
                // System\HdPacks\<rom filename stem>\hires.txt).
                string stem = Path.GetFileNameWithoutExtension(loadable);
                string packName = SanitizeName(displayName);

                // Re-installing the currently active pack: park it first so the
                // fresh files land in the library and re-activation adopts them.
                string? currentActive = ReadActiveName(stem);
                if (currentActive != null &&
                    string.Equals(SanitizeName(currentActive), packName, StringComparison.OrdinalIgnoreCase))
                    ActivateByStem(stem, null);

                string libDir = Path.Combine(ModsLibraryDir(stem), packName);
                if (Directory.Exists(libDir)) Directory.Delete(libDir, recursive: true);
                ExtractUnderPrefix(files, prefix, libDir);

                if (!ActivateByStem(stem, packName))
                    return HdPackInstallResult.Fail("The pack was extracted but couldn't be activated (files in use?). Try again with the game closed.");

                return FinishInstall(db, library, target, ActiveModDir(stem),
                    $"HD mod '{packName}' installed for '{target.Title}'{mismatchNote}{mesen2Note}");
            }
            catch (Exception ex)
            {
                return HdPackInstallResult.Fail($"HD pack install failed: {ex.Message}");
            }
        }

        // ── Texture pack install (GameCube / N64 / PSP) ──────────────────────

        public static Task<HdPackInstallResult> InstallTexturePackAsync(
            string archivePath, DatabaseService db, IReadOnlyList<Game> library, Game target)
            => Task.Run(() => InstallTexturePack(archivePath, db, library, target));

        private static HdPackInstallResult InstallTexturePack(
            string archivePath, DatabaseService db, IReadOnlyList<Game> library, Game target)
        {
            try
            {
                using var archive = Archives.RomArchive.Open(archivePath);
                var files = archive.Entries.Where(e => !e.IsDirectory && e.Key != null).ToList();
                if (files.Count == 0) return HdPackInstallResult.Fail("The archive is empty.");

                switch (target.Console)
                {
                    case "GameCube":
                    {
                        // Dolphin reads <User>/Load/Textures/<GameID>/. Prefer the ID
                        // folder the pack ships; fall back to the disc header (first
                        // 6 bytes of .iso/.gcm).
                        string? gameId = FindIdFolder(files, GcIdRegex) ?? ReadGcGameId(target.RomPath);
                        if (gameId == null)
                            return HdPackInstallResult.Fail(
                                "Couldn't determine the GameCube game ID (pack has no ID folder and the disc header isn't readable). Rename the pack's top folder to the game ID (e.g. GZLE01) and try again.");
                        string userDir = Path.Combine(AppPaths.GetFolder("BatterySaves", "GameCube"), "User");
                        string dest = Path.Combine(userDir, "Load", "Textures", gameId);
                        ExtractUnderPrefix(files, FolderPrefixFor(files, gameId), dest);
                        return FinishInstall(db, library, target, dest,
                            $"Texture pack installed for '{target.Title}' ({gameId})");
                    }

                    case "N64":
                    {
                        // GLideN64: pre-compiled .htc/.hts go to Mupen64plus/cache/,
                        // PNG trees to Mupen64plus/hires_texture/ — both keyed by the
                        // ROM's internal name, which pack authors bake into filenames.
                        string root = Path.Combine(AppPaths.GetFolder("System"), "Mupen64plus");
                        var compiled = files.Where(f =>
                        {
                            string k = NormalizeKey(f.Key!);
                            return k.EndsWith(".htc", StringComparison.OrdinalIgnoreCase)
                                || k.EndsWith(".hts", StringComparison.OrdinalIgnoreCase);
                        }).ToList();

                        string dest;
                        if (compiled.Count > 0)
                        {
                            dest = Path.Combine(root, "cache");
                            Directory.CreateDirectory(dest);
                            foreach (var f in compiled)
                                ExtractSingle(f, Path.Combine(dest, Path.GetFileName(NormalizeKey(f.Key!))));
                        }
                        else
                        {
                            // PNG form: keep everything below "hires_texture/" if the
                            // pack has that wrapper, else take the pack's root folder
                            // as the game folder GLideN64 expects.
                            string? wrapped = files
                                .Select(f => NormalizeKey(f.Key!))
                                .Where(k => k.Contains("hires_texture/", StringComparison.OrdinalIgnoreCase))
                                .Select(k => k[..(k.IndexOf("hires_texture/", StringComparison.OrdinalIgnoreCase) + "hires_texture/".Length)])
                                .OrderBy(p => p.Length)
                                .FirstOrDefault();
                            dest = Path.Combine(root, "hires_texture");
                            ExtractUnderPrefix(files, wrapped ?? "", dest);
                        }
                        return FinishInstall(db, library, target, dest,
                            $"Texture pack installed for '{target.Title}'");
                    }

                    case "PSP":
                    {
                        // PPSSPP reads <saves>/PSP/TEXTURES/<GameID>/ (the core builds
                        // the PSP/ tree inside the save directory we hand it).
                        string? gameId = FindIdFolder(files, PspIdRegex);
                        if (gameId == null)
                            return HdPackInstallResult.Fail(
                                "Couldn't determine the PSP game ID from the pack. Rename the pack's top folder to the game ID (e.g. ULUS10041) and try again.");
                        string dest = Path.Combine(AppPaths.GetFolder("BatterySaves", "PSP"),
                            "PSP", "TEXTURES", gameId);
                        ExtractUnderPrefix(files, FolderPrefixFor(files, gameId), dest);
                        return FinishInstall(db, library, target, dest,
                            $"Texture pack installed for '{target.Title}' ({gameId})");
                    }

                    default:
                        return HdPackInstallResult.Fail($"Texture packs aren't supported for {target.Console}.");
                }
            }
            catch (Exception ex)
            {
                return HdPackInstallResult.Fail($"Texture pack install failed: {ex.Message}");
            }
        }

        // ── Shared plumbing ──────────────────────────────────────────────────

        private static HdPackInstallResult FinishInstall(
            DatabaseService db, IReadOnlyList<Game> library, Game target, string packDir,
            string message)
        {
            // In-place model: the pack belongs to the game itself. Pin the
            // pack-capable core, remember the pack location, and enable it —
            // the in-game overlay picker/toggle takes it from here.
            string preferred = PreferredCoreFor(target.Console);
            if (preferred.Length > 0 &&
                !string.Equals(target.PreferredCore, preferred, StringComparison.OrdinalIgnoreCase))
            {
                db.UpdatePreferredCore(target.Id, preferred);
                target.PreferredCore = preferred;
            }
            db.UpdateHdPackPath(target.Id, packDir);
            db.UpdateHdPackEnabled(target.Id, true);
            target.HdPackPath = packDir;
            target.HdPackEnabled = true;

            string coreHint = "";
            if (preferred.Length > 0 &&
                !File.Exists(Path.Combine(AppPaths.GetCoresFolder(), preferred)))
            {
                coreHint = $" Install the {(IsMesenConsole(target.Console) ? "Mesen" : "Mupen64Plus-Next")} core from Preferences → Cores to use it.";
            }
            string tail = IsMesenConsole(target.Console)
                ? "switch mods in-game from the cog menu (HD Mod)."
                : "toggle it in-game from the cog menu (HD Pack: On/Off).";
            return new HdPackInstallResult(true, $"{message} — {tail}{coreHint}", target);
        }

        private static string NormalizeKey(string key) => key.Replace('\\', '/').TrimStart('/');

        // Extract every archive file under `prefix` into destDir, preserving
        // the remaining relative structure. Existing files are overwritten so
        // re-installing a newer pack version updates in place.
        private static void ExtractUnderPrefix(
            IEnumerable<Archives.IRomArchiveEntry> files, string prefix, string destDir)
        {
            foreach (var f in files)
            {
                string key = NormalizeKey(f.Key!);
                if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                string rel = prefix.Length > 0 ? key[prefix.Length..] : key;
                if (rel.Length == 0) continue;
                // Guard against zip-slip: no rooted or parent-escaping entries.
                if (rel.Contains("..")) continue;
                ExtractSingle(f, Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        private static void ExtractSingle(Archives.IRomArchiveEntry entry, string destPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var src = entry.OpenEntryStream();
            using var dst = File.Create(destPath);
            src.CopyTo(dst);
        }

        private static readonly Regex GcIdRegex  = new("^[A-Z0-9]{6}$", RegexOptions.Compiled);
        private static readonly Regex PspIdRegex = new("^[A-Z]{4}[0-9]{5}$", RegexOptions.Compiled);
        private static readonly Regex Sha1Regex  = new("[0-9a-fA-F]{40}", RegexOptions.Compiled);

        // First directory segment in the archive that looks like a game ID.
        private static string? FindIdFolder(
            IEnumerable<Archives.IRomArchiveEntry> files, Regex idPattern)
        {
            foreach (var f in files)
            {
                foreach (var seg in NormalizeKey(f.Key!).Split('/')[..^1])
                    if (idPattern.IsMatch(seg))
                        return seg.ToUpperInvariant();
            }
            return null;
        }

        // Prefix up to and including "<id>/" so pack contents land directly in
        // the destination ID folder (avoids Textures/GZLE01/GZLE01/…).
        private static string FolderPrefixFor(
            IEnumerable<Archives.IRomArchiveEntry> files, string idFolder)
        {
            foreach (var f in files)
            {
                string key = NormalizeKey(f.Key!);
                int idx = key.IndexOf(idFolder + "/", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) return key[..(idx + idFolder.Length + 1)];
            }
            return "";
        }

        // GameCube disc header: the game ID is the first 6 bytes of .iso/.gcm.
        // Compressed formats (.rvz etc.) aren't readable this way — callers fall
        // back to the pack's own ID folder or ask the user to provide one.
        private static string? ReadGcGameId(string romPath)
        {
            try
            {
                string ext = Path.GetExtension(romPath).ToLowerInvariant();
                if (ext != ".iso" && ext != ".gcm") return null;
                Span<byte> id = stackalloc byte[6];
                using var fs = File.OpenRead(romPath);
                if (fs.Read(id) != 6) return null;
                string s = System.Text.Encoding.ASCII.GetString(id);
                return s.All(char.IsLetterOrDigit) ? s.ToUpperInvariant() : null;
            }
            catch { return null; }
        }

        private static HashSet<string> ParseSupportedRomHashes(string hiresText)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in hiresText.Split('\n'))
            {
                if (line.IndexOf("<supportedRom>", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                foreach (Match m in Sha1Regex.Matches(line))
                    set.Add(m.Value);
            }
            return set;
        }

        // The file the core will actually load: the ROM itself, or the entry
        // extracted from its archive (same ZipRomExtractor path the launch uses,
        // so the resulting filename stem matches what Mesen sees).
        private static string? ResolveLoadableRom(Game g)
        {
            try
            {
                string raw = g.RomPath;
                if (string.IsNullOrEmpty(raw)) return null;
                string ext = Path.GetExtension(raw);
                if (ZipRomExtractor.IsArchiveExtension(ext) && ZipRomExtractor.ConsoleNeedsExtraction(g.Console))
                {
                    string? extracted = ZipRomExtractor.ExtractSync(raw, g.Console);
                    if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted)) return extracted;
                    return null;
                }
                return File.Exists(raw) ? raw : null;
            }
            catch { return null; }
        }

        private static string? Sha1OfFile(string path)
        {
            try
            {
                // Packs target cartridge ROMs — skip anything implausibly large.
                if (new FileInfo(path).Length > 64 * 1024 * 1024) return null;
                using var fs = File.OpenRead(path);
                return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(fs)).ToLowerInvariant();
            }
            catch { return null; }
        }
    }
}
