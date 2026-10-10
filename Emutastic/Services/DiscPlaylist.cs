using System;
using System.IO;

namespace Emutastic.Services
{
    /// <summary>
    /// Maps a disc index of a multi-disc launch to the file behind it. Emutastic launches
    /// multi-disc games through an .m3u, and the cores that register the libretro disk-control
    /// interface (Beetle PSX, Kronos, Genesis Plus GX, PUAE) number their images in the order the
    /// playlist lists them, so entry N of the playlist is image N. RetroAchievements needs that
    /// file to verify that a swapped-in disc belongs to the loaded game.
    /// </summary>
    public static class DiscPlaylist
    {
        /// <summary>
        /// The file behind disc <paramref name="index"/> of the launch at <paramref name="romPath"/>,
        /// or null when the launch was not an .m3u, the index is past the playlist's end, or the
        /// file is missing. Comment lines ('#'), blank lines and the "file|label" form are read the
        /// way the cores read them.
        /// </summary>
        public static string? ResolveDiscPath(string? romPath, uint index)
        {
            try
            {
                if (string.IsNullOrEmpty(romPath)) return null;
                if (!romPath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) || !File.Exists(romPath)) return null;
                return ResolveEntry(Path.GetDirectoryName(romPath) ?? "", File.ReadAllLines(romPath), index);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[RA] disc path lookup failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// <see cref="ResolveDiscPath"/> over the playlist's lines. Relative entries resolve against
        /// <paramref name="playlistDir"/>; the file must exist.
        /// </summary>
        public static string? ResolveEntry(string playlistDir, string[] lines, uint index)
        {
            uint n = 0;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (n++ != index) continue;
                int bar = line.IndexOf('|');            // "file|label"
                if (bar > 0) line = line.Substring(0, bar).Trim();
                string path = Path.IsPathRooted(line) ? line : Path.Combine(playlistDir, line);
                path = Path.GetFullPath(path);
                return File.Exists(path) ? path : null;
            }
            return null;
        }
    }
}
