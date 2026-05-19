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
    /// supported CD console (PS1, PS2, Saturn, SegaCD, Dreamcast, PSP,
    /// TG-CD, 3DO, NGCD, PC-FX).
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

            // Set for default-backed handles
            public bool IsDefault;
            public IntPtr DefaultHandle;
        }

        // Maps CHD track type strings to (header_skip, cooked_data_size).
        // Matches the default cdreader's interpretation at cdreader.c:42-80.
        private static (uint headerSize, uint rawDataSize) GetSectorGeometry(string trackType)
        {
            // Normalize — track types are uppercase per chdman
            switch (trackType.ToUpperInvariant())
            {
                case "MODE1":         return (0,  2048);  // cooked 2048-byte sectors
                case "MODE1_RAW":     return (16, 2048);  // raw 2352-byte sectors, skip sync+header
                case "MODE2":         return (0,  2336);  // cooked 2336-byte mode 2 (rare)
                case "MODE2_RAW":     return (24, 2048);  // raw 2352-byte mode 2 form 1 (PSX default)
                case "MODE2_FORM1":   return (24, 2048);
                case "MODE2_FORM2":   return (24, 2324);
                case "MODE2_FORM_MIX":return (24, 2048);  // detect per-sector via subheader — approximate as Form 1
                case "AUDIO":         return (0,  2352);  // raw audio (rcheevos shouldn't hash this)
                default:              return (16, 2048);  // fallback: assume MODE1_RAW
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

        // Pinned delegate references for app-lifetime callbacks. Held in
        // static fields so they're never GC'd while rcheevos has the
        // function pointers.
        private static OpenTrackDelegate? _openTrackDel;
        private static OpenTrackIteratorDelegate? _openTrackIteratorDel;
        private static ReadSectorDelegate? _readSectorDel;
        private static CloseTrackDelegate? _closeTrackDel;
        private static FirstTrackSectorDelegate? _firstTrackSectorDel;

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

                // Walk track metadata to resolve the requested track number to
                // its first sector. Supports CDROM_TRACK_METADATA2/CHGD/CHTR
                // for CD/GD-ROM, and HARD_DISK_METADATA_TAG for PS2 DVD-CHDs.
                if (!ResolveTrack(chd, track, unitsPerHunk, out uint firstSector, out string trackType))
                {
                    RaLog.Write($"[RcheevosChd] '{path}' track {track} not found in metadata");
                    LibChdr.chd_close(chd);
                    return IntPtr.Zero;
                }

                var (sectorHeaderSize, rawDataSize) = GetSectorGeometry(trackType);

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
                };
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
        // RC_HASH_CDTRACK_* magic constants) to (first_sector, track_type).
        // Returns false if no matching track is found.
        private static bool ResolveTrack(IntPtr chd, uint requested, uint unitsPerHunk,
            out uint firstSector, out string trackType)
        {
            firstSector = 0;
            trackType = string.Empty;

            var tracks = ReadAllTracks(chd);

            // GDDD (HARD_DISK_METADATA_TAG) — PS2 DVD-CHD or hard-disk CHD.
            // No track structure; the whole image is one synthetic track.
            if (tracks.Count == 0)
            {
                string? gddd = LibChdr.TryReadMetadataString(chd, LibChdr.HARD_DISK_METADATA_TAG, 0);
                if (gddd != null)
                {
                    firstSector = 0;
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

            firstSector = tracks[idx].StartSector;
            trackType = tracks[idx].Type;
            return true;
        }

        // Parsed track metadata entry. StartSector is the absolute LBA where
        // the track's DATA section begins — i.e., accumulated previous tracks'
        // frames+pad PLUS this track's pregap. Matches what the default
        // cdreader's first_track_sector returns (cdreader.c:819).
        private readonly struct CdTrack
        {
            public readonly uint Number;
            public readonly string Type;       // MODE1_RAW, MODE2_RAW, AUDIO, etc.
            public readonly uint Frames;       // sector count of this track (incl. pregap per chdman)
            public readonly uint Pregap;
            public readonly uint Pad;          // GD-ROM only; inter-track padding (CHGD format)
            public readonly uint StartSector;  // absolute LBA of data section start
            public CdTrack(uint n, string t, uint f, uint pre, uint pad, uint start)
            { Number = n; Type = t; Frames = f; Pregap = pre; Pad = pad; StartSector = start; }
        }

        private static List<CdTrack> ReadAllTracks(IntPtr chd)
        {
            var list = new List<CdTrack>();
            uint absSector = 0;
            for (uint index = 0; ; index++)
            {
                // Try CDROM_TRACK_METADATA2 first (most common), then GDROM,
                // then legacy CDROM_TRACK_METADATA.
                string? blob = LibChdr.TryReadMetadataString(chd, LibChdr.CDROM_TRACK_METADATA2_TAG, index)
                            ?? LibChdr.TryReadMetadataString(chd, LibChdr.GDROM_TRACK_METADATA_TAG,  index)
                            ?? LibChdr.TryReadMetadataString(chd, LibChdr.CDROM_TRACK_METADATA_TAG,  index);
                if (blob == null) break;

                if (!TryParseTrackMetadata(blob, out uint num, out string type, out uint frames, out uint pregap, out uint pad))
                    break;

                // Data section start = accumulated previous tracks + this track's pregap.
                // chdman: FRAMES includes pregap; PAD (CHGD only) is extra inter-track
                // padding not in FRAMES — must accumulate frames + pad for the next track.
                uint dataStart = absSector + pregap;
                list.Add(new CdTrack(num, type, frames, pregap, pad, dataStart));
                absSector += frames + pad;
            }
            return list;
        }

        // Parses metadata blobs like:
        //   CHT2: "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:N PREGAP:N PGTYPE:S PGSUB:S POSTGAP:N"
        //   CHGD: "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:N PAD:N PREGAP:N PGTYPE:S PGSUB:S POSTGAP:N"
        //   CHTR: "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:N"  (legacy, no pregap/pad)
        // Tolerates missing optional fields.
        private static bool TryParseTrackMetadata(string blob,
            out uint num, out string type, out uint frames, out uint pregap, out uint pad)
        {
            num = 0; type = string.Empty; frames = 0; pregap = 0; pad = 0;
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
                uint hunkNum = currentSector / th.UnitsPerHunk;
                uint unitInHunk = currentSector % th.UnitsPerHunk;
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
        /// Builds the full <see cref="RcHashCallbacks"/> struct (filereader
        /// + cdreader + encryption — message callbacks null) and registers
        /// it with the given rc_client. Call once after rc_client_create.
        /// After this call, CHD content identifies via libchdr-backed
        /// reading; all other extensions continue through rcheevos's
        /// built-in default cdreader.
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
                var callbacks = new RcHashCallbacks
                {
                    verbose_message = IntPtr.Zero,
                    error_message   = IntPtr.Zero,
                    filereader      = default,        // all-null → rcheevos uses default file I/O
                    cdreader        = GetCdreader(),
                    encryption      = default,        // we don't ship 3DS CHD support
                };

                rc_client_set_hash_callbacks(client, ref callbacks);
                RaLog.Write($"[RcheevosChd] cdreader installed (CHD support active for PS1/PS2/Saturn/SegaCD/Dreamcast/PSP/TG-CD/3DO/NGCD/PC-FX). Struct size {actualSize} bytes, default cdreader captured={(_defaultCdreader.open_track_iterator != IntPtr.Zero || _defaultCdreader.open_track != IntPtr.Zero)}.");
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
    }
}
