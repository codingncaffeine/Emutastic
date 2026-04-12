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

        private static readonly HttpClient _http = new();

        /// <summary>Fired on the emulation thread when an achievement is triggered.</summary>
        public event Action<AchievementInfo>? AchievementTriggered;

        /// <summary>Fired when the player completes the game (all achievements).</summary>
        public event Action? GameCompleted;

        /// <summary>Fired when rcheevos requests an emulator reset (hardcore toggle).</summary>
        public event Action? ResetRequested;

        /// <summary>Fired for achievement progress updates (show/update/hide).</summary>
        public event Action<AchievementInfo?, bool>? ProgressIndicatorChanged;

        public bool IsInitialized => _client != IntPtr.Zero;
        public bool IsGameLoaded => _client != IntPtr.Zero && rc_client_is_game_loaded(_client) != 0;

        public void Initialize(LibretroCore core, bool hardcoreEnabled)
        {
            _core = core;

            _readMemoryDelegate = OnReadMemory;
            _serverCallDelegate = OnServerCall;

            _client = rc_client_create(_readMemoryDelegate, _serverCallDelegate);
            if (_client == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create rcheevos client.");

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
            // rcheevos uses a virtual address space. For most consoles, system RAM
            // starts at address 0. We map linearly: system RAM first, then save RAM.
            IntPtr srcPtr;
            uint offset;

            if (_systemRamSize > 0 && address < _systemRamSize)
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
            if (_saveRamSize > 0)
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
                        ProgressIndicatorChanged?.Invoke(info, true);
                    }
                    break;

                case RC_CLIENT_EVENT_ACHIEVEMENT_PROGRESS_INDICATOR_HIDE:
                    ProgressIndicatorChanged?.Invoke(null, false);
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
