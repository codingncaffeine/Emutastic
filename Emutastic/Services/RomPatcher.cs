using System;
using System.IO;

namespace Emutastic.Services
{
    public enum PatchFormat { Unknown, Ips, Ups, Bps }

    /// <summary>Outcome of applying a ROM-hack patch to a base ROM.</summary>
    public sealed class PatchResult
    {
        public bool    Ok       { get; private init; }
        public byte[]? Patched  { get; private init; }
        public string? Error    { get; private init; }

        public static PatchResult Success(byte[] patched) => new() { Ok = true, Patched = patched };
        public static PatchResult Fail(string error)      => new() { Ok = false, Error = error };
    }

    /// <summary>
    /// Frontend-side ROM soft-patching. Applies an IPS / UPS / BPS patch to a base ROM
    /// in memory (the original file is never modified). UPS and BPS embed a source CRC32,
    /// so a wrong base ROM — or a headered/unheadered mismatch — is detected and rejected
    /// with a clear message. IPS has no checksum, so it is applied as-is.
    /// XDelta/VCDIFF is intentionally not supported (v1).
    /// </summary>
    public static class RomPatcher
    {
        // Guard against a corrupt patch claiming an absurd output size and OOMing us.
        private const long MaxOutputBytes = 128L * 1024 * 1024;

        public static PatchFormat DetectFormat(byte[] patch)
        {
            if (patch.Length >= 5 && patch[0] == 'P' && patch[1] == 'A' && patch[2] == 'T' && patch[3] == 'C' && patch[4] == 'H')
                return PatchFormat.Ips;
            if (patch.Length >= 4 && patch[0] == 'U' && patch[1] == 'P' && patch[2] == 'S' && patch[3] == '1')
                return PatchFormat.Ups;
            if (patch.Length >= 4 && patch[0] == 'B' && patch[1] == 'P' && patch[2] == 'S' && patch[3] == '1')
                return PatchFormat.Bps;
            return PatchFormat.Unknown;
        }

        public static bool IsPatchExtension(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".ips" || ext == ".ups" || ext == ".bps";
        }

        /// <summary>
        /// Cartridge consoles where "Apply ROM Hack" is offered. These cores load the ROM
        /// from a memory buffer (need_fullpath=false), which soft-patching requires; the
        /// LoadGame runtime gate is the backstop if a specific core reports otherwise.
        /// Disc-based systems (PS1/Saturn/etc.) are intentionally excluded.
        /// </summary>
        public static readonly System.Collections.Generic.HashSet<string> SupportedConsoles = new()
        {
            "NES", "FDS", "SNES", "GB", "GBC", "GBA", "N64",
            "Genesis", "Sega32X", "SMS", "GameGear", "SG1000",
            "TG16", "VirtualBoy", "Atari2600", "Atari7800",
            "Jaguar", "ColecoVision", "Odyssey2",
        };

        /// <summary>Applies <paramref name="patch"/> to <paramref name="source"/>, auto-detecting the format.</summary>
        public static PatchResult Apply(byte[] source, byte[] patch)
        {
            try
            {
                return DetectFormat(patch) switch
                {
                    PatchFormat.Ips => ApplyIps(source, patch),
                    PatchFormat.Ups => ApplyUps(source, patch),
                    PatchFormat.Bps => ApplyBps(source, patch),
                    _ => PatchResult.Fail("Unrecognized patch format — expected IPS, UPS, or BPS."),
                };
            }
            catch (Exception ex)
            {
                // Any out-of-range / malformed decode lands here rather than crashing the app.
                return PatchResult.Fail($"The patch file appears corrupt or invalid ({ex.GetType().Name}).");
            }
        }

        // ── IPS ─────────────────────────────────────────────────────────────
        private static PatchResult ApplyIps(byte[] src, byte[] p)
        {
            using var ms = new MemoryStream();
            ms.Write(src, 0, src.Length);

            int pos = 5; // past "PATCH"
            while (pos + 3 <= p.Length)
            {
                // EOF marker (also a valid-but-rare offset 0x454F46; standard IPS treats it as end).
                if (p[pos] == 'E' && p[pos + 1] == 'O' && p[pos + 2] == 'F')
                {
                    pos += 3;
                    if (pos + 3 <= p.Length) // optional truncate-extension
                    {
                        long trunc = (p[pos] << 16) | (p[pos + 1] << 8) | p[pos + 2];
                        if (trunc >= 0 && trunc <= ms.Length) ms.SetLength(trunc);
                    }
                    return PatchResult.Success(ms.ToArray());
                }

                int offset = (p[pos] << 16) | (p[pos + 1] << 8) | p[pos + 2]; pos += 3;
                if (pos + 2 > p.Length) break;
                int size = (p[pos] << 8) | p[pos + 1]; pos += 2;

                if (size == 0) // RLE record
                {
                    if (pos + 3 > p.Length) break;
                    int runLen = (p[pos] << 8) | p[pos + 1]; pos += 2;
                    byte val = p[pos]; pos += 1;
                    ms.Seek(offset, SeekOrigin.Begin);
                    for (int i = 0; i < runLen; i++) ms.WriteByte(val);
                }
                else
                {
                    if (pos + size > p.Length) break;
                    ms.Seek(offset, SeekOrigin.Begin);
                    ms.Write(p, pos, size);
                    pos += size;
                }
            }

            // IPS has no integrity field; return whatever applied (most files end with EOF above).
            return PatchResult.Success(ms.ToArray());
        }

        // ── UPS ─────────────────────────────────────────────────────────────
        private static PatchResult ApplyUps(byte[] src, byte[] p)
        {
            if (p.Length < 4 + 12) return PatchResult.Fail("UPS patch is too small to be valid.");

            int pos = 4; // past "UPS1"
            long inSize  = ReadVarint(p, ref pos);
            long outSize = ReadVarint(p, ref pos);
            if (outSize > MaxOutputBytes) return PatchResult.Fail("UPS patch declares an implausibly large output.");

            uint srcCrcExpected = ReadUInt32Le(p, p.Length - 12);
            uint outCrcExpected = ReadUInt32Le(p, p.Length - 8);

            if (src.Length != inSize || Crc32(src, 0, src.Length) != srcCrcExpected)
                return PatchResult.Fail("This UPS patch doesn't match this ROM (it may expect a different or headered/unheadered copy).");

            var output = new byte[outSize];
            int copy = (int)Math.Min(src.Length, outSize);
            Array.Copy(src, output, copy);

            int bodyEnd = p.Length - 12;
            long outPos = 0;
            while (pos < bodyEnd)
            {
                outPos += ReadVarint(p, ref pos);
                while (pos < bodyEnd)
                {
                    byte b = p[pos++];
                    if (outPos < output.Length) output[outPos] ^= b;
                    outPos++;
                    if (b == 0) break;
                }
            }

            if (Crc32(output, 0, output.Length) != outCrcExpected)
                return PatchResult.Fail("The UPS patch did not apply cleanly (output checksum mismatch).");

            return PatchResult.Success(output);
        }

        // ── BPS ─────────────────────────────────────────────────────────────
        private static PatchResult ApplyBps(byte[] src, byte[] p)
        {
            if (p.Length < 4 + 12) return PatchResult.Fail("BPS patch is too small to be valid.");

            int pos = 4; // past "BPS1"
            long sourceSize = ReadVarint(p, ref pos);
            long targetSize = ReadVarint(p, ref pos);
            long metaSize   = ReadVarint(p, ref pos);
            pos += (int)metaSize; // skip metadata

            if (targetSize > MaxOutputBytes) return PatchResult.Fail("BPS patch declares an implausibly large output.");

            uint srcCrcExpected    = ReadUInt32Le(p, p.Length - 12);
            uint targetCrcExpected = ReadUInt32Le(p, p.Length - 8);

            if (src.Length != sourceSize || Crc32(src, 0, src.Length) != srcCrcExpected)
                return PatchResult.Fail("This BPS patch doesn't match this ROM (it may expect a different or headered/unheadered copy).");

            var output = new byte[targetSize];
            int commandEnd = p.Length - 12;
            long outputOffset = 0, sourceRel = 0, targetRel = 0;

            while (pos < commandEnd)
            {
                long number = ReadVarint(p, ref pos);
                long action = number & 3;
                long length = (number >> 2) + 1;

                switch (action)
                {
                    case 0: // SourceRead
                        for (long i = 0; i < length; i++, outputOffset++)
                            output[outputOffset] = src[outputOffset];
                        break;
                    case 1: // TargetRead (literal bytes from patch)
                        for (long i = 0; i < length; i++)
                            output[outputOffset++] = p[pos++];
                        break;
                    case 2: // SourceCopy
                    {
                        long data = ReadVarint(p, ref pos);
                        sourceRel += (data & 1) != 0 ? -(data >> 1) : (data >> 1);
                        for (long i = 0; i < length; i++)
                            output[outputOffset++] = src[sourceRel++];
                        break;
                    }
                    case 3: // TargetCopy (RLE from already-written target)
                    {
                        long data = ReadVarint(p, ref pos);
                        targetRel += (data & 1) != 0 ? -(data >> 1) : (data >> 1);
                        for (long i = 0; i < length; i++)
                            output[outputOffset++] = output[targetRel++];
                        break;
                    }
                }
            }

            if (Crc32(output, 0, output.Length) != targetCrcExpected)
                return PatchResult.Fail("The BPS patch did not apply cleanly (output checksum mismatch).");

            return PatchResult.Success(output);
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        // UPS/BPS variable-width integer (same scheme for both).
        private static long ReadVarint(byte[] p, ref int pos)
        {
            long data = 0, shift = 1;
            while (true)
            {
                byte x = p[pos++];
                data += (x & 0x7f) * shift;
                if ((x & 0x80) != 0) break;
                shift <<= 7;
                data += shift;
            }
            return data;
        }

        private static uint ReadUInt32Le(byte[] p, int off)
            => (uint)(p[off] | (p[off + 1] << 8) | (p[off + 2] << 16) | (p[off + 3] << 24));

        private static readonly uint[] _crcTable = BuildCrcTable();
        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        private static uint Crc32(byte[] data, int offset, int length)
        {
            uint c = 0xFFFFFFFF;
            for (int i = 0; i < length; i++)
                c = _crcTable[(c ^ data[offset + i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }
    }
}
