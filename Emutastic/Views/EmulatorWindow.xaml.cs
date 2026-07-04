using Emutastic.Models;
using Emutastic.Services;
using Emutastic.Services.ConsoleHandlers;
using Emutastic.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Emutastic.Effects;

namespace Emutastic.Views
{
    public partial class EmulatorWindow : Window
    {
        // =========================================================================
        // Fields
        // =========================================================================
        private readonly Game _game;
        private readonly LibretroCore _core;
        private volatile bool _loadFailed;
        private DispatcherTimer? _timer;
        private string _srmPath = "";   // per-game battery save file (.srm)
        private WriteableBitmap? _bitmap;
        private uint _videoWidth;
        private uint _videoHeight;
        private uint _lastFrameWidth;   // actual OnVideoRefresh dimensions (all paths, for recording)
        private uint _lastFrameHeight;
        // Reused frame buffer — avoids Large Object Heap allocation every frame.
        // Resized only when the core changes resolution.
        private byte[] _videoFrameBuffer = Array.Empty<byte>();
        private byte[]? _recPackedBuffer;  // Reusable buffer for stripping row padding before recording
        private volatile bool _videoPending = false;

        // Pixel formats
        private const uint RETRO_PIXEL_FORMAT_0RGB1555 = 0;
        private const uint RETRO_PIXEL_FORMAT_XRGB8888 = 1;
        private const uint RETRO_PIXEL_FORMAT_RGB565   = 2;
        private uint _pixelFormat = RETRO_PIXEL_FORMAT_RGB565;

        // Libretro device type IDs
        private const uint RETRO_DEVICE_NONE     = 0;
        private const uint RETRO_DEVICE_JOYPAD   = 1;
        private const uint RETRO_DEVICE_MOUSE    = 2;
        private const uint RETRO_DEVICE_KEYBOARD = 3;
        private const uint RETRO_DEVICE_LIGHTGUN = 4;
        private const uint RETRO_DEVICE_ANALOG   = 5;
        private const uint RETRO_DEVICE_POINTER  = 6;

        // Pointer device ID constants (touch input for NDS)
        private const uint RETRO_DEVICE_ID_POINTER_X       = 0;
        private const uint RETRO_DEVICE_ID_POINTER_Y       = 1;
        private const uint RETRO_DEVICE_ID_POINTER_PRESSED = 2;

        // Pointer state — mouse position normalized to libretro range (-32768..32767)
        private short _pointerX;
        private short _pointerY;
        private volatile bool _pointerPressed;

        // Mouse delta accumulation for RETRO_DEVICE_MOUSE (NDS touch via desmume)
        private double _mouseLastPixelX = double.NaN;
        private double _mouseLastPixelY = double.NaN;
        private int _mouseDeltaX;
        private int _mouseDeltaY;

        // DOS mouse capture — Boxer-style: lock cursor to window, hide it, and warp back to
        // the GameScreen center each move to turn absolute WPF MouseMove into relative deltas.
        // Middle mouse button releases capture. Window Deactivated also releases.
        private bool _mouseCaptured;
        private int  _captureCenterX;      // screen coords of GameScreen center
        private int  _captureCenterY;
        private bool _ignoreNextMove;      // suppress the warp-back event itself
        private volatile bool _leftMousePressed;
        private volatile bool _rightMousePressed;

        // RETRO_DEVICE_ANALOG index / id constants
        private const uint RETRO_DEVICE_INDEX_ANALOG_LEFT   = 0;
        private const uint RETRO_DEVICE_INDEX_ANALOG_RIGHT  = 1;
        private const uint RETRO_DEVICE_INDEX_ANALOG_BUTTON = 2;  // analog triggers (Dreamcast L/R via Flycast)
        private const uint RETRO_DEVICE_ID_ANALOG_X         = 0;
        private const uint RETRO_DEVICE_ID_ANALOG_Y         = 1;

        // Joypad button IDs
        private readonly bool[] _inputState = new bool[16];
        // Raw-keyboard state for cores that poll RETRO_DEVICE_KEYBOARD (DOSBox Pure, etc).
        private readonly Services.RetroKeyboardState _retroKb = new();

        // Keyboard event callback registered by cores via SET_KEYBOARD_CALLBACK (env cmd 12).
        // DOSBox Pure routes INT 16h / text input through this — polled KEYBOARD state alone
        // is not enough for menus, RPG prompts, character-level input.
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate void RetroKeyboardEventDelegate([MarshalAs(UnmanagedType.I1)] bool down, uint keycode, uint character, ushort keyModifiers);
        private RetroKeyboardEventDelegate? _coreKeyboardEvent;

        // Keyboard events must be delivered on the EmuThread — invoking the core's
        // callback from the WPF UI thread while retro_run is executing races DBP's
        // internal thread and corrupts the DOS BIOS buffer, producing a delayed
        // CLR EE fault.  Queue on the UI thread; drain before every retro_run.
        private readonly System.Collections.Concurrent.ConcurrentQueue<(bool down, uint key, ushort mod)> _kbEventQueue
            = new(); // kept GC-rooted; provided by core
        // Canonical values live in Services.LibretroInput; aliased here so the many
        // existing JOYPAD_* references in this file don't all need to change.
        private const uint JOYPAD_B      = Services.LibretroInput.JOYPAD_B;
        private const uint JOYPAD_Y      = Services.LibretroInput.JOYPAD_Y;
        private const uint JOYPAD_SELECT = Services.LibretroInput.JOYPAD_SELECT;
        private const uint JOYPAD_START  = Services.LibretroInput.JOYPAD_START;
        private const uint JOYPAD_UP     = Services.LibretroInput.JOYPAD_UP;
        private const uint JOYPAD_DOWN   = Services.LibretroInput.JOYPAD_DOWN;
        private const uint JOYPAD_LEFT   = Services.LibretroInput.JOYPAD_LEFT;
        private const uint JOYPAD_RIGHT  = Services.LibretroInput.JOYPAD_RIGHT;
        private const uint JOYPAD_A      = Services.LibretroInput.JOYPAD_A;
        private const uint JOYPAD_X      = Services.LibretroInput.JOYPAD_X;
        private const uint JOYPAD_L      = Services.LibretroInput.JOYPAD_L;
        private const uint JOYPAD_R      = Services.LibretroInput.JOYPAD_R;
        private const uint JOYPAD_L2     = Services.LibretroInput.JOYPAD_L2;
        private const uint JOYPAD_R2     = Services.LibretroInput.JOYPAD_R2;
        private const uint JOYPAD_L3     = Services.LibretroInput.JOYPAD_L3;
        private const uint JOYPAD_R3     = Services.LibretroInput.JOYPAD_R3;

        // Turbo / autofire: per-port set of button IDs to modulate.
        // Modulation matches RetroArch defaults: period=6 frames, duty=3 → ~10Hz at 60fps.
        // Counter is the existing _retroRunCallCount (incremented once per retro_run),
        // which makes turbo timing scale correctly with fast-forward and non-60Hz cores.
        private readonly HashSet<uint>[] _turboButtons =
            { new(), new(), new(), new() };
        private const long TurboPeriodFrames = 6;
        private const long TurboDutyFrames   = 3;
        // Buttons that are never turbo-able regardless of user choice.
        private static readonly HashSet<uint> TurboBlacklist = new()
        {
            JOYPAD_SELECT, JOYPAD_START,
            JOYPAD_UP, JOYPAD_DOWN, JOYPAD_LEFT, JOYPAD_RIGHT,
            JOYPAD_L3, JOYPAD_R3,
        };

        // Per-port map of JOYPAD button id -> human label, populated from
        // SET_INPUT_DESCRIPTORS at core init. Lets the turbo dialog show only the
        // buttons the current core actually uses (e.g. NES = just B and A).
        private readonly Dictionary<uint, string>[] _joypadDescriptors =
            { new(), new(), new(), new() };
        private bool _descriptorsReceived;

        private void ParseInputDescriptors(IntPtr data)
        {
            if (data == IntPtr.Zero) return;
            try
            {
                // RetroArch replaces wholesale on each call — cores re-send descriptors
                // after device-type changes (e.g. Saturn 3D pad → digital pad shrinks
                // the button set). Clear before re-populating.
                for (int i = 0; i < _joypadDescriptors.Length; i++)
                    _joypadDescriptors[i].Clear();
                // struct retro_input_descriptor { uint port; uint device; uint index;
                //                                 uint id; const char *description; }
                // Terminated by an entry whose description pointer is NULL.
                int stride = (4 * 4) + IntPtr.Size;
                IntPtr p = data;
                int safety = 0;
                while (safety++ < 4096)
                {
                    IntPtr descPtr = Marshal.ReadIntPtr(p, 16);
                    if (descPtr == IntPtr.Zero) break;
                    uint port   = (uint)Marshal.ReadInt32(p, 0);
                    uint device = (uint)Marshal.ReadInt32(p, 4);
                    // index at +8 — not used for joypad digital buttons
                    uint id     = (uint)Marshal.ReadInt32(p, 12);
                    if (port < 4 && device == RETRO_DEVICE_JOYPAD && id < 16)
                    {
                        string label = Marshal.PtrToStringAnsi(descPtr) ?? "";
                        if (!string.IsNullOrWhiteSpace(label))
                            _joypadDescriptors[port][id] = label;
                    }
                    p = IntPtr.Add(p, stride);
                }
                _descriptorsReceived = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("ParseInputDescriptors: " + ex);
            }
        }

        // Snapshot of available joypad buttons per port for the turbo dialog.
        // Falls back to the standard 8 face/shoulder buttons if the core didn't
        // declare descriptors. Blacklisted IDs (Start/Select/d-pad/L3/R3) are filtered
        // by the dialog so they never appear regardless.
        public IReadOnlyDictionary<uint, string> GetTurboableButtonsForPort(int port)
        {
            if (port < 0 || port >= 4) return new Dictionary<uint, string>();
            // When the core has declared descriptors, treat them as authoritative for
            // *every* port — including ports the core didn't populate. Otherwise a
            // single-port NES would render phantom "Player 2/3/4" sections from the
            // fallback list.
            if (_descriptorsReceived)
                return _joypadDescriptors[port];
            // Fallback (no descriptors at all): canonical labels for the 8 turboable buttons.
            return new Dictionary<uint, string>
            {
                { 0,  "B" }, { 1,  "Y" }, { 8,  "A" }, { 9,  "X" },
                { 10, "L" }, { 11, "R" }, { 12, "L2" }, { 13, "R2" },
            };
        }

        private void LoadTurboConfig()
        {
            if (_game == null) return;
            for (int p = 0; p < _turboButtons.Length; p++)
            {
                _turboButtons[p].Clear();
                string raw = _configService.GetValue($"turbo_p{p}_{_game.Id}", "");
                if (string.IsNullOrWhiteSpace(raw)) continue;
                foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (uint.TryParse(part.Trim(), out uint id) && id < 16 && !TurboBlacklist.Contains(id))
                        _turboButtons[p].Add(id);
                }
            }
        }

        private void SaveTurboConfig()
        {
            if (_game == null) return;
            for (int p = 0; p < _turboButtons.Length; p++)
            {
                string raw = string.Join(",", _turboButtons[p]);
                _configService.SetValue($"turbo_p{p}_{_game.Id}", raw);
            }
        }

        // Public bridge for TurboButtonsDialog — toggles save immediately on click,
        // matching the cheats UX (no separate Save button).
        public void SaveTurboConfigPublic() => SaveTurboConfig();

        private bool TurboGate(uint port, uint id)
        {
            if (port >= 4) return true;
            // Snapshot the reference; the dialog atomically swaps in a new HashSet
            // when the user saves, so reading once gives a tear-free view even if
            // the swap races with this read.
            var set = _turboButtons[port];
            if (set.Count == 0 || !set.Contains(id)) return true;
            return (_retroRunCallCount % TurboPeriodFrames) < TurboDutyFrames;
        }

        // Keyboard analog axis state — used when no controller is connected.
        // Values follow libretro convention: up/left = negative, down/right = positive.
        // Y is already negated at assignment time so no further inversion is needed
        // when the controller path reads _keyLeftStickY.
        private short _keyLeftStickX;
        private short _keyLeftStickY;
        private short _keyRightStickX;
        private short _keyRightStickY;

        // Directory pointers (unmanaged lifetime)
        private IntPtr _systemDirPtr  = IntPtr.Zero;
        private IntPtr _saveDirPtr    = IntPtr.Zero;
        private IntPtr _contentDirPtr = IntPtr.Zero;

        // Memory descriptor regions captured from RETRO_ENVIRONMENT_SET_MEMORY_MAPS.
        // Cores typically publish this during retro_load_game, which runs BEFORE
        // _raClient is created (in InitRetroAchievements). Buffer here, feed to
        // _raClient.SetMemoryDescriptors immediately after _raClient.Initialize.
        private Services.RetroAchievementsClient.MemoryRegion[]? _pendingMemoryRegions;

        // Pinned callback delegates (must stay alive as long as the core is running)
        private retro_environment_t?        _envCb;
        private retro_video_refresh_t?      _videoCb;
        private retro_audio_sample_t?       _audioCb;
        private retro_audio_sample_batch_t? _audioBatchCb;
        private retro_input_poll_t?         _inputPollCb;
        private retro_input_state_t?        _inputStateCb;
        private retro_log_printf_t?         _logCb;

        private GCHandle? _envCbHandle;
        private GCHandle? _videoCbHandle;
        private GCHandle? _audioCbHandle;
        private GCHandle? _audioBatchCbHandle;
        private GCHandle? _inputPollCbHandle;
        private GCHandle? _inputStateCbHandle;
        private GCHandle? _logCbHandle;

        // Console handler — all console-specific behaviour delegated here
        private readonly IConsoleHandler _consoleHandler;

        // Target frame budget in ms — written once at startup, updated by SET_SYSTEM_AV_INFO.
        // Read on emu thread each frame; written from env callback (also emu thread) → no lock needed.
        private double _targetFrameMs = 1000.0 / 60.0;

        // Two distinct framerate signals, both reset each timer tick:
        //   _frameCount    — frames actually PRESENTED to screen (display cadence).
        //                    Counted only at real present points, AFTER the
        //                    drop guards, so frames the screen never showed
        //                    (weak GPU / busy UI thread) are not counted.
        //   _emuFrameCount — times the core was stepped (emulation rate),
        //                    incremented once per _core.Run(). Diverges above
        //                    display cadence when presentation is the bottleneck.
        private int  _frameCount        = 0;
        private int  _emuFrameCount     = 0;
        private long _coreRunTotalTicks  = 0;   // sum of Stopwatch ticks spent inside _core.Run()
        private int  _coreRunSampleCount = 0;

        private long _retroRunCallCount = 0;

        // Transient save/load status — shown for 3s alongside the FPS counter
        private string   _transientMsg    = "";
        private DateTime _transientExpiry = DateTime.MinValue;

        // Last-seen controller name list for the in-game hot-plug status diff
        // (primed on the first status-timer tick; see the tick handler).
        private System.Collections.Generic.List<string>? _ctrlStatusLast;

        // Services — up to 4 controllers (one per XInput slot / libretro port)
        private readonly ControllerManager?[] _controllers = new ControllerManager?[4];
        private ControllerManager? _controllerManager; // alias for _controllers[0]
        private AudioPlayer?       _audioPlayer;
        private IRecordingService?  _recordingService;
        private readonly IConfigurationService _configService;
        private InputConfiguration? _inputConfig;
        private readonly Dictionary<Key, uint> _keyboardMappings = new();
        private DatabaseService? _db;
        private DateTime _sessionStartUtc;

        // RetroAchievements
        private RetroAchievementsClient? _raClient;
        // Snapshot of HardcoreMode taken when _raClient was created. Re-reading
        // config live would let a user open Preferences mid-game and silently
        // relax the gates — using a snapshot makes the running session's HC
        // status stable until the next launch, matching RA's session model.
        private bool _raHardcoreActive;

        // Overlay HUD
        private bool _isPaused = false;

        // Rumble interface — Reicast/Flycast gates VMU sub-peripheral init on whether
        // the frontend supplies a rumble interface, so this must always return a valid
        // function pointer.  The callback also drives actual controller vibration:
        // effect 0 = strong (left motor), effect 1 = weak (right motor).
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate bool SetRumbleStateDelegate(uint port, uint effect, ushort strength);
        private SetRumbleStateDelegate? _rumbleStateDelegate;

        private ushort _rumbleStrong = 0; // left/low-freq motor
        private ushort _rumbleWeak   = 0; // right/high-freq motor

        private bool OnSetRumbleState(uint port, uint effect, ushort strength)
        {
            if (port < 4)
            {
                var ctrl = _controllers[port];
                if (ctrl != null)
                {
                    // effect 0 = RETRO_RUMBLE_STRONG (left/low-freq motor)
                    // effect 1 = RETRO_RUMBLE_WEAK   (right/high-freq motor)
                    // Cores send each motor independently; accumulate both before applying.
                    // Note: rumble accumulators are only tracked for port 0 (P1).
                    if (port == 0)
                    {
                        if (effect == 0) _rumbleStrong = strength;
                        else             _rumbleWeak   = strength;
                        ctrl.SetVibration(_rumbleStrong, _rumbleWeak);
                    }
                    else
                    {
                        // For ports 1-3, apply directly (no cross-frame accumulation)
                        ctrl.SetVibration(
                            effect == 0 ? strength : (ushort)0,
                            effect == 1 ? strength : (ushort)0);
                    }
                }
            }
            return true;
        }
        private DispatcherTimer? _overlayTimer;
        private DispatcherTimer? _mousePoller;
        private DispatcherTimer? _swapchainResizeTimer;
        private System.Windows.Point _lastMousePos = new(-1, -1);

        // Analog-to-mouse delta for cores that use RETRO_DEVICE_MOUSE for pointer input.
        // Stick value ÷ this scale = pixels of cursor movement per frame.
        private const float MouseAnalogScale = 200f;

        // Save state
        private string _saveStatePath = "";    // file-system dir for this game's save states
        private volatile bool _saveStatePending = false;
        private volatile bool _loadStatePending = false;
        private string _pendingSaveName  = "";
        private byte[]? _pendingLoadData = null;
        private string _pendingLoadName  = "";
        // rcheevos runtime-state side-car bytes paired with the active state.
        // Read alongside _pendingLoadData and handed to rc_client_deserialize_progress
        // after retro_unserialize succeeds, so partial achievement progress
        // (hit counts, measured trackers) survives the save → load round trip.
        private byte[]? _pendingLoadCheevosBlob = null;
        private string? _pendingLoadStatePath = null;  // load on startup if set
        // CoreName captured from the .state's JSON sidecar at queue time so
        // ExecuteLoadOnEmuThread can refuse cross-core loads. Save state byte
        // formats are NOT portable between cores for the same system (e.g.
        // a Beetle PSX HW state can't be loaded on Beetle PSX SW or
        // Swanstation, even though all three play PSX games). retro_unserialize
        // returns true on the bytes that parse but the loaded state is
        // incoherent and the game wedges. Comparing CoreName at load time
        // prevents the freeze with a clear status-bar message.
        private string _pendingLoadSavedCoreName = "";

        /// <summary>
        /// Read the CoreName from a save state's JSON sidecar (FinalizeSave
        /// writes one alongside every .state file). Returns empty string if
        /// the sidecar is missing/unreadable — callers should treat empty as
        /// "unknown, allow the load" since we don't want to block legacy
        /// states made before sidecars existed.
        /// </summary>
        private static string ReadSavedCoreName(string statePath)
        {
            try
            {
                string json = Path.ChangeExtension(statePath, ".json");
                if (!File.Exists(json)) return "";
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("CoreName", out var cn))
                    return cn.GetString() ?? "";
            }
            catch { }
            return "";
        }
        // Frame counter for the startup-state retry loop. Some cores (Beetle PSX,
        // PCSX-ReARMed) report retro_serialize_size differently during BIOS boot
        // vs. once the game is running, so retro_unserialize fails on the first
        // frame. We retry each frame until success or this cap (~10s @60fps).
        private int _loadStateAttempts = 0;
        private const int MaxLoadStateAttempts = 600;

        // Set by the SET_SERIALIZATION_QUIRKS env handler when the core declares
        // RETRO_SERIALIZATION_QUIRK_SINGLE_SESSION (1 << 4). Cores with this flag
        // (Kronos Saturn, DOSBox Pure, others) document that retro_unserialize
        // is only valid within the same process where retro_serialize was called
        // — restoring across launches looks successful but the core's internal
        // threads/dynarec caches are in inconsistent state and the game freezes.
        // We refuse the startup-state load and let the BIOS boot normally
        // instead of presenting a deceptively "loaded" but frozen game.
        private bool _coreSingleSessionStates = false;

        // Number of retro_run iterations to drive AFTER context_reset before
        // attempting the first retro_unserialize. Beetle PSX HW (and probably
        // any HW renderer that defers VRAM uploads through gl_context_reset's
        // queue drain) wedges if unserialize fires before the pipeline is
        // warm: GPU_RestoreStateP3 queues a 1MB VRAM blob, the queue never
        // drains, the CPU stalls waiting on a GPU IRQ that never asserts,
        // and the game appears "loaded" but frozen on the saved frame. Known
        // upstream issues: libretro/beetle-psx-libretro #297, #423, #443,
        // #445 (FF8-specific), #604. Workaround documented across all those
        // threads: run frames first, THEN unserialize.
        private int _loadStateWarmup = 0;
        private const int LoadStateWarmupFrames = 60;
        // Cheats — loaded once per game from disk, applied after retro_load_game and after every state load.
        private System.Collections.Generic.List<Models.Cheat> _cheats = new();
        private bool _cheatsApplied = false;
        private volatile bool _cheatsApplyPending = false;
        private System.Collections.Generic.List<Models.Cheat>? _cheatsApplyPayload;
        private readonly object _cheatsApplyLock = new();

        // Frontend-handled AR cheats: parsed (address, value, byteCount) tuples
        // applied to system RAM directly after each retro_run. Mirrors the
        // "RetroArch handled" cheat path so codes like Genesis FFFE12:0009
        // actually take effect even though genesis_plus_gx's retro_cheat_set
        // is unreliable for AR.
        private volatile Services.CheatService.ParsedAr[] _frontendArCheats = System.Array.Empty<Services.CheatService.ParsedAr>();
        private IntPtr _systemRamPtr  = IntPtr.Zero;
        private uint   _systemRamSize = 0;

        // Core options
        private readonly Dictionary<string, string> _coreOptions = new();
        // Track unmanaged string ptrs returned via GET_VARIABLE to prevent leaks
        private readonly Dictionary<string, IntPtr> _coreOptionPtrs = new();
        // Tracks the value that each live HGlobal in _coreOptionPtrs currently encodes,
        // so we can return the SAME pointer for repeated GET_VARIABLE calls with an
        // unchanged value. Freeing + reallocating on every call is a use-after-free: cores
        // like DOSBox Pure cache the const char* we return and dereference it later.
        private readonly Dictionary<string, string> _coreOptionPtrValues = new();
        // Every HGlobal we've ever handed to the core for GET_VARIABLE responses.
        // Freed in one shot at emulator close — never mid-session.
        private readonly List<IntPtr> _coreOptionPtrsAllocated = new();
        // Schema accumulated during SET_VARIABLES — saved for the Preferences UI
        private readonly List<CoreOptionEntry> _coreOptionSchema = new();
        // Set to true when the user changes an option mid-game so the core re-reads
        private volatile bool _coreOptionsDirty = false;


        // =========================================================================
        // Disc control state
        //
        // When a core calls RETRO_ENVIRONMENT_SET_DISK_CONTROL_INTERFACE it gives
        // us a struct of its own function pointers.  We store them here and return
        // true to signal we support disc swapping.  For single-disc CHD games the
        // core never calls these back — it just needs the env call to return true
        // to enable disc image loading internally.
        // =========================================================================
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DiskSetEjectState_t(bool ejected);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DiskGetEjectState_t();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint DiskGetImageIndex_t();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DiskSetImageIndex_t(uint index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint DiskGetNumImages_t();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DiskAddImageIndex_t();

        // C ABI layout: 7 pointers at 8 bytes each on 64-bit
        [StructLayout(LayoutKind.Explicit)]
        private struct retro_disk_control_callback
        {
            [FieldOffset(0)]  public IntPtr set_eject_state;
            [FieldOffset(8)]  public IntPtr get_eject_state;
            [FieldOffset(16)] public IntPtr get_image_index;
            [FieldOffset(24)] public IntPtr set_image_index;
            [FieldOffset(32)] public IntPtr get_num_images;
            [FieldOffset(40)] public IntPtr replace_image_index;
            [FieldOffset(48)] public IntPtr add_image_index;
        }

        private DiskSetEjectState_t? _diskSetEjectState;
        private DiskGetEjectState_t? _diskGetEjectState;
        private DiskGetImageIndex_t? _diskGetImageIndex;
        private DiskSetImageIndex_t? _diskSetImageIndex;
        private DiskGetNumImages_t?  _diskGetNumImages;
        private DiskAddImageIndex_t? _diskAddImageIndex;
        private bool _diskControlAvailable = false;

        // Frontend-side disk-swap action — always a CHORD (two inputs held together)
        // so it never steals a gameplay button. Default chord is L3 + Start (L3 is
        // rarely used in games and doesn't collide with Steam/Xbox guide overlays).
        // User can rebind to any two keys or any two controller buttons via
        // Preferences → Controls → "Disk Swap". Detected on EmuThread (rising edge).
        // Stored as volatile int (Key cast to int) with -1 = unset. Avoids torn
        // reads of the underlying Nullable<Key> struct when the UI thread writes
        // a new binding while the EmuThread is reading.
        private volatile int _diskSwapKeyA = -1;
        private volatile int _diskSwapKeyB = -1;
        private uint _diskSwapCtrlA = uint.MaxValue;
        private uint _diskSwapCtrlB = uint.MaxValue;

        // EmuTV save/load hotkey bindings (single controller button id; MaxValue =
        // unset → defaults L3/R2/L2). Loaded per-console in LoadKeyboardMappings.
        private uint _hotkeyModCtrl = uint.MaxValue; // modifier, default L3 (14)
        private uint _saveCtrl      = uint.MaxValue; // save,     default R2 (13)
        private uint _loadCtrl      = uint.MaxValue; // load,     default L2 (12)
        private bool _diskSwapPrevHeld;
        private volatile bool _diskSwapKeyAHeld;
        private volatile bool _diskSwapKeyBHeld;
        // Default controller chord when user has no binding: L3 (14) + Start (3).
        private const uint DEFAULT_DISK_SWAP_CTRL_A = 14;
        private const uint DEFAULT_DISK_SWAP_CTRL_B = 3;
        private System.Windows.Threading.DispatcherTimer? _diskStatusRevertTimer;
        private string? _diskStatusPrevText;
        // Consoles whose cores typically register the disk control interface.
        // Used to decide whether to show the Disk Swap row in Preferences.
        // Cores verified to register the libretro disk-control interface (or, for
        // FDS, to use the JOYPAD_L injection convention) against upstream source:
        //   FDS    — Nestopia/Mesen/FCEUmm: JOYPAD_L injection (no env interface)
        //   PS1    — Beetle PSX: env 58 EXT (or env 13 fallback) registered in retro_init
        //   Saturn — Kronos (libretro/yabause kronos branch): env 13/58 registered, .m3u required for >1 disc
        //   SegaCD — Genesis Plus GX: env 13, deregisters NULL when system_hw != MCD
        //   Amiga  — PUAE: env 13/58 registered in retro_init
        //
        // Removed (cores DON'T register disk control upstream):
        //   TurboGrafx16/PCECD/TG16 — Beetle PCE and Beetle PCE Fast both lack registration
        //   3DO — Opera lacks any disk-control code; 3DO games are typically single-disc
        private static readonly HashSet<string> DiskCapableConsoles =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "FDS", "PS1", "Saturn", "SegaCD", "Amiga",
            };
        public static bool ConsoleSupportsDiskSwap(string console)
            => !string.IsNullOrEmpty(console) && DiskCapableConsoles.Contains(console);

        // =========================================================================
        // Native crash diagnostics + NULL-pointer fixup via VEH
        // =========================================================================
        [DllImport("kernel32.dll")] private static extern IntPtr AddVectoredExceptionHandler(uint first, IntPtr handler);
        [DllImport("kernel32.dll")] private static extern uint RemoveVectoredExceptionHandler(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern void OutputDebugStringW(string msg);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetModuleHandleExW(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetModuleFileNameW(IntPtr hModule, System.Text.StringBuilder lpFilename, uint nSize);
        [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);

        private delegate int VehDelegate(IntPtr exceptionInfo);
        private static VehDelegate? _vehDelegate;
        private static GCHandle? _vehGcHandle;
        private static IntPtr _vehHandle;
        private static IntPtr _dummyPage = IntPtr.Zero; // reusable zeroed page for NULL fixups
        private static volatile bool _vulkanTeardownComplete; // set after Vulkan context disposed
        private static IntPtr _staleDllHandle;  // DLL handle from previous session that needs freeing before next launch

        /// <summary>
        /// Free any stale core DLL from a previous Vulkan session.
        /// MUST be called BEFORE LoadLibrary/new LibretroCore — otherwise LoadLibrary
        /// increments the refcount on the still-loaded DLL, FreeLibrary only decrements
        /// it back, and the DLL never actually unloads (globals stay stale).
        /// </summary>
        public static void FreeStaleDll()
        {
            IntPtr staleDll = System.Threading.Interlocked.Exchange(ref _staleDllHandle, IntPtr.Zero);
            if (staleDll != IntPtr.Zero)
            {
                System.Diagnostics.Trace.WriteLine($"Freeing stale DLL before core load: 0x{staleDll:X}");
                try { NativeMethods.FreeLibrary(staleDll); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Stale DLL free: {ex.Message}"); }
            }
        }

        private const uint EXCEPTION_ACCESS_VIOLATION = 0xC0000005;
        private const int EXCEPTION_CONTINUE_SEARCH = 0;
        private const int EXCEPTION_CONTINUE_EXECUTION = -1;

        // x64 CONTEXT register offsets (from Microsoft docs)
        private const int CTX_RAX = 0x78, CTX_RCX = 0x80, CTX_RDX = 0x88, CTX_RBX = 0x90;
        private const int CTX_RSP = 0x98, CTX_RBP = 0xA0, CTX_RSI = 0xA8, CTX_RDI = 0xB0;
        private const int CTX_R8  = 0xB8, CTX_R9  = 0xC0, CTX_R10 = 0xC8, CTX_R11 = 0xD0;
        private const int CTX_R12 = 0xD8, CTX_R13 = 0xE0, CTX_R14 = 0xE8, CTX_R15 = 0xF0;
        private const int CTX_RIP = 0xF8;

        private static int NativeExceptionHandler(IntPtr exceptionInfoPtr)
        {
            try
            {
                IntPtr recordPtr = Marshal.ReadIntPtr(exceptionInfoPtr, 0);
                IntPtr contextPtr = Marshal.ReadIntPtr(exceptionInfoPtr, IntPtr.Size);
                uint code = (uint)Marshal.ReadInt32(recordPtr, 0);

                if (code != EXCEPTION_ACCESS_VIOLATION) return EXCEPTION_CONTINUE_SEARCH;

                IntPtr faultingIP = Marshal.ReadIntPtr(recordPtr, 16);
                uint numParams = (uint)Marshal.ReadInt32(recordPtr, 24);
                long accessType = numParams >= 1 ? Marshal.ReadInt64(recordPtr, 32) : -1;
                long faultAddr = numParams >= 2 ? Marshal.ReadInt64(recordPtr, 40) : 0;

                // Identify which module the faulting IP is in
                string modName = "unknown";
                if (GetModuleHandleExW(0x4 | 0x2, faultingIP, out IntPtr hMod) && hMod != IntPtr.Zero)
                {
                    var sb = new System.Text.StringBuilder(260);
                    GetModuleFileNameW(hMod, sb, 260);
                    modName = System.IO.Path.GetFileName(sb.ToString());
                }

                long rva = hMod != IntPtr.Zero ? ((long)faultingIP - (long)hMod) : 0;
                string msg = $"!!! NATIVE AV in [{modName}] RVA=0x{rva:X}: IP=0x{faultingIP:X} " +
                             $"{(accessType == 0 ? "READ" : accessType == 1 ? "WRITE" : "DEP")} " +
                             $"addr=0x{faultAddr:X16}";
                OutputDebugStringW(msg);
                System.Diagnostics.Trace.WriteLine(msg);

                // ---------------------------------------------------------------
                // Fixup C: Post-teardown driver/core thread AVs.
                //
                // After Vulkan teardown, background threads from nvoglv64.dll,
                // ParaLLEl-RDP, and the core may AV on destroyed swapchain/surface
                // resources.  VkDevice/VkInstance are kept alive (leaked) so the
                // driver's device tables stay clean for relaunch.
                //
                // Catch ALL post-teardown AVs and ExitThread the faulting thread.
                // Only do this on background threads (not the main thread).
                // ---------------------------------------------------------------
                if (_vulkanTeardownComplete)
                {
                    try
                    {
                        IntPtr exitThreadAddr = NativeMethods2.GetProcAddress(
                            NativeMethods2.GetModuleHandle("kernel32.dll"), "ExitThread");
                        if (exitThreadAddr != IntPtr.Zero)
                        {
                            Marshal.WriteInt64(contextPtr, CTX_RCX, 0);
                            Marshal.WriteInt64(contextPtr, CTX_RIP, exitThreadAddr.ToInt64());

                            string fixMsg = $"  → ExitThread redirect for post-teardown AV in [{modName}]";
                            OutputDebugStringW(fixMsg);
                            System.Diagnostics.Trace.WriteLine(fixMsg);
                            return EXCEPTION_CONTINUE_EXECUTION;
                        }
                    }
                    catch { }
                }

                // ---------------------------------------------------------------
                // Fixup A: GL dispatch-table null-deref in OPENGL32.DLL.
                //
                // mupen64plus/glide64's cleanup thread calls GL functions after
                // retro_unload_game returns, but has no current GL context.
                // OPENGL32.DLL's dispatch stub does:
                //   mov r64, [r64 + 0xA38]   <- reads function ptr from null ctx
                //   call r64                  <- calls through the loaded ptr
                //
                // glide64 wraps these calls in __try/__except, but when the
                // cleanup thread's call-stack doesn't have the handler in scope
                // the AV propagates and kills the process.
                //
                // Fix: when we see a READ fault at address 0xA38 in OPENGL32.DLL,
                // decode the 7-byte "REX.W MOV reg, [base+disp32]" instruction,
                // zero the destination register, and advance RIP past it.
                // The next CALL through the now-zero register then faults at IP=0
                // (Fixup B below simulates "ret" from that call).
                //
                // This is safe to apply unconditionally for this specific pattern:
                // address 0xA38 is never a valid GL dispatch read during live
                // emulation — it only happens when the context pointer is NULL.
                // ---------------------------------------------------------------
                if (accessType == 0 /* READ */ && faultAddr == 0x0A38
                    && modName.Equals("opengl32.dll", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Expected encoding: REX.W(0x48|0x4C) + 0x8B + ModRM(mod=2) + 38 0A 00 00
                        byte rex   = Marshal.ReadByte(faultingIP, 0);
                        byte op    = Marshal.ReadByte(faultingIP, 1);
                        byte modrm = Marshal.ReadByte(faultingIP, 2);
                        byte d0    = Marshal.ReadByte(faultingIP, 3);
                        byte d1    = Marshal.ReadByte(faultingIP, 4);
                        byte d2    = Marshal.ReadByte(faultingIP, 5);
                        byte d3    = Marshal.ReadByte(faultingIP, 6);
                        int  mod   = (modrm >> 6) & 0x3;
                        int  reg   = (modrm >> 3) & 0x7;   // destination register index
                        int  rm    = modrm & 0x7;           // r/m field

                        if ((rex == 0x48 || rex == 0x4C)    // REX.W (+ optional REX.R)
                            && op == 0x8B                   // MOV r64, r/m64
                            && mod == 2                     // disp32 addressing
                            && rm != 4                      // no SIB byte
                            && d0 == 0x38 && d1 == 0x0A && d2 == 0x00 && d3 == 0x00)
                        {
                            // Map reg field → CONTEXT offset.  REX.R extends reg to R8–R15.
                            bool rexR = (rex & 0x04) != 0;
                            int[] baseOff = { CTX_RAX, CTX_RCX, CTX_RDX, CTX_RBX, 0, CTX_RBP, CTX_RSI, CTX_RDI };
                            int[] extOff  = { CTX_R8,  CTX_R9,  CTX_R10, CTX_R11, 0, CTX_R13, CTX_R14, CTX_R15 };
                            int ctxOff = rexR ? extOff[reg] : baseOff[reg];
                            if (ctxOff != 0)
                            {
                                Marshal.WriteInt64(contextPtr, ctxOff, 0);               // zero destination
                                Marshal.WriteInt64(contextPtr, CTX_RIP, faultingIP.ToInt64() + 7); // skip instruction
                                return EXCEPTION_CONTINUE_EXECUTION;
                            }
                        }
                    }
                    catch { }
                }

                // ---------------------------------------------------------------
                // Fixup B: call-through-null follow-up from Fixup A.
                //
                // After Fixup A zeroes the function-pointer register, the next
                // instruction is CALL <that register>.  Calling address 0 pushes
                // the return address onto the stack and then faults at IP=0.
                // Simulate a "ret": restore RIP from the top of stack and pop RSP.
                // ---------------------------------------------------------------
                if (faultingIP == IntPtr.Zero)
                {
                    try
                    {
                        long rsp        = Marshal.ReadInt64(contextPtr, CTX_RSP);
                        long returnAddr = Marshal.ReadInt64((IntPtr)rsp);
                        Marshal.WriteInt64(contextPtr, CTX_RIP, returnAddr);
                        Marshal.WriteInt64(contextPtr, CTX_RSP, rsp + 8);
                        return EXCEPTION_CONTINUE_EXECUTION;
                    }
                    catch { }
                }

                // Log only for everything else — do NOT attempt to fix up.
                // Old plugins (glide64, rice) use __try/__except as normal flow
                // control; intercepting those AVs and patching the context corrupts
                // their state and causes a secondary crash that kills the process.
            }
            catch { /* must not throw from VEH */ }
            return EXCEPTION_CONTINUE_SEARCH;
        }

        private static void InstallCrashDiagnostics()
        {
            _vehDelegate = NativeExceptionHandler;
            _vehGcHandle = GCHandle.Alloc(_vehDelegate);
            IntPtr fnPtr = Marshal.GetFunctionPointerForDelegate(_vehDelegate);
            _vehHandle = AddVectoredExceptionHandler(1, fnPtr);
        }

        // =========================================================================
        // OpenGL / HW render state
        // =========================================================================
        [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string name);
        [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hdc);
        [DllImport("opengl32.dll")] private static extern bool   wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
        [DllImport("opengl32.dll")] private static extern bool   wglDeleteContext(IntPtr hglrc);
        [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
        [DllImport("user32.dll")]   private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]   private static extern int    ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")]   private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]   private static extern bool   PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("gdi32.dll")]    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")]    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")]    private static extern bool   DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")]    private static extern bool   DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]    private static extern int    GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFO bmi, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER { public int biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)] public byte[] bmiColors; }

        /// <summary>
        /// Capture an HWND's client area to a BGRA32 BitmapSource via PrintWindow
        /// with PW_RENDERFULLCONTENT. Works for many overlay surfaces (incl. some
        /// DXGI/Vulkan compositions) when DWM compositing is enabled. Returns null
        /// if the call fails or the window is zero-size.
        /// </summary>
        private static BitmapSource? CaptureWindowToBitmap(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            if (!GetClientRect(hwnd, out var rc)) return null;
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0) return null;

            const uint PW_RENDERFULLCONTENT = 0x02;
            IntPtr screenDC = IntPtr.Zero, memDC = IntPtr.Zero, hbm = IntPtr.Zero, oldObj = IntPtr.Zero;
            try
            {
                screenDC = GetDC(IntPtr.Zero);
                memDC    = CreateCompatibleDC(screenDC);
                hbm      = CreateCompatibleBitmap(screenDC, w, h);
                oldObj   = SelectObject(memDC, hbm);

                if (!PrintWindow(hwnd, memDC, PW_RENDERFULLCONTENT))
                    return null;

                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize        = Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth       = w,
                        biHeight      = -h,           // top-down
                        biPlanes      = 1,
                        biBitCount    = 32,
                        biCompression = 0,            // BI_RGB
                    },
                    bmiColors = new byte[1024],
                };
                int stride = w * 4;
                byte[] pixels = new byte[stride * h];
                var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    if (GetDIBits(memDC, hbm, 0, (uint)h, pin.AddrOfPinnedObject(), ref bmi, 0) == 0)
                        return null;
                }
                finally { pin.Free(); }

                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
            finally
            {
                if (oldObj   != IntPtr.Zero) SelectObject(memDC, oldObj);
                if (hbm      != IntPtr.Zero) DeleteObject(hbm);
                if (memDC    != IntPtr.Zero) DeleteDC(memDC);
                if (screenDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDC);
            }
        }
        [DllImport("gdi32.dll")]    private static extern int    ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32.dll")]    private static extern bool   SetPixelFormat(IntPtr hdc, int fmt, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32.dll")]    private static extern bool   DescribePixelFormat(IntPtr hdc, int iPixelFormat, uint nBytes, ref PIXELFORMATDESCRIPTOR ppfd);
        [DllImport("gdi32.dll")]    private static extern bool   SwapBuffers(IntPtr hdc);
        [DllImport("opengl32.dll")] private static extern void   glReadPixels(int x, int y, int width, int height, uint format, uint type, IntPtr pixels);
        [DllImport("opengl32.dll")] private static extern uint   glGetError();

        private const uint GL_FRAMEBUFFER       = 0x8D40;
        private const uint GL_READ_FRAMEBUFFER  = 0x8CA8;
        private const uint GL_RGBA              = 0x1908;
        private const uint GL_UNSIGNED_BYTE     = 0x1401;
        private const uint GL_BGRA              = 0x80E1;
        private const uint GL_TEXTURE_2D        = 0x0DE1;
        private const uint GL_TEXTURE_MIN_FILTER= 0x2801;
        private const uint GL_TEXTURE_MAG_FILTER= 0x2800;
        private const uint GL_LINEAR            = 0x2601;
        private const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
        private const uint GL_DEPTH_ATTACHMENT  = 0x8D00;
        private const uint GL_RENDERBUFFER      = 0x8D41;
        private const uint GL_DEPTH_COMPONENT24 = 0x81A5;
        private const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
        private const uint GL_DRAW_FRAMEBUFFER  = 0x8CA9;
        private const uint GL_COLOR_BUFFER_BIT  = 0x00004000;
        private const uint GL_NEAREST           = 0x2600;
        private const int  GL_RGBA8             = 0x8058;
        private const uint GL_PIXEL_PACK_BUFFER = 0x88EB;
        private const uint GL_STREAM_READ       = 0x88E1;
        private const uint GL_READ_ONLY         = 0x88B8;

        [StructLayout(LayoutKind.Sequential)]
        private struct PIXELFORMATDESCRIPTOR
        {
            public ushort nSize, nVersion;
            public uint dwFlags;
            public byte iPixelType, cColorBits, cRedBits, cRedShift;
            public byte cGreenBits, cGreenShift, cBlueBits, cBlueShift;
            public byte cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits;
            public byte cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
            public byte cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
            public uint dwLayerMask, dwVisibleMask, dwDamageMask;
        }

        private const uint PFD_DRAW_TO_WINDOW = 0x00000004;
        private const uint PFD_SUPPORT_OPENGL = 0x00000020;
        private const uint PFD_DOUBLEBUFFER   = 0x00000001;
        private const byte PFD_TYPE_RGBA      = 0;

        private const int WGL_CONTEXT_MAJOR_VERSION_ARB             = 0x2091;
        private const int WGL_CONTEXT_MINOR_VERSION_ARB             = 0x2092;
        private const int WGL_CONTEXT_PROFILE_MASK_ARB              = 0x9126;
        private const int WGL_CONTEXT_CORE_PROFILE_BIT_ARB          = 0x00000001;
        private const int WGL_CONTEXT_COMPATIBILITY_PROFILE_BIT_ARB = 0x00000002;

        private delegate IntPtr wglCreateContextAttribsARBDelegate(IntPtr hDC, IntPtr hShareContext, int[] attribList);
        private delegate bool   wglSwapIntervalEXTDelegate(int interval);

        private IntPtr _hwnd         = IntPtr.Zero;
        private IntPtr _hdc          = IntPtr.Zero;
        private IntPtr _hglrc        = IntPtr.Zero;  // share context — never current after context_reset
        private IntPtr _secondaryCtx = IntPtr.Zero;  // main-thread rendering context, shares with _hglrc
        private wglCreateContextAttribsARBDelegate? _wglCreateContextAttribsARB;
        private bool   _hwRenderActive  = false;
        private ShaderPreset _activeShader = ShaderPreset.None;

        // Downloaded libretro slang shader (librashader). Runs on the emu thread and
        // reads back into the existing WriteableBitmap, so the HUD/AR/screenshot/
        // recording paths are untouched. Null preset = built-in WPF shaders / None;
        // the raw present path stays byte-identical when this is off.
        private Effects.Librashader.ShaderRenderer? _shaderRenderer;
        private volatile string? _slangPresetPath;
        private volatile bool _slangInitFailed;
        private volatile bool _shaderResetRequested;  // UI sets; emu thread acts
        private System.Windows.Data.ListCollectionView? _shaderView;  // picker list view
        private bool _suppressShaderSelect;                           // guard programmatic selection
        private bool   _vsyncDisabled   = false;
        private GameHwndHost? _hwndHost;

        private retro_hw_context_reset_t?           _hwContextReset;
        private retro_hw_context_reset_t?           _hwContextDestroy;
        private retro_hw_get_current_framebuffer_t? _getFramebufferDelegate;
        private retro_hw_get_proc_address_t?        _getProcAddressDelegate;
        private GCHandle? _getFramebufferHandle;
        private GCHandle? _getProcAddressHandle;

        private uint _fboId     = 0;
        private uint _fboTex    = 0;
        private uint _fboDepth  = 0;
        private uint _fboWidth  = 640;
        private uint _fboHeight = 480;

        // Reusable pixel buffers for HW readback — avoids 2.4 MB of per-frame allocations
        // (one for glReadPixels result, one for the vertically-flipped copy sent to WPF).
        // Resized only when the render resolution changes.
        private byte[] _hwPixelBuffer   = Array.Empty<byte>();
        private byte[] _hwFlippedBuffer = Array.Empty<byte>();
        private uint   _hwFlippedWidth  = 0;   // actual readback dimensions (may differ from _fboWidth/Height)
        private uint   _hwFlippedHeight = 0;
        private volatile bool _hwVideoPending = false;  // true while a BeginInvoke frame callback is queued

        // ── Direct3D 11 HW rendering (PS2 / LRPS2 D3D11 GS backend) ──────────────
        private D3D11Context? _d3d11Context;
        private bool _isD3d11HwRender = false;
        private bool _d3d11VideoPending = false;
        private System.Windows.Interop.D3DImage? _d3dImage;
        private int _lastCapSrcW = -1, _lastCapSrcH = -1;   // throttles the per-frame cap log

        /// <summary>
        /// Clamps a HW-render frame size to the monitor's pixel dimensions,
        /// preserving aspect ratio. There is no point presenting a surface larger
        /// than the display can show, and the per-frame D3DImage copy scales with
        /// surface area — so this is what keeps high PS2 internal resolutions at
        /// 60fps instead of halving to 30 (see project_ps2_d3d11_present_scaling).
        /// </summary>
        private (int w, int h) CapPresentToDisplay(int w, int h)
        {
            if (w <= 0 || h <= 0) return (w, h);
            double scale = 1.0;
            var src = System.Windows.PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null) scale = src.CompositionTarget.TransformToDevice.M11;
            int monW = (int)System.Math.Ceiling(SystemParameters.PrimaryScreenWidth  * scale);
            int monH = (int)System.Math.Ceiling(SystemParameters.PrimaryScreenHeight * scale);
            if (monW <= 0 || monH <= 0) return (w, h);
            // Clamp BOTH axes to the monitor, preserving aspect ratio — bounded by
            // whichever axis is tighter. Capping only the longest side oversizes a
            // 4:3 frame on a wide monitor: the game is pillarboxed, so its visible
            // size is limited by the shorter (height) axis, and surface rows beyond
            // that are copied every frame for pixels that can never be shown.
            double s = System.Math.Min(1.0, System.Math.Min((double)monW / w, (double)monH / h));
            if (s >= 1.0) return (w, h);
            return (System.Math.Max(1, (int)System.Math.Round(w * s)),
                    System.Math.Max(1, (int)System.Math.Round(h * s)));
        }

        // ── Vulkan HW rendering ─────────────────────────────────────────────────
        private VulkanContext? _vulkanContext;
        private bool _isVulkanHwRender = false;
        private IntPtr _vulkanNegotiationPtr = IntPtr.Zero;
        private IntPtr _vulkanOverlayHwnd = IntPtr.Zero; // top-level popup window for Vulkan swapchain
        private volatile bool _vulkanPresenting;         // true after first PresentFrame succeeds
        private Window? _vulkanHudWindow;                // transparent popup for HUD above Vulkan/GL overlay
        private Grid? _vulkanHudGrid;

        // GL overlay: WS_POPUP window for direct glBlitFramebuffer + SwapBuffers presentation
        private IntPtr _glOverlayHwnd = IntPtr.Zero;
        private IntPtr _glOverlayDC   = IntPtr.Zero;
        private int _glOverlayWidth, _glOverlayHeight;
        private int _glPixelFormatIndex;  // stored from offscreen DC for overlay reuse
        private int _glOverlayTraceCount;  // separate counter for blit trace (not reset by FPS display)

        private IntPtr _glHwnd     = IntPtr.Zero;
        private bool   _glHwndOwned = false;  // true when we own the GL window (must DestroyWindow on close)
        private static IntPtr HWND_MESSAGE = new IntPtr(-3);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // Field-pinned WndProc delegate — prevents GC collecting the stub while the
        // window class is registered (window class lifetime = process lifetime).
        private WndProcDelegate? _offscreenWndProc;

        // Overlay subclass — forwards key messages to the WPF window so F9/F5/F12/Escape work
        private WndProcDelegate? _overlaySubclassProc;
        private IntPtr _overlayOldWndProc;
        private IntPtr _wpfHwnd;

        private void SubclassOverlay(IntPtr overlayHwnd)
        {
            if (_wpfHwnd == IntPtr.Zero)
                _wpfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            _overlaySubclassProc = OverlayWndProc;
            _overlayOldWndProc = GetWindowLongPtr(overlayHwnd, -4 /* GWL_WNDPROC */);
            SetWindowLongPtr(overlayHwnd, -4, Marshal.GetFunctionPointerForDelegate(_overlaySubclassProc));
        }

        private IntPtr OverlayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            const uint WM_KEYDOWN    = 0x0100;
            const uint WM_KEYUP      = 0x0101;
            const uint WM_SYSKEYDOWN = 0x0104;
            const uint WM_SYSKEYUP   = 0x0105;
            const uint WM_RBUTTONDOWN = 0x0204;

            if (msg == WM_KEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP)
            {
                // Forward key messages to the WPF window
                PostMessage(_wpfHwnd, msg, wParam, lParam);
            }
            else if (msg == WM_RBUTTONDOWN && _isPaused)
            {
                // Right-click on the overlay-hwnd present surface (Vulkan/GL consoles)
                // doesn't reach WPF — the child window swallows it. Forward to the UI
                // thread so paused right-clicks cycle the pause effect just like they
                // do on software-rendered consoles.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { CyclePauseEffect(); } catch { }
                }));
            }

            return CallWindowProc(_overlayOldWndProc, hWnd, msg, wParam, lParam);
        }

        // PeekMessage / DispatchMessage — used to pump NVIDIA driver sync messages
        // on the emu thread so it doesn't __fastfail waiting for a message pump.
        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint   message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint   time;
            public int    pt_x, pt_y;
        }
        private const uint PM_REMOVE = 0x0001;
        [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
        [DllImport("user32.dll")] private static extern bool DispatchMessage(ref MSG lpmsg);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int X, int Y);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint      cbSize;
            public uint      style;
            public IntPtr    lpfnWndProc;   // function pointer — passed as IntPtr
            public int       cbClsExtra;
            public int       cbWndExtra;
            public IntPtr    hInstance;
            public IntPtr    hIcon;
            public IntPtr    hCursor;
            public IntPtr    hbrBackground;
            public string?   lpszMenuName;
            public string?   lpszClassName;
            public IntPtr    hIconSm;
        }

        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

        private delegate void glGenFramebuffersDelegate(int n, uint[] ids);
        private delegate void glBindFramebufferDelegate(uint target, uint framebuffer);
        private delegate void glFramebufferTexture2DDelegate(uint target, uint attachment, uint textarget, uint texture, int level);
        private delegate void glGenRenderbuffersDelegate(int n, uint[] ids);
        private delegate void glBindRenderbufferDelegate(uint target, uint renderbuffer);
        private delegate void glRenderbufferStorageDelegate(uint target, uint internalformat, int width, int height);
        private delegate void glFramebufferRenderbufferDelegate(uint target, uint attachment, uint renderbuffertarget, uint renderbuffer);
        private delegate uint glCheckFramebufferStatusDelegate(uint target);
        private delegate void glGenTexturesDelegate(int n, uint[] textures);
        private delegate void glBindTextureDelegate(uint target, uint texture);
        private delegate void glTexImage2DDelegate(uint target, int level, int internalformat, int width, int height, int border, uint format, uint type, IntPtr data);
        private delegate void glTexParameteriDelegate(uint target, uint pname, int param);
        private delegate void glDeleteFramebuffersDelegate(int n, uint[] framebuffers);
        private delegate void glDeleteRenderbuffersDelegate(int n, uint[] renderbuffers);
        private delegate void glDeleteTexturesDelegate(int n, uint[] textures);
        private delegate void glBlitFramebufferDelegate(int srcX0, int srcY0, int srcX1, int srcY1,
            int dstX0, int dstY0, int dstX1, int dstY1, uint mask, uint filter);
        private delegate void   glGenBuffersDelegate(int n, uint[] buffers);
        private delegate void   glBindBufferDelegate(uint target, uint buffer);
        private delegate void   glBufferDataDelegate(uint target, IntPtr size, IntPtr data, uint usage);
        private delegate IntPtr glMapBufferDelegate(uint target, uint access);
        private delegate bool   glUnmapBufferDelegate(uint target);
        private delegate void   glDeleteBuffersDelegate(int n, uint[] buffers);

        private glGenFramebuffersDelegate?         _glGenFramebuffers;
        private glBindFramebufferDelegate?         _glBindFramebuffer;
        private glFramebufferTexture2DDelegate?    _glFramebufferTexture2D;
        private glGenRenderbuffersDelegate?        _glGenRenderbuffers;
        private glBindRenderbufferDelegate?        _glBindRenderbuffer;
        private glRenderbufferStorageDelegate?     _glRenderbufferStorage;
        private glFramebufferRenderbufferDelegate? _glFramebufferRenderbuffer;
        private glCheckFramebufferStatusDelegate?  _glCheckFramebufferStatus;
        private glGenTexturesDelegate?             _glGenTextures;
        private glBindTextureDelegate?             _glBindTexture;
        private glTexImage2DDelegate?              _glTexImage2D;
        private glTexParameteriDelegate?           _glTexParameteri;
        private glDeleteFramebuffersDelegate?      _glDeleteFramebuffers;
        private glDeleteRenderbuffersDelegate?     _glDeleteRenderbuffers;
        private glDeleteTexturesDelegate?          _glDeleteTextures;
        private glBlitFramebufferDelegate?         _glBlitFramebuffer;
        private glGenBuffersDelegate?              _glGenBuffers;
        private glBindBufferDelegate?              _glBindBuffer;
        private glBufferDataDelegate?              _glBufferData;
        private glMapBufferDelegate?               _glMapBuffer;
        private glUnmapBufferDelegate?             _glUnmapBuffer;
        private glDeleteBuffersDelegate?           _glDeleteBuffers;

        // PBO async readback (ping-pong): glReadPixels writes into writeIdx PBO asynchronously;
        // next frame we map readIdx PBO (already in system RAM) for zero-stall CPU access.
        private readonly uint[] _pboIds    = new uint[2];
        private int             _pboReadIdx = 0;
        private bool            _pboReady   = false;   // true after at least one async kick


        // =========================================================================
        // Constructor
        // =========================================================================
        public EmulatorWindow(Game game, LibretroCore core, string? pendingLoadStatePath = null)
        {
            try
            {
                // ----------------------------------------------------------
                // File log — works in Release builds (Trace is not stripped)
                // Written to %APPDATA%\Emutastic\Logs\emulator.log
                // ----------------------------------------------------------
                try
                {
                    string logDir = AppPaths.GetFolder("Logs");
                    string logPath = Path.Combine(logDir, "emulator.log");
                    // Rotate if over 5 MB — keeps one previous session as .old
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > 5 * 1024 * 1024)
                        File.Move(logPath, Path.Combine(logDir, "emulator.old.log"), overwrite: true);
                    var traceListener = new System.Diagnostics.TextWriterTraceListener(logPath, "FileLog")
                    {
                        TraceOutputOptions = System.Diagnostics.TraceOptions.DateTime
                    };
                    System.Diagnostics.Trace.Listeners.Add(traceListener);
                    System.Diagnostics.Trace.AutoFlush = true;
                }
                catch { /* non-fatal — logging may be unavailable */ }

                System.Diagnostics.Trace.WriteLine("EmulatorWindow constructor started");
                InitializeComponent();
                ApplyWindowsChrome();
                SourceInitialized += OnSourceInitialized;

                // Wire up mouse events for touch input (NDS) and DOS mouse capture (DOSBox Pure)
                GameScreen.MouseLeftButtonDown  += GameScreen_PointerDown;
                GameScreen.MouseLeftButtonUp    += GameScreen_PointerUp;
                GameScreen.MouseRightButtonDown += GameScreen_RightDown;
                GameScreen.MouseRightButtonUp   += GameScreen_RightUp;
                GameScreen.PreviewMouseDown     += GameScreen_PreviewMouseDown; // middle-click release
                GameScreen.MouseMove            += GameScreen_PointerMove;
                GameScreen.MouseLeave           += (_, _) => { _pointerPressed = false; _mouseLastPixelX = double.NaN; };
                Deactivated                     += (_, _) => ExitMouseCapture();

                _game = game;

                // Show NDS screen layout button in overlay
                if (game.Console == "NDS")
                {
                    OverlayScreenLayoutBtn.Visibility = Visibility.Visible;
                    UpdateScreenLayoutLabel();
                }

                // Show N64 controller pak swap button in overlay
                // (Memory Pak ↔ Rumble Pak — N64 hardware only allows one at a time)
                if (game.Console == "N64")
                    OverlayPakBtn.Visibility = Visibility.Visible;

                // Load Vectrex game overlay if available
                if (game.Console == "Vectrex")
                    InitVectrexOverlay(game);

                // Arcade / Neo Geo bezel frame (shows the cog toggle; auto-loads if enabled)
                InitBezelOverlay(game);

                _core = core;
                _consoleHandler = ConsoleHandlerFactory.Create(game.Console, game);
                Title = $"{game.Title} - {game.Console}";

                string sysDir     = AppPaths.GetFolder("System");
                string batteryDir = AppPaths.GetFolder("BatterySaves", game.Console);
                _consoleHandler.PrepareSaveDirectory(batteryDir);

                // Cloud sync: make sure this console's memory cards / save trees are
                // on disk BEFORE the core boots and reads them (PS2/PSP/GameCube/
                // Dreamcast/3DS — the .srm download later only covers frontend SRAM).
                // Usually a fast no-op: the startup/login background sync has already
                // pulled them; this just waits for that in-flight sync if needed.
                try
                {
                    var extraSync = Services.GitHubSyncService.Instance;
                    var extraCfg  = App.Configuration?.GetCloudSyncConfiguration();
                    if (extraSync.IsAuthenticated && extraCfg is { Enabled: true })
                        extraSync.EnsureConsoleSavesReadyAsync(game.Console).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Services.CloudSyncLog.Write($"Pre-launch memcard sync failed: {ex.Message}");
                }

                // Per-game .srm file named after the ROM file stem (not the DB title),
                // matching how RetroArch and most frontends identify saves.
                string romStem = Path.GetFileNameWithoutExtension(game.RomPath);
                string srmStem = SanitizeFileName(romStem);
                // ROM-hack entries share the base ROM file (and thus its stem); disambiguate
                // their battery save by the entry's (patched) hash so a hack never shares the
                // base game's .srm. Cloud sync already keys battery saves by RomHash.
                if (game.HasPatch && !string.IsNullOrEmpty(game.RomHash))
                    srmStem += "." + game.RomHash.Substring(0, Math.Min(8, game.RomHash.Length));
                _srmPath = Path.Combine(batteryDir, srmStem + ".srm");

                _saveStatePath = AppPaths.GetFolder("Save States",
                    SanitizeFileName(game.Console), SanitizeFileName(game.Title));
                _pendingLoadStatePath = pendingLoadStatePath;

                string coreDllDir = Path.GetDirectoryName(core.CorePath) ?? sysDir;
                string resolvedSysDir = _consoleHandler.ResolveSystemDirectory(sysDir, coreDllDir);
                Directory.CreateDirectory(resolvedSysDir);
                _systemDirPtr  = Marshal.StringToHGlobalAnsi(resolvedSysDir);
                _saveDirPtr    = Marshal.StringToHGlobalAnsi(batteryDir);
                string contentDir = Path.GetDirectoryName(game.RomPath) ?? resolvedSysDir;
                _contentDirPtr = Marshal.StringToHGlobalAnsi(contentDir);

                SeedDefaultCoreOptions();

                // Clear any descriptors captured from a prior session — fresh start.
                _pendingMemoryRegions = null;

                _envCb        = OnEnvironment;
                _videoCb      = OnVideoRefresh;
                _audioCb      = OnAudioSample;
                _audioBatchCb = OnAudioSampleBatch;
                _inputPollCb  = OnInputPoll;
                _inputStateCb = OnInputState;
                _logCb        = OnRetroLog;

                _envCbHandle        = GCHandle.Alloc(_envCb,        GCHandleType.Normal);
                _videoCbHandle      = GCHandle.Alloc(_videoCb,      GCHandleType.Normal);
                _audioCbHandle      = GCHandle.Alloc(_audioCb,      GCHandleType.Normal);
                _audioBatchCbHandle = GCHandle.Alloc(_audioBatchCb, GCHandleType.Normal);
                _inputPollCbHandle  = GCHandle.Alloc(_inputPollCb,  GCHandleType.Normal);
                _inputStateCbHandle = GCHandle.Alloc(_inputStateCb, GCHandleType.Normal);
                _logCbHandle        = GCHandle.Alloc(_logCb,        GCHandleType.Normal);

                _db                = new DatabaseService();
                _configService     = App.Configuration ?? throw new InvalidOperationException("Configuration not initialized");
                for (uint i = 0; i < 4; i++)
                    _controllers[i] = new ControllerManager(_configService, null, game.Console, playerNumber: i);
                _controllerManager = _controllers[0];
                _controllerManager!.ButtonChanged += OnControllerButtonChanged;
                _rumbleStateDelegate = OnSetRumbleState; // must be assigned after _controllerManager exists; field keeps it GC-rooted

                LoadKeyboardMappings();
                _audioPlayer = new AudioPlayer(44100);

                Loaded += OnWindowLoaded;
                System.Diagnostics.Trace.WriteLine("EmulatorWindow constructor completed successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("EmulatorWindow constructor failed: " + ex);
                throw;
            }
        }

        // =========================================================================
        // Core option seeding
        // =========================================================================
        private void SeedDefaultCoreOptions()
        {
            _coreOptions.Clear();
            var defaults = _consoleHandler.GetDefaultCoreOptions();
            foreach (var kv in defaults) _coreOptions[kv.Key] = kv.Value;
            if (defaults.Count > 0)
                System.Diagnostics.Trace.WriteLine($"Seeded {defaults.Count} default core options for {_game.Console}");

            // NDS: default to touch mode (absolute pointer, no crosshair) instead of mouse mode
            if (_game.Console == "NDS")
            {
                _coreOptions.TryAdd("desmume_pointer_type", "touch");
                // Controller-only players (couch/TV) can't click the screen, and
                // games gate progression behind mandatory touches (e.g. RPG intro
                // sequences). The core's emulated pointer moves a crosshair with
                // the RIGHT ANALOG STICK and taps with R2 — coexists with mouse
                // clicks, which keep working. TryAdd → a user's saved Core Options
                // choice still wins.
                _coreOptions.TryAdd("desmume_pointer_device_r", "emulated");
            }

            // PS2: LRPS2 defaults pcsx2_bios to the first file its folder scan
            // returns (the oldest JP dump) and ignores region. Pre-seed a
            // region-appropriate, newest dump. At the defaults layer, so a
            // pcsx2_bios chosen in Core Options (applied below) still wins.
            if (_game.Console == "PS2")
            {
                string? ps2Bios = Services.ConsoleHandlers.Ps2Handler.ResolveRegionBios(_game.RomPath);
                if (ps2Bios != null)
                {
                    _coreOptions["pcsx2_bios"] = ps2Bios;
                    System.Diagnostics.Trace.WriteLine($"PS2 BIOS auto-selected: {ps2Bios}");
                }
            }

            // Apply legacy per-console overrides (e.g. N64 GFX plugin selection)
            var configSvc = _configService ?? App.Configuration;
            var prefs = configSvc?.GetCorePreferences();
            if (prefs?.CoreOptionOverrides.TryGetValue(_game.Console, out var overrides) == true)
            {
                foreach (var kv in overrides)
                {
                    _coreOptions[kv.Key] = kv.Value;
                    System.Diagnostics.Trace.WriteLine($"User override (legacy): {kv.Key} = {kv.Value}");
                }
            }

            // Apply user values saved via Core Options UI (highest priority)
            string coreName = Path.GetFileNameWithoutExtension(_core.CorePath);
            var userValues = App.CoreOptions.LoadValues(coreName);
            foreach (var kv in userValues)
            {
                // PS2 BIOS is region-determined per game (ResolveRegionBios, above).
                // A single saved pcsx2_bios can't be correct for games of every
                // region, so never let a stale saved value override the per-game
                // region pick — that pinned one BIOS (and one boot crash) for ALL
                // games regardless of region.
                if (_game.Console == "PS2" && kv.Key == "pcsx2_bios") continue;
                _coreOptions[kv.Key] = kv.Value;
                System.Diagnostics.Trace.WriteLine($"User value: {kv.Key} = {kv.Value}");
            }

            // N64: force ParaLLEl-RDP — other plugins (glide64, rice, angrylion) are broken/slow.
            // This overrides any legacy config that may still have a different plugin saved.
            if (_game.Console == "N64")
                _coreOptions["parallel-n64-gfxplugin"] = "parallel";
        }

        // =========================================================================
        // Window loaded / start
        // =========================================================================
        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Restore saved window size for this console
                RestoreWindowSize();

                // Restore saved shader preset for this game
                RestoreShaderPreset();

                // Load per-game turbo button assignments
                LoadTurboConfig();

                // Overlay: set core label and start hide timer
                OverlayCoreLabel.Text = System.IO.Path.GetFileNameWithoutExtension(_core.CorePath);

                // Hide the Cheats item entirely for cores that stub retro_cheat_set —
                // showing it would just frustrate users (e.g. PPSSPP uses CWCheat .ini files).
                if (Services.CheatSupport.Lookup(_core.CorePath).Level == Services.CheatSupportLevel.NotSupported)
                    OverlayCheatsBtn.Visibility = Visibility.Collapsed;
                _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                _overlayTimer.Tick += (_, _) =>
                {
                    // Don't auto-hide while any submenu the user might be reading is open.
                    // The cog/cheats/save menus stay open until the user clicks elsewhere;
                    // hiding the HUD out from under them was the main "disappears mid-use" bug.
                    if (OverlayMenu.Visibility == Visibility.Visible
                        || CheatsMenu.Visibility == Visibility.Visible
                        || SaveMenu.Visibility == Visibility.Visible
                        || VisualsPanel.Visibility == Visibility.Visible
                        || ShaderPanel.Visibility == Visibility.Visible)
                    {
                        _overlayTimer?.Stop();
                        _overlayTimer?.Start();
                        return;
                    }
                    HideOverlay();
                };

                // Poll mouse position every 100ms — MouseMove doesn't fire over HwndHost
                // (Win32 child windows swallow mouse messages before WPF sees them).
                _mousePoller = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _mousePoller.Tick += (_, _) =>
                {
                    // Catch & swallow: an exception here would otherwise take the timer
                    // down and leave the overlay permanently hidden.
                    try
                    {
                        var pos = Mouse.GetPosition(this);
                        if (pos != _lastMousePos) { _lastMousePos = pos; ShowOverlay(); }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine("Mouse poller tick: " + ex);
                    }
                };
                _mousePoller.Start();

                StatusText.Text = "Starting emulator...";
                _emuThread = new System.Threading.Thread(StartEmulator, 32 * 1024 * 1024)
                {
                    IsBackground = true,
                    Name         = "EmuThread",
                    // AboveNormal reduces Windows scheduling jitter that causes mid-frame preemption.
                    // Avoids Highest/TimeCritical which can starve system threads.
                    Priority     = System.Threading.ThreadPriority.AboveNormal,
                };
                _emuThread.SetApartmentState(System.Threading.ApartmentState.MTA);
                _emuThread.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Window load failed: " + ex);
                MessageBox.Show("Window load failed:\n\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RestoreWindowSize()
        {
            try
            {
                double w = _configService.GetValue($"emuWinWidth_{_game.Id}",  0.0);
                double h = _configService.GetValue($"emuWinHeight_{_game.Id}", 0.0);
                if (w >= 320 && h >= 240)
                {
                    Width  = w;
                    Height = h;
                    // Mark as already sized so AutoSizeWindowToGameAr doesn't
                    // overwrite the user's saved dimensions on the first frame.
                    _windowSized = true;
                }
            }
            catch { }
        }

        private void SaveWindowSize()
        {
            try
            {
                System.Diagnostics.Trace.WriteLine($"SaveWindowSize: Console={_game.Console}, WindowState={WindowState}, W={Width}, H={Height}");
                // Save regardless of WindowState — for borderless windows the user may have
                // resized without ever maximizing.  RestoreBounds gives the Normal-state rect
                // when maximized; otherwise use current Width/Height.
                double w, h;
                if (WindowState == WindowState.Normal)
                {
                    w = Width;
                    h = Height;
                }
                else
                {
                    w = RestoreBounds.Width;
                    h = RestoreBounds.Height;
                }

                if (w >= 320 && h >= 240)
                {
                    _configService.SetValue($"emuWinWidth_{_game.Id}",  w);
                    _configService.SetValue($"emuWinHeight_{_game.Id}", h);
                    _ = _configService.SaveAsync();
                    System.Diagnostics.Trace.WriteLine($"SaveWindowSize: saved {w}x{h} for game {_game.Id} ({_game.Title})");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"SaveWindowSize FAILED: {ex.Message}");
            }
        }

        private void StartEmulator()
        {
            // Raise emu thread priority so the OS doesn't preempt it mid-frame.
            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal;

            _vulkanTeardownComplete = false;
            InstallCrashDiagnostics();

            try
            {
                System.Diagnostics.Trace.WriteLine($"=== Starting {_game.Title} ({_game.Console}) ===");
                System.Diagnostics.Trace.WriteLine($"ROM: {_game.RomPath}");

                _core.SetCallbacks(_envCb!, _videoCb!, _audioCb!, _audioBatchCb!, _inputPollCb!, _inputStateCb!);

                Dispatcher.Invoke(() => StatusText.Text = "Initializing core...");
                _core.Init();
                System.Diagnostics.Trace.WriteLine($"Core init OK — need_fullpath={_core.SystemInfo.need_fullpath}");

                Dispatcher.Invoke(() => StatusText.Text = "Loading game...");

                // Launch-time backstop: if the DB stored a .zip/.7z RomPath (e.g. from
                // pre-fix imports done while sitting on a console-specific nav), extract
                // the inner ROM once and update the DB row so subsequent launches are
                // fast. Skips Arcade/NeoGeo whose cores read the archive natively.
                //
                // Originally gated on need_fullpath=true, but FDS via Nestopia surfaced
                // a need_fullpath=false counterexample: the core reads the zip bytes via
                // the data buffer and rejects them because there's no FDS header. Some
                // need_fullpath=false cores (Snes9x, FCEUmm) tolerate raw zip bytes
                // internally; extraction is harmless for those (they get inner-ROM bytes
                // either way) and makes the DB row correct.
                string romToLoad = _game.RomPath;
                string romExt = System.IO.Path.GetExtension(romToLoad);
                if (Services.ZipRomExtractor.IsArchiveExtension(romExt)
                    && Services.ZipRomExtractor.ConsoleNeedsExtraction(_game.Console))
                {
                    System.Diagnostics.Trace.WriteLine($"Launch backstop: extracting {romToLoad} for {_game.Console}");
                    string? extracted = Services.ZipRomExtractor.ExtractSync(romToLoad, _game.Console);
                    if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted))
                    {
                        romToLoad = extracted;
                        _game.RomPath = extracted;
                        try { _db?.UpdateRomPath(_game.Id, extracted); }
                        catch (Exception ex)
                        { System.Diagnostics.Trace.WriteLine($"UpdateRomPath failed: {ex.Message}"); }
                    }
                    else
                    {
                        System.Diagnostics.Trace.WriteLine("Launch backstop: extraction failed; passing original path to core");
                    }
                }

                bool loaded = _core.LoadGame(romToLoad, _game.PatchPath);
                System.Diagnostics.Trace.WriteLine($"LoadGame: {loaded}");
                // Note: we deliberately do NOT call eject(true)→set_image_index(0)→eject(false)
                // here. RetroArch doesn't either (`disk_control_interface.c` only invokes
                // `set_initial_image` for resume-on-disc-N when a stored index file exists).
                // Cores boot with disc 0 already inserted by retro_load_game. The earlier
                // attempt was an FDS workaround, but FDS via Nestopia doesn't register the
                // env interface anyway — its boot flow uses the `nestopia_fds_auto_insert`
                // core option instead.

                if (!loaded)
                {
                    // Do NOT call Deinit() or Dispose() here — cores that fail
                    // retro_load_game (e.g. geolith without neogeo.zip) leave
                    // internal state partially initialized, and any native cleanup
                    // triggers an access violation in ntdll.  Let the DLL leak;
                    // the close path checks _loadFailed and skips disposal.
                    _loadFailed = true;

                    string loadErrDetail = string.IsNullOrEmpty(_core.LastError)
                        ? "Check debug output for details."
                        : _core.LastError;
                    Dispatcher.Invoke(() => MessageBox.Show($"Failed to load {_game.Title}\n\n{loadErrDetail}",
                        "Load Error", MessageBoxButton.OK, MessageBoxImage.Error));
                    return;
                }

                // Persist the core options schema now that CoreName is available
                // (SET_VARIABLES fires during retro_set_environment before SystemInfo is populated).
                if (_coreOptionSchema.Count > 0)
                {
                    string cn = Path.GetFileNameWithoutExtension(_core.CorePath);
                    App.CoreOptions.SaveSchema(cn, new CoreOptionsSchema
                    {
                        DisplayName = _core.CoreName,
                        ConsoleName = _consoleHandler.ConsoleName,
                        Options     = new List<CoreOptionEntry>(_coreOptionSchema)
                    });
                }

                // Game loaded — record play count and last played on both the DB and the
                // in-memory Game object so the detail card shows fresh stats after closing.
                _db?.UpdatePlayCount(_game.Id);
                _game.PlayCount++;
                _game.LastPlayed = DateTime.Now;
                _sessionStartUtc = DateTime.UtcNow;

                // Call retro_set_controller_port_device for all active ports.
                // Handler decides how many ports to configure (GameCube needs all 4).
                _consoleHandler.ConfigureControllerPorts(_core);

                // Seed display AR from the core's geometry so overlay cores (Vulkan/GL)
                // that bypass the WriteableBitmap path still get AR correction.
                var avGeom = _core.AvInfo.geometry;
                if (avGeom.base_width > 0 && avGeom.base_height > 0)
                    UpdateDisplayAspectRatio(avGeom.base_width, avGeom.base_height, avGeom.aspect_ratio);

                // Cloud sync: pull remote battery save if newer before loading into core.
                if (!string.IsNullOrEmpty(_game.RomHash))
                {
                    try
                    {
                        var syncSvc = Services.GitHubSyncService.Instance;
                        var syncCfg = App.Configuration?.GetCloudSyncConfiguration();
                        if (syncSvc.IsAuthenticated && syncCfg is { Enabled: true })
                        {
                            var gameLock = syncSvc.GetGameLock(_game.RomHash);
                            if (gameLock.Wait(5000))
                            {
                                try
                                {
                                    bool encrypted = syncCfg.EncryptionEnabled
                                        && !string.IsNullOrEmpty(syncCfg.PassphraseProtected);
                                    string repoPath = $"BatterySaves/{_game.Console}/{_game.RomHash}.srm"
                                        + (encrypted ? ".enc" : "");

                                    // Check manifest: only download if remote is newer than local
                                    DateTime remoteMtime = default;
                                    bool hasRemoteMtime = syncSvc.ManifestCache.Files.TryGetValue(repoPath, out var mEntry)
                                        && DateTime.TryParse(mEntry.LastModifiedUtc, null,
                                            System.Globalization.DateTimeStyles.RoundtripKind, out remoteMtime);
                                    // Newest-wins, no clobber: pull only when we have no
                                    // local save yet, or the remote is KNOWN to be strictly
                                    // newer than ours. Never overwrite a local save that's
                                    // newer or equal, and never overwrite an existing local
                                    // save when the remote mtime is unknown (a stale or
                                    // not-yet-loaded manifest must not clobber a fresh local
                                    // save). Pulling the other machine's newer save is the
                                    // full sync's job (startup + periodic).
                                    bool shouldDownload = !File.Exists(_srmPath)
                                        || (hasRemoteMtime
                                            && remoteMtime > File.GetLastWriteTimeUtc(_srmPath));

                                    byte[]? remote = shouldDownload
                                        ? syncSvc.DownloadFileAsync(repoPath).GetAwaiter().GetResult()
                                        : null;
                                    if (remote != null && remote.Length > 0)
                                    {
                                        if (encrypted)
                                        {
                                            byte[] key = Services.GitHubSyncService.DeriveKey(
                                                Services.GitHubSyncService.UnprotectString(syncCfg.PassphraseProtected),
                                                syncSvc.Username ?? "");
                                            remote = Services.GitHubSyncService.Decrypt(remote, key);
                                        }
                                        Directory.CreateDirectory(Path.GetDirectoryName(_srmPath)!);
                                        File.WriteAllBytes(_srmPath, remote);
                                        // Same mtime-echo fix as FullSync's download phase: without
                                        // this the next full sync re-uploads a save we only ever
                                        // downloaded ("90 up with no changes").
                                        if (hasRemoteMtime) File.SetLastWriteTimeUtc(_srmPath, remoteMtime);
                                        Services.CloudSyncLog.Write($"Downloaded remote save: {repoPath}");
                                    }
                                }
                                finally { gameLock.Release(); }
                            }
                            else
                            {
                                Services.CloudSyncLog.Write("Lock timeout — skipping download, using local save");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Services.CloudSyncLog.Write($"Pre-launch download failed: {ex.Message}");
                    }
                }

                // Load battery save (SRAM / memory card) into the core's RAM buffer.
                // Must happen after LoadGame so the core's SRAM pointer is valid.
                if (File.Exists(_srmPath))
                {
                    try
                    {
                        byte[] sram = File.ReadAllBytes(_srmPath);
                        bool ok = _core.LoadSaveRam(sram);
                        System.Diagnostics.Trace.WriteLine($"SRAM load: {Path.GetFileName(_srmPath)} ({sram.Length} bytes) → {(ok ? "OK" : "no SRAM in core")}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"SRAM load failed: {ex.Message}");
                    }
                }

                // ── RetroAchievements ─────────────────────────────────────────────
                InitRetroAchievements();
                ApplyHardcoreHudVisibility();

                double fps = _core.AvInfo.timing.fps;
                if (double.IsNaN(fps) || double.IsInfinity(fps) || fps <= 0 || fps > 1000) fps = 60;
                // Handler can force a hardware-native rate regardless of what the core reports.
                // Dreamcast: Flycast reports game fps (30 for some titles) but the DC hardware
                // is always 60Hz — using 30 halves the VBL rate and games run at half speed.
                double hwFps = _consoleHandler.HardwareTargetFps;
                if (hwFps > 0) fps = hwFps;

                // Reinitialise audio with the sample rate the core actually reported.
                // Dolphin uses ~32029 Hz for GameCube DMA audio, not the 44100 Hz
                // default the AudioPlayer was constructed with.
                double reportedRate = _core.AvInfo.timing.sample_rate;
                int sampleRate = (reportedRate > 8000 && reportedRate <= 192000)
                    ? (int)reportedRate : 44100;
                System.Diagnostics.Trace.WriteLine($"Audio sample rate from core: {reportedRate} → using {sampleRate}");
                _audioPlayer?.Dispose();
                _audioPlayer = new AudioPlayer(sampleRate);
                if (_isVulkanHwRender)
                    _audioPlayer.DesiredLatencyMs = 200;

                Dispatcher.Invoke(() =>
                {
                    _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
                    _timer.Tick += (s, e) =>
                    {
                        int actual   = System.Threading.Interlocked.Exchange(ref _frameCount, 0);
                        int emuRate  = System.Threading.Interlocked.Exchange(ref _emuFrameCount, 0);
                        long ticks   = System.Threading.Interlocked.Exchange(ref _coreRunTotalTicks, 0);
                        int  samples = System.Threading.Interlocked.Exchange(ref _coreRunSampleCount, 0);
                        double avgMs = samples > 0
                            ? (double)ticks / samples / System.Diagnostics.Stopwatch.Frequency * 1000.0
                            : 0;

                        // Track how long FPS has been 0 — covers the gap where retro_run
                        // is mid-call (e.g. inside a synchronous glCompileShader) and
                        // OnRetroLog isn't firing, so the shader-compile banner can't
                        // refresh. If we've been at 0 fps for ≥2 ticks AND no transient
                        // message is active, fall back to a generic stall indicator.
                        if (actual == 0) _zeroFpsSeconds++; else _zeroFpsSeconds = 0;

                        // Controller hot-plug during gameplay: diff the cached SDL
                        // name list (instant, no pump) once per tick and surface
                        // connect/disconnect with the controller's actual name in
                        // the transient slot — mirrors the library's status banner.
                        // First tick primes silently (those controllers were
                        // present before the game started — not events).
                        try
                        {
                            var ctrls = Services.ControllerManager.GetConnectedControllers();
                            if (_ctrlStatusLast != null)
                            {
                                foreach (var n in ctrls.Except(_ctrlStatusLast, StringComparer.Ordinal))
                                {
                                    _transientMsg    = $"Controller connected: {n}";
                                    _transientExpiry = DateTime.Now.AddSeconds(5);
                                }
                                foreach (var n in _ctrlStatusLast.Except(ctrls, StringComparer.Ordinal))
                                {
                                    _transientMsg    = $"Controller disconnected: {n}";
                                    _transientExpiry = DateTime.Now.AddSeconds(5);
                                }
                            }
                            _ctrlStatusLast = ctrls;
                        }
                        catch { /* status only — never disturb the FPS tick */ }

                        // Benchmark log: one line per second to Logs/perf.log so
                        // the tweak→measure loop can read steady-state fps back
                        // without the screen. Cheap; runs for every console.
                        Services.PerfLog.Tick(_game?.Console ?? "?", actual, emuRate, fps, avgMs);

                        // Headline number is display cadence (frames actually
                        // shown); "target" is always the goal rate. When the core
                        // steps faster than the screen can present, append "emu N"
                        // — that gap means presentation (GPU / UI thread), not the
                        // core, is the bottleneck.
                        string fpsStr = (emuRate - actual > 2 && actual > 0)
                            ? $"{actual} fps  (target {fps:F0}, emu {emuRate})  core.Run avg {avgMs:F1}ms"
                            : $"{actual} fps  (target {fps:F0})  core.Run avg {avgMs:F1}ms";
                        string msg    = _transientMsg;
                        bool   transientLive = msg.Length > 0 && DateTime.Now < _transientExpiry;
                        if (transientLive)
                            StatusText.Text = $"{fpsStr}    ✓ {msg}";
                        else if (_zeroFpsSeconds >= 2)
                            StatusText.Text = $"{fpsStr}    ⏳ Working… ({_zeroFpsSeconds}s with no frame)";
                        else
                            StatusText.Text = fpsStr;
                    };
                    _timer.Start();
                    StatusText.Text = "Running...";
                });

                // Benchmark session header: game + the core options that move
                // framerate, so a perf.log read needs no extra context.
                try
                {
                    string optSnap = string.Join(" ", _coreOptions
                        .Where(kv => kv.Key.Contains("resolution") || kv.Key.Contains("shader")
                                  || kv.Key.Contains("jit") || kv.Key.Contains("texture_filter")
                                  || kv.Key.Contains("cpu_clock") || kv.Key.Contains("graphics_api")
                                  || kv.Key.Contains("accurate") || kv.Key.Contains("cores")
                                  || kv.Key.Contains("cpu_mode") || kv.Key.Contains("upscale")
                                  || kv.Key.Contains("renderer"))
                        .OrderBy(kv => kv.Key)
                        .Select(kv => $"{kv.Key}={kv.Value}"));
                    Services.PerfLog.SessionStart(
                        $"{_game?.Console} \"{_game?.Title}\" core={_core?.CoreName} [{optSnap}]");
                }
                catch { /* logging only */ }

                _audioPlayer?.Start();

                // Per libretro spec: call context_reset AFTER retro_load_game returns,
                // not inside the SET_HW_RENDER callback (which fires mid-LoadGame).
                // Calling it too early puts mupen64plus / Dolphin in an invalid state.
                if (_hwRenderActive && _hwContextReset != null)
                {
                    if (_isVulkanHwRender)
                    {
                        // Initialize VulkanContext now — by this point the core has sent
                        // both SET_HW_RENDER and SET_HW_RENDER_CONTEXT_NEGOTIATION_INTERFACE.
                        if (_vulkanContext == null)
                        {
                            // Create a top-level popup window for Vulkan swapchain presentation.
                            // We can't use HwndHost (WS_CHILD) because EmulatorWindow has
                            // AllowsTransparency="True" — layered windows don't composite children.
                            // A top-level WS_POPUP window owned by our HWND avoids this limitation.
                            IntPtr vulkanHwnd = IntPtr.Zero;
                            Dispatcher.Invoke(() =>
                            {
                                GameScreen.Visibility = System.Windows.Visibility.Collapsed;

                                var helper = new WindowInteropHelper(this);
                                IntPtr ownerHwnd = helper.Handle;

                                // Get viewport bounds in screen coordinates
                                var viewportPoint = GameViewport.PointToScreen(new System.Windows.Point(0, 0));
                                int vx = (int)viewportPoint.X;
                                int vy = (int)viewportPoint.Y;
                                int vw = (int)GameViewport.ActualWidth;
                                int vh = (int)GameViewport.ActualHeight;
                                if (vw < 1) vw = 640;
                                if (vh < 1) vh = 480;

                                const uint WS_POPUP = 0x80000000;
                                const uint WS_VISIBLE = 0x10000000;
                                const uint WS_CLIPSIBLINGS = 0x04000000;
                                const uint WS_EX_NOACTIVATE = 0x08000000;
                                _vulkanOverlayHwnd = CreateWindowEx(
                                    WS_EX_NOACTIVATE, "Static", "",
                                    WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS,
                                    vx, vy, vw, vh,
                                    ownerHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                                vulkanHwnd = _vulkanOverlayHwnd;
                                System.Diagnostics.Trace.WriteLine($"[Vulkan] Overlay HWND=0x{vulkanHwnd:X} at ({vx},{vy}) {vw}x{vh}");

                                // Subclass overlay to forward key events to WPF window
                                SubclassOverlay(_vulkanOverlayHwnd);

                                // Hook move/resize/state events to keep overlay in sync
                                LocationChanged += VulkanOverlay_Reposition;
                                SizeChanged += VulkanOverlay_Reposition;
                                StateChanged += VulkanOverlay_StateChanged;
                            });

                            _vulkanContext = new VulkanContext();
                            if (!_vulkanContext.Initialize(_vulkanNegotiationPtr, vulkanHwnd))
                            {
                                System.Diagnostics.Trace.WriteLine("[Vulkan] Init failed at context_reset time");
                                _vulkanContext?.Dispose();
                                _vulkanContext = null;
                                _isVulkanHwRender = false;
                                _hwRenderActive = false;
                                Dispatcher.BeginInvoke(() => OverlayShaderBtn.Visibility = Visibility.Visible);
                                return;
                            }
                            System.Diagnostics.Trace.WriteLine($"[Vulkan] Context initialized at context_reset time (swapchain={_vulkanContext.HasSwapchain})");
                        }

                        _consoleHandler.OnBeforeContextReset();
                        System.Diagnostics.Trace.WriteLine("Calling context_reset (Vulkan, post-LoadGame)...");
                        _hwContextReset.Invoke();
                        _consoleHandler.OnAfterContextReset();
                        System.Diagnostics.Trace.WriteLine("context_reset done (Vulkan).");
                    }
                    else if (_isD3d11HwRender)
                    {
                        // D3D11 swapchain present: create a WS_POPUP overlay window
                        // (same airspace trick as Vulkan — AllowsTransparency blocks
                        // WS_CHILD compositing) and a DXGI swapchain on it. The core
                        // frame is blitted straight to the display-sized backbuffer and
                        // presented at vsync — no D3D9/D3DImage CPU copy. If swapchain
                        // creation fails we fall back to the in-tree D3DImage path
                        // (HasSwapchain stays false and OnVideoRefresh routes there).
                        if (_d3d11Context != null && !_d3d11Context.HasSwapchain)
                        {
                            IntPtr d3dHwnd = IntPtr.Zero;
                            int sw = 640, sh = 480;
                            Dispatcher.Invoke(() =>
                            {
                                GameScreen.Visibility = System.Windows.Visibility.Collapsed;
                                var helper = new WindowInteropHelper(this);
                                IntPtr ownerHwnd = helper.Handle;
                                var viewportPoint = GameViewport.PointToScreen(new System.Windows.Point(0, 0));
                                int vx = (int)viewportPoint.X;
                                int vy = (int)viewportPoint.Y;
                                int vw = Math.Max(1, (int)GameViewport.ActualWidth);
                                int vh = Math.Max(1, (int)GameViewport.ActualHeight);

                                const uint WS_POPUP = 0x80000000;
                                const uint WS_VISIBLE = 0x10000000;
                                const uint WS_CLIPSIBLINGS = 0x04000000;
                                const uint WS_EX_NOACTIVATE = 0x08000000;
                                _vulkanOverlayHwnd = CreateWindowEx(
                                    WS_EX_NOACTIVATE, "Static", "",
                                    WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS,
                                    vx, vy, vw, vh,
                                    ownerHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                                d3dHwnd = _vulkanOverlayHwnd;
                                sw = vw; sh = vh;
                                System.Diagnostics.Trace.WriteLine($"[D3D11] Overlay HWND=0x{d3dHwnd:X} at ({vx},{vy}) {vw}x{vh}");

                                SubclassOverlay(_vulkanOverlayHwnd);
                                LocationChanged += VulkanOverlay_Reposition;
                                SizeChanged += VulkanOverlay_Reposition;
                                StateChanged += VulkanOverlay_StateChanged;
                            });

                            if (!_d3d11Context.CreateSwapchain(d3dHwnd, sw, sh))
                            {
                                // Fall back to the D3DImage path: tear the overlay back
                                // down and re-show GameScreen so the in-tree present works.
                                System.Diagnostics.Trace.WriteLine("[D3D11] swapchain failed — reverting to D3DImage path");
                                Dispatcher.Invoke(() =>
                                {
                                    LocationChanged -= VulkanOverlay_Reposition;
                                    SizeChanged -= VulkanOverlay_Reposition;
                                    StateChanged -= VulkanOverlay_StateChanged;
                                    if (_vulkanOverlayHwnd != IntPtr.Zero) { DestroyWindow(_vulkanOverlayHwnd); _vulkanOverlayHwnd = IntPtr.Zero; }
                                    GameScreen.Visibility = System.Windows.Visibility.Visible;
                                });
                            }
                            else
                            {
                                Dispatcher.BeginInvoke(() => RepositionOverlayWindow());
                            }
                        }

                        _consoleHandler.OnBeforeContextReset();
                        System.Diagnostics.Trace.WriteLine("Calling context_reset (D3D11, post-LoadGame)...");
                        _hwContextReset.Invoke();
                        _consoleHandler.OnAfterContextReset();
                        System.Diagnostics.Trace.WriteLine("context_reset done (D3D11).");
                    }
                    else
                    {
                        // GL path: re-acquire context, resize FBO, call context_reset.
                        wglMakeCurrent(_hdc, _hglrc);
                        System.Diagnostics.Trace.WriteLine($"Pre-context_reset: wglMakeCurrent _hglrc=0x{_hglrc:X}");

                        if (!_consoleHandler.AllowHwSharedContext && !_consoleHandler.UseEmbeddedWindow)
                        {
                            var geom = _core!.AvInfo.geometry;
                            uint needW = geom.max_width  > 0 ? geom.max_width  : geom.base_width;
                            uint needH = geom.max_height > 0 ? geom.max_height : geom.base_height;
                            if (needW > _fboWidth || needH > _fboHeight)
                            {
                                System.Diagnostics.Trace.WriteLine(
                                    $"Pre-context_reset FBO resize: {_fboWidth}x{_fboHeight} → {needW}x{needH}");
                                CreateFBO(needW, needH);
                            }

                            // The offscreen GL window is created at a fixed 640×480, which
                            // sets the default glViewport to (0,0,640,480). Cores that don't
                            // explicitly call glViewport render to (0,0,640,480) of whatever
                            // framebuffer is bound — content lands in the BL 640×480. Resizing
                            // the window updates the default viewport so upscaled renders fill
                            // the FBO. Applies to both the FBO overlay path (NVIDIA) and the
                            // UseDefaultFramebuffer readback path (AMD/Intel compat).
                            if (_glHwndOwned && _glHwnd != IntPtr.Zero)
                            {
                                const uint SWP_NOMOVE = 0x0002;
                                const uint SWP_NOZORDER = 0x0004;
                                const uint SWP_NOACTIVATE = 0x0010;
                                SetWindowPos(_glHwnd, IntPtr.Zero, 0, 0, (int)needW, (int)needH,
                                    SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                                System.Diagnostics.Trace.WriteLine(
                                    $"Pre-context_reset offscreen window resize → {needW}x{needH} (default glViewport)");
                            }
                        }

                        _consoleHandler.OnBeforeContextReset();
                        System.Diagnostics.Trace.WriteLine("Calling context_reset (post-LoadGame, per libretro spec)...");
                        _hwContextReset.Invoke();
                        _consoleHandler.OnAfterContextReset();
                        System.Diagnostics.Trace.WriteLine("context_reset done.");

                        var swapFn = GetGLProc<wglSwapIntervalEXTDelegate>("wglSwapIntervalEXT");
                        if (swapFn != null)
                        {
                            swapFn(0);
                            System.Diagnostics.Trace.WriteLine("vsync re-disabled after context_reset.");
                        }

                        // GL overlay: create WS_POPUP window for direct blit+swap presentation
                        if (_consoleHandler.UseGLOverlay && _glOverlayHwnd == IntPtr.Zero)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                GameScreen.Visibility = System.Windows.Visibility.Collapsed;
                                var helper = new WindowInteropHelper(this);
                                IntPtr ownerHwnd = helper.Handle;
                                var viewportPoint = GameViewport.PointToScreen(new System.Windows.Point(0, 0));
                                int vx = (int)viewportPoint.X;
                                int vy = (int)viewportPoint.Y;
                                int vw = (int)GameViewport.ActualWidth;
                                int vh = (int)GameViewport.ActualHeight;
                                if (vw < 1) vw = 640;
                                if (vh < 1) vh = 480;
                                const uint WS_POPUP = 0x80000000;
                                const uint WS_VISIBLE = 0x10000000;
                                const uint WS_CLIPSIBLINGS = 0x04000000;
                                const uint WS_EX_NOACTIVATE = 0x08000000;
                                _glOverlayHwnd = CreateWindowEx(
                                    WS_EX_NOACTIVATE, "Static", "",
                                    WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS,
                                    vx, vy, vw, vh,
                                    ownerHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                                _glOverlayWidth = vw;
                                _glOverlayHeight = vh;
                                System.Diagnostics.Trace.WriteLine($"[GL Overlay] HWND=0x{_glOverlayHwnd:X} at ({vx},{vy}) {vw}x{vh}");

                                // Subclass overlay to forward key events to WPF window
                                SubclassOverlay(_glOverlayHwnd);

                                // Hook move/resize/state events (same handler as Vulkan overlay)
                                LocationChanged += VulkanOverlay_Reposition;
                                SizeChanged += VulkanOverlay_Reposition;
                                StateChanged += VulkanOverlay_StateChanged;
                            });

                            if (_glOverlayHwnd != IntPtr.Zero)
                            {
                                // Set up pixel format on overlay DC — MUST use the exact same
                                // pixel format index as the offscreen DC so wglMakeCurrent can
                                // switch between them with the same HGLRC.
                                _glOverlayDC = GetDC(_glOverlayHwnd);
                                var pfd = new PIXELFORMATDESCRIPTOR
                                {
                                    nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), nVersion = 1,
                                    dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
                                    iPixelType = PFD_TYPE_RGBA, cColorBits = 32, cDepthBits = 24, cStencilBits = 8,
                                };
                                bool pfOk = SetPixelFormat(_glOverlayDC, _glPixelFormatIndex, ref pfd);
                                System.Diagnostics.Trace.WriteLine($"[GL Overlay] SetPixelFormat idx={_glPixelFormatIndex} ok={pfOk}");

                                // Verify wglMakeCurrent works on overlay DC
                                bool mcOk = wglMakeCurrent(_glOverlayDC, _hglrc);
                                System.Diagnostics.Trace.WriteLine($"[GL Overlay] wglMakeCurrent overlay={mcOk}");
                                if (mcOk && swapFn != null) swapFn(0);
                                wglMakeCurrent(_hdc, _hglrc);

                                if (!mcOk)
                                {
                                    // Pixel format mismatch — fall back to readback path
                                    System.Diagnostics.Trace.WriteLine("[GL Overlay] wglMakeCurrent failed — falling back to readback");
                                    ReleaseDC(_glOverlayHwnd, _glOverlayDC);
                                    _glOverlayDC = IntPtr.Zero;
                                }
                                else
                                {
                                    System.Diagnostics.Trace.WriteLine("[GL Overlay] DC and pixel format configured");
                                }
                            }
                        }

                        if (_consoleHandler.AllowHwSharedContext)
                        {
                            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                            System.Diagnostics.Trace.WriteLine("GL context released for EmuThread (shared context mode).");
                        }
                    }
                }

                // If launched via "Load" from the save states browser, queue the state to be applied
                // between retro_run calls (after the first frame). Calling retro_unserialize before
                // any retro_run has executed is not safe — the core may not be at a consistent
                // checkpoint yet (mupen64plus starts its own EmuThread during retro_load_game).
                if (_pendingLoadStatePath != null)
                {
                    // RA hardcore-compliance: refuse pending state loads (e.g. user
                    // double-clicked a state in the save browser). Mirrors the
                    // RequestLoad gate so the rule applies regardless of entry point.
                    if (IsHardcoreActive())
                    {
                        System.Diagnostics.Trace.WriteLine($"Pending load refused — hardcore mode active: {_pendingLoadStatePath}");
                        _transientMsg    = "Save state loading is disabled in hardcore mode";
                        _transientExpiry = DateTime.Now.AddSeconds(4);
                    }
                    else if (File.Exists(_pendingLoadStatePath))
                    {
                        try
                        {
                            _pendingLoadData         = File.ReadAllBytes(_pendingLoadStatePath);
                            _pendingLoadName         = Path.GetFileNameWithoutExtension(_pendingLoadStatePath);
                            _pendingLoadSavedCoreName = ReadSavedCoreName(_pendingLoadStatePath);
                            _loadStatePending        = true;
                            System.Diagnostics.Trace.WriteLine($"Queued pending state load: {_pendingLoadStatePath} (saved core='{_pendingLoadSavedCoreName}')");
                        }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Pending load read failed: {ex.Message}"); }
                    }
                    else
                    {
                        // Surface this loudly — if a save state's .state file went missing
                        // (Defender quarantine, accidental delete, etc.), the launch
                        // silently used to fall through to a fresh BIOS boot. Now it shows.
                        string missing = _pendingLoadStatePath;
                        System.Diagnostics.Trace.WriteLine($"Pending load skipped — file not found: {missing}");
                        _transientMsg    = $"Save state file missing: {Path.GetFileName(missing)}";
                        _transientExpiry = DateTime.Now.AddSeconds(6);
                    }
                    _pendingLoadStatePath = null;
                }

                if (!_isVulkanHwRender)
                {
                    IntPtr curCtx = wglGetCurrentContext();
                    System.Diagnostics.Trace.WriteLine($"Pre-loop GL: current=0x{curCtx:X} _hglrc=0x{_hglrc:X}");
                }

                // Apply any pre-saved cheats before the loop starts. Safe even when the
                // core stubs retro_cheat_set — the call is a silent no-op on stubs.
                if (!_cheatsApplied)
                {
                    _cheatsApplied = true;
                    try
                    {
                        // Cache system RAM for frontend-handled AR cheats. id=2
                        // is RETRO_MEMORY_SYSTEM_RAM. Cores that don't expose it
                        // (or expose it as 0 bytes) just skip frontend AR.
                        if (_core != null)
                        {
                            const uint RETRO_MEMORY_SYSTEM_RAM = 2;
                            var (ptr, size) = _core.GetMemoryRegion(RETRO_MEMORY_SYSTEM_RAM);
                            _systemRamPtr  = ptr;
                            _systemRamSize = size;
                        }

                        _cheats = _game != null ? Services.CheatService.Load(_game) : new();
                        if (_cheats.Count > 0 && _core != null)
                            ApplyAllCheats(_cheats);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"Cheats initial apply failed: {ex.Message}");
                    }
                }

                EmulationLoop(fps);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Emulator start failed: " + ex);
                Dispatcher.Invoke(() => MessageBox.Show("Emulator start failed:\n\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error));
            }

            // ── Emu-thread teardown ─────────────────────────────────────────────────
            // This MUST run on the same OS thread that called retro_run() because:
            //
            //   • mupen64plus uses libco coroutines (co_switch). retro_unload_game()
            //     calls co_switch to let the EmuThread coroutine finish, then switches
            //     back to "main_thread". If called from a *different* OS thread, the
            //     switch lands on a dead/wrong stack → crash in OPENGL32.dll.
            //
            //   • PPSSPP/Dolphin have a GPU thread that holds the OpenGL context.
            //     Calling wglMakeCurrent on a different thread steals the context from
            //     the GPU thread; the GPU thread's final "clear buffers" pass then
            //     crashes on a null context pointer in nvoglv64.dll.
            //
            // Both issues vanish when UnloadGame + context_destroy run here.
            if (_isClosing)
            {
                // Save SRAM while the game is still loaded, before UnloadGame.
                try
                {
                    byte[]? sram = _core?.GetSaveRam();
                    if (sram != null && sram.Length > 0 && !string.IsNullOrEmpty(_srmPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_srmPath)!);
                        File.WriteAllBytes(_srmPath, sram);
                        System.Diagnostics.Trace.WriteLine($"SRAM saved: {Path.GetFileName(_srmPath)} ({sram.Length} bytes)");
                    }
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"SRAM save: {ex.Message}"); }

                // ── Vulkan teardown ──────────────────────────────────────────
                // Correct order: context_destroy → unload_game → deinit
                // context_destroy tells ParaLLEl-RDP to release its Vulkan objects
                // BEFORE the core is deinitialized.  Without this, the Vulkan
                // driver's internal state is left dirty and the next session crashes.
                if (_hwRenderActive && _isVulkanHwRender)
                {
                    if (_hwContextDestroy != null)
                    {
                        try
                        {
                            System.Diagnostics.Trace.WriteLine("Calling context_destroy (Vulkan)...");
                            _hwContextDestroy.Invoke();
                            System.Diagnostics.Trace.WriteLine("context_destroy done (Vulkan).");
                        }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"context_destroy (Vulkan): {ex.Message}"); }
                    }

                    try { _core?.UnloadGame(); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"UnloadGame (Vulkan): {ex.Message}"); }

                    try { _core?.Deinit(); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"retro_deinit (Vulkan): {ex.Message}"); }

                    if (_vulkanContext != null)
                    {
                        _vulkanContext.Dispose();
                        _vulkanContext = null;
                    }
                    // Destroy overlay window on UI thread
                    try { Dispatcher.Invoke(() => DestroyVulkanOverlay()); }
                    catch { /* window may already be gone */ }
                    _isVulkanHwRender = false;
                    _vulkanTeardownComplete = true;
                    System.Diagnostics.Trace.WriteLine("Vulkan teardown complete.");
                }
                // ── GL teardown ─────────────────────────────────────────────
                else if (_hwRenderActive && _hdc != IntPtr.Zero)
                {
                    // AllowHwSharedContext=true (N64/glide64): we released our GL context
                    // to the core's EmuThread after context_reset. Re-acquire it NOW so
                    // glide64's cleanup (which runs on this thread via co_switch) can call GL.
                    //
                    // AllowHwSharedContext=false (PPSSPP/Dolphin): the core's GPU thread
                    // holds the GL context. Do NOT take it yet — let the GPU thread keep it
                    // so its final frame-flush completes without crashing.
                    if (_consoleHandler.AllowHwSharedContext)
                    {
                        IntPtr ctx = _secondaryCtx != IntPtr.Zero ? _secondaryCtx : _hglrc;
                        try { wglMakeCurrent(_hdc, ctx); }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"wglMakeCurrent (pre-unload): {ex.Message}"); }
                    }

                    string _teardownCoreName = _core != null ? System.IO.Path.GetFileName(_core.CorePath).ToLowerInvariant() : "";

                    // PCSX2 (LRPS2) OpenGL only: retro_unload_game deadlocks inside its own
                    // GS/MTGS shutdown (CPUThreadShutdown → GSshutdown; cpu_thread.join never
                    // returns), so UnloadGame below would hang and the emu thread is declared
                    // hung after 10s. context_destroy cleanly closes MTGS (freeze + CloseGS)
                    // while the GL context is current, so call it FIRST — then UnloadGame
                    // finds the GS already torn down and returns without deadlocking. (Reverse
                    // order is the hang.) D3D11 never reaches this branch (_hdc == 0), so this
                    // cannot affect the DirectX path. pcsx2 is added to _skipContextDestroy
                    // below so it isn't invoked a second time after unload.
                    if (_teardownCoreName.Contains("pcsx2") && _hwContextDestroy != null
                        && !_consoleHandler.AllowHwSharedContext)
                    {
                        IntPtr ctxP = _secondaryCtx != IntPtr.Zero ? _secondaryCtx : _hglrc;
                        try { wglMakeCurrent(_hdc, ctxP); } catch { }
                        System.Diagnostics.Trace.WriteLine("PCSX2: context_destroy BEFORE UnloadGame (context current)...");
                        try { _hwContextDestroy.Invoke(); }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"PCSX2 pre-unload context_destroy: {ex.Message}"); }
                        System.Diagnostics.Trace.WriteLine("PCSX2: pre-unload context_destroy done.");
                    }

                    // Stop emulation. Core threads run their GL cleanup while the context
                    // is still properly owned (either by us or by the core's GPU thread).
                    try { _core?.UnloadGame(); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"UnloadGame: {ex.Message}"); }

                    // For non-shared cores: all core threads have now stopped and released
                    // the GL context (threads release context on exit). Acquire it here.
                    if (!_consoleHandler.AllowHwSharedContext)
                    {
                        IntPtr ctx = _secondaryCtx != IntPtr.Zero ? _secondaryCtx : _hglrc;
                        try { wglMakeCurrent(_hdc, ctx); }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"wglMakeCurrent (post-unload): {ex.Message}"); }
                    }

                    // Let the core free its remaining GL objects.
                    //
                    // Some cores crash if context_destroy is called while their internal threads
                    // are still alive (even after retro_unload_game returns).  For these cores,
                    // skip context_destroy entirely — the quarantine delay before wglDeleteContext
                    // is sufficient to let driver-internal callbacks (texture frees, fence signals)
                    // drain safely.
                    //
                    // PPSSPP: crashes in ppsspp_libretro.dll FBO cleanup (READ 0x0) — GPU thread
                    //   already self-cleaned; context_destroy hits freed state.
                    // N64 (mupen64plus/parallel_n64): mupen64plus's internal EmuThread continues
                    //   running cleanup for hundreds of ms after retro_unload_game returns via
                    //   co_switch; context_destroy fires while that thread is still calling GL.
                    bool _skipContextDestroy = _teardownCoreName.Contains("ppsspp")
                                           || _teardownCoreName.Contains("mupen64")
                                           || _teardownCoreName.Contains("parallel_n64")
                                           || _teardownCoreName.Contains("azahar")
                                           || _teardownCoreName.Contains("pcsx2");   // already called pre-unload above
                    if (_hwContextDestroy != null && !_skipContextDestroy)
                    {
                        try { _hwContextDestroy.Invoke(); }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"context_destroy: {ex.Message}"); }
                    }
                    else if (_skipContextDestroy)
                    {
                        System.Diagnostics.Trace.WriteLine($"Skipping context_destroy for {_teardownCoreName} (crash avoidance).");
                    }

                    // Call retro_deinit NOW while GL context is still current on this thread.
                    // mupen64plus/glide64's retro_deinit triggers GL cleanup calls (texture
                    // deletes, context queries).  If we defer this to the background Task.Run
                    // thread, that thread has no GL context and wglMakeCurrent fails on thread-
                    // pool threads → AV in OPENGL32.dll's null dispatch table.
                    if (_teardownCoreName.Contains("mupen64") || _teardownCoreName.Contains("parallel_n64")
                        || _teardownCoreName.Contains("ppsspp") || _teardownCoreName.Contains("azahar"))
                    {
                        System.Diagnostics.Trace.WriteLine("Calling retro_deinit on emu thread (GL context active)...");
                        try { _core?.Deinit(); }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Emu-thread retro_deinit: {ex.Message}"); }
                        System.Diagnostics.Trace.WriteLine("Emu-thread retro_deinit complete.");
                    }

                    // Destroy GL overlay window on UI thread (if active)
                    if (_glOverlayHwnd != IntPtr.Zero)
                    {
                        try { Dispatcher.Invoke(() => DestroyVulkanOverlay()); }
                        catch { /* window may already be gone */ }
                    }

                    // Release the context so the cleanup task can quarantine-delete it.
                    try { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); }
                    catch { }

                    System.Diagnostics.Trace.WriteLine("Emu-thread GL teardown complete.");
                }
                else if (_isClosing)
                {
                    // Software-render path: just unload.
                    try { _core?.UnloadGame(); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"UnloadGame: {ex.Message}"); }
                }
            }

        }

        private bool _isClosing = false;
        private bool _closeStarted = false;

        // ── Fullscreen ──────────────────────────────────────────────────
        // Set by the caller (e.g. EmuTV) before Show so the game opens fullscreen.
        public bool StartInFullscreen { get; set; }
        private bool _isFullscreen;
        private WindowState _preFullscreenState;
        private double _preFsLeft, _preFsTop, _preFsWidth, _preFsHeight;
        private Thickness _preFsBorderThickness;
        private CornerRadius _preFsCornerRadius;
        private Thickness _preFsMargin;
        private System.Windows.Media.Effects.Effect? _preFsEffect;
        private ResizeMode _preFsResizeMode;
        private GridLength _preFsRow0Height;
        private GridLength _preFsRow2Height;
        private Visibility _preFsTitleBarVisibility;
        private System.Windows.WindowStyle _preFsWindowStyle;
        private System.Threading.Thread? _emuThread;

        private void SwapBuffers()
        {
            try
            {
                if (_hdc != IntPtr.Zero)
                    SwapBuffers(_hdc);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"SwapBuffers: {ex.Message}"); }
        }

        private void EmulationLoop(double targetFps)
        {
            System.Diagnostics.Trace.WriteLine("EmulationLoop targetFps=" + targetFps);

            // Stopwatch-primary timing: one retro_run per frame budget (1000/fps ms).
            // The Stopwatch is the real clock; audio is not the primary timing signal.
            //
            // Pre-fill: with a Stopwatch loop, produce == drain every frame so the
            // buffer hovers near zero and WaveOut starves.  We pre-fill to ~150ms so
            // WaveOut always has a comfortable cushion before the paced loop starts.
            //
            // Low-watermark catch-up: if the core produces slightly less audio than
            // WaveOut drains (N64 VI rate 60.098Hz ≠ our 60fps Stopwatch), the buffer
            // drifts down.  Running an extra retro_run when it dips below 80ms refills
            // it without audible stutter.
            // Vulkan readback cores need a bigger audio cushion — the synchronous
            // GPU→CPU copy adds per-frame latency that causes deeper audio dips.
            int prefillMs    = _isVulkanHwRender ? 250 : 150;
            int lowWatermark = _isVulkanHwRender ? 120 : 80;
            int backpressureMs = _isVulkanHwRender ? 500 : 300;
            // Seed the shared field — SET_SYSTEM_AV_INFO may update it mid-run (e.g. Flycast
            // switches from 60fps menus to 30fps gameplay for titles like Hydro Thunder).
            _targetFrameMs = 1000.0 / targetFps;

            // Force 1ms Windows timer resolution for the emulation thread so that
            // Thread.Sleep(1) in the frame-budget sleep actually sleeps ~1ms rather
            // than up to 15.6ms (the default timer granularity).
            timeBeginPeriod(1);
            try
            {
                // --- Pre-fill phase ---
                // WaveOut.Play() is intentionally deferred until here so the hardware
                // never starts reading from an empty buffer (initial underrun = crackling).
                System.Diagnostics.Trace.WriteLine($"Pre-filling audio buffer to {prefillMs}ms...");

                void DrainKeyboardQueue()
                {
                    var cb = _coreKeyboardEvent;
                    if (cb == null) return;
                    while (_kbEventQueue.TryDequeue(out var ev))
                    {
                        try { cb(ev.down, ev.key, 0, ev.mod); }
                        catch (Exception kbEx) { System.Diagnostics.Trace.WriteLine($"[KB] dispatch exception: {kbEx.GetType().Name}: {kbEx.Message}"); }
                    }
                }

                while (!_isClosing && (_audioPlayer?.GetBufferedMs() ?? prefillMs) < prefillMs)
                {
                    DrainKeyboardQueue();
                    _core?.Run();
                    // Don't drive the state-load warmup during prefill: prefill
                    // runs retro_run back-to-back at GPU speed (no 60fps audio
                    // gate), so 60 prefill iterations is ~100ms of wall clock
                    // — not enough time for HW renderers to drain their
                    // deferred-upload queues. Defer all unserialize attempts
                    // until the main run loop, where each retro_run is paced
                    // at the game's framerate and 60 iterations = 1 second of
                    // real warmup (Beetle PSX HW state-load workaround).
                    if (_glHwndOwned) { MSG m; while (PeekMessage(out m, IntPtr.Zero, 0, 0, PM_REMOVE)) DispatchMessage(ref m); }
                }
                _audioPlayer?.BeginPlayback();
                System.Diagnostics.Trace.WriteLine("Pre-fill done, playback started.");

                var frameTimer = System.Diagnostics.Stopwatch.StartNew();

                // HW cores (Dreamcast, GameCube, N64 etc.) use audio sync timing:
                // after retro_run, wait until the audio buffer drains back to prefillMs.
                // If retro_run advanced N game frames (e.g. 2 for a 30fps Dreamcast game),
                // it produced N frames of audio, so we wait N frame-times → correct speed
                // regardless of how many frames the core advances per call.
                // SW cores keep the Stopwatch path.
                bool isHwCore = _consoleHandler.PreferredHwContext != -1;

                while (_timer != null && _core != null && !_isClosing)
                {
                    // Pause: sleep 16ms and skip the frame when the user has paused.
                    if (_isPaused)
                    {
                        _raClient?.Idle();
                        System.Threading.Thread.Sleep(16);
                        frameTimer.Restart();
                        continue;
                    }

                    // Backpressure: if the core is running too fast, spin briefly.
                    // SpinWait is microsecond-accurate and immune to Windows timer granularity.
                    int waitAttempts = 0;
                    while ((_audioPlayer?.GetBufferedMs() ?? 0) > backpressureMs && waitAttempts++ < 50)
                        System.Threading.Thread.SpinWait(1000);

                    try
                    {
                        var _sw = System.Diagnostics.Stopwatch.StartNew();
                        System.Threading.Interlocked.Increment(ref _retroRunCallCount);
                        DrainKeyboardQueue();
                        _core.Run();
                        // Emulation rate: one step per run, counted regardless of
                        // whether the produced frame ends up displayed.
                        System.Threading.Interlocked.Increment(ref _emuFrameCount);
                        ApplyFrontendArToRam();   // re-clamp AR cheats every frame
                        try { _raClient?.DoFrame(); }
                        catch (Exception raEx) { System.Diagnostics.Trace.WriteLine($"[RA] DoFrame error: {raEx.Message}"); }
                        _sw.Stop();
                        System.Threading.Interlocked.Add(ref _coreRunTotalTicks, _sw.ElapsedTicks);
                        System.Threading.Interlocked.Increment(ref _coreRunSampleCount);

                        // Generic stall detector — covers EVERY cause of a single
                        // retro_run taking far longer than the frame budget (shader
                        // compile, EE/IOP JIT recompile, texture upload, blob decode,
                        // disc seek, etc). When any single Run() exceeds ~5× target
                        // frame ms, surface a "Working…" status so the user knows the
                        // app isn't hung. Auto-clears once Run() is back to normal.
                        double runMs = _sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        if (runMs > _targetFrameMs * 5)
                        {
                            // Don't clobber a more specific message (e.g. "Compiling shaders…")
                            // if it's still active.
                            if (DateTime.Now >= _transientExpiry
                                || (!string.IsNullOrEmpty(_transientMsg) && !_transientMsg.StartsWith("Compiling")))
                            {
                                _transientMsg    = $"Working… (frame took {runMs:F0} ms)";
                                _transientExpiry = DateTime.Now.AddSeconds(3);
                            }
                        }

                        // Low-watermark catch-up: if the buffer dipped below the safe cushion,
                        // run one extra frame to refill before sleeping the frame budget.
                        if ((_audioPlayer?.GetBufferedMs() ?? lowWatermark) < lowWatermark)
                        {
                            DrainKeyboardQueue();
                            _core.Run();
                            ApplyFrontendArToRam();
                            try { _raClient?.DoFrame(); }
                            catch (Exception raEx) { System.Diagnostics.Trace.WriteLine($"[RA] DoFrame error: {raEx.Message}"); }
                        }

                        // Pending save/load — executed between retro_run calls for thread safety.
                        if (_saveStatePending) ExecuteSaveOnEmuThread();
                        if (_loadStatePending) ExecuteLoadOnEmuThread();
                        if (_cheatsApplyPending) ExecuteCheatsApplyOnEmuThread();
                    }
                    catch (AccessViolationException ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"AccessViolation: {ex.Message}\n{ex.StackTrace}");
                        Dispatcher.BeginInvoke(() => StatusText.Text = $"Emulation crashed: {ex.Message}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"Core exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                        Dispatcher.BeginInvoke(() => StatusText.Text = $"Emulation error: {ex.Message}");
                        break;
                    }

                    // Primary timing:
                    // HW cores (Dreamcast, GameCube, N64): audio-sync — wait until the buffer
                    // drains back to prefillMs. If retro_run advanced N game frames it produced
                    // N frames of audio, so the drain takes N frame-times → correct speed for
                    // any per-call frame count (handles 30fps games running at 60Hz VBL, etc.).
                    // A Stopwatch cap of 4× targetFrameMs guards against silent scenes.
                    // SW cores: classic Stopwatch sleep+spin for sub-millisecond accuracy.
                    if (_isClosing) break;

                    if (isHwCore && _audioPlayer != null)
                    {
                        frameTimer.Restart();
                        while (!_isClosing && _audioPlayer.GetBufferedMs() > prefillMs &&
                               frameTimer.Elapsed.TotalMilliseconds < _targetFrameMs * 4)
                            System.Threading.Thread.Sleep(1);
                        frameTimer.Restart();
                    }
                    else
                    {
                        double elapsed = frameTimer.Elapsed.TotalMilliseconds;
                        double remaining = _targetFrameMs - elapsed;
                        if (remaining > 1.5 && !_isClosing)
                            System.Threading.Thread.Sleep((int)(remaining - 1.0));
                        while (!_isClosing && frameTimer.Elapsed.TotalMilliseconds < _targetFrameMs)
                            System.Threading.Thread.SpinWait(10);
                        frameTimer.Restart();
                    }

                    // Drain any Win32 messages queued to this thread's windows.
                    // NVIDIA's GL driver posts synchronization messages (e.g. during
                    // context creation and SwapBuffers) to the window owner thread.
                    // If we never call PeekMessage the driver times out and calls
                    // __fastfail, killing the process — this was the outside-VS crash.
                    if (_glHwndOwned)
                    {
                        MSG msg;
                        while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                            DispatchMessage(ref msg);
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Loop error: {ex.Message}"); }
            finally
            {
                // Flush the live-progress snapshot BEFORE disposing the RA
                // client (Dispose tears down the rcheevos client and the
                // captured-progress dict goes with it). Persistence runs on a
                // Task.Run so we never block the emu-thread teardown on a
                // SQLite write.
                FlushLiveProgressOnExit();

                try { _raClient?.Dispose(); _raClient = null; }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"RA cleanup: {ex.Message}"); }
                // Phase 6b.1: cancel the friend-rank prefetch + clear
                // per-game caches so a subsequent game load starts clean.
                try { _friendServiceForLb?.EndCurrentGameLbPrefetch(); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"LB prefetch cleanup: {ex.Message}"); }
                timeEndPeriod(1);
                System.Diagnostics.Trace.WriteLine("Emulation loop ended");
            }
        }

        // =========================================================================
        // OpenGL context
        // =========================================================================
        private bool InitOpenGLContext()
        {
            try
            {
                IntPtr glHwnd = IntPtr.Zero;

                if (_consoleHandler.UseEmbeddedWindow)
                {
                    // Dolphin: embed a real Win32 child window in the WPF layout.
                    // Dolphin renders directly to FBO 0 (window back buffer) on its
                    // own EmuThread; we present with SwapBuffers.
                    Dispatcher.Invoke(() =>
                    {
                        _hwndHost = new GameHwndHost
                        {
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            VerticalAlignment   = VerticalAlignment.Stretch,
                        };
                        GameViewport.Children.Add(_hwndHost);
                        GameScreen.Visibility = Visibility.Collapsed;
                        glHwnd = _hwndHost.Handle;
                    });
                }
                else
                {
                    // Hidden offscreen window created on the EMU THREAD itself.
                    // NVIDIA's GL driver requires that the window, the DC, and the GL
                    // context all belong to the same thread.  Previously we created the
                    // window on the UI thread (Dispatcher.Invoke) to give it a message
                    // pump, but that gave the DC a different owner thread than the GL
                    // context — NVIDIA's driver __fastfail'd on that mismatch outside VS
                    // (VS's debugger pump masked it).
                    // The correct fix: create everything on the emu thread, then add a
                    // PeekMessage loop inside EmulationLoop to service driver messages.
                    _offscreenWndProc = DefWindowProc;   // keep delegate alive for class lifetime
                    const uint CS_OWNDC   = 0x0020;
                    const uint CS_HREDRAW = 0x0002;
                    const uint CS_VREDRAW = 0x0001;
                    var wc = new WNDCLASSEX
                    {
                        cbSize        = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                        style         = CS_OWNDC | CS_HREDRAW | CS_VREDRAW,
                        lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_offscreenWndProc),
                        hInstance     = GetModuleHandle(null),
                        lpszClassName = "OEWGLOffscreen",
                    };
                    RegisterClassEx(ref wc); // no-op if already registered
                    glHwnd = CreateWindowEx(0, "OEWGLOffscreen", "GLOffscreen",
                        0x80000000u /* WS_POPUP */, 0, 0, 640, 480,
                        IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                    _glHwndOwned = true;
                }

                if (glHwnd == IntPtr.Zero)
                {
                    System.Diagnostics.Trace.WriteLine("HwndHost HWND is zero");
                    return false;
                }

                _glHwnd = glHwnd;
                _hdc = GetDC(_glHwnd);
                if (_hdc == IntPtr.Zero) { System.Diagnostics.Trace.WriteLine("GetDC failed"); return false; }

                // Dolphin (UseEmbeddedWindow) renders to the window and needs PFD_DOUBLEBUFFER
                // so SwapBuffers presents the frame.
                // All other cores (N64/glide64, SNES, etc.) render into an FBO; the window back-buffer
                // is never used.  With PFD_DOUBLEBUFFER on an offscreen window, SwapBuffers triggers
                // DWM compositing which enforces monitorHz÷N vsync (144Hz → 48fps) even when
                // wglSwapIntervalEXT(0) is set.  Without PFD_DOUBLEBUFFER, SwapBuffers is a no-op
                // (just glFlush) — no page flip, no DWM lock.
                uint pfdFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL;
                if (_consoleHandler.UseEmbeddedWindow || _consoleHandler.UseGLOverlay) pfdFlags |= PFD_DOUBLEBUFFER;

                var pfd = new PIXELFORMATDESCRIPTOR
                {
                    nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), nVersion = 1,
                    dwFlags = pfdFlags,
                    iPixelType = PFD_TYPE_RGBA, cColorBits = 32, cDepthBits = 24, cStencilBits = 8,
                };

                int fmt = ChoosePixelFormat(_hdc, ref pfd);
                if (fmt == 0 || !SetPixelFormat(_hdc, fmt, ref pfd))
                {
                    System.Diagnostics.Trace.WriteLine("ChoosePixelFormat/SetPixelFormat failed");
                    return false;
                }
                _glPixelFormatIndex = fmt;
                System.Diagnostics.Trace.WriteLine($"GL pixel format index={fmt} flags=0x{pfdFlags:X}");

                IntPtr dummyCtx = wglCreateContext(_hdc);
                if (dummyCtx == IntPtr.Zero || !wglMakeCurrent(_hdc, dummyCtx))
                {
                    System.Diagnostics.Trace.WriteLine("Dummy context failed");
                    return false;
                }

                var createAttribs = GetGLProc<wglCreateContextAttribsARBDelegate>("wglCreateContextAttribsARB");
                _wglCreateContextAttribsARB = createAttribs;  // save for later use in SET_HW_RENDER
                if (createAttribs == null)
                {
                    _hglrc = dummyCtx;
                }
                else
                {
                    // Cores that declare OPENGL_CORE as their preferred context need Core Profile 3.3.
                    // N64/glide64 and other legacy GL plugins require Compatibility Profile —
                    // Core Profile strips legacy 1.x/2.x APIs (glBegin etc.) that glide64 uses.
                    int profileBit = (_consoleHandler.PreferredHwContext == (int)RETRO_HW_CONTEXT_OPENGL_CORE)
                        ? WGL_CONTEXT_CORE_PROFILE_BIT_ARB
                        : WGL_CONTEXT_COMPATIBILITY_PROFILE_BIT_ARB;

                    int[] attribs = { WGL_CONTEXT_MAJOR_VERSION_ARB, 3, WGL_CONTEXT_MINOR_VERSION_ARB, 3,
                                      WGL_CONTEXT_PROFILE_MASK_ARB, profileBit, 0 };
                    _hglrc = createAttribs(_hdc, IntPtr.Zero, attribs);

                    // If the requested profile failed, fall back to the other
                    if (_hglrc == IntPtr.Zero)
                    {
                        attribs[5] = _consoleHandler.UseEmbeddedWindow
                            ? WGL_CONTEXT_COMPATIBILITY_PROFILE_BIT_ARB
                            : WGL_CONTEXT_CORE_PROFILE_BIT_ARB;
                        _hglrc = createAttribs(_hdc, IntPtr.Zero, attribs);
                    }

                    if (_hglrc == IntPtr.Zero) { _hglrc = dummyCtx; }
                    else { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(dummyCtx); }
                }

                if (!wglMakeCurrent(_hdc, _hglrc))
                {
                    System.Diagnostics.Trace.WriteLine("Final wglMakeCurrent failed");
                    wglDeleteContext(_hglrc); _hglrc = IntPtr.Zero;
                    ReleaseDC(_glHwnd, _hdc); _hdc = IntPtr.Zero;
                    return false;
                }

                System.Diagnostics.Trace.WriteLine($"GL context ready: HGLRC=0x{_hglrc:X}, HWND=0x{_glHwnd:X}, shared={_consoleHandler.AllowHwSharedContext}");
                LoadGLExtensions();

                // Disable vsync immediately — driver default is ON which caps readback FPS
                // and causes variable-latency stalls in glReadPixels.
                var swapIntervalFn = GetGLProc<wglSwapIntervalEXTDelegate>("wglSwapIntervalEXT");
                if (swapIntervalFn != null) { swapIntervalFn(0); _vsyncDisabled = true; }
                System.Diagnostics.Trace.WriteLine($"vsync disabled={_vsyncDisabled}");

                return true;
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"InitOpenGLContext: {ex.Message}"); return false; }
        }

        private static IntPtr _opengl32 = IntPtr.Zero;
        private static IntPtr GetOpenGL32()
        {
            if (_opengl32 == IntPtr.Zero) _opengl32 = NativeMethods2.GetModuleHandle("opengl32.dll");
            if (_opengl32 == IntPtr.Zero) _opengl32 = NativeMethods2.LoadLibrary("opengl32.dll");
            return _opengl32;
        }

        private T? GetGLProc<T>(string name) where T : class
        {
            IntPtr ptr = wglGetProcAddress(name);
            if (ptr == IntPtr.Zero || ((long)ptr >= 1 && (long)ptr <= 3))
            {
                IntPtr lib = GetOpenGL32();
                if (lib != IntPtr.Zero) ptr = NativeMethods2.GetProcAddress(lib, name);
            }
            if (ptr == IntPtr.Zero) { System.Diagnostics.Trace.WriteLine($"GL proc missing: {name}"); return null; }
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        private void LoadGLExtensions()
        {
            _glGenFramebuffers         = GetGLProc<glGenFramebuffersDelegate>("glGenFramebuffers");
            _glBindFramebuffer         = GetGLProc<glBindFramebufferDelegate>("glBindFramebuffer");
            _glFramebufferTexture2D    = GetGLProc<glFramebufferTexture2DDelegate>("glFramebufferTexture2D");
            _glGenRenderbuffers        = GetGLProc<glGenRenderbuffersDelegate>("glGenRenderbuffers");
            _glBindRenderbuffer        = GetGLProc<glBindRenderbufferDelegate>("glBindRenderbuffer");
            _glRenderbufferStorage     = GetGLProc<glRenderbufferStorageDelegate>("glRenderbufferStorage");
            _glFramebufferRenderbuffer = GetGLProc<glFramebufferRenderbufferDelegate>("glFramebufferRenderbuffer");
            _glCheckFramebufferStatus  = GetGLProc<glCheckFramebufferStatusDelegate>("glCheckFramebufferStatus");
            _glGenTextures             = GetGLProc<glGenTexturesDelegate>("glGenTextures");
            _glBindTexture             = GetGLProc<glBindTextureDelegate>("glBindTexture");
            _glTexImage2D              = GetGLProc<glTexImage2DDelegate>("glTexImage2D");
            _glTexParameteri           = GetGLProc<glTexParameteriDelegate>("glTexParameteri");
            _glDeleteFramebuffers      = GetGLProc<glDeleteFramebuffersDelegate>("glDeleteFramebuffers");
            _glDeleteRenderbuffers     = GetGLProc<glDeleteRenderbuffersDelegate>("glDeleteRenderbuffers");
            _glDeleteTextures          = GetGLProc<glDeleteTexturesDelegate>("glDeleteTextures");
            _glBlitFramebuffer         = GetGLProc<glBlitFramebufferDelegate>("glBlitFramebuffer");
            _glGenBuffers              = GetGLProc<glGenBuffersDelegate>("glGenBuffers");
            _glBindBuffer              = GetGLProc<glBindBufferDelegate>("glBindBuffer");
            _glBufferData              = GetGLProc<glBufferDataDelegate>("glBufferData");
            _glMapBuffer               = GetGLProc<glMapBufferDelegate>("glMapBuffer");
            _glUnmapBuffer             = GetGLProc<glUnmapBufferDelegate>("glUnmapBuffer");
            _glDeleteBuffers           = GetGLProc<glDeleteBuffersDelegate>("glDeleteBuffers");
        }

        private void CreateFBO(uint width, uint height)
        {
            if (_glGenTextures == null || _glTexImage2D == null ||
                _glBindTexture == null || _glTexParameteri == null)
            {
                System.Diagnostics.Trace.WriteLine("FBO creation skipped — missing GL functions");
                return;
            }

            DestroyFBO();
            _fboWidth = width; _fboHeight = height;

            uint[] ids = new uint[1];
            _glGenTextures!(1, ids); _fboTex = ids[0];
            _glBindTexture!(GL_TEXTURE_2D, _fboTex);
            _glTexImage2D!(GL_TEXTURE_2D, 0, GL_RGBA8, (int)width, (int)height, 0, GL_RGBA, GL_UNSIGNED_BYTE, IntPtr.Zero);
            _glTexParameteri!(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_LINEAR);
            _glTexParameteri!(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_LINEAR);
            _glBindTexture!(GL_TEXTURE_2D, 0);

            _glGenRenderbuffers!(1, ids); _fboDepth = ids[0];
            _glBindRenderbuffer!(GL_RENDERBUFFER, _fboDepth);
            _glRenderbufferStorage!(GL_RENDERBUFFER, GL_DEPTH_COMPONENT24, (int)width, (int)height);
            _glBindRenderbuffer!(GL_RENDERBUFFER, 0);

            if (_consoleHandler.AllowHwSharedContext)
            {
                // Shared-context path (N64/glide64): core renders to FBO 0 of its own EmuThread
                // context, not to an FBO we allocate.  Leave _fboId = 0; GetCurrentFramebuffer
                // returns 0; OnVideoRefresh reads back from FBO 0 via glReadPixels.
                _fboId = 0;
                System.Diagnostics.Trace.WriteLine($"Shared-ctx path: texture={_fboTex} rb={_fboDepth} (not bound — core uses EmuThread FBO 0)");
            }
            else
            {
                _glGenFramebuffers!(1, ids); _fboId = ids[0];
                _glBindFramebuffer!(GL_FRAMEBUFFER, _fboId);
                _glFramebufferTexture2D!(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _fboTex, 0);
                _glFramebufferRenderbuffer!(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, _fboDepth);
                uint status = _glCheckFramebufferStatus!(GL_FRAMEBUFFER);
                System.Diagnostics.Trace.WriteLine(status == GL_FRAMEBUFFER_COMPLETE
                    ? $"FBO ok: {width}x{height} id={_fboId}" : $"FBO incomplete: 0x{status:X}");
                _glBindFramebuffer!(GL_FRAMEBUFFER, 0);
            }

            // Pre-allocate PBOs sized to this FBO — allows async glReadPixels next frame.
            CreatePBOs((int)(width * height * 4));
        }

        private void DestroyFBO()
        {
            DestroyPBOs();
            if (_fboId != 0)
            {
                // For AllowHwSharedContext cores _fboId stays 0 (core uses EmuThread FBO 0),
                // so this branch only executes for single-threaded HW cores (GameCube etc.).
                if (!_consoleHandler.AllowHwSharedContext)
                    _glDeleteFramebuffers?.Invoke(1, new[] { _fboId });
                _fboId = 0;
            }
            if (_fboTex   != 0) { _glDeleteTextures?.Invoke(1, new[] { _fboTex });        _fboTex   = 0; }
            if (_fboDepth != 0) { _glDeleteRenderbuffers?.Invoke(1, new[] { _fboDepth }); _fboDepth = 0; }
        }

        private void CreatePBOs(int byteCount)
        {
            if (_glGenBuffers == null || _glBindBuffer == null || _glBufferData == null) return;
            DestroyPBOs();
            _glGenBuffers(2, _pboIds);
            for (int i = 0; i < 2; i++)
            {
                _glBindBuffer(GL_PIXEL_PACK_BUFFER, _pboIds[i]);
                _glBufferData(GL_PIXEL_PACK_BUFFER, (IntPtr)byteCount, IntPtr.Zero, GL_STREAM_READ);
            }
            _glBindBuffer(GL_PIXEL_PACK_BUFFER, 0);
            _pboReadIdx = 0;
            _pboReady   = false;
            System.Diagnostics.Trace.WriteLine($"PBOs created: 2 × {byteCount} bytes");
        }

        private void DestroyPBOs()
        {
            if (_pboIds[0] != 0 || _pboIds[1] != 0)
            {
                _glDeleteBuffers?.Invoke(2, _pboIds);
                _pboIds[0] = _pboIds[1] = 0;
            }
            _pboReady = false;
        }

        // sourceFbo: which GL framebuffer to read from.
        //   0         = default framebuffer (window back buffer) — use when core renders to FBO 0
        //   _fboId    = our explicit FBO — use when core properly binds get_current_framebuffer result
        private void ReadBackFramebuffer(uint sourceFbo = 0, uint rw = 0, uint rh = 0)
        {
            uint w = rw > 0 ? rw : _fboWidth;
            uint h = rh > 0 ? rh : _fboHeight;
            if (w == 0 || h == 0) return;

            if (_hwVideoPending) return;

            try
            {
                int byteCount = (int)(w * h * 4);

                // Resize reusable buffers only when resolution changes (avoids per-frame GC pressure)
                if (_hwPixelBuffer.Length != byteCount)
                {
                    _hwPixelBuffer   = new byte[byteCount];
                    _hwFlippedBuffer = new byte[byteCount];
                }

                // Re-acquire the GL context for the readback — we released it after
                // context_reset so mupen64's EmuThread could claim it.  mupen64's
                // EmuThread finishes rendering before calling OnVideoRefresh (which
                // calls us), so the context should be idle at this point.
                wglMakeCurrent(_hdc, _hglrc);
                var pin = GCHandle.Alloc(_hwPixelBuffer, GCHandleType.Pinned);
                try
                {
                    _glBindFramebuffer?.Invoke(GL_READ_FRAMEBUFFER, sourceFbo);
                    glReadPixels(0, 0, (int)w, (int)h, GL_BGRA, GL_UNSIGNED_BYTE, pin.AddrOfPinnedObject());
                    _glBindFramebuffer?.Invoke(GL_READ_FRAMEBUFFER, 0);
                }
                finally
                {
                    pin.Free();
                    // Release again so mupen64's EmuThread can reclaim it next frame.
                    wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                }

                // Flip vertically in-place into the reusable flip buffer (OpenGL is bottom-up)
                int stride = (int)w * 4;
                for (int y = 0; y < (int)h; y++)
                    Buffer.BlockCopy(_hwPixelBuffer, y * stride, _hwFlippedBuffer, ((int)h - 1 - y) * stride, stride);

                // Force alpha=255 — glide64 leaves alpha=0 in the colour attachment;
                // WPF Bgra32 treats alpha=0 as fully transparent → dark/black pixels.
                for (int i = 3; i < byteCount; i += 4)
                    _hwFlippedBuffer[i] = 0xFF;

                // Recording: feed the readback buffer into the FFmpeg path.
                // Only fires when the active service is the FFmpeg one — Vectrex
                // routes here; WGC-recorded HW cores have a WgcRecordingService and
                // skip this branch via the `is` type check.
                if (_recordingService is Services.RecordingService ffmpegRec && ffmpegRec.IsRecording)
                {
                    ffmpegRec.QueueVideoFrame(_hwFlippedBuffer, byteCount);
                }

                _hwFlippedWidth  = w;
                _hwFlippedHeight = h;
                _hwVideoPending  = true;
                uint capturedW = w, capturedH = h;
                // Snapshot the buffer REFERENCE so the dispatcher invoke uses
                // the buffer that existed at queue time. The readback path can
                // reallocate _hwFlippedBuffer as resolution changes per frame
                // (Beetle PSX HW alternates 2048×1920 ↔ 5120×3824 etc. while
                // booting / changing display modes). Without this snapshot the
                // closure late-binds to the field and ends up copying from a
                // smaller buffer than capturedW×capturedH expects → throws
                // "argument out of range" and the frame is dropped — most
                // upscaled frames vanish, the bitmap goes stale.
                byte[] capturedBuffer = _hwFlippedBuffer;
                int    capturedBytes  = (int)(capturedW * capturedH * 4);
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (capturedBuffer == null || capturedBuffer.Length < capturedBytes)
                        {
                            System.Diagnostics.Trace.WriteLine(
                                $"HW video UI: buffer size mismatch (have {capturedBuffer?.Length ?? 0}, need {capturedBytes}) — frame dropped");
                            return;
                        }
                        if (_bitmap == null || _videoWidth != capturedW || _videoHeight != capturedH || _bitmap.Format != PixelFormats.Bgra32)
                        {
                            _videoWidth = capturedW; _videoHeight = capturedH;
                            _bitmap = new WriteableBitmap((int)capturedW, (int)capturedH, 96, 96, PixelFormats.Bgra32, null);
                            GameScreen.Source = _bitmap;
                            UpdateDisplayAspectRatio(capturedW, capturedH, _core?.AvInfo.geometry.aspect_ratio ?? 0f);
                            UpdateShaderScreenHeight(capturedH);
                            ApplyGameScreenScalingMode(capturedW, capturedH);
                        }
                        _bitmap.Lock();
                        Marshal.Copy(capturedBuffer, 0, _bitmap.BackBuffer, capturedBytes);
                        _bitmap.AddDirtyRect(new Int32Rect(0, 0, (int)capturedW, (int)capturedH));
                        _bitmap.Unlock();
                    }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"HW video UI: {ex.Message}"); }
                    finally { _hwVideoPending = false; }
                }, DispatcherPriority.Render);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"ReadBackFramebuffer: {ex.Message}"); }
        }

        // Called from mupen64plus EmuThread — its own GL context is already current.
        // sourceFbo == 0 means read from the default framebuffer (back buffer of EmuThread's window).
        // No wglMakeCurrent needed: we use the caller's current context directly.
        //
        // Uses double-buffered PBO async readback when available:
        //   Frame N:   glReadPixels into PBO[writeIdx]  — async DMA starts, returns immediately
        //   Frame N+1: map PBO[readIdx] — data already in system RAM, zero GPU stall
        // This eliminates the PCIe bus stall that capped FPS at ~48.
        private void ReadBackFromCurrentContext(uint sourceFbo, uint rw, uint rh)
        {
            uint w = rw > 0 ? rw : _fboWidth;
            uint h = rh > 0 ? rh : _fboHeight;
            if (w == 0 || h == 0) return;

            try
            {
                int byteCount = (int)(w * h * 4);
                if (_hwPixelBuffer.Length != byteCount)
                {
                    _hwPixelBuffer   = new byte[byteCount];
                    _hwFlippedBuffer = new byte[byteCount];
                    // PBOs are sized to FBO at CreateFBO time; recreate if resolution changed at runtime.
                    CreatePBOs(byteCount);
                }

                bool usePbo = _glBindBuffer != null && _glMapBuffer != null &&
                              _glUnmapBuffer != null && _pboIds[0] != 0;

                if (usePbo)
                {
                    int writeIdx = 1 - _pboReadIdx;
                    bool hasData = false;

                    // Read previous frame from _pboIds[_pboReadIdx] (already in system RAM — no GPU stall).
                    if (_pboReady)
                    {
                        _glBindBuffer!(GL_PIXEL_PACK_BUFFER, _pboIds[_pboReadIdx]);
                        IntPtr ptr = _glMapBuffer!(GL_PIXEL_PACK_BUFFER, GL_READ_ONLY);
                        if (ptr != IntPtr.Zero)
                        {
                            Marshal.Copy(ptr, _hwPixelBuffer, 0, byteCount);
                            hasData = true;
                        }
                        _glUnmapBuffer!(GL_PIXEL_PACK_BUFFER);
                        _glBindBuffer!(GL_PIXEL_PACK_BUFFER, 0);
                    }

                    // Kick off async DMA for current frame into _pboIds[writeIdx].
                    // glReadPixels with a bound PBO returns immediately; the driver DMAs in the background.
                    _glBindFramebuffer?.Invoke(GL_READ_FRAMEBUFFER, sourceFbo);
                    _glBindBuffer!(GL_PIXEL_PACK_BUFFER, _pboIds[writeIdx]);
                    glReadPixels(0, 0, (int)w, (int)h, GL_BGRA, GL_UNSIGNED_BYTE, IntPtr.Zero);
                    _glBindBuffer!(GL_PIXEL_PACK_BUFFER, 0);
                    _glBindFramebuffer?.Invoke(GL_READ_FRAMEBUFFER, 0);

                    _pboReadIdx = writeIdx;
                    _pboReady   = true;

                    if (!hasData) return;  // first frame: PBO not yet filled, nothing to display yet
                    System.Threading.Interlocked.Increment(ref _frameCount);
                }
                else
                {
                    // Fallback: synchronous readback (PBO extension not available).
                    var pin = GCHandle.Alloc(_hwPixelBuffer, GCHandleType.Pinned);
                    try
                    {
                        _glBindFramebuffer?.Invoke(GL_READ_FRAMEBUFFER, sourceFbo);
                        glReadPixels(0, 0, (int)w, (int)h, GL_BGRA, GL_UNSIGNED_BYTE, pin.AddrOfPinnedObject());
                        _glBindFramebuffer?.Invoke(GL_READ_FRAMEBUFFER, 0);
                    }
                    finally { pin.Free(); }
                    System.Threading.Interlocked.Increment(ref _frameCount);
                }

                int stride = (int)w * 4;
                for (int y = 0; y < (int)h; y++)
                    Buffer.BlockCopy(_hwPixelBuffer, y * stride, _hwFlippedBuffer, ((int)h - 1 - y) * stride, stride);

                // Force alpha=255 — glide64 leaves alpha=0 in the colour attachment;
                // WPF Bgra32 treats alpha=0 as fully transparent → dark/black pixels.
                for (int i = 3; i < byteCount; i += 4)
                    _hwFlippedBuffer[i] = 0xFF;

                // Recording: feed the readback buffer into the FFmpeg path.
                // Only fires when the active service is the FFmpeg one — Vectrex
                // routes here; WGC-recorded HW cores have a WgcRecordingService and
                // skip this branch via the `is` type check.
                if (_recordingService is Services.RecordingService ffmpegRec && ffmpegRec.IsRecording)
                {
                    ffmpegRec.QueueVideoFrame(_hwFlippedBuffer, byteCount);
                }

                _hwFlippedWidth  = w;
                _hwFlippedHeight = h;
                _hwVideoPending  = true;
                uint capturedW = w, capturedH = h;
                // Snapshot the buffer REFERENCE so the dispatcher invoke uses
                // the buffer that existed at queue time. The readback path can
                // reallocate _hwFlippedBuffer as resolution changes per frame
                // (Beetle PSX HW alternates 2048×1920 ↔ 5120×3824 etc. while
                // booting / changing display modes). Without this snapshot the
                // closure late-binds to the field and ends up copying from a
                // smaller buffer than capturedW×capturedH expects → throws
                // "argument out of range" and the frame is dropped — most
                // upscaled frames vanish, the bitmap goes stale.
                byte[] capturedBuffer = _hwFlippedBuffer;
                int    capturedBytes  = (int)(capturedW * capturedH * 4);
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (capturedBuffer == null || capturedBuffer.Length < capturedBytes)
                        {
                            System.Diagnostics.Trace.WriteLine(
                                $"HW video UI: buffer size mismatch (have {capturedBuffer?.Length ?? 0}, need {capturedBytes}) — frame dropped");
                            return;
                        }
                        if (_bitmap == null || _videoWidth != capturedW || _videoHeight != capturedH || _bitmap.Format != PixelFormats.Bgra32)
                        {
                            _videoWidth = capturedW; _videoHeight = capturedH;
                            _bitmap = new WriteableBitmap((int)capturedW, (int)capturedH, 96, 96, PixelFormats.Bgra32, null);
                            GameScreen.Source = _bitmap;
                            UpdateDisplayAspectRatio(capturedW, capturedH, _core?.AvInfo.geometry.aspect_ratio ?? 0f);
                            UpdateShaderScreenHeight(capturedH);
                            ApplyGameScreenScalingMode(capturedW, capturedH);
                        }
                        _bitmap.Lock();
                        Marshal.Copy(capturedBuffer, 0, _bitmap.BackBuffer, capturedBytes);
                        _bitmap.AddDirtyRect(new Int32Rect(0, 0, (int)capturedW, (int)capturedH));
                        _bitmap.Unlock();
                    }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"HW video UI: {ex.Message}"); }
                    finally { _hwVideoPending = false; }
                }, DispatcherPriority.Render);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"ReadBackFromCurrentContext: {ex.Message}"); }
        }

        /// <summary>
        /// Picks the right WPF BitmapScalingMode for the source frame size.
        /// NearestNeighbor preserves crisp pixel-art when the source is
        /// at-or-below native resolution and the Image upscales it. But when
        /// the source is HW-upscaled (Beetle PSX HW @ 8×, ParaLLEl-RDP @ 4×,
        /// etc.) the Image is downsampling — and NearestNeighbor downsampling
        /// throws away most of the high-frequency detail, defeating the
        /// upscale. HighQuality (Fant filter) downsamples properly.
        /// Threshold of ~960×720 catches anything past 1.5× native PS1 res.
        /// </summary>
        private void ApplyGameScreenScalingMode(uint w, uint h)
        {
            const long UpscaleArea = 960L * 720L;
            var mode = (long)w * (long)h > UpscaleArea
                ? BitmapScalingMode.HighQuality
                : BitmapScalingMode.NearestNeighbor;
            if (RenderOptions.GetBitmapScalingMode(GameScreen) != mode)
            {
                RenderOptions.SetBitmapScalingMode(GameScreen, mode);
                System.Diagnostics.Trace.WriteLine(
                    $"[VID] GameScreen scaling = {mode} (source {w}x{h})");
            }
        }

        // =========================================================================
        // Libretro environment constants
        // =========================================================================
        private const uint RETRO_ENVIRONMENT_SET_ROTATION                              = 1;
        private const uint RETRO_ENVIRONMENT_GET_OVERSCAN                              = 2;
        private const uint RETRO_ENVIRONMENT_GET_CAN_DUPE                              = 3;
        private const uint RETRO_ENVIRONMENT_SET_MESSAGE                               = 6;
        private const uint RETRO_ENVIRONMENT_SHUTDOWN                                  = 7;
        private const uint RETRO_ENVIRONMENT_SET_PERFORMANCE_LEVEL                     = 8;
        private const uint RETRO_ENVIRONMENT_GET_SYSTEM_DIRECTORY                      = 9;
        private const uint RETRO_ENVIRONMENT_SET_PIXEL_FORMAT                          = 10;
        private const uint RETRO_ENVIRONMENT_SET_INPUT_DESCRIPTORS                     = 11;
        private const uint RETRO_ENVIRONMENT_SET_KEYBOARD_CALLBACK                     = 12;
        private const uint RETRO_ENVIRONMENT_SET_DISK_CONTROL_INTERFACE                = 13;
        private const uint RETRO_ENVIRONMENT_SET_HW_RENDER                             = 14;
        private const uint RETRO_ENVIRONMENT_GET_VARIABLE                              = 15;
        private const uint RETRO_ENVIRONMENT_SET_VARIABLES                             = 16;
        private const uint RETRO_ENVIRONMENT_GET_VARIABLE_UPDATE                       = 17;
        private const uint RETRO_ENVIRONMENT_SET_SUPPORT_NO_GAME                       = 18;
        private const uint RETRO_ENVIRONMENT_GET_LIBRETRO_PATH                         = 19;
        private const uint RETRO_ENVIRONMENT_SET_FRAME_TIME_CALLBACK                   = 21;
        private const uint RETRO_ENVIRONMENT_SET_AUDIO_CALLBACK                        = 22;
        private const uint RETRO_ENVIRONMENT_GET_RUMBLE_INTERFACE                      = 23;
        private const uint RETRO_ENVIRONMENT_GET_INPUT_DEVICE_CAPABILITIES             = 24;
        private const uint RETRO_ENVIRONMENT_GET_SENSOR_INTERFACE                      = 25;
        private const uint RETRO_ENVIRONMENT_GET_CAMERA_INTERFACE                      = 26;
        private const uint RETRO_ENVIRONMENT_GET_LOG_INTERFACE                         = 27;
        private const uint RETRO_ENVIRONMENT_GET_PERF_INTERFACE                        = 28;
        private const uint RETRO_ENVIRONMENT_GET_LOCATION_INTERFACE                    = 29;
        private const uint RETRO_ENVIRONMENT_GET_CONTENT_DIRECTORY                     = 30;
        private const uint RETRO_ENVIRONMENT_GET_SAVE_DIRECTORY                        = 31;
        private const uint RETRO_ENVIRONMENT_SET_SYSTEM_AV_INFO                        = 32;
        private const uint RETRO_ENVIRONMENT_SET_PROC_ADDRESS_CALLBACK                 = 33;
        private const uint RETRO_ENVIRONMENT_SET_SUBSYSTEM_INFO                        = 34;
        private const uint RETRO_ENVIRONMENT_SET_CONTROLLER_INFO                       = 35;
        private const uint RETRO_ENVIRONMENT_SET_MEMORY_MAPS                           = 36;
        private const uint RETRO_ENVIRONMENT_SET_GEOMETRY                              = 37;
        private const uint RETRO_ENVIRONMENT_GET_USERNAME                              = 38;
        private const uint RETRO_ENVIRONMENT_GET_LANGUAGE                              = 39;
        private const uint RETRO_ENVIRONMENT_GET_CURRENT_SOFTWARE_FRAMEBUFFER          = 40;
        private const uint RETRO_ENVIRONMENT_GET_HW_RENDER_INTERFACE                   = 41;
        private const uint RETRO_ENVIRONMENT_SET_SUPPORT_ACHIEVEMENTS                  = 42;
        private const uint RETRO_ENVIRONMENT_SET_HW_RENDER_CONTEXT_NEGOTIATION_INTERFACE = 43;
        private const uint RETRO_ENVIRONMENT_SET_SERIALIZATION_QUIRKS                  = 44;
        private const uint RETRO_ENVIRONMENT_SET_HW_SHARED_CONTEXT                     = 44; // 44 | EXPERIMENTAL in libretro.h (same baseCmd)
        private const uint RETRO_ENVIRONMENT_GET_VFS_INTERFACE                         = 45;
        private const uint RETRO_ENVIRONMENT_GET_LED_INTERFACE                         = 46;
        private const uint RETRO_ENVIRONMENT_GET_AUDIO_VIDEO_ENABLE                    = 47;
        private const uint RETRO_ENVIRONMENT_GET_MIDI_INTERFACE                        = 48;
        private const uint RETRO_ENVIRONMENT_GET_FASTFORWARDING                        = 49;
        private const uint RETRO_ENVIRONMENT_GET_TARGET_REFRESH_RATE                   = 50;
        private const uint RETRO_ENVIRONMENT_GET_INPUT_BITMASKS                        = 51;
        private const uint RETRO_ENVIRONMENT_GET_CORE_OPTIONS_VERSION                  = 52;
        private const uint RETRO_ENVIRONMENT_SET_CORE_OPTIONS                          = 53;
        private const uint RETRO_ENVIRONMENT_SET_CORE_OPTIONS_INTL                     = 54;
        private const uint RETRO_ENVIRONMENT_SET_CORE_OPTIONS_DISPLAY                  = 55;
        private const uint RETRO_ENVIRONMENT_GET_PREFERRED_HW_RENDER                   = 56;
        private const uint RETRO_ENVIRONMENT_GET_DISK_CONTROL_INTERFACE_VERSION        = 57;
        private const uint RETRO_ENVIRONMENT_SET_DISK_CONTROL_EXT_INTERFACE            = 58;
        private const uint RETRO_ENVIRONMENT_GET_MESSAGE_INTERFACE_VERSION             = 59;
        private const uint RETRO_ENVIRONMENT_SET_MESSAGE_EXT                           = 60;
        private const uint RETRO_ENVIRONMENT_GET_INPUT_MAX_USERS                       = 61;
        private const uint RETRO_ENVIRONMENT_SET_AUDIO_BUFFER_STATUS_CALLBACK          = 62;
        private const uint RETRO_ENVIRONMENT_SET_MINIMUM_AUDIO_LATENCY                 = 63;
        private const uint RETRO_ENVIRONMENT_SET_FASTFORWARDING_OVERRIDE               = 64;
        private const uint RETRO_ENVIRONMENT_SET_CONTENT_INFO_OVERRIDE                 = 65;
        private const uint RETRO_ENVIRONMENT_GET_GAME_INFO_EXT                         = 66;
        private const uint RETRO_ENVIRONMENT_SET_CORE_OPTIONS_V2                       = 67;
        private const uint RETRO_ENVIRONMENT_SET_CORE_OPTIONS_V2_INTL                  = 68;
        private const uint RETRO_ENVIRONMENT_SET_CORE_OPTIONS_UPDATE_DISPLAY_CALLBACK  = 69;

        private const uint RETRO_HW_CONTEXT_NONE        = 0;
        private const uint RETRO_HW_CONTEXT_OPENGL      = 1;
        private const uint RETRO_HW_CONTEXT_OPENGLES2   = 2;
        private const uint RETRO_HW_CONTEXT_OPENGL_CORE = 3;
        private const uint RETRO_HW_CONTEXT_OPENGLES3   = 4;
        private const uint RETRO_HW_CONTEXT_VULKAN      = 6;
        private const uint RETRO_HW_CONTEXT_D3D11       = 7;

        // =========================================================================
        // Environment callback
        // =========================================================================
        private static readonly HashSet<uint> _seenEnvCmds = new();
        private bool OnEnvironment(uint cmd, IntPtr data)
        {
            uint baseCmd = cmd & 0xFF;
            // Log each unique env cmd once per session — gives a compact picture of
            // what the core actually calls without flooding the log. Helps diagnose
            // missing disk-control / unhandled-command bugs.
            lock (_seenEnvCmds)
            {
                if (_seenEnvCmds.Add(cmd))
                    System.Diagnostics.Trace.WriteLine($"[ENV-FIRST] cmd=0x{cmd:X} base={baseCmd} (decimal cmd={cmd})");
            }
            try
            {
                return OnEnvironmentBody(cmd, baseCmd, data);
            }
            catch
            {
                return false;
            }
        }

        private bool OnEnvironmentBody(uint cmd, uint baseCmd, IntPtr data)
        {
            try
            {
                switch (baseCmd)
                {
                    // ------------------------------------------------------------------
                    // Disc control interface
                    //
                    // The core passes us a struct of its own function pointers so the
                    // frontend can call them to eject/insert/swap discs.
                    //
                    // Returning TRUE is what allows disc-based cores (genesis_plus_gx,
                    // mednafen_pce, beetle_psx, etc.) to load CHD/cue/bin images.
                    // Returning false causes those cores to silently refuse to load
                    // disc images even when need_fullpath is true and the file exists.
                    // ------------------------------------------------------------------
                    case RETRO_ENVIRONMENT_SET_DISK_CONTROL_INTERFACE:
                    {
                        if (data == IntPtr.Zero) return false;

                        var cb = Marshal.PtrToStructure<retro_disk_control_callback>(data);

                        if (cb.set_eject_state != IntPtr.Zero)
                            _diskSetEjectState = Marshal.GetDelegateForFunctionPointer<DiskSetEjectState_t>(cb.set_eject_state);
                        if (cb.get_eject_state != IntPtr.Zero)
                            _diskGetEjectState = Marshal.GetDelegateForFunctionPointer<DiskGetEjectState_t>(cb.get_eject_state);
                        if (cb.get_image_index != IntPtr.Zero)
                            _diskGetImageIndex = Marshal.GetDelegateForFunctionPointer<DiskGetImageIndex_t>(cb.get_image_index);
                        if (cb.set_image_index != IntPtr.Zero)
                            _diskSetImageIndex = Marshal.GetDelegateForFunctionPointer<DiskSetImageIndex_t>(cb.set_image_index);
                        if (cb.get_num_images != IntPtr.Zero)
                            _diskGetNumImages = Marshal.GetDelegateForFunctionPointer<DiskGetNumImages_t>(cb.get_num_images);
                        if (cb.add_image_index != IntPtr.Zero)
                            _diskAddImageIndex = Marshal.GetDelegateForFunctionPointer<DiskAddImageIndex_t>(cb.add_image_index);

                        _diskControlAvailable = true;
                        System.Diagnostics.Trace.WriteLine("Disc control interface registered");
                        return true;
                    }

                    // Extended disc interface — same first seven function pointers as
                    // the legacy struct, so we can parse it as retro_disk_control_callback
                    // and pick up the standard callbacks. Modern Nestopia uses ONLY the
                    // EXT version for FDS — without this capture the in-game disk-swap
                    // feature was inert (acknowledging the env call without grabbing the
                    // function pointers means _diskControlAvailable stayed false).
                    case RETRO_ENVIRONMENT_SET_DISK_CONTROL_EXT_INTERFACE:
                    {
                        if (data == IntPtr.Zero) return false;
                        var cb = Marshal.PtrToStructure<retro_disk_control_callback>(data);
                        if (cb.set_eject_state != IntPtr.Zero)
                            _diskSetEjectState = Marshal.GetDelegateForFunctionPointer<DiskSetEjectState_t>(cb.set_eject_state);
                        if (cb.get_eject_state != IntPtr.Zero)
                            _diskGetEjectState = Marshal.GetDelegateForFunctionPointer<DiskGetEjectState_t>(cb.get_eject_state);
                        if (cb.get_image_index != IntPtr.Zero)
                            _diskGetImageIndex = Marshal.GetDelegateForFunctionPointer<DiskGetImageIndex_t>(cb.get_image_index);
                        if (cb.set_image_index != IntPtr.Zero)
                            _diskSetImageIndex = Marshal.GetDelegateForFunctionPointer<DiskSetImageIndex_t>(cb.set_image_index);
                        if (cb.get_num_images != IntPtr.Zero)
                            _diskGetNumImages = Marshal.GetDelegateForFunctionPointer<DiskGetNumImages_t>(cb.get_num_images);
                        if (cb.add_image_index != IntPtr.Zero)
                            _diskAddImageIndex = Marshal.GetDelegateForFunctionPointer<DiskAddImageIndex_t>(cb.add_image_index);
                        _diskControlAvailable = true;
                        System.Diagnostics.Trace.WriteLine("Disc control EXT interface registered");
                        return true;
                    }

                    // Report basic disc control version (0 = original spec)
                    case RETRO_ENVIRONMENT_GET_DISK_CONTROL_INTERFACE_VERSION:
                        if (data != IntPtr.Zero) Marshal.WriteInt32(data, 0);
                        return true;

                    // ------------------------------------------------------------------
                    // Hardware rendering
                    // ------------------------------------------------------------------
                    case RETRO_ENVIRONMENT_SET_HW_RENDER:
                    {
                        if (data == IntPtr.Zero) return false;

                        var hw = Marshal.PtrToStructure<retro_hw_render_callback>(data);
                        System.Diagnostics.Trace.WriteLine(
                            $"SET_HW_RENDER: type={hw.context_type} v{hw.version_major}.{hw.version_minor}" +
                            $" depth={hw.depth} stencil={hw.stencil}");

                        // ── Vulkan path ──────────────────────────────────────────
                        // Defer VulkanContext creation to context_reset time, because
                        // the core sends SET_HW_RENDER_CONTEXT_NEGOTIATION_INTERFACE
                        // AFTER SET_HW_RENDER during retro_load_game.
                        if (hw.context_type == RETRO_HW_CONTEXT_VULKAN)
                        {
                            _isVulkanHwRender = true;
                            _hwRenderActive = true;
                            Dispatcher.BeginInvoke(() =>
                            {
                                OverlayShaderBtn.Visibility = Visibility.Collapsed;
                            });

                            if (hw.context_reset != IntPtr.Zero)
                                _hwContextReset = Marshal.GetDelegateForFunctionPointer<retro_hw_context_reset_t>(hw.context_reset);
                            if (hw.context_destroy != IntPtr.Zero)
                                _hwContextDestroy = Marshal.GetDelegateForFunctionPointer<retro_hw_context_reset_t>(hw.context_destroy);

                            // get_current_framebuffer unused for Vulkan
                            Marshal.WriteIntPtr(data, 16, IntPtr.Zero);

                            System.Diagnostics.Trace.WriteLine($"SET_HW_RENDER: Vulkan noted, init deferred to context_reset. context_destroy={hw.context_destroy:X}");
                            return true;
                        }

                        // ── Direct3D 11 path (LRPS2 D3D11 GS backend) ────────────
                        // We own the device; the core QIs it on context_reset via
                        // GET_HW_RENDER_INTERFACE. No negotiation interface (unlike
                        // Vulkan), so create the device now — it must exist before
                        // context_reset fires post-LoadGame.
                        if (hw.context_type == RETRO_HW_CONTEXT_D3D11)
                        {
                            _d3d11Context = new D3D11Context();
                            if (!_d3d11Context.Initialize())
                            {
                                System.Diagnostics.Trace.WriteLine("SET_HW_RENDER: D3D11 device creation failed");
                                _d3d11Context = null;
                                return false;
                            }
                            _isD3d11HwRender = true;
                            _hwRenderActive = true;
                            Dispatcher.BeginInvoke(() => OverlayShaderBtn.Visibility = Visibility.Collapsed);

                            if (hw.context_reset != IntPtr.Zero)
                                _hwContextReset = Marshal.GetDelegateForFunctionPointer<retro_hw_context_reset_t>(hw.context_reset);
                            if (hw.context_destroy != IntPtr.Zero)
                                _hwContextDestroy = Marshal.GetDelegateForFunctionPointer<retro_hw_context_reset_t>(hw.context_destroy);

                            // get_current_framebuffer / get_proc_address unused for D3D11.
                            Marshal.WriteIntPtr(data, 16, IntPtr.Zero);
                            System.Diagnostics.Trace.WriteLine("SET_HW_RENDER: D3D11 device ready, context_reset deferred to post-LoadGame.");
                            return true;
                        }

                        // ── OpenGL path ──────────────────────────────────────────
                        if (hw.context_type != RETRO_HW_CONTEXT_OPENGL &&
                            hw.context_type != RETRO_HW_CONTEXT_OPENGL_CORE)
                        {
                            System.Diagnostics.Trace.WriteLine($"Rejecting context_type={hw.context_type}");
                            return false;
                        }

                        if (!InitOpenGLContext()) return false;

                        CreateFBO(640, 480);
                        _hwRenderActive = true;
                        Dispatcher.BeginInvoke(() =>
                        {
                            OverlayShaderBtn.Visibility = Visibility.Collapsed;
                        });

                        if (hw.context_reset != IntPtr.Zero)
                            _hwContextReset = Marshal.GetDelegateForFunctionPointer<retro_hw_context_reset_t>(hw.context_reset);
                        if (hw.context_destroy != IntPtr.Zero)
                            _hwContextDestroy = Marshal.GetDelegateForFunctionPointer<retro_hw_context_reset_t>(hw.context_destroy);

                        _getFramebufferDelegate = GetCurrentFramebuffer;
                        _getProcAddressDelegate  = GetProcAddress;

                        if (_getFramebufferHandle.HasValue) _getFramebufferHandle.Value.Free();
                        if (_getProcAddressHandle.HasValue)  _getProcAddressHandle.Value.Free();
                        _getFramebufferHandle = GCHandle.Alloc(_getFramebufferDelegate, GCHandleType.Normal);
                        _getProcAddressHandle  = GCHandle.Alloc(_getProcAddressDelegate,  GCHandleType.Normal);

                        Marshal.WriteIntPtr(data, 16, Marshal.GetFunctionPointerForDelegate(_getFramebufferDelegate));
                        Marshal.WriteIntPtr(data, 24, Marshal.GetFunctionPointerForDelegate(_getProcAddressDelegate));

                        // Per libretro spec: context_reset is called AFTER retro_load_game
                        // returns, not inside this callback (see StartEmulator below).
                        System.Diagnostics.Trace.WriteLine("SET_HW_RENDER: function pointers written, context_reset deferred to post-LoadGame.");
                        return true;
                    }

                    case RETRO_ENVIRONMENT_GET_PREFERRED_HW_RENDER:
                    {
                        int pref = _consoleHandler.PreferredHwContext;
                        if (pref < 0) return false;  // let the core decide
                        if (data != IntPtr.Zero) Marshal.WriteInt32(data, pref);
                        return true;
                    }

                    case RETRO_ENVIRONMENT_GET_HW_RENDER_INTERFACE:
                    {
                        if (_isVulkanHwRender && _vulkanContext != null)
                        {
                            IntPtr ifacePtr = _vulkanContext.BuildHwRenderInterface();
                            Marshal.WriteIntPtr(data, ifacePtr);
                            System.Diagnostics.Trace.WriteLine("GET_HW_RENDER_INTERFACE: Vulkan interface provided");
                            return true;
                        }
                        if (_isD3d11HwRender && _d3d11Context != null)
                        {
                            IntPtr ifacePtr = _d3d11Context.BuildHwRenderInterface();
                            if (ifacePtr == IntPtr.Zero) return false;
                            Marshal.WriteIntPtr(data, ifacePtr);
                            System.Diagnostics.Trace.WriteLine("GET_HW_RENDER_INTERFACE: D3D11 interface provided");
                            return true;
                        }
                        return false;
                    }

                    // ------------------------------------------------------------------
                    // Pixel format
                    // ------------------------------------------------------------------
                    case RETRO_ENVIRONMENT_SET_PIXEL_FORMAT:
                        _pixelFormat = (uint)Marshal.ReadInt32(data);
                        System.Diagnostics.Trace.WriteLine($"Pixel format: {_pixelFormat}");
                        return true;

                    // ------------------------------------------------------------------
                    // Core options v1 — announce
                    // ------------------------------------------------------------------
                    case RETRO_ENVIRONMENT_SET_VARIABLES:
                    {
                        if (data == IntPtr.Zero) return true;
                        _coreOptionSchema.Clear();
                        IntPtr ptr = data;
                        while (true)
                        {
                            IntPtr keyPtr = Marshal.ReadIntPtr(ptr, 0);
                            if (keyPtr == IntPtr.Zero) break;
                            string key = Marshal.PtrToStringAnsi(keyPtr) ?? "";
                            IntPtr valPtr = Marshal.ReadIntPtr(ptr, IntPtr.Size);
                            string raw = valPtr != IntPtr.Zero ? (Marshal.PtrToStringAnsi(valPtr) ?? "") : "";
                            int semi = raw.IndexOf(';');
                            // Description is the text before the semicolon; valid values are after.
                            string desc = semi >= 0 ? raw.Substring(0, semi).Trim() : key;
                            string[] validValues = semi >= 0
                                ? raw.Substring(semi + 1).Trim().Split('|').Select(v => v.Trim()).ToArray()
                                : Array.Empty<string>();

                            // Let the console handler drop values it doesn't want exposed (e.g.
                            // GameCube removes the 1x/2x Internal Resolution values that trigger
                            // dolphin_libretro's low-res cornering bug). Default is a pass-through,
                            // so non-overriding cores are unaffected.
                            if (validValues.Length > 0)
                                validValues = _consoleHandler.FilterCoreOptionValues(key, validValues);

                            if (_coreOptions.ContainsKey(key))
                            {
                                // Validate pre-seeded value — if not in the valid list, use safe fallback.
                                // Use case-insensitive comparison so "OGL"/"ogl" variants match.
                                string preSeeded = _coreOptions[key];
                                string? exactMatch = validValues.FirstOrDefault(v =>
                                    string.Equals(v, preSeeded, StringComparison.OrdinalIgnoreCase));

                                if (validValues.Length > 0 && exactMatch == null)
                                {
                                    // For GFX backend, prefer any OpenGL variant over Vulkan/D3D
                                    string? oglVariant = (key == "dolphin_gfx_backend")
                                        ? validValues.FirstOrDefault(v =>
                                            v.IndexOf("ogl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            v.IndexOf("opengl", StringComparison.OrdinalIgnoreCase) >= 0)
                                        : null;
                                    string fallback = oglVariant ?? validValues[0];
                                    System.Diagnostics.Trace.WriteLine($"Core option INVALID: {key} = '{preSeeded}' not in [{string.Join(", ", validValues)}] — using '{fallback}'");
                                    _coreOptions[key] = fallback;
                                }
                                else
                                {
                                    // Use the exact casing from the core's valid list
                                    if (exactMatch != null && exactMatch != preSeeded)
                                        _coreOptions[key] = exactMatch;
                                    System.Diagnostics.Trace.WriteLine($"Core option kept: {key} = {_coreOptions[key]}");
                                }
                                // Give the handler a chance to react to the (now validated) pre-seeded value.
                                _consoleHandler.OnVariableAnnounced(key, validValues, _coreOptions);
                            }
                            else
                            {
                                // Let the handler set the value first (e.g. dolphin_cpu_core auto-select).
                                // Only fall back to the core's own default if the handler leaves it unset.
                                _consoleHandler.OnVariableAnnounced(key, validValues, _coreOptions);
                                if (!_coreOptions.ContainsKey(key))
                                {
                                    string def = validValues.Length > 0 ? validValues[0] : raw.Trim();
                                    _coreOptions[key] = def;
                                    System.Diagnostics.Trace.WriteLine($"Core option: {key} = {def}");
                                }
                            }

                            _coreOptionSchema.Add(new CoreOptionEntry
                            {
                                Key          = key,
                                Description  = desc,
                                ValidValues  = validValues,
                                // Store the core's true default (first value in the list per
                                // libretro convention), not the currently active value — so
                                // "Reset to Defaults" actually resets to the core defaults.
                                DefaultValue = validValues.Length > 0 ? validValues[0] : ""
                            });

                            ptr += IntPtr.Size * 2;
                        }
                        return true;
                    }

                    // ------------------------------------------------------------------
                    // Core options v1 — read
                    // ------------------------------------------------------------------
                    case RETRO_ENVIRONMENT_GET_VARIABLE:
                    {
                        if (data == IntPtr.Zero) return false;
                        IntPtr keyPtr = Marshal.ReadIntPtr(data, 0);
                        if (keyPtr == IntPtr.Zero) return false;
                        string key = Marshal.PtrToStringAnsi(keyPtr) ?? "";
                        if (_coreOptions.TryGetValue(key, out string? value))
                        {
                            // Reuse an existing HGlobal if the value hasn't changed. Cores such as
                            // DOSBox Pure cache the const char* we hand back and dereference it from
                            // their own variable-change logic on later frames — freeing-and-reallocating
                            // on every call causes a use-after-free that surfaces as 0x80131506 when
                            // the CLR next scans the native heap.
                            IntPtr valPtr;
                            if (_coreOptionPtrs.TryGetValue(key, out IntPtr existing) && existing != IntPtr.Zero
                                && _coreOptionPtrValues.TryGetValue(key, out string? prev) && prev == value)
                            {
                                valPtr = existing;
                            }
                            else
                            {
                                // Value differs — allocate a fresh pointer. We deliberately leak the
                                // old one: another core thread may still be reading it. The per-session
                                // leak is tiny (a few dozen short ANSI strings). All HGlobals are
                                // released together in the close path.
                                valPtr = Marshal.StringToHGlobalAnsi(value);
                                _coreOptionPtrs[key] = valPtr;
                                _coreOptionPtrValues[key] = value ?? "";
                                _coreOptionPtrsAllocated.Add(valPtr);
                            }
                            Marshal.WriteIntPtr(data, IntPtr.Size, valPtr);
                            // Clear dirty flag here (not in GET_VARIABLE_UPDATE) so the core
                            // can call GET_VARIABLE_UPDATE multiple times during check_variables()
                            // and still see true until it has actually read a variable.
                            _coreOptionsDirty = false;
                            System.Diagnostics.Trace.WriteLine($"GET_VARIABLE: {key} -> {value}");
                            return true;
                        }
                        System.Diagnostics.Trace.WriteLine($"GET_VARIABLE: {key} -> (not found)");
                        return false;
                    }

                    case RETRO_ENVIRONMENT_GET_CORE_OPTIONS_VERSION:
                        if (data != IntPtr.Zero) Marshal.WriteInt32(data, 0);
                        return true;

                    case RETRO_ENVIRONMENT_SET_CORE_OPTIONS:
                    case RETRO_ENVIRONMENT_SET_CORE_OPTIONS_INTL:
                    case RETRO_ENVIRONMENT_SET_CORE_OPTIONS_V2:
                    case RETRO_ENVIRONMENT_SET_CORE_OPTIONS_V2_INTL:
                        // Returning false forces every core (incl. Dolphin) to fall back to the
                        // legacy SET_VARIABLES path above, which is the ONLY place
                        // IConsoleHandler.FilterCoreOptionValues runs (e.g. GameCube drops the
                        // 1x/2x Internal Resolution values). If v2 parsing is ever implemented
                        // here, replicate that filter call or the GameCube low-res bug returns.
                        return false;

                    case RETRO_ENVIRONMENT_GET_VARIABLE_UPDATE:
                        if (data != IntPtr.Zero)
                            Marshal.WriteByte(data, _coreOptionsDirty ? (byte)1 : (byte)0);
                        // Do NOT clear dirty here — clear it in GET_VARIABLE when the core
                        // actually reads a value. This matches RetroArch's behavior and prevents
                        // early clearing if the core calls GET_VARIABLE_UPDATE multiple times.
                        return true;

                    // ------------------------------------------------------------------
                    // Geometry / AV info
                    // ------------------------------------------------------------------
                    case RETRO_ENVIRONMENT_SET_GEOMETRY:
                    {
                        if (data == IntPtr.Zero) return false;
                        var geom = Marshal.PtrToStructure<retro_game_geometry>(data);
                        // For FBO-based cores (N64 etc.), recreate FBO if the reported max
                        // dimensions exceed the current FBO size.
                        if (!_consoleHandler.AllowHwSharedContext && _hwRenderActive)
                        {
                            uint needW = geom.max_width  > 0 ? geom.max_width  : geom.base_width;
                            uint needH = geom.max_height > 0 ? geom.max_height : geom.base_height;
                            if (needW > _fboWidth || needH > _fboHeight)
                                CreateFBO(needW, needH);
                        }
                        UpdateDisplayAspectRatio(geom.base_width, geom.base_height, geom.aspect_ratio);
                        return true;
                    }

                    case RETRO_ENVIRONMENT_SET_SYSTEM_AV_INFO:
                    {
                        if (data == IntPtr.Zero) return false;
                        var av = Marshal.PtrToStructure<retro_system_av_info>(data);
                        // No FBO resize needed — same reasoning as SET_GEOMETRY above.
                        UpdateDisplayAspectRatio(av.geometry.base_width, av.geometry.base_height, av.geometry.aspect_ratio);
                        // Update loop timing only if the handler doesn't force a hardware rate.
                        // (Dreamcast forces 60Hz so Flycast's per-game fps reports are ignored.)
                        if (_consoleHandler.HardwareTargetFps <= 0)
                        {
                            double newFps = av.timing.fps;
                            if (newFps > 0 && newFps <= 1000 && !double.IsNaN(newFps))
                            {
                                _targetFrameMs = 1000.0 / newFps;
                                System.Diagnostics.Trace.WriteLine($"SET_SYSTEM_AV_INFO: fps={newFps:F2} → targetFrameMs={_targetFrameMs:F2}");
                            }
                        }
                        return true;
                    }

                    case RETRO_ENVIRONMENT_SET_ROTATION:
                    {
                        if (data == IntPtr.Zero) return false;
                        uint rotation = (uint)Marshal.ReadInt32(data);  // 0=0°, 1=90°, 2=180°, 3=270°
                        System.Diagnostics.Trace.WriteLine($"[Env] SET_ROTATION={rotation} ({rotation * 90}°)");
                        _coreRotation = rotation;
                        // Re-apply AR/rotation when geometry is next reported, or force it now
                        // if geometry is already known (covers cores that set rotation after load).
                        var avInfo = _core?.AvInfo;
                        if (avInfo.HasValue)
                        {
                            var g = avInfo.Value.geometry;
                            UpdateDisplayAspectRatio(g.base_width, g.base_height, g.aspect_ratio);
                        }
                        return true;
                    }

                    // ------------------------------------------------------------------
                    // Misc
                    // ------------------------------------------------------------------
                    case RETRO_ENVIRONMENT_GET_OVERSCAN:
                        if (data != IntPtr.Zero) Marshal.WriteByte(data, 0);
                        return true;

                    case RETRO_ENVIRONMENT_GET_CAN_DUPE:
                        if (data != IntPtr.Zero) Marshal.WriteByte(data, 1);
                        return true;

                    // Core requests frontend shutdown — e.g. DOSBox Pure's "Shutdown DOSBox"
                    // menu item, or any game exit that triggers it. Queue a close on the UI
                    // thread so retro_run can return cleanly first.
                    case RETRO_ENVIRONMENT_SHUTDOWN:
                        Dispatcher.BeginInvoke(new Action(() => { try { Close(); } catch { } }));
                        return true;

                    case RETRO_ENVIRONMENT_GET_SYSTEM_DIRECTORY:
                        if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, _systemDirPtr);
                        return true;

                    case RETRO_ENVIRONMENT_GET_SAVE_DIRECTORY:
                        if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, _saveDirPtr);
                        return true;

                    case RETRO_ENVIRONMENT_GET_CONTENT_DIRECTORY:
                        if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, _contentDirPtr);
                        return true;

                    // Advertise joypad + analog + mouse + pointer capability
                    case RETRO_ENVIRONMENT_GET_INPUT_DEVICE_CAPABILITIES:
                        if (data != IntPtr.Zero)
                            Marshal.WriteInt64(data, (1L << (int)RETRO_DEVICE_JOYPAD) |
                                                     (1L << (int)RETRO_DEVICE_ANALOG)  |
                                                     (1L << (int)RETRO_DEVICE_MOUSE)   |
                                                     (1L << (int)RETRO_DEVICE_POINTER));
                        return true;

                    // GET_INPUT_MAX_USERS — tell the core we support up to 4 players.
                    case RETRO_ENVIRONMENT_GET_INPUT_MAX_USERS:
                        if (data != IntPtr.Zero) Marshal.WriteInt32(data, 4);
                        return true;

                    // GET_AUDIO_VIDEO_ENABLE = (47 | 0x10000) — core asks each frame
                    // whether audio/video are active. bit 0 = video, bit 1 = audio.
                    case RETRO_ENVIRONMENT_GET_AUDIO_VIDEO_ENABLE:
                        if (data != IntPtr.Zero) Marshal.WriteInt32(data, 0x3); // video + audio enabled
                        return true;

                    // GET_FASTFORWARDING = (49 | 0x10000) — Dolphin asks if we're fast-forwarding.
                    // data is a bool* (1 byte). Writing Int32 here would corrupt Dolphin's stack.
                    case RETRO_ENVIRONMENT_GET_FASTFORWARDING:
                        if (data != IntPtr.Zero) Marshal.WriteByte(data, 0);  // false = normal speed
                        return true;

                    // Provide Dolphin's log callback so we can see its internal diagnostics
                    case RETRO_ENVIRONMENT_GET_LOG_INTERFACE:
                        if (data != IntPtr.Zero && _logCb != null)
                            Marshal.WriteIntPtr(data, Marshal.GetFunctionPointerForDelegate(_logCb));
                        return true;

                    case RETRO_ENVIRONMENT_SET_CONTROLLER_INFO:
                        // Must return true — Reicast/Flycast uses a false response here
                        // as a signal to skip ALL sub-peripheral (VMU/Purupuru) init,
                        // causing games to report "No VMU Found".
                        return true;

                    case RETRO_ENVIRONMENT_GET_RUMBLE_INTERFACE:
                        // Provide a rumble callback so Reicast initialises maple bus
                        // sub-peripherals (VMU, Purupuru) for all ports. A missing
                        // rumble interface also blocks sub-peripheral setup.
                        // The same callback drives real XInput vibration.
                        if (data != IntPtr.Zero && _rumbleStateDelegate != null)
                            Marshal.WriteIntPtr(data, Marshal.GetFunctionPointerForDelegate(_rumbleStateDelegate));
                        return true;

                    case RETRO_ENVIRONMENT_SET_KEYBOARD_CALLBACK:
                        // struct retro_keyboard_callback { retro_keyboard_event_t callback; }
                        if (data != IntPtr.Zero)
                        {
                            IntPtr fnPtr = Marshal.ReadIntPtr(data);
                            _coreKeyboardEvent = fnPtr != IntPtr.Zero
                                ? Marshal.GetDelegateForFunctionPointer<RetroKeyboardEventDelegate>(fnPtr)
                                : null;
                        }
                        return true;

                    case RETRO_ENVIRONMENT_SET_INPUT_DESCRIPTORS:
                        ParseInputDescriptors(data);
                        return true;

                    case RETRO_ENVIRONMENT_SET_AUDIO_CALLBACK:
                    case RETRO_ENVIRONMENT_SET_SUPPORT_NO_GAME:
                    case RETRO_ENVIRONMENT_GET_USERNAME:
                    case RETRO_ENVIRONMENT_GET_LANGUAGE:
                    case RETRO_ENVIRONMENT_GET_TARGET_REFRESH_RATE:
                    case RETRO_ENVIRONMENT_SET_PERFORMANCE_LEVEL:
                    case RETRO_ENVIRONMENT_SET_SUBSYSTEM_INFO:
                        return true;

                    // ------------------------------------------------------------------
                    // Memory descriptor map — cores call this to expose their full
                    // memory layout (regions, virtual addresses, byte-order flags) to
                    // the frontend. RetroAchievementsClient consumes these for memory
                    // reads when present, falling back to legacy retro_get_memory_data
                    // when the core doesn't publish a map.
                    //
                    // Critical for NGCD achievements (and any other M68K-big-endian
                    // console exposing memory via RETRO_MEMDESC_BIGENDIAN), since
                    // those require per-region byteswap that the legacy interface
                    // doesn't carry.
                    // ------------------------------------------------------------------
                    case RETRO_ENVIRONMENT_SET_MEMORY_MAPS:
                    {
                        if (data == IntPtr.Zero) return true;
                        try
                        {
                            // struct retro_memory_map { const retro_memory_descriptor *descriptors; unsigned num_descriptors; }
                            IntPtr descPtr  = Marshal.ReadIntPtr(data, 0);
                            int    descCount = Marshal.ReadInt32(data, IntPtr.Size);
                            if (descPtr == IntPtr.Zero || descCount <= 0 || descCount > 1024)
                                return true;

                            // x64 retro_memory_descriptor layout:
                            //   uint64 flags    (offset 0)
                            //   void*  ptr      (offset 8)
                            //   size_t offset   (offset 16)
                            //   size_t start    (offset 24)
                            //   size_t select   (offset 32)
                            //   size_t disconnect (offset 40)
                            //   size_t len      (offset 48)
                            //   const char* addrspace (offset 56)
                            // Total: 64 bytes per descriptor on x64.
                            //
                            // We SKIP descriptors with `select != 0` — those use the
                            // bank-mirror addressing model (SNES, NES, Genesis) which
                            // requires (address & select) == start matching plus
                            // disconnect-clear / len-fold logic we don't implement.
                            // Those reads fall through to the legacy linear path and
                            // continue working as they did pre-descriptor-aware.
                            const int DESC_SIZE = 64;
                            var regions = new List<Services.RetroAchievementsClient.MemoryRegion>(descCount);
                            int skippedSelect = 0;
                            for (int i = 0; i < descCount; i++)
                            {
                                IntPtr d = IntPtr.Add(descPtr, i * DESC_SIZE);
                                ulong  flags  = (ulong)Marshal.ReadInt64(d, 0);
                                IntPtr ptr    = Marshal.ReadIntPtr(d, 8);
                                ulong  off    = (ulong)Marshal.ReadInt64(d, 16);
                                ulong  start  = (ulong)Marshal.ReadInt64(d, 24);
                                ulong  select = (ulong)Marshal.ReadInt64(d, 32);
                                ulong  len    = (ulong)Marshal.ReadInt64(d, 48);
                                if (select != 0) { skippedSelect++; continue; }
                                regions.Add(new Services.RetroAchievementsClient.MemoryRegion(flags, ptr, off, start, len));
                            }

                            var arr = regions.ToArray();
                            // _raClient is created later (in InitRetroAchievements which runs
                            // after LoadGame returns); buffer the regions and feed them at
                            // _raClient.Initialize time. Also feed any current instance in
                            // case the core re-publishes mid-session (e.g. after a reset).
                            _pendingMemoryRegions = arr;
                            _raClient?.SetMemoryDescriptors(arr);
                            System.Diagnostics.Trace.WriteLine(
                                $"[ENV] SET_MEMORY_MAPS captured: {arr.Length} usable region(s), {skippedSelect} skipped (select-mirror)");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Trace.WriteLine($"[ENV] SET_MEMORY_MAPS parse failed: {ex.Message}");
                        }
                        return true;
                    }

                    // baseCmd 44 is shared: SET_SERIALIZATION_QUIRKS (44) and
                    // SET_HW_SHARED_CONTEXT (44 | EXPERIMENTAL). Check the flag.
                    case RETRO_ENVIRONMENT_SET_SERIALIZATION_QUIRKS:
                        if ((cmd & 0x10000) != 0)
                            return _consoleHandler.AllowHwSharedContext;
                        // Read the core's quirk flags, then OR in our own ack.
                        //  - SINGLE_SESSION (1<<4): core's states only valid in
                        //    the same process — Kronos Saturn, DOSBox Pure, etc.
                        //    We refuse cross-launch restore for these instead of
                        //    shipping a frozen-but-"loaded" game.
                        //  - FRONT_VARIABLE_SIZE (1<<3): tell the core we accept
                        //    variable-size states. Beetle PSX gates
                        //    `enable_variable_serialization_size` on this; without
                        //    the ack it reports a stub serialize_size that no
                        //    real saved state ever matches.
                        if (data != IntPtr.Zero)
                        {
                            const ulong SINGLE_SESSION      = 1UL << 4;
                            const ulong FRONT_VARIABLE_SIZE = 1UL << 3;
                            ulong coreFlags = (ulong)Marshal.ReadInt64(data);
                            if ((coreFlags & SINGLE_SESSION) != 0)
                            {
                                _coreSingleSessionStates = true;
                                System.Diagnostics.Trace.WriteLine(
                                    $"[Quirks] Core declares SINGLE_SESSION (flags=0x{coreFlags:X16}) — startup state load disabled");
                            }
                            Marshal.WriteInt64(data, (long)(coreFlags | FRONT_VARIABLE_SIZE));
                        }
                        return true;

                    case RETRO_ENVIRONMENT_SET_HW_RENDER_CONTEXT_NEGOTIATION_INTERFACE:
                    {
                        if (data == IntPtr.Zero) return false;
                        _vulkanNegotiationPtr = data;
                        // Log what we actually received for debugging
                        uint negType = (uint)Marshal.ReadInt32(data, 0);
                        uint negVer = (uint)Marshal.ReadInt32(data, 4);
                        System.Diagnostics.Trace.WriteLine(
                            $"Stored Vulkan context negotiation interface: ptr=0x{data:X} type={negType} version={negVer}");
                        return true;
                    }

                    // FBNeo queries this to decide if save states / hiscores work.
                    // Return RETRO_SAVESTATE_CONTEXT_NORMAL (0) = standard save states.
                    case 213: // RETRO_ENVIRONMENT_GET_SAVESTATE_CONTEXT
                        if (data != IntPtr.Zero) Marshal.WriteInt32(data, 0); // NORMAL
                        return true;

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Env cmd {baseCmd} threw: {ex.Message}");
                return false;
            }
        }

        // =========================================================================
        // HW render frontend callbacks
        // =========================================================================
        // For UseEmbeddedWindow cores: return 0 (core renders to its own window).
        // For AllowHwSharedContext cores (N64/glide64): return 0; core renders to
        //   FBO 0 of the EmuThread context; OnVideoRefresh reads it back via glReadPixels.
        // For single-threaded HW cores (GameCube/Dolphin with main_cpu_thread=disabled):
        //   return _fboId; context stays current on _emuThread throughout retro_run;
        //   OnVideoRefresh reads it back via ReadBackFromCurrentContext.
        private ulong GetCurrentFramebuffer()
        {
            if (_consoleHandler.UseEmbeddedWindow)
                return 0;

            if (_consoleHandler.AllowHwSharedContext)
                return 0;   // N64: core renders to EmuThread's FBO 0

            // AMD/Intel GameCube compatibility: core renders directly to the default
            // backbuffer (FBO 0) instead of our managed FBO. Trades the GL overlay
            // for working video on drivers that don't tolerate the FBO indirection.
            if (_consoleHandler.UseDefaultFramebuffer)
                return 0;

            return _fboId;  // single-threaded HW core: GL context stays current on _emuThread
        }

        // Stubs returned to cores via GetProcAddress to block vsync and GPU sync calls
        // that would cap framerate to monitorHz÷N (48fps on 144Hz = 144÷3).
        private delegate bool wglSwapIntervalStubDelegate(int interval);
        private delegate void glFinishStubDelegate();
        private wglSwapIntervalStubDelegate? _swapIntervalStub;
        private glFinishStubDelegate?        _glFinishStub;
        private GCHandle _swapIntervalStubHandle;
        private GCHandle _glFinishStubHandle;

        private IntPtr GetProcAddress(string sym)
        {
            try
            {
                // Intercept wglSwapIntervalEXT — prevent core re-enabling vsync.
                if (sym == "wglSwapIntervalEXT")
                {
                    if (_swapIntervalStub == null)
                    {
                        _swapIntervalStub = _ => true;
                        _swapIntervalStubHandle = GCHandle.Alloc(_swapIntervalStub);
                    }
                    return Marshal.GetFunctionPointerForDelegate(_swapIntervalStub);
                }

                // Intercept glFinish — glide64 calls this to sync GPU completion, but the
                // GPU driver may wait for the next display interval before returning
                // (144Hz ÷ 3 = 48fps pattern).  We handle sync ourselves via the PBO
                // pipeline; the core does not need to stall here.
                if (sym == "glFinish")
                {
                    if (_glFinishStub == null)
                    {
                        _glFinishStub = () => { };
                        _glFinishStubHandle = GCHandle.Alloc(_glFinishStub);
                    }
                    return Marshal.GetFunctionPointerForDelegate(_glFinishStub);
                }

                IntPtr ptr = wglGetProcAddress(sym);
                if (ptr == IntPtr.Zero || ((long)ptr >= 1 && (long)ptr <= 3))
                {
                    IntPtr lib = GetOpenGL32();
                    if (lib != IntPtr.Zero) ptr = NativeMethods2.GetProcAddress(lib, sym);
                }
                return ptr;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"GetProcAddress({sym}): {ex.Message}");
                return IntPtr.Zero;
            }
        }

        // =========================================================================
        // Aspect ratio / rotation
        // =========================================================================
        private uint   _coreRotation = 0;   // value from RETRO_ENVIRONMENT_SET_ROTATION (0-3)
        private uint   _flipRotation = 0;   // user override: 0 = normal, 2 = flipped 180°
        private double _displayAr    = 0;   // current display aspect ratio (0 = unknown)
        private bool   _windowSized  = false; // true after the first auto-size

        // Arcade/NeoGeo bezel overlay: when active the WINDOW snaps to the bezel's
        // AR (~16:9) while the game keeps its own AR/rotation transform. WindowAr is
        // used for window geometry (snap + WM_SIZING); the game ScaleTransform still
        // uses _displayAr so the game renders correctly inside the bezel window.
        private bool   _bezelActive  = false;
        private double _bezelAr      = 0;
        private double WindowAr => (_bezelActive && _bezelAr > 0.01) ? _bezelAr : _displayAr;

        private void UpdateDisplayAspectRatio(uint baseWidth, uint baseHeight, float coreAr)
        {
            // Dolphin (UseEmbeddedWindow) renders directly into the HwndHost Win32 window;
            // WPF layout does not control the image size, so no transform is needed.
            if (_hwRenderActive && _consoleHandler.UseEmbeddedWindow) return;

            // All other paths (software cores + HW readback cores like N64, Vectrex) write
            // frames into the GameScreen WriteableBitmap, so normal AR correction applies.
            Dispatcher.BeginInvoke(() =>
            {
                double displayAr = _consoleHandler.GetDisplayAspectRatio(baseWidth, baseHeight, coreAr);
                if (displayAr <= 0) return;

                // For 90°/270° rotation the visual output swaps width ↔ height,
                // so invert the aspect ratios to match the post-rotation orientation.
                uint effectiveRotation = (_coreRotation + _flipRotation) % 4;
                bool rotated = effectiveRotation == 1 || effectiveRotation == 3;
                if (rotated)
                    displayAr = 1.0 / displayAr;

                _displayAr = displayAr;

                GameScreen.Width   = double.NaN;
                GameScreen.Height  = double.NaN;
                GameScreen.Stretch = Stretch.Uniform;

                double bitmapAr = baseHeight > 0 ? (double)baseWidth / baseHeight : displayAr;
                double scaleX   = displayAr / bitmapAr;

                // Apply both the AR correction scale and any rotation the core requested,
                // plus any user flip override.
                // Libretro rotation is CCW; WPF RotateTransform is CW — negate to match.
                var group = new TransformGroup();
                group.Children.Add(new ScaleTransform(scaleX, 1.0));
                if (effectiveRotation != 0)
                    group.Children.Add(new RotateTransform(-(int)effectiveRotation * 90.0));
                GameScreen.LayoutTransform = group;

                if (_isFullscreen)
                {
                    // Fullscreen: the window fills the screen, so constrain the viewport
                    // to the game AR (black bars) rather than resizing the window.
                    ApplyFullscreenAspect();
                }
                else if (!_windowSized)
                {
                    _windowSized = true;
                    AutoSizeWindowToGameAr(displayAr);   // bezel-aware when a bezel is active
                }
                else
                {
                    // Window was restored from a saved size — snap height to match the
                    // AR (the bezel AR when a bezel is active) so the game isn't stretched.
                    SnapWindowToAr(WindowAr);
                }
            });
        }

        /// <summary>
        /// Resize the emulator window so the game viewport fills a sensible default area.
        /// Targets 2× native resolution, clamped to 85% of the screen working area.
        /// </summary>
        private void AutoSizeWindowToGameAr(double displayAr)
        {
            var avInfo = _core?.AvInfo;
            if (!avInfo.HasValue) return;

            var geom = avInfo.Value.geometry;
            if (geom.base_width == 0 || geom.base_height == 0) return;

            // Chrome: title bar (32) + status bar + border — measure live so it's exact.
            double chromeH = ActualHeight - GameViewport.ActualHeight;

            var screen = System.Windows.SystemParameters.WorkArea;

            // Target 2× native pixels for the game viewport, then scale down if needed.
            // For rotated games (90°/270°), swap native dimensions so the window is portrait.
            uint effectiveRotation = (_coreRotation + _flipRotation) % 4;
            bool rotated = effectiveRotation == 1 || effectiveRotation == 3;
            double nativeW = (rotated ? geom.base_height : geom.base_width)  * 2.0;
            double nativeH = (rotated ? geom.base_width  : geom.base_height) * 2.0;

            // Apply the display AR correction (same scaleX used in LayoutTransform).
            double bitmapAr = nativeH > 0 ? nativeW / nativeH : displayAr;
            double scaleX   = displayAr / bitmapAr;
            double gameW    = nativeW * scaleX;
            double gameH    = nativeH;

            double maxW = screen.Width  * 0.85;
            double maxH = (screen.Height - chromeH) * 0.85;

            // Scale down uniformly if too large.
            if (gameW > maxW || gameH > maxH)
            {
                double scale = Math.Min(maxW / gameW, maxH / gameH);
                gameW *= scale;
                gameH *= scale;
            }

            if (_bezelActive && _bezelAr > 0.01)
            {
                // Bezel active: keep the game's height and widen the window to the bezel's
                // frame AR (~16:9) so the game keeps its size and vertical games aren't
                // squashed into a sliver. The game centres in the wider viewport.
                Width  = Math.Max(Math.Min(gameH * _bezelAr, screen.Width * 0.95), 320);
                Height = Math.Max(gameH + chromeH, 200);
            }
            else
            {
                Width  = Math.Max(gameW, 320);
                Height = Math.Max(gameH + chromeH, 200);
            }
        }

        /// <summary>
        /// Adjusts a restored window size so it respects the game's aspect ratio.
        /// Keeps the current width and recalculates the height to match the AR.
        /// </summary>
        private void SnapWindowToAr(double displayAr)
        {
            if (displayAr <= 0) return;

            double chromeH = ActualHeight - GameViewport.ActualHeight;
            double gameW   = Width;
            double gameH   = gameW / displayAr;

            Height = Math.Max(gameH + chromeH, 200);
        }

        // Maintain the game's aspect ratio in fullscreen. The window is forced to the
        // screen's AR, so center GameViewport at the game AR and let GameBorder's black
        // background form the letterbox/pillarbox bars. Both the software WriteableBitmap
        // and the HW overlay render into GameViewport, so this corrects every core path
        // where the AR is known (WindowAr > 0). Windowed mode shapes the window itself,
        // so there the viewport simply fills it.
        private void ApplyFullscreenAspect()
        {
            if (_isFullscreen)
            {
                double ar    = WindowAr;
                double availW = GameBorder.ActualWidth;
                double availH = GameBorder.ActualHeight;
                if (ar > 0.01 && availW > 1 && availH > 1)
                {
                    double rectW, rectH;
                    if (availW / availH > ar) { rectH = availH; rectW = availH * ar; } // pillarbox
                    else                      { rectW = availW; rectH = availW / ar; } // letterbox
                    GameViewport.HorizontalAlignment = HorizontalAlignment.Center;
                    GameViewport.VerticalAlignment   = VerticalAlignment.Center;
                    GameViewport.Width  = rectW;
                    GameViewport.Height = rectH;
                }
                else
                {
                    // AR/size not known yet — fill for now; re-applied when AR arrives.
                    GameViewport.HorizontalAlignment = HorizontalAlignment.Stretch;
                    GameViewport.VerticalAlignment   = VerticalAlignment.Stretch;
                    GameViewport.Width  = double.NaN;
                    GameViewport.Height = double.NaN;
                }
            }
            else
            {
                GameViewport.HorizontalAlignment = HorizontalAlignment.Stretch;
                GameViewport.VerticalAlignment   = VerticalAlignment.Stretch;
                GameViewport.Width  = double.NaN;
                GameViewport.Height = double.NaN;
            }

            // Re-place the separate HW overlay window(s) over the new viewport rect after
            // layout settles. Embedded HwndHost children resize with the viewport directly.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                try { RepositionOverlayWindow(); } catch { }
                try { RepositionVulkanHud(); } catch { }
            }));
        }

        // =========================================================================
        // Video refresh — software cores
        // =========================================================================
        private void OnVideoRefresh(IntPtr data, uint width, uint height, UIntPtr pitch)
        {
            // Track last frame dimensions for recording (all paths including Vulkan swapchain)
            if (width > 0 && height > 0) { _lastFrameWidth = width; _lastFrameHeight = height; }

            if (_hwRenderActive)
            {
                // ── Vulkan path ──────────────────────────────────────────────
                if (_isVulkanHwRender && _vulkanContext != null)
                {
                    if (_vulkanContext.HasSwapchain)
                    {
                        // Direct GPU presentation — no CPU readback
                        if (_vulkanContext.PresentFrame(width, height))
                        {
                            _vulkanPresenting = true;
                            System.Threading.Interlocked.Increment(ref _frameCount);

                        }
                        return;
                    }

                    // Fallback: CPU readback to WriteableBitmap
                    if (_hwVideoPending) return;

                    var (pixels, w, h) = _vulkanContext.ReadbackFrame(width, height);
                    if (pixels != null && w > 0 && h > 0)
                    {
                        System.Threading.Interlocked.Increment(ref _frameCount);

                        uint capturedW = (uint)w, capturedH = (uint)h;

                        _hwVideoPending = true;
                        Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                if (_bitmap == null || _videoWidth != capturedW || _videoHeight != capturedH || _bitmap.Format != PixelFormats.Bgra32)
                                {
                                    _videoWidth = capturedW; _videoHeight = capturedH;
                                    _bitmap = new WriteableBitmap((int)capturedW, (int)capturedH, 96, 96, PixelFormats.Bgra32, null);
                                    GameScreen.Source = _bitmap;
                                    UpdateDisplayAspectRatio(capturedW, capturedH, _core?.AvInfo.geometry.aspect_ratio ?? 0f);
                                    UpdateShaderScreenHeight(capturedH);
                                    ApplyGameScreenScalingMode(capturedW, capturedH);
                                }
                                _bitmap.Lock();
                                Marshal.Copy(pixels, 0, _bitmap.BackBuffer, (int)(capturedW * capturedH * 4));
                                _bitmap.AddDirtyRect(new Int32Rect(0, 0, (int)capturedW, (int)capturedH));
                                _bitmap.Unlock();
                            }
                            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Vulkan] Bitmap: {ex.Message}"); }
                            finally { _hwVideoPending = false; }
                        }, DispatcherPriority.Render);
                    }
                    return;
                }

                // ── Direct3D 11 path (LRPS2) ─────────────────────────────────
                // The core left its output bound on PS-SRV-slot-0; copy it into
                // our shared texture (emu thread, our immediate context), then
                // present it through a WPF D3DImage backed by the D3D9 view of
                // that same shared surface (UI thread).
                if (_isD3d11HwRender && _d3d11Context != null)
                {
                    // Preferred path: present straight to the DXGI swapchain on the
                    // emu thread. Present(1,…) vsync-paces us here — no UI dispatch,
                    // no D3DImage copy. Falls through to the D3DImage path below only
                    // when the swapchain couldn't be created.
                    if (_d3d11Context.HasSwapchain)
                    {
                        if (_d3d11Context.PresentFrame())
                        {
                            System.Threading.Interlocked.Increment(ref _frameCount);
                            // Route the HUD (FPS pill, cog, achievement toasts, pause
                            // effect) onto the transparent overlay window above the
                            // swapchain — same flag the Vulkan/GL overlay paths use.
                            _vulkanPresenting = true;
                        }
                        return;
                    }

                    if (_d3d11VideoPending) return;
                    bool captured = _d3d11Context.CaptureCoreFrame();
                    uint cw = width, ch = height;
                    _d3d11VideoPending = true;
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                            // Cap the present surface to the monitor. The core can
                            // render far above display res (PS2 8x native = 5120x3584
                            // = 73MB/frame); copying that whole surface through the
                            // D3DImage bridge every frame is the present bottleneck
                            // and halves the screen to 30fps. The monitor can't show
                            // those extra pixels anyway, and the blit's linear sampler
                            // supersamples the full-res frame down into the capped
                            // surface for free — so detail is kept, copy cost isn't.
                            var (pw, ph) = CapPresentToDisplay((int)cw, (int)ch);
                            // Log only when the source size changes — this runs every
                            // frame, so an unconditional log floods emulator.log.
                            if ((int)cw != _lastCapSrcW || (int)ch != _lastCapSrcH)
                            {
                                _lastCapSrcW = (int)cw; _lastCapSrcH = (int)ch;
                                if (pw != (int)cw || ph != (int)ch)
                                    System.Diagnostics.Trace.WriteLine(
                                        $"[D3D11] capping present {cw}x{ch} -> {pw}x{ph} (monitor-bound)");
                            }
                            bool recreated = _d3d11Context.EnsurePresentTarget(pw, ph, hwnd);
                            if (recreated)
                            {
                                _d3dImage ??= new System.Windows.Interop.D3DImage();
                                _d3dImage.Lock();
                                _d3dImage.SetBackBuffer(System.Windows.Interop.D3DResourceType.IDirect3DSurface9,
                                                        _d3d11Context.D9SurfacePointer);
                                _d3dImage.Unlock();
                                GameScreen.Source = _d3dImage;
                                _videoWidth = cw; _videoHeight = ch;
                                UpdateDisplayAspectRatio(cw, ch, _core?.AvInfo.geometry.aspect_ratio ?? 0f);
                                ApplyGameScreenScalingMode(cw, ch);
                            }
                            if (captured && _d3dImage != null && _d3dImage.IsFrontBufferAvailable
                                && _d3d11Context.D9SurfacePointer != IntPtr.Zero)
                            {
                                _d3dImage.Lock();
                                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _d3dImage.PixelWidth, _d3dImage.PixelHeight));
                                _d3dImage.Unlock();
                                System.Threading.Interlocked.Increment(ref _frameCount);
                            }
                        }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[D3D11] present: {ex.Message}"); }
                        finally { _d3d11VideoPending = false; }
                    }, DispatcherPriority.Render);
                    return;
                }

                // data == (void*)-1 means RETRO_HW_FRAME_BUFFER_VALID.

                // GL overlay: blit FBO → overlay window back buffer → SwapBuffers (zero CPU readback)
                if (_glOverlayDC != IntPtr.Zero && _consoleHandler.UseGLOverlay)
                {
                    uint rw = width  > 0 ? width  : _fboWidth;
                    uint rh = height > 0 ? height : _fboHeight;
                    if (rw > 0 && rh > 0)
                    {
                        bool blitOk = false;
                        try
                        {
                            // Switch context to overlay DC for presentation
                            bool mc = wglMakeCurrent(_glOverlayDC, _hglrc);
                            if (_glOverlayTraceCount < 3)
                            {
                                System.Diagnostics.Trace.WriteLine($"[GL Overlay] Blit frame {_glOverlayTraceCount}: {rw}x{rh} → {_glOverlayWidth}x{_glOverlayHeight} fbo={_fboId} mc={mc}");
                                _glOverlayTraceCount++;
                            }

                            if (mc)
                            {
                                // Blit from our FBO to FBO 0 (overlay window's back buffer)
                                _glBindFramebuffer!(GL_READ_FRAMEBUFFER, _fboId);
                                _glBindFramebuffer!(GL_DRAW_FRAMEBUFFER, 0);
                                // Dolphin renders top-down into the FBO — no Y flip needed
                                _glBlitFramebuffer!(0, 0, (int)rw, (int)rh,
                                                   0, 0, _glOverlayWidth, _glOverlayHeight,
                                                   GL_COLOR_BUFFER_BIT, GL_LINEAR);
                                _glBindFramebuffer!(GL_READ_FRAMEBUFFER, 0);
                                _glBindFramebuffer!(GL_DRAW_FRAMEBUFFER, 0);

                                SwapBuffers(_glOverlayDC);

                                // Switch context back to offscreen DC for next retro_run
                                wglMakeCurrent(_hdc, _hglrc);

                                System.Threading.Interlocked.Increment(ref _frameCount);
                                blitOk = true;
                            }
                            else
                            {
                                // wglMakeCurrent failed — restore context and fall through to readback
                                wglMakeCurrent(_hdc, _hglrc);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Trace.WriteLine($"[GL Overlay] Blit error: {ex.Message}");
                            wglMakeCurrent(_hdc, _hglrc);
                        }
                        if (blitOk) return;
                        // Fall through to readback path if blit failed
                    }
                }

                if (_consoleHandler.UseEmbeddedWindow)
                {
                    // Dolphin: rendered directly to HwndHost FBO 0 on its EmuThread. Just present.
                    if (!_vsyncDisabled)
                    {
                        var swapInterval = GetGLProc<wglSwapIntervalEXTDelegate>("wglSwapIntervalEXT");
                        if (swapInterval != null) swapInterval(0);
                        _vsyncDisabled = true;
                    }



                    try { if (data != IntPtr.Zero && _hdc != IntPtr.Zero) SwapBuffers(_hdc); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"SwapBuffers: {ex.Message}"); }
                }
                else if (_consoleHandler.AllowHwSharedContext)
                {
                    // Called from the EmuThread with its own GL context current.
                    // N64/glide64: GetCurrentFramebuffer returned 0; core rendered to FBO 0.
                    // _fboId == 0 here, so ReadBackFromCurrentContext reads from FBO 0.
                    uint rw = width  > 0 ? width  : _fboWidth;
                    uint rh = height > 0 ? height : _fboHeight;
                    ReadBackFromCurrentContext(_fboId, rw, rh);
                }
                else
                {
                    // Single-threaded HW core path.
                    // UseFullFboReadback=true (vecx): renders to full FBO square and relies
                    //   on aspect_ratio for display — read the entire FBO.
                    // UseFullFboReadback=false (default — PSP, GameCube, etc.): renders at
                    //   exactly the callback dimensions; use width/height from the callback.
                    uint rw = _consoleHandler.UseFullFboReadback
                        ? _fboWidth
                        : (width  > 0 ? width  : _fboWidth);
                    uint rh = _consoleHandler.UseFullFboReadback
                        ? _fboHeight
                        : (height > 0 ? height : _fboHeight);
                    // AMD/Intel GameCube compatibility: core rendered into FBO 0 instead
                    // of our managed FBO (mirrors GetCurrentFramebuffer's branch).
                    uint sourceFbo = _consoleHandler.UseDefaultFramebuffer ? 0 : _fboId;
                    ReadBackFromCurrentContext(sourceFbo, rw, rh);
                }
                return;
            }
            if (data == IntPtr.Zero) return;
            try
            {
                PixelFormat pixFmt = _pixelFormat == RETRO_PIXEL_FORMAT_XRGB8888
                    ? PixelFormats.Bgr32 : PixelFormats.Bgr565;
                int bpp       = _pixelFormat == RETRO_PIXEL_FORMAT_XRGB8888 ? 4 : 2;
                int srcPitch  = (int)(ulong)pitch;
                int rowBytes  = (int)width * bpp;
                int frameSize = srcPitch * (int)height;

                // Drop this frame if the UI thread is still processing the previous one.
                // This prevents BeginInvoke from queueing unlimited frames AND prevents
                // writing new data into the buffer while the UI thread is reading it.
                if (_videoPending) return;

                // Reuse the frame buffer — resize only when resolution changes.
                // Avoids Large Object Heap allocation every frame (was 1.2MB/frame at
                // 640×480 XRGB8888, causing gen2 GC pauses and stuttering).
                if (_videoFrameBuffer.Length != frameSize)
                {
                    _videoFrameBuffer = new byte[frameSize];
                }
                Marshal.Copy(data, _videoFrameBuffer, 0, frameSize);

                // Recording: queue the raw frame for encoding.
                // If the core's row pitch has padding (srcPitch > rowBytes), we must
                // strip it — FFmpeg rawvideo expects tightly packed rows.
                if (_recordingService is Services.RecordingService ffmpegRec && ffmpegRec.IsRecording)
                {
                    if (srcPitch == rowBytes)
                    {
                        ffmpegRec.QueueVideoFrame(_videoFrameBuffer, frameSize);
                    }
                    else
                    {
                        int packedSize = rowBytes * (int)height;
                        if (_recPackedBuffer == null || _recPackedBuffer.Length < packedSize)
                            _recPackedBuffer = new byte[packedSize];
                        for (int row = 0; row < (int)height; row++)
                            Buffer.BlockCopy(_videoFrameBuffer, row * srcPitch, _recPackedBuffer, row * rowBytes, rowBytes);
                        ffmpegRec.QueueVideoFrame(_recPackedBuffer, packedSize);
                    }
                }

                // Downloaded slang shader (librashader): GPU pass on THIS (emu) thread.
                // The shaded BGRA output goes to the UI closure below; any failure
                // falls through to the untouched raw path.
                // Honor a toggle from the UI thread: dispose+reinit happen HERE on the
                // emu thread so the renderer is never touched from two threads at once.
                if (_shaderResetRequested)
                {
                    _shaderResetRequested = false;
                    var oldR = _shaderRenderer; _shaderRenderer = null;
                    oldR?.Dispose();
                }
                byte[]? shadedBuf = null; int shadedW = 0, shadedH = 0;
                if (_slangPresetPath != null && !_slangInitFailed)
                {
                    EnsureShaderRenderer();
                    if (_shaderRenderer is { IsReady: true })
                        shadedBuf = _shaderRenderer.Process(
                            _videoFrameBuffer, (int)width, (int)height, srcPitch,
                            _pixelFormat == RETRO_PIXEL_FORMAT_XRGB8888,
                            out shadedW, out shadedH);
                }

                _videoPending = true;

                // Capture locals for the closure — fields may change on next frame.
                byte[] buf      = _videoFrameBuffer;
                int    sp       = srcPitch;
                int    rBytes   = rowBytes;
                uint   w = width, h = height;
                PixelFormat pf  = pixFmt;
                byte[]? shaded  = shadedBuf;
                int     shW = shadedW, shH = shadedH;

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (shaded != null)
                        {
                            // Shaded output: BGRA32, tightly packed, at the shader's
                            // output resolution. AR/geometry still derive from the
                            // NATIVE w/h — the uniform upscale preserves the ratio.
                            if (_bitmap == null || _bitmap.PixelWidth != shW || _bitmap.PixelHeight != shH || _bitmap.Format != PixelFormats.Bgra32)
                            {
                                _videoWidth = w; _videoHeight = h;
                                _bitmap = new WriteableBitmap(shW, shH, 96, 96, PixelFormats.Bgra32, null);
                                GameScreen.Source = _bitmap;
                                UpdateDisplayAspectRatio(w, h, _core?.AvInfo.geometry.aspect_ratio ?? 0f);
                                UpdateShaderScreenHeight(h);
                                ApplyGameScreenScalingMode(w, h);
                            }
                            _bitmap.Lock();
                            try
                            {
                                int destPitch = _bitmap.BackBufferStride;
                                int srcStride = shW * 4;
                                for (int y = 0; y < shH; y++)
                                    Marshal.Copy(shaded, y * srcStride, _bitmap.BackBuffer + y * destPitch, srcStride);
                                _bitmap.AddDirtyRect(new Int32Rect(0, 0, shW, shH));
                            }
                            finally { _bitmap.Unlock(); }
                        }
                        else
                        {
                            if (_bitmap == null || _videoWidth != w || _videoHeight != h || _bitmap.Format != pf)
                            {
                                _videoWidth = w; _videoHeight = h;
                                _bitmap = new WriteableBitmap((int)w, (int)h, 96, 96, pf, null);
                                GameScreen.Source = _bitmap;
                                UpdateDisplayAspectRatio(w, h, _core?.AvInfo.geometry.aspect_ratio ?? 0f);
                                UpdateShaderScreenHeight(h);
                                ApplyGameScreenScalingMode(w, h);
                            }
                            _bitmap.Lock();
                            try
                            {
                                int destPitch = _bitmap.BackBufferStride;
                                for (int y = 0; y < (int)h; y++)
                                    Marshal.Copy(buf, y * sp, _bitmap.BackBuffer + y * destPitch, rBytes);
                                _bitmap.AddDirtyRect(new Int32Rect(0, 0, (int)w, (int)h));
                            }
                            finally { _bitmap.Unlock(); }
                        }
                        // Display cadence: count the frame only now that it has
                        // actually been painted (frames dropped at the _videoPending
                        // guard above never reach here).
                        System.Threading.Interlocked.Increment(ref _frameCount);
                    }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Video UI: {ex.Message}"); }
                    finally { _videoPending = false; }
                }, DispatcherPriority.Render);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Video refresh: {ex.Message}"); }
        }

        /// <summary>
        /// Lazily creates the librashader renderer on the emu thread the first time a
        /// downloaded preset is active. On failure sets _slangInitFailed so we don't
        /// retry every frame (the caller then falls back to the raw/built-in path).
        /// </summary>
        private void EnsureShaderRenderer()
        {
            if (_shaderRenderer != null || _slangInitFailed || _slangPresetPath == null) return;
            try
            {
                string dll = System.IO.Path.Combine(AppPaths.GetFolder("Shaders"), "librashader.dll");
                var r = new Effects.Librashader.ShaderRenderer();
                if (r.Initialize(dll, _slangPresetPath))
                {
                    _shaderRenderer = r;
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine($"[Shader] librashader init failed: {r.LastError}");
                    r.Dispose();
                    _slangInitFailed = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Shader] renderer init exception: {ex.Message}");
                _slangInitFailed = true;
            }
        }

        // =========================================================================
        // Audio
        // =========================================================================
        private void OnAudioSample(short left, short right)
        {
            try { _audioPlayer?.QueueSample(left, right); }
            catch { }
        }

        // Reused audio staging buffer — avoids a heap allocation every frame.
        private byte[] _audioBatchBuffer = new byte[4096];

        private UIntPtr OnAudioSampleBatch(IntPtr data, UIntPtr frames)
        {
            if (data == IntPtr.Zero) return frames;
            try
            {
                // Native data is already interleaved 16-bit stereo PCM — copy straight to bytes.
                int byteCount = (int)(uint)frames * 4; // 2 channels × 2 bytes
                if (_audioBatchBuffer.Length < byteCount)
                    _audioBatchBuffer = new byte[byteCount * 2]; // grow with headroom, rare
                Marshal.Copy(data, _audioBatchBuffer, 0, byteCount);
                _audioPlayer?.QueueBatchBytes(_audioBatchBuffer, byteCount);
                _recordingService?.QueueAudioSamples(_audioBatchBuffer, byteCount);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Audio batch: {ex.Message}"); }
            return frames;
        }

        // =========================================================================
        // Core log interface
        // =========================================================================
        // NOTE: fires on native core threads — Trace.WriteLine is safe because
        // App.OnStartup replaces DefaultTraceListener with ConsoleTraceListener.
        private int _shaderCompileCount;
        private DateTime _shaderLastSeenUtc;
        private int _zeroFpsSeconds;

        private void OnRetroLog(uint level, IntPtr fmtPtr,
            IntPtr a0, IntPtr a1, IntPtr a2, IntPtr a3)
        {
            try
            {
                string fmt = Marshal.PtrToStringAnsi(fmtPtr) ?? "";
                string msg = FormatCoreLog(fmt, a0, a1, a2, a3);
                string[] labels = { "DEBUG", "INFO", "WARN", "ERROR" };
                string tag = level < (uint)labels.Length ? labels[level] : $"L{level}";
                System.Diagnostics.Trace.WriteLine($"[CORE {tag}] {msg.TrimEnd('\n', '\r')}");

                // Surface shader-compile bursts as a HUD status — bursts happen on
                // first game launch AND mid-game when a new scene triggers shaders
                // that aren't in the program cache yet, both manifesting as a black
                // screen / 0 fps stall. The status reassures the user that the game
                // isn't frozen.
                if (msg.Contains("Compiling new", StringComparison.Ordinal))
                {
                    int n = System.Threading.Interlocked.Increment(ref _shaderCompileCount);
                    _shaderLastSeenUtc = DateTime.UtcNow;
                    _transientMsg    = $"Compiling shaders ({n})…";
                    _transientExpiry = DateTime.Now.AddSeconds(8);
                }
            }
            catch { }
        }

        /// <summary>
        /// Minimal printf formatter for core log messages.
        /// Handles the common specifiers cores use (%s, %d, %i, %u, %x, %X, %f, %g, %e, %ld, %lu, %02d, etc.).
        /// Every matched specifier MUST advance argIdx — otherwise a skipped spec (e.g. %f) would
        /// leave a double's bit pattern sitting in the next args[] slot and a following %s would
        /// feed that bit pattern into Marshal.PtrToStringAnsi as a wild pointer, AV in native
        /// code, and corrupt CLR state (0x80131506 on next GC scan).
        /// Covers up to 4 varargs (R8, R9, and first two stack slots in x64 Windows ABI; doubles
        /// in varargs positions are mirrored into the integer register per the MS x64 ABI).
        /// </summary>
        private static string FormatCoreLog(string fmt, IntPtr a0, IntPtr a1, IntPtr a2, IntPtr a3)
        {
            if (!fmt.Contains('%')) return fmt;

            var args = new IntPtr[] { a0, a1, a2, a3 };
            int argIdx = 0;

            return System.Text.RegularExpressions.Regex.Replace(fmt,
                @"%%|%[-+0 #]*\d*(?:\.\d+)?(?:hh?|ll?|[Lqjzt])?([diouxXscpfFgGeE])",
                m =>
                {
                    if (m.Value == "%%") return "%";
                    if (argIdx >= args.Length) return m.Value;

                    IntPtr arg = args[argIdx++];
                    char type = m.Groups[1].Value[0];
                    string spec = m.Value;

                    // Honour width/precision from the original specifier where practical.
                    // Extract optional width (e.g. "02" from "%02d").
                    string? widthStr = System.Text.RegularExpressions.Regex.Match(spec, @"0?(\d+)").Groups[1].Value;
                    int width = int.TryParse(widthStr, out int w) ? w : 0;
                    bool zeroPad = spec.Contains('0') && !spec.Contains('-');

                    return type switch
                    {
                        's' => Marshal.PtrToStringAnsi(arg) ?? "(null)",
                        'd' or 'i' => PadNum(((long)arg).ToString(), width, zeroPad),
                        'u'        => PadNum(((ulong)arg).ToString(), width, zeroPad),
                        'x'        => PadNum(((ulong)arg).ToString("x"), width, zeroPad),
                        'X'        => PadNum(((ulong)arg).ToString("X"), width, zeroPad),
                        'p'        => "0x" + ((ulong)arg).ToString("x16"),
                        'c'        => ((char)(byte)arg).ToString(),
                        // Windows x64 variadic ABI: floats/doubles are passed in XMM AND mirrored
                        // into the corresponding integer register / stack slot. Reinterpret the
                        // 8-byte slot as an IEEE-754 double.
                        'f' or 'F' or 'g' or 'G' or 'e' or 'E' =>
                            System.BitConverter.Int64BitsToDouble((long)arg).ToString("G"),
                        _          => m.Value
                    };
                });
        }

        private static string PadNum(string s, int width, bool zeroPad)
            => width > 0 ? (zeroPad ? s.PadLeft(width, '0') : s.PadLeft(width)) : s;

        // =========================================================================
        // Input
        // =========================================================================
        private void OnInputPoll()
        {
            PollDiskSwap();
            PollHotkeys();
            // Decrement the FDS L-injection counter once per polled frame so the
            // simulated press lasts a fixed wall-clock duration regardless of how
            // many id-queries the core makes in that frame.
            if (_fdsSideChangeFrames > 0) _fdsSideChangeFrames--;

            // Deferred disc-tray re-insert: countdown from set_image_index to the
            // matching set_eject_state(false). When it hits zero, fire the insert.
            if (_diskInsertPendingFrames > 0)
            {
                _diskInsertPendingFrames--;
                if (_diskInsertPendingFrames == 0 && _diskSetEjectState != null)
                {
                    try
                    {
                        _diskSetEjectState.Invoke(false);
                        System.Diagnostics.Trace.WriteLine("Disk swap: deferred insert fired (eject false)");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"Disk swap deferred insert failed: {ex.Message}");
                    }
                }
            }
        }

        // Diagnostic state for disk-swap polling — log only on transitions to keep
        // the log readable while still surfacing why the chord isn't firing.
        private bool _diskDiagPrevCtrlA;
        private bool _diskDiagPrevCtrlB;
        private bool _diskDiagPrevKeyA;
        private bool _diskDiagPrevKeyB;
        private bool _diskDiagPrevControllerConnected;
        private long _diskDiagLastHeartbeat;

        // EmuTV quit chord (L3+R3+L2+R2 held ~1.5s) — frame-counted on the EmuThread.
        private int _quitChordFrames;
        private bool _quitChordFired;
        private const int QuitChordFramesRequired = 90; // ~1.5s at 60fps

        // Detect disk-swap chord on the EmuThread. Two halves both required:
        //   • Controller chord — defaults to L3 + Start when user has no binding,
        //     otherwise the user-configured pair.
        //   • Keyboard chord — only active when the user has bound both halves.
        // Rising-edge so a held chord fires once. Always runs regardless of
        // _diskControlAvailable — SwapToNextDisk shows a status message in the
        // not-supported case, which is the user-facing signal that the chord
        // registered but the core didn't expose disk control for this game.
        // Map a libretro JOYPAD id (0-15) to the raw XInput wButtons bitmask. Returns
        // 0 for ids that aren't a wButtons bit (triggers, analog directions, unknown).
        // For mask=0 we fall back to ControllerManager.GetButtonState — that path
        // works for L2/R2 trigger thresholds and analog directions, which DO populate
        // _buttonStates regardless of mapping.
        private static ushort LibretroIdToRawXInputMask(uint id) => id switch
        {
            0  => 0x2000, // B
            1  => 0x8000, // Y
            2  => 0x0020, // Back/Select
            3  => 0x0010, // Start
            4  => 0x0001, // DPad Up
            5  => 0x0002, // DPad Down
            6  => 0x0004, // DPad Left
            7  => 0x0008, // DPad Right
            8  => 0x1000, // A
            9  => 0x4000, // X
            10 => 0x0100, // Left Shoulder
            11 => 0x0200, // Right Shoulder
            14 => 0x0040, // Left Thumb (L3)
            15 => 0x0080, // Right Thumb (R3)
            _  => 0,      // 12/13 triggers, 16-23 analog dirs
        };

        private static bool IsChordHalfHeld(ControllerManager ctl, uint libretroId)
        {
            ushort mask = LibretroIdToRawXInputMask(libretroId);
            if (mask != 0) return ctl.IsRawXInputButtonDown(mask);
            // Fallback for triggers/analog dirs — these don't have wButtons bits
            // but ControllerManager populates _buttonStates regardless of mapping
            // for those specific ids (lines 332-335 in ControllerManager.cs).
            return libretroId < 24 && ctl.GetButtonState(libretroId);
        }

        // Raw, mapping-independent "is this libretro button physically held", for
        // frontend hotkeys. Triggers (L2/R2 = 12/13) read the raw analog value rather
        // than _buttonStates — which is empty for ids a console doesn't map (e.g. SNES
        // has no L2/R2, so GetButtonState(13) is always false). Everything else uses
        // the raw wButtons bit.
        private static bool IsRawButtonHeld(ControllerManager ctl, uint libretroId)
        {
            if (libretroId == 12) return ctl.IsRawTriggerDown(false); // L2
            if (libretroId == 13) return ctl.IsRawTriggerDown(true);  // R2
            ushort mask = LibretroIdToRawXInputMask(libretroId);
            return mask != 0 && ctl.IsRawXInputButtonDown(mask);
        }

        private void PollDiskSwap()
        {
            // Controller chord: user binding takes precedence; otherwise default L3+Start.
            uint cA = _diskSwapCtrlA != uint.MaxValue ? _diskSwapCtrlA : DEFAULT_DISK_SWAP_CTRL_A;
            uint cB = _diskSwapCtrlB != uint.MaxValue ? _diskSwapCtrlB : DEFAULT_DISK_SWAP_CTRL_B;
            var ctl0 = _controllers[0];
            bool connected = ctl0 != null && ctl0.IsConnected;
            // Read raw XInput state so the chord works regardless of the per-console
            // controller mapping. ControllerManager.GetButtonState reads the *mapped*
            // libretro state, which is false for buttons the console doesn't use
            // (e.g. NES/FDS doesn't map L3, so GetButtonState(14) is always false even
            // when L3 is physically pressed).
            bool ctrlA = connected && IsChordHalfHeld(ctl0!, cA);
            bool ctrlB = connected && IsChordHalfHeld(ctl0!, cB);
            bool ctrlChord = ctrlA && ctrlB;

            // Keyboard chord — both halves must be bound.
            bool keyChord = _diskSwapKeyA >= 0 && _diskSwapKeyB >= 0
                            && _diskSwapKeyAHeld && _diskSwapKeyBHeld;

            // ── Diagnostics ───────────────────────────────────────────────────
            // Heartbeat once every ~5 seconds so we can confirm OnInputPoll is
            // running at all. Without this, "no log" means either the chord
            // didn't fire OR the entire polling code path is dead.
            long now = _retroRunCallCount;
            if (now - _diskDiagLastHeartbeat >= 300) // ~5s at 60fps
            {
                _diskDiagLastHeartbeat = now;
                System.Diagnostics.Trace.WriteLine(
                    $"[DiskDiag] poll alive runId={now} ctlConnected={connected} " +
                    $"chord-bind cA={cA}(L3=14) cB={cB}(Start=3) " +
                    $"key-bind kA={_diskSwapKeyA} kB={_diskSwapKeyB} " +
                    $"current ctrlA={ctrlA} ctrlB={ctrlB} keyA={_diskSwapKeyAHeld} keyB={_diskSwapKeyBHeld} " +
                    $"console={_game?.Console} diskCtrl={_diskControlAvailable}");
            }

            // Log every controller-state transition for the chord halves. If you press
            // L3 alone you'll see "ctrlA edge=Down"; if nothing logs, the controller
            // isn't reaching us OR the wrong button id is being polled.
            if (connected != _diskDiagPrevControllerConnected)
            {
                System.Diagnostics.Trace.WriteLine($"[DiskDiag] controller[0] connected={connected}");
                _diskDiagPrevControllerConnected = connected;
            }
            if (ctrlA != _diskDiagPrevCtrlA)
            {
                System.Diagnostics.Trace.WriteLine($"[DiskDiag] chord half A (id={cA}) edge={(ctrlA ? "Down" : "Up")}");
                _diskDiagPrevCtrlA = ctrlA;
            }
            if (ctrlB != _diskDiagPrevCtrlB)
            {
                System.Diagnostics.Trace.WriteLine($"[DiskDiag] chord half B (id={cB}) edge={(ctrlB ? "Down" : "Up")}");
                _diskDiagPrevCtrlB = ctrlB;
            }
            if (_diskSwapKeyAHeld != _diskDiagPrevKeyA)
            {
                System.Diagnostics.Trace.WriteLine($"[DiskDiag] key half A (key={_diskSwapKeyA}) edge={(_diskSwapKeyAHeld ? "Down" : "Up")}");
                _diskDiagPrevKeyA = _diskSwapKeyAHeld;
            }
            if (_diskSwapKeyBHeld != _diskDiagPrevKeyB)
            {
                System.Diagnostics.Trace.WriteLine($"[DiskDiag] key half B (key={_diskSwapKeyB}) edge={(_diskSwapKeyBHeld ? "Down" : "Up")}");
                _diskDiagPrevKeyB = _diskSwapKeyBHeld;
            }

            bool held = ctrlChord || keyChord;
            if (held && !_diskSwapPrevHeld)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[DiskDiag] CHORD RISING EDGE — calling SwapToNextDisk. " +
                    $"ctrlChord={ctrlChord} keyChord={keyChord} diskControlAvailable={_diskControlAvailable}");
                SwapToNextDisk();
            }
            _diskSwapPrevHeld = held;

            // EmuTV quit chord: L3+R3+L2+R2 held ~1.5s quits the game (same combo that
            // opens EmuTV from the desktop). Independent of disk-swap; reuses ctl0.
            if (connected && ctl0!.IsTvModeChordHeld)
            {
                if (!_quitChordFired && ++_quitChordFrames >= QuitChordFramesRequired)
                {
                    _quitChordFired = true;
                    Dispatcher.BeginInvoke(new Action(() => { try { Close(); } catch { } }));
                }
            }
            else
            {
                _quitChordFrames = 0;
            }
        }

        // ── EmuTV save/load hotkeys ───────────────────────────────────────────────
        // Hold the modifier (default L3) ~1s to "arm", then a trigger fires an action:
        //   R2 = Save (new state),  L2 = Load latest.
        // R3 is gated out so this never collides with the L3+R3+L2+R2 quit chord.
        // Stage 2 will read per-system bindings; for now the defaults are hardcoded.
        private long _hotkeyArmSince;     // TickCount64 when the modifier went down (0 = up)
        private bool _hotkeyArmed;
        private bool _saveHotkeyLatch;
        private bool _loadHotkeyLatch;
        private const long HotkeyHoldMs = 1000;

        private void PollHotkeys()
        {
            var ctl0 = _controllers[0];
            bool connected = ctl0 != null && ctl0.IsConnected;

            // Per-console bindings (defaults: modifier L3=14, save R2=13, load L2=12).
            // R3 (15) held means the user is doing the quit chord, so stay out of the way.
            uint modId  = _hotkeyModCtrl != uint.MaxValue ? _hotkeyModCtrl : 14;
            uint saveId = _saveCtrl      != uint.MaxValue ? _saveCtrl      : 13;
            uint loadId = _loadCtrl      != uint.MaxValue ? _loadCtrl      : 12;

            bool modifier = connected && IsRawButtonHeld(ctl0!, modId);
            // Quit-chord guard: stay out of the way while R3 is held (the quit chord is
            // L3+R3+L2+R2) — UNLESS the user bound a hotkey to R3 itself, in which case
            // R3 is legitimately the modifier/action and must not self-cancel.
            bool r3IsHotkey = modId == 15 || saveId == 15 || loadId == 15;
            bool r3Held     = connected && !r3IsHotkey && IsRawButtonHeld(ctl0!, 15);

            if (!modifier || r3Held)
            {
                _hotkeyArmSince  = 0;
                _hotkeyArmed     = false;
                _saveHotkeyLatch = false;
                _loadHotkeyLatch = false;
                return;
            }

            if (_hotkeyArmSince == 0) _hotkeyArmSince = Environment.TickCount64;
            if (Environment.TickCount64 - _hotkeyArmSince < HotkeyHoldMs) return; // still arming

            if (!_hotkeyArmed)
            {
                _hotkeyArmed = true;
                Dispatcher.BeginInvoke(() => ShowHotkeyToast("Hotkeys armed     R2  Save      L2  Load"));
            }

            bool save = IsRawButtonHeld(ctl0!, saveId);
            bool load = IsRawButtonHeld(ctl0!, loadId);

            if (save && !_saveHotkeyLatch)
            {
                _saveHotkeyLatch = true;
                Dispatcher.BeginInvoke(() =>
                {
                    RequestSave(DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"));
                    ShowHotkeyToast("State saved");
                });
            }
            if (!save) _saveHotkeyLatch = false;

            if (load && !_loadHotkeyLatch)
            {
                _loadHotkeyLatch = true;
                Dispatcher.BeginInvoke(() =>
                {
                    var latest = _db?.GetSaveStatesByGame(_game.Id)
                                    ?.OrderByDescending(s => s.CreatedAt).FirstOrDefault();
                    if (latest != null) { RequestLoad(latest.StatePath, "Load Latest"); ShowHotkeyToast("Loaded latest save"); }
                    else ShowHotkeyToast("No save states yet");
                });
            }
            if (!load) _loadHotkeyLatch = false;
        }

        // Save/load hotkey feedback that shows OVER the game, so it's visible in
        // fullscreen (where the status-bar StatusText is collapsed to zero height).
        private System.Windows.Threading.DispatcherTimer? _hotkeyToastTimer;
        private void ShowHotkeyToast(string text)
        {
            HotkeyToastText.Text   = text;
            HotkeyToast.Visibility = Visibility.Visible;
            _hotkeyToastTimer ??= new System.Windows.Threading.DispatcherTimer();
            _hotkeyToastTimer.Stop();
            _hotkeyToastTimer.Interval = TimeSpan.FromSeconds(2);
            _hotkeyToastTimer.Tick -= HotkeyToastTick;
            _hotkeyToastTimer.Tick += HotkeyToastTick;
            _hotkeyToastTimer.Start();
        }
        private void HotkeyToastTick(object? sender, EventArgs e)
        {
            _hotkeyToastTimer?.Stop();
            HotkeyToast.Visibility = Visibility.Collapsed;
        }

        // Show a transient disk-swap status message and auto-revert after 3 s.
        // Called from EmuThread (SwapToNextDisk) — marshals to UI thread.
        private void ShowDiskStatus(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_diskStatusRevertTimer == null || !_diskStatusRevertTimer.IsEnabled)
                        _diskStatusPrevText = StatusText.Text;
                    StatusText.Text = message;

                    _diskStatusRevertTimer ??= new System.Windows.Threading.DispatcherTimer
                        { Interval = TimeSpan.FromSeconds(3) };
                    _diskStatusRevertTimer.Tick -= DiskStatusRevertTick;
                    _diskStatusRevertTimer.Tick += DiskStatusRevertTick;
                    _diskStatusRevertTimer.Stop();
                    _diskStatusRevertTimer.Start();
                }
                catch { }
            }));
        }

        private void DiskStatusRevertTick(object? sender, EventArgs e)
        {
            try
            {
                _diskStatusRevertTimer?.Stop();
                if (!string.IsNullOrEmpty(_diskStatusPrevText))
                    StatusText.Text = _diskStatusPrevText!;
                _diskStatusPrevText = null;
            }
            catch { }
        }

        // FDS / NES disk swap is done by injecting JOYPAD_L on port 0 (NES libretro
        // cores convention: Nestopia, Mesen, FCEUmm all use this — none of them
        // register the disk-control env interface). Counter is decremented per
        // frame in OnInputPoll; OnInputState returns 1 for JOYPAD_L while > 0.
        private int _fdsSideChangeFrames;

        // Deferred disc-tray re-insert. After set_image_index, we wait this many
        // frames before calling set_eject_state(false). Matches RetroArch's pattern
        // (runloop.c "pending_disk_control_insert"). Decremented in OnInputPoll.
        private int _diskInsertPendingFrames;

        private void SwapToNextDisk()
        {
            // FDS-family fallback: cores in this family don't expose env-based disk
            // control. Inject a JOYPAD_L press on port 0 for several frames — that's
            // what the cores wire to "Disk Side Change".
            if (!_diskControlAvailable
                && string.Equals(_game?.Console, "FDS", StringComparison.OrdinalIgnoreCase))
            {
                _fdsSideChangeFrames = 6; // ~100ms at 60fps — long enough for the core to register
                ShowDiskStatus("Disk: side change");
                System.Diagnostics.Trace.WriteLine("Disk swap: injecting JOYPAD_L on port 0 (FDS side change)");
                return;
            }

            // Always surface SOMETHING in the status bar so the user knows the
            // chord registered and why nothing changed (if nothing did).
            if (!_diskControlAvailable)
            {
                ShowDiskStatus("Disk swap: this core doesn't support disc switching");
                return;
            }
            if (_diskGetNumImages == null || _diskSetImageIndex == null
                || _diskSetEjectState == null)
            {
                ShowDiskStatus("Disk swap: core registered an incomplete disc interface");
                return;
            }

            try
            {
                uint count = _diskGetNumImages.Invoke();
                if (count <= 1)
                {
                    // Common case for CD-based games imported as a single .cue/.chd.
                    // Tell the user how to fix it.
                    string console = _game?.Console ?? "";
                    bool isCd = console is "PS1" or "Saturn" or "SegaCD" or "Amiga"
                                       or "TurboGrafx16" or "PCECD" or "TG16" or "3DO";
                    ShowDiskStatus(isCd
                        ? "Disk swap: only one disc loaded — put all discs in the same folder and re-import"
                        : "Disk swap: this game has only one disc image");
                    return;
                }

                uint cur = _diskGetImageIndex?.Invoke() ?? 0;
                uint next = (cur + 1) % count;

                // Mirror RetroArch's swap pattern (`disk_control_interface.c`
                // `disk_control_set_index` → runloop frame counter at runloop.c:6741):
                //   1) eject immediately
                //   2) set_image_index immediately
                //   3) defer set_eject_state(false) by ~100 frames (~1.67s @ 60fps)
                //
                // The deferred re-insert matters most for Beetle PSX, whose CD audio
                // engine assumes the disc spun down between swap operations; an
                // immediate re-insert can confuse it. Other cores (GenPlusGX-MCD,
                // PicoDrive-MCD, PUAE, Kronos) tolerate either pattern, but we use
                // RetroArch's timing for behavioral parity.
                bool alreadyEjected = _diskGetEjectState?.Invoke() ?? false;
                if (!alreadyEjected) _diskSetEjectState.Invoke(true);
                _diskSetImageIndex.Invoke(next);
                _diskInsertPendingFrames = 100;

                System.Diagnostics.Trace.WriteLine($"Disk swap: {cur} -> {next} (of {count}) — insert deferred 100 frames");

                ShowDiskStatus($"Disk {next + 1} / {count}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"SwapToNextDisk failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by the core once per frame to query each button/axis state.
        ///
        /// Parameters (from libretro.h):
        ///   port   — controller port, 0 = player 1
        ///   device — RETRO_DEVICE_JOYPAD (1) or RETRO_DEVICE_ANALOG (5)
        ///   index  — for ANALOG: 0 = left stick, 1 = right stick
        ///   id     — joypad button id, or for ANALOG: 0 = X axis, 1 = Y axis
        ///
        /// Analog return range: -32768 (left/up) to +32767 (right/down).
        ///
        /// Y-axis inversion: libretro up = negative, XInput up = positive.
        /// GetAnalogAxisValue() returns raw XInput values, so we negate Y here.
        /// Keyboard axis values (_keyLeftStickY etc.) are already negated at
        /// assignment time in SetKey(), so no second negation is needed there.
        /// </summary>
        private short OnInputState(uint port, uint device, uint index, uint id)
        {
            try
            {
            if (port >= 4) return 0;
            var ctrl = _controllers[port];
            // Keyboard input is only for port 0 (player 1)
            bool isPort0 = port == 0;

            if (device == RETRO_DEVICE_JOYPAD)
            {
                // Bitmask mode: core requests all buttons in a single call (id=256).
                const uint RETRO_DEVICE_ID_JOYPAD_MASK = 256;
                if (id == RETRO_DEVICE_ID_JOYPAD_MASK)
                {
                    short mask = 0;
                    for (uint b = 0; b < 16; b++)
                    {
                        bool bp = (isPort0 && b < (uint)_inputState.Length && _inputState[b])
                                  || (ctrl?.GetButtonState(b) ?? false);
                        if (!bp && isPort0 && (_consoleHandler?.PromoteAnalogStickToDpad ?? false) && ctrl != null)
                        {
                            bp = b switch
                            {
                                JOYPAD_UP    => ctrl.GetButtonState(ControllerManager.ANALOG_LEFT_UP),
                                JOYPAD_DOWN  => ctrl.GetButtonState(ControllerManager.ANALOG_LEFT_DOWN),
                                JOYPAD_LEFT  => ctrl.GetButtonState(ControllerManager.ANALOG_LEFT_LEFT),
                                JOYPAD_RIGHT => ctrl.GetButtonState(ControllerManager.ANALOG_LEFT_RIGHT),
                                _ => false
                            };
                        }
                        // FDS side-change injection (bitmask path).
                        if (port == 0 && b == JOYPAD_L && _fdsSideChangeFrames > 0)
                            bp = true;
                        if (bp && !TurboGate(port, b)) bp = false;
                        if (bp) mask |= (short)(1 << (int)b);
                    }
                    return mask;
                }

                if (id >= 16) return 0;
                // FDS side-change injection: the chord sets _fdsSideChangeFrames > 0
                // and we report JOYPAD_L pressed on port 0 for that many frames.
                // Nestopia/Mesen/FCEUmm all wire this to "Disk Side Change".
                if (port == 0 && id == JOYPAD_L && _fdsSideChangeFrames > 0)
                    return 1;
                bool pressed = (isPort0 && id < (uint)_inputState.Length && _inputState[id])
                               || (ctrl?.GetButtonState(id) ?? false);

                // Promote left analog stick to JOYPAD directions for consoles
                // whose handler opts in (CDi: cursor needs smooth movement;
                // Arcade: modern controllers' stick is the natural input for
                // FBNeo / MAME 2003-Plus games, originally digital joysticks).
                // X and Y are read against independent deadzone thresholds so
                // diagonals (NE/NW/SE/SW) survive — both JOYPAD_UP and
                // JOYPAD_RIGHT can be reported true on the same poll.
                if (!pressed && isPort0 && (_consoleHandler?.PromoteAnalogStickToDpad ?? false) && ctrl != null)
                {
                    pressed = id switch
                    {
                        JOYPAD_UP    => ctrl.GetButtonState(ControllerManager.ANALOG_LEFT_UP),
                        JOYPAD_DOWN  => ctrl.GetButtonState(ControllerManager.ANALOG_LEFT_DOWN),
                        JOYPAD_LEFT  => ctrl.GetButtonState(ControllerManager.ANALOG_LEFT_LEFT),
                        JOYPAD_RIGHT => ctrl.GetButtonState(ControllerManager.ANALOG_LEFT_RIGHT),
                        _ => false
                    };
                }

                if (pressed && !TurboGate(port, id)) pressed = false;
                return pressed ? (short)1 : (short)0;
            }

            // Mouse device — used by MAME-based cores (e.g. SAME CDi, port 0).
            // id=0 MOUSE_X: X delta (right = positive)
            // id=1 MOUSE_Y: Y delta (down = positive, so negate XInput Y)
            // id=2 MOUSE_LEFT:  Button 1
            // id=3 MOUSE_RIGHT: Button 2
            if (device == RETRO_DEVICE_MOUSE)
            {
                bool acceptDeltas = isPort0;

                if (id == 0) // MOUSE_X delta
                {
                    int wpfDelta = acceptDeltas ? Interlocked.Exchange(ref _mouseDeltaX, 0) : 0;

                    // Controller analog stick fallback
                    if (ctrl != null && ctrl.IsConnected)
                    {
                        short x = ctrl.GetAnalogAxisValue(0, 0);
                        wpfDelta += (int)(x / MouseAnalogScale);
                    }

                    return (short)Math.Clamp(wpfDelta, short.MinValue, short.MaxValue);
                }
                if (id == 1) // MOUSE_Y delta
                {
                    int wpfDelta = acceptDeltas ? Interlocked.Exchange(ref _mouseDeltaY, 0) : 0;

                    if (ctrl != null && ctrl.IsConnected)
                    {
                        short y = ctrl.GetAnalogAxisValue(0, 1);
                        wpfDelta += (int)(-y / MouseAnalogScale); // negate: XInput up=+, mouse down=+
                    }

                    return (short)Math.Clamp(wpfDelta, short.MinValue, short.MaxValue);
                }
                if (id == 2) // MOUSE_LEFT → Button 1
                {
                    bool pressed = (acceptDeltas && (_pointerPressed || _leftMousePressed)) ||
                                   (isPort0 && _inputState[JOYPAD_B]) ||
                                   (ctrl?.GetButtonState(JOYPAD_B) ?? false);
                    return pressed ? (short)1 : (short)0;
                }
                if (id == 3) // MOUSE_RIGHT → Button 2
                {
                    bool pressed = (acceptDeltas && _rightMousePressed) ||
                                   (isPort0 && _inputState[JOYPAD_Y]) ||
                                   (ctrl?.GetButtonState(JOYPAD_Y) ?? false);
                    return pressed ? (short)1 : (short)0;
                }
                return 0;
            }

            if (device == RETRO_DEVICE_ANALOG)
            {

                // Analog triggers — index=2 (RETRO_DEVICE_INDEX_ANALOG_BUTTON), id=L2(12)/R2(13).
                // Flycast queries Dreamcast L/R triggers this way; Dolphin queries GC L/R
                // triggers the same way (GC triggers map to L2/R2 in the libretro convention).
                // Returns 0..32767.
                if (index == RETRO_DEVICE_INDEX_ANALOG_BUTTON)
                {
                    if (ctrl != null && ctrl.IsConnected)
                    {
                        if (id == JOYPAD_L2) return ctrl.GetTriggerValue(0);
                        if (id == JOYPAD_R2) return ctrl.GetTriggerValue(1);
                    }
                    return 0;
                }

                // Analog sticks — index=0 (left) or 1 (right), id=0 (X) or 1 (Y).
                if (id == RETRO_DEVICE_ID_ANALOG_X || id == RETRO_DEVICE_ID_ANALOG_Y)
                {
                    if (ctrl != null && ctrl.IsConnected)
                    {
                        short raw = ctrl.GetAnalogAxisValue(index, id);

                        // Negate Y: XInput up = +32767, libretro up = -32768
                        if (id == RETRO_DEVICE_ID_ANALOG_Y)
                            raw = raw == short.MinValue ? short.MaxValue : (short)-raw;

                        return raw;
                    }
                    else if (isPort0)
                    {
                        // Keyboard fallback — already in libretro convention, port 0 only
                        return (index, id) switch
                        {
                            (0, 0) => _keyLeftStickX,
                            (0, 1) => _keyLeftStickY,
                            (1, 0) => _keyRightStickX,
                            (1, 1) => _keyRightStickY,
                            _      => 0
                        };
                    }
                }
            }

            // Raw keyboard — used by DOSBox Pure and any core that polls RETRO_DEVICE_KEYBOARD.
            // Core queries each RETROK_* id individually; we just return the tracked state.
            if (device == RETRO_DEVICE_KEYBOARD)
                return _retroKb.IsPressed(id) ? (short)1 : (short)0;

            // Pointer device — touch input for NDS bottom screen (port 0 only).
            if (isPort0 && device == RETRO_DEVICE_POINTER)
            {
                return id switch
                {
                    RETRO_DEVICE_ID_POINTER_X       => _pointerPressed ? _pointerX : (short)0,
                    RETRO_DEVICE_ID_POINTER_Y       => _pointerPressed ? _pointerY : (short)0,
                    RETRO_DEVICE_ID_POINTER_PRESSED => _pointerPressed ? (short)1  : (short)0,
                    _ => 0
                };
            }

            return 0;
            }
            catch { return 0; }
        }

        private void OnControllerButtonChanged(uint button, bool pressed)
        {
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Let an editable text field (e.g. the shader picker search box) receive
            // keystrokes instead of routing them to game input / consuming them.
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
            RecLog($"KeyDown: {e.Key}");
            SetKey(e.Key, true);

            bool hotkeyModifier = true;

            if (hotkeyModifier)
            {
                if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }
                if (e.Key == Key.Escape)
                {
                    if (_isFullscreen) ToggleFullscreen();
                    else Close();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.F5)
                {
                    LoadPickerPanel.Visibility = Visibility.Collapsed;
                    RequestSave("Quick Save");
                }
                if (e.Key == Key.F7)
                {
                    var qs = _db?.GetSaveStateByGameAndName(_game.Id, "Quick Save");
                    if (qs != null) RequestLoad(qs.StatePath, "Quick Save");
                    else { _transientMsg = "No Quick Save found"; _transientExpiry = DateTime.Now.AddSeconds(3); }
                }
                // Configurable screenshot hotkey (Preferences → Media → Screenshot hotkey).
                // PrintScreen always fires as a hardware-baked fallback regardless of the
                // user's setting; F12 fires when the setting is empty (default) or set to
                // F12; any other configured key takes its place.
                {
                    string configured = App.Configuration?.GetUserPreferences()?.ScreenshotKey ?? "";
                    bool isDefault = string.IsNullOrEmpty(configured);
                    bool match = e.Key == Key.PrintScreen
                              || (isDefault && e.Key == Key.F12)
                              || (!isDefault && string.Equals(e.Key.ToString(), configured, StringComparison.OrdinalIgnoreCase));
                    if (match) TakeScreenshot();
                }
                if (e.Key == Key.F9)
                    ToggleRecording();
            }
            e.Handled = true;
        }

        private void TakeScreenshot()
        {
            try
            {
                // Capture chain prioritized for "what the user actually sees right now":
                //   1. HW cores with an active Vulkan/GL overlay window → WGC capture
                //      of that HWND. Captures the upscaled GPU-rendered frame at its
                //      actual displayed resolution (e.g. PS1 HW at 4x internal → 1280×960,
                //      N64 ParaLLEl-RDP at 4x → similar). Bypasses our small CPU readback.
                //   2. SW cores, or HW cores without an overlay → RenderTargetBitmap of
                //      the WPF GameScreen Image. This gives a WYSIWYG capture including
                //      any shaders/scaling applied at the WPF level, sized to the actual
                //      display area instead of the tiny native console resolution.
                //   3. PrintWindow fallback for HW cores when WGC fails.
                //   4. Raw _bitmap.CopyPixels() last resort — native console resolution,
                //      no shaders applied. The pre-fix behavior, kept only as a backstop.
                BitmapSource? bmp = null;

                IntPtr overlayHwnd = _vulkanOverlayHwnd != IntPtr.Zero
                                        ? _vulkanOverlayHwnd
                                        : _glOverlayHwnd;

                // 1. WGC on overlay (HW cores)
                if (overlayHwnd != IntPtr.Zero)
                {
                    try { bmp = Emutastic.Services.WgcSnapshotService.Capture(overlayHwnd); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Screenshot] WGC failed: {ex.Message}"); }
                }

                // 2. WPF GameScreen RenderTargetBitmap (SW cores; HW with no overlay)
                if (bmp == null)
                {
                    BitmapSource? rtbCaptured = null;
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            // Under a bezel, capture the composite (game + frame, no HUD)
                            // so the screenshot is WYSIWYG; otherwise just the game.
                            FrameworkElement capTarget = _bezelActive ? GameLayer : GameScreen;
                            int w = (int)Math.Round(capTarget.ActualWidth);
                            int h = (int)Math.Round(capTarget.ActualHeight);
                            if (w > 0 && h > 0)
                            {
                                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                                rtb.Render(capTarget);
                                rtb.Freeze();
                                rtbCaptured = rtb;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Trace.WriteLine($"[Screenshot] RenderTargetBitmap failed: {ex.Message}");
                        }
                    });
                    bmp = rtbCaptured;
                }

                // 3. PrintWindow on overlay (HW backup when WGC is unhappy)
                if (bmp == null && overlayHwnd != IntPtr.Zero)
                {
                    try { bmp = CaptureWindowToBitmap(overlayHwnd); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Screenshot] PrintWindow failed: {ex.Message}"); }
                }

                // 4. Raw _bitmap (native console res — last resort)
                if (bmp == null && _bitmap != null)
                {
                    int w = _bitmap.PixelWidth, h = _bitmap.PixelHeight;
                    var snap = new WriteableBitmap(w, h, _bitmap.DpiX, _bitmap.DpiY, _bitmap.Format, null);
                    snap.Lock();
                    _bitmap.CopyPixels(new Int32Rect(0, 0, w, h), snap.BackBuffer, snap.BackBufferStride * h, snap.BackBufferStride);
                    snap.AddDirtyRect(new Int32Rect(0, 0, w, h));
                    snap.Unlock();
                    snap.Freeze();
                    bmp = snap;
                }

                if (bmp == null)
                {
                    _transientMsg    = "Screenshot not available for this core";
                    _transientExpiry = DateTime.Now.AddSeconds(3);
                    return;
                }

                // Rotate for vertical-orientation arcade games (SET_ROTATION 1/3).
                // Skipped under a bezel — the rendered composite already includes orientation.
                if (_coreRotation != 0 && !_bezelActive)
                {
                    double angle = ((-(int)_coreRotation * 90.0) % 360 + 360) % 360;
                    bmp = new TransformedBitmap(bmp, new RotateTransform(angle));
                    if (bmp.CanFreeze && !bmp.IsFrozen) bmp.Freeze();
                }

                var service  = new Services.ScreenshotService();
                string? path = service.Save(bmp, _game.Title, _game.Console);

                _transientMsg    = path != null ? "Screenshot saved" : "Screenshot failed";
                _transientExpiry = DateTime.Now.AddSeconds(3);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Screenshot] {ex.Message}");
                _transientMsg    = "Screenshot failed";
                _transientExpiry = DateTime.Now.AddSeconds(3);
            }
        }

        private void OverlayReset_Click(object sender, RoutedEventArgs e)
        {
            _core?.Reset();
            _transientMsg = "Game reset";
            _transientExpiry = DateTime.Now.AddSeconds(2);
        }

        private void OverlayRecord_Click(object sender, RoutedEventArgs e) => ToggleRecording();

        private void OverlayNotes_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null) return;
            OverlayMenu.Visibility = Visibility.Collapsed;
            // Pinned so it floats above the running game; the user can unpin.
            NotesWindow.ShowFor(_game, this, pinned: true);
        }

        private async void OverlayManual_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null) return;
            OverlayMenu.Visibility = Visibility.Collapsed;
            // No main-window banner in-game — surface download status on the HUD.
            var db = new DatabaseService();
            await ManualLauncher.OpenOrDownloadInGameAsync(_game, db, this, s =>
                Dispatcher.BeginInvoke(() =>
                {
                    _transientMsg = s;
                    _transientExpiry = DateTime.Now.AddSeconds(string.IsNullOrEmpty(s) ? 0 : 4);
                }));
        }

        private void OverlayViewRecordings_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null) return;
            string safeTitle = string.Join("_", _game.Title.Split(System.IO.Path.GetInvalidFileNameChars()));
            string consoleDir = AppPaths.GetFolder("Recordings", _game.Console);
            string gameDir = System.IO.Path.Combine(consoleDir, safeTitle);
            System.IO.Directory.CreateDirectory(gameDir);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(gameDir) { UseShellExecute = true }); }
            catch { }
        }

        private static void RecLog(string msg)
        {
            try
            {
                string logDir = Emutastic.AppPaths.GetFolder("Logs");
                string logPath = System.IO.Path.Combine(logDir, "recording_debug.log");
                Emutastic.Services.LogRotation.RotateIfLarge(logPath);
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }

        private void ToggleRecording()
        {
            RecLog($"ToggleRecording called. hwRenderActive={_hwRenderActive}, isVulkan={_isVulkanHwRender}, vulkanHwnd=0x{_vulkanOverlayHwnd:X}, glHwnd=0x{_glOverlayHwnd:X}");
            if (_recordingService?.IsRecording == true)
            {
                var elapsed = _recordingService.Elapsed;
                bool wasWgc = _recordingService is Services.WgcRecordingService;
                _recordingService.Stop();
                _recordingService = null;
                OverlayRecordIcon.Foreground = System.Windows.Media.Brushes.White;
                OverlayRecordMenuBtn.Content = "Record";
                RecIndicator.Visibility = Visibility.Collapsed;
                _transientMsg = wasWgc
                    ? $"Recording saved ({elapsed:mm\\:ss})"
                    : $"Recording stopped ({elapsed:mm\\:ss}) — encoding...";
                _transientExpiry = DateTime.Now.AddSeconds(3);
                return;
            }

            var avInfo = _core?.AvInfo;
            if (avInfo == null)
            {
                _transientMsg = "Recording unavailable — core not ready";
                _transientExpiry = DateTime.Now.AddSeconds(3);
                return;
            }

            int fps = (int)Math.Round(avInfo.Value.timing.fps);
            int sampleRate = (int)Math.Round(avInfo.Value.timing.sample_rate);
            if (sampleRate <= 0) sampleRate = 44100;

            string safeTitle = string.Join("_", _game.Title.Split(System.IO.Path.GetInvalidFileNameChars()));
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string consoleDir = AppPaths.GetFolder("Recordings", _game.Console);
            string outputDir = System.IO.Path.Combine(consoleDir, safeTitle);
            System.IO.Directory.CreateDirectory(outputDir);
            string outputPath = System.IO.Path.Combine(outputDir, $"{timestamp}.mp4");

            string? err;

            // Some HW-render cores (Vectrex, PPSSPP/PSP, anything routed through
            // GenericHandler with no overlay flags) read back to a CPU buffer and
            // display via WPF rather than rendering into a child HWND. WGC has no
            // child window to target in those cases.
            //
            // Two recovery paths:
            //   - Vectrex stays on the FFmpeg readback path (verified working,
            //     small frames, low data rate; no perf risk).
            //   - Everything else (PSP and any future case) falls back to WGC
            //     against the main emulator window HWND. WGC captures the
            //     composited window via DWM on the GPU side — zero CPU readback,
            //     no temp file, no emu-thread copy. Fixes PSP's 8.4 MB-per-frame
            //     readback that was tanking game FPS during recording.
            //
            // Cores that *do* have an overlay HWND (GameCube embedded window,
            // N64 GL overlay) keep their existing zero-copy WGC path unchanged.
            bool noOverlayHwnd = _vulkanOverlayHwnd == IntPtr.Zero
                              && _glOverlayHwnd == IntPtr.Zero
                              && (_hwndHost?.Handle ?? IntPtr.Zero) == IntPtr.Zero;
            bool isVectrex = _consoleHandler?.ConsoleName == "Vectrex";
            bool useReadbackFfmpegPath = _hwRenderActive && noOverlayHwnd && isVectrex;

            if (_hwRenderActive && !useReadbackFfmpegPath)
            {
                // 3D / HW-render cores: use Windows.Graphics.Capture (zero-copy GPU pipeline)
                RecLog("HW render path — checking WGC support...");
                if (!Services.WgcRecordingService.IsSupported)
                {
                    RecLog("WGC not supported on this OS");
                    _transientMsg = "Recording requires Windows 10 1903 or later";
                    _transientExpiry = DateTime.Now.AddSeconds(4);
                    return;
                }

                // Determine the HWND to capture. Order: dedicated overlay
                // (Vulkan/GL) → embedded host → main emulator window. The
                // main-window fallback covers HW-readback cores like PSP that
                // don't create a child HWND but still benefit from WGC's
                // GPU-side capture instead of CPU-side glReadPixels recording.
                IntPtr captureHwnd = IntPtr.Zero;
                // The dedicated render overlay (a borderless WS_POPUP) holds the actual game
                // pixels and no window chrome — capture it whenever it exists. Both the Vulkan
                // path AND the D3D11 swapchain path render into _vulkanOverlayHwnd; gating this
                // on _isVulkanHwRender made D3D11 (PS2) fall through to capturing the MAIN window
                // → recorded the window chrome + a black game area (the swapchain overlay isn't
                // composited into the parent window's capture).
                if (_vulkanOverlayHwnd != IntPtr.Zero)
                    captureHwnd = _vulkanOverlayHwnd;
                else if (_glOverlayHwnd != IntPtr.Zero)
                    captureHwnd = _glOverlayHwnd;
                else if (_hwndHost is not null && _hwndHost.Handle != IntPtr.Zero)
                    captureHwnd = _hwndHost.Handle;
                else
                {
                    // Main-window fallback — _wpfHwnd is only set inside
                    // SubclassOverlay, which never runs for HW cores without
                    // an overlay (e.g. PSP). Resolve directly from this window
                    // so the fallback actually works.
                    captureHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                }

                RecLog($"captureHwnd=0x{captureHwnd:X}");

                if (captureHwnd == IntPtr.Zero)
                {
                    _transientMsg = "Recording unavailable — no render window found";
                    _transientExpiry = DateTime.Now.AddSeconds(3);
                    return;
                }

                Action<string> onComplete = (result) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (System.IO.File.Exists(result))
                        {
                            _transientMsg = "Recording saved to Recordings";
                            _transientExpiry = DateTime.Now.AddSeconds(4);
                        }
                        else
                        {
                            _transientMsg = $"Recording failed: {result}";
                            _transientExpiry = DateTime.Now.AddSeconds(5);
                        }
                    });
                };

                try
                {
                    var wgcService = new Services.WgcRecordingService();
                    err = wgcService.Start(outputPath, captureHwnd, fps, sampleRate, onComplete);
                    _recordingService = wgcService;
                    RecLog($"WGC Start result: {err ?? "OK"}");
                }
                catch (Exception ex)
                {
                    RecLog($"WGC Start exception: {ex}");
                    err = ex.Message;
                }
            }
            else
            {
                // 2D / software-render cores: use raw frame capture + FFmpeg encode
                if (Services.RecordingService.FindFfmpeg() == null)
                {
                    _transientMsg = "ffmpeg.exe not found — download it in Preferences → Extras";
                    _transientExpiry = DateTime.Now.AddSeconds(4);
                    return;
                }

                uint w, h;
                string pixFmt;
                if (useReadbackFfmpegPath)
                {
                    // Vectrex feeds the readback buffer (BGRA32, post-flip) into
                    // RecordingService.QueueVideoFrame from ReadBackFromCurrentContext.
                    // Need at least one frame rendered before we can record.
                    if (_hwFlippedWidth == 0 || _hwFlippedHeight == 0)
                    {
                        _transientMsg = "Recording unavailable — wait for first frame";
                        _transientExpiry = DateTime.Now.AddSeconds(3);
                        return;
                    }
                    w = _hwFlippedWidth;
                    h = _hwFlippedHeight;
                    pixFmt = "bgra";
                }
                else
                {
                    w = _lastFrameWidth > 0 ? _lastFrameWidth : avInfo.Value.geometry.base_width;
                    h = _lastFrameHeight > 0 ? _lastFrameHeight : avInfo.Value.geometry.base_height;

                    if (_pixelFormat == RETRO_PIXEL_FORMAT_XRGB8888)
                        pixFmt = "bgra";
                    else if (_pixelFormat == RETRO_PIXEL_FORMAT_RGB565)
                        pixFmt = "rgb565le";
                    else
                        pixFmt = "rgb555le";
                }

                Action<string> onEncodeComplete = (result) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (System.IO.File.Exists(result))
                        {
                            _transientMsg = "Recording saved to Recordings";
                            _transientExpiry = DateTime.Now.AddSeconds(4);
                        }
                        else
                        {
                            _transientMsg = $"Encoding failed: {result}";
                            _transientExpiry = DateTime.Now.AddSeconds(5);
                        }
                    });
                };

                var recCfg = _configService.GetRecordingConfiguration();

                // Aspect-ratio correction is currently scoped to CD-i only —
                // its half-height interlaced framebuffer (e.g. 384x140) breaks
                // uniform integer scaling and produces unusably stretched videos
                // (1536x560 for 4x). Every other console gets the historical
                // uniform-scale path, leaving working recordings untouched.
                float displayAspect = _consoleHandler?.ConsoleName == "CDi"
                    ? avInfo.Value.geometry.aspect_ratio
                    : 0f;

                var encodeSettings = new Services.RecordingEncodeSettings
                {
                    Quality = recCfg.Quality,
                    OutputScale = recCfg.OutputScale,
                    DisplayAspectRatio = displayAspect,
                    Encoder = recCfg.Encoder,
                    HighChroma = recCfg.HighChroma,
                    AudioBitrateKbps = recCfg.AudioBitrateKbps,
                };

                var ffmpegService = new Services.RecordingService();
                err = ffmpegService.Start(outputPath, (int)w, (int)h, fps, sampleRate, pixFmt, onEncodeComplete, encodeSettings);
                _recordingService = ffmpegService;
            }

            if (err == null)
            {
                OverlayRecordIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE0, 0x35, 0x35));
                OverlayRecordMenuBtn.Content = "Stop Recording";
                RecIndicator.Visibility = Visibility.Visible;
                _transientMsg = "Recording started — press F9 to stop";
                _transientExpiry = DateTime.Now.AddSeconds(3);
            }
            else
            {
                _recordingService = null;
                _transientMsg = $"Recording failed: {err}";
                _transientExpiry = DateTime.Now.AddSeconds(5);
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) { base.OnKeyUp(e); return; }
            SetKey(e.Key, false);
            base.OnKeyUp(e);
        }

        private void LoadKeyboardMappings()
        {
            try
            {
                // Preferences saves per-player keys as "{Console}_P{N}"; load P1 mappings.
                var p1Key = $"{_game.Console}_P1";
                var p1Config = _configService.GetInputConfiguration(p1Key);
                _inputConfig = p1Config.KeyboardMappings.Count > 0
                    ? p1Config
                    : _configService.GetInputConfiguration(_game.Console); // fallback for legacy saves
                _diskSwapKeyA = _diskSwapKeyB = -1;
                _diskSwapCtrlA = _diskSwapCtrlB = uint.MaxValue;
                _hotkeyModCtrl = _saveCtrl = _loadCtrl = uint.MaxValue;
                // Clear any stale held-state from a previous binding. Prevents a
                // spurious fire on the next poll if a key flag was left true while
                // the prefs dialog had focus (no KeyUp delivered to this window).
                _diskSwapKeyAHeld = _diskSwapKeyBHeld = false;
                _diskSwapPrevHeld = false;
                foreach (var mapping in _inputConfig.KeyboardMappings)
                {
                    if (string.Equals(mapping.ButtonName, "Disk Swap", StringComparison.OrdinalIgnoreCase))
                    {
                        // Chord format: "KeyA+KeyB". Anything else (single key from a
                        // pre-chord build) is ignored — user must rebind.
                        var parts = (mapping.InputIdentifier ?? "").Split('+', 2);
                        if (parts.Length == 2
                            && Enum.TryParse<Key>(parts[0].Trim(), out var ka)
                            && Enum.TryParse<Key>(parts[1].Trim(), out var kb))
                        {
                            _diskSwapKeyA = (int)ka;
                            _diskSwapKeyB = (int)kb;
                        }
                        continue;
                    }
                    if (Enum.TryParse<Key>(mapping.InputIdentifier, out var key))
                    {
                        uint id = Services.LibretroInput.GetButtonId(mapping.ButtonName, _game.Console);
                        if (id < 16) _keyboardMappings[key] = id;
                    }
                }
                foreach (var cm in _inputConfig.ControllerMappings)
                {
                    if (string.Equals(cm.ButtonName, "Disk Swap", StringComparison.OrdinalIgnoreCase))
                    {
                        // Chord format: "id1+id2".
                        var parts = (cm.InputIdentifier ?? "").Split('+', 2);
                        if (parts.Length == 2
                            && uint.TryParse(parts[0].Trim(), out uint a) && a < 24
                            && uint.TryParse(parts[1].Trim(), out uint b) && b < 24)
                        {
                            _diskSwapCtrlA = a;
                            _diskSwapCtrlB = b;
                        }
                        break;
                    }
                }

                // EmuTV save/load hotkeys — single controller button per action. Read
                // from the per-player controller config (p1Config) directly: _inputConfig
                // falls back to the legacy "{console}" key when there are no KEYBOARD
                // mappings (the norm for controller-only users), which misses the rebind.
                foreach (var cm in p1Config.ControllerMappings)
                {
                    if (!uint.TryParse(cm.InputIdentifier, out uint hid) || hid >= 24) continue;
                    if      (string.Equals(cm.ButtonName, "Hotkey",     StringComparison.OrdinalIgnoreCase)) _hotkeyModCtrl = hid;
                    else if (string.Equals(cm.ButtonName, "Save State", StringComparison.OrdinalIgnoreCase)) _saveCtrl      = hid;
                    else if (string.Equals(cm.ButtonName, "Load State", StringComparison.OrdinalIgnoreCase)) _loadCtrl      = hid;
                }

                System.Diagnostics.Trace.WriteLine(
                    $"Loaded {_keyboardMappings.Count} keyboard mappings " +
                    $"(disk swap key chord: {_diskSwapKeyA}+{_diskSwapKeyB}, " +
                    $"ctrl chord: {_diskSwapCtrlA}+{_diskSwapCtrlB})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Keyboard mapping load failed: {ex.Message}");
                LoadDefaultKeyboardMappings();
            }
        }

        private void LoadDefaultKeyboardMappings()
        {
            _keyboardMappings.Clear();
            _keyboardMappings[Key.Up]         = JOYPAD_UP;
            _keyboardMappings[Key.Down]       = JOYPAD_DOWN;
            _keyboardMappings[Key.Left]       = JOYPAD_LEFT;
            _keyboardMappings[Key.Right]      = JOYPAD_RIGHT;
            _keyboardMappings[Key.Z]          = JOYPAD_B;
            _keyboardMappings[Key.X]          = JOYPAD_A;
            _keyboardMappings[Key.C]          = JOYPAD_Y;
            _keyboardMappings[Key.V]          = JOYPAD_X;
            _keyboardMappings[Key.Q]          = JOYPAD_L;
            _keyboardMappings[Key.E]          = JOYPAD_R;
            _keyboardMappings[Key.Enter]      = JOYPAD_START;
            _keyboardMappings[Key.LeftShift]  = JOYPAD_SELECT;
            _keyboardMappings[Key.RightShift] = JOYPAD_SELECT;
        }

        // GetLibretroButtonId moved to Services/LibretroInput.GetButtonId.

        // ── Pointer / touch input (NDS bottom screen) ─────────────────────

        private void UpdatePointerPosition(System.Windows.Input.MouseEventArgs e)
        {
            var pos = e.GetPosition(GameScreen);
            double imgW = GameScreen.ActualWidth;
            double imgH = GameScreen.ActualHeight;
            if (imgW <= 0 || imgH <= 0) return;

            // Normalize to -32768..32767 across the full rendered image
            _pointerX = (short)Math.Clamp((pos.X / imgW * 65535) - 32768, -32768, 32767);
            _pointerY = (short)Math.Clamp((pos.Y / imgH * 65535) - 32768, -32768, 32767);

            // Accumulate pixel deltas for RETRO_DEVICE_MOUSE
            if (!double.IsNaN(_mouseLastPixelX))
            {
                _mouseDeltaX += (int)(pos.X - _mouseLastPixelX);
                _mouseDeltaY += (int)(pos.Y - _mouseLastPixelY);
            }
            _mouseLastPixelX = pos.X;
            _mouseLastPixelY = pos.Y;
        }

        private void GameScreen_PointerDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            UpdatePointerPosition(e);
            _pointerPressed = true;
            GameScreen.CaptureMouse();
        }

        private void GameScreen_PointerUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _pointerPressed = false;
            GameScreen.ReleaseMouseCapture();
        }

        private void GameScreen_RightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // While paused, right-click rotates the pause-screen effect to the next
            // one in the catalog (round-robin, skipping "None"). Doesn't fire during
            // gameplay so it can't interfere with in-game mouse-right semantics
            // (SAME CDi / MAME mouse-driven cores).
            if (_isPaused)
            {
                CyclePauseEffect();
                e.Handled = true;
                return;
            }
        }

        // Rotate to the next pause effect in the registry, skipping "None".
        // Persists the new selection so the next pause picks the same one.
        private void CyclePauseEffect()
        {
            var all = Views.PauseEffects.PauseEffectRegistry.All;
            // Build the "real" list (excluding the None sentinel).
            var rotation = new System.Collections.Generic.List<Views.PauseEffects.PauseEffectRegistry.Entry>();
            foreach (var e in all)
            {
                if (!string.Equals(e.Id, Views.PauseEffects.PauseEffectRegistry.NoneId,
                                   StringComparison.OrdinalIgnoreCase))
                    rotation.Add(e);
            }
            if (rotation.Count == 0) return;

            string currentId = _configService.GetValue("pauseEffect",
                Views.PauseEffects.PauseEffectRegistry.NoneId);
            int idx = rotation.FindIndex(e =>
                string.Equals(e.Id, currentId, StringComparison.OrdinalIgnoreCase));
            int nextIdx = idx < 0 ? 0 : (idx + 1) % rotation.Count;
            string nextId = rotation[nextIdx].Id;

            _configService.SetValue("pauseEffect", nextId);

            // Restart the active effect immediately so the user sees the new one.
            // IMPORTANT: do NOT null the runner here. The runner uses an internal
            // _stopGen counter to invalidate the FadeOut.Completed callback that
            // its previous Stop() scheduled. If we null the runner and create a
            // new one, the OLD runner's still-pending Completed closure captured
            // the OLD _stopGen and that counter never changes — so 250ms later
            // it fires and clears + collapses the shared host element, hiding
            // the new effect that just started. Reusing the runner means
            // Start()'s built-in Stop+gen-bump correctly cancels the pending fade.
            StartPauseEffect();
        }

        private void GameScreen_RightUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
        }

        private void GameScreen_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && _mouseCaptured)
            {
                ExitMouseCapture();
                e.Handled = true;
            }
        }

        private void EnterMouseCapture()
        {
            if (_mouseCaptured) return;
            RecomputeCaptureCenter();
            _mouseCaptured = true;
            _ignoreNextMove = true;
            SetCursorPos(_captureCenterX, _captureCenterY);
            Mouse.OverrideCursor = System.Windows.Input.Cursors.None;
            GameScreen.CaptureMouse();

            _transientMsg = "Mouse captured — middle-click to release";
            _transientExpiry = DateTime.Now.AddSeconds(3);
        }

        private void ExitMouseCapture()
        {
            if (!_mouseCaptured) return;
            _mouseCaptured = false;
            _leftMousePressed = false;
            _rightMousePressed = false;
            Mouse.OverrideCursor = null;
            GameScreen.ReleaseMouseCapture();
        }

        private void RecomputeCaptureCenter()
        {
            double w = GameScreen.ActualWidth;
            double h = GameScreen.ActualHeight;
            if (w <= 0 || h <= 0) return;
            try
            {
                var center = GameScreen.PointToScreen(new System.Windows.Point(w / 2.0, h / 2.0));
                _captureCenterX = (int)center.X;
                _captureCenterY = (int)center.Y;
            }
            catch { /* PointToScreen can throw if window not yet presented */ }
        }

        private void GameScreen_PointerMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_mouseCaptured)
            {
                if (_ignoreNextMove) { _ignoreNextMove = false; return; }
                if (!GetCursorPos(out POINT cur)) return;
                int dx = cur.X - _captureCenterX;
                int dy = cur.Y - _captureCenterY;
                if (dx == 0 && dy == 0) return;
                _mouseDeltaX += dx;
                _mouseDeltaY += dy;
                _ignoreNextMove = true;
                SetCursorPos(_captureCenterX, _captureCenterY);
                return;
            }
            if (_pointerPressed)
                UpdatePointerPosition(e);
        }

        private const short KEY_FULL = 32767;

        private void SetKey(Key key, bool pressed)
        {
            // Mirror every press to the raw-keyboard state so cores that poll
            // RETRO_DEVICE_KEYBOARD (DOSBox Pure) see it regardless of joypad mapping.
            _retroKb.SetKey(key, pressed);

            // If a core registered a keyboard callback (DOSBox Pure does), enqueue the
            // event.  DrainKeyboardQueue() invokes the core's callback on the EmuThread
            // right before each retro_run — never from the WPF UI thread, which would
            // race the core's internal state and corrupt memory.
            if (_coreKeyboardEvent != null)
            {
                uint retroKey = Services.RetroKeyboardMap.ToRetroKey(key);
                if (retroKey != 0)
                {
                    var mods = Keyboard.Modifiers;
                    ushort retroMod = 0;
                    if ((mods & ModifierKeys.Shift)   != 0) retroMod |= 0x01;
                    if ((mods & ModifierKeys.Control) != 0) retroMod |= 0x02;
                    if ((mods & ModifierKeys.Alt)     != 0) retroMod |= 0x04;
                    if ((mods & ModifierKeys.Windows) != 0) retroMod |= 0x08;
                    if (Keyboard.IsKeyToggled(Key.NumLock))  retroMod |= 0x10;
                    if (Keyboard.IsKeyToggled(Key.CapsLock)) retroMod |= 0x20;
                    _kbEventQueue.Enqueue((pressed, retroKey, retroMod));
                }
            }

            // Custom mappings first
            // (kb queue drain happens on EmuThread via DrainKeyboardQueue — never here)
            if (_keyboardMappings.TryGetValue(key, out var id) && id < 16)
            {
                _inputState[id] = pressed;
                return;
            }

            // Disk Swap chord — track each half independently. EmuThread polls both
            // flags and fires when both halves are simultaneously held.
            if (_diskSwapKeyA >= 0 && (int)key == _diskSwapKeyA) _diskSwapKeyAHeld = pressed;
            if (_diskSwapKeyB >= 0 && (int)key == _diskSwapKeyB) _diskSwapKeyBHeld = pressed;

            bool isAnalog = _consoleHandler.UsesAnalogStick;

            switch (key)
            {
                case Key.Up:    _inputState[JOYPAD_UP]    = pressed; break;
                case Key.Down:  _inputState[JOYPAD_DOWN]  = pressed; break;
                case Key.Left:  _inputState[JOYPAD_LEFT]  = pressed; break;
                case Key.Right: _inputState[JOYPAD_RIGHT] = pressed; break;

                // WASD — analog left stick for analog consoles, D-pad otherwise
                // NOTE: Y is negated here (up = negative) to match libretro convention.
                case Key.W:
                    if (isAnalog) _keyLeftStickY = pressed ? (short)-KEY_FULL : (short)0;
                    else _inputState[JOYPAD_UP] = pressed;
                    break;
                case Key.S:
                    if (isAnalog) _keyLeftStickY = pressed ? KEY_FULL : (short)0;
                    else _inputState[JOYPAD_DOWN] = pressed;
                    break;
                case Key.A:
                    if (isAnalog) _keyLeftStickX = pressed ? (short)-KEY_FULL : (short)0;
                    else _inputState[JOYPAD_LEFT] = pressed;
                    break;
                case Key.D:
                    if (isAnalog) _keyLeftStickX = pressed ? KEY_FULL : (short)0;
                    else _inputState[JOYPAD_RIGHT] = pressed;
                    break;

                case Key.Z:     _inputState[JOYPAD_B]      = pressed; break;
                case Key.X:     _inputState[JOYPAD_A]      = pressed; break;
                case Key.C:     _inputState[JOYPAD_Y]      = pressed; break;
                case Key.V:     _inputState[JOYPAD_X]      = pressed; break;
                case Key.Q:     _inputState[JOYPAD_L]      = pressed; break;
                case Key.E:     _inputState[JOYPAD_R]      = pressed; break;
                case Key.Enter: _inputState[JOYPAD_START]  = pressed; break;
                case Key.LeftShift:
                case Key.RightShift: _inputState[JOYPAD_SELECT] = pressed; break;

                // IJKL — right analog stick (N64 C-buttons / PS1 right stick)
                // Y negated to match libretro convention.
                case Key.I: _keyRightStickY = pressed ? (short)-KEY_FULL : (short)0; break;
                case Key.K: _keyRightStickY = pressed ? KEY_FULL         : (short)0; break;
                case Key.J: _keyRightStickX = pressed ? (short)-KEY_FULL : (short)0; break;
                case Key.L: _keyRightStickX = pressed ? KEY_FULL         : (short)0; break;
            }
        }

        // =========================================================================
        // Disc swap helpers (can be wired to future UI buttons)
        // =========================================================================

        /// <summary>
        /// Swaps to the disc at the given zero-based index.
        /// Sequence: eject → set index → insert.
        /// </summary>
        public bool SwapDisc(uint discIndex)
        {
            if (!_diskControlAvailable || _diskSetEjectState == null || _diskSetImageIndex == null)
            {
                System.Diagnostics.Trace.WriteLine("SwapDisc: disc control not available");
                return false;
            }
            try
            {
                _diskSetEjectState(true);
                bool ok = _diskSetImageIndex(discIndex);
                _diskSetEjectState(false);
                System.Diagnostics.Trace.WriteLine($"SwapDisc({discIndex}): {ok}");
                return ok;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"SwapDisc error: {ex.Message}");
                return false;
            }
        }

        public uint GetCurrentDiscIndex() => _diskGetImageIndex?.Invoke() ?? 0;
        public uint GetTotalDiscs()       => _diskGetNumImages?.Invoke()  ?? 0;

        // =========================================================================
        // Save / load state
        // =========================================================================

        private static string SanitizeFileName(string s)
            => FileNameHelper.SanitizeFileName(s);

        /// <summary>
        /// True when the loaded core's save state support is unreliable enough that
        /// we proactively disable save/load UI rather than silently dropping the user's
        /// save. Currently: mame2003-plus (MAME's per-game inconsistent serialization).
        /// </summary>
        private bool IsSaveStateUnreliable()
        {
            string p = (_core?.CorePath ?? "").ToLowerInvariant();
            return p.Contains("mame2003_plus");
        }

        /// <summary>Request a named save from the UI thread. Emu thread picks it up after next retro_run.</summary>
        private void RequestSave(string name)
        {
            if (IsSaveStateUnreliable())
            {
                _transientMsg    = "Save states are disabled for MAME 2003-Plus (unreliable per-game)";
                _transientExpiry = DateTime.Now.AddSeconds(5);
                return;
            }
            _pendingSaveName  = name;
            _saveStatePending = true;
        }

        /// <summary>Called on the emu thread between retro_run calls.</summary>
        private void ExecuteSaveOnEmuThread()
        {
            _saveStatePending = false;
            string name = _pendingSaveName;

            byte[]? data = _core?.SaveState();
            if (data == null)
            {
                _transientMsg    = "Save state not supported by this core";
                _transientExpiry = DateTime.Now.AddSeconds(5);
                return;
            }

            // Snapshot framebuffer bytes now (on emu thread) before handing off to Task.Run
            byte[]? screenshotPixels = null;
            uint    ssWidth = 0, ssHeight = 0;
            bool    isHw    = _hwRenderActive;

            if (isHw && _hwFlippedBuffer.Length > 0 && _hwFlippedWidth > 0 && _hwFlippedHeight > 0)
            {
                screenshotPixels = (byte[])_hwFlippedBuffer.Clone();
                ssWidth  = _hwFlippedWidth;
                ssHeight = _hwFlippedHeight;
            }

            uint coreRot = _coreRotation; // capture on emu thread — used to rotate screenshot to match display
            System.Threading.Tasks.Task.Run(() => FinalizeSave(name, data, screenshotPixels, ssWidth, ssHeight, isHw, coreRot));
        }

        private void FinalizeSave(string name, byte[] data,
            byte[]? screenshotPixels, uint ssWidth, uint ssHeight, bool isHw, uint coreRotation = 0)
        {
            try
            {
                string safeName = SanitizeFileName(name.Length > 0 ? name : "state");
                string statePath    = Path.Combine(_saveStatePath, safeName + ".state");
                string pngPath      = Path.Combine(_saveStatePath, safeName + ".png");
                string jsonPath     = Path.Combine(_saveStatePath, safeName + ".json");
                string cheevosPath  = Path.Combine(_saveStatePath, safeName + ".cheevos");

                File.WriteAllBytes(statePath, data);

                // Pair the libretro state with rcheevos's runtime state so
                // achievement hit counts and measured-progress trackers survive
                // a load (RA Section A: "Hit counts should be stored in save
                // states"). Side-car format keeps the .state file binary-
                // compatible with other libretro frontends — anyone loading the
                // .state alone gets a working game with default RA progress;
                // loading both restores partial progress too. No-op when RA
                // isn't initialized for this session.
                try
                {
                    byte[]? cheevosBlob = _raClient?.SerializeProgress();
                    if (cheevosBlob != null && cheevosBlob.Length > 0)
                        File.WriteAllBytes(cheevosPath, cheevosBlob);
                    else if (File.Exists(cheevosPath))
                        File.Delete(cheevosPath); // overwriting a previous state — stale side-car would silently restore wrong progress
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[RA] Save-state cheevos side-car write failed: {ex.Message}");
                }

                // Screenshot — try in order:
                //  1. HW cores: pre-captured _hwFlippedBuffer pixels (readback path)
                //  2. SW cores: WPF WriteableBitmap _bitmap (the source-of-truth frame)
                //  3. Fallback for either path when (1)/(2) is empty: RenderTargetBitmap
                //     of the GameScreen control on the UI thread. Works whenever the
                //     core renders into the WPF visual tree (i.e. not a native Vulkan/GL
                //     overlay window). Beats silently saving no PNG.
                BitmapSource? bmp = null;
                try
                {
                    if (isHw)
                    {
                        if (screenshotPixels != null && ssWidth > 0 && ssHeight > 0)
                        {
                            bmp = BitmapSource.Create((int)ssWidth, (int)ssHeight,
                                96, 96, PixelFormats.Bgra32, null, screenshotPixels,
                                (int)ssWidth * 4);
                        }
                        else
                        {
                            System.Diagnostics.Trace.WriteLine(
                                $"Screenshot HW path skipped — readback empty (buf={screenshotPixels?.Length ?? 0}, {ssWidth}x{ssHeight}). Trying GameScreen fallback.");
                        }
                    }
                    else
                    {
                        // Software core: capture from WPF WriteableBitmap on UI thread
                        byte[]? swPixels = null;
                        int swW = 0, swH = 0, swStride = 0;
                        PixelFormat swFmt = PixelFormats.Bgr565;
                        Dispatcher.Invoke(() =>
                        {
                            if (_bitmap != null)
                            {
                                swW = _bitmap.PixelWidth; swH = _bitmap.PixelHeight;
                                swStride = _bitmap.BackBufferStride; // actual stride (Bgr565 = swW*2, not swW*4)
                                swFmt = _bitmap.Format;              // Bgra32 when a slang shader is active
                                swPixels = new byte[swH * swStride];
                                _bitmap.CopyPixels(swPixels, swStride, 0);
                            }
                        });
                        if (swPixels != null && swW > 0)
                        {
                            // Branch on the ACTUAL bitmap format, not the core's native
                            // format — a downloaded slang shader renders into a Bgra32
                            // bitmap even for 565 cores.
                            if (swFmt == PixelFormats.Bgra32)
                            {
                                // [B, G, R, X/A]: force alpha opaque for BitmapSource.Create(Bgra32).
                                for (int i = 3; i < swPixels.Length; i += 4)
                                    swPixels[i] = 0xFF;
                            }
                            else if (swFmt == PixelFormats.Bgr565)
                            {
                                // Convert Bgr565 → Bgra32.
                                // Must index by row×stride+col×2 because stride ≠ swW*2 in general.
                                var bgra = new byte[swW * swH * 4];
                                for (int y = 0; y < swH; y++)
                                for (int x = 0; x < swW; x++)
                                {
                                    int    src = y * swStride + x * 2;
                                    ushort px  = (ushort)(swPixels[src] | (swPixels[src + 1] << 8));
                                    int    dst = (y * swW + x) * 4;
                                    bgra[dst + 0] = (byte)((px & 0x1F)        * 255 / 31);
                                    bgra[dst + 1] = (byte)(((px >> 5) & 0x3F) * 255 / 63);
                                    bgra[dst + 2] = (byte)((px >> 11)          * 255 / 31);
                                    bgra[dst + 3] = 0xFF;
                                }
                                swPixels = bgra; swStride = swW * 4;
                            }
                            bmp = BitmapSource.Create(swW, swH, 96, 96, PixelFormats.Bgra32, null, swPixels, swStride);
                        }
                        else
                        {
                            System.Diagnostics.Trace.WriteLine(
                                $"Screenshot SW path skipped — _bitmap unavailable (pixels={swPixels?.Length ?? 0}, {swW}x{swH}). Trying GameScreen fallback.");
                        }
                    }

                    // Fallback chain when the primary capture path returned nothing:
                    //  1. If a native overlay HWND is active (N64 Vulkan, Dreamcast
                    //     Flycast, etc.), try Windows.Graphics.Capture first —
                    //     compositor-level grab that works for Vulkan/DXGI surfaces.
                    //  2. PrintWindow with PW_RENDERFULLCONTENT as a backup — it
                    //     captures cleared backing for Vulkan most of the time,
                    //     but works for some GL overlays (confirmed for Flycast
                    //     Dreamcast in earlier testing).
                    //  3. RenderTargetBitmap of the WPF GameScreen for cores that
                    //     render through the WPF visual tree.
                    IntPtr overlayHwnd = _vulkanOverlayHwnd != IntPtr.Zero
                                            ? _vulkanOverlayHwnd
                                            : _glOverlayHwnd;
                    if (bmp == null && overlayHwnd != IntPtr.Zero)
                    {
                        bmp = Emutastic.Services.WgcSnapshotService.Capture(overlayHwnd);
                        System.Diagnostics.Trace.WriteLine(
                            bmp != null
                                ? $"Screenshot via WGC on overlay 0x{overlayHwnd:X}"
                                : $"Screenshot WGC failed for overlay 0x{overlayHwnd:X} — trying PrintWindow");
                    }
                    if (bmp == null && overlayHwnd != IntPtr.Zero)
                    {
                        bmp = CaptureWindowToBitmap(overlayHwnd);
                        System.Diagnostics.Trace.WriteLine(
                            bmp != null
                                ? $"Screenshot via PrintWindow on overlay 0x{overlayHwnd:X}"
                                : $"Screenshot PrintWindow also failed for overlay 0x{overlayHwnd:X}");
                    }
                    if (bmp == null && overlayHwnd == IntPtr.Zero)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                FrameworkElement capTarget = _bezelActive ? GameLayer : GameScreen;
                                int w = (int)Math.Round(capTarget.ActualWidth);
                                int h = (int)Math.Round(capTarget.ActualHeight);
                                if (w > 0 && h > 0)
                                {
                                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                                    rtb.Render(capTarget);
                                    rtb.Freeze();
                                    bmp = rtb;
                                }
                                else
                                {
                                    System.Diagnostics.Trace.WriteLine(
                                        $"Screenshot fallback skipped — GameScreen size {w}x{h}");
                                }
                            }
                            catch (Exception fbEx)
                            {
                                System.Diagnostics.Trace.WriteLine($"Screenshot fallback failed: {fbEx.Message}");
                            }
                        });
                    }

                    if (bmp != null)
                    {
                        // Rotate screenshot to match display orientation (vertical arcade games etc.)
                        // Skipped under a bezel — the rendered composite already includes orientation.
                        if (coreRotation != 0 && !_bezelActive)
                        {
                            double angle = ((-(int)coreRotation * 90.0) % 360 + 360) % 360;
                            bmp = new TransformedBitmap(bmp, new RotateTransform(angle));
                        }
                        if (bmp.CanFreeze && !bmp.IsFrozen) bmp.Freeze();
                        using var fs = new FileStream(pngPath, FileMode.Create);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(bmp));
                        enc.Save(fs);
                        System.Diagnostics.Trace.WriteLine($"Screenshot saved: {pngPath}");
                    }
                    else
                    {
                        System.Diagnostics.Trace.WriteLine($"Screenshot not saved — every capture path returned empty for {safeName}");
                        pngPath = "";
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"Screenshot failed: {ex.Message}");
                    pngPath = "";
                }
                var meta = new
                {
                    Name        = name,
                    GameTitle   = _game.Title,
                    ConsoleName = _game.Console,
                    CoreName    = _core?.CoreName ?? "",
                    RomHash     = _game.RomHash ?? "",
                    CreatedAt   = DateTime.Now.ToString("o"),
                };
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(meta,
                    new JsonSerializerOptions { WriteIndented = true }));

                // Persist to database
                var ss = new SaveState
                {
                    GameId         = _game.Id,
                    Name           = name,
                    GameTitle      = _game.Title,
                    ConsoleName    = _game.Console,
                    CoreName       = meta.CoreName,
                    RomHash        = _game.RomHash ?? "",
                    StatePath      = statePath,
                    ScreenshotPath = pngPath,
                    CreatedAt      = DateTime.Now,
                };

                // If a state with the same name already exists for this game, overwrite its file paths.
                var existing = _db?.GetSaveStateByGameAndName(_game.Id, name);
                if (existing != null)
                {
                    _db?.UpdateSaveStateName(existing.Id, name, statePath, pngPath);
                    ss.Id = existing.Id;
                }
                else
                {
                    ss.Id = _db?.InsertSaveState(ss) ?? 0;
                    _db?.RecalcSaveCount(_game.Id);
                    _game.SaveCount++;
                }

                Dispatcher.BeginInvoke(() =>
                {
                    _transientMsg    = $"Saved: {name}";
                    _transientExpiry = DateTime.Now.AddSeconds(3);
                    PopulateLoadPicker();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"FinalizeSave error: {ex.Message}");
                _transientMsg    = "Save state failed";
                _transientExpiry = DateTime.Now.AddSeconds(5);
            }
        }

        /// <summary>Request a load by file path from the UI thread.</summary>
        private void RequestLoad(string statePath, string name)
        {
            // RA hardcore-compliance: loading save states is an auto-fail blocker
            // per https://docs.retroachievements.org/general/hardcore-compliance-requirements.html.
            // Saves continue to work — creating states is permitted, only loading is blocked.
            if (IsHardcoreActive())
            {
                _transientMsg    = "Save state loading is disabled in hardcore mode";
                _transientExpiry = DateTime.Now.AddSeconds(4);
                return;
            }
            if (IsSaveStateUnreliable())
            {
                _transientMsg    = "Save states are disabled for MAME 2003-Plus (unreliable per-game)";
                _transientExpiry = DateTime.Now.AddSeconds(5);
                return;
            }
            try
            {
                _pendingLoadData         = File.ReadAllBytes(statePath);
                _pendingLoadName         = name;
                _pendingLoadSavedCoreName = ReadSavedCoreName(statePath);
                // Pair the libretro state with the rcheevos progress side-car
                // when one exists. Older states predate the side-car and load
                // with a null blob, which DeserializeProgress treats as a no-op
                // (current rcheevos state is left untouched).
                _pendingLoadCheevosBlob = null;
                try
                {
                    string cheevosPath = Path.ChangeExtension(statePath, ".cheevos");
                    if (File.Exists(cheevosPath))
                        _pendingLoadCheevosBlob = File.ReadAllBytes(cheevosPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[RA] Cheevos side-car read failed: {ex.Message}");
                }
                _loadStatePending        = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not read state file: {ex.Message}";
            }
        }

        /// <summary>Called on the emu thread between retro_run calls.</summary>
        private void ExecuteLoadOnEmuThread()
        {
            byte[]? data = _pendingLoadData;
            string   name = _pendingLoadName;

            if (data == null)
            {
                _loadStatePending         = false;
                _loadStateAttempts        = 0;
                _loadStateWarmup          = 0;
                _pendingLoadSavedCoreName = "";
                return;
            }

            // Refuse cross-session restore on cores that declared
            // RETRO_SERIALIZATION_QUIRK_SINGLE_SESSION. Those cores document
            // that retro_unserialize is not valid across launches; the call
            // returns true but the loaded game freezes. Better to let the
            // game boot normally with a clear message than ship a frozen one.
            if (_coreSingleSessionStates)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[LoadState] skipped — core declares SINGLE_SESSION quirk: {name}");
                _transientMsg    = "This core doesn't support resuming save states across launches.";
                _transientExpiry = DateTime.Now.AddSeconds(6);
                _loadStatePending         = false;
                _loadStateAttempts        = 0;
                _pendingLoadData          = null;
                _pendingLoadSavedCoreName = "";
                _pendingLoadCheevosBlob   = null;
                Dispatcher.BeginInvoke(() => LoadPickerPanel.Visibility = Visibility.Collapsed);
                return;
            }

            // Refuse cross-core loads. Save state byte formats are NOT portable
            // between cores for the same system (Beetle PSX HW state can't be
            // loaded on Beetle PSX SW; a Genesis Plus GX state can't be loaded
            // on PicoDrive; etc.). retro_unserialize returns true for the
            // bytes that parse, but the loaded state is incoherent and the
            // game wedges with no way to recover. Empty saved-core means the
            // sidecar was missing — let those legacy states through (we can't
            // know what made them, and we don't want to break old saves).
            string activeCore = _core?.CoreName ?? "";
            if (!string.IsNullOrEmpty(_pendingLoadSavedCoreName)
                && !string.IsNullOrEmpty(activeCore)
                && !string.Equals(_pendingLoadSavedCoreName, activeCore, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[LoadState] refused — state was made on '{_pendingLoadSavedCoreName}' but active core is '{activeCore}': {name}");
                _transientMsg    = $"Save state was made with {_pendingLoadSavedCoreName}; current core is {activeCore}. Switch the per-game core or make a fresh state.";
                _transientExpiry = DateTime.Now.AddSeconds(8);
                _loadStatePending         = false;
                _loadStateAttempts        = 0;
                _loadStateWarmup          = 0;
                _pendingLoadData          = null;
                _pendingLoadSavedCoreName = "";
                _pendingLoadCheevosBlob   = null;
                Dispatcher.BeginInvoke(() => LoadPickerPanel.Visibility = Visibility.Collapsed);
                return;
            }

            // Warmup: drive the core through N retro_run frames after
            // context_reset before attempting unserialize. Required for HW
            // renderers (Beetle PSX HW especially) that defer VRAM uploads
            // through context_reset's queue drain — without warm frames the
            // post-load CPU stalls waiting on a GPU IRQ that never fires and
            // the game appears frozen on the saved frame.
            if (_loadStateWarmup < LoadStateWarmupFrames)
            {
                _loadStateWarmup++;
                return;
            }

            bool ok = _core?.LoadState(data) ?? false;

            // Retry across frames if the core hasn't reached a state where
            // retro_unserialize will accept this snapshot yet (typically PSX
            // cores during BIOS boot — serialize_size doesn't stabilize for
            // dozens of frames). Bail with an error after ~10 seconds.
            if (!ok && _loadStateAttempts < MaxLoadStateAttempts)
            {
                _loadStateAttempts++;
                return;
            }

            _loadStatePending         = false;
            _loadStateAttempts        = 0;
            _loadStateWarmup          = 0;
            _pendingLoadData          = null;
            _pendingLoadSavedCoreName = "";

            // Restore rcheevos's runtime state from the .cheevos side-car
            // (if one was paired with this .state). Only on a successful
            // core-side load — restoring rcheevos hits onto a failed/partial
            // emulation state would mis-credit unlocks. Empty/missing blob
            // is treated as a no-op by DeserializeProgress.
            if (ok)
            {
                _raClient?.DeserializeProgress(_pendingLoadCheevosBlob);
            }
            _pendingLoadCheevosBlob = null;

            // After successful unserialize, re-prime the controller port-device
            // assignments for all four ports. Beetle PSX HW's FrontIO rebuilds
            // its device pointers during state restore and the libretro input-
            // device assignment can dangle, leaving input dead even though
            // emulation appears alive. Cheap to do unconditionally.
            if (ok && _core != null)
            {
                try
                {
                    _consoleHandler?.ConfigureControllerPorts(_core);
                    System.Diagnostics.Trace.WriteLine("[LoadState] re-primed controller ports post-unserialize");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[LoadState] port re-prime failed: {ex.Message}");
                }
            }

            // Re-seat the disc post-unserialize for disc-streaming cores. Beetle
            // PSX HW's CDC loses its disc handle during state restore (upstream
            // issue #297) — any subsequent disc read stalls forever, so games
            // that stream constantly (FF8 across field/battle/FMV) appear to
            // load but freeze on the first access. The recovery is the same
            // pattern as a manual disk-swap: eject the tray, set the current
            // image index, then DEFER the re-insert (eject false) by ~100
            // frames so the CDC audio engine spins down properly. An
            // immediate re-insert is what was happening before and confuses
            // the Beetle PSX CDC just like an undelayed swap does.
            if (ok && _diskSetEjectState != null && _diskSetImageIndex != null
                   && _diskGetImageIndex != null)
            {
                try
                {
                    uint cur = _diskGetImageIndex();
                    _diskSetEjectState(true);
                    _diskSetImageIndex(cur);
                    _diskInsertPendingFrames = 100;
                    System.Diagnostics.Trace.WriteLine(
                        $"[LoadState] re-seated disc index {cur} post-unserialize (insert deferred 100 frames)");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[LoadState] disc re-seat failed: {ex.Message}");
                }
            }

            _transientMsg    = ok ? $"Loaded: {name}" : $"Failed to load: {name}";
            _transientExpiry = DateTime.Now.AddSeconds(3);
            System.Diagnostics.Trace.WriteLine(
                $"[LoadState] {(ok ? "succeeded" : "gave up")} after {(ok ? _loadStateAttempts : MaxLoadStateAttempts)} attempts: {name}");

            // Some cores wipe their cheat table on state load — re-apply so codes survive.
            // Snapshot the list before iterating to avoid racing the UI thread, which can
            // mutate _cheats from the cheat editor at any moment.
            if (ok && _core != null && _cheats.Count > 0)
            {
                var snapshot = new System.Collections.Generic.List<Models.Cheat>(_cheats);
                try { ApplyAllCheats(snapshot); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Cheats re-apply (post state-load) failed: {ex.Message}"); }
            }

            Dispatcher.BeginInvoke(() => LoadPickerPanel.Visibility = Visibility.Collapsed);
        }

        /// <summary>Populate the inline load picker with the last 5 save states for this game.</summary>
        private void PopulateLoadPicker()
        {
            var states = _db?.GetSaveStatesByGame(_game.Id).Take(5).ToList() ?? new();
            LoadPickerItems.Children.Clear();

            if (states.Count == 0)
            {
                LoadPickerEmpty.Visibility = Visibility.Visible;
                return;
            }
            LoadPickerEmpty.Visibility = Visibility.Collapsed;

            foreach (var s in states)
            {
                var row = new Border
                {
                    Padding         = new Thickness(6, 5, 6, 5),
                    Cursor          = System.Windows.Input.Cursors.Hand,
                    Background      = Brushes.Transparent,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush     = (Brush)FindResource("BorderSubtleBrush"),
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameText = new TextBlock
                {
                    Text               = s.Name,
                    FontFamily         = (FontFamily)FindResource("PrimaryFont"),
                    FontSize           = 11,
                    Foreground         = (Brush)FindResource("TextPrimaryBrush"),
                    VerticalAlignment  = VerticalAlignment.Center,
                    TextTrimming       = TextTrimming.CharacterEllipsis,
                };
                var timeText = new TextBlock
                {
                    Text               = s.RelativeTime,
                    FontFamily         = (FontFamily)FindResource("PrimaryFont"),
                    FontSize           = 10,
                    Foreground         = (Brush)FindResource("TextMutedBrush"),
                    VerticalAlignment  = VerticalAlignment.Center,
                    Margin             = new Thickness(8, 0, 0, 0),
                };
                Grid.SetColumn(nameText, 0);
                Grid.SetColumn(timeText, 1);
                grid.Children.Add(nameText);
                grid.Children.Add(timeText);
                row.Child = grid;

                var captured = s;
                row.MouseLeftButtonUp += (_, _) => RequestLoad(captured.StatePath, captured.Name);
                row.MouseEnter += (_, _) => row.Background = (Brush)FindResource("BgSecondaryBrush");
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

                LoadPickerItems.Children.Add(row);
            }
        }

        private void SaveStateBtn_Click(object sender, RoutedEventArgs e)
        {
            LoadPickerPanel.Visibility = Visibility.Collapsed;
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss");
            RequestSave(ts);
        }

        private void LoadStateBtn_Click(object sender, RoutedEventArgs e)
        {
            // RA hardcore-compliance: defense-in-depth. The button is hidden when
            // hardcore is active, but a programmatic invocation or a stale state
            // would still fall through here without this guard.
            if (IsHardcoreActive())
            {
                _transientMsg    = "Save state loading is disabled in hardcore mode";
                _transientExpiry = DateTime.Now.AddSeconds(4);
                return;
            }
            if (LoadPickerPanel.Visibility == Visibility.Visible)
            {
                LoadPickerPanel.Visibility = Visibility.Collapsed;
                return;
            }
            PopulateLoadPicker();
            LoadPickerPanel.Visibility = Visibility.Visible;
        }

        // =========================================================================
        // Overlay HUD
        // =========================================================================
        private bool _overlayHiding; // guards against stale fade-out Completed callbacks

        private void ShowOverlay()
        {
            _overlayHiding = false; // cancel any in-flight hide

            // Belt-and-suspenders: if the mouse poller stopped (it shouldn't, but if any
            // exception in a Tick handler ever takes it down, the overlay would be locked
            // hidden forever). Restart it whenever Show is called.
            if (_mousePoller != null && !_mousePoller.IsEnabled)
                _mousePoller.Start();

            // Overlay window path (Vulkan or GL): show HUD in a separate window above
            // the overlay so both the game and the HUD are visible simultaneously
            if ((_vulkanOverlayHwnd != IntPtr.Zero && _vulkanPresenting) || _glOverlayHwnd != IntPtr.Zero)
            {
                EnsureVulkanHudWindow();
                // Reparent OverlayHud into the HUD window (once)
                if (OverlayHud.Parent == GameViewport)
                {
                    GameViewport.Children.Remove(OverlayHud);
                    _vulkanHudGrid!.Children.Add(OverlayHud);
                }
                // Same for the load-state picker — otherwise the WS_POPUP overlay
                // covers it and the user sees nothing when clicking the Load button.
                if (LoadPickerPanel.Parent == GameViewport)
                {
                    GameViewport.Children.Remove(LoadPickerPanel);
                    _vulkanHudGrid!.Children.Add(LoadPickerPanel);
                }
                if (VisualsPanel.Parent == GameViewport)
                {
                    GameViewport.Children.Remove(VisualsPanel);
                    _vulkanHudGrid!.Children.Add(VisualsPanel);
                }
                // Always cancel any in-flight fade-out animation before re-showing,
                // otherwise its Completed callback can race us back to Collapsed.
                OverlayHud.BeginAnimation(OpacityProperty, null);
                if (OverlayHud.Visibility != Visibility.Visible)
                {
                    OverlayHud.Visibility = Visibility.Visible;
                    var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                    OverlayHud.BeginAnimation(OpacityProperty, fade);
                }
                else
                {
                    OverlayHud.Opacity = 1.0; // ensure visible if a prior fade left it partial
                }
                OverlayHud.IsHitTestVisible = true;
                RepositionVulkanHud();
                _vulkanHudWindow!.Show();
                // Ensure HUD window is above the Vulkan overlay
                var hudHwnd = new System.Windows.Interop.WindowInteropHelper(_vulkanHudWindow).Handle;
                if (hudHwnd != IntPtr.Zero)
                {
                    const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
                    SetWindowPos(hudHwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }
            else
            {
                // Non-Vulkan path: show HUD in the main window
                OverlayHud.BeginAnimation(OpacityProperty, null);
                if (OverlayHud.Visibility != Visibility.Visible)
                {
                    OverlayHud.Visibility = Visibility.Visible;
                    var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                    OverlayHud.BeginAnimation(OpacityProperty, fade);
                }
                else
                {
                    OverlayHud.Opacity = 1.0;
                }
                OverlayHud.IsHitTestVisible = true;
            }
            if (_isFullscreen && !_mouseCaptured)
                Mouse.OverrideCursor = null;
            _overlayTimer?.Stop();
            _overlayTimer?.Start();
        }

        private void HideOverlay()
        {
            // Defensive: never hide while a submenu is active.  The timer guard catches
            // the common case, but other call-sites (and future ones) shouldn't have to
            // remember this rule.
            if (OverlayMenu.Visibility == Visibility.Visible
                || CheatsMenu.Visibility == Visibility.Visible
                || SaveMenu.Visibility == Visibility.Visible
                || VisualsPanel.Visibility == Visibility.Visible
                || ShaderPanel.Visibility == Visibility.Visible)
            {
                _overlayTimer?.Stop();
                _overlayTimer?.Start();
                return;
            }
            _overlayHiding = true;
            _overlayTimer?.Stop();
            OverlayMenu.Visibility = Visibility.Collapsed;
            CheatsMenu.Visibility = Visibility.Collapsed;
            CloseSaveMenu();
            if (_isFullscreen && !_mouseCaptured)
                Mouse.OverrideCursor = System.Windows.Input.Cursors.None;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fade.Completed += (_, _) =>
            {
                if (!_overlayHiding) return;
                OverlayHud.Visibility = Visibility.Collapsed;
                // Vulkan path: hide the HUD window — but only if nothing else is
                // using it. The pause effect (which gets reparented into the same
                // _vulkanHudGrid on Vulkan consoles) needs the window to stay
                // visible while the game is paused, otherwise hiding the HUD pill
                // also hides the screensaver.
                if (_vulkanHudWindow != null && _vulkanHudWindow.IsVisible
                    && !IsPauseEffectActive())
                    _vulkanHudWindow.Hide();
            };
            OverlayHud.BeginAnimation(OpacityProperty, fade);
        }

        private void EnsureVulkanHudWindow()
        {
            if (_vulkanHudWindow != null) return;
            _vulkanHudGrid = new Grid();
            _vulkanHudWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                Content = _vulkanHudGrid,
                Owner = this,
                // Critical: don't steal focus from the emulator window.
                ShowActivated = false,
                Focusable = false,
            };
            // Apply WS_EX_NOACTIVATE so even clicks on HUD content don't activate the
            // window — clicks on cog/cheats buttons would otherwise pull foreground off
            // the Vulkan presentation hwnd, leaving the HUD wedged on some drivers.
            _vulkanHudWindow.SourceInitialized += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(_vulkanHudWindow!).Handle;
                if (hwnd == IntPtr.Zero) return;
                const int GWL_EXSTYLE = -20;
                const int WS_EX_NOACTIVATE = 0x08000000;
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE));
            };
        }

        private void RepositionVulkanHud()
        {
            if (_vulkanHudWindow == null) return;
            var hudHwnd = new System.Windows.Interop.WindowInteropHelper(_vulkanHudWindow).Handle;
            if (hudHwnd == IntPtr.Zero) return;
            try
            {
                var viewportPoint = GameViewport.PointToScreen(new System.Windows.Point(0, 0));
                int vx = (int)viewportPoint.X;
                int vy = (int)viewportPoint.Y;
                int vw = Math.Max(1, (int)GameViewport.ActualWidth);
                int vh = Math.Max(1, (int)GameViewport.ActualHeight);
                const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
                SetWindowPos(hudHwnd, IntPtr.Zero, vx, vy, vw, vh, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch { }
        }

        private void ResetOverlayTimer()
        {
            _overlayTimer?.Stop();
            _overlayTimer?.Start();
        }

        // ── RetroAchievements initialization ─────────────────────────────────

        /// <summary>
        /// True when an RA session is live AND was launched in hardcore mode.
        /// All hardcore gates (save-state load, cheats, HUD indicator) read
        /// from this. _raClient being null means RA didn't initialize for this
        /// launch — no creds, unsupported console, login failed — so there's
        /// no hardcore session to enforce against and the gates relax.
        ///
        /// The HardcoreMode value is snapshotted at <see cref="InitRetroAchievements"/>
        /// time rather than re-read live; see _raHardcoreActive comment.
        /// </summary>
        private bool IsHardcoreActive() => _raClient != null && _raHardcoreActive;

        /// <summary>
        /// Hides in-HUD affordances that don't apply in hardcore: the Load
        /// State buttons (loads are blocked) and the Cheats button (cheat
        /// codes can't apply). Called once after RA init completes; visibility
        /// is fixed for the session because <see cref="_raHardcoreActive"/>
        /// is snapshotted at launch.
        /// </summary>
        private void ApplyHardcoreHudVisibility()
        {
            if (!IsHardcoreActive()) return;
            try
            {
                if (LoadStateBtn != null) LoadStateBtn.Visibility = Visibility.Collapsed;
                if (LoadStateHoverBtn != null) LoadStateHoverBtn.Visibility = Visibility.Collapsed;
                if (OverlayCheatsBtn != null) OverlayCheatsBtn.Visibility = Visibility.Collapsed;
                // RA compliance Section E: hardcore state must be visibly indicated
                // during play. Lives in the persistent status bar, not the fading
                // overlay, so it's always on screen.
                if (HardcoreIndicator != null) HardcoreIndicator.Visibility = Visibility.Visible;
            }
            catch { /* HUD elements may be unavailable in unusual init orderings */ }
        }

        private void InitRetroAchievements()
        {
            try
            {
                var raConfig = _configService.GetRetroAchievementsConfiguration();
                if (!raConfig.IsConfigured)
                {
                    System.Diagnostics.Trace.WriteLine("[RA] Not signed in — skipping.");
                    return;
                }

                uint consoleId = RetroAchievementsClient.GetConsoleId(_game.Console);
                if (consoleId == 0)
                {
                    System.Diagnostics.Trace.WriteLine($"[RA] No RA console ID for '{_game.Console}' — skipping.");
                    Dispatcher.BeginInvoke(() => _transientMsg = $"RetroAchievements: {_game.Console} not supported");
                    return;
                }

                // RA hardcore-compliance carve-out for PSP. The PPSSPP libretro
                // core reads cheats from cheats/<DiscID>.ini directly, bypassing
                // the libretro retro_cheat_set frontend API our ApplyAllCheats
                // gate intercepts. libretro exposes no environment callback for
                // the frontend to communicate hardcore state, so we can't reach
                // into PPSSPP's cheat-load path from out here. Until upstream
                // PPSSPP gains a hardcore-aware behavior in libretro mode, the
                // conservative posture is to refuse to run hardcore on PSP —
                // we drop to softcore for this session and surface a transient
                // explaining why. Achievements still track in softcore; just
                // not on the hardcore leaderboard.
                bool effectiveHardcore = raConfig.HardcoreMode;
                if (effectiveHardcore && string.Equals(_game.Console, "PSP", StringComparison.Ordinal))
                {
                    effectiveHardcore = false;
                    System.Diagnostics.Trace.WriteLine("[RA] Hardcore mode refused for PSP — PPSSPP cheats path not gateable from the frontend; dropping to softcore for this session.");
                    Dispatcher.BeginInvoke(() =>
                    {
                        _transientMsg = "Hardcore Mode is disabled for PSP titles — achievements still track";
                        _transientExpiry = DateTime.Now.AddSeconds(6);
                    });
                }

                // Stamp the active libretro core into the rcheevos HTTP User-Agent
                // so RA's logs can correlate unlock requests to a specific core +
                // version. Per RA's UA format: "Emutastic/<v> (OS) coreName/coreVersion".
                // Must happen BEFORE the client's login/identify HTTP calls fire.
                RetroAchievementsClient.SetCoreContext(_core.CoreName, _core.CoreVersion);

                _raClient = new RetroAchievementsClient();
                _raHardcoreActive = effectiveHardcore;
                _raClient.Initialize(_core, effectiveHardcore, _game.Console);

                // Replay any memory descriptors the core published during LoadGame
                // (before _raClient existed). Without this, the descriptor-aware
                // memory-read path is dead code and the legacy fallback runs.
                if (_pendingMemoryRegions != null)
                    _raClient.SetMemoryDescriptors(_pendingMemoryRegions);

                // Subscribe to events — marshal to UI thread for toast display
                _raClient.AchievementTriggered += info =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        ShowAchievementToast(info.Title, info.Description, info.Points, info.BadgeUrl);
                        try
                        {
                            var fcfg = App.Configuration?.GetFriendsConfiguration();
                            if (fcfg == null)
                            {
                                Services.RaLog.Write($"[AchSound] FriendsConfig null — sound skipped");
                                return;
                            }
                            double sinceLast = (DateTime.UtcNow - _lastAchievementSoundUtc).TotalSeconds;
                            Services.RaLog.Write($"[AchSound] ach=[{info.Title}] soundEnabled={fcfg.LbToastSoundEnabled} cooldownSec={fcfg.LbToastCooldownSec} sinceLastSec={sinceLast:F1} volume={fcfg.LbToastSoundVolume}");
                            if (fcfg.LbToastSoundEnabled
                                && sinceLast >= fcfg.LbToastCooldownSec)
                            {
                                Services.FriendNotificationSound.Play(App.Configuration);
                                _lastAchievementSoundUtc = DateTime.UtcNow;
                                Services.RaLog.Write($"[AchSound] Play() invoked");
                            }
                            else
                            {
                                Services.RaLog.Write($"[AchSound] suppressed (enabled={fcfg.LbToastSoundEnabled} cooldownActive={sinceLast < fcfg.LbToastCooldownSec})");
                            }
                        }
                        catch (Exception ex)
                        {
                            Services.RaLog.Write($"[AchSound] EX: {ex.GetType().Name} {ex.Message}");
                        }
                    });
                };
                _raClient.GameCompleted += () =>
                {
                    Dispatcher.BeginInvoke(() => ShowAchievementToast("Mastery!", "All achievements earned!", 0, header: "GAME COMPLETE"));
                };
                // In-game indicators (challenge badges bottom-right, measured-
                // progress pill top-right). Events arrive on the emu thread.
                _raClient.ChallengeIndicatorChanged += (info, shown) =>
                {
                    Dispatcher.BeginInvoke(() => SetRaChallenge(info, shown));
                };
                _raClient.ProgressIndicatorChanged += (info, shown) =>
                {
                    Dispatcher.BeginInvoke(() => SetRaProgress(info, shown));
                };
                // Phase 6b.1: leaderboard SCOREBOARD post-submission. The
                // decision (triumph vs proximity vs neither) runs against
                // the FriendService's pre-fetched per-game friend ranks.
                _raClient.LeaderboardScoreboardReceived += info =>
                {
                    Dispatcher.BeginInvoke(() => HandleLbScoreboard(info));
                };

                // Try token login first, fall back to password login
                System.Diagnostics.Trace.WriteLine($"[RA] Logging in as {raConfig.Username}...");
                bool loginOk = false;
                string? loginErr = null;
                string? newToken = null;

                if (!string.IsNullOrWhiteSpace(raConfig.Token))
                {
                    System.Diagnostics.Trace.WriteLine("[RA] Attempting token login...");
                    (loginOk, loginErr, newToken) = _raClient.LoginWithToken(raConfig.Username, raConfig.Token);
                }

                if (!loginOk && !string.IsNullOrWhiteSpace(raConfig.Password))
                {
                    System.Diagnostics.Trace.WriteLine("[RA] Token login failed or no token, trying password...");
                    (loginOk, loginErr, newToken) = _raClient.LoginWithPassword(raConfig.Username, raConfig.Password);

                    // Save the token for next time so the password isn't needed again
                    if (loginOk && !string.IsNullOrWhiteSpace(newToken))
                    {
                        raConfig.Token = newToken;
                        _configService.SetRetroAchievementsConfiguration(raConfig);
                        _ = _configService.SaveAsync();
                        System.Diagnostics.Trace.WriteLine("[RA] Login token saved for future sessions.");
                    }
                }

                if (!loginOk)
                {
                    System.Diagnostics.Trace.WriteLine($"[RA] Login failed: {loginErr}");
                    Dispatcher.BeginInvoke(() => _transientMsg = "RetroAchievements: login failed");
                    _raClient.Dispose();
                    _raClient = null;
                    return;
                }
                System.Diagnostics.Trace.WriteLine("[RA] Login OK");

                System.Diagnostics.Trace.WriteLine($"[RA] Loading game: {_game.RomPath} (console {consoleId})");
                var (loadOk, loadErr) = _raClient.LoadGame(_game.RomPath, consoleId);
                if (!loadOk)
                {
                    System.Diagnostics.Trace.WriteLine($"[RA] Game load failed: {loadErr}");
                    Dispatcher.BeginInvoke(() => _transientMsg = "RetroAchievements: game not in database");
                    // Persist the outcome so the Detail card can show a clear
                    // status next time the user opens it, instead of an empty
                    // RA section that looks identical to "never launched."
                    // rcheevos returns two distinct "no playable achievements"
                    // strings: "Unknown game" (hash didn't match anything in
                    // RA's database) and "Response contained no sets" (hash
                    // matched but the game has no authored achievement set).
                    // Both should read to the user as "no achievements
                    // available," so they're bucketed together as
                    // not_in_database. Anything else (network failure,
                    // timeout, credential reject) is a generic load_failed.
                    string err = loadErr ?? "";
                    bool noAchievements =
                           err.IndexOf("unknown game",      StringComparison.OrdinalIgnoreCase) >= 0
                        || err.IndexOf("no sets",           StringComparison.OrdinalIgnoreCase) >= 0
                        || err.IndexOf("response contained", StringComparison.OrdinalIgnoreCase) >= 0;
                    string outcome = noAchievements ? "not_in_database" : "load_failed";
                    Emutastic.Services.RaLog.Write(
                        $"launch identify failed: localId={_game.Id} console={_game.Console} " +
                        $"title=\"{_game.Title}\" outcome={outcome} err=\"{err}\"");
                    try
                    {
                        _game.RALastLaunchOutcome = outcome;
                        _db?.UpdateRALastLaunchOutcome(_game.Id, outcome);
                    }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[RA] Persist outcome failed: {ex.Message}"); }
                    _raClient.Dispose();
                    _raClient = null;
                    return;
                }

                string? gameTitle = _raClient.GetGameTitle();
                int raGameId = _raClient.GetGameId();
                System.Diagnostics.Trace.WriteLine($"[RA] Game identified: {gameTitle} (id={raGameId})");
                Emutastic.Services.RaLog.Write(
                    $"launch identified: localId={_game.Id} console={_game.Console} " +
                    $"title=\"{_game.Title}\" raGameId={raGameId} raTitle=\"{gameTitle}\" " +
                    $"existingRAGameId={_game.RAGameId} dbNull={(_db == null)} " +
                    $"dbPath=\"{_db?.DbPath ?? "<null>"}\" portable={AppPaths.IsPortable}");
                // Persist the success outcome so the Detail card knows this
                // game has a verified RA catalog entry without needing the
                // Web API fetch to have already landed.
                if (!string.Equals(_game.RALastLaunchOutcome, "identified", StringComparison.Ordinal))
                {
                    try
                    {
                        _game.RALastLaunchOutcome = "identified";
                        _db?.UpdateRALastLaunchOutcome(_game.Id, "identified");
                    }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[RA] Persist outcome failed: {ex.Message}"); }
                }

                // Cache the RA game ID on the Game row so the detail card's
                // Web API fetch can skip the hash-resolve roundtrip on every
                // subsequent library visit. Idempotent — same value is written
                // each launch.
                if (raGameId > 0 && _game.RAGameId != raGameId)
                {
                    _game.RAGameId = raGameId;
                    if (_db == null)
                    {
                        Emutastic.Services.RaLog.Write($"persist SKIPPED: _db is null — RAGameId={raGameId} not saved for localId={_game.Id}");
                    }
                    else
                    {
                        try
                        {
                            int rows = _db.UpdateRAGameIdReturningCount(_game.Id, raGameId);
                            Emutastic.Services.RaLog.Write($"persist: localId={_game.Id} raGameId={raGameId} rowsAffected={rows}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Trace.WriteLine($"[RA] Failed to persist RAGameId: {ex.Message}");
                            Emutastic.Services.RaLog.Write($"persist FAILED: localId={_game.Id} raGameId={raGameId} ex={ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }

                Dispatcher.BeginInvoke(() =>
                {
                    _transientMsg = $"RetroAchievements: {gameTitle}";
                });

                // Phase 6b.1: kick the friend-rank pre-fetch for this
                // game's LBs. Fire-and-forget; the rcheevos SCOREBOARD
                // handler will consult AllFriendLbRanks on submission.
                // Cancelled on game close via EndCurrentGameLbPrefetch
                // from the cleanup path.
                if (raGameId > 0)
                {
                    try { GetFriendService()?.StartFriendLbPrefetch(raGameId); }
                    catch (Exception lbex) { Emutastic.Services.RaLog.Write($"[LbPrefetch] kick EX: {lbex.Message}"); }
                }
            }
            catch (DllNotFoundException)
            {
                System.Diagnostics.Trace.WriteLine("[RA] rcheevos.dll not found — achievements disabled.");
                _raClient = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[RA] Init error: {ex.Message}");
                try { _raClient?.Dispose(); } catch { }
                _raClient = null;
            }
        }

        /// <summary>
        /// Called from the emu-loop's finally block. Snapshots the live-progress
        /// dict accumulated during play and offloads the SQLite write to a
        /// Task-pool thread so the emu-thread teardown never blocks on disk.
        /// Skipped silently when RA wasn't active or no progress events fired.
        /// </summary>
        private void FlushLiveProgressOnExit()
        {
            try
            {
                if (_raClient == null || _game == null || _game.RAGameId <= 0) return;

                var snap = _raClient.GetLiveProgressSnapshot();
                if (snap.Count == 0) return;

                bool hardcore = App.Configuration?.GetRetroAchievementsConfiguration()?.HardcoreMode == true;
                var payload = new Emutastic.Models.RALiveProgress { Hardcore = hardcore };

                // Defensive cap. A typical RA set has <50 measured achievements;
                // capping at 50 by descending percent keeps the JSON small even
                // if some future set ships 200+ progress-tracked achievements.
                const int MaxPerGame = 50;
                var topByPercent = snap
                    .OrderByDescending(kvp => kvp.Value.MeasuredPercent)
                    .Take(MaxPerGame);
                foreach (var kvp in topByPercent)
                {
                    payload.Achievements[kvp.Key] = new Emutastic.Models.RALiveAchievementProgress
                    {
                        Percent = kvp.Value.MeasuredPercent,
                        ProgressText = kvp.Value.MeasuredProgress ?? "",
                    };
                }

                string json = System.Text.Json.JsonSerializer.Serialize(payload);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int gameId = _game.Id;

                // Mirror the persisted value onto the live Game object so the
                // detail card can read it the moment the user exits without
                // waiting for a DB re-read.
                _game.RALiveProgressJson = json;
                _game.RALiveProgressFetchedAt = now;

                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var db = new Services.DatabaseService();
                        db.UpdateRALiveProgress(gameId, json, now);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"[RA] live-progress flush failed: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[RA] FlushLiveProgressOnExit: {ex.Message}");
            }
        }

        private DispatcherTimer? _achievementToastTimer;

        // ── Phase 6b.1: leaderboard SCOREBOARD handling ──────────────────
        // Per-LB cooldown to prevent burst spam in shmup/pinball sessions
        // where the user submits many scores in close succession. Each LB
        // can toast at most once per LbToastCooldownSec window.
        private readonly System.Collections.Generic.Dictionary<int, DateTimeOffset> _lbToastCooldown = new();
        // Cooldown for the achievement-unlock chime. Shares
        // LbToastCooldownSec so rapid unlock chains don't pile up.
        private DateTime _lastAchievementSoundUtc = DateTime.MinValue;
        private FriendService? _friendServiceForLb;

        // Lazy fetch of FriendService — same lazy ctor pattern MainWindow
        // uses; lets us read friend list + pre-fetched LB ranks without
        // pulling it through every layer of EmulatorWindow construction.
        private FriendService? GetFriendService()
        {
            if (_friendServiceForLb != null) return _friendServiceForLb;
            try
            {
                if (App.Configuration != null && _db != null)
                {
                    _friendServiceForLb = new FriendService(App.Configuration, _db,
                        new RetroAchievementsService(App.Configuration, _db));
                }
            }
            catch { /* leave null — handler short-circuits */ }
            return _friendServiceForLb;
        }

        private void HandleLbScoreboard(RetroAchievementsClient.LbScoreboardInfo info)
        {
            try
            {
                var cfg = App.Configuration?.GetFriendsConfiguration();
                if (cfg == null) return;
                if (!cfg.LbToastWhenYouBeat && !cfg.LbToastForProximity) return;
                // Respect the user's "hardcore-only toasts" preference. If
                // they've asked for HC-only signal and this session is
                // softcore (PSP carve-out or HC disabled in this session),
                // suppress LB toasts wholesale — matches the achievement
                // toast behavior at MainWindow.xaml.cs:3835.
                if (cfg.HardcoreOnlyToast && !IsHardcoreActive())
                {
                    RaLog.Write($"[LbToast] HardcoreOnlyToast=true and session is softcore — suppressing");
                    return;
                }

                // Guard against rcheevos failure submissions (info.NewRank == 0
                // means the submit didn't land server-side). Without this,
                // "newRank <= friendRank" trivially fires for every friend.
                if (info.NewRank == 0)
                {
                    RaLog.Write($"[LbToast] new_rank=0 (failed submit?) — skipping");
                    return;
                }

                // Cooldown gate + opportunistic prune of stale entries.
                var now = DateTimeOffset.UtcNow;
                var cooldownTtl = TimeSpan.FromSeconds(cfg.LbToastCooldownSec);
                var staleThreshold = now - TimeSpan.FromSeconds(cfg.LbToastCooldownSec * 2);
                var stale = new System.Collections.Generic.List<int>();
                foreach (var kv in _lbToastCooldown)
                    if (kv.Value < staleThreshold) stale.Add(kv.Key);
                foreach (var k in stale) _lbToastCooldown.Remove(k);
                if (_lbToastCooldown.TryGetValue(info.LeaderboardId, out var lastFired)
                    && (now - lastFired) < cooldownTtl)
                {
                    RaLog.Write($"[LbToast] cooldown active for lb={info.LeaderboardId}, suppressing");
                    return;
                }

                var svc = GetFriendService();
                var friendRanks = svc?.AllFriendLbRanks;
                if (svc == null || friendRanks == null || friendRanks.Count == 0)
                {
                    RaLog.Write($"[LbToast] no friend ranks cached — skipping (svc={(svc == null ? "null" : "ok")} ranks={friendRanks?.Count ?? 0})");
                    return;
                }

                // Game title + console — pull from the current game record.
                string gameTitle = _game?.Title ?? "";
                string consoleName = _game?.Console ?? "";

                // Triumph requires knowing my OLD rank too — "did I cross
                // the friend?" is `oldRank > friendRank && newRank <= friendRank`.
                // The pre-fetch caches both my ranks and friends' ranks
                // at game-load; without it we'd false-positive on every
                // submission where I'm above a friend who was ALWAYS
                // below me.
                var myOld = svc.GetMyLbScore(info.LeaderboardId);
                int myOldRank = myOld?.Rank ?? int.MaxValue; // no prior entry = effectively last

                // Snapshot friend list ONCE — the svc.Friends getter
                // re-materializes a fresh array per call, so reading it
                // inside the loop would be O(N²) for an N-friend list.
                var friendsByUid = svc.Friends.ToDictionary(f => f.UserId);

                // Walk all friends; collect candidates I just CROSSED
                // (triumph) and candidates I'm close behind (proximity).
                var triumphs = new System.Collections.Generic.List<(string user, FriendLbScore prev)>();
                var nearMisses = new System.Collections.Generic.List<(string user, FriendLbScore other, long gap)>();
                foreach (var kv in friendRanks)
                {
                    int friendUid = kv.Key;
                    var byLb = kv.Value;
                    if (!byLb.TryGetValue(info.LeaderboardId, out var fScore)) continue;
                    if (fScore.Rank <= 0) continue;
                    // Per-friend mute applies symmetrically: "no notifications
                    // about this user" means YOU beating them shouldn't toast
                    // either. The polling path already gates at
                    // FriendService.cs:970; this is the matching gate on the
                    // SCOREBOARD-event path.
                    if (!friendsByUid.TryGetValue(friendUid, out var friend)) continue;
                    if (!friend.ToastsEnabled) continue;
                    string fUser = friend.Username;

                    // Triumph: I was below the friend (myOldRank > fScore.Rank)
                    // AND I'm now at or above (info.NewRank <= fScore.Rank).
                    if (cfg.LbToastWhenYouBeat
                        && myOldRank > fScore.Rank
                        && info.NewRank <= fScore.Rank)
                    {
                        triumphs.Add((fUser, fScore));
                    }

                    // Proximity: I'm now BEHIND the friend by a small rank
                    // gap. Phase 6b.1 uses rank gap (direction-agnostic);
                    // score-percentage proximity needs lower_is_better
                    // (info.LowerIsBetter now reads correctly after the
                    // struct-layout fix; revisit if score-pct UX is
                    // wanted). The configured pct threshold is reused as
                    // a max rank gap for now — "within 5 ranks" maps
                    // cleanly to "within 5%" semantics on small LBs.
                    int rankGapThreshold = Math.Max(1, cfg.LbToastProximityPct);
                    if (cfg.LbToastForProximity
                        && fScore.Rank < info.NewRank
                        && (info.NewRank - fScore.Rank) <= rankGapThreshold)
                    {
                        long rankGap = info.NewRank - fScore.Rank;
                        nearMisses.Add((fUser, fScore, rankGap));
                    }
                }

                // Update my cached rank after the comparison so the next
                // submission compares against the post-submission baseline.
                svc.UpdateMyLbRank(info.LeaderboardId, info.NewRank, info.SubmittedScore);

                if (triumphs.Count == 0 && nearMisses.Count == 0)
                {
                    RaLog.Write($"[LbToast] no candidates for lb={info.LeaderboardId} (rank=#{info.NewRank})");
                    return;
                }

                _lbToastCooldown[info.LeaderboardId] = now;

                // Defend the title string if rcheevos somehow gave us an
                // empty one (the event ALWAYS includes the leaderboard
                // pointer per rc_client semantics, but the IntPtr.Zero
                // guard at marshal time can produce ""). Better to ship
                // "this LB" than a dangling preposition.
                string lbTitleDisplay = string.IsNullOrWhiteSpace(info.LbTitle)
                    ? "this leaderboard" : info.LbTitle;

                if (triumphs.Count > 0)
                {
                    var first = triumphs[0];
                    // Centralized copy via FriendsCopy. The multi-friend
                    // case falls outside the helper's per-friend shape;
                    // build inline with "and N others" suffix.
                    string headline, subline;
                    if (triumphs.Count == 1)
                        (headline, subline) = FriendsCopy.LbTriumphYou(first.user, lbTitleDisplay, gameTitle, consoleName);
                    else
                    {
                        headline = $"You beat {first.user} and {triumphs.Count - 1} other{(triumphs.Count > 2 ? "s" : "")} on {lbTitleDisplay}";
                        subline = string.IsNullOrEmpty(consoleName) ? gameTitle : $"{gameTitle} · {consoleName}";
                    }
                    RaLog.Write($"[LbToast] TRIUMPH lb={info.LeaderboardId} title=[{lbTitleDisplay}] passed={triumphs.Count}");
                    ShowAchievementToast(headline, subline, 0);
                    FriendNotificationSound.Play(App.Configuration);
                }
                else if (nearMisses.Count > 0)
                {
                    var closest = nearMisses.OrderBy(n => n.gap).First();
                    string gapDesc = $"{closest.gap} rank{(closest.gap == 1 ? "" : "s")}";
                    var (headline, subline) = FriendsCopy.LbProximity(closest.user, gapDesc, lbTitleDisplay, gameTitle, consoleName);
                    RaLog.Write($"[LbToast] PROXIMITY lb={info.LeaderboardId} title=[{lbTitleDisplay}] closest={closest.user} gap={closest.gap}");
                    ShowAchievementToast(headline, subline, 0);
                    // No sound on proximity — informational, not celebratory.
                }
            }
            catch (Exception ex)
            {
                RaLog.Write($"[LbToast] HandleLbScoreboard EX: {ex.GetType().Name} {ex.Message}");
            }
        }

        // Cache decoded badge bitmaps so a re-unlock (or rapid chain) reuses the
        // download instead of refetching from media.retroachievements.org.
        private readonly System.Collections.Generic.Dictionary<string, System.Windows.Media.Imaging.BitmapImage> _badgeCache = new();
        // URL of the badge the currently-shown toast wants — guards against a
        // slow download for a superseded unlock painting over a newer one.
        private string? _toastBadgeUrl;
        // Same guard for the measured-progress pill's badge.
        private string? _progressBadgeUrl;

        /// <summary>
        /// Mirror of the HUD-pill reparenting pattern: on HW-rendered cores
        /// (Vulkan / OpenGL) the game render lives in a WS_POPUP overlay that
        /// covers the WPF main window. Without reparenting into the dedicated
        /// HUD window, a toast/indicator would draw underneath and be invisible
        /// even though the underlying event fired and submitted server-side.
        /// No-op for software-rendered cores (element stays in GameViewport).
        /// </summary>
        private void MoveHudElementToOverlay(FrameworkElement el)
        {
            bool useOverlayWindow = (_vulkanOverlayHwnd != IntPtr.Zero && _vulkanPresenting)
                                 || _glOverlayHwnd != IntPtr.Zero;
            if (!useOverlayWindow) return;

            EnsureVulkanHudWindow();
            if (el.Parent == GameViewport)
            {
                GameViewport.Children.Remove(el);
                _vulkanHudGrid!.Children.Add(el);
            }
            RepositionVulkanHud();
            if (!_vulkanHudWindow!.IsVisible) _vulkanHudWindow.Show();
            var hudHwnd = new System.Windows.Interop.WindowInteropHelper(_vulkanHudWindow).Handle;
            if (hudHwnd != IntPtr.Zero)
            {
                const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
                SetWindowPos(hudHwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }

        // ── RA challenge + progress indicators ──────────────────────────────
        // rcheevos CHALLENGE_/PROGRESS_INDICATOR events, RetroArch-standard
        // presentation (matches the Linux port): primed challenge badges sit
        // bottom-right while the attempt is live; a transient measured-progress
        // pill ("50/100") with the achievement's badge shows top-right. rcheevos
        // drives ALL show/update/hide timing — no local timers.
        private readonly System.Collections.Generic.Dictionary<uint, Image> _raChallengeBadges = new();

        private void SetRaChallenge(Services.AchievementInfo info, bool shown)
        {
            if (!shown)
            {
                if (_raChallengeBadges.Remove(info.Id, out var img))
                    RaChallengeStrip.Children.Remove(img);
            }
            else if (!_raChallengeBadges.ContainsKey(info.Id))
            {
                var img = new Image
                {
                    Width = 32, Height = 32,
                    Margin = new Thickness(4, 0, 0, 0),
                    ToolTip = info.Title
                };
                _raChallengeBadges[info.Id] = img;
                RaChallengeStrip.Children.Add(img);
                if (info.BadgeUrl != null)
                {
                    uint id = info.Id;
                    LoadBadgeAsync(info.BadgeUrl, bmp =>
                    {
                        // Only apply if this challenge is still primed.
                        if (_raChallengeBadges.TryGetValue(id, out var liveImg))
                            liveImg.Source = bmp;
                    });
                }
            }

            RaChallengeStrip.Visibility = _raChallengeBadges.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            if (_raChallengeBadges.Count > 0)
                MoveHudElementToOverlay(RaChallengeStrip);
        }

        private void SetRaProgress(Services.AchievementInfo? info, bool shown)
        {
            if (!shown || info == null)
            {
                RaProgressPill.Visibility = Visibility.Collapsed;
                return;
            }

            RaProgressText.Text = !string.IsNullOrEmpty(info.MeasuredProgress)
                ? info.MeasuredProgress
                : $"{info.MeasuredPercent:0}%";
            RaProgressBadgeBrush.ImageSource = null;
            if (info.BadgeUrl != null)
            {
                string badgeUrl = info.BadgeUrl;
                _progressBadgeUrl = badgeUrl;
                LoadBadgeAsync(badgeUrl, bmp =>
                {
                    // Only apply if the pill is still showing this achievement.
                    if (_progressBadgeUrl == badgeUrl) RaProgressBadgeBrush.ImageSource = bmp;
                });
            }
            else _progressBadgeUrl = null;

            MoveHudElementToOverlay(RaProgressPill);
            RaProgressPill.Visibility = Visibility.Visible;
        }

        private void ShowAchievementToast(string title, string description, uint points,
                                          string? badgeUrl = null, string? header = null)
        {
            // Apply the user's style FIRST. The renderer sets every visual property
            // (background/border/shadow/shape/colors/fonts/sizes/position) plus the
            // STYLE-driven badge/header visibility (ShowBadge/ShowHeader). The per-unlock
            // content logic below may only further COLLAPSE — never force-show — so the
            // effective rule is (ShowX AND has-content). (Phase-2 audit carry-forward.)
            var style = _configService.GetRetroAchievementsConfiguration().ToastStyle;
            Services.ToastStyleRenderer.ApplyTo(
                AchievementToast, AchievementBadge, AchievementHeader,
                AchievementTitle, AchievementDesc, AchievementPoints,
                style, LoadLocalImage);

            // Badge image: only the real achievement-unlock path supplies a URL.
            // Mastery / leaderboard toasts have no badge → collapse regardless of style.
            bool hasBadge = !string.IsNullOrWhiteSpace(badgeUrl);
            if (hasBadge)
            {
                // Clear any prior badge, then fill it in off-thread. _toastBadgeUrl
                // tags the current toast so a slow download for an earlier unlock
                // can't paint over a newer one that already replaced it.
                AchievementIconBrush.ImageSource = null;
                _toastBadgeUrl = badgeUrl;
                LoadBadgeAsync(badgeUrl!, bmp =>
                {
                    if (_toastBadgeUrl == badgeUrl) AchievementIconBrush.ImageSource = bmp;
                });
            }
            else
            {
                _toastBadgeUrl = null;
                AchievementIconBrush.ImageSource = null;
                AchievementBadge.Visibility = Visibility.Collapsed; // AND: never re-show
            }

            // Header eyebrow: explicit override wins; otherwise it only reads
            // "ACHIEVEMENT UNLOCKED" when there's an actual badge to crown. Leave the
            // visibility ApplyTo chose (ShowHeader) unless there's no text → collapse.
            string? headerText = header ?? (hasBadge ? "ACHIEVEMENT UNLOCKED" : null);
            if (string.IsNullOrEmpty(headerText))
                AchievementHeader.Visibility = Visibility.Collapsed; // AND: never re-show
            else
                AchievementHeader.Text = headerText;

            AchievementTitle.Text = title;
            AchievementDesc.Text = description;
            AchievementDesc.Visibility = string.IsNullOrEmpty(description)
                ? Visibility.Collapsed : Visibility.Visible;
            AchievementPoints.Text = points > 0 ? $"{points} points" : "";
            AchievementPoints.Visibility = points > 0 ? Visibility.Visible : Visibility.Collapsed;

            MoveHudElementToOverlay(AchievementToast);

            AchievementToast.Visibility = Visibility.Visible;

            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
            AchievementToast.BeginAnimation(OpacityProperty, fadeIn);

            _achievementToastTimer?.Stop();
            _achievementToastTimer = new DispatcherTimer { Interval = Services.ToastStyleRenderer.Duration(style) };
            _achievementToastTimer.Tick += (_, _) =>
            {
                _achievementToastTimer.Stop();
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
                fadeOut.Completed += (_, _) => AchievementToast.Visibility = Visibility.Collapsed;
                AchievementToast.BeginAnimation(OpacityProperty, fadeOut);
            };
            _achievementToastTimer.Start();
        }

        // Decode an RA badge URL into a frozen, cached BitmapImage. BitmapImage
        // streams http(s) URIs asynchronously and updates the ImageBrush once the
        // bytes arrive, so the toast appears instantly and the art fills in a beat
        // later. Freezing makes the cached instance safe to reuse across threads.
        // Shared client for badge image downloads. Static so the connection
        // pool is reused across unlocks.
        private static readonly System.Net.Http.HttpClient _badgeHttp = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        /// <summary>
        /// Loads an RA badge without ever blocking the UI thread. A cached badge
        /// is applied synchronously (decode-from-memory is already done); an
        /// uncached one returns immediately and the network fetch + decode run
        /// on a background thread, with <paramref name="apply"/> invoked on the
        /// dispatcher once the frozen image is ready.
        ///
        /// The previous version set BitmapImage.UriSource to a remote URL with
        /// CacheOption.OnLoad and froze it on the UI thread, which forced a
        /// synchronous download on the dispatcher — a visible hitch the first
        /// time each badge appeared.
        /// </summary>
        private void LoadBadgeAsync(string url, Action<System.Windows.Media.Imaging.BitmapImage> apply)
        {
            // Must be called on the UI thread (all callers are). Keeps
            // _badgeCache single-threaded.
            if (_badgeCache.TryGetValue(url, out var cached)) { apply(cached); return; }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    byte[] bytes = await _badgeHttp.GetByteArrayAsync(url).ConfigureAwait(false);
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new System.IO.MemoryStream(bytes);
                    bmp.EndInit();
                    bmp.Freeze();
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        _badgeCache[url] = bmp;
                        apply(bmp);
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[RA] Badge load failed ({url}): {ex.Message}");
                }
            });
        }

        // Cache for user-chosen local toast-background images (keyed by absolute path).
        private readonly System.Collections.Generic.Dictionary<string, System.Windows.Media.ImageSource> _localImageCache = new();

        // Decode a local image file into a frozen, cached ImageSource for the toast
        // background. Mirrors LoadBadge but for a filesystem path; returns null on any
        // failure so the renderer falls back to the gradient/solid background.
        private System.Windows.Media.ImageSource? LoadLocalImage(string path)
        {
            try
            {
                if (_localImageCache.TryGetValue(path, out var cached))
                    return cached;
                if (!System.IO.File.Exists(path))
                    return null;

                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                if (bmp.CanFreeze) bmp.Freeze();
                _localImageCache[path] = bmp;
                return bmp;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[RA] Toast background load failed ({path}): {ex.Message}");
                return null;
            }
        }

        private Views.PauseEffects.PauseEffectRunner? _pauseEffectRunner;

        // True when the user is paused AND has a non-None pause effect configured.
        // Used by HideOverlay to keep the Vulkan transparent overlay window
        // visible (since the pause effect lives inside it on Vulkan consoles).
        private bool IsPauseEffectActive()
        {
            if (!_isPaused) return false;
            string id = _configService.GetValue("pauseEffect",
                Views.PauseEffects.PauseEffectRegistry.NoneId);
            return !string.Equals(id, Views.PauseEffects.PauseEffectRegistry.NoneId,
                                  StringComparison.OrdinalIgnoreCase);
        }

        private void TogglePause()
        {
            _isPaused = !_isPaused;
            OverlayPauseIcon.Kind = _isPaused
                ? MaterialDesignThemes.Wpf.PackIconKind.Play
                : MaterialDesignThemes.Wpf.PackIconKind.Pause;
            if (_isPaused) StartPauseEffect();
            else           StopPauseEffect();
        }

        private void StartPauseEffect()
        {
            try
            {
                string id = _configService.GetValue("pauseEffect",
                    Views.PauseEffects.PauseEffectRegistry.NoneId);
                double intensity = _configService.GetValue("pauseEffectIntensity", 1.0);
                if (string.Equals(id, Views.PauseEffects.PauseEffectRegistry.NoneId,
                                  StringComparison.OrdinalIgnoreCase))
                    return;

                // Mirror the OverlayHud reparenting trick: on Vulkan/GL overlay paths,
                // the HwndHost child window obscures any WPF content in the same parent
                // window. Move PauseEffect into the transparent overlay window so it
                // composites above the present hwnd.
                if ((_vulkanOverlayHwnd != IntPtr.Zero && _vulkanPresenting) || _glOverlayHwnd != IntPtr.Zero)
                {
                    EnsureVulkanHudWindow();
                    if (PauseEffect.Parent is System.Windows.Controls.Panel currentParent
                        && currentParent != _vulkanHudGrid)
                    {
                        currentParent.Children.Remove(PauseEffect);
                        _vulkanHudGrid!.Children.Add(PauseEffect);
                    }
                    _vulkanHudWindow!.Show();
                }

                var instance = Views.PauseEffects.PauseEffectRegistry.Create(id);
                _pauseEffectRunner ??= new Views.PauseEffects.PauseEffectRunner(PauseEffect);
                if (instance is Views.PauseEffects.IPauseEffect vector)
                    _pauseEffectRunner.Start(vector, intensity);
                else if (instance is Views.PauseEffects.IPixelPauseEffect pixel)
                    _pauseEffectRunner.Start(pixel, intensity);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"StartPauseEffect failed: {ex.Message}");
            }
        }

        private void StopPauseEffect()
        {
            try { _pauseEffectRunner?.Stop(); } catch { }
            // If the HUD pill is collapsed too, the Vulkan overlay window has
            // nothing left to show — hide it. (Mirrors HideOverlay's logic but
            // fires when pause ends rather than when the HUD timer expires.)
            try
            {
                if (_vulkanHudWindow != null && _vulkanHudWindow.IsVisible
                    && OverlayHud.Visibility != Visibility.Visible)
                    _vulkanHudWindow.Hide();
            }
            catch { }
        }

        private void OverlayPower_Click(object sender, RoutedEventArgs e)   => Close();
        private void OverlayPause_Click(object sender, RoutedEventArgs e)   { TogglePause(); ResetOverlayTimer(); }
        private void OverlaySave_Click(object sender, RoutedEventArgs e)
        {
            OverlayMenu.Visibility = Visibility.Collapsed;
            CheatsMenu.Visibility = Visibility.Collapsed;
            VisualsPanel.Visibility = Visibility.Collapsed;
            ShaderPanel.Visibility = Visibility.Collapsed;
            if (SaveMenu.Visibility == Visibility.Visible)
            {
                CloseSaveMenu();
            }
            else
            {
                LoadSlotSubmenu.BeginAnimation(MaxWidthProperty, null);
                LoadSlotSubmenu.MaxWidth = 0;
                SaveMenu.Visibility = Visibility.Visible;
            }
            ResetOverlayTimer();
        }

        private void CloseSaveMenu()
        {
            SaveMenu.Visibility = Visibility.Collapsed;
            LoadSlotSubmenu.BeginAnimation(MaxWidthProperty, null);
            LoadSlotSubmenu.MaxWidth = 0;
        }

        private void OverlaySaveDirect_Click(object sender, RoutedEventArgs e)
        {
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss");
            RequestSave(ts);
            CloseSaveMenu();
            ResetOverlayTimer();
        }

        private void SaveMenuItem_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(
                LoadSlotSubmenu.MaxWidth, 0, TimeSpan.FromMilliseconds(150));
            LoadSlotSubmenu.BeginAnimation(MaxWidthProperty, anim);
        }

        private void LoadStateHover_Enter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            PopulateOverlayLoadSlots();
            var anim = new System.Windows.Media.Animation.DoubleAnimation(
                LoadSlotSubmenu.MaxWidth, 228,
                TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            LoadSlotSubmenu.BeginAnimation(MaxWidthProperty, anim);
        }

        private void SaveMenu_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            CloseSaveMenu();
        }

        private void PopulateOverlayLoadSlots()
        {
            OverlayLoadSlotItems.Children.Clear();
            var states = _db?.GetSaveStatesByGame(_game.Id).Take(6).ToList() ?? new();

            if (states.Count == 0)
            {
                OverlayLoadSlotItems.Children.Add(new TextBlock
                {
                    Text       = "No save states yet",
                    FontFamily = (FontFamily)FindResource("PrimaryFont"),
                    FontSize   = 11,
                    Foreground = (Brush)FindResource("TextMutedBrush"),
                    Margin     = new Thickness(8, 6, 8, 6),
                });
                return;
            }

            foreach (var s in states)
            {
                var row = new Border
                {
                    Padding         = new Thickness(8, 6, 8, 6),
                    Cursor          = System.Windows.Input.Cursors.Hand,
                    Background      = Brushes.Transparent,
                    CornerRadius    = new CornerRadius(4),
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var nameText = new TextBlock
                {
                    Text              = s.Name,
                    FontFamily        = (FontFamily)FindResource("PrimaryFont"),
                    FontSize          = 11,
                    Foreground        = (Brush)FindResource("TextPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming      = TextTrimming.CharacterEllipsis,
                };
                var timeText = new TextBlock
                {
                    Text              = s.RelativeTime,
                    FontFamily        = (FontFamily)FindResource("PrimaryFont"),
                    FontSize          = 10,
                    Foreground        = (Brush)FindResource("TextMutedBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(8, 0, 0, 0),
                };
                Grid.SetColumn(nameText, 0);
                Grid.SetColumn(timeText, 1);
                grid.Children.Add(nameText);
                grid.Children.Add(timeText);
                row.Child = grid;

                var captured = s;
                row.MouseLeftButtonUp += (_, _) => { RequestLoad(captured.StatePath, captured.Name); CloseSaveMenu(); };
                row.MouseEnter += (_, _) => row.Background = (Brush)FindResource("BgSecondaryBrush");
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                OverlayLoadSlotItems.Children.Add(row);
            }
        }

        private void OverlayCog_Click(object sender, RoutedEventArgs e)
        {
            CloseSaveMenu();
            CheatsMenu.Visibility = Visibility.Collapsed;
            VisualsPanel.Visibility = Visibility.Collapsed;
            ShaderPanel.Visibility = Visibility.Collapsed;
            OverlayMenu.Visibility = OverlayMenu.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (OverlayMenu.Visibility == Visibility.Visible)
            {
                if (_game?.Console == "N64") UpdatePakLabel();
                OverlayVisualsBtn.Visibility = (_consoleHandler?.GetVisualOptions(_coreOptions).Count ?? 0) > 0
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            ResetOverlayTimer();
        }

        // ── Cheats menu ──────────────────────────────────────────────────────
        private void OverlayCheats_Click(object sender, RoutedEventArgs e)
        {
            OverlayMenu.Visibility = Visibility.Collapsed;
            VisualsPanel.Visibility = Visibility.Collapsed;
            ShaderPanel.Visibility = Visibility.Collapsed;
            CloseSaveMenu();
            RefreshCheatsList();
            CheatsMenu.Visibility = Visibility.Visible;
            ResetOverlayTimer();
        }

        private void RefreshCheatsList()
        {
            CheatsListItems.Children.Clear();

            // Tell the user up-front when their core can't apply cheats.
            string corePath = _core?.CorePath ?? "";
            var support = Services.CheatSupport.Lookup(corePath);
            CheatsUnsupportedHint.Visibility = support.Level == Services.CheatSupportLevel.NotSupported
                ? Visibility.Visible : Visibility.Collapsed;

            CheatsListSeparator.Visibility = _cheats.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            for (int i = 0; i < _cheats.Count; i++)
            {
                var cheat = _cheats[i];
                int captured = i;

                var btn = new Button { Style = (Style)FindResource("OverlayMenuItemStyle") };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Pill-style sliding toggle — knob right + accent when on,
                // knob left + dim when off. Reads unambiguously as a control.
                // The row Button still opens the editor for clicks anywhere
                // else in the row.
                var knob = new Border
                {
                    Width             = 14,
                    Height            = 14,
                    CornerRadius      = new CornerRadius(7),
                    Background        = Brushes.White,
                    Margin            = new Thickness(2, 0, 2, 0),
                    HorizontalAlignment = cheat.Enabled ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    VerticalAlignment   = VerticalAlignment.Center,
                };
                var toggle = new Border
                {
                    Background        = cheat.Enabled
                        ? (Brush)FindResource("AccentBrush")
                        : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                    BorderBrush       = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                    BorderThickness   = new Thickness(1),
                    Width             = 34,
                    Height            = 18,
                    CornerRadius      = new CornerRadius(9),
                    Cursor            = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    ToolTip           = cheat.Enabled ? "Click to disable" : "Click to enable",
                    Child             = knob,
                };
                toggle.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;   // don't fall through to the row Button's OpenCheatEditor
                    ToggleCheatInOverlay(captured);
                };

                var label = new TextBlock
                {
                    Text              = cheat.Title,
                    Foreground        = cheat.Enabled ? Brushes.White : new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming      = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(toggle, 0);
                Grid.SetColumn(label, 1);
                grid.Children.Add(toggle);
                grid.Children.Add(label);
                btn.Content = grid;

                btn.Click += (_, _) => OpenCheatEditor(captured);
                CheatsListItems.Children.Add(btn);
            }
        }

        private void ToggleCheatInOverlay(int index)
        {
            if (index < 0 || index >= _cheats.Count) return;
            _cheats[index].Enabled = !_cheats[index].Enabled;

            try { Services.CheatService.Save(_game, _cheats); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Cheat toggle save failed: {ex.Message}"); }

            // Re-apply on the emu thread (same pending-flag pattern as Add/Edit)
            // so disabling a cheat actually clears it from the core mid-game.
            lock (_cheatsApplyLock)
            {
                _cheatsApplyPayload = new System.Collections.Generic.List<Models.Cheat>(_cheats);
                _cheatsApplyPending = true;
            }

            RefreshCheatsList();
            ResetOverlayTimer();
        }

        private void OverlayAddCheat_Click(object sender, RoutedEventArgs e)
        {
            OpenCheatEditor(-1);
        }

        private void OverlayImportCheats_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null) return;

            if (!Services.CheatDatabaseService.IsInstalled())
            {
                MessageBox.Show(this,
                    "The cheats database hasn't been downloaded yet.\n\n" +
                    "Open Preferences → Cores / Extras and click Download next to \"Cheats Database\".",
                    "No Database",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var result = Services.CheatDatabaseService.LookupForGame(_game);
            if (result == null)
            {
                MessageBox.Show(this,
                    "No cheats found in the database for this game.\n\n" +
                    "The database is matched by ROM filename, so renames or " +
                    "non-standard filenames may miss.",
                    "No Cheats Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Dedupe against what's already loaded (matched by code — titles
            // can differ between user-entered and DB versions).
            var existingCodes = new System.Collections.Generic.HashSet<string>(
                _cheats.Select(c => c.Code), System.StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var c in result.Cheats)
            {
                if (existingCodes.Contains(c.Code)) continue;
                _cheats.Add(c);
                added++;
            }

            try { Services.CheatService.Save(_game, _cheats); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Cheat save failed: {ex.Message}"); }

            // Live-apply via the emu-thread pending flag. Imported cheats
            // arrive disabled-by-default, so this is effectively a no-op
            // until the user toggles them — but we still queue the apply
            // so reset semantics are consistent with Add/Edit.
            lock (_cheatsApplyLock)
            {
                _cheatsApplyPayload = new System.Collections.Generic.List<Models.Cheat>(_cheats);
                _cheatsApplyPending = true;
            }

            RefreshCheatsList();

            string msg = added > 0
                ? $"Imported {added} cheat(s).\nAll are disabled by default — toggle the ones you want."
                : "All matching cheats from the database are already in your list.";
            MessageBox.Show(this, msg, "Cheats Imported",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenCheatEditor(int existingIndex)
        {
            string corePath = _core?.CorePath ?? "";

            Models.Cheat? existing = (existingIndex >= 0 && existingIndex < _cheats.Count) ? _cheats[existingIndex] : null;
            var dlg = new CheatEditWindow(existing, corePath) { Owner = this };
            bool? ok = dlg.ShowDialog();
            if (ok != true) return;

            if (dlg.DeleteRequested && existingIndex >= 0)
            {
                _cheats.RemoveAt(existingIndex);
            }
            else if (existingIndex >= 0)
            {
                _cheats[existingIndex] = dlg.Result;
            }
            else
            {
                _cheats.Add(dlg.Result);
            }

            try { Services.CheatService.Save(_game, _cheats); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Cheat save failed: {ex.Message}"); }

            // Re-apply on the emu thread to avoid racing retro_run.
            lock (_cheatsApplyLock)
            {
                _cheatsApplyPayload = new System.Collections.Generic.List<Models.Cheat>(_cheats);
                _cheatsApplyPending = true;
            }

            RefreshCheatsList();
        }

        /// <summary>Called on the emu thread between retro_run calls.</summary>
        private void ExecuteCheatsApplyOnEmuThread()
        {
            System.Collections.Generic.List<Models.Cheat>? payload;
            lock (_cheatsApplyLock)
            {
                payload = _cheatsApplyPayload;
                _cheatsApplyPayload = null;
                _cheatsApplyPending = false;
            }
            if (payload == null || _core == null) return;
            try { ApplyAllCheats(payload); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Cheats apply (queued) failed: {ex.Message}"); }
        }

        /// <summary>
        /// Sorts cheats into core-handled vs frontend AR, applies the core
        /// path via retro_cheat_set, and updates the per-frame frontend AR
        /// list. Caller must already be on the EmuThread (retro_cheat_set
        /// is unsafe from UI thread).
        /// </summary>
        private void ApplyAllCheats(System.Collections.Generic.IList<Models.Cheat> cheats)
        {
            if (_core == null) return;
            // RA hardcore-compliance: cheats are an auto-fail blocker in hardcore.
            // This is the single chokepoint for every cheat-apply path —
            // launch-time apply, in-game overlay toggle, in-game editor save,
            // and post-state-load re-apply all funnel through here, so one gate
            // is enough. _frontendArCheats stays empty too, which makes the
            // per-frame ApplyFrontendArToRam a no-op even if it ran.
            if (IsHardcoreActive())
            {
                _frontendArCheats = System.Array.Empty<Services.CheatService.ParsedAr>();
                if (cheats.Count > 0)
                {
                    _transientMsg    = "Cheats are disabled in hardcore mode";
                    _transientExpiry = DateTime.Now.AddSeconds(4);
                }
                return;
            }
            var (coreHandled, frontendAr) = Services.CheatService.Sort(cheats, _game?.Console ?? "");
            // Volatile swap — the per-frame ApplyFrontendArToRam reads this
            // without locking; new array reference is the safe handover.
            _frontendArCheats = frontendAr.ToArray();
            Services.CheatService.Apply(_core, coreHandled);
        }

        /// <summary>
        /// Writes every parsed AR code into system RAM. Called once per
        /// retro_run on the emu thread. No-op when there are no AR cheats
        /// or no system RAM exposed by the core.
        /// </summary>
        private unsafe void ApplyFrontendArToRam()
        {
            var ar = _frontendArCheats;  // volatile snapshot
            if (ar.Length == 0 || _systemRamPtr == IntPtr.Zero || _systemRamSize == 0) return;

            uint mask = _systemRamSize - 1;  // works for power-of-2 RAM sizes (NES 2K, Genesis 64K, SNES 128K, etc.)
            byte* ram = (byte*)_systemRamPtr.ToPointer();
            for (int i = 0; i < ar.Length; i++)
            {
                var c = ar[i];
                uint offset = c.Address & mask;
                if (c.ByteCount == 1)
                {
                    ram[offset] = (byte)c.Value;
                }
                else
                {
                    // Big-endian word write — Genesis/Saturn/N64 native order.
                    // LE systems (PS1/SNES/NES/GBA) might want byte-swap; most
                    // LE-system cheat databases use byte-only AR codes anyway.
                    ram[offset]              = (byte)((c.Value >> 8) & 0xFF);
                    ram[(offset + 1) & mask] = (byte)( c.Value       & 0xFF);
                }
            }
        }
        private void OverlayEditControls_Click(object sender, RoutedEventArgs e)
        {
            OverlayMenu.Visibility = Visibility.Collapsed;
            var win = new PreferencesWindow(_db!, _controllerManager!, _configService,
                initialConsole: _game?.Console)
                { Owner = this };
            win.ShowDialog();
            LoadKeyboardMappings();
            foreach (var c in _controllers) c?.ReloadInputConfiguration();
            ResetOverlayTimer();
        }

        private void OverlayTurbo_Click(object sender, RoutedEventArgs e)
        {
            OverlayMenu.Visibility = Visibility.Collapsed;
            var dlg = new TurboButtonsDialog(this, _turboButtons);
            dlg.ShowDialog();
            ResetOverlayTimer();
        }

        // ── NDS Screen Layout cycling ─────────────────────────────────────

        private static readonly string[] NdsScreenLayouts =
        {
            "top/bottom", "bottom/top", "left/right", "right/left",
            "top only", "bottom only", "hybrid/top", "hybrid/bottom"
        };

        private static readonly Dictionary<string, string> NdsLayoutLabels = new()
        {
            { "top/bottom",    "Top / Bottom" },
            { "bottom/top",    "Bottom / Top" },
            { "left/right",    "Side by Side" },
            { "right/left",    "Side by Side (reversed)" },
            { "top only",      "Top Screen Only" },
            { "bottom only",   "Bottom Screen Only" },
            { "hybrid/top",    "Hybrid (Top focus)" },
            { "hybrid/bottom", "Hybrid (Bottom focus)" },
        };

        private void UpdateScreenLayoutLabel()
        {
            string current = _coreOptions.TryGetValue("desmume_screens_layout", out var v) ? v : "top/bottom";
            string label = NdsLayoutLabels.TryGetValue(current, out var l) ? l : current;
            OverlayScreenLayoutBtn.Content = $"Screen Layout: {label}";
        }

        private void OverlayScreenLayout_Click(object sender, RoutedEventArgs e)
        {
            string current = _coreOptions.TryGetValue("desmume_screens_layout", out var v) ? v : "top/bottom";
            int idx = Array.IndexOf(NdsScreenLayouts, current);
            int next = (idx + 1) % NdsScreenLayouts.Length;
            string newLayout = NdsScreenLayouts[next];

            _coreOptions["desmume_screens_layout"] = newLayout;
            _coreOptionsDirty = true;
            UpdateScreenLayoutLabel();

            // Persist the change so it survives restarts
            string coreName = Path.GetFileNameWithoutExtension(_core.CorePath);
            App.CoreOptions.SaveValues(coreName, new Dictionary<string, string>
                { { "desmume_screens_layout", newLayout } });

            ResetOverlayTimer();
        }

        // ── N64 Controller Pak swap (Memory ↔ Rumble) ─────────────────────
        // N64 hardware only allows one pak per controller at a time, so games that
        // use both rumble and saves (Forsaken, Banjo, OoT, etc.) can't see them
        // simultaneously. Cycling here flips Player 1's pak between Memory and
        // Rumble; the core picks up the change via _coreOptionsDirty + check_variables().
        private void UpdatePakLabel()
        {
            string current = _coreOptions.TryGetValue("parallel-n64-pak1", out var v) ? v : "memory";
            string label = current switch
            {
                "memory"   => "Memory Pak",
                "rumble"   => "Rumble Pak",
                "transfer" => "Transfer Pak",
                "none"     => "No Pak",
                _          => current,
            };
            OverlayPakBtn.Content = $"P1 Pak: {label}";
        }

        private void OverlayPak_Click(object sender, RoutedEventArgs e)
        {
            string current = _coreOptions.TryGetValue("parallel-n64-pak1", out var v) ? v : "memory";
            string next = current == "memory" ? "rumble" : "memory";

            _coreOptions["parallel-n64-pak1"] = next;
            _coreOptionsDirty = true;
            UpdatePakLabel();

            string coreName = Path.GetFileNameWithoutExtension(_core.CorePath);
            App.CoreOptions.SaveValues(coreName, new Dictionary<string, string>
                { { "parallel-n64-pak1", next } });

            ResetOverlayTimer();
        }

        private void OverlayFlip_Click(object sender, RoutedEventArgs e)
        {
            _flipRotation = _flipRotation == 0u ? 2u : 0u;
            OverlayFlipBtn.Content = _flipRotation == 2 ? "Flip Display ✓" : "Flip Display";
            OverlayMenu.Visibility = Visibility.Collapsed;
            // Re-trigger AR update so the new rotation is applied immediately.
            if (_core?.AvInfo is { } av)
                UpdateDisplayAspectRatio(av.geometry.base_width, av.geometry.base_height,
                    av.geometry.aspect_ratio);
        }

        // ── Shader Effects ────────────────────────────────────────────────

        private async void OverlayShader_Click(object sender, RoutedEventArgs e)
        {
            // Open the picker (little selection window) instead of cycling.
            if (_hwRenderActive) return;   // defense-in-depth; the button is hidden on HW cores
            OverlayMenu.Visibility = Visibility.Collapsed;
            CheatsMenu.Visibility = Visibility.Collapsed;
            VisualsPanel.Visibility = Visibility.Collapsed;
            CloseSaveMenu();
            await BuildShaderPickerAsync();
            ShaderPanel.Visibility = Visibility.Visible;
            ShaderSearchBox.Focus();   // so keystrokes reach the box, not game input
            ResetOverlayTimer();
        }

        /// <summary>
        /// Populates the picker: built-in effects + downloaded presets (scanned OFF the
        /// UI thread, grouped by category), then pre-selects the active entry without
        /// firing an apply.
        /// </summary>
        private async System.Threading.Tasks.Task BuildShaderPickerAsync()
        {
            var items = new System.Collections.Generic.List<Effects.Librashader.ShaderPresetItem>();
            foreach (var p in Enum.GetValues<ShaderPreset>())
                items.Add(new Effects.Librashader.ShaderPresetItem
                {
                    Display = p.DisplayName(), Category = "Built-in", IsBuiltin = true, Builtin = p
                });

            string slangRoot = AppPaths.GetFolder("Shaders", "slang");
            var downloaded = await System.Threading.Tasks.Task.Run(
                () => Effects.Librashader.ShaderCatalog.GetDownloaded(slangRoot));
            items.AddRange(downloaded);

            var view = new System.Windows.Data.ListCollectionView(items);
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Category"));
            _shaderView = view;
            ApplyShaderFilter();

            bool MatchesActive(Effects.Librashader.ShaderPresetItem it)
            {
                if (_slangPresetPath != null)
                    return !it.IsBuiltin && it.AbsolutePath != null
                        && string.Equals(System.IO.Path.GetFullPath(it.AbsolutePath),
                                          System.IO.Path.GetFullPath(_slangPresetPath),
                                          StringComparison.OrdinalIgnoreCase);
                return it.IsBuiltin && it.Builtin == _activeShader;
            }

            _suppressShaderSelect = true;
            ShaderList.ItemsSource = view;
            var active = items.FirstOrDefault(MatchesActive);
            ShaderList.SelectedItem = active;
            if (active != null) ShaderList.ScrollIntoView(active);
            _suppressShaderSelect = false;
        }

        private void ApplyShaderFilter()
        {
            if (_shaderView == null) return;
            string q = ShaderSearchBox.Text?.Trim() ?? "";
            _shaderView.Filter = string.IsNullOrEmpty(q)
                ? null
                : o => o is Effects.Librashader.ShaderPresetItem it
                    && (it.Display.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || it.Category.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        private void ShaderSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => ApplyShaderFilter();

        private void ShaderList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressShaderSelect) return;
            if (ShaderList.SelectedItem is not Effects.Librashader.ShaderPresetItem item) return;

            if (item.IsBuiltin)
            {
                SetSlangPreset(null);            // clear any downloaded preset (emu-thread reset)
                _activeShader = item.Builtin;
                ApplyShader(_activeShader);
                _configService.SetValue($"shader_{_game.Id}", _activeShader.ToString());
            }
            else if (item.AbsolutePath != null)
            {
                SetSlangPreset(item.AbsolutePath);
                _configService.SetValue($"shader_{_game.Id}",
                    "slang:" + (item.RelativePath ?? System.IO.Path.GetFileName(item.AbsolutePath)));
            }
            _ = _configService.SaveAsync();
            UpdateShaderLabel();
        }

        private void ShaderPickerDone_Click(object sender, RoutedEventArgs e)
            => ShaderPanel.Visibility = Visibility.Collapsed;

        private void ApplyShader(ShaderPreset preset)
        {
            if (preset == ShaderPreset.Smooth)
            {
                GameScreen.Effect = null;
                RenderOptions.SetBitmapScalingMode(GameScreen, BitmapScalingMode.HighQuality);
            }
            else
            {
                RenderOptions.SetBitmapScalingMode(GameScreen, BitmapScalingMode.NearestNeighbor);
                GameScreen.Effect = ShaderEffectFactory.Create(preset, _videoHeight > 0 ? _videoHeight : 240);
            }
        }

        /// <summary>
        /// Selects (or clears) the downloaded slang preset. Renderer create/dispose
        /// is deferred to the emu thread (via _shaderResetRequested) so it is never
        /// touched from two threads at once.
        /// </summary>
        private void SetSlangPreset(string? path)
        {
            _slangPresetPath = path;
            _slangInitFailed = false;
            _shaderResetRequested = true;       // emu thread disposes old + reinits
            if (path != null)
            {
                GameScreen.Effect = null;       // the slang shader replaces the WPF effect
                RenderOptions.SetBitmapScalingMode(GameScreen, BitmapScalingMode.HighQuality);
            }
        }

        private void UpdateShaderLabel()
        {
            OverlayShaderBtn.Content = _slangPresetPath != null
                ? $"Shader: {System.IO.Path.GetFileNameWithoutExtension(_slangPresetPath)} (downloaded)"
                : $"Shader: {_activeShader.DisplayName()}";
        }

        private void RestoreShaderPreset()
        {
            try
            {
                string saved = _configService.GetValue($"shader_{_game.Id}", "None");
                if (saved.StartsWith("slang:", StringComparison.Ordinal))
                {
                    string relOrName = saved["slang:".Length..];
                    string? abs = Effects.Librashader.ShaderCatalog.Resolve(
                        AppPaths.GetFolder("Shaders", "slang"), relOrName);
                    if (abs != null) { SetSlangPreset(abs); UpdateShaderLabel(); return; }
                    // Pack/preset no longer present — fall through to built-in None.
                }
                if (Enum.TryParse<ShaderPreset>(saved, out var preset))
                    _activeShader = preset;
                ApplyShader(_activeShader);
                UpdateShaderLabel();
            }
            catch { }
        }

        private void UpdateShaderScreenHeight(uint height)
        {
            if (GameScreen.Effect is CrtScanlinesEffect crt)
                crt.ScreenHeight = height;
            else if (GameScreen.Effect is LcdGridEffect lcd)
                lcd.ScreenHeight = height;
            else if (GameScreen.Effect is GameBoyDmgLcdEffect dmgLcd)
                dmgLcd.ScreenHeight = height;
        }

        // ── Vectrex Overlay ───────────────────────────────────────────────

        private string? _vectrexOverlayPath;

        private void InitVectrexOverlay(Game game)
        {
            _vectrexOverlayPath = VectrexOverlayService.FindOverlay(game.RomPath);
            if (_vectrexOverlayPath == null) return;

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(_vectrexOverlayPath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                VectrexOverlayImage.Source = bmp;
            }
            catch { return; }

            bool enabled = VectrexOverlayService.IsOverlayEnabled(game.Id);
            ApplyVectrexOverlay(enabled);
            OverlayToggleBtn.Visibility = Visibility.Visible;
        }

        private void ApplyVectrexOverlay(bool enabled)
        {
            VectrexOverlayImage.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            OverlayToggleBtn.Content = enabled ? "Overlay: On" : "Overlay: Off";
        }

        private void OverlayToggle_Click(object sender, RoutedEventArgs e)
        {
            bool newState = VectrexOverlayImage.Visibility != Visibility.Visible;

            ApplyVectrexOverlay(newState);
            VectrexOverlayService.SetOverlayEnabled(_game.Id, newState);

            OverlayMenu.Visibility = Visibility.Collapsed;
            ResetOverlayTimer();
        }

        // ── Arcade / Neo Geo Bezel Overlay ────────────────────────────────
        // Frames the game with a Bezel Project PNG. The bezel is a 1920x1080 image
        // with a transparent window; the game keeps its own AR/rotation transform and
        // renders centred in the now-16:9 viewport, landing in that window. Only the
        // WINDOW snaps to the bezel AR (via WindowAr); the game transform is untouched.

        private string? _bezelPngPath;

        private void InitBezelOverlay(Game game)
        {
            if (!BezelService.AppliesTo(game.Console)) return;
            // Master switch (Preferences -> Cores/Extras). Off => no bezel UI at all.
            if (!BezelService.FeatureEnabled) return;

            // Show the cog toggle for arcade/NeoGeo so the user can flip it per game.
            BezelToggleBtn.Visibility = Visibility.Visible;
            bool enabled = BezelService.IsEnabledForGame(game.Id);
            BezelToggleBtn.Content = enabled ? "Bezel: On" : "Bezel: Off";

            // Auto-fetch + show if enabled (network runs off the UI thread; applies when ready).
            if (enabled)
                _ = LoadBezelAsync(game, userInitiated: false);
        }

        private async System.Threading.Tasks.Task LoadBezelAsync(Game game, bool userInitiated)
        {
            string? path = await BezelService.EnsureBezelAsync(game.RomPath, game.Console);
            if (path == null)
            {
                if (userInitiated)
                {
                    BezelToggleBtn.Content = "Bezel: Off";
                    _transientMsg    = "No bezel available for this game";
                    _transientExpiry = DateTime.Now.AddSeconds(3);
                }
                return;
            }
            _bezelPngPath = path;
            ApplyBezel(true);
        }

        private void ApplyBezel(bool enabled)
        {
            if (enabled && _bezelPngPath != null)
            {
                if (BezelImage.Source == null)
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource   = new Uri(_bezelPngPath);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        BezelImage.Source = bmp;
                        _bezelAr = bmp.PixelHeight > 0 ? (double)bmp.PixelWidth / bmp.PixelHeight : 16.0 / 9.0;
                    }
                    catch { return; }
                }

                _bezelActive = true;
                BezelImage.Visibility  = Visibility.Visible;
                BezelToggleBtn.Content = "Bezel: On";
                AutoSizeWindowToGameAr(_displayAr);   // bezel-aware: frame the window to ~16:9
            }
            else
            {
                _bezelActive = false;
                BezelImage.Visibility  = Visibility.Collapsed;
                BezelToggleBtn.Content = "Bezel: Off";
                AutoSizeWindowToGameAr(_displayAr);   // back to the game's own AR
            }
        }

        private void BezelToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null) return;
            bool newState = !_bezelActive;
            BezelService.SetEnabledForGame(_game.Id, newState);

            if (newState)
            {
                if (_bezelPngPath != null) ApplyBezel(true);
                else _ = LoadBezelAsync(_game, userInitiated: true);
            }
            else ApplyBezel(false);

            OverlayMenu.Visibility = Visibility.Collapsed;
            ResetOverlayTimer();
        }

        private void VisualsDone_Click(object sender, RoutedEventArgs e)
        {
            VisualsPanel.Visibility = Visibility.Collapsed;
        }

        private void OverlayVisuals_Click(object sender, RoutedEventArgs e)
        {
            OverlayMenu.Visibility = Visibility.Collapsed;
            CheatsMenu.Visibility = Visibility.Collapsed;
            ShaderPanel.Visibility = Visibility.Collapsed;
            CloseSaveMenu();
            BuildVisualsPanel();
            VisualsPanel.Visibility = Visibility.Visible;
            ResetOverlayTimer();
        }

        private void BuildVisualsPanel()
        {
            VisualOptionRows.Children.Clear();
            var options = _consoleHandler?.GetVisualOptions(_coreOptions);
            if (options == null || options.Count == 0) return;

            var schema = _coreOptionSchema;
            var coreName = _core != null ? Path.GetFileNameWithoutExtension(_core.CorePath) : null;

            foreach (var (key, label) in options)
            {
                if (!_coreOptions.ContainsKey(key)) continue;

                var entry = schema.Find(e => e.Key == key);
                if (entry == null || entry.ValidValues == null || entry.ValidValues.Length == 0) continue;

                var row = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 6) };

                row.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = label,
                    FontFamily = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                    FontSize = 11,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                    Margin = new Thickness(0, 0, 0, 2),
                });

                string current = _coreOptions.TryGetValue(key, out var cv) ? cv : entry.DefaultValue;

                // Overlay-only gating (e.g. PS2 hides sub-3x internal resolutions).
                // Does NOT touch the saved value or Preferences; never hide the
                // currently-active value so a sub-floor setting chosen in Preferences
                // still shows here.
                var overlayValues = _consoleHandler != null
                    ? _consoleHandler.FilterOverlayValues(key, entry.ValidValues)
                    : entry.ValidValues;
                if (!string.IsNullOrEmpty(current) && !overlayValues.Contains(current))
                    overlayValues = new[] { current }.Concat(overlayValues).ToArray();

                var combo = new System.Windows.Controls.ComboBox
                {
                    Style = (Style)FindResource("OverlayComboBox"),
                    ItemsSource = overlayValues,
                    Tag = key,
                };
                combo.SelectedItem = current;

                combo.SelectionChanged += (s, args) =>
                {
                    if (s is System.Windows.Controls.ComboBox cb && cb.SelectedItem is string val && cb.Tag is string k)
                    {
                        _coreOptions[k] = val;
                        _coreOptionsDirty = true;
                        if (coreName != null)
                            App.CoreOptions?.SaveValues(coreName, new Dictionary<string, string> { [k] = val });
                    }
                };

                row.Children.Add(combo);
                VisualOptionRows.Children.Add(row);
            }
        }

        /// <summary>
        /// Called by PreferencesWindow "Reset to Defaults" to apply default option values
        /// to the live session. Sets the dirty flag so the core re-reads on the next frame.
        /// </summary>
        public void ApplyCoreOptionDefaults(Services.CoreOptionsSchema schema)
        {
            if (_isClosing || _core == null) return;
            foreach (var opt in schema.Options)
            {
                if (!string.IsNullOrEmpty(opt.DefaultValue))
                    _coreOptions[opt.Key] = opt.DefaultValue;
            }
            _coreOptionsDirty = true;
        }

        /// <summary>Returns the DLL name (without extension) of the currently loaded core.</summary>
        public string? RunningCoreName =>
            (_isClosing || _core == null) ? null
            : Path.GetFileNameWithoutExtension(_core.CorePath);


        // =========================================================================
        // Window chrome + AR-constrained resize
        // =========================================================================

        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void ApplyWindowsChrome()
        {
            var theme = App.Configuration?.GetThemeConfiguration();
            if (theme?.UseWindowsChrome != true) return;

            WindowStyle = System.Windows.WindowStyle.SingleBorderWindow;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.CanResize;

            RootBorder.Margin = new Thickness(0);
            RootBorder.CornerRadius = new CornerRadius(0);
            RootBorder.BorderThickness = new Thickness(0);
            RootBorder.Effect = null;

            CustomTitleBar.Visibility = Visibility.Collapsed;
            RootGrid.RowDefinitions[0].Height = new GridLength(0);

            SourceInitialized += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int value = 1;
                    DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
                }
            };
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var source = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            source?.AddHook(HwndHook);

            // Launched from EmuTV (couch mode) → enter fullscreen automatically.
            // Deferred to Loaded priority so layout/chrome is settled first.
            if (StartInFullscreen)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() => { if (!_isFullscreen) ToggleFullscreen(); }));
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                                 ref bool handled)
        {
            const int WM_SYSCOMMAND  = 0x0112;
            const int SC_MAXIMIZE    = 0xF030;
            const int WM_SIZING      = 0x0214;
            const int WMSZ_TOP       = 3;
            const int WMSZ_BOTTOM    = 6;

            if (msg == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_MAXIMIZE)
            {
                Dispatcher.BeginInvoke(() => ToggleFullscreen());
                handled = true;
                return IntPtr.Zero;
            }

            // WindowAr = the bezel AR (~16:9) when a bezel is active, else the game AR,
            // so a manual resize keeps the window in the bezel's frame shape.
            if (msg == WM_SIZING && WindowAr > 0 && WindowState == WindowState.Normal)
            {
                var rect = Marshal.PtrToStructure<RECT>(lParam);

                double chromeH = ActualHeight - GameViewport.ActualHeight;
                int edge = (int)wParam;

                int w     = rect.Right  - rect.Left;
                int gameH = rect.Bottom - rect.Top - (int)Math.Round(chromeH);

                if (edge == WMSZ_TOP || edge == WMSZ_BOTTOM)
                {
                    // Height-led drag: adjust width to maintain AR.
                    int newW = (int)Math.Round(Math.Max(gameH, 60) * WindowAr);
                    rect.Right = rect.Left + Math.Max(newW, 160);
                }
                else
                {
                    // Width-led drag (left, right, or any corner): adjust height to maintain AR.
                    int newGameH = (int)Math.Round(Math.Max(w, 160) / WindowAr);
                    rect.Bottom = rect.Top + (int)Math.Round(chromeH) + Math.Max(newGameH, 60);
                }

                Marshal.StructureToPtr(rect, lParam, false);
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ---- Invisible edge/corner resize for borderless window ----
        private const int _resizeBorder = 6;

        private int HitTestEdge(Point p)
        {
            bool top    = p.Y < _resizeBorder;
            bool bottom = p.Y >= RootBorder.ActualHeight - _resizeBorder;
            bool left   = p.X < _resizeBorder;
            bool right  = p.X >= RootBorder.ActualWidth  - _resizeBorder;

            if (top && left)       return 4; // WMSZ_TOPLEFT
            if (top && right)      return 5; // WMSZ_TOPRIGHT
            if (bottom && left)    return 7; // WMSZ_BOTTOMLEFT
            if (bottom && right)   return 8; // WMSZ_BOTTOMRIGHT
            if (top)               return 3; // WMSZ_TOP
            if (bottom)            return 6; // WMSZ_BOTTOM
            if (left)              return 1; // WMSZ_LEFT
            if (right)             return 2; // WMSZ_RIGHT
            return 0;
        }

        private void RootBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (WindowState != WindowState.Normal) { RootBorder.Cursor = null; return; }
            int edge = HitTestEdge(e.GetPosition(RootBorder));
            RootBorder.Cursor = edge switch
            {
                1 or 2 => Cursors.SizeWE,
                3 or 6 => Cursors.SizeNS,
                4 or 8 => Cursors.SizeNWSE,
                5 or 7 => Cursors.SizeNESW,
                _      => null
            };
        }

        private void RootBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (WindowState != WindowState.Normal) return;
            int edge = HitTestEdge(e.GetPosition(RootBorder));
            if (edge == 0) return;

            // SC_SIZE = 0xF000, direction offset matches WMSZ values
            const uint WM_SYSCOMMAND = 0x0112;
            const int SC_SIZE = 0xF000;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)(SC_SIZE + edge), IntPtr.Zero);
            e.Handled = true;
        }

        private void ToggleFullscreen()
        {
            if (_isFullscreen)
            {
                // Exit fullscreen — restore saved state
                WindowStyle = _preFsWindowStyle;
                RootBorder.BorderThickness = _preFsBorderThickness;
                RootBorder.CornerRadius = _preFsCornerRadius;
                RootBorder.Margin = _preFsMargin;
                RootBorder.Effect = _preFsEffect;
                RootGrid.RowDefinitions[0].Height = _preFsRow0Height;
                RootGrid.RowDefinitions[2].Height = _preFsRow2Height;
                CustomTitleBar.Visibility = _preFsTitleBarVisibility;
                ResizeMode = _preFsResizeMode;
                WindowState = WindowState.Normal;
                Left = _preFsLeft;
                Top = _preFsTop;
                Width = _preFsWidth;
                Height = _preFsHeight;
                if (_preFullscreenState == WindowState.Maximized)
                    WindowState = WindowState.Maximized;
                Topmost = false;
                _isFullscreen = false;
                Mouse.OverrideCursor = null;
            }
            else
            {
                // Enter fullscreen — save all state
                _preFullscreenState = WindowState;
                if (WindowState == WindowState.Maximized)
                {
                    _preFsLeft = RestoreBounds.Left;
                    _preFsTop = RestoreBounds.Top;
                    _preFsWidth = RestoreBounds.Width;
                    _preFsHeight = RestoreBounds.Height;
                }
                else
                {
                    _preFsLeft = Left;
                    _preFsTop = Top;
                    _preFsWidth = Width;
                    _preFsHeight = Height;
                }
                _preFsWindowStyle = WindowStyle;
                _preFsBorderThickness = RootBorder.BorderThickness;
                _preFsCornerRadius = RootBorder.CornerRadius;
                _preFsMargin = RootBorder.Margin;
                _preFsEffect = RootBorder.Effect;
                _preFsResizeMode = ResizeMode;
                _preFsRow0Height = RootGrid.RowDefinitions[0].Height;
                _preFsRow2Height = RootGrid.RowDefinitions[2].Height;
                _preFsTitleBarVisibility = CustomTitleBar.Visibility;

                // Strip ALL chrome — works for both custom and Windows chrome paths
                if (WindowState != WindowState.Normal)
                    WindowState = WindowState.Normal;
                WindowStyle = System.Windows.WindowStyle.None;
                RootBorder.BorderThickness = new Thickness(0);
                RootBorder.CornerRadius = new CornerRadius(0);
                RootBorder.Margin = new Thickness(0);
                RootBorder.Effect = null;
                RootGrid.RowDefinitions[0].Height = new GridLength(0);
                RootGrid.RowDefinitions[2].Height = new GridLength(0);
                CustomTitleBar.Visibility = Visibility.Collapsed;
                ResizeMode = ResizeMode.NoResize;

                // WindowStyle.None + Maximized fills the entire screen including taskbar
                WindowState = WindowState.Maximized;
                Topmost = true;

                _isFullscreen = true;
            }

            ApplyFullscreenAspect();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                ToggleFullscreen();
            else DragMove();
        }

        private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaxBtn_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Second pass: async cleanup finished and called Close() — let WPF proceed.
            if (_closeStarted) return;

            if (_isFullscreen) ToggleFullscreen();

            // First pass: cancel the close, signal the emu thread, and run the blocking
            // Join + cleanup on a background thread so the WPF message pump stays live.
            //
            // WHY async: the emu thread fires Dispatcher.BeginInvoke calls for video/status
            // updates.  If we block the UI thread in Join() those callbacks can never execute,
            // the emu loop never sees _isClosing, Join times out after 3 s, and we then free
            // delegates while the emu thread is still alive → unhandled exception on the
            // background thread → process terminates (no crash dump).
            e.Cancel = true;
            _closeStarted = true;
            _isClosing = true;
            _timer?.Stop();
            Services.PerfLog.SessionEnd();
            _overlayTimer?.Stop();
            _mousePoller?.Stop();
            _audioPlayer?.Stop();

            // Stop recording before core teardown so the MP4 is finalized cleanly
            if (_recordingService?.IsRecording == true)
            {
                _recordingService.Stop();
                _recordingService = null;
            }

            // Hide overlay surfaces and HUD immediately so they don't linger during cleanup.
            // Native WS_POPUP windows live independently of WPF's Hide() and must be
            // dismissed explicitly — otherwise the GL/Vulkan present surface sits frozen
            // on screen for the duration of the close (emu-thread join + GL quarantine).
            if (_vulkanOverlayHwnd != IntPtr.Zero)
                ShowWindow(_vulkanOverlayHwnd, 0); // SW_HIDE
            if (_glOverlayHwnd != IntPtr.Zero)
                ShowWindow(_glOverlayHwnd, 0);     // SW_HIDE
            _vulkanHudWindow?.Hide();

            // Stop forwarding keyboard events to the core — the core's function pointer
            // will be invalidated once retro_deinit runs, and a late key event would AV.
            _coreKeyboardEvent = null;

            // Accumulate play time for this session.
            try
            {
                if (_sessionStartUtc != default)
                {
                    int sessionSec = (int)(DateTime.UtcNow - _sessionStartUtc).TotalSeconds;
                    if (sessionSec > 0)
                    {
                        _db?.UpdatePlayTime(_game.Id, sessionSec);
                        _game.TotalPlayTimeSeconds += sessionSec;
                    }
                }
            }
            catch { }

            // Hide immediately so the user isn't staring at an unresponsive window
            // while the emu thread and GL cleanup finish in the background.
            Hide();

            // Save window size NOW while we're on the UI thread and the window is still alive.
            // This must happen before the Task.Run cleanup — native interop in cleanup can throw
            // and skip anything that comes after it.
            SaveWindowSize();

            // Tear down the pause effect runner — releases its CompositionTarget.Rendering
            // subscription so it doesn't tick against the closing visual tree.
            try { _pauseEffectRunner?.Dispose(); _pauseEffectRunner = null; } catch { }

            System.Diagnostics.Trace.WriteLine("EmulatorWindow closing — deferring cleanup to background");

            System.Threading.Tasks.Task.Run(() =>
            {
                // Wait for the emu thread to fully exit.
                // The emu thread now does: SRAM save → UnloadGame → context_destroy → GL release
                // before exiting, so this join covers all of it.
                // Allow up to 10 s for heavy cores (PPSSPP, N64) whose internal threads take time.
                if (!(_emuThread?.Join(10000) ?? true))
                    System.Diagnostics.Trace.WriteLine("WARNING: emu thread did not exit within 10s");

                // Emu thread has stopped — safe to dispose the shader renderer it owned.
                try { _shaderRenderer?.Dispose(); } catch { /* best effort */ }
                _shaderRenderer = null;

                // Emu thread is gone, so nothing else touches the D3D11 device/present
                // bridge — release it (device, D3D9Ex/swapchain, shared surface, shaders).
                // Previously leaked on close.
                try { _d3d11Context?.Dispose(); } catch { /* best effort */ }
                _d3d11Context = null;

                // Tear down the present overlay window — the D3D11 swapchain path reuses
                // the Vulkan overlay HWND + reposition hooks. Idempotent: no-op if the
                // Vulkan teardown already ran it, or if the D3DImage path (no overlay)
                // was used. Must follow the context dispose so no Present hits a dead HWND.
                try { Dispatcher.Invoke(() => DestroyVulkanOverlay()); } catch { /* window may be gone */ }

                // Cloud sync: upload battery save after emu thread has flushed SRAM to disk.
                if (!_loadFailed && !string.IsNullOrEmpty(_game.RomHash)
                    && !string.IsNullOrEmpty(_srmPath) && System.IO.File.Exists(_srmPath))
                {
                    var syncSvc = Services.GitHubSyncService.Instance;
                    if (syncSvc.IsAuthenticated)
                    {
                        var cfg = App.Configuration?.GetCloudSyncConfiguration();
                        if (cfg is { Enabled: true })
                        {
                            bool encrypted = cfg.EncryptionEnabled
                                && !string.IsNullOrEmpty(cfg.PassphraseProtected);
                            string repoPath = $"BatterySaves/{_game.Console}/{_game.RomHash}.srm"
                                + (encrypted ? ".enc" : "");
                            try
                            {
                                // Newest-wins, no clobber: don't replace a newer (or equal)
                                // remote save with our older local one — e.g. the game was
                                // opened and closed without writing a save while the other OS
                                // had already uploaded newer progress. After actually playing,
                                // the local .srm mtime is "now" and wins; a launch-without-save
                                // keeps the remote mtime stamped on pull, so this correctly no-ops.
                                if (syncSvc.ManifestCache.Files.TryGetValue(repoPath, out var existing)
                                    && DateTime.TryParse(existing.LastModifiedUtc, null,
                                        System.Globalization.DateTimeStyles.RoundtripKind, out var remoteMtime)
                                    && remoteMtime >= System.IO.File.GetLastWriteTimeUtc(_srmPath))
                                {
                                    Services.CloudSyncLog.Write($"Skipped save upload (remote is newer/same): {repoPath}");
                                }
                                else
                                {
                                    byte[] srmBytes = System.IO.File.ReadAllBytes(_srmPath);
                                    if (encrypted)
                                    {
                                        byte[] key = Services.GitHubSyncService.DeriveKey(
                                            Services.GitHubSyncService.UnprotectString(cfg.PassphraseProtected), syncSvc.Username ?? "");
                                        srmBytes = Services.GitHubSyncService.Encrypt(srmBytes, key);
                                    }
                                    _ = syncSvc.UploadFileAsync(repoPath, srmBytes);
                                    syncSvc.ManifestCache.Files[repoPath] = new Services.GitHubSyncService.SyncFileEntry
                                    {
                                        LastModifiedUtc = System.IO.File.GetLastWriteTimeUtc(_srmPath).ToString("o"),
                                        SizeBytes = new System.IO.FileInfo(_srmPath).Length
                                    };
                                    Services.CloudSyncLog.Write($"Queued upload: {repoPath}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Services.CloudSyncLog.Write($"Upload prep failed: {ex.Message}");
                            }
                        }
                    }
                }

                // Cloud sync: upload this console's memory cards / save trees (PS2,
                // PSP, GameCube, Dreamcast, 3DS, …). Separate from the .srm block
                // above because those consoles have no .srm, so that block is skipped
                // for them. Fire-and-forget — never blocks the close path.
                if (!_loadFailed && _game != null && !string.IsNullOrEmpty(_game.Console))
                {
                    var extraSvc = Services.GitHubSyncService.Instance;
                    var extraCfg = App.Configuration?.GetCloudSyncConfiguration();
                    if (extraSvc.IsAuthenticated && extraCfg is { Enabled: true })
                    {
                        try { _ = extraSvc.UploadConsoleExtraSavesAsync(_game.Console); }
                        catch (Exception ex) { Services.CloudSyncLog.Write($"Memcard upload prep failed: {ex.Message}"); }
                    }
                }

                // retro_deinit — final core teardown.
                // LibretroCore.Dispose() skips retro_unload_game (already called on emu
                // thread) and skips retro_deinit for N64 (called on emu thread with GL
                // context active).  Dispose() handles the post-deinit wait + FreeLibrary.
                if (!_loadFailed)
                {
                    // _core.Dispose calls retro_deinit. For heavy 3D cores the core's
                    // internal worker threads can leave retro_deinit hung indefinitely —
                    // blocking the rest of cleanup and preventing the window from ever
                    // calling Close(). Run Dispose with a hard timeout so the WPF window
                    // can finalise even when the core is misbehaving; any leaked native
                    // state is reclaimed by App.OnExit's Environment.Exit when the user
                    // quits the app.
                    var disposeTask = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { _core?.Dispose(); }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Core dispose: {ex.Message}"); }
                    });
                    if (!disposeTask.Wait(TimeSpan.FromSeconds(5)))
                        System.Diagnostics.Trace.WriteLine("WARNING: core dispose did not complete within 5s — abandoning to let window close");
                }
                else if (_core != null && _core.NativeHandle != IntPtr.Zero)
                {
                    // Load failed — retro_deinit would AV on partially-initialized state,
                    // but we MUST FreeLibrary so the next launch gets clean globals.
                    // Without this, the DLL stays loaded with dirty state and the next
                    // game on the same core crashes during retro_init.
                    try { NativeMethods.FreeLibrary(_core.NativeHandle); }
                    catch { }
                    _core.FreeMarshaledMemory();
                    System.Diagnostics.Trace.WriteLine("Load-failed cleanup: FreeLibrary on dirty DLL");
                }

                // GL context cleanup + optional DLL unload.
                //
                // After retro_unload_game + retro_deinit, some cores leave driver-internal
                // callbacks (texture frees, fence signals) that fire on a background OS
                // thread.  Deleting the HGLRC too soon causes those callbacks to hit a null
                // dispatch table → AV in nvoglv64 / OPENGL32.
                //
                // For cores with deferred FreeLibrary (N64/Dolphin): retro_deinit now runs
                // on the emu thread with GL context, so cleanup is largely complete.  We do
                // a synchronous short wait → wglDeleteContext → FreeLibrary RIGHT HERE on
                // the Task.Run thread so the DLL is fully unloaded before the user can
                // launch another game (prevents stale global state / "Failed to initialize").
                //
                // For other HW cores: fire-and-forget async quarantine (longer delays).
                bool glSyncHandledDll = false;
                if (_hwRenderActive && (_hglrc != IntPtr.Zero || _secondaryCtx != IntPtr.Zero))
                {
                    IntPtr hglrcQ    = _hglrc;         _hglrc        = IntPtr.Zero;
                    IntPtr secCtxQ   = _secondaryCtx;  _secondaryCtx = IntPtr.Zero;
                    IntPtr deferredDll = _core?.DeferredFreeHandle ?? IntPtr.Zero;

                    if (deferredDll != IntPtr.Zero)
                    {
                        glSyncHandledDll = true; // prevent Vulkan stash path from re-stashing this DLL
                        // Synchronous path: retro_deinit already ran on emu thread with GL.
                        // Wait for residual driver/GPU-thread callbacks, then delete + free.
                        // PPSSPP's GPU thread self-cleans after retro_unload_game but takes
                        // longer to fully exit than N64/Dolphin (context_destroy is skipped).
                        string dllName = _core != null ? System.IO.Path.GetFileName(_core.CorePath).ToLowerInvariant() : "";
                        bool skipFreeLibrary = dllName.Contains("dolphin");
                        int preDeleteMs = dllName.Contains("ppsspp") ? 3000 : 1500;
                        System.Diagnostics.Trace.WriteLine($"GL sync cleanup: waiting {preDeleteMs}ms before wglDeleteContext{(skipFreeLibrary ? " (FreeLibrary skipped for Dolphin)" : $" + FreeLibrary 0x{deferredDll:X}")}");
                        System.Threading.Thread.Sleep(preDeleteMs);
                        try
                        {
                            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                            if (secCtxQ  != IntPtr.Zero) wglDeleteContext(secCtxQ);
                            if (hglrcQ   != IntPtr.Zero) wglDeleteContext(hglrcQ);
                            System.Diagnostics.Trace.WriteLine("GL sync cleanup: contexts deleted.");
                        }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"GL sync delete: {ex.Message}"); }

                        if (!skipFreeLibrary)
                        {
                            System.Threading.Thread.Sleep(500);
                            try
                            {
                                NativeMethods.FreeLibrary(deferredDll);
                                System.Diagnostics.Trace.WriteLine($"GL sync cleanup: FreeLibrary 0x{deferredDll:X} done.");
                            }
                            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"GL sync FreeLibrary: {ex.Message}"); }
                        }
                    }
                    else
                    {
                        // Async quarantine for cores without deferred FreeLibrary (PPSSPP, etc.).
                        string dllName = _core != null ? System.IO.Path.GetFileName(_core.CorePath).ToLowerInvariant() : "";
                        int quarantineMs = dllName switch
                        {
                            var d when d.Contains("ppsspp")       => 4000,
                            var d when d.Contains("kronos")       => 2000,
                            var d when d.Contains("mednafen_psx") => 1500,
                            var d when d.Contains("pcsx_rearmed") => 1500,
                            _                                     =>  500,
                        };
                        System.Diagnostics.Trace.WriteLine($"GL quarantine: deleting contexts in {quarantineMs}ms");

                        System.Threading.Tasks.Task.Run(async () =>
                        {
                            await System.Threading.Tasks.Task.Delay(quarantineMs);
                            try
                            {
                                wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                                if (secCtxQ  != IntPtr.Zero) wglDeleteContext(secCtxQ);
                                if (hglrcQ   != IntPtr.Zero) wglDeleteContext(hglrcQ);
                                System.Diagnostics.Trace.WriteLine("GL quarantine: contexts deleted.");
                            }
                            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"GL quarantine delete: {ex.Message}"); }
                        });
                    }
                }

                // ── Vulkan / non-GL DLL cleanup ─────────────────────────────────
                // VkDevice/VkInstance are intentionally leaked (deferred in VulkanContext)
                // so nvoglv64.dll's device tables stay clean for relaunch.
                //
                // Any residual driver/core threads that AV are caught by VEH Fixup C
                // (ExitThread).  Stash the DLL handle for deferred FreeLibrary at the
                // start of the next session — by then the ExitThread'd threads are dead
                // and FreeLibrary gives clean globals for the next LoadLibrary.
                if (!glSyncHandledDll && !(_hwRenderActive && (_hglrc != IntPtr.Zero || _secondaryCtx != IntPtr.Zero)))
                {
                    IntPtr deferredDll = _core?.DeferredFreeHandle ?? IntPtr.Zero;
                    if (deferredDll != IntPtr.Zero)
                    {
                        string dllName = _core != null ? System.IO.Path.GetFileName(_core.CorePath).ToLowerInvariant() : "";
                        _staleDllHandle = deferredDll;
                        System.Diagnostics.Trace.WriteLine($"Vulkan DLL cleanup: stashed 0x{deferredDll:X} ({dllName}) for deferred FreeLibrary on next launch");
                    }
                }

                if (_hdc != IntPtr.Zero && _glHwnd != IntPtr.Zero) { ReleaseDC(_glHwnd, _hdc); _hdc = IntPtr.Zero; }
                // Destroy the offscreen GL window if we created it; HwndHost owns its own window.
                if (_glHwndOwned && _glHwnd != IntPtr.Zero) { DestroyWindow(_glHwnd); _glHwndOwned = false; }
                _glHwnd = IntPtr.Zero;

                try { _recordingService?.Dispose(); foreach (var c in _controllers) c?.Dispose(); _audioPlayer?.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Service cleanup: {ex.Message}"); }

                if (_systemDirPtr  != IntPtr.Zero) { Marshal.FreeHGlobal(_systemDirPtr);  _systemDirPtr  = IntPtr.Zero; }
                if (_saveDirPtr    != IntPtr.Zero) { Marshal.FreeHGlobal(_saveDirPtr);    _saveDirPtr    = IntPtr.Zero; }
                if (_contentDirPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_contentDirPtr); _contentDirPtr = IntPtr.Zero; }

                // Free cached GET_VARIABLE string pointers. Iterate the full allocation list
                // (not just the current _coreOptionPtrs map) because the map only holds the
                // latest pointer per key — historical ones are kept in _coreOptionPtrsAllocated
                // to avoid the use-after-free we'd hit if we freed mid-session.
                foreach (var ptr in _coreOptionPtrsAllocated)
                    if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
                _coreOptionPtrsAllocated.Clear();
                _coreOptionPtrs.Clear();
                _coreOptionPtrValues.Clear();

                static void FreeH(ref GCHandle? h) { if (h.HasValue) { h.Value.Free(); h = null; } }
                FreeH(ref _envCbHandle);
                FreeH(ref _videoCbHandle);
                FreeH(ref _audioCbHandle);
                FreeH(ref _audioBatchCbHandle);
                FreeH(ref _inputPollCbHandle);
                FreeH(ref _inputStateCbHandle);
                FreeH(ref _logCbHandle);
                FreeH(ref _getFramebufferHandle);
                FreeH(ref _getProcAddressHandle);
                if (_swapIntervalStubHandle.IsAllocated) { _swapIntervalStubHandle.Free(); }
                if (_glFinishStubHandle.IsAllocated)    { _glFinishStubHandle.Free(); }

                System.Diagnostics.Trace.WriteLine("EmulatorWindow cleanup complete");

                // Flush and close the file log listener
                var fileLog = System.Diagnostics.Trace.Listeners["FileLog"];
                if (fileLog != null)
                {
                    fileLog.Flush();
                    System.Diagnostics.Trace.Listeners.Remove(fileLog);
                    fileLog.Dispose();
                }

                // Now that all cleanup is done, close the window on the UI thread.
                // Window_Closing will fire again; _closeStarted is true so it returns
                // immediately without cancelling — WPF then destroys the window normally.
                Dispatcher.Invoke(() => Close());
            });
        }

        // =========================================================================
        // Vulkan overlay window — position sync
        // =========================================================================
        private void VulkanOverlay_Reposition(object? sender, EventArgs e)
        {
            if (_vulkanOverlayHwnd == IntPtr.Zero && _glOverlayHwnd == IntPtr.Zero) return;
            RepositionOverlayWindow();
        }

        private void VulkanOverlay_StateChanged(object? sender, EventArgs e)
        {
            IntPtr overlayHwnd = _vulkanOverlayHwnd != IntPtr.Zero ? _vulkanOverlayHwnd : _glOverlayHwnd;
            if (overlayHwnd == IntPtr.Zero) return;
            if (WindowState == WindowState.Minimized)
            {
                ShowWindow(overlayHwnd, 0); // SW_HIDE
                _vulkanHudWindow?.Hide();
            }
            else
            {
                ShowWindow(overlayHwnd, 5); // SW_SHOW
                RepositionOverlayWindow();
            }
        }

        private void RepositionOverlayWindow()
        {
            IntPtr overlayHwnd = _vulkanOverlayHwnd != IntPtr.Zero ? _vulkanOverlayHwnd : _glOverlayHwnd;
            if (overlayHwnd == IntPtr.Zero) return;
            try
            {
                var viewportPoint = GameViewport.PointToScreen(new System.Windows.Point(0, 0));
                int vx = (int)viewportPoint.X;
                int vy = (int)viewportPoint.Y;
                int vw = Math.Max(1, (int)GameViewport.ActualWidth);
                int vh = Math.Max(1, (int)GameViewport.ActualHeight);

                // AR-correct the overlay rectangle so the game pillarboxes/letterboxes
                // instead of stretching. GameBorder's black background fills the bars.
                if (_displayAr > 0.01)
                {
                    double viewportAr = (double)vw / vh;
                    if (_displayAr > viewportAr)
                    {
                        int newH = Math.Max(1, (int)(vw / _displayAr));
                        vy += (vh - newH) / 2;
                        vh = newH;
                    }
                    else if (_displayAr < viewportAr)
                    {
                        int newW = Math.Max(1, (int)(vh * _displayAr));
                        vx += (vw - newW) / 2;
                        vw = newW;
                    }
                }

                const uint SWP_NOZORDER = 0x0004;
                const uint SWP_NOACTIVATE = 0x0010;
                SetWindowPos(overlayHwnd, IntPtr.Zero, vx, vy, vw, vh, SWP_NOZORDER | SWP_NOACTIVATE);

                // GL overlay: update cached dimensions (SwapBuffers uses current window size)
                if (_glOverlayHwnd != IntPtr.Zero)
                {
                    _glOverlayWidth = vw;
                    _glOverlayHeight = vh;
                }

                // Debounce swapchain recreation — destroy + create is too expensive to
                // run on every pixel of a window drag. Reposition the Win32 overlay
                // instantly (cheap) but defer the heavy swapchain work until 150ms after
                // the last resize event. Covers both the Vulkan and D3D11 swapchains.
                bool hasSwapchain = (_vulkanContext != null && _vulkanContext.HasSwapchain)
                                 || (_d3d11Context != null && _d3d11Context.HasSwapchain);
                if (hasSwapchain)
                {
                    if (_swapchainResizeTimer == null)
                    {
                        _swapchainResizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                        _swapchainResizeTimer.Tick += (_, _) =>
                        {
                            _swapchainResizeTimer.Stop();
                            var vp = GameViewport;
                            uint w = (uint)Math.Max(1, (int)vp.ActualWidth);
                            uint h = (uint)Math.Max(1, (int)vp.ActualHeight);
                            if (_displayAr > 0.01)
                            {
                                double vpAr = (double)w / h;
                                if (_displayAr > vpAr)
                                    h = (uint)Math.Max(1, (int)(w / _displayAr));
                                else if (_displayAr < vpAr)
                                    w = (uint)Math.Max(1, (int)(h * _displayAr));
                            }
                            if (_vulkanContext != null && _vulkanContext.HasSwapchain)
                                _vulkanContext.RecreateSwapchain(w, h);
                            if (_d3d11Context != null && _d3d11Context.HasSwapchain)
                                _d3d11Context.RecreateSwapchain((int)w, (int)h);
                        };
                    }
                    _swapchainResizeTimer.Stop();
                    _swapchainResizeTimer.Start();
                }

                // Keep HUD window in sync if it's showing
                RepositionVulkanHud();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Overlay reposition: {ex.Message}");
            }
        }

        private void DestroyVulkanOverlay()
        {
            LocationChanged -= VulkanOverlay_Reposition;
            SizeChanged -= VulkanOverlay_Reposition;
            StateChanged -= VulkanOverlay_StateChanged;
            if (_swapchainResizeTimer != null) { _swapchainResizeTimer.Stop(); _swapchainResizeTimer = null; }

            // Reparent OverlayHud back to main window if it's in the HUD window
            if (_vulkanHudGrid != null && OverlayHud.Parent == _vulkanHudGrid)
            {
                _vulkanHudGrid.Children.Remove(OverlayHud);
                GameViewport.Children.Add(OverlayHud);
            }
            if (_vulkanHudWindow != null)
            {
                _vulkanHudWindow.Close();
                _vulkanHudWindow = null;
                _vulkanHudGrid = null;
            }

            if (_vulkanOverlayHwnd != IntPtr.Zero)
            {
                DestroyWindow(_vulkanOverlayHwnd);
                _vulkanOverlayHwnd = IntPtr.Zero;
            }

            // GL overlay cleanup
            if (_glOverlayDC != IntPtr.Zero && _glOverlayHwnd != IntPtr.Zero)
            {
                ReleaseDC(_glOverlayHwnd, _glOverlayDC);
                _glOverlayDC = IntPtr.Zero;
            }
            if (_glOverlayHwnd != IntPtr.Zero)
            {
                DestroyWindow(_glOverlayHwnd);
                _glOverlayHwnd = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// A real Win32 child window embedded in the WPF layout via HwndHost airspace.
    /// Dolphin renders directly to FBO 0 on this window; SwapBuffers presents the frame.
    /// </summary>
    internal class GameHwndHost : HwndHost
    {
        private const uint WS_CHILD        = 0x40000000;
        private const uint WS_VISIBLE      = 0x10000000;
        private const uint WS_CLIPCHILDREN = 0x02000000;
        private const uint WS_CLIPSIBLINGS = 0x04000000;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName,
            string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        private IntPtr _hwnd = IntPtr.Zero;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _hwnd = CreateWindowEx(0, "Static", "",
                WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            System.Diagnostics.Trace.WriteLine($"GameHwndHost: HWND=0x{_hwnd:X}");
            return new HandleRef(this, _hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        }
    }

    internal static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern void RtlCopyMemory(IntPtr dest, IntPtr src, uint count);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr hModule);
    }

    internal static class NativeMethods2
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        internal static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);
    }
}