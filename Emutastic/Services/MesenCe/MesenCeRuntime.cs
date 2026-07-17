using System;
using System.IO;

namespace Emutastic.Services.MesenCe
{
    /// <summary>
    /// Locates the external Mesen 2 (MesenCE) emulator package used for Mesen 2-format
    /// HD mods (pack format v107+), which the classic libretro Mesen core cannot render.
    /// The package is acquired on demand through the Cores tab from the upstream
    /// project's own releases (GPL-3.0; nothing is bundled or redistributed by us).
    /// Runs fully portable: a settings.json beside the exe makes MesenCE keep its
    /// config, saves and HdPacks inside this folder instead of Documents.
    /// </summary>
    public static class MesenCeRuntime
    {
        public static string GetDir()
        {
            string? dev = Environment.GetEnvironmentVariable("EMUTASTIC_MESEN2_DIR");
            if (!string.IsNullOrWhiteSpace(dev)) return dev;
            return Path.Combine(AppPaths.GetCoresFolder(), "mesen2");
        }

        public static string GetExe() => Path.Combine(GetDir(), "Mesen.exe");

        public static bool IsInstalled() => File.Exists(GetExe());

        // Records the installed build's release tag so the Cores tab can show updates.
        private static string BuildMarkerPath => Path.Combine(GetDir(), "emutastic_build.txt");

        public static string? GetInstalledBuild()
        {
            try { return File.Exists(BuildMarkerPath) ? File.ReadAllText(BuildMarkerPath).Trim() : null; }
            catch { return null; }
        }

        public static void SetInstalledBuild(string id)
        {
            try { File.WriteAllText(BuildMarkerPath, id); } catch { }
        }

        /// <summary>
        /// Idempotent pre-launch preparation: ensures portable mode (settings.json
        /// beside the exe is MesenCE's portable marker) with embedding-friendly
        /// defaults seeded on first run — menu auto-hidden so the embedded view
        /// looks native, no update prompts, no background pause (we re-parent its
        /// window, which can look "unfocused" to the emulator).
        /// </summary>
        public static void PrepareForEmbedding()
        {
            try
            {
                string cfg = Path.Combine(GetDir(), "settings.json");
                if (!File.Exists(cfg))
                {
                    File.WriteAllText(cfg,
                        "{\n" +
                        "  \"FirstRun\": false,\n" +
                        "  \"Preferences\": {\n" +
                        "    \"AutoHideMenu\": true,\n" +
                        "    \"AutomaticallyCheckForUpdates\": false,\n" +
                        "    \"PauseWhenInBackground\": false\n" +
                        "  }\n" +
                        "}\n");
                }
            }
            catch { /* best effort — MesenCE still runs with its own defaults */ }
        }

        /// <summary>
        /// Copies the game's ACTIVE HD mod into MesenCE's HdPacks folder (replacing
        /// any previous copy for that ROM) so the hosted emulator renders it. The
        /// Emutastic mod library stays the source of truth; this is a launch-time
        /// mirror, same idea as the GameCube IPL sync.
        /// </summary>
        public static void SyncActiveMod(string romStem)
        {
            try
            {
                string src = Path.Combine(AppPaths.GetFolder("System"), "HdPacks", romStem);
                string dst = Path.Combine(GetDir(), "HdPacks", romStem);
                if (!File.Exists(Path.Combine(src, "hires.txt")))
                {
                    // No active mod → make sure MesenCE doesn't render a stale copy.
                    if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
                    return;
                }
                if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
                CopyTree(src, dst);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[MesenCE] mod sync failed: {ex.Message}");
            }
        }

        private static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
            foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), overwrite: true);
        }
    }
}
