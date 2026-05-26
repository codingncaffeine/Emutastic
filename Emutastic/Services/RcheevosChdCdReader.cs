using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Emutastic.Services
{
    /// <summary>
    /// Bridges libchdr to rcheevos's cdreader callback surface, enabling
    /// achievement identification for CHD-based disc content across every
    /// supported CD console (PS1, Saturn, SegaCD, Dreamcast, PSP,
    /// TG-CD, 3DO, NGCD).
    ///
    /// Strategy: capture the default cdreader from rcheevos at init, then
    /// register an extension-dispatcher cdreader. Our open_track_iterator
    /// checks the file extension — .chd routes through libchdr; everything
    /// else delegates to the captured default reader (preserving existing
    /// .cue+.bin / .gdi / .iso behavior).
    ///
    /// Architectural constraints from rcheevos audit:
    /// - hash.c:980 rc_hash_merge_callbacks only copies our cdreader struct
    ///   if open_track is non-NULL → we must populate open_track even though
    ///   open_track_iterator is what rcheevos actually invokes.
    /// - hash_disc.c:33-44 cdreader dispatch routes by callback-set, not by
    ///   file extension → we own dispatch ourselves.
    /// - cdreader.c:824-831 default cdreader leaves open_track = NULL and
    ///   uses open_track_iterator exclusively.
    /// </summary>
    internal static class RcheevosChdCdReader
    {
        private const string Rcheevos = "rcheevos";

        // ── rcheevos exports ────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        public struct RcHashCdreader
        {
            public IntPtr open_track;
            public IntPtr read_sector;
            public IntPtr close_track;
            public IntPtr first_track_sector;
            public IntPtr open_track_iterator;
        }

        // Matches rc_hash.h:45-52 rc_hash_filereader_t — 5 function pointers.
        [StructLayout(LayoutKind.Sequential)]
        public struct RcHashFilereader
        {
            public IntPtr open;
            public IntPtr seek;
            public IntPtr tell;
            public IntPtr read;
            public IntPtr close;
        }

        // Matches the nested encryption substruct at rc_hash.h:142-145.
        // CRITICAL: this is a 2-IntPtr nested struct, NOT 4 flat IntPtrs as
        // a prior draft of the plan had it. Wrong size here causes
        // rc_client_set_hash_callbacks' memcpy to read past our buffer.
        [StructLayout(LayoutKind.Sequential)]
        public struct RcHashEncryption
        {
            public IntPtr get_3ds_cia_normal_key;
            public IntPtr get_3ds_ncch_normal_keys;
        }

        // Matches rc_hash.h:132-147 rc_hash_callbacks_t (with RC_HASH_NO_DISC
        // NOT defined, which is the default for rcheevos builds with disc
        // support — confirmed by rcheevos.dll exporting rc_hash_get_default_cdreader).
        // Expected size on x64: 2 + 5 + 5 + 2 IntPtrs = 14 × 8 = 112 bytes.
        [StructLayout(LayoutKind.Sequential)]
        public struct RcHashCallbacks
        {
            public IntPtr verbose_message;
            public IntPtr error_message;
            public RcHashFilereader filereader;
            public RcHashCdreader   cdreader;
            public RcHashEncryption encryption;
        }

        [DllImport(Rcheevos, CallingConvention = CallingConvention.Cdecl)]
        private static extern void rc_hash_get_default_cdreader(out RcHashCdreader cdreader);

        [DllImport(Rcheevos, CallingConvention = CallingConvention.Cdecl)]
        private static extern void rc_client_set_hash_callbacks(IntPtr client, ref RcHashCallbacks callbacks);

        // ── Default cdreader captured at init ───────────────────────────

        private static RcHashCdreader _defaultCdreader;
        private static bool _defaultCaptured;
        private static readonly object _initLock = new();

        // RC_HASH_CDTRACK_* magic track numbers — match rc_hash.h:60-65.
        private const uint TRACK_FIRST_DATA               = unchecked((uint)-1);
        private const uint TRACK_LAST                     = unchecked((uint)-2);
        private const uint TRACK_LARGEST                  = unchecked((uint)-3);
        private const uint TRACK_FIRST_OF_SECOND_SESSION  = unchecked((uint)-4);

        // ── Track handle bookkeeping ────────────────────────────────────

        // Discriminated handle: backed by either libchdr (we manage everything)
        // or the default cdreader (we forward calls to it). Stored as
        // GCHandle.ToIntPtr → returned to rcheevos as opaque pointer.
        private sealed class TrackHandle
        {
            // Set for libchdr-backed handles
            public IntPtr Chd;
            public uint UnitBytes;        // bytes per unit on disk (typically 2448 = 2352 raw + 96 subchannel)
            public uint UnitsPerHunk;
            public uint FirstSector;
            public uint SectorHeaderSize; // bytes to skip per sector to reach cooked data (16 MODE1_RAW, 24 MODE2_RAW, 0 cooked/audio)
            public uint RawDataSize;      // cooked-data bytes per sector (2048 typical, 2324 for MODE2_FORM2, 2352 audio)
            public byte[]? HunkCache;
            public uint CachedHunkNum;    // uint.MaxValue = no hunk cached

            // Delta added to every incoming disc LBA before the hunk
            // lookup. Discovered by auto-probe: when the boot signature
            // is found at chdFrame ≠ firstSector (common on GD-ROM
            // where chdman packs the HD-area track 1 frame later than
            // the cumulative-frames accumulator predicts), the shift is
            // the chdFrame-minus-canonical-LBA difference. Stays at 0
            // for ordinary CD-CHDs and is applied uniformly so all of
            // rcheevos's reads (track-start, PVD, directory records,
            // boot executable) land on the right chd_frame.
            public int LbaToChdShift;

            // Set for default-backed handles
            public bool IsDefault;
            public IntPtr DefaultHandle;
        }

        // Maps CHD track type strings to (header_skip, cooked_data_size).
        // Matches the default cdreader's interpretation at cdreader.c:42-80.
        // Tests a candidate `dataStart` by reading the would-be data sectors
        // 1 and 16 from the CHD and looking for any known CD boot signature:
        //   - PCE-CD boot record: "PC Engine CD-ROM SYSTEM" at byte 32 of
        //     data sector 1 (the canonical PCE-CD boot block)
        //   - ISO9660 Primary Volume Descriptor: "CD001" at byte 1 of data
        //     sector 16 (works for PS1, Saturn, SegaCD, most CD-based discs)
        // Returns true if ANY signature matches at the implied location.
        private static bool ProbeDataStart(IntPtr chd, uint dataStart, uint unitsPerHunk, uint unitBytes, uint headerSize)
        {
            try
            {
                // Sector 0: Dreamcast GD-ROM IP.BIN check
                // ("SEGA SEGAKATANA " at byte 0). Cheapest read; cover the
                // common Dreamcast track-3-start case before reading more.
                var s0 = ReadSlotBytes(chd, dataStart, unitsPerHunk, unitBytes, headerSize, 16);
                if (s0 != null
                    && s0[0] == 'S' && s0[1] == 'E' && s0[2] == 'G' && s0[3] == 'A'
                    && s0[4] == ' ' && s0[5] == 'S' && s0[6] == 'E' && s0[7] == 'G'
                    && s0[8] == 'A' && s0[9] == 'K' && s0[10] == 'A' && s0[11] == 'T'
                    && s0[12] == 'A' && s0[13] == 'N' && s0[14] == 'A')
                {
                    return true;
                }

                // Sector 1: PCE-CD boot block check
                var s1 = ReadSlotBytes(chd, dataStart + 1, unitsPerHunk, unitBytes, headerSize, 64);
                if (s1 != null
                    && s1[32] == 'P' && s1[33] == 'C' && s1[34] == ' '
                    && s1[35] == 'E' && s1[36] == 'n' && s1[37] == 'g'
                    && s1[38] == 'i' && s1[39] == 'n' && s1[40] == 'e')
                {
                    return true;
                }

                // Sector 16: ISO9660 PVD check ("CD001" at byte 1)
                var s16 = ReadSlotBytes(chd, dataStart + 16, unitsPerHunk, unitBytes, headerSize, 8);
                if (s16 != null
                    && s16[1] == 'C' && s16[2] == 'D'
                    && s16[3] == '0' && s16[4] == '0' && s16[5] == '1')
                {
                    return true;
                }
            }
            catch { /* fall through */ }
            return false;
        }

        // Reads `length` bytes of cooked data from the unit slot at `chdFrame`.
        // Returns the byte array or null on read failure.
        private static byte[]? ReadSlotBytes(IntPtr chd, uint chdFrame, uint unitsPerHunk, uint unitBytes, uint headerSize, uint length)
        {
            uint hunkNum = chdFrame / unitsPerHunk;
            uint unitInHunk = chdFrame % unitsPerHunk;
            var hunkBuf = new byte[unitsPerHunk * unitBytes];
            var pin = GCHandle.Alloc(hunkBuf, GCHandleType.Pinned);
            try
            {
                if (LibChdr.chd_read(chd, hunkNum, pin.AddrOfPinnedObject()) != LibChdr.ChdError.None)
                    return null;
            }
            finally { pin.Free(); }
            int slotOffset = (int)(unitInHunk * unitBytes + headerSize);
            if (slotOffset + length > hunkBuf.Length) return null;
            var result = new byte[length];
            Array.Copy(hunkBuf, slotOffset, result, 0, length);
            return result;
        }

        private static (uint headerSize, uint rawDataSize) GetSectorGeometry(string trackType, uint unitBytes)
        {
            // RetroArch's CHD reading is layered: chdstream returns bytes at
            // offset 0 of each slot (frame_offset=0 always), but CDFS sits
            // ABOVE chdstream and applies its own per-sector header_size:
            //   cdfs_seek_track_sector(track, N):
            //     seek to N * stream_sector_size + stream_sector_header_size
            //   ↑ N=relative sector              ↑ this is set per-mode in
            //   cdfs_open_chd_track based on frame_size:
            //     frame_size==2352 (RAW) → header_size = 16
            //     frame_size==2336 (XA)  → header_size = 8
            //     frame_size==2048 (cooked) → header_size = 0 (already cooked)
            //
            // Since our cdreader collapses both layers into one read, the
            // header skip must be applied here. MODE1 (bare) = cooked stored
            // (2048 bytes data at slot offset 0). MODE1_RAW = raw 2352-byte
            // sectors stored, cooked data starts at slot offset 16. Same for
            // MODE2 family: cooked stored at 0, raw stored skips 24 bytes.
            _ = unitBytes;
            switch (trackType.ToUpperInvariant())
            {
                case "MODE1":          return (0,  2048);  // cooked: data at slot offset 0
                case "MODE1_RAW":      return (16, 2048);  // raw: skip 12-byte sync + 4-byte header
                case "MODE2":          return (0,  2336);  // XA cooked, no sync/header
                case "MODE2_RAW":      return (24, 2048);  // raw: skip 12 sync + 4 header + 8 subheader
                case "MODE2_FORM1":    return (24, 2048);
                case "MODE2_FORM_MIX": return (24, 2048);
                case "MODE2_FORM2":    return (24, 2324);
                case "AUDIO":          return (0,  2352);
                case "DVD":            return (0,  2048);  // PSP UMD: 2048-byte sectors, no sync/header
                default:               return (16, 2048);  // assume raw Mode1 (most common chdman default)
            }
        }

        // ── Delegate type aliases matching rc_hash.h:69-89 ──────────────

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr OpenTrackDelegate(IntPtr pathUtf8, uint track);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr OpenTrackIteratorDelegate(IntPtr pathUtf8, uint track, IntPtr iterator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate UIntPtr ReadSectorDelegate(IntPtr trackHandle, uint sector, IntPtr buffer, UIntPtr requestedBytes);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void CloseTrackDelegate(IntPtr trackHandle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint FirstTrackSectorDelegate(IntPtr trackHandle);

        // rc_hash.h:130 — void(const char*, const rc_hash_iterator*)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void HashMessageDelegate(IntPtr messageUtf8, IntPtr iterator);

        // Pinned delegate references for app-lifetime callbacks. Held in
        // static fields so they're never GC'd while rcheevos has the
        // function pointers.
        private static OpenTrackDelegate? _openTrackDel;
        private static OpenTrackIteratorDelegate? _openTrackIteratorDel;
        private static ReadSectorDelegate? _readSectorDel;
        private static CloseTrackDelegate? _closeTrackDel;
        private static FirstTrackSectorDelegate? _firstTrackSectorDel;
        private static HashMessageDelegate? _verboseMessageDel;
        private static HashMessageDelegate? _errorMessageDel;

        // Cached delegates for the captured default cdreader's function
        // pointers — materialized once at init to avoid per-call
        // Marshal.GetDelegateForFunctionPointer allocations in the
        // non-CHD dispatch path (hot path during cue+bin/.gdi/.iso hashing).
        private static OpenTrackDelegate? _defaultOpenTrackDel;
        private static OpenTrackIteratorDelegate? _defaultOpenTrackIteratorDel;
        private static ReadSectorDelegate? _defaultReadSectorDel;
        private static CloseTrackDelegate? _defaultCloseTrackDel;
        private static FirstTrackSectorDelegate? _defaultFirstTrackSectorDel;

        // ── Public init / accessor ──────────────────────────────────────

        /// <summary>
        /// Captures the default cdreader and builds the dispatcher cdreader
        /// struct. Safe to call multiple times; idempotent.
        /// Returns an <see cref="RcHashCdreader"/> ready to be embedded in
        /// rc_hash_callbacks and passed to rc_client_set_hash_callbacks.
        /// </summary>
        public static RcHashCdreader GetCdreader()
        {
            lock (_initLock)
            {
                if (!_defaultCaptured)
                {
                    try
                    {
                        rc_hash_get_default_cdreader(out _defaultCdreader);
                        _defaultCaptured = true;

                        // Materialize the default delegates once. Saves
                        // 3000-5000 throwaway allocations per ISO hash.
                        if (_defaultCdreader.open_track != IntPtr.Zero)
                            _defaultOpenTrackDel = Marshal.GetDelegateForFunctionPointer<OpenTrackDelegate>(_defaultCdreader.open_track);
                        if (_defaultCdreader.open_track_iterator != IntPtr.Zero)
                            _defaultOpenTrackIteratorDel = Marshal.GetDelegateForFunctionPointer<OpenTrackIteratorDelegate>(_defaultCdreader.open_track_iterator);
                        if (_defaultCdreader.read_sector != IntPtr.Zero)
                            _defaultReadSectorDel = Marshal.GetDelegateForFunctionPointer<ReadSectorDelegate>(_defaultCdreader.read_sector);
                        if (_defaultCdreader.close_track != IntPtr.Zero)
                            _defaultCloseTrackDel = Marshal.GetDelegateForFunctionPointer<CloseTrackDelegate>(_defaultCdreader.close_track);
                        if (_defaultCdreader.first_track_sector != IntPtr.Zero)
                            _defaultFirstTrackSectorDel = Marshal.GetDelegateForFunctionPointer<FirstTrackSectorDelegate>(_defaultCdreader.first_track_sector);
                    }
                    catch (Exception ex)
                    {
                        RaLog.Write($"[RcheevosChd] failed to capture default cdreader: {ex.Message}");
                        // Continue with zeroed defaults; CHD will work, cue+bin will fail
                        _defaultCaptured = true;
                    }
                }

                if (_openTrackDel == null)
                {
                    _openTrackDel          = OpenTrackDispatch;
                    _openTrackIteratorDel  = OpenTrackIteratorDispatch;
                    _readSectorDel         = ReadSectorDispatch;
                    _closeTrackDel         = CloseTrackDispatch;
                    _firstTrackSectorDel   = FirstTrackSectorDispatch;
                }

                return new RcHashCdreader
                {
                    open_track          = Marshal.GetFunctionPointerForDelegate(_openTrackDel),
                    open_track_iterator = Marshal.GetFunctionPointerForDelegate(_openTrackIteratorDel!),
                    read_sector         = Marshal.GetFunctionPointerForDelegate(_readSectorDel!),
                    close_track         = Marshal.GetFunctionPointerForDelegate(_closeTrackDel!),
                    first_track_sector  = Marshal.GetFunctionPointerForDelegate(_firstTrackSectorDel!),
                };
            }
        }

        // ── Callback bodies (every body wrapped in try/catch to prevent
        // exception propagation across the native boundary, which would
        // fast-fail the process per .NET runtime rules) ─────────────────

        // open_track is required to be non-NULL for rc_hash_merge_callbacks
        // to copy our struct (hash.c:980), but in practice rcheevos prefers
        // open_track_iterator (hash_disc.c:35-36). We make this a thin
        // dispatcher that synthesizes the iterator-less case.
        private static IntPtr OpenTrackDispatch(IntPtr pathUtf8, uint track)
        {
            try { return OpenTrackCore(pathUtf8, track, IntPtr.Zero); }
            catch (Exception ex) { LogException(nameof(OpenTrackDispatch), ex); return IntPtr.Zero; }
        }

        private static IntPtr OpenTrackIteratorDispatch(IntPtr pathUtf8, uint track, IntPtr iterator)
        {
            try { return OpenTrackCore(pathUtf8, track, iterator); }
            catch (Exception ex) { LogException(nameof(OpenTrackIteratorDispatch), ex); return IntPtr.Zero; }
        }

        private static IntPtr OpenTrackCore(IntPtr pathUtf8, uint track, IntPtr iterator)
        {
            string? path = Marshal.PtrToStringUTF8(pathUtf8);
            if (string.IsNullOrEmpty(path)) return IntPtr.Zero;

            bool isChd = Path.GetExtension(path).Equals(".chd", StringComparison.OrdinalIgnoreCase);

            if (!isChd)
            {
                // Delegate to default cdreader's open_track_iterator (preferred)
                // or open_track (legacy fallback).
                IntPtr defaultHandle = IntPtr.Zero;
                if (_defaultOpenTrackIteratorDel != null && iterator != IntPtr.Zero)
                    defaultHandle = _defaultOpenTrackIteratorDel(pathUtf8, track, iterator);
                else if (_defaultOpenTrackDel != null)
                    defaultHandle = _defaultOpenTrackDel(pathUtf8, track);

                if (defaultHandle == IntPtr.Zero) return IntPtr.Zero;

                var wrapper = new TrackHandle { IsDefault = true, DefaultHandle = defaultHandle };
                return GCHandle.ToIntPtr(GCHandle.Alloc(wrapper));
            }

            return OpenChdTrack(path, track);
        }

        private static IntPtr OpenChdTrack(string path, uint track)
        {
            LibChdr.ChdError err = LibChdr.chd_open(path, LibChdr.CHD_OPEN_READ, IntPtr.Zero, out IntPtr chd);
            if (err != LibChdr.ChdError.None)
            {
                RaLog.Write($"[RcheevosChd] chd_open '{path}' failed: {LibChdr.ErrorString(err)}");
                return IntPtr.Zero;
            }

            try
            {
                var hdr = LibChdr.ReadHeader(chd);

                if (hdr.Version < 5)
                {
                    RaLog.Write($"[RcheevosChd] '{path}' is CHD v{hdr.Version}; v5+ required for achievement hashing. Re-create with chdman 0.205+.");
                    LibChdr.chd_close(chd);
                    return IntPtr.Zero;
                }

                if (hdr.UnitBytes == 0 || hdr.HunkBytes == 0 || hdr.HunkBytes % hdr.UnitBytes != 0)
                {
                    RaLog.Write($"[RcheevosChd] '{path}' has unsupported hunk/unit geometry (hunk={hdr.HunkBytes}, unit={hdr.UnitBytes})");
                    LibChdr.chd_close(chd);
                    return IntPtr.Zero;
                }

                uint unitsPerHunk = hdr.HunkBytes / hdr.UnitBytes;

                // Walk track metadata to resolve the requested track to
                // its disc LBA (StartSector — what rcheevos sees) AND its
                // CHD-storage frame (ChdFrameStart — where the bytes live).
                // For ordinary CD-CHDs the two are identical. For GD-ROM
                // (CHGD) StartSector is rebased to the canonical 45000
                // anchor while ChdFrameStart is the accumulator-derived
                // storage position (often 45000-45010).
                if (!ResolveTrack(chd, track, unitsPerHunk, out uint firstSector, out uint chdFrameStart, out string trackType, out uint pregapFrames))
                {
                    RaLog.Write($"[RcheevosChd] '{path}' track {track} not found in metadata");
                    LibChdr.chd_close(chd);
                    return IntPtr.Zero;
                }

                var (sectorHeaderSize, rawDataSize) = GetSectorGeometry(trackType, hdr.UnitBytes);

                // Base lba shift = chdFrame - firstSector. For ordinary
                // CDs this is 0; for GD-ROM rebased tracks it's the
                // 0-10 frame delta between accumulator and canonical
                // 45000 anchor. Auto-probe then refines by ±2 /
                // -pregap in case the actual data sits a hair off the
                // computed chdFrameStart (chdman alignment quirks).
                int lbaShift = (int)((long)chdFrameStart - (long)firstSector);
                if (!trackType.StartsWith("AUDIO", StringComparison.OrdinalIgnoreCase))
                {
                    var deltas = pregapFrames > 0
                        ? new int[] { 0, -(int)pregapFrames, 1, -1, 2, -2 }
                        : new int[] { 0, 1, -1, 2, -2 };
                    int picked = int.MinValue;
                    foreach (int d in deltas)
                    {
                        long cand = (long)chdFrameStart + d;
                        if (cand < 0) continue;
                        if (ProbeDataStart(chd, (uint)cand, unitsPerHunk, hdr.UnitBytes, sectorHeaderSize))
                        {
                            picked = d;
                            break;
                        }
                    }
                    if (picked == int.MinValue)
                    {
                        RaLog.Write($"[RcheevosChd]   auto-probe: no known boot signature within ±2 / -pregap window of chdFrame={chdFrameStart}; lba shift={lbaShift}");
                    }
                    else
                    {
                        lbaShift += picked;
                        RaLog.Write($"[RcheevosChd]   auto-probe: boot signature found at chdFrame{(picked >= 0 ? "+" : "")}{picked}; firstSector={firstSector} chdFrame={chdFrameStart} lba shift={lbaShift}");
                    }
                }

                var th = new TrackHandle
                {
                    Chd = chd,
                    UnitBytes = hdr.UnitBytes,
                    UnitsPerHunk = unitsPerHunk,
                    FirstSector = firstSector,
                    SectorHeaderSize = sectorHeaderSize,
                    RawDataSize = rawDataSize,
                    HunkCache = null,        // lazy-allocated on first read
                    CachedHunkNum = uint.MaxValue,
                    LbaToChdShift = lbaShift,
                };
                // One-line summary per CHD open. Useful when something goes
                // wrong; the auto-probe outcome above this is the other half
                // of what's worth keeping. Per-track listings, byte-dumps,
                // and the boot-magic scanner are all stripped from the
                // shipping build — they live in git history.
                RaLog.Write(
                    $"[RcheevosChd] opened '{System.IO.Path.GetFileName(path)}' track {track} → " +
                    $"type={trackType}, firstSector={firstSector}, unitsPerHunk={unitsPerHunk}, " +
                    $"unitBytes={hdr.UnitBytes}, hunkCount={hdr.HunkCount}, " +
                    $"headerSize={sectorHeaderSize}, rawDataSize={rawDataSize}");

                return GCHandle.ToIntPtr(GCHandle.Alloc(th));
            }
            catch (Exception ex)
            {
                RaLog.Write($"[RcheevosChd] open_track exception for '{path}': {ex.Message}");
                LibChdr.chd_close(chd);
                return IntPtr.Zero;
            }
        }

        // Resolves the rcheevos track number (regular 1-based, or one of the
        // RC_HASH_CDTRACK_* magic constants) to (first_sector, chdFrameStart,
        // track_type). Returns false if no matching track is found.
        //
        // `firstSector` is the disc LBA returned to rcheevos.
        // `chdFrameStart` is the CHD storage frame where the track's data
        // physically lives. The two differ for GD-ROM CHGD format where
        // pad-frames represent virtual disc-side gaps not in CHD storage.
        private static bool ResolveTrack(IntPtr chd, uint requested, uint unitsPerHunk,
            out uint firstSector, out uint chdFrameStart, out string trackType, out uint pregapFrames)
        {
            firstSector = 0;
            chdFrameStart = 0;
            trackType = string.Empty;
            pregapFrames = 0;

            var tracks = ReadAllTracks(chd);

            // No CD/GD track metadata? Try the single-blob disc formats:
            //   DVD_METADATA_TAG ("DVD ")  — PSP UMD
            //   HARD_DISK_METADATA_TAG ("GDDD") — raw hard-disk image
            // Both expose the entire CHD as one synthetic data track that
            // any of (track 1 / FIRST_DATA / LARGEST) resolves to.
            if (tracks.Count == 0)
            {
                string? dvd = LibChdr.TryReadMetadataString(chd, LibChdr.DVD_METADATA_TAG, 0);
                string? gddd = dvd ?? LibChdr.TryReadMetadataString(chd, LibChdr.HARD_DISK_METADATA_TAG, 0);
                if (gddd != null)
                {
                    firstSector = 0;
                    chdFrameStart = 0;
                    trackType = "DVD";
                    return requested == 1 || requested == TRACK_FIRST_DATA || requested == TRACK_LARGEST;
                }
                return false;
            }

            // Resolve magic track values
            int idx;
            if (requested == TRACK_FIRST_DATA)
            {
                idx = tracks.FindIndex(t => !t.Type.StartsWith("AUDIO", StringComparison.OrdinalIgnoreCase));
            }
            else if (requested == TRACK_LARGEST)
            {
                idx = -1;
                uint largestFrames = 0;
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (tracks[i].Type.StartsWith("AUDIO", StringComparison.OrdinalIgnoreCase)) continue;
                    if (tracks[i].Frames > largestFrames) { largestFrames = tracks[i].Frames; idx = i; }
                }
            }
            else if (requested == TRACK_LAST)
            {
                idx = tracks.Count - 1;
            }
            else if (requested == TRACK_FIRST_OF_SECOND_SESSION)
            {
                // Not supported for now — sessions aren't surfaced in CHD metadata
                idx = -1;
            }
            else
            {
                idx = (int)requested - 1; // 1-based
            }

            if (idx < 0 || idx >= tracks.Count) return false;

            firstSector   = tracks[idx].StartSector;
            chdFrameStart = tracks[idx].ChdFrameStart;
            trackType     = tracks[idx].Type;
            pregapFrames  = tracks[idx].Pregap;
            return true;
        }

        // Parsed track metadata entry.
        //
        // `StartSector` is the absolute DISC LBA where the track's DATA
        // section begins — what rcheevos uses (matches the default cdreader's
        // first_track_sector contract, cdreader.c:819). Includes prior tracks'
        // frames AND pad (the latter only matters for GD-ROM CHGD format).
        //
        // `ChdFrameStart` is the CHD STORAGE position of the track's data
        // section — i.e. where the bytes physically live in the .chd file.
        // For ordinary CD-CHDs these are identical (pad=0 throughout). For
        // GD-ROM CHDs the pad represents a virtual disc-LBA gap that is
        // NOT in CHD storage, so StartSector and ChdFrameStart diverge by
        // the running pad accumulator.
        private readonly struct CdTrack
        {
            public readonly uint Number;
            public readonly string Type;          // MODE1_RAW, MODE2_RAW, AUDIO, etc.
            public readonly uint Frames;          // sector count of this track (incl. pregap per chdman)
            public readonly uint Pregap;
            public readonly uint Pad;             // GD-ROM only; virtual inter-track padding (CHGD format)
            public readonly uint StartSector;     // absolute DISC LBA of data section start
            public readonly uint ChdFrameStart;   // CHD STORAGE position of data section start
            public CdTrack(uint n, string t, uint f, uint pre, uint pad, uint start, uint chdStart)
            { Number = n; Type = t; Frames = f; Pregap = pre; Pad = pad; StartSector = start; ChdFrameStart = chdStart; }
        }

        private static List<CdTrack> ReadAllTracks(IntPtr chd)
        {
            var list = new List<CdTrack>();
            // Single accumulator matching RetroArch's chd_stream.c:613
            // chdstream_get_first_track_sector — sums `frames + extra`
            // (TRACK_PAD=4 alignment) only, NEVER `pad`. The disc LBA
            // that rcheevos uses IS the CHD storage frame; chdman packs
            // tracks linearly such that absolute disc LBA == chd_frame.
            // PVD-encoded LBAs reference the same coordinate system, so
            // reads pass through directly without any disc-to-chd offset.
            //
            // GD-ROM (CHGD) caveat: the LD-area tracks' total frame count
            // varies per game (Sonic/SF3/AeroWings happen to sum to
            // exactly 45000, Crazy Taxi sums to 45004). The PVD on the
            // HD-area data track is mastered with LBAs anchored at the
            // canonical 45000 boundary regardless. We patch StartSector
            // to that canonical value (45000 for the first HD-area
            // track) so PVD-driven directory traversal lands correctly;
            // the auto-probe at open_track then sets the chd-frame
            // shift to bridge the gap.
            uint chdFrame  = 0;
            bool sawPad    = false;
            int firstHdTrackIdx = -1;
            for (uint index = 0; ; index++)
            {
                // Try CDROM_TRACK_METADATA2 first (most common), then GDROM,
                // then legacy CDROM_TRACK_METADATA.
                string? blob = LibChdr.TryReadMetadataString(chd, LibChdr.CDROM_TRACK_METADATA2_TAG, index)
                            ?? LibChdr.TryReadMetadataString(chd, LibChdr.GDROM_TRACK_METADATA_TAG,  index)
                            ?? LibChdr.TryReadMetadataString(chd, LibChdr.CDROM_TRACK_METADATA_TAG,  index);
                if (blob == null) break;

                if (!TryParseTrackMetadata(blob, out uint num, out string type, out uint frames, out uint pregap, out uint pad, out string pgtype))
                    break;

                // CHD storage geometry, ported from RetroArch's chd_stream.c:
                //   absSector = cumulative CHD frame offset where THIS track starts
                //   dataStart = CHD frame offset where this track's DATA sectors begin
                //                = absSector + pregap (pregap is always in storage)
                //
                // After each track, advance absSector by `frames + pad + extra`,
                // where extra is the 0-3 frame TRACK_PAD=4 alignment chdman applies
                // to every track's storage region (matches chdstream.c
                // padding_frames()).
                //
                // We previously gated this on PGTYPE='V' per RA's chdstream comment
                // "/* Only include pregap data if it was in the track file */", but
                // empirically pregap IS in CHD storage even when PGTYPE='V' for
                // common chdman-produced CDs (Castlevania Rondo, Gate of Thunder
                // verified via scanner — boot record sits at chdFrameStart+pregap+1
                // regardless of PGTYPE). The scanner finding is ground truth; the
                // PGTYPE comment may apply only to specific chdman versions or
                // refer to something other than storage presence.
                uint extra = ((frames + 3u) & ~3u) - frames;
                uint dataStart = chdFrame + pregap;       // disc LBA == CHD storage position
                list.Add(new CdTrack(num, type, frames, pregap, pad, dataStart, dataStart));

                // Note the first non-audio track that follows a padded
                // (LD-area) track — this is the canonical Dreamcast
                // HD-area data track and the PVD's LBAs anchor to 45000
                // for it. The CHGD format is the only one that emits
                // non-zero PAD frames.
                if (pad > 0) sawPad = true;
                if (sawPad && firstHdTrackIdx < 0
                    && !type.StartsWith("AUDIO", StringComparison.OrdinalIgnoreCase))
                {
                    firstHdTrackIdx = list.Count - 1;
                }

                chdFrame  += frames + extra;              // accumulator: frames + TRACK_PAD=4 alignment only
            }

            // GD-ROM post-pass: rebase the first HD-area track's
            // StartSector (and any subsequent tracks) to the canonical
            // 45000 anchor that the disc creator used in the PVD. The
            // delta against the accumulator-derived StartSector becomes
            // the auto-probe-aware lba shift via open_track.
            if (firstHdTrackIdx >= 0)
            {
                uint accDataStart = list[firstHdTrackIdx].StartSector;
                long delta = 45000L - (long)accDataStart;
                if (delta != 0)
                {
                    for (int i = firstHdTrackIdx; i < list.Count; i++)
                    {
                        var t = list[i];
                        long s = (long)t.StartSector + delta;
                        if (s < 0) s = 0;
                        list[i] = new CdTrack(t.Number, t.Type, t.Frames, t.Pregap, t.Pad, (uint)s, t.ChdFrameStart);
                    }
                }
            }
            return list;
        }

        // Parses metadata blobs like:
        //   CHT2: "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:N PREGAP:N PGTYPE:S PGSUB:S POSTGAP:N"
        //   CHGD: "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:N PAD:N PREGAP:N PGTYPE:S PGSUB:S POSTGAP:N"
        //   CHTR: "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:N"  (legacy, no pregap/pad)
        // PGTYPE is critical: when "V" (virtual), the pregap frames are NOT
        // present in CHD storage — data sectors begin immediately at the
        // track's storage start. Otherwise pregap IS stored and data starts
        // `pregap` frames into the track's storage region.
        private static bool TryParseTrackMetadata(string blob,
            out uint num, out string type, out uint frames, out uint pregap, out uint pad, out string pgtype)
        {
            num = 0; type = string.Empty; frames = 0; pregap = 0; pad = 0; pgtype = string.Empty;
            foreach (string token in blob.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = token.IndexOf(':');
                if (colon <= 0 || colon == token.Length - 1) continue;
                string key = token.Substring(0, colon);
                string val = token.Substring(colon + 1);
                switch (key)
                {
                    case "TRACK":  uint.TryParse(val, out num); break;
                    case "TYPE":   type = val; break;
                    case "FRAMES": uint.TryParse(val, out frames); break;
                    case "PREGAP": uint.TryParse(val, out pregap); break;
                    case "PAD":    uint.TryParse(val, out pad); break;
                    case "PGTYPE": pgtype = val; break;
                }
            }
            return num > 0 && !string.IsNullOrEmpty(type) && frames > 0;
        }

        // ── read_sector ──────────────────────────────────────────────────

        private static UIntPtr ReadSectorDispatch(IntPtr trackHandle, uint sector, IntPtr buffer, UIntPtr requestedBytes)
        {
            try
            {
                if (trackHandle == IntPtr.Zero) return UIntPtr.Zero;
                var th = (TrackHandle?)GCHandle.FromIntPtr(trackHandle).Target;
                if (th == null) return UIntPtr.Zero;

                if (th.IsDefault)
                {
                    if (_defaultReadSectorDel == null) return UIntPtr.Zero;
                    return _defaultReadSectorDel(th.DefaultHandle, sector, buffer, requestedBytes);
                }

                return ReadChdSector(th, sector, buffer, (uint)requestedBytes);
            }
            catch (Exception ex)
            {
                LogException(nameof(ReadSectorDispatch), ex);
                return UIntPtr.Zero;
            }
        }

        private static UIntPtr ReadChdSector(TrackHandle th, uint sector, IntPtr buffer, uint requestedBytes)
        {
            // Mirror the default cdreader's contract (cdreader.c:766-801):
            //   read N bytes of COOKED data starting at the requested sector.
            // For raw (MODE1_RAW / MODE2_RAW) sectors, skip the per-sector
            // sync+header bytes; return only the user-data portion of each
            // sector. Multi-sector reads stitch consecutive sectors' cooked
            // data together.
            //
            // Read loop: walk one sector at a time. For each, locate the
            // hunk it lives in, decompress that hunk (cache-aware), then
            // copy at most rawDataSize bytes (or remaining requestedBytes,
            // whichever is smaller) from the post-header offset.

            uint totalCopied = 0;
            uint currentSector = sector;
            IntPtr writePtr = buffer;

            while (requestedBytes > 0)
            {
                // rcheevos passes a disc LBA in the coordinate system
                // anchored at FirstSector. Apply the auto-probe shift to
                // land on the real chd_frame. For ordinary CD-CHDs the
                // shift is 0 and disc LBA == chd_frame; for GD-ROM (and
                // any other CHD where chdman's storage frame differs
                // from the cumulative-frames accumulator) the shift
                // adjusts uniformly.
                long shifted = (long)currentSector + th.LbaToChdShift;
                if (shifted < 0)
                {
                    RaLog.Write($"[RcheevosChd] read_sector LBA {currentSector} + shift {th.LbaToChdShift} < 0");
                    return (UIntPtr)totalCopied;
                }
                uint chdSector       = (uint)shifted;
                uint hunkNum         = chdSector / th.UnitsPerHunk;
                uint unitInHunk      = chdSector % th.UnitsPerHunk;
                uint sectorStartInHunk = unitInHunk * th.UnitBytes;
                uint cookedStartInHunk = sectorStartInHunk + th.SectorHeaderSize;

                if (th.HunkCache == null)
                    th.HunkCache = new byte[(int)(th.UnitsPerHunk * th.UnitBytes)];

                if (th.CachedHunkNum != hunkNum)
                {
                    GCHandle pin = GCHandle.Alloc(th.HunkCache, GCHandleType.Pinned);
                    try
                    {
                        LibChdr.ChdError err = LibChdr.chd_read(th.Chd, hunkNum, pin.AddrOfPinnedObject());
                        if (err != LibChdr.ChdError.None)
                        {
                            RaLog.Write($"[RcheevosChd] chd_read hunk {hunkNum} failed: {LibChdr.ErrorString(err)}");
                            return (UIntPtr)totalCopied;
                        }
                    }
                    finally { pin.Free(); }
                    th.CachedHunkNum = hunkNum;
                }

                // Copy up to rawDataSize bytes (cooked portion of this sector)
                // or whatever remaining requestedBytes asks for, whichever
                // is smaller.
                uint toCopyThisSector = requestedBytes < th.RawDataSize ? requestedBytes : th.RawDataSize;

                // Defensive: ensure we don't read past the hunk's allocated buffer
                uint hunkSize = th.UnitsPerHunk * th.UnitBytes;
                if (cookedStartInHunk + toCopyThisSector > hunkSize)
                    toCopyThisSector = hunkSize - cookedStartInHunk;

                Marshal.Copy(th.HunkCache!, (int)cookedStartInHunk, writePtr, (int)toCopyThisSector);

                totalCopied   += toCopyThisSector;
                writePtr       = IntPtr.Add(writePtr, (int)toCopyThisSector);
                requestedBytes -= toCopyThisSector;
                currentSector++;

                // If this sector was the last available data, stop
                if (toCopyThisSector < th.RawDataSize) break;
            }

            return (UIntPtr)totalCopied;
        }

        // ── close_track ──────────────────────────────────────────────────

        private static void CloseTrackDispatch(IntPtr trackHandle)
        {
            try
            {
                if (trackHandle == IntPtr.Zero) return;
                var gch = GCHandle.FromIntPtr(trackHandle);
                var th = (TrackHandle?)gch.Target;

                if (th != null)
                {
                    if (th.IsDefault)
                    {
                        if (_defaultCloseTrackDel != null && th.DefaultHandle != IntPtr.Zero)
                            _defaultCloseTrackDel(th.DefaultHandle);
                    }
                    else if (th.Chd != IntPtr.Zero)
                    {
                        LibChdr.chd_close(th.Chd);
                        th.Chd = IntPtr.Zero;
                    }
                }
                gch.Free();
            }
            catch (Exception ex)
            {
                LogException(nameof(CloseTrackDispatch), ex);
            }
        }

        // ── first_track_sector ───────────────────────────────────────────

        private static uint FirstTrackSectorDispatch(IntPtr trackHandle)
        {
            try
            {
                if (trackHandle == IntPtr.Zero) return 0;
                var th = (TrackHandle?)GCHandle.FromIntPtr(trackHandle).Target;
                if (th == null) return 0;

                if (th.IsDefault)
                {
                    if (_defaultFirstTrackSectorDel == null) return 0;
                    return _defaultFirstTrackSectorDel(th.DefaultHandle);
                }
                return th.FirstSector;
            }
            catch (Exception ex)
            {
                LogException(nameof(FirstTrackSectorDispatch), ex);
                return 0;
            }
        }

        // ── Installation entry point ────────────────────────────────────

        /// <summary>
        // ── 3DS encryption callbacks ────────────────────────────────────
        // 3DS NCCH/CIA hashing requires AES key derivation via the
        // "key scrambler": NormalKey = (ROL128(KeyX, 2) XOR KeyY) + C
        // rcheevos delegates this to the caller via the encryption struct.

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GetCiaNormalKeyDelegate(IntPtr key, IntPtr commonKey, int commonKeyIndex, IntPtr tid);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GetNcchNormalKeysDelegate(IntPtr primaryKey, IntPtr secondaryKey, IntPtr keyX, IntPtr keyY);

        private static GetCiaNormalKeyDelegate? _ciaNormalKeyDel;
        private static GetNcchNormalKeysDelegate? _ncchNormalKeysDel;

        private static readonly byte[] KeyScrambleC =
        {
            0x1F, 0xF9, 0xE9, 0xAA, 0xC5, 0xFE, 0x04, 0x08,
            0x02, 0x45, 0x91, 0xDC, 0x5D, 0x52, 0x76, 0x8A
        };

        private static unsafe void ScrambleKey(byte* outKey, byte* x, byte* y)
        {
            byte* rotated = stackalloc byte[16];
            for (int i = 0; i < 16; i++)
                rotated[i] = (byte)((x[i] << 2) | (x[(i + 1) % 16] >> 6));

            for (int i = 0; i < 16; i++)
                rotated[i] ^= y[i];

            uint carry = 0;
            for (int i = 15; i >= 0; i--)
            {
                uint sum = (uint)rotated[i] + KeyScrambleC[i] + carry;
                outKey[i] = (byte)sum;
                carry = sum >> 8;
            }
        }

        private static unsafe void OnGetCiaNormalKey(IntPtr key, IntPtr commonKey, int commonKeyIndex, IntPtr tid)
        {
            try { ScrambleKey((byte*)key, (byte*)commonKey, (byte*)tid); }
            catch { }
        }

        private static unsafe void OnGetNcchNormalKeys(IntPtr primaryKey, IntPtr secondaryKey, IntPtr keyX, IntPtr keyY)
        {
            try
            {
                ScrambleKey((byte*)primaryKey, (byte*)keyX, (byte*)keyY);
                ScrambleKey((byte*)secondaryKey, (byte*)keyX, (byte*)keyY + 16);
            }
            catch { }
        }

        private static RcHashEncryption GetEncryptionCallbacks()
        {
            _ciaNormalKeyDel  ??= OnGetCiaNormalKey;
            _ncchNormalKeysDel ??= OnGetNcchNormalKeys;
            return new RcHashEncryption
            {
                get_3ds_cia_normal_key   = Marshal.GetFunctionPointerForDelegate(_ciaNormalKeyDel),
                get_3ds_ncch_normal_keys = Marshal.GetFunctionPointerForDelegate(_ncchNormalKeysDel),
            };
        }

        /// <summary>
        /// Builds the full <see cref="RcHashCallbacks"/> struct (filereader
        /// + cdreader + encryption) and registers it with the given rc_client.
        /// Call once after rc_client_create.
        /// After this call, CHD content identifies via libchdr-backed
        /// reading; all other extensions continue through rcheevos's
        /// built-in default cdreader. 3DS content hashes via the key
        /// scrambler callbacks.
        /// </summary>
        public static void InstallInto(IntPtr client)
        {
            if (client == IntPtr.Zero) return;

            // One-time struct-size sanity check. Expected x64 size:
            // 2+5+5+2 = 14 IntPtrs × 8 = 112 bytes. If this fails, our
            // RcHashCallbacks layout doesn't match rcheevos's expectations
            // and the upcoming memcpy in rc_client_set_hash_callbacks
            // would silently read past our buffer or skip fields.
            int expectedSize = IntPtr.Size * 14;
            int actualSize = Marshal.SizeOf<RcHashCallbacks>();
            if (actualSize != expectedSize)
            {
                RaLog.Write(
                    $"[RcheevosChd] FATAL: RcHashCallbacks size mismatch: expected {expectedSize}, got {actualSize}. " +
                    $"Likely C# struct layout drift from rc_hash_callbacks_t. CHD identification disabled.");
                return;
            }

            try
            {
                _verboseMessageDel ??= VerboseMessageDispatch;
                _errorMessageDel   ??= ErrorMessageDispatch;

                var callbacks = new RcHashCallbacks
                {
                    verbose_message = Marshal.GetFunctionPointerForDelegate(_verboseMessageDel),
                    error_message   = Marshal.GetFunctionPointerForDelegate(_errorMessageDel),
                    filereader      = default,        // all-null → rcheevos uses default file I/O
                    cdreader        = GetCdreader(),
                    encryption      = GetEncryptionCallbacks(),
                };

                rc_client_set_hash_callbacks(client, ref callbacks);
                RaLog.Write($"[RcheevosChd] cdreader installed (CHD support active for PS1/Saturn/SegaCD/Dreamcast/PSP/TG-CD/3DO/NGCD). Struct size {actualSize} bytes, default cdreader captured={(_defaultCdreader.open_track_iterator != IntPtr.Zero || _defaultCdreader.open_track != IntPtr.Zero)}.");
            }
            catch (Exception ex)
            {
                RaLog.Write($"[RcheevosChd] InstallInto failed: {ex.Message}");
            }
        }

        // ── Util ─────────────────────────────────────────────────────────

        private static void LogException(string where, Exception ex)
        {
            RaLog.Write($"[RcheevosChd:{where}] {ex.GetType().Name}: {ex.Message}");
        }

        // rcheevos hash-pipeline message taps. These surface the exact
        // hash_disc.c failure reason (e.g. "Not a Dreamcast CD", "Could
        // not locate boot executable") that's otherwise invisible from
        // the launch-identify wrapper's generic "hash generation failed".
        private static void VerboseMessageDispatch(IntPtr messageUtf8, IntPtr iterator)
        {
            try
            {
                string? msg = Marshal.PtrToStringUTF8(messageUtf8);
                if (!string.IsNullOrEmpty(msg))
                    RaLog.Write($"[rcheevos] {msg}");
            }
            catch { /* never propagate into native */ }
        }

        private static void ErrorMessageDispatch(IntPtr messageUtf8, IntPtr iterator)
        {
            try
            {
                string? msg = Marshal.PtrToStringUTF8(messageUtf8);
                if (!string.IsNullOrEmpty(msg))
                    RaLog.Write($"[rcheevos:error] {msg}");
            }
            catch { /* never propagate into native */ }
        }
    }
}
