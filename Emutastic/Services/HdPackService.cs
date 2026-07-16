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
    /// capable core actually reads them and creates an "(HD)" library entry —
    /// the base entry stays vanilla, mirroring how ROM-hack entries work.
    ///
    /// The "(HD)" entry shares the base game's RomHash on purpose: the ROM
    /// bytes are identical (packs are render/audio-time overlays, not patches),
    /// so battery saves, artwork and RetroAchievements identity carry over.
    /// Per-entry behaviour comes from PreferredCore (pinned at install) plus
    /// ForcedOptionsFor (applied at launch when Game.HasHdPack).
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

        /// <summary>Core DLL an "(HD)" entry gets pinned to (empty = keep console default).</summary>
        public static string PreferredCoreFor(string console) => console switch
        {
            "NES" or "FDS" => "mesen_libretro.dll",
            "N64"          => "mupen64plus_next_libretro.dll", // parallel (default) can't do packs
            _              => ""                               // GameCube/PSP: single core already
        };

        // ── Archive sniffing ─────────────────────────────────────────────────

        /// <summary>True when the archive contains a Mesen HD pack (hires.txt at any depth).</summary>
        public static bool IsMesenHdPackArchive(string archivePath)
        {
            try
            {
                using var archive = Archives.RomArchive.Open(archivePath);
                return archive.Entries.Any(e => !e.IsDirectory && e.Key != null &&
                    NormalizeKey(e.Key).EndsWith("hires.txt", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
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
                    foreach (var g in library.Where(g => IsMesenConsole(g.Console) && !g.HasHdPack))
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

                // Mesen matches folder-form packs by the loaded file's name:
                // System\HdPacks\<rom filename stem>\hires.txt
                string stem = Path.GetFileNameWithoutExtension(loadable);
                string destDir = Path.Combine(AppPaths.GetFolder("System"), "HdPacks", stem);
                ExtractUnderPrefix(files, prefix, destDir);

                return FinishInstall(db, library, target, destDir,
                    $"HD pack installed for '{target.Title}'");
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
            // Re-install onto an existing "(HD)" entry just refreshes the files.
            var existing = library.FirstOrDefault(g =>
                !string.IsNullOrEmpty(g.HdPackPath) &&
                string.Equals(Path.GetFullPath(g.HdPackPath), Path.GetFullPath(packDir),
                    StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return new HdPackInstallResult(true, $"{message} — updated existing entry '{existing.Title}'.", existing);

            var entry = new Game
            {
                Title           = $"{target.Title} (HD)",
                Console         = target.Console,
                Manufacturer    = target.Manufacturer,
                Year            = target.Year,
                RomPath         = target.RomPath,
                // Same bytes, same game: sharing RomHash keeps battery saves,
                // artwork and RetroAchievements identity in sync with the base.
                RomHash         = target.RomHash,
                Developer       = target.Developer,
                Publisher       = target.Publisher,
                Genre           = target.Genre,
                Description     = target.Description,
                BackgroundColor = target.BackgroundColor,
                AccentColor     = target.AccentColor,
                PreferredCore   = PreferredCoreFor(target.Console),
            };
            db.InsertGame(entry);
            db.UpdateHdPackPath(entry.Id, packDir);
            entry.HdPackPath = packDir;

            string coreHint = "";
            string preferred = PreferredCoreFor(target.Console);
            if (preferred.Length > 0 &&
                !File.Exists(Path.Combine(AppPaths.GetCoresFolder(), preferred)))
            {
                coreHint = $" Install the {(IsMesenConsole(target.Console) ? "Mesen" : "Mupen64Plus-Next")} core from Preferences → Cores to play it.";
            }
            return new HdPackInstallResult(true, $"{message} — new entry '{entry.Title}'.{coreHint}", entry);
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
