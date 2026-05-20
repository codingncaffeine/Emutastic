using System;
using System.Runtime.InteropServices;

namespace Emutastic.Services
{
    /// <summary>
    /// P/Invoke bindings for the rcheevos native library (rc_client API).
    /// </summary>
    internal static class RcheevosInterop
    {
        private const string DLL = "rcheevos";

        // ── Error codes ──────────────────────────────────────────────────────
        public const int RC_OK = 0;
        public const int RC_ABORTED = -31;
        public const int RC_NO_RESPONSE = -32;
        public const int RC_INVALID_CREDENTIALS = -34;

        // ── Event types ──────────────────────────────────────────────────────
        public const uint RC_CLIENT_EVENT_ACHIEVEMENT_TRIGGERED = 1;
        public const uint RC_CLIENT_EVENT_ACHIEVEMENT_CHALLENGE_INDICATOR_SHOW = 5;
        public const uint RC_CLIENT_EVENT_ACHIEVEMENT_CHALLENGE_INDICATOR_HIDE = 6;
        public const uint RC_CLIENT_EVENT_ACHIEVEMENT_PROGRESS_INDICATOR_SHOW = 7;
        public const uint RC_CLIENT_EVENT_ACHIEVEMENT_PROGRESS_INDICATOR_HIDE = 8;
        public const uint RC_CLIENT_EVENT_ACHIEVEMENT_PROGRESS_INDICATOR_UPDATE = 9;
        public const uint RC_CLIENT_EVENT_LEADERBOARD_SCOREBOARD = 13;
        public const uint RC_CLIENT_EVENT_RESET = 14;
        public const uint RC_CLIENT_EVENT_GAME_COMPLETED = 15;
        public const uint RC_CLIENT_EVENT_SERVER_ERROR = 16;
        public const uint RC_CLIENT_EVENT_DISCONNECTED = 17;
        public const uint RC_CLIENT_EVENT_RECONNECTED = 18;

        // RC_CLIENT_LEADERBOARD_DISPLAY_SIZE — rc_client.h:591
        public const int RC_CLIENT_LEADERBOARD_DISPLAY_SIZE = 24;

        // ── Log levels ───────────────────────────────────────────────────────
        public const int RC_CLIENT_LOG_LEVEL_NONE = 0;
        public const int RC_CLIENT_LOG_LEVEL_ERROR = 1;
        public const int RC_CLIENT_LOG_LEVEL_WARN = 2;
        public const int RC_CLIENT_LOG_LEVEL_INFO = 3;
        public const int RC_CLIENT_LOG_LEVEL_VERBOSE = 4;

        // ── Console IDs (subset we support) ──────────────────────────────────
        public const uint RC_CONSOLE_MEGA_DRIVE = 1;
        public const uint RC_CONSOLE_NINTENDO_64 = 2;
        public const uint RC_CONSOLE_SUPER_NINTENDO = 3;
        public const uint RC_CONSOLE_GAMEBOY = 4;
        public const uint RC_CONSOLE_GAMEBOY_ADVANCE = 5;
        public const uint RC_CONSOLE_GAMEBOY_COLOR = 6;
        public const uint RC_CONSOLE_NINTENDO = 7;
        public const uint RC_CONSOLE_PC_ENGINE = 8;
        public const uint RC_CONSOLE_SEGA_CD = 9;
        public const uint RC_CONSOLE_SEGA_32X = 10;
        public const uint RC_CONSOLE_MASTER_SYSTEM = 11;
        public const uint RC_CONSOLE_PLAYSTATION = 12;
        public const uint RC_CONSOLE_GAME_GEAR = 15;
        public const uint RC_CONSOLE_GAMECUBE = 16;
        public const uint RC_CONSOLE_ARCADE = 27;
        public const uint RC_CONSOLE_VIRTUAL_BOY = 28;
        public const uint RC_CONSOLE_SATURN = 39;
        public const uint RC_CONSOLE_DREAMCAST = 40;
        public const uint RC_CONSOLE_PSP = 41;
        public const uint RC_CONSOLE_CDI = 42;
        public const uint RC_CONSOLE_NEOGEO_POCKET = 14;
        public const uint RC_CONSOLE_NINTENDO_DS = 18;
        public const uint RC_CONSOLE_ATARI_2600 = 25;
        public const uint RC_CONSOLE_SG1000 = 33;
        public const uint RC_CONSOLE_3DO = 43;
        public const uint RC_CONSOLE_COLECOVISION = 44;
        public const uint RC_CONSOLE_VECTREX = 46;
        public const uint RC_CONSOLE_ATARI_7800 = 51;
        public const uint RC_CONSOLE_ATARI_JAGUAR = 17;
        public const uint RC_CONSOLE_NEO_GEO_CD = 56;
        public const uint RC_CONSOLE_PC_ENGINE_CD = 76;
        public const uint RC_CONSOLE_FAMICOM_DISK_SYSTEM = 81;
        public const uint RC_CONSOLE_NINTENDO_3DS = 101;

        // ── Structs ──────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        public struct rc_client_event_t
        {
            public uint type;
            public IntPtr achievement;        // rc_client_achievement_t*
            public IntPtr leaderboard;         // rc_client_leaderboard_t*
            public IntPtr leaderboard_tracker; // rc_client_leaderboard_tracker_t*
            public IntPtr leaderboard_scoreboard;
            public IntPtr server_error;
            public IntPtr subset;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct rc_client_achievement_t
        {
            public IntPtr title;           // const char*
            public IntPtr description;     // const char*
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] badge_name;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
            public byte[] measured_progress;
            public float measured_percent;
            public uint id;
            public uint points;
            public long unlock_time;       // time_t (64-bit on x64 MSVC)
            public byte state;
            public byte category;
            public byte bucket;
            public byte unlocked;
            public float rarity;
            public float rarity_hardcore;
            public byte type;
            // padding to align pointers
            private byte _pad1;
            private byte _pad2;
            private byte _pad3;
            private int _pad4;     // additional padding to 8-byte alignment
            public IntPtr badge_url;       // const char*
            public IntPtr badge_locked_url; // const char*
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct rc_client_user_t
        {
            public IntPtr display_name;    // const char*
            public IntPtr username;        // const char*
            public IntPtr token;           // const char*
            public uint score;
            public uint score_softcore;
            public uint num_unread_messages;
            public IntPtr avatar_url;      // const char*
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct rc_client_game_t
        {
            public uint id;
            public uint console_id;
            public IntPtr title;           // const char*
            public IntPtr hash;            // const char*
            public IntPtr badge_name;      // const char*
            public IntPtr badge_url;       // const char*
        }

        // rc_client.h:593-601 — rc_client_leaderboard_t
        // ALL THREE leading fields are pointers (const char*), NOT inline
        // arrays. tracker_value is also a pointer. There is no `bucket`
        // field. Getting the layout wrong silently shifts every field
        // after the bad one — lower_is_better reads garbage, etc.
        [StructLayout(LayoutKind.Sequential)]
        public struct rc_client_leaderboard_t
        {
            public IntPtr title;           // const char*
            public IntPtr description;     // const char*
            public IntPtr tracker_value;   // const char*
            public uint id;
            public byte state;
            public byte format;
            public byte lower_is_better;   // non-zero = time-based / lower=better
        }

        // rc_client.h:714-730 — rc_client_leaderboard_scoreboard_t.
        // submitted_score and best_score are FIXED-SIZE char arrays
        // (24 bytes each), NOT pointers. Mirror the badge_name pattern
        // above. Wrong layout = silent data corruption.
        [StructLayout(LayoutKind.Sequential)]
        public struct rc_client_leaderboard_scoreboard_t
        {
            public uint leaderboard_id;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = RC_CLIENT_LEADERBOARD_DISPLAY_SIZE)]
            public byte[] submitted_score;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = RC_CLIENT_LEADERBOARD_DISPLAY_SIZE)]
            public byte[] best_score;
            public uint new_rank;
            public uint num_entries;
            public IntPtr top_entries;     // rc_client_leaderboard_scoreboard_entry_t* — unused for toast
            public uint num_top_entries;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct rc_api_request_t
        {
            public IntPtr url;             // const char*
            public IntPtr post_data;       // const char*
            public IntPtr content_type;    // const char*
            // rc_buffer_t follows but we don't need to read it
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct rc_api_server_response_t
        {
            public IntPtr body;            // const char*
            public UIntPtr body_length;    // size_t
            public int http_status_code;
        }

        // ── Callback delegates ───────────────────────────────────────────────

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint ReadMemoryFunc(uint address, IntPtr buffer, uint numBytes, IntPtr client);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ServerCallbackFunc(IntPtr serverResponse, IntPtr callbackData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ServerCallFunc(IntPtr request, ServerCallbackFunc callback, IntPtr callbackData, IntPtr client);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ClientCallbackFunc(int result, IntPtr errorMessage, IntPtr client, IntPtr userdata);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EventHandlerFunc(IntPtr eventPtr, IntPtr client);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void MessageCallbackFunc(IntPtr message, IntPtr client);

        // ── P/Invoke functions ───────────────────────────────────────────────

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr rc_client_create(ReadMemoryFunc readMemory, ServerCallFunc serverCall);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_client_destroy(IntPtr client);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_client_set_event_handler(IntPtr client, EventHandlerFunc handler);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_client_enable_logging(IntPtr client, int level, MessageCallbackFunc callback);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_client_set_hardcore_enabled(IntPtr client, int enabled);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_client_get_hardcore_enabled(IntPtr client);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_client_set_userdata(IntPtr client, IntPtr userdata);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr rc_client_get_userdata(IntPtr client);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr rc_client_begin_login_with_token(
            IntPtr client,
            [MarshalAs(UnmanagedType.LPStr)] string username,
            [MarshalAs(UnmanagedType.LPStr)] string token,
            ClientCallbackFunc callback,
            IntPtr callbackUserdata);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr rc_client_begin_login_with_password(
            IntPtr client,
            [MarshalAs(UnmanagedType.LPStr)] string username,
            [MarshalAs(UnmanagedType.LPStr)] string password,
            ClientCallbackFunc callback,
            IntPtr callbackUserdata);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_client_logout(IntPtr client);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr rc_client_get_user_info(IntPtr client);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr rc_client_begin_identify_and_load_game(
            IntPtr client,
            uint consoleId,
            [MarshalAs(UnmanagedType.LPStr)] string filePath,
            IntPtr data,
            UIntPtr dataSize,
            ClientCallbackFunc callback,
            IntPtr callbackUserdata);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr rc_client_begin_load_game(
            IntPtr client,
            [MarshalAs(UnmanagedType.LPStr)] string hash,
            ClientCallbackFunc callback,
            IntPtr callbackUserdata);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_client_unload_game(IntPtr client);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_client_is_game_loaded(IntPtr client);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr rc_client_get_game_info(IntPtr client);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_client_do_frame(IntPtr client);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_client_idle(IntPtr client);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rc_client_reset(IntPtr client);

        // ── Runtime progress serialization ───────────────────────────────────
        // Used to round-trip rcheevos's internal hit counts through frontend
        // save-state save/load. Without these the core's retro_serialize only
        // captures emulation state; achievement progress (partial unlocks,
        // measured counters) resets to the on-disk RA-server state on every
        // load. RA recommends preserving the in-memory state via this API.

        /// <summary>Bytes required to serialize the current rcheevos runtime state.</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr rc_client_progress_size(IntPtr client);

        /// <summary>Serializes runtime state into a caller-provided buffer. Returns RC_OK on success.</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_client_serialize_progress_sized(IntPtr client, byte[] buffer, UIntPtr bufferSize);

        /// <summary>Deserializes runtime state from a buffer. Returns RC_OK on success.</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_client_deserialize_progress_sized(IntPtr client, byte[] serialized, UIntPtr serializedSize);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_client_has_achievements(IntPtr client);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rc_client_has_rich_presence(IntPtr client);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr rc_client_get_rich_presence_message(IntPtr client, IntPtr buffer, UIntPtr bufferSize);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr rc_client_get_user_agent_clause(IntPtr client, IntPtr buffer, UIntPtr bufferSize);

        // ── Helpers ──────────────────────────────────────────────────────────

        public static string? PtrToStringUTF8(IntPtr ptr)
            => ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }
}
