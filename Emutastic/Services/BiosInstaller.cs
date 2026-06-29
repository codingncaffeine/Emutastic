using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Emutastic.Views;   // KnownBios, BiosEntry

namespace Emutastic.Services
{
    /// <summary>
    /// Recognizes a file as a known console BIOS by content hash and installs it
    /// into the System directory under its canonical filename.
    ///
    /// Used by the import pipeline so a BIOS dropped anywhere — e.g. sitting
    /// alongside the games, with its full No-Intro name — is auto-installed to the
    /// System folder instead of being added to the library as a game. Matching is
    /// by MD5 (not filename or size): several games share a BIOS's byte size, so
    /// only the content hash can tell them apart.
    /// </summary>
    internal static class BiosInstaller
    {
        /// <summary>
        /// If <paramref name="srcPath"/> matches a known BIOS, copies it into the
        /// System dir under the canonical filename and returns the matched entry.
        /// Returns null if the file isn't a known BIOS (or on any error).
        /// </summary>
        internal static BiosEntry? TryInstall(string srcPath)
        {
            try
            {
                if (!File.Exists(srcPath)) return null;
                string name = Path.GetFileName(srcPath);
                long   size = new FileInfo(srcPath).Length;

                BiosEntry? match = null;

                // Tier 1 — content hash. Works regardless of the file's name, so a BIOS
                // dropped with its full No-Intro name is still recognized. Only hash when
                // the size could match an MD5-pinned entry (cheap pre-filter).
                if (KnownBios.All.Any(b => b.Md5 != null && (b.ExpectedSize == 0 || b.ExpectedSize == size)))
                {
                    string md5 = ComputeMd5(srcPath);
                    match = KnownBios.All.FirstOrDefault(b => b.Md5 != null &&
                        string.Equals(b.Md5, md5, StringComparison.OrdinalIgnoreCase));
                }

                // Tier 2 — canonical filename (+ size when the entry pins one). Covers the
                // presence-only BIOS that have no MD5 in the table (e.g. neogeo.zip,
                // syscard3.pce, cdibios.zip), which ship correctly named anyway.
                match ??= KnownBios.All.FirstOrDefault(b =>
                    string.Equals(Path.GetFileName(b.Filename), name, StringComparison.OrdinalIgnoreCase)
                    && (b.ExpectedSize == 0 || b.ExpectedSize == size));

                if (match == null) return null;

                string sysDir = AppPaths.GetFolder("System");
                string dest   = Path.Combine(sysDir, match.Filename);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(srcPath, dest, overwrite: true);
                PreferencesCache.InvalidateBiosScan();
                return match;
            }
            catch { return null; }
        }

        /// <summary>
        /// True when a filename is plainly a BIOS / system-ROM (e.g. an alternate dump that
        /// doesn't hash-match an installable entry). Used to keep such files out of the game
        /// library even when they can't be auto-installed. Word-boundary matched so it won't
        /// trip on titles that merely contain the letters (e.g. "biosphere").
        /// </summary>
        internal static bool LooksLikeBiosName(string fileName)
        {
            string n = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(n, @"\bbios\b")
                || n.Contains("system rom")
                || n.Contains("boot rom");
        }

        private static string ComputeMd5(string path)
        {
            using var md5 = MD5.Create();
            using var fs  = File.OpenRead(path);
            return BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
    }
}
