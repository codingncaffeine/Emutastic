using System;
using System.Runtime.InteropServices;

namespace Emutastic.Services
{
    /// <summary>
    /// P/Invoke bindings for libchdr (CHD format reader). Used by the cdreader
    /// callback layer (RcheevosChdCdReader) to expose CHD-backed disc content to
    /// rcheevos's hashing pipeline, enabling RetroAchievements identification for
    /// CHD files across every CD-based console (PS1, Saturn, SegaCD, NGCD, etc.).
    ///
    /// libchdr.dll ships in the application directory; standard LoadLibrary lookup
    /// finds it without needing SetDllImportResolver.
    /// </summary>
    internal static class LibChdr
    {
        private const string Lib = "libchdr";

        public const int CHD_OPEN_READ = 1;

        // Subset of chd_error — we only check for None at the binding layer; callers
        // pass other codes through chd_error_string for display.
        public enum ChdError
        {
            None = 0,
            NoInterface = 1,
            OutOfMemory = 2,
            InvalidFile = 3,
            InvalidParameter = 4,
            InvalidData = 5,
            FileNotFound = 6,
            RequiresParent = 7,
            FileNotWriteable = 8,
            ReadError = 9,
            WriteError = 10,
            CodecError = 11,
            InvalidParent = 12,
            HunkOutOfRange = 13,
            DecompressionError = 14,
            CompressionError = 15,
            CantCreateFile = 16,
            CantVerify = 17,
            NotSupported = 18,
            MetadataNotFound = 19,
            InvalidMetadataSize = 20,
            UnsupportedVersion = 21,
            VerifyIncomplete = 22,
            InvalidMetadata = 23,
            InvalidState = 24,
            OperationPending = 25,
            NoAsyncOperation = 26,
            UnsupportedFormat = 27,
        }

        // CHD CD-ROM track metadata tags. FOURCC stored big-endian in the file but
        // chd_get_metadata takes them as host-uint32 — packed via (a<<24)|(b<<16)|(c<<8)|d.
        public static readonly uint CDROM_TRACK_METADATA_TAG  = MakeFourCc('C', 'H', 'T', 'R');
        public static readonly uint CDROM_TRACK_METADATA2_TAG = MakeFourCc('C', 'H', 'T', '2');
        public static readonly uint GDROM_TRACK_METADATA_TAG  = MakeFourCc('C', 'H', 'G', 'D');
        public static readonly uint HARD_DISK_METADATA_TAG    = MakeFourCc('G', 'D', 'D', 'D');
        public static readonly uint DVD_METADATA_TAG          = MakeFourCc('D', 'V', 'D', ' ');

        private static uint MakeFourCc(char a, char b, char c, char d)
            => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;

        // ── Function imports ──────────────────────────────────────────────

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
        public static extern ChdError chd_open(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
            int mode, IntPtr parent, out IntPtr chd);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void chd_close(IntPtr chd);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ChdError chd_read(IntPtr chd, uint hunknum, IntPtr buffer);

        // Returns const chd_header* — pointer into libchdr-owned memory, do NOT free.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr chd_get_header(IntPtr chd);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ChdError chd_get_metadata(
            IntPtr chd, uint searchtag, uint searchindex,
            byte[] output, uint outputlen, out uint resultlen,
            out uint resulttag, out byte resultflags);

        // Returns const char* — caller must Marshal.PtrToStringAnsi.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr chd_error_string(ChdError err);

        // ── Header field offsets (computed from libchdr's struct _chd_header) ──
        //
        // libchdr's in-memory chd_header layout (x64, natural alignment):
        //                                  offset
        //   uint32   length                  0
        //   uint32   version                 4
        //   uint32   flags                   8
        //   uint32   compression[4]         12  (16 bytes)
        //   uint32   hunkbytes              28
        //   uint32   totalhunks             32
        //   <4 bytes pad to align uint64>   36
        //   uint64   logicalbytes           40
        //   uint64   metaoffset             48
        //   uint64   mapoffset              56
        //   uint8    md5[16]                64
        //   uint8    parentmd5[16]          80
        //   uint8    sha1[20]               96
        //   uint8    rawsha1[20]           116
        //   uint8    parentsha1[20]        136
        //   uint32   unitbytes             156
        //   uint64   unitcount             160
        //   uint32   hunkcount             168
        //   uint32   mapentrybytes         172
        //   <4 bytes pad to align ptr>     176
        //   uint8*   rawmap                176
        //   uint32   obsolete_cylinders... 184+
        //
        // We read by absolute offset via Marshal.ReadInt32 rather than marshaling
        // the whole struct (avoids ABI fragility around the rawmap pointer).
        private const int HDR_OFFSET_VERSION    = 4;
        private const int HDR_OFFSET_HUNKBYTES  = 28;
        private const int HDR_OFFSET_TOTALHUNKS = 32;
        private const int HDR_OFFSET_UNITBYTES  = 156;
        private const int HDR_OFFSET_HUNKCOUNT  = 168;

        public readonly struct HeaderFields
        {
            public readonly uint Version;
            public readonly uint HunkBytes;
            public readonly uint TotalHunks;
            public readonly uint UnitBytes;
            public readonly uint HunkCount;

            public HeaderFields(uint version, uint hunkBytes, uint totalHunks, uint unitBytes, uint hunkCount)
            {
                Version = version;
                HunkBytes = hunkBytes;
                TotalHunks = totalHunks;
                UnitBytes = unitBytes;
                HunkCount = hunkCount;
            }
        }

        public static HeaderFields ReadHeader(IntPtr chd)
        {
            IntPtr hdr = chd_get_header(chd);
            if (hdr == IntPtr.Zero)
                throw new InvalidOperationException("chd_get_header returned null");
            return new HeaderFields(
                (uint)Marshal.ReadInt32(hdr, HDR_OFFSET_VERSION),
                (uint)Marshal.ReadInt32(hdr, HDR_OFFSET_HUNKBYTES),
                (uint)Marshal.ReadInt32(hdr, HDR_OFFSET_TOTALHUNKS),
                (uint)Marshal.ReadInt32(hdr, HDR_OFFSET_UNITBYTES),
                (uint)Marshal.ReadInt32(hdr, HDR_OFFSET_HUNKCOUNT));
        }

        public static string ErrorString(ChdError err)
        {
            IntPtr p = chd_error_string(err);
            return p == IntPtr.Zero ? $"chd_error_{(int)err}" : Marshal.PtrToStringAnsi(p) ?? $"chd_error_{(int)err}";
        }

        /// <summary>
        /// Reads ASCII metadata blob for the given tag/index. Returns null if the
        /// tag is absent (CHDERR_METADATA_NOT_FOUND) or any error occurs.
        /// </summary>
        public static string? TryReadMetadataString(IntPtr chd, uint tag, uint index)
        {
            // 256 bytes is more than enough for any CDROM_TRACK_METADATA blob
            byte[] buf = new byte[256];
            ChdError err = chd_get_metadata(chd, tag, index, buf, (uint)buf.Length,
                out uint resultLen, out _, out _);
            if (err != ChdError.None) return null;
            int len = (int)Math.Min(resultLen, (uint)buf.Length);
            // Metadata strings are null-terminated; trim
            int nul = Array.IndexOf<byte>(buf, 0, 0, len);
            if (nul >= 0) len = nul;
            return System.Text.Encoding.ASCII.GetString(buf, 0, len);
        }

        /// <summary>
        /// Smoke test: opens, reads header, closes. Returns the header on success
        /// or null on any error (with the error text in <paramref name="errorMessage"/>).
        /// Used by Phase 2 verification; harmless to call at runtime.
        /// </summary>
        public static HeaderFields? TryOpenAndReadHeader(string path, out string? errorMessage)
        {
            errorMessage = null;
            IntPtr chd = IntPtr.Zero;
            try
            {
                ChdError err = chd_open(path, CHD_OPEN_READ, IntPtr.Zero, out chd);
                if (err != ChdError.None)
                {
                    errorMessage = ErrorString(err);
                    return null;
                }
                return ReadHeader(chd);
            }
            catch (DllNotFoundException ex)
            {
                errorMessage = $"libchdr.dll not found: {ex.Message}";
                return null;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }
            finally
            {
                if (chd != IntPtr.Zero) chd_close(chd);
            }
        }
    }
}
