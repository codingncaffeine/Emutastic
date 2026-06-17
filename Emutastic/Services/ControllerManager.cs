using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;
using Emutastic.Configuration;
using Microsoft.Extensions.Logging;

namespace Emutastic.Services
{
    public class ControllerManager : IDisposable
    {
        // XInput constants
        private const uint XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE  = 7849;
        private const uint XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE = 8689;
        private const uint XINPUT_GAMEPAD_TRIGGER_THRESHOLD    = 30;

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte   bLeftTrigger;
            public byte   bRightTrigger;
            public short  sThumbLX;
            public short  sThumbLY;
            public short  sThumbRX;
            public short  sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_STATE
        {
            public uint          dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        // Button masks
        private const ushort XINPUT_GAMEPAD_DPAD_UP        = 0x0001;
        private const ushort XINPUT_GAMEPAD_DPAD_DOWN      = 0x0002;
        private const ushort XINPUT_GAMEPAD_DPAD_LEFT      = 0x0004;
        private const ushort XINPUT_GAMEPAD_DPAD_RIGHT     = 0x0008;
        private const ushort XINPUT_GAMEPAD_START          = 0x0010;
        private const ushort XINPUT_GAMEPAD_BACK           = 0x0020;
        private const ushort XINPUT_GAMEPAD_LEFT_THUMB     = 0x0040;
        private const ushort XINPUT_GAMEPAD_RIGHT_THUMB    = 0x0080;
        private const ushort XINPUT_GAMEPAD_LEFT_SHOULDER  = 0x0100;
        private const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
        private const ushort XINPUT_GAMEPAD_A              = 0x1000;
        private const ushort XINPUT_GAMEPAD_B              = 0x2000;
        private const ushort XINPUT_GAMEPAD_X              = 0x4000;
        private const ushort XINPUT_GAMEPAD_Y              = 0x8000;

        private delegate uint XInputGetStateDelegate(uint dwUserIndex, out XINPUT_STATE pState);
        private delegate uint XInputSetStateDelegate(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_VIBRATION
        {
            public ushort wLeftMotorSpeed;
            public ushort wRightMotorSpeed;
        }

        private static XInputGetStateDelegate? _xInputGetState;
        private static XInputSetStateDelegate? _xInputSetState;
        private static bool _xInputInitialized;

        // Serializes XInputGetState calls across the per-player PollController
        // timer thread(s) and any off-thread enumeration via
        // GetConnectedControllers / PreferencesCache.GetControllerDevicesAsync.
        // XInput is not documented as thread-safe; we previously relied on every
        // call running on the dispatcher.
        private static readonly object _xInputLock = new();

        private readonly Timer _pollTimer;
        private bool[] _buttonStates     = new bool[16];
        private bool[] _prevButtonStates = new bool[16];
        private bool _isConnected = false;
        private volatile InputConfiguration? _inputConfig;
        private readonly IConfigurationService _configService;
        private readonly ILogger<ControllerManager>? _logger;
        private readonly string _consoleName;
        private readonly uint _playerNumber; // 0-based player/port index
        private volatile int _xInputIndex;   // which XInput slot to poll (can be overridden by config)

        // Raw XInput wButtons bitmask from the most recent poll. Bypasses the per-
        // console mapping table so callers (e.g. frontend chords like Disk Swap)
        // can read physical button state without depending on whether the user has
        // mapped it for this game.
        private volatile ushort _lastRawButtons;

        /// <summary>
        /// Returns true if the given raw XInput button bit (e.g. XINPUT_GAMEPAD_LEFT_THUMB
        /// = 0x0040) is currently held. Snapshot from the most recent XInput poll.
        /// </summary>
        public bool IsRawXInputButtonDown(ushort mask) => (_lastRawButtons & mask) != 0;

        // Public aliases of the raw XInput masks for frontend chord / couch-nav
        // detection (use with IsRawXInputButtonDown; bypasses per-console mapping).
        public const ushort RAW_A          = XINPUT_GAMEPAD_A;
        public const ushort RAW_B          = XINPUT_GAMEPAD_B;
        public const ushort RAW_BACK       = XINPUT_GAMEPAD_BACK;
        public const ushort RAW_START      = XINPUT_GAMEPAD_START;
        public const ushort RAW_DPAD_UP    = XINPUT_GAMEPAD_DPAD_UP;
        public const ushort RAW_DPAD_DOWN  = XINPUT_GAMEPAD_DPAD_DOWN;
        public const ushort RAW_DPAD_LEFT  = XINPUT_GAMEPAD_DPAD_LEFT;
        public const ushort RAW_DPAD_RIGHT = XINPUT_GAMEPAD_DPAD_RIGHT;

        /// <summary>
        /// Returns true if the named trigger (false = left/L2, true = right/R2) is
        /// held past the XInput activation threshold. Snapshot from the most recent
        /// poll; bypasses the mapping table so frontend chords (e.g. the TV-mode
        /// launch combo) can read physical trigger state directly.
        /// </summary>
        public bool IsRawTriggerDown(bool rightTrigger) =>
            (rightTrigger ? _rightTrigger : _leftTrigger) > XINPUT_GAMEPAD_TRIGGER_THRESHOLD;

        /// <summary>
        /// True while the EmuTV launch chord — L3 + R3 + L2 + R2 (both stick clicks
        /// plus both triggers) — is physically held. Reads the raw poll snapshot,
        /// ignoring per-console button mappings. The caller owns the hold-duration
        /// debounce. Chosen to avoid colliding with Windows / Xbox Game Bar gestures
        /// that grab Select / Start / Guide.
        /// </summary>
        public bool IsTvModeChordHeld =>
            IsRawTriggerDown(false) && IsRawTriggerDown(true) &&
            (_lastRawButtons & XINPUT_GAMEPAD_LEFT_THUMB)  != 0 &&
            (_lastRawButtons & XINPUT_GAMEPAD_RIGHT_THUMB) != 0;

        public event Action<uint, bool>? ButtonChanged;

        /// <summary>
        /// Fires when the controller's connection state changes — true when the
        /// XInput slot starts responding (controller plugged in or powered on),
        /// false when it stops (unplugged, battery died, USB reset). Raised on
        /// the polling Timer thread, so subscribers that touch UI state must
        /// marshal to the Dispatcher themselves.
        /// </summary>
        public event Action<bool>? ConnectionChanged;

        // -------------------------------------------------------------------------
        // Raw analog axis storage
        //
        // These hold the raw XInput thumb values (-32768..32767) after deadzone
        // clamping.  They are read on the poll thread and consumed on the emulation
        // thread; both accesses are reads/writes of 16-bit values which are atomic
        // on x86/x64, so no lock is needed.
        //
        // Libretro Y-axis convention: up = NEGATIVE.
        // XInput Y-axis convention:   up = POSITIVE (sThumbLY > 0 when pushing up).
        // We store raw XInput values here and negate Y at the call site in
        // EmulatorWindow.OnInputState so that every consumer gets correct values.
        // -------------------------------------------------------------------------
        private volatile short _leftStickX;   // raw XInput, -32768..32767
        private volatile short _leftStickY;   // raw XInput, up=positive
        private volatile short _rightStickX;
        private volatile short _rightStickY;

        // Trigger axes — stored as 0..255, exposed as 0..32767
        private volatile byte _leftTrigger;
        private volatile byte _rightTrigger;

        // Deadzone applied before storing; below this the axis reads zero
        private readonly int _analogDeadzone = 8000;

        // Extended button IDs for analog directions (beyond standard 16 buttons).
        // Aliased to LibretroInput so callers like PreferencesWindow that reference
        // ControllerManager.ANALOG_LEFT_UP keep compiling without churn — the canonical
        // values live in LibretroInput.
        public const uint ANALOG_LEFT_UP     = LibretroInput.ANALOG_LEFT_UP;
        public const uint ANALOG_LEFT_DOWN   = LibretroInput.ANALOG_LEFT_DOWN;
        public const uint ANALOG_LEFT_LEFT   = LibretroInput.ANALOG_LEFT_LEFT;
        public const uint ANALOG_LEFT_RIGHT  = LibretroInput.ANALOG_LEFT_RIGHT;
        public const uint ANALOG_RIGHT_UP    = LibretroInput.ANALOG_RIGHT_UP;
        public const uint ANALOG_RIGHT_DOWN  = LibretroInput.ANALOG_RIGHT_DOWN;
        public const uint ANALOG_RIGHT_LEFT  = LibretroInput.ANALOG_RIGHT_LEFT;
        public const uint ANALOG_RIGHT_RIGHT = LibretroInput.ANALOG_RIGHT_RIGHT;

        // -------------------------------------------------------------------------
        // Static initialiser — load XInput DLL once for the process lifetime
        // -------------------------------------------------------------------------
        static ControllerManager()
        {
            InitializeXInput();
        }

        private static void InitializeXInput()
        {
            // Try XInput 1.4 first (Windows 8+)
            try
            {
                var xinput14 = LoadLibrary("xinput1_4.dll");
                if (xinput14 != IntPtr.Zero)
                {
                    var getAddr = GetProcAddress(xinput14, "XInputGetState");
                    var setAddr = GetProcAddress(xinput14, "XInputSetState");
                    if (getAddr != IntPtr.Zero && setAddr != IntPtr.Zero)
                    {
                        _xInputGetState    = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(getAddr);
                        _xInputSetState    = Marshal.GetDelegateForFunctionPointer<XInputSetStateDelegate>(setAddr);
                        _xInputInitialized = true;
                        return;
                    }
                }
            }
            catch { }

            // Fall back to XInput 1.3
            try
            {
                var xinput13 = LoadLibrary("xinput1_3.dll");
                if (xinput13 != IntPtr.Zero)
                {
                    var getAddr = GetProcAddress(xinput13, "XInputGetState");
                    var setAddr = GetProcAddress(xinput13, "XInputSetState");
                    if (getAddr != IntPtr.Zero && setAddr != IntPtr.Zero)
                    {
                        _xInputGetState    = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(getAddr);
                        _xInputSetState    = Marshal.GetDelegateForFunctionPointer<XInputSetStateDelegate>(setAddr);
                        _xInputInitialized = true;
                        return;
                    }
                }
            }
            catch { }

            _xInputInitialized = false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        // -------------------------------------------------------------------------
        // Constructors
        // -------------------------------------------------------------------------
        public ControllerManager(IConfigurationService configService, ILogger<ControllerManager>? logger = null, string consoleName = "NES", uint playerNumber = 0)
        {
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _logger        = logger;
            _consoleName   = consoleName;
            _playerNumber  = playerNumber;
            _xInputIndex   = (int)playerNumber; // default: player N polls XInput slot N

            LoadInputConfiguration();

            if (!_xInputInitialized)
                _logger?.LogWarning("XInput not available — controller support disabled");

            _pollTimer = new Timer(PollController, null, 0, 16); // ~60 Hz
        }

        public ControllerManager() : this(App.Configuration ?? throw new InvalidOperationException("Configuration not initialized"))
        {
        }

        public ControllerManager(DatabaseService db, string consoleName = "NES")
            : this(App.Configuration ?? throw new InvalidOperationException("Configuration not initialized"), null, consoleName)
        {
            MigrateDatabaseMappings(db);
        }

        // -------------------------------------------------------------------------
        // Configuration helpers
        // -------------------------------------------------------------------------
        public void ReloadInputConfiguration() => LoadInputConfiguration();

        private void LoadInputConfiguration()
        {
            try
            {
                // Preferences saves per-player keys as "{Console}_P{N}"; load mappings for this player.
                var playerKey = $"{_consoleName}_P{_playerNumber + 1}";
                var playerConfig = _configService.GetInputConfiguration(playerKey);
                _inputConfig = playerConfig.ControllerMappings.Count > 0
                    ? playerConfig
                    : _playerNumber == 0
                        ? _configService.GetInputConfiguration(_consoleName) // fallback for legacy P1 saves
                        : new InputConfiguration { ConsoleName = _consoleName };

                // Apply user-assigned controller slot if configured (-1 = use default)
                if (_inputConfig.ControllerSlot >= 0 && _inputConfig.ControllerSlot <= 3)
                    _xInputIndex = _inputConfig.ControllerSlot;
                else
                    _xInputIndex = (int)_playerNumber;
                _logger?.LogInformation($"Loaded input config for {_consoleName}: {_inputConfig.ControllerMappings.Count} mappings");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Failed to load input config for {_consoleName}");
                _inputConfig = new InputConfiguration { ConsoleName = _consoleName };
            }
        }

        private void MigrateDatabaseMappings(DatabaseService db)
        {
            try
            {
                var dbMappings = db.GetInputMappings()
                    .Where(m => m.ConsoleName == _consoleName && m.InputType == InputType.Controller)
                    .ToList();

                if (!dbMappings.Any()) return;

                _inputConfig = _configService.GetInputConfiguration(_consoleName);
                foreach (var dbMapping in dbMappings)
                {
                    _inputConfig.ControllerMappings.Add(new ButtonMapping
                    {
                        ButtonName      = dbMapping.ButtonName,
                        InputIdentifier = dbMapping.ControllerButtonId.ToString(),
                        InputType       = dbMapping.InputType == Services.InputType.Keyboard
                            ? Configuration.InputType.Keyboard
                            : Configuration.InputType.Controller,
                        DisplayName = dbMapping.DisplayText
                    });
                }

                _configService.SetInputConfiguration(_consoleName, _inputConfig);
                _configService.SaveAsync().Wait();
                _logger?.LogInformation($"Migrated {dbMappings.Count} controller mappings from database for {_consoleName}");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Failed to migrate database mappings for {_consoleName}");
            }
        }

        // -------------------------------------------------------------------------
        // Poll
        // -------------------------------------------------------------------------
        private void PollController(object? state)
        {
            if (!_xInputInitialized || _xInputGetState == null)
            {
                _isConnected = false;
                return;
            }

            try
            {
                uint result;
                XINPUT_STATE xinputState;
                lock (_xInputLock)
                {
                    result = _xInputGetState((uint)_xInputIndex, out xinputState);
                }
                bool wasConnected = _isConnected;
                _isConnected      = result == 0;

                if (!_isConnected)
                {
                    if (wasConnected)
                    {
                        Array.Clear(_buttonStates, 0, _buttonStates.Length);
                        Array.Clear(_prevButtonStates, 0, _prevButtonStates.Length);
                        _leftStickX = _leftStickY = _rightStickX = _rightStickY = 0;
                        _leftTrigger = _rightTrigger = 0;
                        _logger?.LogInformation("Controller disconnected");
                        try { ConnectionChanged?.Invoke(false); } catch { }
                    }
                    return;
                }

                if (!wasConnected)
                {
                    _logger?.LogInformation("Controller connected");
                    try { ConnectionChanged?.Invoke(true); } catch { }
                }

                var gamepad          = xinputState.Gamepad;
                _lastRawButtons      = gamepad.wButtons;
                _prevButtonStates    = (bool[])_buttonStates.Clone();
                Array.Clear(_buttonStates, 0, _buttonStates.Length);

                // ------------------------------------------------------------------
                // Digital buttons
                // ------------------------------------------------------------------
                if (!RawMode && _inputConfig?.ControllerMappings != null && _inputConfig.ControllerMappings.Count > 0)
                {
                    foreach (var mapping in _inputConfig.ControllerMappings)
                    {
                        if (!uint.TryParse(mapping.InputIdentifier, out var controllerButtonId)) continue;
                        uint libretroId = LibretroInput.GetButtonId(mapping.ButtonName, _consoleName);
                        if (libretroId < 16 && controllerButtonId < 16)
                            _buttonStates[libretroId] = IsXboxButtonPressed(gamepad.wButtons, controllerButtonId);
                        else if (libretroId == uint.MaxValue)
                        {
                            // A saved binding whose name the translator doesn't know
                            // is a BUG (definition/translator drift — NeoGeo, CDi and
                            // NDS "Tap Stylus" have all hit this). Surface it instead
                            // of silently ignoring the user's binding. Once per name.
                            if (_unknownButtonNamesLogged.Add($"{_consoleName}:{mapping.ButtonName}"))
                                CtrlLog($"UNKNOWN BUTTON NAME '{mapping.ButtonName}' (console={_consoleName}) — binding ignored! LibretroInput.GetButtonId needs a case for it.");
                        }
                    }
                }
                else
                {
                    // Default mapping
                    _buttonStates[0]  = (gamepad.wButtons & XINPUT_GAMEPAD_B) != 0;
                    _buttonStates[1]  = (gamepad.wButtons & XINPUT_GAMEPAD_Y) != 0;
                    _buttonStates[2]  = (gamepad.wButtons & XINPUT_GAMEPAD_BACK) != 0;
                    _buttonStates[3]  = (gamepad.wButtons & XINPUT_GAMEPAD_START) != 0;
                    _buttonStates[4]  = (gamepad.wButtons & XINPUT_GAMEPAD_DPAD_UP) != 0;
                    _buttonStates[5]  = (gamepad.wButtons & XINPUT_GAMEPAD_DPAD_DOWN) != 0;
                    _buttonStates[6]  = (gamepad.wButtons & XINPUT_GAMEPAD_DPAD_LEFT) != 0;
                    _buttonStates[7]  = (gamepad.wButtons & XINPUT_GAMEPAD_DPAD_RIGHT) != 0;
                    _buttonStates[8]  = (gamepad.wButtons & XINPUT_GAMEPAD_A) != 0;
                    _buttonStates[9]  = (gamepad.wButtons & XINPUT_GAMEPAD_X) != 0;
                    _buttonStates[10] = (gamepad.wButtons & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0;
                    _buttonStates[11] = (gamepad.wButtons & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0;
                    // L2/R2 — map triggers to digital (threshold = 128)
                    _buttonStates[12] = gamepad.bLeftTrigger  > 128;
                    _buttonStates[13] = gamepad.bRightTrigger > 128;
                    _buttonStates[14] = (gamepad.wButtons & XINPUT_GAMEPAD_LEFT_THUMB) != 0;
                    _buttonStates[15] = (gamepad.wButtons & XINPUT_GAMEPAD_RIGHT_THUMB) != 0;
                }

                if (_buttonStates[0] != _prevButtonStates[0] || _buttonStates[8] != _prevButtonStates[8])
                    _logger?.LogDebug($"ControllerManager: Xbox A={_buttonStates[8]}, B={_buttonStates[0]}, Raw={gamepad.wButtons:X4}");

                // ------------------------------------------------------------------
                // Raw analog axes — apply deadzone, store raw values.
                // Callers that need smooth axis data use GetAnalogAxisValue().
                // Callers that need on/off thresholds use GetButtonState() with
                // the ANALOG_* constants, which are derived from these raw values.
                // ------------------------------------------------------------------
                _leftStickX  = ApplyDeadzone(gamepad.sThumbLX, (short)_analogDeadzone);
                _leftStickY  = ApplyDeadzone(gamepad.sThumbLY, (short)_analogDeadzone);
                _rightStickX = ApplyDeadzone(gamepad.sThumbRX, (short)_analogDeadzone);
                _rightStickY = ApplyDeadzone(gamepad.sThumbRY, (short)_analogDeadzone);

                _leftTrigger  = gamepad.bLeftTrigger;
                _rightTrigger = gamepad.bRightTrigger;

                // Analog direction booleans (for GetButtonState with ANALOG_* ids)
                bool leftUp    = _leftStickY  >  _analogDeadzone;
                bool leftDown  = _leftStickY  < -_analogDeadzone;
                bool leftLeft  = _leftStickX  < -_analogDeadzone;
                bool leftRight = _leftStickX  >  _analogDeadzone;
                bool rightUp   = _rightStickY >  _analogDeadzone;
                bool rightDown = _rightStickY < -_analogDeadzone;
                bool rightLeft = _rightStickX < -_analogDeadzone;
                bool rightRight= _rightStickX >  _analogDeadzone;

                // ------------------------------------------------------------------
                // Fire digital button events
                // ------------------------------------------------------------------
                for (int i = 0; i < _buttonStates.Length; i++)
                {
                    if (_buttonStates[i] != _prevButtonStates[i])
                        ButtonChanged?.Invoke((uint)i, _buttonStates[i]);
                }

                // Analog direction events
                bool prevLeftUp    = _prevLeftStickY >  _analogDeadzone;
                bool prevLeftDown  = _prevLeftStickY < -_analogDeadzone;
                bool prevLeftLeft  = _prevLeftStickX < -_analogDeadzone;
                bool prevLeftRight = _prevLeftStickX >  _analogDeadzone;
                bool prevRightUp   = _prevRightStickY >  _analogDeadzone;
                bool prevRightDown = _prevRightStickY < -_analogDeadzone;
                bool prevRightLeft = _prevRightStickX < -_analogDeadzone;
                bool prevRightRight= _prevRightStickX >  _analogDeadzone;

                if (leftUp    != prevLeftUp)    ButtonChanged?.Invoke(ANALOG_LEFT_UP,    leftUp);
                if (leftDown  != prevLeftDown)  ButtonChanged?.Invoke(ANALOG_LEFT_DOWN,  leftDown);
                if (leftLeft  != prevLeftLeft)  ButtonChanged?.Invoke(ANALOG_LEFT_LEFT,  leftLeft);
                if (leftRight != prevLeftRight) ButtonChanged?.Invoke(ANALOG_LEFT_RIGHT, leftRight);
                if (rightUp   != prevRightUp)   ButtonChanged?.Invoke(ANALOG_RIGHT_UP,   rightUp);
                if (rightDown != prevRightDown) ButtonChanged?.Invoke(ANALOG_RIGHT_DOWN, rightDown);
                if (rightLeft != prevRightLeft) ButtonChanged?.Invoke(ANALOG_RIGHT_LEFT, rightLeft);
                if (rightRight!= prevRightRight)ButtonChanged?.Invoke(ANALOG_RIGHT_RIGHT,rightRight);

                // Advance prev-frame stick values so edge detection fires correctly on release.
                _prevLeftStickX  = _leftStickX;
                _prevLeftStickY  = _leftStickY;
                _prevRightStickX = _rightStickX;
                _prevRightStickY = _rightStickY;
            }
            catch
            {
                _isConnected = false;
            }
        }

        // Prev-frame values needed for analog direction edge detection
        private short _prevLeftStickX, _prevLeftStickY, _prevRightStickX, _prevRightStickY;

        private static short ApplyDeadzone(short value, short deadzone)
        {
            if (value > -deadzone && value < deadzone) return 0;
            if (value == short.MinValue) return -short.MaxValue;
            return value;
        }

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Returns the state of a digital button (0-15) or an analog direction
        /// threshold (ANALOG_* constants 16-23).
        /// </summary>
        public bool GetButtonState(uint button)
        {
            if (button < (uint)_buttonStates.Length)
                return _buttonStates[button];

            // Analog direction thresholds
            return button switch
            {
                ANALOG_LEFT_UP    => _leftStickY  >  _analogDeadzone,
                ANALOG_LEFT_DOWN  => _leftStickY  < -_analogDeadzone,
                ANALOG_LEFT_LEFT  => _leftStickX  < -_analogDeadzone,
                ANALOG_LEFT_RIGHT => _leftStickX  >  _analogDeadzone,
                ANALOG_RIGHT_UP   => _rightStickY >  _analogDeadzone,
                ANALOG_RIGHT_DOWN => _rightStickY < -_analogDeadzone,
                ANALOG_RIGHT_LEFT => _rightStickX < -_analogDeadzone,
                ANALOG_RIGHT_RIGHT=> _rightStickX >  _analogDeadzone,
                _ => false
            };
        }

        /// <summary>
        /// Returns the raw analog axis value in the range -32768..32767.
        ///
        /// Parameters follow the libretro RETRO_DEVICE_ANALOG convention:
        ///   stickIndex — 0 = left stick, 1 = right stick
        ///   axisId     — 0 = X axis, 1 = Y axis
        ///
        /// IMPORTANT: Y values are returned in XInput convention (up = positive).
        /// The caller (OnInputState) must negate Y before passing to the core
        /// because libretro uses the opposite convention (up = negative).
        ///
        /// Returns 0 when the controller is disconnected or below deadzone.
        /// </summary>
        public short GetAnalogAxisValue(uint stickIndex, uint axisId)
        {
            if (!_isConnected) return 0;

            return (stickIndex, axisId) switch
            {
                (0, 0) => _leftStickX,
                (0, 1) => _leftStickY,
                (1, 0) => _rightStickX,
                (1, 1) => _rightStickY,
                _      => 0
            };
        }

        /// <summary>
        /// Returns left trigger (0) or right trigger (1) as a libretro axis value
        /// (0..32767).  Triggers have no negative range.
        /// </summary>
        public short GetTriggerValue(uint triggerIndex)
        {
            if (!_isConnected) return 0;
            byte raw = triggerIndex == 0 ? _leftTrigger : _rightTrigger;
            return (short)((raw / 255.0f) * 32767);
        }

        public bool IsConnected => _isConnected;

        /// <summary>
        /// When true the polling loop ignores stored input mappings and always
        /// fires raw physical button IDs (0-15). Used by PreferencesWindow while
        /// capturing new mappings so unmapped buttons still produce events.
        /// </summary>
        public bool RawMode { get; set; } = false;

        public void SetVibration(ushort leftSpeed, ushort rightSpeed)
        {
            if (!_isConnected || _xInputSetState == null) return;
            try
            {
                var vib = new XINPUT_VIBRATION { wLeftMotorSpeed = leftSpeed, wRightMotorSpeed = rightSpeed };
                lock (_xInputLock)
                {
                    _xInputSetState((uint)_xInputIndex, ref vib);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _pollTimer?.Dispose();
        }

        // GetLibretroButtonId moved to Services/LibretroInput.GetButtonId.

        private bool IsXboxButtonPressed(ushort wButtons, uint controllerButtonId) =>
            controllerButtonId switch
            {
                0  => (wButtons & XINPUT_GAMEPAD_B) != 0,
                1  => (wButtons & XINPUT_GAMEPAD_Y) != 0,
                2  => (wButtons & XINPUT_GAMEPAD_BACK) != 0,
                3  => (wButtons & XINPUT_GAMEPAD_START) != 0,
                4  => (wButtons & XINPUT_GAMEPAD_DPAD_UP) != 0,
                5  => (wButtons & XINPUT_GAMEPAD_DPAD_DOWN) != 0,
                6  => (wButtons & XINPUT_GAMEPAD_DPAD_LEFT) != 0,
                7  => (wButtons & XINPUT_GAMEPAD_DPAD_RIGHT) != 0,
                8  => (wButtons & XINPUT_GAMEPAD_A) != 0,
                9  => (wButtons & XINPUT_GAMEPAD_X) != 0,
                10 => (wButtons & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0,
                11 => (wButtons & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0,
                12 => _leftTrigger  > 64,
                13 => _rightTrigger > 64,
                14 => (wButtons & XINPUT_GAMEPAD_LEFT_THUMB) != 0,
                15 => (wButtons & XINPUT_GAMEPAD_RIGHT_THUMB) != 0,
                _  => false
            };

        // ── SDL3 P/Invoke for controller name enumeration ────────────────────
        // SDL3 has a huge built-in controller database and handles USB, Bluetooth,
        // and Xbox wireless correctly — the right tool for getting real device names.
        // SDL3 changed from index-based to instance-ID-based joystick enumeration.

        private const string SDL3Dll = "SDL3.dll";
        private const uint SDL_INIT_JOYSTICK = 0x00000200u;
        private const uint SDL_INIT_GAMEPAD  = 0x00002000u;  // was SDL_INIT_GAMECONTROLLER

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_Init(uint flags);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint SDL_WasInit(uint flags);

        // Returns malloc'd array of SDL_JoystickID (uint32); caller must SDL_free
        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetJoysticks(out int count);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern bool SDL_IsGamepad(uint instance_id);

        // Returns pointer to UTF-8 string owned by SDL — do NOT free it
        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetGamepadNameForID(uint instance_id);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetJoystickNameForID(uint instance_id);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_free(IntPtr mem);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_PumpEvents();

        private static volatile bool _sdl3Available;

        private static void InitSdl3()
        {
            if (_sdl3Available) return;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                uint needed = SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD;
                if ((SDL_WasInit(needed) & needed) != needed)
                    SDL_Init(needed);
                _sdl3Available = true;
                sw.Stop();
                CtrlLog($"SDL3 InitSdl3 completed in {sw.ElapsedMilliseconds}ms (ready)");
            }
            catch (DllNotFoundException ex)
            {
                CtrlLog($"SDL3 init failed — DLL not found: {ex.Message}");
            }
            catch (Exception ex)
            {
                CtrlLog($"SDL3 init failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Dedicated SDL thread: SDL3 hot-plug requires WM_DEVICECHANGE on the
        // thread that called SDL_Init. We give SDL its own thread with its
        // own WPF dispatcher (= message loop), keeping the WPF UI dispatcher
        // free of any SDL cost. Init can take 10 s on some Windows systems
        // (Bluetooth / USB enumeration), but the UI never blocks.
        private static System.Windows.Threading.Dispatcher? _sdlDispatcher;
        private static readonly object _sdlThreadStartLock = new();
        private static bool _sdlThreadStarted;

        /// <summary>
        /// Spin up the dedicated SDL3 thread (with its own dispatcher /
        /// message loop), run SDL_Init on it, then leave the dispatcher
        /// pumping forever so SDL3 receives WM_DEVICECHANGE for hot-plug.
        /// Returns immediately; init runs in the background. Safe to call
        /// repeatedly — second + later calls no-op.
        /// </summary>
        public static void EnsureSdl3InitInBackground()
        {
            if (_sdl3Available || _sdlThreadStarted) return;
            lock (_sdlThreadStartLock)
            {
                if (_sdl3Available || _sdlThreadStarted) return;
                _sdlThreadStarted = true;

                var ready = new System.Threading.ManualResetEventSlim(false);
                var thread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        _sdlDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                        ready.Set();              // dispatcher captured (instant)
                        InitSdl3();               // 10 s on slow machines; on THIS thread only

                        // Once SDL is up, run a periodic refresh of the device-name
                        // cache on THIS thread (not the UI thread). SDL_PumpEvents
                        // can stall for 10–20 seconds when a complex controller hot-plugs
                        // (e.g. Xbox Elite 2 fires 3 WM_DEVICECHANGE events and each
                        // takes ~18 s to process with Steam Input active). Doing it
                        // here keeps the UI thread free; the UI just reads _cachedDevices.
                        if (_sdl3Available)
                        {
                            // Initial snapshot so the cache isn't empty before the first tick.
                            try { RefreshDevicesCacheOnSdlThread(); } catch { }

                            var poll = new System.Windows.Threading.DispatcherTimer(
                                System.Windows.Threading.DispatcherPriority.Background)
                            {
                                Interval = TimeSpan.FromSeconds(1)
                            };
                            poll.Tick += (_, _) =>
                            {
                                try { RefreshDevicesCacheOnSdlThread(); } catch { }
                            };
                            poll.Start();
                            CtrlLog("SDL thread periodic device-cache refresh timer started (1 s)");
                        }

                        System.Windows.Threading.Dispatcher.Run(); // pump WM_DEVICECHANGE + run the timer
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"[SDL thread] exited with {ex}");
                        CtrlLog($"SDL thread exited with: {ex.GetType().Name}: {ex.Message}");
                        ready.Set();
                    }
                })
                {
                    IsBackground = true,
                    Name = "SDL3 hot-plug dispatcher",
                };
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
                ready.Wait();                     // wait only for dispatcher capture, not SDL_Init
            }
        }

        // Thread-safe cache of the most recent SDL device name list, refreshed
        // on the SDL thread itself by the periodic timer above. UI thread reads
        // this cache via GetConnectedControllers — instant, no Invoke, no block.
        private static readonly object _cachedDevicesLock = new();
        private static List<string> _cachedDevices = new();
        private static DateTime _cachedDevicesAt = DateTime.MinValue;

        /// <summary>
        /// Runs ON the SDL thread (inside the periodic DispatcherTimer). Pumps
        /// WM_DEVICECHANGE events, enumerates joysticks, and updates the cache.
        /// Slow pumps (10–20 s for Elite 2 + Steam Input) stay on this thread;
        /// the UI thread never sees them.
        /// </summary>
        private static void RefreshDevicesCacheOnSdlThread()
        {
            var fresh = EnumerateOnSdlThread();
            lock (_cachedDevicesLock)
            {
                _cachedDevices   = fresh;
                _cachedDevicesAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// True once SDL3 has finished initializing on the dedicated thread.
        /// While false, enumeration falls back to XInput (instant, generic
        /// names). Once true, callers marshal SDL calls to <see cref="_sdlDispatcher"/>.
        /// </summary>
        public static bool IsSdl3Ready => _sdl3Available;

        /// <summary>
        /// Cleanly shut down the SDL3 dispatcher thread on app exit so it
        /// isn't terminated abruptly mid-message-pump (which can leave HID
        /// hidden-window handles dangling). Safe to call from app shutdown.
        /// </summary>
        public static void ShutdownSdl3Thread()
        {
            try { _sdlDispatcher?.InvokeShutdown(); } catch { }
        }

        private static string? Utf8PtrToString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            if (len == 0) return null;
            byte[] bytes = new byte[len];
            Marshal.Copy(ptr, bytes, 0, len);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Returns a display name for each connected controller.
        /// Uses SDL3's controller database for real names (Xbox, DualSense, Logitech, etc.)
        /// across USB, Bluetooth and Xbox wireless. Falls back to XInput slot count
        /// with generic labels if SDL3.dll is not present.
        /// </summary>
        // Dedicated controller-diagnostic log file at a hardcoded path next to
        // the running exe. Direct File.AppendAllText — no TraceListener
        // indirection, nothing to misconfigure or fail silently.
        private static readonly string _ctrlDiagLogPath =
            System.IO.Path.Combine(AppContext.BaseDirectory, "controller-diag.log");
        private static readonly object _ctrlDiagLogLock = new();

        // De-dupes the unknown-button-name diagnostic (logged once per
        // console+name pair, not once per poll tick at 60Hz).
        private static readonly System.Collections.Generic.HashSet<string> _unknownButtonNamesLogged = new();
        private static void CtrlLog(string msg)
        {
            try
            {
                lock (_ctrlDiagLogLock)
                {
                    LogRotation.RotateIfLarge(_ctrlDiagLogPath);
                    System.IO.File.AppendAllText(_ctrlDiagLogPath,
                        $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
                }
            }
            catch { /* never throw from logging */ }
        }

        public static List<string> GetConnectedControllers()
        {
            // SDL3 is up: return the cache that the SDL thread is keeping fresh
            // (refresh timer ticks every 1 s on that thread). This is a copy of
            // a small list under a tiny lock — instant, never blocks the UI even
            // when SDL_PumpEvents is mid-stall during a hot-plug event.
            if (_sdl3Available)
            {
                lock (_cachedDevicesLock)
                {
                    return new List<string>(_cachedDevices);
                }
            }
            // SDL not initialised yet: fall back to a synchronous XInput poll.
            // Generic "Controller N" names, valid until SDL warms up and the
            // next 1-second cache refresh upgrades them to friendly names.
            return EnumerateXInputFallback();
        }

        private static List<string> EnumerateOnSdlThread()
        {
            var result = new List<string>();
            try
            {
                if (_sdl3Available)
                {
                    var swPump = System.Diagnostics.Stopwatch.StartNew();
                    SDL_PumpEvents();
                    swPump.Stop();

                    var swEnum = System.Diagnostics.Stopwatch.StartNew();
                    IntPtr arr = SDL_GetJoysticks(out int count);
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            uint id = (uint)Marshal.ReadInt32(arr, i * 4);
                            IntPtr namePtr = SDL_IsGamepad(id)
                                ? SDL_GetGamepadNameForID(id)
                                : SDL_GetJoystickNameForID(id);

                            string name = Utf8PtrToString(namePtr)
                                ?? $"Controller {i + 1}";
                            result.Add(name);
                        }
                    }
                    finally
                    {
                        if (arr != IntPtr.Zero) SDL_free(arr);
                    }
                    swEnum.Stop();

                    if (swPump.ElapsedMilliseconds + swEnum.ElapsedMilliseconds > 50)
                        CtrlLog($"EnumerateOnSdlThread pump={swPump.ElapsedMilliseconds}ms enum={swEnum.ElapsedMilliseconds}ms count={result.Count} names=[{string.Join(", ", result)}]");
                }
            }
            catch (Exception ex)
            {
                CtrlLog($"EnumerateOnSdlThread THREW: {ex.GetType().Name}: {ex.Message}");
            }
            return result;
        }

        // XInput-only fallback. Used until the SDL thread finishes initializing
        // (or if SDL is unavailable). Returns generic names because XInput
        // doesn't expose device-specific friendly names.
        private static List<string> EnumerateXInputFallback()
        {
            var result = new List<string>();
            if (!_xInputInitialized || _xInputGetState == null)
                return result;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (uint slot = 0; slot < 4; slot++)
            {
                try
                {
                    uint code;
                    lock (_xInputLock)
                    {
                        code = _xInputGetState(slot, out XINPUT_STATE _);
                    }
                    if (code != 0) continue;
                    result.Add($"Controller {slot + 1}");
                }
                catch { }
            }
            sw.Stop();
            if (sw.ElapsedMilliseconds > 25)
                CtrlLog($"EnumerateXInputFallback took {sw.ElapsedMilliseconds}ms count={result.Count} — XInput lock contention?");
            return result;
        }
    }
}