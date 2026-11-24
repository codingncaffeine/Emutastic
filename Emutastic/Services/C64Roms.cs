using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Emutastic.Services
{
    /// <summary>
    /// The Commodore 64's system ROMs as Kernal64 finds them: by their contents (SHA-1), in the System folder and
    /// its kernal64/ folder, whatever the files are named. A C64C's combined 16 KB image counts as BASIC (first
    /// half) and KERNAL (second half). The two 1541 halves are optional: without them disks are refused by the core.
    /// </summary>
    public static class C64Roms
    {
        private static readonly (string Role, int Size, string Sha1)[] Known =
        {
            ("BASIC ROM (basic.901226-01.bin)",          8192, "79015323128650c742a3694c9429aa91f355905e"),
            ("KERNAL ROM (kernal.901227-03.bin)",        8192, "1d503e56df85a62fee696e7618dc5b4e781df1bb"),
            ("character ROM (characters.901225-01.bin)", 4096, "adc7c31e18c7c7413d54802ef2f4193da14711aa"),
        };

        /// <summary>The required ROMs not found, as names for the "BIOS required" message; empty = ready.</summary>
        public static List<string> Missing(string systemDir)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (string dir in new[] { systemDir, Path.Combine(systemDir, "kernal64") })
            {
                IEnumerable<string> files;
                try { files = Directory.Exists(dir) ? Directory.EnumerateFiles(dir) : Enumerable.Empty<string>(); }
                catch { continue; }
                foreach (string f in files)
                {
                    long size;
                    try { size = new FileInfo(f).Length; } catch { continue; }
                    if (size != 4096 && size != 8192 && size != 16384) continue;
                    byte[] data;
                    try { data = File.ReadAllBytes(f); } catch { continue; }
                    if (size == 16384)
                    {
                        found.Add(Sha1(data.AsSpan(0, 8192)));
                        found.Add(Sha1(data.AsSpan(8192, 8192)));
                    }
                    else found.Add(Sha1(data));
                }
            }
            return Known.Where(k => !found.Contains(k.Sha1)).Select(k => k.Role).ToList();
        }

        private static string Sha1(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
    }
}
