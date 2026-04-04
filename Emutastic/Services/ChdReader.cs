using System;
using System.IO;

namespace Emutastic.Services
{
    /// <summary>
    /// Reads metadata from CHD (Compressed Hunks of Data) v4/v5 headers without
    /// decompressing the archive.  The SHA1 stored in the header matches the value
    /// Redump publishes for the same disc, so it can be used for DAT lookups.
    /// </summary>
    public static class ChdReader
    {
        private static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes("MComprHD");

        // CHD v5 header layout (124 bytes total)
        //   0x00  8 bytes  magic "MComprHD"
        //   0x08  4 bytes  header length
        //   0x0C  4 bytes  version (5)
        //   0x10 16 bytes  compression[4]
        //   0x20  8 bytes  logical bytes
        //   0x28  8 bytes  map offset
        //   0x30  8 bytes  meta offset
        //   0x38  4 bytes  hunk size
        //   0x3C  4 bytes  unit bytes
        //   0x40 20 bytes  rawsha1  ← SHA1 of uncompressed data (matches Redump)
        //   0x54 20 bytes  sha1     ← SHA1 with parent chain (same as rawsha1 for standalone)
        //   0x68 20 bytes  parent sha1
        private const int V5_RAWSHA1_OFFSET = 0x40;

        // CHD v4 header layout (108 bytes total)
        //   0x00  8 bytes  magic
        //   0x08  4 bytes  header length
        //   0x0C  4 bytes  version (4)
        //   0x10  4 bytes  flags
        //   0x14  4 bytes  compression
        //   0x18  4 bytes  hunk size
        //   0x1C  8 bytes  logical bytes
        //   0x24  8 bytes  meta offset
        //   0x2C  4 bytes  hunk count
        //   0x30 20 bytes  parent sha1
        //   0x44 20 bytes  sha1     ← SHA1 of uncompressed data
        private const int V4_SHA1_OFFSET = 0x44;

        /// <summary>
        /// Returns the lowercase hex SHA1 string stored in the CHD header,
        /// or null if the file is not a valid CHD v4/v5.
        /// </summary>
        public static string? ReadSha1(string path)
        {
            try
            {
                // Read enough bytes to cover both header variants (v5 = 124 bytes)
                Span<byte> buf = stackalloc byte[124];
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                int read = fs.Read(buf);
                if (read < 16) return null;

                // Verify magic
                for (int i = 0; i < Magic.Length; i++)
                    if (buf[i] != Magic[i]) return null;

                uint version = ReadUInt32BE(buf, 0x0C);

                int sha1Offset = version switch
                {
                    5 when read >= V5_RAWSHA1_OFFSET + 20 => V5_RAWSHA1_OFFSET,
                    4 when read >= V4_SHA1_OFFSET   + 20 => V4_SHA1_OFFSET,
                    _ => -1
                };

                if (sha1Offset < 0) return null;

                return Convert.ToHexString(buf.Slice(sha1Offset, 20)).ToLowerInvariant();
            }
            catch { return null; }
        }

        private static uint ReadUInt32BE(Span<byte> buf, int offset)
            => ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16)
             | ((uint)buf[offset + 2] << 8)  |  buf[offset + 3];
    }
}
