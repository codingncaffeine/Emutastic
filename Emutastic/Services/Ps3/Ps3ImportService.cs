using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Emutastic.Services.Ps3
{
    /// <summary>
    /// Imports user-supplied, decrypted PlayStation 3 content into the library. Handles the shapes
    /// seen in the wild: installable content packages (with optional license files), extracted game
    /// folders, and disc images — inside archives or as plain folders/files. Each title is identified
    /// from its PARAM.SFO and registered with a boot path the launcher hands to the external emulator.
    /// Nothing proprietary is bundled; the emulator and any system files are user-provided.
    /// </summary>
    public static class Ps3ImportService
    {
        private static readonly string[] ArchiveExt = { ".zip", ".7z", ".rar" };

        public static async Task ImportBatchAsync(List<string> paths, ImportService owner)
        {
            foreach (string path in paths)
            {
                try { await ImportOneAsync(path, owner); }
                catch (Exception ex) { Trace.WriteLine($"[Ps3Import] {path}: {ex.Message}"); }
            }
        }

        private static async Task ImportOneAsync(string path, ImportService owner)
        {
            string ext = Path.GetExtension(path);

            // Archive → extract to a working folder, then import its contents.
            if (File.Exists(path) && ArchiveExt.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                string work = Path.Combine(AppPaths.GetFolder("ExtractedRoms", "PS3"),
                    Path.GetFileNameWithoutExtension(path));
                owner.ReportImportStatus($"Extracting {Path.GetFileName(path)}…");
                await Task.Run(() => ExtractArchive(path, work));
                await ImportFolderAsync(work, path, owner);
                return;
            }

            // A single installable package (its license files, if any, sit beside it).
            if (File.Exists(path) && ext.Equals(".pkg", StringComparison.OrdinalIgnoreCase))
            {
                await InstallPackageAsync(path, FindSiblingLicenses(path), path, owner);
                return;
            }

            // A disc image — register directly (the emulator reads it as-is).
            if (File.Exists(path) && IsDiscImage(ext))
            {
                owner.RegisterPs3Game(Path.GetFileNameWithoutExtension(path), path, path);
                return;
            }

            // A folder — scan it for any of the above.
            if (Directory.Exists(path))
                await ImportFolderAsync(path, path, owner);
        }

        private static async Task ImportFolderAsync(string folder, string source, ImportService owner)
        {
            // Bootable game first: a disc dump (PS3_GAME/USRDIR/EBOOT.BIN) or an extracted game
            // (USRDIR/EBOOT.BIN). A dump can contain internal *.pkg game-data files (e.g. shaders)
            // that are NOT installable packages, so the boot file must take priority over them.
            var boots = SafeEnumerate(folder, "EBOOT.BIN");
            if (boots.Count > 0)
            {
                foreach (string boot in boots)
                {
                    string title = ResolveTitle(boot) ?? Path.GetFileName(folder);
                    owner.RegisterPs3Game(title, boot, source, FindBundledCover(boot), FindBundledSnap(boot));
                }
                return;
            }

            // Installable packages: a real package sits at the import root, never inside a game's
            // data folders — exclude any *.pkg that lives under a game's USRDIR (that's game data).
            var packages = SafeEnumerate(folder, "*.pkg").Where(p => !IsGameData(p)).ToList();
            if (packages.Count > 0)
            {
                var licenses = SafeEnumerate(folder, "*.rap");
                foreach (string pkg in packages)
                    await InstallPackageAsync(pkg, licenses, source, owner);
                return;
            }

            var images = SafeEnumerate(folder, "*.iso");
            if (images.Count > 0)
            {
                foreach (string image in images)
                    owner.RegisterPs3Game(Path.GetFileNameWithoutExtension(image), image, source);
                return;
            }

            owner.ReportImportStatus($"No PlayStation 3 content found in {Path.GetFileName(folder)}.");
            await Task.CompletedTask;
        }

        // True for *.pkg files that are game data inside a dump (e.g. shaders), not installable packages.
        private static bool IsGameData(string path)
        {
            string p = path.Replace('/', '\\');
            return p.IndexOf("\\USRDIR\\", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("\\PS3_GAME\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static async Task InstallPackageAsync(string package, List<string> licenses, string source, ImportService owner)
        {
            if (!Rpcs3Runtime.IsInstalled())
            {
                owner.ReportImportStatus("Install PlayStation 3 support from the Cores / Extras tab before importing packages.");
                return;
            }

            // Copy any license files into the per-user license store first.
            string exdata = Path.Combine(Rpcs3Runtime.GetDir(), "dev_hdd0", "home", "00000001", "exdata");
            try { Directory.CreateDirectory(exdata); } catch { }
            foreach (string license in licenses)
            {
                try { File.Copy(license, Path.Combine(exdata, Path.GetFileName(license)), overwrite: true); }
                catch (Exception ex) { Trace.WriteLine($"[Ps3Import] license copy: {ex.Message}"); }
            }

            // Snapshot the installed-titles store, install, then register whatever is new.
            string gameRoot = Path.Combine(Rpcs3Runtime.GetDir(), "dev_hdd0", "game");
            var before = SnapshotSerials(gameRoot);

            owner.ReportImportStatus($"Installing {Path.GetFileName(package)}…");
            await RunInstallAsync(package);

            if (!Directory.Exists(gameRoot)) return;
            foreach (string dir in Directory.GetDirectories(gameRoot))
            {
                string serial = Path.GetFileName(dir);
                if (before.Contains(serial)) continue; // not newly installed by this package
                string boot = Path.Combine(dir, "USRDIR", "EBOOT.BIN");
                if (!File.Exists(boot)) continue;
                string title = ResolveTitle(boot) ?? serial;
                owner.RegisterPs3Game(title, boot, source, FindBundledCover(boot), FindBundledSnap(boot));
            }
        }

        private static async Task RunInstallAsync(string package)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Rpcs3Runtime.GetExe(),
                    UseShellExecute = false,
                    WorkingDirectory = Rpcs3Runtime.GetDir(),
                };
                psi.ArgumentList.Add("--installpkg");
                psi.ArgumentList.Add(package);
                psi.ArgumentList.Add("--headless");
                using var proc = Process.Start(psi);
                if (proc != null) await proc.WaitForExitAsync();
                // The emulator exits with a non-zero status on headless shutdown even when the
                // install succeeded, so the result is verified by the new-titles scan, not the code.
            }
            catch (Exception ex) { Trace.WriteLine($"[Ps3Import] install failed: {ex.Message}"); }
        }

        // PARAM.SFO sits next to the boot file (USRDIR) or one level up in the game root.
        private static string? ResolveTitle(string bootFile)
        {
            string? usrDir = Path.GetDirectoryName(bootFile);
            string? gameRoot = usrDir != null ? Path.GetDirectoryName(usrDir) : null;
            foreach (string? dir in new[] { gameRoot, usrDir })
            {
                if (dir == null) continue;
                string sfo = Path.Combine(dir, "PARAM.SFO");
                if (File.Exists(sfo))
                {
                    string? t = ParamSfo.Title(sfo);
                    if (!string.IsNullOrWhiteSpace(t)) return t;
                }
            }
            return null;
        }

        private static HashSet<string> SnapshotSerials(string gameRoot)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(gameRoot))
                    foreach (string dir in Directory.GetDirectories(gameRoot))
                        set.Add(Path.GetFileName(dir));
            }
            catch { }
            return set;
        }

        private static List<string> FindSiblingLicenses(string file)
        {
            try
            {
                string? dir = Path.GetDirectoryName(file);
                return dir != null ? SafeEnumerate(dir, "*.rap") : new List<string>();
            }
            catch { return new List<string>(); }
        }

        private static List<string> SafeEnumerate(string dir, string pattern)
        {
            try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).ToList(); }
            catch { return new List<string>(); }
        }

        private static bool IsDiscImage(string ext)
            => ext.Equals(".iso", StringComparison.OrdinalIgnoreCase);

        // Every PS3 title ships its own art locally. ICON0 is the cover; PIC1 is the HD background
        // (a good snap). Both work offline, before (or instead of) any network lookup.
        private static string? FindBundledCover(string bootFile) => FindBundled(bootFile, "ICON0.PNG", "PIC0.PNG");
        private static string? FindBundledSnap(string bootFile) => FindBundled(bootFile, "PIC1.PNG", "PIC0.PNG");

        private static string? FindBundled(string bootFile, params string[] names)
        {
            string? usrDir = Path.GetDirectoryName(bootFile);
            string? gameRoot = usrDir != null ? Path.GetDirectoryName(usrDir) : null;
            foreach (string? dir in new[] { gameRoot, usrDir })
            {
                if (dir == null) continue;
                foreach (string name in names)
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        private static void ExtractArchive(string archive, string dest)
        {
            Directory.CreateDirectory(dest);
            using var arc = Emutastic.Services.Archives.RomArchive.Open(archive);
            foreach (var entry in arc.Entries)
            {
                if (entry.IsDirectory) continue;
                string relative = entry.Key.Replace('/', Path.DirectorySeparatorChar);
                string outPath = Path.Combine(dest, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var fs = File.Create(outPath);
                entry.ExtractTo(fs);
            }
        }
    }
}
