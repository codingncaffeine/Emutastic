using Emutastic.Models;
using Emutastic.Services;
using Emutastic.Services.ConsoleHandlers;
using Emutastic.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace Emutastic.Views
{
    public partial class EmulatorWindow : Window
    {
        // =========================================================================
        // Fields
        // =========================================================================
        private readonly Game _game;
        private readonly LibretroCore _core;
        private DispatcherTimer? _timer;
        private string _srmPath = "";   // per-game battery save file (.srm)
        private WriteableBitmap? _bitmap;
        private uint _videoWidth;
        private uint _videoHeight;
        // Reused frame buffer — avoids Large Object Heap allocation every frame.
        // Resized only when the core changes resolution.
        private byte[] _videoFrameBuffer = Array.Empty<byte>();
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

        // RETRO_DEVICE_ANALOG index / id constants
        private const uint RETRO_DEVICE_INDEX_ANALOG_LEFT   = 0;
        private const uint RETRO_DEVICE_INDEX_ANALOG_RIGHT  = 1;
        private const uint RETRO_DEVICE_INDEX_ANALOG_BUTTON = 2;  // analog triggers (Dreamcast L/R via Flycast)
        private const uint RETRO_DEVICE_ID_ANALOG_X         = 0;
        private const uint RETRO_DEVICE_ID_ANALOG_Y         = 1;

        // Joypad button IDs
        private readonly bool[] _inputState = new bool[16];
        private const uint JOYPAD_B      = 0;
        private const uint JOYPAD_Y      = 1;
        private const uint JOYPAD_SELECT = 2;
        private const uint JOYPAD_START  = 3;
        private const uint JOYPAD_UP     = 4;
        private const uint JOYPAD_DOWN   = 5;
        private const uint JOYPAD_LEFT   = 6;
        private const uint JOYPAD_RIGHT  = 7;
        private const uint JOYPAD_A      = 8;
        private const uint JOYPAD_X      = 9;
        private const uint JOYPAD_L      = 10;
        private const uint JOYPAD_R      = 11;
        private const uint JOYPAD_L2     = 12;
        private const uint JOYPAD_R2     = 13;

        // Keyboard analog axis state — used when no controller is connected.
        // Values follow libretro convention: up/left = negative, down/right = positive.
        // Y is already negated at assignment time so no further inversion is needed
        // when the controller path reads _keyLeftStickY.
        private short _keyLeftStickX;
        private short _keyLeftStickY;
        private short _keyRightStickX;
        private short _keyRightStickY;

        // Directory pointers (unmanaged lifetime)
        private IntPtr _systemDirPtr = IntPtr.Zero;
        private IntPtr _saveDirPtr   = IntPtr.Zero;

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

        // Actual frame counter for real FPS display (not the core's target rate)
        private int  _frameCount        = 0;
        private long _coreRunTotalTicks  = 0;   // sum of Stopwatch ticks spent inside _core.Run()
        private int  _coreRunSampleCount = 0;

        // Transient save/load status — shown for 3s alongside the FPS counter
        private string   _transientMsg    = "";
        private DateTime _transientExpiry = DateTime.MinValue;

        // Services
        private ControllerManager? _controllerManager;
        private AudioPlayer?       _audioPlayer;
        private readonly IConfigurationService _configService;
        private InputConfiguration? _inputConfig;
        private readonly Dictionary<Key, uint> _keyboardMappings = new();
        private DatabaseService? _db;

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
            if (port == 0 && _controllerManager != null)
            {
                // effect 0 = RETRO_RUMBLE_STRONG (left/low-freq motor)
                // effect 1 = RETRO_RUMBLE_WEAK   (right/high-freq motor)
                // Cores send each motor independently; accumulate both before applying.
                if (effect == 0) _rumbleStrong = strength;
                else             _rumbleWeak   = strength;
                _controllerManager.SetVibration(_rumbleStrong, _rumbleWeak);
            }
            return true;
        }
        private DispatcherTimer? _overlayTimer;
        private DispatcherTimer? _mousePoller;
        private System.Windows.Point _lastMousePos = new(-1, -1);

        // Save state
        private string _saveStatePath = "";    // file-system dir for this game's save states
        private volatile bool _saveStatePending = false;
        private volatile bool _loadStatePending = false;
        private string _pendingSaveName  = "";
        private byte[]? _pendingLoadData = null;
        private string _pendingLoadName  = "";
        private string? _pendingLoadStatePath = null;  // load on startup if set

        // Core options
        private readonly Dictionary<string, string> _coreOptions = new();
        // Track unmanaged string ptrs returned via GET_VARIABLE to prevent leaks
        private readonly Dictionary<string, IntPtr> _coreOptionPtrs = new();
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
        private byte[] _hwPixelBuffer  = Array.Empty<byte>();
        private byte[] _hwFlippedBuffer = Array.Empty<byte>();
        private volatile bool _hwVideoPending = false;  // drop frame if UI thread hasn't consumed last one

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

        // Field-pinned WndProc delegate — prevents GC collecting the stub while the
        // window class is registered (window class lifetime = process lifetime).
        private WndProcDelegate? _offscreenWndProc;

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
                    string logDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emutastic", "Logs");
                    Directory.CreateDirectory(logDir);
                    string logPath = Path.Combine(logDir, "emulator.log");
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
                SourceInitialized += OnSourceInitialized;

                _game = game;
                _core = core;
                _consoleHandler = ConsoleHandlerFactory.Create(game.Console);
                Title = $"{game.Title} - {game.Console}";

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string sysDir      = Path.Combine(appData, "Emutastic", "System");
                string batteryDir  = Path.Combine(appData, "Emutastic", "BatterySaves", game.Console);
                Directory.CreateDirectory(sysDir);
                Directory.CreateDirectory(batteryDir);
                _consoleHandler.PrepareSaveDirectory(batteryDir);

                // Per-game .srm file named after the ROM file stem (not the DB title),
                // matching how RetroArch and most frontends identify saves.
                string romStem = Path.GetFileNameWithoutExtension(game.RomPath);
                _srmPath = Path.Combine(batteryDir, SanitizeFileName(romStem) + ".srm");

                _saveStatePath = Path.Combine(appData, "Emutastic", "Save States",
                    SanitizeFileName(game.Console), SanitizeFileName(game.Title));
                Directory.CreateDirectory(_saveStatePath);
                _pendingLoadStatePath = pendingLoadStatePath;

                string coreDllDir = Path.GetDirectoryName(core.CorePath) ?? sysDir;
                string resolvedSysDir = _consoleHandler.ResolveSystemDirectory(sysDir, coreDllDir);
                Directory.CreateDirectory(resolvedSysDir);
                _systemDirPtr = Marshal.StringToHGlobalAnsi(resolvedSysDir);
                _saveDirPtr   = Marshal.StringToHGlobalAnsi(batteryDir);

                SeedDefaultCoreOptions();

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
                _controllerManager = new ControllerManager(_configService, null, game.Console);
                _controllerManager.ButtonChanged += OnControllerButtonChanged;
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
                _coreOptions[kv.Key] = kv.Value;
                System.Diagnostics.Trace.WriteLine($"User value: {kv.Key} = {kv.Value}");
            }
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

                // Overlay: set core label and start hide timer
                OverlayCoreLabel.Text = System.IO.Path.GetFileNameWithoutExtension(_core.CorePath);
                _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                _overlayTimer.Tick += (_, _) => HideOverlay();

                // Poll mouse position every 100ms — MouseMove doesn't fire over HwndHost
                // (Win32 child windows swallow mouse messages before WPF sees them).
                _mousePoller = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _mousePoller.Tick += (_, _) =>
                {
                    var pos = Mouse.GetPosition(this);
                    if (pos != _lastMousePos) { _lastMousePos = pos; ShowOverlay(); }
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
                double w = _configService.GetValue($"emuWinWidth_{_game.Console}",  0.0);
                double h = _configService.GetValue($"emuWinHeight_{_game.Console}", 0.0);
                if (w >= 320 && h >= 240)
                {
                    Width  = w;
                    Height = h;
                }
            }
            catch { }
        }

        private void SaveWindowSize()
        {
            try
            {
                if (WindowState == WindowState.Normal)
                {
                    _configService.SetValue($"emuWinWidth_{_game.Console}",  Width);
                    _configService.SetValue($"emuWinHeight_{_game.Console}", Height);
                    _ = _configService.SaveAsync();
                }
            }
            catch { }
        }

        private void StartEmulator()
        {
            // Raise emu thread priority so the OS doesn't preempt it mid-frame.
            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal;

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
                bool loaded = _core.LoadGame(_game.RomPath);
                System.Diagnostics.Trace.WriteLine($"LoadGame: {loaded}");

                if (!loaded)
                {
                    // Clean up GL + core state before returning so the close path
                    // (Task.Run → Dispose) doesn't crash calling retro_deinit without
                    // a GL context.  The GL context may be current from SET_HW_RENDER.
                    if (_hwRenderActive && _hdc != IntPtr.Zero && _hglrc != IntPtr.Zero)
                    {
                        wglMakeCurrent(_hdc, _hglrc);
                        try { _core?.Deinit(); }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Failed-load retro_deinit: {ex.Message}"); }
                        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                        System.Diagnostics.Trace.WriteLine("Failed-load GL cleanup done.");
                    }
                    else
                    {
                        try { _core?.Deinit(); }
                        catch { }
                    }

                    Dispatcher.Invoke(() => MessageBox.Show($"Failed to load {_game.Title}\n\nCheck debug output for details.",
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

                // Call retro_set_controller_port_device for all active ports.
                // Handler decides how many ports to configure (GameCube needs all 4).
                _consoleHandler.ConfigureControllerPorts(_core);

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

                Dispatcher.Invoke(() =>
                {
                    _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
                    _timer.Tick += (s, e) =>
                    {
                        int actual   = System.Threading.Interlocked.Exchange(ref _frameCount, 0);
                        long ticks   = System.Threading.Interlocked.Exchange(ref _coreRunTotalTicks, 0);
                        int  samples = System.Threading.Interlocked.Exchange(ref _coreRunSampleCount, 0);
                        double avgMs = samples > 0
                            ? (double)ticks / samples / System.Diagnostics.Stopwatch.Frequency * 1000.0
                            : 0;
                        string fpsStr = $"{actual} fps  (target {fps:F0})  core.Run avg {avgMs:F1}ms";
                        string msg    = _transientMsg;
                        StatusText.Text = (msg.Length > 0 && DateTime.Now < _transientExpiry)
                            ? $"{fpsStr}    ✓ {msg}"
                            : fpsStr;
                    };
                    _timer.Start();
                    StatusText.Text = "Running...";
                });

                _audioPlayer?.Start();

                // Per libretro spec: call context_reset AFTER retro_load_game returns,
                // not inside the SET_HW_RENDER callback (which fires mid-LoadGame).
                // Calling it too early puts mupen64plus / Dolphin in an invalid state.
                if (_hwRenderActive && _hwContextReset != null)
                {
                    // Always re-acquire the context on the emu thread for context_reset.
                    // For shared-context cores (N64) the context was pre-created here and
                    // is still current, so this is a no-op — but being explicit is safe.
                    // For single-threaded cores it makes the context current for retro_run.
                    wglMakeCurrent(_hdc, _hglrc);
                    System.Diagnostics.Trace.WriteLine($"Pre-context_reset: wglMakeCurrent _hglrc=0x{_hglrc:X}");

                    // Resize FBO to final dimensions BEFORE context_reset.  The initial
                    // FBO is only 640×480 (created during SET_HW_RENDER mid-LoadGame).
                    // Cores like vecx query the FBO size during context_reset to set up
                    // their GL viewport; if the FBO is still 640×480, the viewport clips
                    // game content that extends beyond 640×480 — causing the top and right
                    // edges to be cut off for the lifetime of the session.
                    if (!_consoleHandler.AllowHwSharedContext && !_consoleHandler.UseEmbeddedWindow)
                    {
                        var geom = _core.AvInfo.geometry;
                        uint needW = geom.max_width  > 0 ? geom.max_width  : geom.base_width;
                        uint needH = geom.max_height > 0 ? geom.max_height : geom.base_height;
                        if (needW > _fboWidth || needH > _fboHeight)
                        {
                            System.Diagnostics.Trace.WriteLine(
                                $"Pre-context_reset FBO resize: {_fboWidth}x{_fboHeight} → {needW}x{needH}");
                            CreateFBO(needW, needH);
                        }
                    }

                    _consoleHandler.OnBeforeContextReset();
                    System.Diagnostics.Trace.WriteLine("Calling context_reset (post-LoadGame, per libretro spec)...");
                    _hwContextReset.Invoke();
                    _consoleHandler.OnAfterContextReset();
                    System.Diagnostics.Trace.WriteLine("context_reset done.");

                    // Re-apply vsync=0 after context_reset — glide64 calls wglSwapIntervalEXT(1)
                    // during init, which would cap the loop at monitorHz÷3 (48fps on 144Hz).
                    // GetProcAddress intercepts the extension for future calls; this covers any
                    // wglSwapIntervalEXT call glide64 made via the real extension pointer it
                    // cached before context_reset.
                    var swapFn = GetGLProc<wglSwapIntervalEXTDelegate>("wglSwapIntervalEXT");
                    if (swapFn != null)
                    {
                        swapFn(0);
                        System.Diagnostics.Trace.WriteLine("vsync re-disabled after context_reset.");
                    }

                    if (_consoleHandler.AllowHwSharedContext)
                    {
                        // Release so the core's EmuThread can claim the context for rendering.
                        // N64: EmuThread renders into FBO 0; video callback reads it back.
                        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                        System.Diagnostics.Trace.WriteLine("GL context released for EmuThread (shared context mode).");
                    }
                    // else: keep current for single-threaded retro_run readback.
                }

                // If launched via "Load" from the save states browser, queue the state to be applied
                // between retro_run calls (after the first frame). Calling retro_unserialize before
                // any retro_run has executed is not safe — the core may not be at a consistent
                // checkpoint yet (mupen64plus starts its own EmuThread during retro_load_game).
                if (_pendingLoadStatePath != null && File.Exists(_pendingLoadStatePath))
                {
                    try
                    {
                        _pendingLoadData  = File.ReadAllBytes(_pendingLoadStatePath);
                        _pendingLoadName  = Path.GetFileNameWithoutExtension(_pendingLoadStatePath);
                        _loadStatePending = true;
                        System.Diagnostics.Trace.WriteLine($"Queued pending state load: {_pendingLoadStatePath}");
                    }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Pending load read failed: {ex.Message}"); }
                    _pendingLoadStatePath = null;
                }

                IntPtr curCtx = wglGetCurrentContext();
                System.Diagnostics.Trace.WriteLine($"Pre-loop GL: current=0x{curCtx:X} _hglrc=0x{_hglrc:X}");
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

                if (_hwRenderActive && _hdc != IntPtr.Zero)
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

                    // Stop emulation. Core threads run their GL cleanup while the context
                    // is still properly owned (either by us or by the core's GPU thread).
                    try { _core?.UnloadGame(); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"UnloadGame: {ex.Message}"); }

                    string _teardownCoreName = _core != null ? System.IO.Path.GetFileName(_core.CorePath).ToLowerInvariant() : "";

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
                                           || _teardownCoreName.Contains("parallel_n64");
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
                        || _teardownCoreName.Contains("ppsspp"))
                    {
                        System.Diagnostics.Trace.WriteLine("Calling retro_deinit on emu thread (GL context active)...");
                        try { _core?.Deinit(); }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Emu-thread retro_deinit: {ex.Message}"); }
                        System.Diagnostics.Trace.WriteLine("Emu-thread retro_deinit complete.");
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
            const int prefillMs    = 150;   // fill to this level before starting the Stopwatch loop
            const int lowWatermark = 80;    // run an extra retro_run if buffer dips this low
            const int backpressureMs = 300; // pause if buffer exceeds this (core running too fast)
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
                while (!_isClosing && (_audioPlayer?.GetBufferedMs() ?? prefillMs) < prefillMs)
                {
                    _core?.Run();
                    // Apply startup state after the first retro_run — core is now at a safe checkpoint.
                    if (_loadStatePending) ExecuteLoadOnEmuThread();
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
                        _core.Run();
                        _sw.Stop();
                        System.Threading.Interlocked.Add(ref _coreRunTotalTicks, _sw.ElapsedTicks);
                        System.Threading.Interlocked.Increment(ref _coreRunSampleCount);

                        // Low-watermark catch-up: if the buffer dipped below the safe cushion,
                        // run one extra frame to refill before sleeping the frame budget.
                        if ((_audioPlayer?.GetBufferedMs() ?? lowWatermark) < lowWatermark)
                            _core.Run();

                        // Pending save/load — executed between retro_run calls for thread safety.
                        if (_saveStatePending) ExecuteSaveOnEmuThread();
                        if (_loadStatePending) ExecuteLoadOnEmuThread();
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
                    if (isHwCore && _audioPlayer != null)
                    {
                        frameTimer.Restart();
                        while (_audioPlayer.GetBufferedMs() > prefillMs &&
                               frameTimer.Elapsed.TotalMilliseconds < _targetFrameMs * 4)
                            System.Threading.Thread.Sleep(1);
                        frameTimer.Restart();
                    }
                    else
                    {
                        double elapsed = frameTimer.Elapsed.TotalMilliseconds;
                        double remaining = _targetFrameMs - elapsed;
                        if (remaining > 1.5)
                            System.Threading.Thread.Sleep((int)(remaining - 1.0));
                        while (frameTimer.Elapsed.TotalMilliseconds < _targetFrameMs)
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
                if (_consoleHandler.UseEmbeddedWindow) pfdFlags |= PFD_DOUBLEBUFFER;

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

            // Drop this frame if the UI thread hasn't consumed the previous one yet.
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

                _hwVideoPending = true;
                uint capturedW = w, capturedH = h;
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
                        }
                        _bitmap.Lock();
                        Marshal.Copy(_hwFlippedBuffer, 0, _bitmap.BackBuffer, (int)(capturedW * capturedH * 4));
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
            if (_hwVideoPending) return;

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

                _hwVideoPending = true;
                uint capturedW = w, capturedH = h;
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
                        }
                        _bitmap.Lock();
                        Marshal.Copy(_hwFlippedBuffer, 0, _bitmap.BackBuffer, (int)(capturedW * capturedH * 4));
                        _bitmap.AddDirtyRect(new Int32Rect(0, 0, (int)capturedW, (int)capturedH));
                        _bitmap.Unlock();
                    }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"HW video UI: {ex.Message}"); }
                    finally { _hwVideoPending = false; }
                }, DispatcherPriority.Render);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"ReadBackFromCurrentContext: {ex.Message}"); }
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
        private const uint RETRO_ENVIRONMENT_SET_HW_SHARED_CONTEXT                     = 45;
        private const uint RETRO_ENVIRONMENT_GET_VFS_INTERFACE                         = 46;
        private const uint RETRO_ENVIRONMENT_GET_LED_INTERFACE                         = 47;
        private const uint RETRO_ENVIRONMENT_GET_AUDIO_VIDEO_INTERFACE                 = 48;
        private const uint RETRO_ENVIRONMENT_GET_FASTMATHING_INTERFACE                 = 49;
        private const uint RETRO_ENVIRONMENT_GET_MIDI_INTERFACE                        = 50;
        private const uint RETRO_ENVIRONMENT_GET_TARGET_REFRESH_RATE                   = 52;
        private const uint RETRO_ENVIRONMENT_GET_INPUT_BITMASKS                        = 53;
        private const uint RETRO_ENVIRONMENT_GET_CORE_OPTIONS_VERSION                  = 54;
        private const uint RETRO_ENVIRONMENT_SET_CORE_OPTIONS                          = 55;
        private const uint RETRO_ENVIRONMENT_SET_CORE_OPTIONS_INTL                     = 56;
        private const uint RETRO_ENVIRONMENT_GET_CORE_OPTIONS_DISPLAY                  = 57;
        private const uint RETRO_ENVIRONMENT_GET_CORE_OPTIONS_UPDATE_DISPLAY           = 58;
        private const uint RETRO_ENVIRONMENT_SET_CORE_OPTIONS_V2                       = 59;
        private const uint RETRO_ENVIRONMENT_SET_CORE_OPTIONS_V2_INTL                  = 60;
        private const uint RETRO_ENVIRONMENT_GET_PREFERRED_HW_RENDER                   = 61;
        private const uint RETRO_ENVIRONMENT_GET_DISK_CONTROL_INTERFACE_VERSION        = 62;
        private const uint RETRO_ENVIRONMENT_SET_DISK_CONTROL_EXT_INTERFACE            = 63;
        private const uint RETRO_ENVIRONMENT_GET_MESSAGE_INTERFACE_VERSION             = 64;
        private const uint RETRO_ENVIRONMENT_SET_MESSAGE_EXT                           = 65;
        private const uint RETRO_ENVIRONMENT_SET_INPUT_BITMASKS                        = 66;
        private const uint RETRO_ENVIRONMENT_GET_CORE_OPTIONS_UPDATE_DISPLAY_CALLBACK  = 67;
        private const uint RETRO_ENVIRONMENT_SET_CORE_OPTIONS_UPDATE_DISPLAY_CALLBACK  = 68;
        private const uint RETRO_ENVIRONMENT_GET_INPUT_MAX_USERS                       = 69;

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
        private bool OnEnvironment(uint cmd, IntPtr data)
        {
            uint baseCmd = cmd & 0xFF;
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

                    // Extended disc interface — acknowledge but not fully implemented
                    case RETRO_ENVIRONMENT_SET_DISK_CONTROL_EXT_INTERFACE:
                        System.Diagnostics.Trace.WriteLine("SET_DISK_CONTROL_EXT_INTERFACE acknowledged");
                        return true;

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

                        if (hw.context_type != RETRO_HW_CONTEXT_OPENGL &&
                            hw.context_type != RETRO_HW_CONTEXT_OPENGL_CORE)
                        {
                            System.Diagnostics.Trace.WriteLine($"Rejecting context_type={hw.context_type}");
                            return false;
                        }

                        if (!InitOpenGLContext()) return false;

                        CreateFBO(640, 480);
                        _hwRenderActive = true;

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
                        return false;

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
                                DefaultValue = _coreOptions.TryGetValue(key, out string? dv) ? dv : ""
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
                            // Free previous allocation for this key, then allocate new one
                            if (_coreOptionPtrs.TryGetValue(key, out IntPtr oldPtr) && oldPtr != IntPtr.Zero)
                                Marshal.FreeHGlobal(oldPtr);
                            IntPtr valPtr = Marshal.StringToHGlobalAnsi(value);
                            _coreOptionPtrs[key] = valPtr;
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

                    case RETRO_ENVIRONMENT_GET_SYSTEM_DIRECTORY:
                        if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, _systemDirPtr);
                        return true;

                    case RETRO_ENVIRONMENT_GET_SAVE_DIRECTORY:
                        if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, _saveDirPtr);
                        return true;

                    // Advertise joypad + analog capability
                    case RETRO_ENVIRONMENT_GET_INPUT_DEVICE_CAPABILITIES:
                        if (data != IntPtr.Zero)
                            Marshal.WriteInt64(data, (1L << (int)RETRO_DEVICE_JOYPAD) |
                                                     (1L << (int)RETRO_DEVICE_ANALOG));
                        return true;

                    // GET_FASTFORWARDING = (49 | 0x10000) — Dolphin asks if we're fast-forwarding.
                    // data is a bool* (1 byte). Writing Int32 here would corrupt Dolphin's stack.
                    case RETRO_ENVIRONMENT_GET_FASTMATHING_INTERFACE:  // base 49 = GET_FASTFORWARDING
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

                    case RETRO_ENVIRONMENT_SET_AUDIO_CALLBACK:
                    case RETRO_ENVIRONMENT_SET_INPUT_DESCRIPTORS:
                    case RETRO_ENVIRONMENT_SET_SUPPORT_NO_GAME:
                    case RETRO_ENVIRONMENT_GET_USERNAME:
                    case RETRO_ENVIRONMENT_GET_LANGUAGE:
                    case RETRO_ENVIRONMENT_GET_INPUT_BITMASKS:
                    case RETRO_ENVIRONMENT_SET_INPUT_BITMASKS:
                    case RETRO_ENVIRONMENT_GET_TARGET_REFRESH_RATE:
                    case RETRO_ENVIRONMENT_GET_AUDIO_VIDEO_INTERFACE:
                    case RETRO_ENVIRONMENT_SET_PERFORMANCE_LEVEL:
                    case RETRO_ENVIRONMENT_SET_SERIALIZATION_QUIRKS:
                    case RETRO_ENVIRONMENT_SET_SUBSYSTEM_INFO:
                    case RETRO_ENVIRONMENT_SET_MEMORY_MAPS:
                    case RETRO_ENVIRONMENT_SET_HW_RENDER_CONTEXT_NEGOTIATION_INTERFACE:
                        return false;

                    case RETRO_ENVIRONMENT_SET_HW_SHARED_CONTEXT:
                        // N64/glide64's EmuThread needs this to create a shared GL context.
                        // For all other cores, return false so they don't rely on it.
                        return _consoleHandler.AllowHwSharedContext;

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
        private double _displayAr    = 0;   // current display aspect ratio (0 = unknown)
        private bool   _windowSized  = false; // true after the first auto-size

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

                _displayAr = displayAr;

                GameScreen.Width   = double.NaN;
                GameScreen.Height  = double.NaN;
                GameScreen.Stretch = Stretch.Uniform;

                double bitmapAr = baseHeight > 0 ? (double)baseWidth / baseHeight : displayAr;
                double scaleX   = displayAr / bitmapAr;

                // Apply both the AR correction scale and any rotation the core requested.
                var group = new TransformGroup();
                group.Children.Add(new ScaleTransform(scaleX, 1.0));
                if (_coreRotation != 0)
                    group.Children.Add(new RotateTransform(_coreRotation * 90.0));
                GameScreen.LayoutTransform = group;

                if (!_windowSized)
                {
                    _windowSized = true;
                    AutoSizeWindowToGameAr(displayAr);
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
            double nativeW = geom.base_width  * 2.0;
            double nativeH = geom.base_height * 2.0;

            // Apply the display AR correction (same scaleX used in LayoutTransform).
            double bitmapAr = geom.base_height > 0 ? (double)geom.base_width / geom.base_height : displayAr;
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

            Width  = Math.Max(gameW, 320);
            Height = Math.Max(gameH + chromeH, 200);
        }

        // =========================================================================
        // Video refresh — software cores
        // =========================================================================
        private void OnVideoRefresh(IntPtr data, uint width, uint height, UIntPtr pitch)
        {
            if (_hwRenderActive)
            {
                // data == (void*)-1 means RETRO_HW_FRAME_BUFFER_VALID.
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
                    ReadBackFromCurrentContext(_fboId, rw, rh);
                }
                return;
            }
            if (data == IntPtr.Zero) return;
            System.Threading.Interlocked.Increment(ref _frameCount);
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
                    _videoFrameBuffer = new byte[frameSize];
                Marshal.Copy(data, _videoFrameBuffer, 0, frameSize);
                _videoPending = true;

                // Capture locals for the closure — fields may change on next frame.
                byte[] buf      = _videoFrameBuffer;
                int    sp       = srcPitch;
                int    rBytes   = rowBytes;
                uint   w = width, h = height;
                PixelFormat pf  = pixFmt;

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (_bitmap == null || _videoWidth != w || _videoHeight != h || _bitmap.Format != pf)
                        {
                            _videoWidth = w; _videoHeight = h;
                            _bitmap = new WriteableBitmap((int)w, (int)h, 96, 96, pf, null);
                            GameScreen.Source = _bitmap;
                            UpdateDisplayAspectRatio(w, h, _core?.AvInfo.geometry.aspect_ratio ?? 0f);
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
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Video UI: {ex.Message}"); }
                    finally { _videoPending = false; }
                }, DispatcherPriority.Render);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Video refresh: {ex.Message}"); }
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
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Audio batch: {ex.Message}"); }
            return frames;
        }

        // =========================================================================
        // Core log interface
        // =========================================================================
        // NOTE: fires on native core threads — Trace.WriteLine is safe because
        // App.OnStartup replaces DefaultTraceListener with ConsoleTraceListener.
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
            }
            catch { }
        }

        /// <summary>
        /// Minimal printf formatter for core log messages.
        /// Handles the common specifiers cores use (%s, %d, %i, %u, %x, %X, %ld, %lu, %02d, etc.).
        /// Floats are skipped — their 8-byte value can't be reliably read from an IntPtr slot.
        /// Covers up to 4 varargs (R8, R9, and first two stack slots in x64 Windows ABI).
        /// </summary>
        private static string FormatCoreLog(string fmt, IntPtr a0, IntPtr a1, IntPtr a2, IntPtr a3)
        {
            if (!fmt.Contains('%')) return fmt;

            var args = new IntPtr[] { a0, a1, a2, a3 };
            int argIdx = 0;

            return System.Text.RegularExpressions.Regex.Replace(fmt,
                @"%%|%[-+0 #]*\d*(?:\.\d+)?(?:hh?|ll?|[Lqjzt])?([diouxXscp])",
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
                        _          => m.Value
                    };
                });
        }

        private static string PadNum(string s, int width, bool zeroPad)
            => width > 0 ? (zeroPad ? s.PadLeft(width, '0') : s.PadLeft(width)) : s;

        // =========================================================================
        // Input
        // =========================================================================
        private void OnInputPoll() { }

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
            if (port != 0) return 0;

            if (device == RETRO_DEVICE_JOYPAD)
            {
                if (id >= (uint)_inputState.Length) return 0;
                bool pressed = _inputState[id] || (_controllerManager?.GetButtonState(id) ?? false);
                return pressed ? (short)1 : (short)0;
            }

            if (device == RETRO_DEVICE_ANALOG)
            {
                // Analog triggers — index=2 (RETRO_DEVICE_INDEX_ANALOG_BUTTON), id=L2(12)/R2(13).
                // Flycast queries Dreamcast L/R triggers this way. Returns 0..32767.
                if (index == RETRO_DEVICE_INDEX_ANALOG_BUTTON)
                {
                    if (_controllerManager != null && _controllerManager.IsConnected)
                    {
                        if (id == JOYPAD_L2) return _controllerManager.GetTriggerValue(0);
                        if (id == JOYPAD_R2) return _controllerManager.GetTriggerValue(1);
                    }
                    return 0;
                }

                // Analog sticks — index=0 (left) or 1 (right), id=0 (X) or 1 (Y).
                if (id == RETRO_DEVICE_ID_ANALOG_X || id == RETRO_DEVICE_ID_ANALOG_Y)
                {
                    if (_controllerManager != null && _controllerManager.IsConnected)
                    {
                        short raw = _controllerManager.GetAnalogAxisValue(index, id);

                        // Negate Y: XInput up = +32767, libretro up = -32768
                        if (id == RETRO_DEVICE_ID_ANALOG_Y)
                            raw = raw == short.MinValue ? short.MaxValue : (short)-raw;

                        return raw;
                    }
                    else
                    {
                        // Keyboard fallback — already in libretro convention
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

            return 0;
            }
            catch { return 0; }
        }

        private void OnControllerButtonChanged(uint button, bool pressed)
        {
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            SetKey(e.Key, true);
            if (e.Key == Key.Escape) Close();
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
            e.Handled = true;
        }

        protected override void OnKeyUp(KeyEventArgs e) { SetKey(e.Key, false); base.OnKeyUp(e); }

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
                foreach (var mapping in _inputConfig.KeyboardMappings)
                {
                    if (Enum.TryParse<Key>(mapping.InputIdentifier, out var key))
                    {
                        uint id = GetLibretroButtonId(mapping.ButtonName, _game.Console);
                        if (id < 16) _keyboardMappings[key] = id;
                    }
                }
                System.Diagnostics.Trace.WriteLine($"Loaded {_keyboardMappings.Count} keyboard mappings");
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

        private uint GetLibretroButtonId(string name, string console = "")
        {
            string n = name.ToLower();

            switch (console)
            {
                // ── Sega 6-button layout: A→Y, C→A, Z→R, Mode→Select ─────────
                case "Genesis": case "SegaCD": case "Sega32X":
                    return n switch {
                        "a" => JOYPAD_Y, "b" => JOYPAD_B, "c" => JOYPAD_A,
                        "x" => JOYPAD_X, "y" => JOYPAD_L, "z" => JOYPAD_R,
                        "mode" => JOYPAD_SELECT, "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };
                case "Saturn":
                    return n switch {
                        "a" => JOYPAD_Y, "b" => JOYPAD_B, "c" => JOYPAD_A,
                        "x" => JOYPAD_X, "y" => JOYPAD_L, "z" => JOYPAD_R,
                        "l" => 12, "r" => 13,               // shoulder → L2/R2
                        "select" => JOYPAD_SELECT, "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── PlayStation: Sony button names → libretro IDs ─────────────
                case "PS1": case "PSP":
                    return n switch {
                        "cross" => JOYPAD_B, "circle" => JOYPAD_A,
                        "square" => JOYPAD_Y, "triangle" => JOYPAD_X,
                        "l1" => JOYPAD_L, "r1" => JOYPAD_R,
                        "l2" => 12, "r2" => 13, "l3" => 14, "r3" => 15,
                        "select" => JOYPAD_SELECT, "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── NEC PC-Engine ─────────────────────────────────────────────
                case "TG16": case "TGCD":
                    return n switch {
                        "ii" => JOYPAD_B, "i" => JOYPAD_A,
                        "select" => JOYPAD_SELECT, "run" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };
                // ── Nintendo 64 (Z trigger → L2; C-buttons via analog path) ──
                case "N64":
                    return n switch {
                        "a" => JOYPAD_B, "b" => JOYPAD_Y,   // N64 A=south(0), B=west(1) per RetroArch standard
                        "z" => 12, "l" => JOYPAD_L, "r" => JOYPAD_R,
                        "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue   // C-buttons / analog handled by WASD/IJKL
                    };

                // ── GameCube (Z → L2; analog handled by WASD/IJKL) ───────────
                case "GameCube":
                    return n switch {
                        "a" => JOYPAD_A, "b" => JOYPAD_B, "x" => JOYPAD_X, "y" => JOYPAD_Y,
                        "l" => JOYPAD_L, "r" => JOYPAD_R, "z" => 12,
                        "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Sega 8-bit: numbered buttons ──────────────────────────────
                case "SMS": case "GameGear": case "SG1000":
                    return n switch {
                        "1" => JOYPAD_B, "2" => JOYPAD_A, "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Atari ─────────────────────────────────────────────────────
                case "Atari2600": case "Atari7800":
                    return n switch {
                        "fire" => JOYPAD_B, "fire 1" => JOYPAD_B, "fire 2" => JOYPAD_Y,
                        "pause" => JOYPAD_START, "reset" => JOYPAD_SELECT,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };
                case "Jaguar":
                    return n switch {
                        "a" => JOYPAD_B, "b" => JOYPAD_A, "c" => JOYPAD_R,
                        "option" => JOYPAD_SELECT, "pause" => JOYPAD_START,
                        "*" => JOYPAD_L, "#" => JOYPAD_Y, "0" => JOYPAD_X,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };
                case "Dreamcast":
                    return n switch {
                        "a" => JOYPAD_B, "b" => JOYPAD_A, "x" => JOYPAD_Y, "y" => JOYPAD_X,
                        "start" => JOYPAD_START,
                        "l trigger" => JOYPAD_L2, "r trigger" => JOYPAD_R2,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue  // analog directions handled via RETRO_DEVICE_ANALOG path
                    };

                // ── Others ────────────────────────────────────────────────────
                case "ColecoVision":
                    return n switch {
                        "l" => JOYPAD_L, "r" => JOYPAD_R,
                        "1" => JOYPAD_A, "2" => JOYPAD_B, "3" => JOYPAD_X, "4" => JOYPAD_Y,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                case "Vectrex":
                    return n switch {
                        "1" => JOYPAD_A, "2" => JOYPAD_B, "3" => JOYPAD_X, "4" => JOYPAD_Y,
                        _ => uint.MaxValue
                    };
                case "3DO":
                    return n switch {
                        "c" => JOYPAD_A, "b" => JOYPAD_B, "a" => JOYPAD_Y, "x" => JOYPAD_X,
                        "l" => JOYPAD_L, "r" => JOYPAD_R, "p" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };
                case "NGP":
                    return n switch {
                        "a" => JOYPAD_A, "b" => JOYPAD_B, "option" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };
                case "VirtualBoy":
                    return n switch {
                        "left up"    => JOYPAD_UP,   "left down"  => JOYPAD_DOWN,
                        "left left"  => JOYPAD_LEFT, "left right" => JOYPAD_RIGHT,
                        "right up"   => JOYPAD_X,    "right down" => JOYPAD_B,
                        "right left" => JOYPAD_Y,    "right right"=> JOYPAD_A,
                        "a" => JOYPAD_A, "b" => JOYPAD_B, "l" => JOYPAD_L, "r" => JOYPAD_R,
                        "select" => JOYPAD_SELECT, "start" => JOYPAD_START,
                        _ => uint.MaxValue
                    };
            }

            // Standard libretro joypad mapping (NES, SNES, GB, GBA, NDS, FDS, MSX, etc.)
            return n switch
            {
                "b" => JOYPAD_B, "y" => JOYPAD_Y, "select" => JOYPAD_SELECT, "start" => JOYPAD_START,
                "up" => JOYPAD_UP, "down" => JOYPAD_DOWN, "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                "a" => JOYPAD_A, "x" => JOYPAD_X, "l" => JOYPAD_L, "r" => JOYPAD_R,
                "l2" => 12, "r2" => 13, "l3" => 14, "r3" => 15,
                _ => uint.MaxValue
            };
        }

        private const short KEY_FULL = 32767;

        private void SetKey(Key key, bool pressed)
        {
            // Custom mappings first
            if (_keyboardMappings.TryGetValue(key, out var id) && id < 16)
            {
                _inputState[id] = pressed;
                return;
            }

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
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        }

        /// <summary>Request a named save from the UI thread. Emu thread picks it up after next retro_run.</summary>
        private void RequestSave(string name)
        {
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

            if (isHw && _hwFlippedBuffer.Length > 0)
            {
                screenshotPixels = (byte[])_hwFlippedBuffer.Clone();
                ssWidth  = _fboWidth;
                ssHeight = _fboHeight;
            }

            System.Threading.Tasks.Task.Run(() => FinalizeSave(name, data, screenshotPixels, ssWidth, ssHeight, isHw));
        }

        private void FinalizeSave(string name, byte[] data,
            byte[]? screenshotPixels, uint ssWidth, uint ssHeight, bool isHw)
        {
            try
            {
                string safeName = SanitizeFileName(name.Length > 0 ? name : "state");
                string statePath = Path.Combine(_saveStatePath, safeName + ".state");
                string pngPath   = Path.Combine(_saveStatePath, safeName + ".png");
                string jsonPath  = Path.Combine(_saveStatePath, safeName + ".json");

                File.WriteAllBytes(statePath, data);

                // Screenshot — HW cores pre-capture pixels on emu thread; SW cores capture from bitmap on UI thread below.
                if (!isHw || (screenshotPixels != null && ssWidth > 0 && ssHeight > 0))
                {
                    try
                    {
                        BitmapSource bmp;
                        if (isHw)
                        {
                            bmp = BitmapSource.Create((int)ssWidth, (int)ssHeight,
                                96, 96, PixelFormats.Bgra32, null, screenshotPixels,
                                (int)ssWidth * 4);
                        }
                        else
                        {
                            // Software core: capture from WPF WriteableBitmap on UI thread
                            byte[]? swPixels = null;
                            int swW = 0, swH = 0, swStride = 0;
                            Dispatcher.Invoke(() =>
                            {
                                if (_bitmap != null)
                                {
                                    swW = _bitmap.PixelWidth; swH = _bitmap.PixelHeight;
                                    swStride = _bitmap.BackBufferStride; // actual stride (Bgr565 = swW*2, not swW*4)
                                    swPixels = new byte[swH * swStride];
                                    _bitmap.CopyPixels(swPixels, swStride, 0);
                                }
                            });
                            if (swPixels != null && swW > 0)
                            {
                                if (_pixelFormat == RETRO_PIXEL_FORMAT_XRGB8888)
                                {
                                    // Bgr32 raw data: bytes are [B, G, R, X] where X=0.
                                    // Set X→0xFF so BitmapSource.Create(Bgra32) gets fully opaque alpha.
                                    for (int i = 3; i < swPixels.Length; i += 4)
                                        swPixels[i] = 0xFF;
                                }
                                else if (_pixelFormat == RETRO_PIXEL_FORMAT_RGB565)
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
                                pngPath = "";
                                bmp = null!;
                            }
                        }

                        if (bmp != null)
                        {
                            bmp.Freeze();
                            using var fs = new FileStream(pngPath, FileMode.Create);
                            var enc = new PngBitmapEncoder();
                            enc.Frames.Add(BitmapFrame.Create(bmp));
                            enc.Save(fs);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"Screenshot failed: {ex.Message}");
                        pngPath = "";
                    }
                }
                else pngPath = "";
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
            try
            {
                _pendingLoadData  = File.ReadAllBytes(statePath);
                _pendingLoadName  = name;
                _loadStatePending = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not read state file: {ex.Message}";
            }
        }

        /// <summary>Called on the emu thread between retro_run calls.</summary>
        private void ExecuteLoadOnEmuThread()
        {
            _loadStatePending = false;
            byte[]? data = _pendingLoadData;
            string   name = _pendingLoadName;
            _pendingLoadData = null;

            if (data == null) return;
            bool ok = _core?.LoadState(data) ?? false;
            _transientMsg    = ok ? $"Loaded: {name}" : $"Failed to load: {name}";
            _transientExpiry = DateTime.Now.AddSeconds(3);
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
        private void ShowOverlay()
        {
            if (OverlayHud.Visibility != Visibility.Visible)
            {
                OverlayHud.Visibility = Visibility.Visible;
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                OverlayHud.BeginAnimation(OpacityProperty, fade);
            }
            _overlayTimer?.Stop();
            _overlayTimer?.Start();
        }

        private void HideOverlay()
        {
            _overlayTimer?.Stop();
            OverlayMenu.Visibility = Visibility.Collapsed;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fade.Completed += (_, _) => OverlayHud.Visibility = Visibility.Collapsed;
            OverlayHud.BeginAnimation(OpacityProperty, fade);
        }

        private void ResetOverlayTimer()
        {
            _overlayTimer?.Stop();
            _overlayTimer?.Start();
        }

        private void TogglePause()
        {
            _isPaused = !_isPaused;
            OverlayPauseIcon.Kind = _isPaused
                ? MaterialDesignThemes.Wpf.PackIconKind.Play
                : MaterialDesignThemes.Wpf.PackIconKind.Pause;
        }

        private void OverlayPower_Click(object sender, RoutedEventArgs e)   => Close();
        private void OverlayPause_Click(object sender, RoutedEventArgs e)   { TogglePause(); ResetOverlayTimer(); }
        private void OverlaySave_Click(object sender, RoutedEventArgs e)
        {
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss");
            RequestSave(ts);
            ResetOverlayTimer();
        }
        private void OverlayCog_Click(object sender, RoutedEventArgs e)
        {
            OverlayMenu.Visibility = OverlayMenu.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            ResetOverlayTimer();
        }
        private void OverlayEditControls_Click(object sender, RoutedEventArgs e)
        {
            OverlayMenu.Visibility = Visibility.Collapsed;
            var win = new PreferencesWindow(_db!, _controllerManager, _configService,
                initialConsole: _game?.Console)
                { Owner = this };
            win.ShowDialog();
            LoadKeyboardMappings();
            _controllerManager?.ReloadInputConfiguration();
            ResetOverlayTimer();
        }

        private void OverlayCoreOptions_Click(object sender, RoutedEventArgs e)
        {
            OverlayMenu.Visibility = Visibility.Collapsed;
            BuildCoreOptionsOverlay();
            CoreOptionsPanel.Visibility = Visibility.Visible;
            ResetOverlayTimer();
        }

        private void CoreOptionsDone_Click(object sender, RoutedEventArgs e)
        {
            CoreOptionsPanel.Visibility = Visibility.Collapsed;
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

        private void BuildCoreOptionsOverlay()
        {
            CoreOptionRows.Children.Clear();

            string coreName = Path.GetFileNameWithoutExtension(_core.CorePath);
            var schema = App.CoreOptions.LoadSchema(coreName);

            if (schema == null || schema.Options.Count == 0)
            {
                CoreOptionRows.Children.Add(new TextBlock
                {
                    Text = "No options have been discovered for this core yet.\nRestart the game once to populate this list.",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x8A)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 4)
                });
                return;
            }

            var style = TryFindResource("OverlayComboBox") as Style;
            string cn = coreName;

            foreach (var opt in schema.Options)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

                row.Children.Add(new TextBlock
                {
                    Text = opt.Description,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xCA)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 3)
                });

                var combo = new ComboBox { Height = 30 };
                if (style != null) combo.Style = style;

                foreach (var val in opt.ValidValues)
                    combo.Items.Add(val);

                string current = _coreOptions.TryGetValue(opt.Key, out string? cv) ? cv : opt.DefaultValue;
                combo.SelectedItem = current;
                if (combo.SelectedItem == null && combo.Items.Count > 0)
                    combo.SelectedIndex = 0;

                string capturedKey = opt.Key;
                var capturedSchema = schema;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is not string newVal) return;
                    _coreOptions[capturedKey] = newVal;
                    _coreOptionsDirty = true;
                    // Persist only schema-declared keys to avoid saving internal handler values
                    var schemaKeys = capturedSchema.Options.Select(o => o.Key).ToHashSet();
                    App.CoreOptions.SaveValues(cn, _coreOptions
                        .Where(kv => schemaKeys.Contains(kv.Key))
                        .ToDictionary(kv => kv.Key, kv => kv.Value));
                };

                row.Children.Add(combo);
                CoreOptionRows.Children.Add(row);
            }
        }

        // =========================================================================
        // Window chrome + AR-constrained resize
        // =========================================================================
        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var source = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            source?.AddHook(HwndHook);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                                 ref bool handled)
        {
            const int WM_SIZING      = 0x0214;
            const int WMSZ_LEFT      = 1;
            const int WMSZ_RIGHT     = 2;
            const int WMSZ_TOP       = 3;
            const int WMSZ_BOTTOM    = 6;

            if (msg == WM_SIZING && _displayAr > 0 && WindowState == WindowState.Normal)
            {
                var rect = Marshal.PtrToStructure<RECT>(lParam);

                double chromeH = ActualHeight - GameViewport.ActualHeight;
                int edge = (int)wParam;

                int w     = rect.Right  - rect.Left;
                int gameH = rect.Bottom - rect.Top - (int)Math.Round(chromeH);

                if (edge == WMSZ_TOP || edge == WMSZ_BOTTOM)
                {
                    // Height-led drag: adjust width to maintain AR.
                    int newW = (int)Math.Round(Math.Max(gameH, 60) * _displayAr);
                    rect.Right = rect.Left + Math.Max(newW, 160);
                }
                else
                {
                    // Width-led drag (left, right, or any corner): adjust height to maintain AR.
                    int newGameH = (int)Math.Round(Math.Max(w, 160) / _displayAr);
                    rect.Bottom = rect.Top + (int)Math.Round(chromeH) + Math.Max(newGameH, 60);
                }

                Marshal.StructureToPtr(rect, lParam, false);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else DragMove();
        }

        private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaxBtn_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Second pass: async cleanup finished and called Close() — let WPF proceed.
            if (_closeStarted) return;

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
            _overlayTimer?.Stop();
            _mousePoller?.Stop();
            _audioPlayer?.Stop();

            System.Diagnostics.Trace.WriteLine("EmulatorWindow closing — deferring cleanup to background");

            System.Threading.Tasks.Task.Run(() =>
            {
                // Wait for the emu thread to fully exit.
                // The emu thread now does: SRAM save → UnloadGame → context_destroy → GL release
                // before exiting, so this join covers all of it.
                // Allow up to 10 s for heavy cores (PPSSPP, N64) whose internal threads take time.
                if (!(_emuThread?.Join(10000) ?? true))
                    System.Diagnostics.Trace.WriteLine("WARNING: emu thread did not exit within 10s");

                // retro_deinit — final core teardown.
                // LibretroCore.Dispose() skips retro_unload_game (already called on emu
                // thread) and skips retro_deinit for N64 (called on emu thread with GL
                // context active).  Dispose() handles the post-deinit wait + FreeLibrary.
                try { _core?.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Core dispose: {ex.Message}"); }

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
                if (_hwRenderActive && (_hglrc != IntPtr.Zero || _secondaryCtx != IntPtr.Zero))
                {
                    IntPtr hglrcQ    = _hglrc;         _hglrc        = IntPtr.Zero;
                    IntPtr secCtxQ   = _secondaryCtx;  _secondaryCtx = IntPtr.Zero;
                    IntPtr deferredDll = _core?.DeferredFreeHandle ?? IntPtr.Zero;

                    if (deferredDll != IntPtr.Zero)
                    {
                        // Synchronous path: retro_deinit already ran on emu thread with GL.
                        // Wait for residual driver/GPU-thread callbacks, then delete + free.
                        // PPSSPP's GPU thread self-cleans after retro_unload_game but takes
                        // longer to fully exit than N64/Dolphin (context_destroy is skipped).
                        string dllName = _core != null ? System.IO.Path.GetFileName(_core.CorePath).ToLowerInvariant() : "";
                        int preDeleteMs = dllName.Contains("ppsspp") ? 3000 : 1500;
                        System.Diagnostics.Trace.WriteLine($"GL sync cleanup: waiting {preDeleteMs}ms before wglDeleteContext + FreeLibrary 0x{deferredDll:X}");
                        System.Threading.Thread.Sleep(preDeleteMs);
                        try
                        {
                            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                            if (secCtxQ  != IntPtr.Zero) wglDeleteContext(secCtxQ);
                            if (hglrcQ   != IntPtr.Zero) wglDeleteContext(hglrcQ);
                            System.Diagnostics.Trace.WriteLine("GL sync cleanup: contexts deleted.");
                        }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"GL sync delete: {ex.Message}"); }

                        System.Threading.Thread.Sleep(500);
                        try
                        {
                            NativeMethods.FreeLibrary(deferredDll);
                            System.Diagnostics.Trace.WriteLine($"GL sync cleanup: FreeLibrary 0x{deferredDll:X} done.");
                        }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"GL sync FreeLibrary: {ex.Message}"); }
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

                if (_hdc != IntPtr.Zero && _glHwnd != IntPtr.Zero) { ReleaseDC(_glHwnd, _hdc); _hdc = IntPtr.Zero; }
                // Destroy the offscreen GL window if we created it; HwndHost owns its own window.
                if (_glHwndOwned && _glHwnd != IntPtr.Zero) { DestroyWindow(_glHwnd); _glHwndOwned = false; }
                _glHwnd = IntPtr.Zero;

                try { _controllerManager?.Dispose(); _audioPlayer?.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Service cleanup: {ex.Message}"); }

                if (_systemDirPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_systemDirPtr); _systemDirPtr = IntPtr.Zero; }
                if (_saveDirPtr   != IntPtr.Zero) { Marshal.FreeHGlobal(_saveDirPtr);   _saveDirPtr   = IntPtr.Zero; }

                // Free cached GET_VARIABLE string pointers
                foreach (var ptr in _coreOptionPtrs.Values)
                    if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
                _coreOptionPtrs.Clear();

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

                // Save window size for this console before closing
                Dispatcher.Invoke(SaveWindowSize);

                // Now that all cleanup is done, close the window on the UI thread.
                // Window_Closing will fire again; _closeStarted is true so it returns
                // immediately without cancelling — WPF then destroys the window normally.
                Dispatcher.Invoke(() => Close());
            });
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