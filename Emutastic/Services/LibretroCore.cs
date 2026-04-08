using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Emutastic.Services
{
    // Libretro callback delegates
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool retro_environment_t(uint cmd, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void retro_video_refresh_t(IntPtr data, uint width, uint height, UIntPtr pitch);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void retro_audio_sample_t(short left, short right);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate UIntPtr retro_audio_sample_batch_t(IntPtr data, UIntPtr frames);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void retro_input_poll_t();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate short retro_input_state_t(uint port, uint device, uint index, uint id);

    // Log interface delegate.
    // x64 Windows ABI: level→RCX, fmt→RDX, then varargs in R8, R9, and stack.
    // Declaring a0-a3 captures the first 4 varargs so OnRetroLog can substitute
    // %s/%d/%x instead of printing the raw format string.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void retro_log_printf_t(uint level, IntPtr fmt,
        IntPtr a0, IntPtr a1, IntPtr a2, IntPtr a3);

    // HW render delegates
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void retro_hw_context_reset_t();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate ulong retro_hw_get_current_framebuffer_t();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr retro_hw_get_proc_address_t([MarshalAs(UnmanagedType.LPStr)] string sym);

    // Libretro structures
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct retro_system_info
    {
        public IntPtr library_name;
        public IntPtr library_version;
        public IntPtr valid_extensions;
        public bool need_fullpath;
        public bool block_extract;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct retro_system_av_info
    {
        public retro_game_geometry geometry;
        public retro_system_timing timing;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct retro_system_timing
    {
        public double fps;
        public double sample_rate;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct retro_game_geometry
    {
        public uint base_width;
        public uint base_height;
        public uint max_width;
        public uint max_height;
        public float aspect_ratio;
    }

    // HW render callback structure — must match the C ABI layout exactly.
    //
    // CRITICAL LAYOUT NOTES:
    //
    // 1. Use LayoutKind.Explicit with correct byte offsets derived from the C ABI.
    //    On 64-bit Windows, the C compiler inserts 4 bytes of padding after
    //    context_type (uint, 4 bytes) to align the first pointer (8 bytes) to an
    //    8-byte boundary.  Pack=1 removes that gap, shifting every subsequent
    //    field by 4 bytes and corrupting version_major/minor reads (you would see
    //    version = 65537 = 0x00010001, i.e. the depth/stencil/origin bytes
    //    reinterpreted as a uint).
    //
    // 2. Ownership of each field:
    //      context_reset         — SET BY CORE.   Frontend reads it and CALLS it
    //                              after the GL context is ready.  Do NOT overwrite.
    //      get_current_framebuffer — SET BY FRONTEND. Core calls it to get the FBO id.
    //      get_proc_address        — SET BY FRONTEND. Core calls it to resolve GL symbols.
    //      context_destroy       — SET BY CORE.   Frontend reads it and CALLS it on
    //                              shutdown.  Do NOT overwrite.
    //
    // C ABI offsets (64-bit):
    //   0  : context_type          (uint,   4 bytes)
    //   4  : [4 bytes padding]
    //   8  : context_reset         (ptr,    8 bytes)
    //   16 : get_current_framebuffer (ptr,  8 bytes)
    //   24 : get_proc_address      (ptr,    8 bytes)
    //   32 : depth                 (bool,   1 byte)
    //   33 : stencil               (bool,   1 byte)
    //   34 : bottom_left_origin    (bool,   1 byte)
    //   35 : [1 byte padding]
    //   36 : version_major         (uint,   4 bytes)
    //   40 : version_minor         (uint,   4 bytes)
    //   44 : cache_context         (bool,   1 byte)
    //   45 : [3 bytes padding]
    //   48 : context_destroy       (ptr,    8 bytes)
    //   56 : debug_callback        (ptr,    8 bytes)
    //   64 : debug_context         (bool,   1 byte)
    [StructLayout(LayoutKind.Explicit)]
    public struct retro_hw_render_callback
    {
        [FieldOffset(0)]
        public uint context_type;

        // 4 bytes of implicit C ABI padding here (pointer alignment)

        [FieldOffset(8)]
        public IntPtr context_reset;              // SET BY CORE — frontend must call this

        [FieldOffset(16)]
        public IntPtr get_current_framebuffer;    // SET BY FRONTEND — core calls this

        [FieldOffset(24)]
        public IntPtr get_proc_address;           // SET BY FRONTEND — core calls this

        [FieldOffset(32)]
        [MarshalAs(UnmanagedType.I1)]
        public bool depth;

        [FieldOffset(33)]
        [MarshalAs(UnmanagedType.I1)]
        public bool stencil;

        [FieldOffset(34)]
        [MarshalAs(UnmanagedType.I1)]
        public bool bottom_left_origin;

        // 1 byte padding at offset 35

        [FieldOffset(36)]
        public uint version_major;

        [FieldOffset(40)]
        public uint version_minor;

        [FieldOffset(44)]
        [MarshalAs(UnmanagedType.I1)]
        public bool cache_context;

        // 3 bytes padding at offsets 45-47

        [FieldOffset(48)]
        public IntPtr context_destroy;            // SET BY CORE — frontend must call this

        [FieldOffset(56)]
        public IntPtr debug_callback;

        [FieldOffset(64)]
        [MarshalAs(UnmanagedType.I1)]
        public bool debug_context;
    }

    public class LibretroCore : IDisposable
    {
        private IntPtr _handle;
        private IntPtr _gamePathPtr = IntPtr.Zero;
        private IntPtr _gameDataPtr = IntPtr.Zero;
        private readonly string _corePath;
        private retro_system_av_info _avInfo;

        public string CorePath => _corePath;

        /// <summary>
        /// Set by Dispose() when FreeLibrary is deferred (N64/Dolphin).
        /// The caller should free this handle after the GL quarantine period.
        /// </summary>
        public IntPtr DeferredFreeHandle { get; private set; } = IntPtr.Zero;

        // Libretro function pointer delegates
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_init_t();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_deinit_t();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint retro_api_version_t();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_get_system_info_t(IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_get_system_av_info_t(IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_set_environment_t(retro_environment_t cb);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_set_video_refresh_t(retro_video_refresh_t cb);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_set_audio_sample_t(retro_audio_sample_t cb);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_set_audio_sample_batch_t(retro_audio_sample_batch_t cb);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_set_input_poll_t(retro_input_poll_t cb);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_set_input_state_t(retro_input_state_t cb);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_reset_t();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_run_t();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool retro_load_game_t(IntPtr game);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_unload_game_t();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool retro_serialize_t(IntPtr data, UIntPtr size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool retro_unserialize_t(IntPtr data, UIntPtr size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate UIntPtr retro_serialize_size_t();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate UIntPtr retro_get_memory_size_t(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr retro_get_memory_data_t(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void retro_set_controller_port_device_t(uint port, uint device);

        private retro_init_t? _retro_init;
        private retro_deinit_t? _retro_deinit;
        private retro_api_version_t? _retro_api_version;
        private retro_get_system_info_t? _retro_get_system_info;
        private retro_get_system_av_info_t? _retro_get_system_av_info;
        private retro_set_environment_t? _retro_set_environment;
        private retro_set_video_refresh_t? _retro_set_video_refresh;
        private retro_set_audio_sample_t? _retro_set_audio_sample;
        private retro_set_audio_sample_batch_t? _retro_set_audio_sample_batch;
        private retro_set_input_poll_t? _retro_set_input_poll;
        private retro_set_input_state_t? _retro_set_input_state;
        private retro_reset_t? _retro_reset;
        private retro_run_t? _retro_run;
        private retro_load_game_t? _retro_load_game;
        private retro_unload_game_t? _retro_unload_game;
        private retro_serialize_t? _retro_serialize;
        private retro_unserialize_t? _retro_unserialize;
        private retro_serialize_size_t? _retro_serialize_size;
        private retro_get_memory_size_t? _retro_get_memory_size;
        private retro_get_memory_data_t? _retro_get_memory_data;
        private retro_set_controller_port_device_t? _retro_set_controller_port_device;

        public retro_system_av_info AvInfo => _avInfo;
        public retro_system_info SystemInfo { get; private set; }

        /// <summary>
        /// Gets the core's library name (e.g., "ParaLLEl N64", "Mupen64Plus-Next")
        /// </summary>
        public string CoreName
        {
            get
            {
                if (SystemInfo.library_name != IntPtr.Zero)
                    return Marshal.PtrToStringAnsi(SystemInfo.library_name) ?? "Unknown";
                return "Unknown";
            }
        }

        public LibretroCore(string corePath)
        {
            _corePath = corePath ?? throw new ArgumentNullException(nameof(corePath));

            if (!File.Exists(corePath))
                throw new FileNotFoundException("Core file not found", corePath);

            LoadCore();
        }

        private void LoadCore()
        {
            _handle = NativeMethods.LoadLibrary(_corePath);
            if (_handle == IntPtr.Zero)
                throw new Exception($"Failed to load core: {_corePath}. Error: {Marshal.GetLastWin32Error()}");

            _retro_init = GetFunctionPointer<retro_init_t>("retro_init");
            _retro_deinit = GetFunctionPointer<retro_deinit_t>("retro_deinit");
            _retro_api_version = GetFunctionPointer<retro_api_version_t>("retro_api_version");
            _retro_get_system_info = GetFunctionPointer<retro_get_system_info_t>("retro_get_system_info");
            _retro_get_system_av_info = GetFunctionPointer<retro_get_system_av_info_t>("retro_get_system_av_info");
            _retro_set_environment = GetFunctionPointer<retro_set_environment_t>("retro_set_environment");
            _retro_set_video_refresh = GetFunctionPointer<retro_set_video_refresh_t>("retro_set_video_refresh");
            _retro_set_audio_sample = GetFunctionPointer<retro_set_audio_sample_t>("retro_set_audio_sample");
            _retro_set_audio_sample_batch = GetFunctionPointer<retro_set_audio_sample_batch_t>("retro_set_audio_sample_batch");
            _retro_set_input_poll = GetFunctionPointer<retro_set_input_poll_t>("retro_set_input_poll");
            _retro_set_input_state = GetFunctionPointer<retro_set_input_state_t>("retro_set_input_state");
            _retro_reset = GetFunctionPointer<retro_reset_t>("retro_reset");
            _retro_run = GetFunctionPointer<retro_run_t>("retro_run");
            _retro_load_game = GetFunctionPointer<retro_load_game_t>("retro_load_game");
            _retro_unload_game = GetFunctionPointer<retro_unload_game_t>("retro_unload_game");
            _retro_serialize = GetFunctionPointer<retro_serialize_t>("retro_serialize");
            _retro_unserialize = GetFunctionPointer<retro_unserialize_t>("retro_unserialize");
            _retro_get_memory_size = GetFunctionPointer<retro_get_memory_size_t>("retro_get_memory_size");
            _retro_get_memory_data = GetFunctionPointer<retro_get_memory_data_t>("retro_get_memory_data");

            // retro_set_controller_port_device is optional in some cores
            try { _retro_set_controller_port_device = GetFunctionPointer<retro_set_controller_port_device_t>("retro_set_controller_port_device"); }
            catch { /* optional */ }

            // retro_serialize_size is optional in some cores
            try { _retro_serialize_size = GetFunctionPointer<retro_serialize_size_t>("retro_serialize_size"); }
            catch { /* optional */ }
        }

        private T GetFunctionPointer<T>(string functionName) where T : class
        {
            IntPtr ptr = NativeMethods.GetProcAddress(_handle, functionName);
            if (ptr == IntPtr.Zero)
                throw new Exception($"Function {functionName} not found in core");

            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        public void SetCallbacks(
            retro_environment_t envCb,
            retro_video_refresh_t videoCb,
            retro_audio_sample_t audioCb,
            retro_audio_sample_batch_t audioBatchCb,
            retro_input_poll_t inputPollCb,
            retro_input_state_t inputStateCb)
        {
            _retro_set_environment?.Invoke(envCb);
            _retro_set_video_refresh?.Invoke(videoCb);
            _retro_set_audio_sample?.Invoke(audioCb);
            _retro_set_audio_sample_batch?.Invoke(audioBatchCb);
            _retro_set_input_poll?.Invoke(inputPollCb);
            _retro_set_input_state?.Invoke(inputStateCb);
        }

        public void Init()
        {
            _retro_init?.Invoke();

            int infoSize = Marshal.SizeOf<retro_system_info>();
            IntPtr infoPtr = Marshal.AllocHGlobal(infoSize);
            try
            {
                // Zero the struct first — some cores read uninitialised bytes
                for (int i = 0; i < infoSize; i++)
                    Marshal.WriteByte(infoPtr, i, 0);

                _retro_get_system_info?.Invoke(infoPtr);
                SystemInfo = Marshal.PtrToStructure<retro_system_info>(infoPtr);

                System.Diagnostics.Debug.WriteLine(
                    $"Core info: {Marshal.PtrToStringAnsi(SystemInfo.library_name)} " +
                    $"v{Marshal.PtrToStringAnsi(SystemInfo.library_version)}");
                System.Diagnostics.Debug.WriteLine(
                    $"Extensions: {Marshal.PtrToStringAnsi(SystemInfo.valid_extensions)}");
                System.Diagnostics.Debug.WriteLine(
                    $"Need fullpath: {SystemInfo.need_fullpath}, Block extract: {SystemInfo.block_extract}");
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }
        }

        public bool LoadGame(string romPath)
        {
            if (!File.Exists(romPath))
            {
                System.Diagnostics.Trace.WriteLine($"LoadGame: ROM file does not exist: {romPath}");
                return false;
            }

            System.Diagnostics.Trace.WriteLine($"LoadGame: ROM exists, size={new FileInfo(romPath).Length} bytes");

            bool needFullPath = SystemInfo.need_fullpath;

            byte[]? romData = null;

            if (!needFullPath)
            {
                try
                {
                    romData = File.ReadAllBytes(romPath);
                    System.Diagnostics.Trace.WriteLine($"LoadGame: Read {romData.Length} bytes into buffer");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"LoadGame: Failed to read ROM: {ex.Message}");
                    return false;
                }
            }

            // Free any previous game pointers
            if (_gamePathPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_gamePathPtr); _gamePathPtr = IntPtr.Zero; }
            if (_gameDataPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_gameDataPtr); _gameDataPtr = IntPtr.Zero; }

            _gamePathPtr = Marshal.StringToHGlobalAnsi(romPath);
            _gameDataPtr = romData != null ? Marshal.AllocHGlobal(romData.Length) : IntPtr.Zero;

            var gameInfo = new retro_game_info
            {
                path = _gamePathPtr,
                data = _gameDataPtr,
                size = romData != null ? (UIntPtr)romData.Length : UIntPtr.Zero,
                meta = IntPtr.Zero
            };

            try
            {
                if (romData != null && gameInfo.data != IntPtr.Zero)
                    Marshal.Copy(romData, 0, gameInfo.data, romData.Length);

                System.Diagnostics.Trace.WriteLine(
                    $"LoadGame: calling retro_load_game — path={romPath}, data=0x{gameInfo.data:X}, size={gameInfo.size}");

                int gameInfoSize = Marshal.SizeOf(gameInfo);
                IntPtr infoPtr = Marshal.AllocHGlobal(gameInfoSize);
                Marshal.StructureToPtr(gameInfo, infoPtr, false);

                bool result = _retro_load_game?.Invoke(infoPtr) ?? false;
                Marshal.FreeHGlobal(infoPtr);

                System.Diagnostics.Trace.WriteLine($"LoadGame: retro_load_game returned {result}");

                if (result)
                {
                    int avInfoSize = Marshal.SizeOf<retro_system_av_info>();
                    IntPtr avInfoPtr = Marshal.AllocHGlobal(avInfoSize);

                    try
                    {
                        // Zero it first
                        for (int i = 0; i < avInfoSize; i++)
                            Marshal.WriteByte(avInfoPtr, i, 0);

                        _retro_get_system_av_info?.Invoke(avInfoPtr);
                        _avInfo = Marshal.PtrToStructure<retro_system_av_info>(avInfoPtr);

                        System.Diagnostics.Debug.WriteLine(
                            $"Game loaded. Resolution: {_avInfo.geometry.base_width}x{_avInfo.geometry.base_height}" +
                            $", AR: {_avInfo.geometry.aspect_ratio}, FPS: {_avInfo.timing.fps}");

                        // Sanitise geometry
                        if (_avInfo.geometry.base_width == 0 || _avInfo.geometry.base_height == 0 ||
                            _avInfo.geometry.base_width > 4096 || _avInfo.geometry.base_height > 4096)
                        {
                            System.Diagnostics.Debug.WriteLine("Invalid resolution — using 640x480 fallback");
                            _avInfo.geometry.base_width = 640;
                            _avInfo.geometry.base_height = 480;
                            _avInfo.geometry.max_width = 640;
                            _avInfo.geometry.max_height = 480;
                        }

                        // Sanitise FPS
                        if (double.IsNaN(_avInfo.timing.fps) || double.IsInfinity(_avInfo.timing.fps) ||
                            _avInfo.timing.fps <= 0 || _avInfo.timing.fps > 1000)
                        {
                            System.Diagnostics.Debug.WriteLine($"Invalid FPS {_avInfo.timing.fps} — using 60");
                            _avInfo.timing.fps = 60.0;
                        }

                        // Sanitise sample rate
                        if (_avInfo.timing.sample_rate <= 0 || _avInfo.timing.sample_rate > 192000)
                            _avInfo.timing.sample_rate = 44100;

                        System.Diagnostics.Debug.WriteLine(
                            $"Final AV: {_avInfo.geometry.base_width}x{_avInfo.geometry.base_height}" +
                            $" @ {_avInfo.timing.fps:F3} fps, {_avInfo.timing.sample_rate} Hz");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(avInfoPtr);
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadGame exception: {ex}");
                return false;
            }
        }

        public void SetControllerPortDevice(uint port, uint device)
        {
            _retro_set_controller_port_device?.Invoke(port, device);
            System.Diagnostics.Debug.WriteLine($"retro_set_controller_port_device({port}, {device})");
        }

        public void Run() => _retro_run?.Invoke();
        public void Reset() => _retro_reset?.Invoke();

        // Set when UnloadGame() is called explicitly so Dispose() doesn't call it again.
        private bool _gameUnloaded = false;
        // Set when Deinit() is called explicitly so Dispose() doesn't call it again.
        private bool _deinitialized = false;

        public void UnloadGame()
        {
            if (_gameUnloaded) return;
            _gameUnloaded = true;
            try { _retro_unload_game?.Invoke(); } catch { }
        }

        /// <summary>
        /// Calls retro_deinit early (e.g. on the emu thread while a GL context is current).
        /// Dispose() will skip retro_deinit if this was already called.
        /// </summary>
        public void Deinit()
        {
            if (_deinitialized) return;
            _deinitialized = true;
            try { _retro_deinit?.Invoke(); } catch { }
        }

        public byte[]? SaveState()
        {
            // Prefer retro_serialize_size if available
            UIntPtr size = UIntPtr.Zero;
            if (_retro_serialize_size != null)
                size = _retro_serialize_size();
            else
                size = _retro_get_memory_size?.Invoke(0) ?? UIntPtr.Zero;

            System.Diagnostics.Trace.WriteLine(
                $"[SaveState] serialize_size={size}, has_serialize={_retro_serialize != null}, has_serialize_size={_retro_serialize_size != null}");

            if (size == UIntPtr.Zero)
            {
                System.Diagnostics.Trace.WriteLine("[SaveState] Size is zero — save states not supported by this core");
                return null;
            }

            IntPtr data = Marshal.AllocHGlobal((int)size);
            try
            {
                bool success = _retro_serialize?.Invoke(data, size) ?? false;
                System.Diagnostics.Trace.WriteLine($"[SaveState] retro_serialize returned {success}, size={(int)size} bytes");
                if (!success) return null;

                byte[] result = new byte[(int)size];
                Marshal.Copy(data, result, 0, (int)size);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        public bool LoadState(byte[] stateData)
        {
            if (stateData == null || stateData.Length == 0) return false;

            IntPtr data = Marshal.AllocHGlobal(stateData.Length);
            try
            {
                Marshal.Copy(stateData, 0, data, stateData.Length);
                return _retro_unserialize?.Invoke(data, (UIntPtr)stateData.Length) ?? false;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        // ── Battery save RAM (SRAM / memory card) ────────────────────────────────
        /// <summary>
        /// Returns a pointer to the core's memory region for the given ID, plus its size.
        /// Used by rcheevos for achievement condition evaluation.
        /// RETRO_MEMORY_SYSTEM_RAM = 2 is the primary region for most cores.
        /// </summary>
        public (IntPtr ptr, uint size) GetMemoryRegion(uint memoryId)
        {
            UIntPtr sz = _retro_get_memory_size?.Invoke(memoryId) ?? UIntPtr.Zero;
            if (sz == UIntPtr.Zero) return (IntPtr.Zero, 0);
            IntPtr ptr = _retro_get_memory_data?.Invoke(memoryId) ?? IntPtr.Zero;
            return (ptr, (uint)sz);
        }

        // Reads the core's SRAM buffer.  Returns null if the core exposes no SRAM.
        public byte[]? GetSaveRam()
        {
            const uint RETRO_MEMORY_SAVE_RAM = 0;
            UIntPtr size = _retro_get_memory_size?.Invoke(RETRO_MEMORY_SAVE_RAM) ?? UIntPtr.Zero;
            if (size == UIntPtr.Zero) return null;

            IntPtr ptr = _retro_get_memory_data?.Invoke(RETRO_MEMORY_SAVE_RAM) ?? IntPtr.Zero;
            if (ptr == IntPtr.Zero) return null;

            byte[] result = new byte[(int)size];
            Marshal.Copy(ptr, result, 0, (int)size);
            return result;
        }

        // Writes data into the core's SRAM buffer.  Copies min(data.Length, coreSize) bytes.
        public bool LoadSaveRam(byte[] data)
        {
            if (data == null || data.Length == 0) return false;
            const uint RETRO_MEMORY_SAVE_RAM = 0;
            UIntPtr size = _retro_get_memory_size?.Invoke(RETRO_MEMORY_SAVE_RAM) ?? UIntPtr.Zero;
            if (size == UIntPtr.Zero) return false;

            IntPtr ptr = _retro_get_memory_data?.Invoke(RETRO_MEMORY_SAVE_RAM) ?? IntPtr.Zero;
            if (ptr == IntPtr.Zero) return false;

            int copySize = Math.Min(data.Length, (int)size);
            Marshal.Copy(data, 0, ptr, copySize);
            return true;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                var handle = _handle;
                _handle = IntPtr.Zero; // zero out first — safe against double-dispose

                if (!_gameUnloaded)
                    try { _retro_unload_game?.Invoke(); } catch { }
                if (!_deinitialized)
                    try { _retro_deinit?.Invoke(); } catch { }

                // Complex cores (PPSSPP, Kronos, Dolphin, mupen64plus) spawn their own
                // internal threads (JIT, GPU renderer, audio, etc.).  retro_deinit tells
                // them to stop, but they may still be executing DLL code when we return.
                // Calling FreeLibrary while a native thread is running inside the DLL
                // causes an access violation that kills the ENTIRE process — not just the
                // emulator window — because it happens on a non-CLR thread where managed
                // exception handlers can't catch it.
                //
                // We wait here to give those threads time to actually finish.
                // Simple cores (snes9x, genesis_plus_gx) run in our thread only and exit
                // instantly, so a short wait costs them nothing meaningful.
                string dllName = System.IO.Path.GetFileName(_corePath).ToLowerInvariant();
                int waitMs = dllName switch
                {
                    var d when d.Contains("ppsspp")       => 1000, // PSP: JIT + GE + audio threads
                    var d when d.Contains("kronos")       =>  800, // Saturn: complex multi-threaded
                    var d when d.Contains("dolphin")      =>  800, // GC/Wii: many internal threads
                    var d when d.Contains("mupen64")      =>  600, // N64: CPU + audio threads
                    var d when d.Contains("parallel_n64") =>  600,
                    var d when d.Contains("mednafen_psx") =>  400, // PS1: some internal state
                    var d when d.Contains("pcsx_rearmed") =>  400,
                    _                                     =>  150, // simple cores
                };
                System.Threading.Thread.Sleep(waitMs);

                // Dolphin and N64 (parallel_n64/mupen64plus) must NOT be unloaded before
                // the quarantine task's wglDeleteContext fires (~5 s for N64, ~3 s for
                // Dolphin after this Dispose call returns).  wglDeleteContext triggers
                // NVIDIA driver cleanup that calls back into core DLL code.  If FreeLibrary
                // has already run, that code is unmapped → AV → process crash.
                //
                // Dolphin and N64 (parallel_n64/mupen64plus): wglDeleteContext triggers
                // NVIDIA driver cleanup that calls back into core DLL code.  FreeLibrary
                // must be deferred until AFTER the GL quarantine period.  Store the handle
                // in DeferredFreeHandle so the caller's quarantine task can free it.
                bool deferFreeLibrary = dllName.Contains("dolphin")
                                     || dllName.Contains("mupen64")
                                     || dllName.Contains("parallel_n64")
                                     || dllName.Contains("ppsspp");
                if (deferFreeLibrary)
                    DeferredFreeHandle = handle;
                else
                    try { NativeMethods.FreeLibrary(handle); } catch { }
            }

            if (_gamePathPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_gamePathPtr); _gamePathPtr = IntPtr.Zero; }
            if (_gameDataPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_gameDataPtr); _gameDataPtr = IntPtr.Zero; }
        }
    }

    // -------------------------------------------------------------------------
    // Libretro structs
    // -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct retro_game_info
    {
        public IntPtr path;
        public IntPtr data;
        public UIntPtr size;
        public IntPtr meta;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct retro_variable
    {
        public IntPtr key;
        public IntPtr value;
        public IntPtr desc;
        public IntPtr next;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct retro_core_option_value
    {
        public IntPtr key;
        public IntPtr value;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct retro_core_option_v2_definition
    {
        public IntPtr key;
        public IntPtr desc;
        public IntPtr info;
        public IntPtr default_value;
        public IntPtr values;
    }

    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll")]
        internal static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);
    }
}