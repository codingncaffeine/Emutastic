using Emutastic.Models;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Emutastic.Services
{
    /// <summary>
    /// The Windows platform: programs and shortcuts kept in the library and started directly
    /// (no core). Answers which files count as a Windows app, what to call one, which program a
    /// dropped folder stands for, and how an app is identified.
    ///
    /// Before this platform existed, the console-nav import shortcut (written for the removed DOS
    /// platform) filed any dropped file under whatever console was selected — including .exe
    /// files, which then "worked" from the wrong console. Windows programs now always land here.
    /// </summary>
    public static class WindowsApps
    {
        public const string ConsoleTag = "Windows";
        public const string Manufacturer = "Microsoft";

        public static bool IsWindows(string? console)
            => string.Equals(console, ConsoleTag, StringComparison.OrdinalIgnoreCase);

        private static readonly HashSet<string> ShortcutExtensions = new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url" };
        private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase) { ".bat", ".cmd" };

        /// <summary>Extensions offered by the import dialog's Windows filter.</summary>
        public const string DialogFilter = "*.exe;*.lnk;*.url;*.bat;*.cmd";

        /// <summary>
        /// True for a file the Windows platform imports: a Windows program, a shortcut, or a batch
        /// file. An .exe must be a real Windows program — PlayStation homebrew uses .exe too
        /// (PS-X EXE), and those belong to PS1.
        /// </summary>
        public static bool IsAppFile(string path)
        {
            string ext = Path.GetExtension(path);
            if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return IsWindowsProgram(path);
            return ShortcutExtensions.Contains(ext) || ScriptExtensions.Contains(ext);
        }

        public static bool IsShortcut(string path) => ShortcutExtensions.Contains(Path.GetExtension(path));

        /// <summary>
        /// True when the file is a Windows executable image: an MZ header whose e_lfanew points at a
        /// "PE\0\0" signature, flagged executable and not a DLL. DOS-only (MZ without PE) and 16-bit
        /// programs don't run on 64-bit Windows, so they don't qualify.
        /// </summary>
        public static bool IsWindowsProgram(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> dos = stackalloc byte[64];
                if (fs.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) < dos.Length) return false;
                if (dos[0] != (byte)'M' || dos[1] != (byte)'Z') return false;

                int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.Slice(0x3C, 4));
                if (peOffset < dos.Length || peOffset > fs.Length - 24) return false;

                fs.Position = peOffset;
                Span<byte> pe = stackalloc byte[24];   // signature + IMAGE_FILE_HEADER
                if (fs.ReadAtLeast(pe, pe.Length, throwOnEndOfStream: false) < pe.Length) return false;
                if (pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0) return false;

                const ushort ExecutableImage = 0x0002, Dll = 0x2000;
                ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(pe.Slice(4 + 18, 2));
                return (characteristics & ExecutableImage) != 0 && (characteristics & Dll) == 0;
            }
            catch
            {
                return false;
            }
        }

        // ── Titles ──────────────────────────────────────────────────────────────────────────

        // Version-resource names that describe the engine or a stub rather than the app.
        private static readonly HashSet<string> GenericProgramNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "launcher", "game", "game launcher", "application", "app", "main", "client", "game client",
            "setup", "installer", "install", "unity player", "unityplayer", "bootstrappackagedgame",
            "bootstrap", "ue4 game", "ue4game", "ue5 game", "unreal engine", "win64 shipping", "shipping",
            "electron", "nw.js", "node.js", "java(tm) platform se binary", "openjdk platform binary",
            "python", "pythonw", "godot engine", "love", "löve", "rgss player", "game player",
        };

        /// <summary>
        /// Library title for an app file: a shortcut or batch file by its own name (the name the
        /// user sees on their desktop); a program by the description in its version resource,
        /// falling back to its file name.
        /// </summary>
        public static string TitleFor(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // Explorer names a new shortcut "<target> - Shortcut".
                string name = CleanName(Regex.Replace(stem, @"\s+-\s+Shortcut$", "", RegexOptions.IgnoreCase));
                return name.Length > 0 ? name : stem;
            }

            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                foreach (string? candidate in new[] { info.FileDescription, info.ProductName })
                {
                    string name = CleanName(candidate);
                    if (IsUsefulProgramName(name, stem)) return name;
                }
            }
            catch { /* no readable version resource */ }

            // A file name such as "hollow_knight": spaces for separators, and title case when it
            // has no capitals of its own.
            string fromFile = CleanName(stem.Replace('_', ' '));
            if (fromFile.Length == 0) return stem;
            return fromFile.Any(char.IsUpper)
                ? fromFile
                : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(fromFile);
        }

        private static string CleanName(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Replace("™", "").Replace("®", "").Replace("©", "").Replace("(TM)", "").Replace("(R)", "");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        private static bool IsUsefulProgramName(string name, string fileStem)
        {
            if (name.Length < 2) return false;
            if (GenericProgramNames.Contains(name)) return false;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            // Windows' own programs carry the OS as their product name.
            if (name.Contains("Operating System", StringComparison.OrdinalIgnoreCase)) return false;
            // A description that only repeats a cryptic file name adds nothing.
            return !string.Equals(name, fileStem, StringComparison.OrdinalIgnoreCase) || !fileStem.Contains('_');
        }

        // ── Folders ─────────────────────────────────────────────────────────────────────────

        // Programs that come with an app but aren't it: installers, redistributables, crash
        // reporters, updaters, anti-cheat, configuration tools and bundled runtimes.
        private static readonly Regex HelperProgram = new(
            @"^(unins\d*|uninstall.*|.*uninstaller.*|setup.*|.*setup|.*installer.*|install|" +
            @"vc_?redist.*|.*redist.*|dxsetup|dxwebsetup|directx.*|dotnet.*|ndp\d.*|oalinst|physx.*|" +
            @"ue[45]?prereqsetup.*|.*crash(handler|report|reporter|sender|uploader|dump|pad).*|bugsplat.*|" +
            @".*report|.*reporter|.*updater?|.*update|.*patcher|" +
            @".*helper|.*service|.*svc|.*config|.*configurator|.*settings|easyanticheat.*|eac_?launcher.*|" +
            @"beservice.*|battleye.*|cefsharp.*|.*subprocess.*|.*webhelper.*|notification_helper|" +
            @"unitycrashhandler(32|64)?|crashpad_handler|dosbox.*|scummvm|7z.*|unrar|python\w*|java\w*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Shortcuts in a folder that point at documentation or a web page rather than an app.
        private static readonly Regex ReferenceShortcut = new(
            @"uninstall|website|web site|homepage|home page|support|manual|readme|read me|forum|register|" +
            @"\bhelp\b|documentation|eula|licen[cs]e|changelog|release notes|discord|twitter|facebook|patreon",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Folders that hold an app's prerequisites, never the app.
        private static readonly HashSet<string> SupportFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "redist", "redists", "redistributables", "_commonredist", "commonredist", "directx", "vcredist",
            "dotnet", "support", "__installer", "installer", "prereqs", "prerequisites", "easyanticheat", "battleye",
        };

        public static bool IsHelperProgram(string path) => HelperProgram.IsMatch(Path.GetFileNameWithoutExtension(path));

        /// <summary>
        /// The apps a dropped folder stands for. Shortcuts directly inside it are the user's own
        /// list of apps and are taken as they are. Otherwise the folder is one app when it holds a
        /// program of its own, and a library of apps (one per subfolder) when it doesn't. An app
        /// folder yields ONE program — its helpers (installers, crash reporters, updaters) are
        /// left out — so dropping a game's install folder adds the game, not everything in it.
        /// </summary>
        public static List<string> FindAppsInFolder(string root, Action<string>? log = null)
        {
            var found = new List<string>();

            var shortcuts = SafeFiles(root).Where(f => IsShortcut(f) && !IsReferenceShortcut(f)).ToList();
            if (shortcuts.Count > 0)
            {
                log?.Invoke($"[Windows] {root}: {shortcuts.Count} shortcut(s)");
                found.AddRange(shortcuts);
                return found;
            }

            string appKey = NameKey(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            string? main = PickMainProgram(root, appKey, log);
            if (main != null)
            {
                found.Add(main);
                return found;
            }

            foreach (string sub in SafeDirectories(root))
            {
                if (SupportFolders.Contains(Path.GetFileName(sub))) continue;
                string? app = FindProgramInAppFolder(sub, NameKey(Path.GetFileName(sub)), depth: 2, log);
                if (app != null) found.Add(app);
            }
            return found;
        }

        /// <summary>An app folder's program, looking up to <paramref name="depth"/> folders down
        /// (Game\bin\game.exe, Game\Binaries\Win64\game.exe) when the folder has none itself.</summary>
        private static string? FindProgramInAppFolder(string dir, string appKey, int depth, Action<string>? log)
        {
            string? main = PickMainProgram(dir, appKey, log);
            if (main != null || depth == 0) return main;

            var nested = SafeDirectories(dir)
                .Where(d => !SupportFolders.Contains(Path.GetFileName(d)))
                .Select(d => FindProgramInAppFolder(d, appKey, depth - 1, log))
                .OfType<string>()
                .ToList();
            return nested.Count <= 1 ? nested.FirstOrDefault() : Best(nested, appKey);
        }

        private static string? PickMainProgram(string dir, string appKey, Action<string>? log)
        {
            var programs = SafeFiles(dir)
                .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            && !IsHelperProgram(f) && IsWindowsProgram(f))
                .ToList();
            if (programs.Count == 0) return null;
            string pick = programs.Count == 1 ? programs[0] : Best(programs, appKey);
            if (programs.Count > 1)
                log?.Invoke($"[Windows] {dir}: {programs.Count} programs, picked {Path.GetFileName(pick)}");
            return pick;
        }

        // The program named after its app wins; otherwise the biggest — the game is the heavy one,
        // the leftovers are tools.
        private static string Best(List<string> programs, string appKey)
            => programs
                .OrderByDescending(p => NameScore(NameKey(Path.GetFileNameWithoutExtension(p)), appKey))
                .ThenByDescending(SafeLength)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .First();

        private static int NameScore(string programKey, string appKey)
        {
            if (programKey.Length == 0 || appKey.Length == 0) return 0;
            if (programKey == appKey) return 2;
            if (appKey.Length >= 3 && programKey.Length >= 3
                && (programKey.Contains(appKey) || appKey.Contains(programKey))) return 1;
            return 0;
        }

        private static string NameKey(string? s)
            => new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        private static bool IsReferenceShortcut(string path)
        {
            if (ReferenceShortcut.IsMatch(Path.GetFileNameWithoutExtension(path))) return true;
            // An internet shortcut to a web page is a bookmark, not an app; launcher links
            // (steam://, com.epicgames.launcher://, …) are apps.
            string? url = ReadInternetShortcutUrl(path);
            return url != null
                && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir); }
            catch { return Array.Empty<string>(); }
        }

        private static IEnumerable<string> SafeDirectories(string dir)
        {
            try
            {
                return Directory.GetDirectories(dir)
                    .Where(d => (File.GetAttributes(d) & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        // ── Shortcuts ───────────────────────────────────────────────────────────────────────

        /// <summary>The URL an internet shortcut (.url) opens, or null.</summary>
        public static string? ReadInternetShortcutUrl(string path)
        {
            if (!path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    string t = line.Trim();
                    if (t.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return t[4..].Trim();
                }
            }
            catch { /* unreadable shortcut */ }
            return null;
        }

        /// <summary>The Steam app id a Steam game shortcut (steam://rungameid/N) launches. Non-Steam
        /// games added to Steam get 64-bit ids with no store page, so they don't match.</summary>
        public static bool TryGetSteamAppId(string path, out int appId)
        {
            appId = 0;
            var m = Regex.Match(ReadInternetShortcutUrl(path) ?? "", @"^steam://(?:rungameid|run)/(\d+)\b",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return m.Success && int.TryParse(m.Groups[1].Value, out appId) && appId > 0;
        }

        // ── Identity and lookups ────────────────────────────────────────────────────────────

        /// <summary>
        /// Library identity for an app, keyed by where it lives rather than by its bytes: many games
        /// ship the same launcher stub, and a content hash would fold different games into one entry
        /// and one cached cover.
        /// </summary>
        public static string LibraryHash(string path)
        {
            string key = "windows-app|" + Path.GetFullPath(path).ToLowerInvariant();
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        }

        /// <summary>
        /// The path handed to artwork and metadata lookups, which search by file name. A program's
        /// file name (hollow_knight.exe, Launcher.exe) says little, so they search by the library
        /// title instead; the folder is kept so nothing else about the path changes.
        /// </summary>
        public static string LookupPath(string path, string? title)
        {
            string name = FileNameHelper.SanitizeFileName(title ?? "");
            if (string.IsNullOrWhiteSpace(name)) return path;
            return Path.Combine(Path.GetDirectoryName(path) ?? "", name + Path.GetExtension(path));
        }

        /// <summary>RomPath for lookups: the title-based name for Windows entries, the file itself otherwise.</summary>
        public static string LookupPathFor(Game game)
            => IsWindows(game.Console) ? LookupPath(game.RomPath, game.Title) : game.RomPath;

        /// <summary>Where an app's generated icon cover is written.</summary>
        public static string IconCoverPath(int gameId)
            => Path.Combine(AppPaths.GetFolder("CoverArt", ConsoleTag), $"{gameId}.png");

        /// <summary>True when a Windows entry's cover is still the generated icon cover — a
        /// placeholder that a real cover may replace.</summary>
        public static bool IsIconCover(string? coverPath)
        {
            if (string.IsNullOrEmpty(coverPath)) return false;
            try
            {
                string dir = Path.GetFullPath(AppPaths.GetFolder("CoverArt", ConsoleTag));
                return string.Equals(Path.GetDirectoryName(Path.GetFullPath(coverPath)), dir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
