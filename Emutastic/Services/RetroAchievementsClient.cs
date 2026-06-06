using System;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static Emutastic.Services.RcheevosInterop;

namespace Emutastic.Services
{
    /// <summary>
    /// High-level wrapper around the rcheevos rc_client API.
    /// Manages login, game loading, per-frame processing, and achievement events.
    /// </summary>
    public class RetroAchievementsClient : IDisposable
    {
        private IntPtr _client;
        private LibretroCore? _core;
        private bool _disposed;

        // Keep delegates alive so GC doesn't collect them while native code holds pointers.
        private ReadMemoryFunc? _readMemoryDelegate;
        private ServerCallFunc? _serverCallDelegate;
        private EventHandlerFunc? _eventHandlerDelegate;
        private MessageCallbackFunc? _logDelegate;

        // Cached memory region pointers (refreshed each frame is too slow;
        // these are stable for the lifetime of a loaded game).
        private IntPtr _systemRamPtr;
        private uint _systemRamSize;
        private IntPtr _saveRamPtr;
        private uint _saveRamSize;
        private IntPtr _videoRamPtr;
        private uint _videoRamSize;

        // Identifies us to RA's server. RA's hardcore policy:
        //   * Missing / unrecognized User-Agent → server downgrades hardcore
        //     unlocks to softcore (or returns 'emulator unknown' when
        //     hardcore is explicitly enabled).
        //   * Even with a valid UA, the emulator name must be on RA's
        //     approved list for hardcore unlocks to actually count.
        // Format per RA's hardcore-compliance docs:
        //   "EmulatorName/v1.0.0 (OSName 10.0)"
        // We append rc_client_get_user_agent_clause's string later if rcheevos
        // exposes one; the static UA below covers the per-emulator portion
        // that the server keys on.
        private static readonly HttpClient _http = CreateRcheevosHttp();

        private static HttpClient CreateRcheevosHttp()
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(EmutasticUserAgent.Build());
            return http;
        }

        /// <summary>
        /// Updates the rcheevos HTTP client's User-Agent to include the active
        /// libretro core's name and version. Call once per game-launch BEFORE
        /// rcheevos login/identify so the UA on those requests reflects the
        /// core that will produce subsequent unlock events.
        ///
        /// Safe to call repeatedly across sessions; passing null/blank values
        /// reverts to the product/OS-only UA. Not thread-safe — assumes the
        /// caller invokes this when no rcheevos request is mid-flight (true at
        /// init time before any frames run).
        /// </summary>
        public static void SetCoreContext(string? coreName, string? coreVersion)
        {
            _http.DefaultRequestHeaders.UserAgent.Clear();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(EmutasticUserAgent.Build(coreName, coreVersion));
        }

        /// <summary>Fired on the emulation thread when an achievement is triggered.</summary>
        public event Action<AchievementInfo>? AchievementTriggered;

        /// <summary>Fired when the player completes the game (all achievements).</summary>
        public event Action? GameCompleted;

        /// <summary>Fired when rcheevos requests an emulator reset (hardcore toggle).</summary>
        public event Action? ResetRequested;

        /// <summary>Fired for achievement progress updates (show/update/hide).</summary>
        public event Action<AchievementInfo?, bool>? ProgressIndicatorChanged;

        /// <summary>
        /// Fired when a challenge achievement primes/un-primes —
        /// rcheevos CHALLENGE_INDICATOR_SHOW/HIDE. Several can be active at once.
        /// Fires on the emulation thread; subscribers must marshal to UI.
        /// </summary>
        public event Action<AchievementInfo, bool>? ChallengeIndicatorChanged;

        /// <summary>
        /// Fired on the emulation thread when rcheevos delivers a leaderboard
        /// scoreboard post-submission (Phase 6b — used for "you beat friend X"
        /// toasts). Subscribers MUST marshal to the UI thread before touching
        /// any WPF surface.
        /// </summary>
        public event Action<LbScoreboardInfo>? LeaderboardScoreboardReceived;

        /// <summary>
        /// Marshaled view of a SCOREBOARD event — pointer-free, safe to retain
        /// past the rcheevos callback boundary. SubmittedScore / BestScore are
        /// the display strings (e.g. "01:23.45" or "1,240,500"); the integer
        /// rank field is what we compare against pre-fetched friend ranks.
        /// </summary>
        public sealed record LbScoreboardInfo(
            int LeaderboardId,
            int NewRank,
            string SubmittedScore,
            string BestScore,
            string LbTitle,
            bool LowerIsBetter);

        // Decode a fixed-size UTF-8 byte buffer (the rcheevos display
        // string format used by submitted_score / best_score / etc.).
        // Stops at the first 0x00 — those buffers are null-padded.
        private static string DecodeFixed(byte[] buf)
        {
            if (buf == null) return "";
            int len = 0;
            while (len < buf.Length && buf[len] != 0) len++;
            return System.Text.Encoding.UTF8.GetString(buf, 0, len);
        }

        // Live measured-progress snapshot, accumulated from PROGRESS_INDICATOR
        // events during play. Written from the emu thread, read from the UI
        // thread at game-exit flush time. ConcurrentDictionary handles the
        // hot-path on the emu thread without locking.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, AchievementInfo> _liveProgress = new();

        /// <summary>
        /// Snapshot of measured-progress data captured during this play
        /// session, keyed by achievement ID. Safe to call from any thread.
        /// EmulatorWindow flushes this on game exit so the detail card can
        /// later show "you were 73% of the way to X" instead of community
        /// median proxies.
        ///
        /// ConcurrentDictionary enumeration is not a consistent snapshot —
        /// concurrent writes mid-walk may or may not appear. In practice
        /// callers only invoke this from the emu loop's finally block,
        /// after the rcheevos event source has stopped firing into this
        /// client, so the race window is closed by construction.
        /// </summary>
        public IReadOnlyDictionary<int, AchievementInfo> GetLiveProgressSnapshot()
        {
            var copy = new Dictionary<int, AchievementInfo>(_liveProgress.Count);
            foreach (var kvp in _liveProgress)
                copy[kvp.Key] = kvp.Value;
            return copy;
        }

        public bool IsInitialized => _client != IntPtr.Zero;
        public bool IsGameLoaded => _client != IntPtr.Zero && rc_client_is_game_loaded(_client) != 0;

        // Per-console virtual-to-real address translation, mirroring rcheevos's
        // built-in _rc_memory_regions_<console> tables (src/rcheevos/consoleinfo.c).
        // rcheevos's set authoring uses virtual addresses; descriptors map
        // real M68K-style hardware addresses to memory pointers. The frontend
        // is responsible for translating virtual→real before descriptor lookup.
        //
        // _virtualAddressBase covers consoles whose virtual space is a single
        // contiguous run with a fixed offset to physical (NGCD, NeoGeo cart).
        // _virtualMap is the multi-region form for consoles whose virtual
        // address space has discontinuous physical bases (SegaCD: M68K RAM at
        // 0xFF0000, CD PRG RAM at 0x80020000, Word RAM at 0x200000).
        private uint _virtualAddressBase;
        private readonly struct VirtualRegion
        {
            public readonly uint VirtStart;   // inclusive
            public readonly uint VirtEnd;     // inclusive
            public readonly ulong PhysStart;
            public VirtualRegion(uint vs, uint ve, ulong ps) { VirtStart = vs; VirtEnd = ve; PhysStart = ps; }
        }
        private VirtualRegion[]? _virtualMap;

        // rcheevos's per-console virtual memory layouts copied from
        // consoleinfo.c. Add new consoles here as they're brought up.
        // Source of truth: rcheevos `_rc_memory_regions_<console>` tables.
        private static readonly VirtualRegion[] _vmap_segacd = new[]
        {
            new VirtualRegion(0x000000u, 0x00FFFFu, 0x00FF0000UL), // 68000 RAM
            new VirtualRegion(0x010000u, 0x08FFFFu, 0x80020000UL), // CD PRG RAM (banked into $020000-$03FFFF physically)
            new VirtualRegion(0x090000u, 0x0AFFFFu, 0x00200000UL), // CD Word RAM
        };
        private static readonly VirtualRegion[] _vmap_megadrive = new[]
        {
            new VirtualRegion(0x000000u, 0x00FFFFu, 0x00FF0000UL), // System RAM
            new VirtualRegion(0x010000u, 0x01FFFFu, 0x00000000UL), // Cartridge RAM (SRAM)
        };
        private static readonly VirtualRegion[] _vmap_gamecube = new[]
        {
            // 24MB System RAM at PowerPC real address 0x80000000
            // (Dolphin libretro publishes the descriptor at this address
            // with RETRO_MEMDESC_BIGENDIAN). rcheevos consoleinfo.c:519.
            new VirtualRegion(0x00000000u, 0x017FFFFFu, 0x80000000UL),
        };

        // Cart Neo Geo only: apply XOR-1 byte-address swap on descriptor reads.
        // RA's cart NeoGeo sets were authored against FBNeo's host-native byte
        // stream; Geolith now exposes canonical big-endian after removing its
        // shadow buffer. NGCD set authors used canonical layout, so the swap
        // must be cart-only — gating by the BIGENDIAN flag alone breaks NGCD.
        //
        // Host-endianness note: this swap assumes a little-endian host. The
        // libretro convention treats LE as default with RETRO_MEMDESC_BIGENDIAN
        // marking big-endian regions; if Emutastic ever ships on a big-endian
        // platform, the swap should be gated on (host == LE && region == BE).
        // Windows x64 is always LE so this is fine for current targets.
        private bool _cartByteswap;

        // ── Memory descriptor table (RETRO_ENVIRONMENT_SET_MEMORY_MAPS) ──
        // When the core publishes a memory map, we honor it for rcheevos
        // reads. Each region carries its own virtual start address, length,
        // pointer, and flags (including RETRO_MEMDESC_BIGENDIAN, which we
        // apply per-region instead of via the hardcoded NeoCD check).
        // When no map is published, OnReadMemory falls back to the legacy
        // retro_get_memory_data linear-concat path.
        public readonly struct MemoryRegion
        {
            public readonly ulong Flags;
            public readonly IntPtr Ptr;
            public readonly ulong Offset;
            public readonly ulong Start;
            public readonly ulong Len;
            public MemoryRegion(ulong flags, IntPtr ptr, ulong offset, ulong start, ulong len)
            { Flags = flags; Ptr = ptr; Offset = offset; Start = start; Len = len; }
        }
        private const ulong RETRO_MEMDESC_BIGENDIAN = 1UL << 1;
        private MemoryRegion[]? _memoryRegions;

        /// <summary>
        /// Called by EmulatorWindow when the core publishes a memory map via
        /// RETRO_ENVIRONMENT_SET_MEMORY_MAPS. OnReadMemory routes through
        /// these regions in preference to the legacy retro_get_memory_data
        /// linear address space.
        /// </summary>
        public void SetMemoryDescriptors(MemoryRegion[] regions)
        {
            bool wasUnset = _memoryRegions == null || _memoryRegions.Length == 0;
            _memoryRegions = regions;
            Trace.WriteLine($"[RA] Memory descriptors registered: {regions.Length} region(s)");
            foreach (var r in regions)
            {
                Trace.WriteLine($"[RA]   start=0x{r.Start:X8} len=0x{r.Len:X} flags=0x{r.Flags:X} {((r.Flags & RETRO_MEMDESC_BIGENDIAN) != 0 ? "BE" : "")}");
            }

            // Dolphin/GameCube (and any future HW core that boots lazily)
            // publishes SET_MEMORY_MAPS during the first retro_run frame,
            // not synchronously during retro_load_game. By the time we get
            // here, rcheevos has already validated every achievement
            // address against an empty memory map and disabled the
            // whole set as "unsupported". Reload the game now that the
            // descriptors are in place so addresses re-validate.
            if (wasUnset && _client != IntPtr.Zero && _lastRomPath != null
                && rc_client_is_game_loaded(_client) != 0)
            {
                Trace.WriteLine("[RA] Descriptors arrived post-load; reloading game to re-arm achievements");
                _reloadCallbackDelegate = (result, errorPtr, client, userdata) =>
                {
                    string? msg = PtrToStringUTF8(errorPtr);
                    Trace.WriteLine($"[RA] Post-descriptor reload result={result} err={msg}");
                };
                rc_client_unload_game(_client);
                rc_client_begin_identify_and_load_game(
                    _client, _lastConsoleId, _lastRomPath,
                    IntPtr.Zero, UIntPtr.Zero,
                    _reloadCallbackDelegate, IntPtr.Zero);
            }
        }

        public void Initialize(LibretroCore core, bool hardcoreEnabled, string? consoleName = null)
        {
            _core = core;

            // Per-console virtual-to-real address translation.
            //
            // NGCD: rcheevos's _rc_memory_regions_neo_geo_cd maps virtual
            // 0x000000-0x00FFFF to real M68K 0x00100000-0x0010FFFF.
            //
            // NeoGeo cart (RC_CONSOLE_ARCADE-categorized in RA): rcheevos has
            // no _rc_memory_regions_arcade table, but Geolith publishes cart
            // descriptors at M68K-hardware `start = 0x100000` while RA's cart
            // Neo Geo sets are authored against FBNeo-style flat-from-0 RAM
            // offsets. Same +0x100000 translation reconciles the convention
            // mismatch, until either Geolith starts publishing the cart
            // descriptor at start=0x000000 or rcheevos adds an arcade
            // memory regions table.
            bool isNeoGeoFamily = string.Equals(consoleName, "NeoCD", StringComparison.Ordinal)
                              || string.Equals(consoleName, "NeoGeo", StringComparison.Ordinal);
            _virtualAddressBase = isNeoGeoFamily ? 0x100000u : 0u;
            _cartByteswap = string.Equals(consoleName, "NeoGeo", StringComparison.Ordinal);

            // Multi-region virtual maps for consoles whose virtual space
            // doesn't collapse to a single offset (SegaCD's RAM regions
            // live at non-contiguous M68K physical bases). Takes precedence
            // over _virtualAddressBase in OnReadMemory.
            _virtualMap = consoleName switch
            {
                "SegaCD"            => _vmap_segacd,
                "Genesis"           => _vmap_megadrive,
                "MegaDrive"         => _vmap_megadrive,
                "GameCube"          => _vmap_gamecube,
                _                    => null,
            };

            _readMemoryDelegate = OnReadMemory;
            _serverCallDelegate = OnServerCall;

            _client = rc_client_create(_readMemoryDelegate, _serverCallDelegate);
            if (_client == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create rcheevos client.");

            // Install our CHD-aware cdreader so achievement identification
            // works for .chd content on every CD-based console (PS1,
            // Saturn, SegaCD, Dreamcast, PSP, TG-CD, 3DO, NGCD).
            // Non-CHD content (.cue+.bin, .gdi, .iso) continues through
            // rcheevos's default cdreader, preserving existing behavior.
            RcheevosChdCdReader.InstallInto(_client);

            // Set up logging
            _logDelegate = OnLogMessage;
            rc_client_enable_logging(_client, RC_CLIENT_LOG_LEVEL_INFO, _logDelegate);

            // Set up event handler
            _eventHandlerDelegate = OnEvent;
            rc_client_set_event_handler(_client, _eventHandlerDelegate);

            // Configure hardcore
            rc_client_set_hardcore_enabled(_client, hardcoreEnabled ? 1 : 0);
        }

        /// <summary>
        /// Log in with a saved token. Returns the token on success for re-saving.
        /// </summary>
        public (bool success, string? error, string? token) LoginWithToken(string username, string token)
        {
            if (_client == IntPtr.Zero) return (false, "Client not initialized.", null);

            bool completed = false;
            int resultCode = 0;
            string? errorMsg = null;
            var loginEvent = new ManualResetEventSlim(false);

            ClientCallbackFunc loginCallback = (result, errorPtr, client, userdata) =>
            {
                resultCode = result;
                errorMsg = PtrToStringUTF8(errorPtr);
                completed = true;
                loginEvent.Set();
            };

            rc_client_begin_login_with_token(_client, username, token, loginCallback, IntPtr.Zero);
            loginEvent.Wait(TimeSpan.FromSeconds(15));

            if (!completed) return (false, "Login timed out.", null);
            if (resultCode != RC_OK) return (false, errorMsg ?? $"Token login failed (code {resultCode}).", null);

            return (true, null, token);
        }

        /// <summary>
        /// Log in with username + password. Returns the token on success for saving.
        /// </summary>
        public (bool success, string? error, string? token) LoginWithPassword(string username, string password)
        {
            if (_client == IntPtr.Zero) return (false, "Client not initialized.", null);

            bool completed = false;
            int resultCode = 0;
            string? errorMsg = null;
            var loginEvent = new ManualResetEventSlim(false);

            ClientCallbackFunc loginCallback = (result, errorPtr, client, userdata) =>
            {
                resultCode = result;
                errorMsg = PtrToStringUTF8(errorPtr);
                completed = true;
                loginEvent.Set();
            };

            rc_client_begin_login_with_password(_client, username, password, loginCallback, IntPtr.Zero);
            loginEvent.Wait(TimeSpan.FromSeconds(15));

            if (!completed) return (false, "Login timed out.", null);
            if (resultCode != RC_OK) return (false, errorMsg ?? $"Password login failed (code {resultCode}).", null);

            // Extract the token from the user info
            IntPtr userPtr = rc_client_get_user_info(_client);
            string? returnedToken = null;
            if (userPtr != IntPtr.Zero)
            {
                var userInfo = Marshal.PtrToStructure<rc_client_user_t>(userPtr);
                returnedToken = PtrToStringUTF8(userInfo.token);
            }

            return (true, null, returnedToken);
        }

        /// <summary>
        /// Identify and load a game by its ROM file path.
        /// Blocks the calling thread until loading completes.
        /// </summary>
        public (bool success, string? error) LoadGame(string romPath, uint consoleId)
        {
            if (_client == IntPtr.Zero) return (false, "Client not initialized.");

            // Stash for late-descriptor reload (Dolphin/GameCube publishes
            // SET_MEMORY_MAPS during the first frame, not during retro_load_game).
            _lastRomPath = romPath;
            _lastConsoleId = consoleId;

            bool completed = false;
            int resultCode = 0;
            string? errorMsg = null;
            var loadEvent = new ManualResetEventSlim(false);

            ClientCallbackFunc loadCallback = (result, errorPtr, client, userdata) =>
            {
                resultCode = result;
                errorMsg = PtrToStringUTF8(errorPtr);
                completed = true;
                loadEvent.Set();
            };

            // Cache memory region pointers BEFORE loading the game — rcheevos validates
            // achievement addresses during load by calling the read memory callback.
            CacheMemoryRegions();

            rc_client_begin_identify_and_load_game(
                _client, consoleId, romPath,
                IntPtr.Zero, UIntPtr.Zero,
                loadCallback, IntPtr.Zero);

            loadEvent.Wait(TimeSpan.FromSeconds(30));

            if (!completed) return (false, "Game load timed out.");
            if (resultCode != RC_OK) return (false, errorMsg ?? $"Game load failed (code {resultCode}).");

            return (true, null);
        }

        // Stored for late-descriptor reload — see SetMemoryDescriptors.
        private string? _lastRomPath;
        private uint _lastConsoleId;
        private ClientCallbackFunc? _reloadCallbackDelegate;

        /// <summary>Call once per emulated frame, after retro_run().</summary>
        public void DoFrame()
        {
            if (_client != IntPtr.Zero)
                rc_client_do_frame(_client);
        }

        /// <summary>Call while paused, at least once per second.</summary>
        public void Idle()
        {
            if (_client != IntPtr.Zero)
                rc_client_idle(_client);
        }

        /// <summary>Call on emulator reset.</summary>
        public void Reset()
        {
            if (_client != IntPtr.Zero)
                rc_client_reset(_client);
        }

        /// <summary>
        /// Serializes rcheevos's in-memory runtime state (achievement hit counts,
        /// measured-progress trackers, leaderboard state) into a blob that can
        /// be paired with a libretro save state. Returns null if no game is
        /// loaded or the rcheevos call failed; an empty array on success when
        /// progress_size returned 0 (nothing to serialize).
        ///
        /// Per RA's hardcore-compliance recommendations (Section A: "Hit counts
        /// should be stored in save states"), the frontend pairs this blob with
        /// the core's retro_serialize bytes so partial-unlock progress survives
        /// a save → load cycle.
        /// </summary>
        public byte[]? SerializeProgress()
        {
            if (_client == IntPtr.Zero) return null;
            try
            {
                UIntPtr size = rc_client_progress_size(_client);
                ulong sz = (ulong)size.ToUInt64();
                if (sz == 0) return System.Array.Empty<byte>();
                var buf = new byte[sz];
                int rc = rc_client_serialize_progress_sized(_client, buf, size);
                if (rc != RC_OK)
                {
                    System.Diagnostics.Trace.WriteLine($"[RA] rc_client_serialize_progress_sized failed: rc={rc}");
                    return null;
                }
                return buf;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[RA] SerializeProgress threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Restores rcheevos's runtime state from a previously-serialized blob
        /// (see <see cref="SerializeProgress"/>). Returns true on success.
        /// Safe to call with an empty buffer (returns false silently) — older
        /// save states predate the side-car file, and missing data should
        /// simply leave the current rcheevos state untouched.
        /// </summary>
        public bool DeserializeProgress(byte[]? blob)
        {
            if (_client == IntPtr.Zero || blob == null || blob.Length == 0) return false;
            try
            {
                int rc = rc_client_deserialize_progress_sized(_client, blob, (UIntPtr)blob.LongLength);
                if (rc != RC_OK)
                {
                    System.Diagnostics.Trace.WriteLine($"[RA] rc_client_deserialize_progress_sized failed: rc={rc}");
                    return false;
                }
                return true;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[RA] DeserializeProgress threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public void UnloadGame()
        {
            if (_client != IntPtr.Zero)
                rc_client_unload_game(_client);
            _systemRamPtr = IntPtr.Zero;
            _systemRamSize = 0;
            _saveRamPtr = IntPtr.Zero;
            _saveRamSize = 0;
            _videoRamPtr = IntPtr.Zero;
            _videoRamSize = 0;
        }

        public string? GetGameTitle()
        {
            if (_client == IntPtr.Zero) return null;
            IntPtr gamePtr = rc_client_get_game_info(_client);
            if (gamePtr == IntPtr.Zero) return null;
            var game = Marshal.PtrToStructure<rc_client_game_t>(gamePtr);
            return PtrToStringUTF8(game.title);
        }

        /// <summary>
        /// Returns RA's numeric game ID for the currently-loaded game, or 0
        /// if no game is loaded / identification didn't land. Cached on the
        /// Game row by the caller so the Web API stats fetch can skip the
        /// hash-resolve roundtrip on subsequent library visits.
        /// </summary>
        public int GetGameId()
        {
            if (_client == IntPtr.Zero) return 0;
            IntPtr gamePtr = rc_client_get_game_info(_client);
            if (gamePtr == IntPtr.Zero) return 0;
            var game = Marshal.PtrToStructure<rc_client_game_t>(gamePtr);
            return (int)game.id;
        }

        // ── Memory read callback ─────────────────────────────────────────────

        private void CacheMemoryRegions()
        {
            if (_core == null) return;
            const uint RETRO_MEMORY_SAVE_RAM = 0;
            const uint RETRO_MEMORY_SYSTEM_RAM = 2;
            const uint RETRO_MEMORY_VIDEO_RAM = 3;

            (_systemRamPtr, _systemRamSize) = _core.GetMemoryRegion(RETRO_MEMORY_SYSTEM_RAM);
            (_saveRamPtr, _saveRamSize) = _core.GetMemoryRegion(RETRO_MEMORY_SAVE_RAM);
            (_videoRamPtr, _videoRamSize) = _core.GetMemoryRegion(RETRO_MEMORY_VIDEO_RAM);

            Trace.WriteLine($"[RA] Memory regions — SRAM: {_saveRamSize} bytes, System: {_systemRamSize} bytes, VRAM: {_videoRamSize} bytes");
        }

        private uint OnReadMemory(uint address, IntPtr buffer, uint numBytes, IntPtr client)
        {
            // Descriptor-aware path: when the core has published a memory map
            // via RETRO_ENVIRONMENT_SET_MEMORY_MAPS, route reads through it.
            // Each region has its own start/length/ptr; BIGENDIAN flag gates
            // a per-read-size byteswap (replicates what RetroArch's rc_libretro
            // does natively).
            //
            // rcheevos calls this with a VIRTUAL address from its per-console
            // memory map (consoleinfo.c _rc_memory_regions_<console>). For NGCD,
            // virtual 0x000000 maps to real M68K 0x00100000. Apply the offset
            // before searching descriptors, since descriptor.start fields are
            // in real M68K address space.
            if (_memoryRegions != null && _memoryRegions.Length > 0)
            {
                // Translate rcheevos virtual address → physical. Multi-region
                // map first (SegaCD/MegaDrive), then single-offset (NGCD),
                // then identity (most consoles publish at rcheevos's
                // virtual addresses directly).
                ulong realAddress;
                if (_virtualMap != null)
                {
                    realAddress = ulong.MaxValue;
                    for (int v = 0; v < _virtualMap.Length; v++)
                    {
                        var vr = _virtualMap[v];
                        if (address >= vr.VirtStart && address <= vr.VirtEnd)
                        {
                            realAddress = vr.PhysStart + (address - vr.VirtStart);
                            break;
                        }
                    }
                    if (realAddress == ulong.MaxValue) return 0; // virtual address not in any region
                }
                else
                {
                    realAddress = (ulong)address + _virtualAddressBase;
                }

                for (int i = 0; i < _memoryRegions.Length; i++)
                {
                    var r = _memoryRegions[i];
                    if (r.Ptr == IntPtr.Zero || r.Len == 0) continue;
                    if (realAddress < r.Start) continue;
                    ulong rel = realAddress - r.Start;
                    if (rel >= r.Len) continue;

                    ulong avail = r.Len - rel;
                    uint toCopy = numBytes < avail ? numBytes : (uint)avail;

                    // Cart Neo Geo: XOR-1 byte addresses to produce FBNeo's
                    // host-native byte stream from Geolith's canonical big-endian
                    // MAINRAM. NGCD set authors used canonical layout, so leave
                    // those reads verbatim. Gated by console (_cartByteswap), not
                    // by the BIGENDIAN flag, because cart and CD descriptors both
                    // carry the flag but only cart needs the swap.
                    unsafe
                    {
                        byte* baseSrc = (byte*)r.Ptr + (long)r.Offset;
                        byte* dst = (byte*)buffer;
                        if (_cartByteswap)
                        {
                            for (uint k = 0; k < toCopy; k++)
                                dst[k] = baseSrc[(long)(rel + k) ^ 1L];
                        }
                        else
                        {
                            Buffer.MemoryCopy(baseSrc + (long)rel, dst, toCopy, toCopy);
                        }
                    }
                    return toCopy;
                }
                return 0; // address not covered by any descriptor
            }

            // Legacy path: linear concat of SYSTEM_RAM then SAVE_RAM at virtual 0.
            // Used when the core doesn't publish a memory map. Most consoles.
            IntPtr srcPtr;
            uint offset;

            if (_systemRamSize > 0 && _systemRamPtr != IntPtr.Zero && address < _systemRamSize)
            {
                srcPtr = _systemRamPtr;
                offset = address;
                uint avail = _systemRamSize - offset;
                uint toCopy = Math.Min(numBytes, avail);
                unsafe
                {
                    Buffer.MemoryCopy(
                        (byte*)srcPtr + offset,
                        (byte*)buffer,
                        toCopy, toCopy);

                }
                return toCopy;
            }

            // Some cores expose save RAM as a secondary region
            if (_saveRamSize > 0 && _saveRamPtr != IntPtr.Zero)
            {
                uint saveStart = _systemRamSize; // save RAM starts after system RAM
                if (address >= saveStart && address < saveStart + _saveRamSize)
                {
                    offset = address - saveStart;
                    uint avail = _saveRamSize - offset;
                    uint toCopy = Math.Min(numBytes, avail);
                    unsafe
                    {
                        Buffer.MemoryCopy(
                            (byte*)_saveRamPtr + offset,
                            (byte*)buffer,
                            toCopy, toCopy);
                    }
                    return toCopy;
                }
            }

            return 0; // address not mapped
        }

        // ── HTTP callback ────────────────────────────────────────────────────

        private void OnServerCall(IntPtr requestPtr, ServerCallbackFunc callback, IntPtr callbackData, IntPtr client)
        {
            // Read the request struct — only the first 3 pointers (url, post_data, content_type)
            IntPtr urlPtr = Marshal.ReadIntPtr(requestPtr, 0);
            IntPtr postDataPtr = Marshal.ReadIntPtr(requestPtr, IntPtr.Size);
            IntPtr contentTypePtr = Marshal.ReadIntPtr(requestPtr, IntPtr.Size * 2);

            string? url = PtrToStringUTF8(urlPtr);
            string? postData = PtrToStringUTF8(postDataPtr);
            string? contentType = PtrToStringUTF8(contentTypePtr);

            // Get the raw native function pointer so it survives GC of the delegate wrapper.
            IntPtr callbackFnPtr = Marshal.GetFunctionPointerForDelegate(callback);

            if (string.IsNullOrEmpty(url))
            {
                InvokeServerCallback(callbackFnPtr, callbackData, IntPtr.Zero, UIntPtr.Zero, 0);
                return;
            }

            Trace.WriteLine($"[RA] HTTP → {(postData != null ? "POST" : "GET")} {url}");

            // Fire off HTTP request on a background thread.
            // We capture the raw function pointer (IntPtr) instead of the delegate
            // to prevent GC of the marshalled delegate wrapper from breaking the callback.
            Task.Run(async () =>
            {
                try
                {
                    HttpResponseMessage response;
                    if (!string.IsNullOrEmpty(postData))
                    {
                        var content = new StringContent(postData, System.Text.Encoding.UTF8,
                            contentType ?? "application/x-www-form-urlencoded");
                        response = await _http.PostAsync(url, content);
                    }
                    else
                    {
                        response = await _http.GetAsync(url);
                    }

                    string body = await response.Content.ReadAsStringAsync();
                    int statusCode = (int)response.StatusCode;
                    Trace.WriteLine($"[RA] HTTP ← {statusCode} ({body.Length} bytes)");

                    IntPtr bodyPtr = Marshal.StringToCoTaskMemUTF8(body);
                    InvokeServerCallback(callbackFnPtr, callbackData, bodyPtr, (UIntPtr)body.Length, statusCode);
                    Marshal.FreeCoTaskMem(bodyPtr);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[RA] HTTP error: {ex.Message}");
                    InvokeServerCallback(callbackFnPtr, callbackData, IntPtr.Zero, UIntPtr.Zero, 0);
                }
            });
        }

        /// <summary>
        /// Invokes the native server callback via its raw function pointer.
        /// Builds the rc_api_server_response_t struct on the stack and calls through.
        /// </summary>
        private static void InvokeServerCallback(IntPtr callbackFnPtr, IntPtr callbackData,
            IntPtr body, UIntPtr bodyLength, int httpStatusCode)
        {
            var resp = new rc_api_server_response_t
            {
                body = body,
                body_length = bodyLength,
                http_status_code = httpStatusCode
            };
            IntPtr respPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<rc_api_server_response_t>());
            try
            {
                Marshal.StructureToPtr(resp, respPtr, false);
                // Call the native function pointer directly
                var fn = Marshal.GetDelegateForFunctionPointer<ServerCallbackFunc>(callbackFnPtr);
                fn(respPtr, callbackData);
            }
            finally
            {
                Marshal.FreeCoTaskMem(respPtr);
            }
        }

        // ── Event handler ────────────────────────────────────────────────────

        private void OnEvent(IntPtr eventPtr, IntPtr client)
        {
            if (eventPtr == IntPtr.Zero) return;

            var evt = Marshal.PtrToStructure<rc_client_event_t>(eventPtr);

            switch (evt.type)
            {
                case RC_CLIENT_EVENT_ACHIEVEMENT_TRIGGERED:
                    if (evt.achievement != IntPtr.Zero)
                    {
                        var info = ReadAchievementInfo(evt.achievement);
                        Trace.WriteLine($"[RA] Achievement triggered: {info.Title} ({info.Points} pts)");
                        AchievementTriggered?.Invoke(info);
                    }
                    break;

                case RC_CLIENT_EVENT_ACHIEVEMENT_PROGRESS_INDICATOR_SHOW:
                case RC_CLIENT_EVENT_ACHIEVEMENT_PROGRESS_INDICATOR_UPDATE:
                    if (evt.achievement != IntPtr.Zero)
                    {
                        var info = ReadAchievementInfo(evt.achievement);
                        // Capture into the live snapshot dict for end-of-session
                        // persistence. This runs on the emu thread — the
                        // ConcurrentDictionary keeps it lock-free, and we never
                        // touch SQLite here (deferred to game-exit flush).
                        if (info.Id > 0 && info.MeasuredPercent > 0)
                            _liveProgress[(int)info.Id] = info;
                        ProgressIndicatorChanged?.Invoke(info, true);
                    }
                    break;

                case RC_CLIENT_EVENT_ACHIEVEMENT_PROGRESS_INDICATOR_HIDE:
                    ProgressIndicatorChanged?.Invoke(null, false);
                    break;

                // Challenge indicators: a primed challenge achievement ("beat the
                // boss without dying") shows its badge while the condition is being
                // attempted; HIDE fires when it un-primes (failed or completed).
                // Several can be active at once.
                case RC_CLIENT_EVENT_ACHIEVEMENT_CHALLENGE_INDICATOR_SHOW:
                case RC_CLIENT_EVENT_ACHIEVEMENT_CHALLENGE_INDICATOR_HIDE:
                    if (evt.achievement != IntPtr.Zero)
                    {
                        var chInfo = ReadAchievementInfo(evt.achievement);
                        if (chInfo.Id > 0)
                            ChallengeIndicatorChanged?.Invoke(chInfo,
                                evt.type == RC_CLIENT_EVENT_ACHIEVEMENT_CHALLENGE_INDICATOR_SHOW);
                    }
                    break;

                case RC_CLIENT_EVENT_LEADERBOARD_SCOREBOARD:
                    if (evt.leaderboard_scoreboard != IntPtr.Zero)
                    {
                        var sb = Marshal.PtrToStructure<rc_client_leaderboard_scoreboard_t>(evt.leaderboard_scoreboard);
                        string lbTitle = "";
                        bool lowerIsBetter = false;
                        if (evt.leaderboard != IntPtr.Zero)
                        {
                            var lb = Marshal.PtrToStructure<rc_client_leaderboard_t>(evt.leaderboard);
                            lbTitle = PtrToStringUTF8(lb.title) ?? "";
                            lowerIsBetter = lb.lower_is_better != 0;
                        }
                        var info = new LbScoreboardInfo(
                            LeaderboardId: (int)sb.leaderboard_id,
                            NewRank: (int)sb.new_rank,
                            SubmittedScore: DecodeFixed(sb.submitted_score),
                            BestScore: DecodeFixed(sb.best_score),
                            LbTitle: lbTitle,
                            LowerIsBetter: lowerIsBetter);
                        RaLog.Write($"[RA] LB scoreboard lb={info.LeaderboardId} title=[{info.LbTitle}] rank=#{info.NewRank} submitted=[{info.SubmittedScore}] best=[{info.BestScore}] lib={info.LowerIsBetter}");
                        LeaderboardScoreboardReceived?.Invoke(info);
                    }
                    break;

                case RC_CLIENT_EVENT_GAME_COMPLETED:
                    Trace.WriteLine("[RA] Game completed (all achievements earned)!");
                    GameCompleted?.Invoke();
                    break;

                case RC_CLIENT_EVENT_RESET:
                    Trace.WriteLine("[RA] Reset requested by rcheevos.");
                    ResetRequested?.Invoke();
                    break;

                case RC_CLIENT_EVENT_SERVER_ERROR:
                    if (evt.server_error != IntPtr.Zero)
                    {
                        IntPtr msgPtr = Marshal.ReadIntPtr(evt.server_error, 0);
                        string? msg = PtrToStringUTF8(msgPtr);
                        Trace.WriteLine($"[RA] Server error: {msg}");
                    }
                    break;

                case RC_CLIENT_EVENT_DISCONNECTED:
                    Trace.WriteLine("[RA] Disconnected from server.");
                    break;

                case RC_CLIENT_EVENT_RECONNECTED:
                    Trace.WriteLine("[RA] Reconnected to server.");
                    break;
            }
        }

        private static AchievementInfo ReadAchievementInfo(IntPtr achPtr)
        {
            var ach = Marshal.PtrToStructure<rc_client_achievement_t>(achPtr);
            return new AchievementInfo
            {
                Id = ach.id,
                Title = PtrToStringUTF8(ach.title) ?? "",
                Description = PtrToStringUTF8(ach.description) ?? "",
                Points = ach.points,
                BadgeUrl = PtrToStringUTF8(ach.badge_url),
                MeasuredProgress = System.Text.Encoding.UTF8.GetString(ach.measured_progress ?? Array.Empty<byte>()).TrimEnd('\0'),
                MeasuredPercent = ach.measured_percent,
                Rarity = ach.rarity,
                RarityHardcore = ach.rarity_hardcore,
                Type = ach.type
            };
        }

        // ── Logging ──────────────────────────────────────────────────────────

        private static void OnLogMessage(IntPtr messagePtr, IntPtr client)
        {
            string? msg = PtrToStringUTF8(messagePtr);
            if (msg != null)
                Trace.WriteLine($"[rcheevos] {msg}");
        }

        // ── Console ID mapping ───────────────────────────────────────────────

        public static uint GetConsoleId(string consoleName)
        {
            return consoleName switch
            {
                "NES"          => RC_CONSOLE_NINTENDO,
                "FDS"          => RC_CONSOLE_FAMICOM_DISK_SYSTEM,
                "SNES"         => RC_CONSOLE_SUPER_NINTENDO,
                "N64"          => RC_CONSOLE_NINTENDO_64,
                "GameCube"     => RC_CONSOLE_GAMECUBE,
                "GB"           => RC_CONSOLE_GAMEBOY,
                "GBC"          => RC_CONSOLE_GAMEBOY_COLOR,
                "GBA"          => RC_CONSOLE_GAMEBOY_ADVANCE,
                "NDS"          => RC_CONSOLE_NINTENDO_DS,
                "VirtualBoy"   => RC_CONSOLE_VIRTUAL_BOY,
                "Genesis"      => RC_CONSOLE_MEGA_DRIVE,
                "SegaCD"       => RC_CONSOLE_SEGA_CD,
                "Sega32X"      => RC_CONSOLE_SEGA_32X,
                "SMS"          => RC_CONSOLE_MASTER_SYSTEM,
                "GameGear"     => RC_CONSOLE_GAME_GEAR,
                "SG1000"       => RC_CONSOLE_SG1000,
                "Saturn"       => RC_CONSOLE_SATURN,
                "Dreamcast"    => RC_CONSOLE_DREAMCAST,
                "PS1"          => RC_CONSOLE_PLAYSTATION,
                "PSP"          => RC_CONSOLE_PSP,
                "TG16"         => RC_CONSOLE_PC_ENGINE,
                "TGCD"         => RC_CONSOLE_PC_ENGINE_CD,
                "NGP"          => RC_CONSOLE_NEOGEO_POCKET,
                // Neo Geo carts. RA classes them under the Arcade system —
                // there's no separate RC_CONSOLE_NEOGEO constant — and
                // achievement sets for Neo Geo cart games (Metal Slug, KOF,
                // etc.) live under arcade game IDs (e.g. 11750, 11771). The
                // arcade hash is filename-based (MD5 of the base name without
                // extension), so a .neo file with the canonical MAME short
                // name (mslug3.neo) hashes identically to FBNeo's mslug3.zip.
                "NeoGeo"       => RC_CONSOLE_ARCADE,
                // Neo Geo CD is its own RA system with content-based hashing
                // of the data track. End-to-end achievement triggers still
                // depend on Geolith landing a CD-mode shadow buffer (the
                // current cart-only patch covers MAINRAM at 64 KB; CD mode
                // needs the full 2 MB PRAM exposed via the FBNeo-compatible
                // byte order).
                "NeoCD"        => RC_CONSOLE_NEO_GEO_CD,
                "Atari2600"    => RC_CONSOLE_ATARI_2600,
                "Atari7800"    => RC_CONSOLE_ATARI_7800,
                "Jaguar"       => RC_CONSOLE_ATARI_JAGUAR,
                "ColecoVision" => RC_CONSOLE_COLECOVISION,
                "Vectrex"      => RC_CONSOLE_VECTREX,
                "3DO"          => RC_CONSOLE_3DO,
                "CDi"          => RC_CONSOLE_CDI,
                "3DS"          => RC_CONSOLE_NINTENDO_3DS,
                "Arcade"       => RC_CONSOLE_ARCADE,
                _ => 0
            };
        }

        // ── Cleanup ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_client != IntPtr.Zero)
            {
                try { rc_client_unload_game(_client); } catch { }
                try { rc_client_destroy(_client); } catch { }
                _client = IntPtr.Zero;
            }

            _core = null;
            GC.SuppressFinalize(this);
        }

        ~RetroAchievementsClient() => Dispose();
    }

    /// <summary>Achievement data passed to event handlers.</summary>
    public class AchievementInfo
    {
        public uint Id { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public uint Points { get; set; }
        public string? BadgeUrl { get; set; }
        public string MeasuredProgress { get; set; } = "";
        public float MeasuredPercent { get; set; }
        public float Rarity { get; set; }
        public float RarityHardcore { get; set; }
        public byte Type { get; set; }
    }
}
