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
        private static bool _useXInput14;

        private readonly Timer _pollTimer;
        private bool[] _buttonStates     = new bool[16];
        private bool[] _prevButtonStates = new bool[16];
        private bool _isConnected = false;
        private InputConfiguration? _inputConfig;
        private readonly IConfigurationService _configService;
        private readonly ILogger<ControllerManager>? _logger;
        private readonly string _consoleName;

        public event Action<uint, bool>? ButtonChanged;

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

        // Extended button IDs for analog directions (beyond standard 16 buttons)
        public const uint ANALOG_LEFT_UP    = 16;
        public const uint ANALOG_LEFT_DOWN  = 17;
        public const uint ANALOG_LEFT_LEFT  = 18;
        public const uint ANALOG_LEFT_RIGHT = 19;
        public const uint ANALOG_RIGHT_UP   = 20;
        public const uint ANALOG_RIGHT_DOWN = 21;
        public const uint ANALOG_RIGHT_LEFT = 22;
        public const uint ANALOG_RIGHT_RIGHT= 23;

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
                        _useXInput14       = true;
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
                        _useXInput14       = false;
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
        public ControllerManager(IConfigurationService configService, ILogger<ControllerManager>? logger = null, string consoleName = "NES")
        {
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _logger        = logger;
            _consoleName   = consoleName;

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
        private void LoadInputConfiguration()
        {
            try
            {
                // Preferences saves per-player keys as "{Console}_P{N}"; load P1 mappings.
                var p1Key = $"{_consoleName}_P1";
                var p1Config = _configService.GetInputConfiguration(p1Key);
                _inputConfig = p1Config.ControllerMappings.Count > 0
                    ? p1Config
                    : _configService.GetInputConfiguration(_consoleName); // fallback for legacy saves
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
                var result        = _xInputGetState(0, out XINPUT_STATE xinputState);
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
                    }
                    return;
                }

                if (!wasConnected) _logger?.LogInformation("Controller connected");

                var gamepad          = xinputState.Gamepad;
                _prevButtonStates    = (bool[])_buttonStates.Clone();
                Array.Clear(_buttonStates, 0, _buttonStates.Length);

                // ------------------------------------------------------------------
                // Digital buttons
                // ------------------------------------------------------------------
                if (_inputConfig?.ControllerMappings != null && _inputConfig.ControllerMappings.Count > 0)
                {
                    foreach (var mapping in _inputConfig.ControllerMappings)
                    {
                        if (!uint.TryParse(mapping.InputIdentifier, out var controllerButtonId)) continue;
                        uint libretroId = GetLibretroButtonId(mapping.ButtonName, _consoleName);
                        if (libretroId < 16 && controllerButtonId < 16)
                            _buttonStates[libretroId] = IsXboxButtonPressed(gamepad.wButtons, controllerButtonId);
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

        public void SetVibration(ushort leftSpeed, ushort rightSpeed)
        {
            if (!_isConnected || _xInputSetState == null) return;
            try
            {
                var vib = new XINPUT_VIBRATION { wLeftMotorSpeed = leftSpeed, wRightMotorSpeed = rightSpeed };
                _xInputSetState(0, ref vib);
            }
            catch { }
        }

        public void Dispose()
        {
            _pollTimer?.Dispose();
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------
        private uint GetLibretroButtonId(string buttonName, string console = "")
        {
            string n = buttonName.ToLower();

            switch (console)
            {
                // ── Sega 6-button: A→Y, C→A, Z→R, Mode→Select ───────────────
                case "Genesis": case "SegaCD": case "Sega32X":
                    return n switch {
                        "a" => 1, "b" => 0, "c" => 8,
                        "x" => 9, "y" => 10, "z" => 11,
                        "mode" => 2, "start" => 3,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        _ => uint.MaxValue
                    };
                case "Saturn":
                    return n switch {
                        "a" => 1, "b" => 0, "c" => 8,
                        "x" => 9, "y" => 10, "z" => 11,
                        "l" => 12, "r" => 13,
                        "select" => 2, "start" => 3,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        _ => uint.MaxValue
                    };

                // ── PlayStation ───────────────────────────────────────────────
                case "PS1": case "PSP":
                    return n switch {
                        "cross" => 0, "circle" => 8, "square" => 1, "triangle" => 9,
                        "l1" => 10, "r1" => 11, "l2" => 12, "r2" => 13, "l3" => 14, "r3" => 15,
                        "select" => 2, "start" => 3,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        // Analog directions route to stick events
                        "left analog up"    => ANALOG_LEFT_UP,    "left analog down"  => ANALOG_LEFT_DOWN,
                        "left analog left"  => ANALOG_LEFT_LEFT,  "left analog right" => ANALOG_LEFT_RIGHT,
                        "right analog up"   => ANALOG_RIGHT_UP,   "right analog down" => ANALOG_RIGHT_DOWN,
                        "right analog left" => ANALOG_RIGHT_LEFT, "right analog right"=> ANALOG_RIGHT_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── NEC PC-Engine ─────────────────────────────────────────────
                case "TG16": case "TGCD":
                    return n switch {
                        "ii" => 0, "i" => 8, "select" => 2, "run" => 3,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        _ => uint.MaxValue
                    };
                case "PCFX":
                    return n switch {
                        "i" => 0, "ii" => 8, "iii" => 1, "iv" => 9, "v" => 10, "vi" => 11,
                        "select" => 2, "run" => 3,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        _ => uint.MaxValue
                    };

                // ── Nintendo 64 ───────────────────────────────────────────────
                case "N64":
                    return n switch {
                        "a" => 0, "b" => 1, "z" => 12, "l" => 10, "r" => 11, "start" => 3,   // A=JOYPAD_B(0), B=JOYPAD_Y(1)
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        "analog up"    => ANALOG_LEFT_UP,    "analog down"  => ANALOG_LEFT_DOWN,
                        "analog left"  => ANALOG_LEFT_LEFT,  "analog right" => ANALOG_LEFT_RIGHT,
                        "c up"         => ANALOG_RIGHT_UP,   "c down"       => ANALOG_RIGHT_DOWN,
                        "c left"       => ANALOG_RIGHT_LEFT, "c right"      => ANALOG_RIGHT_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── GameCube ──────────────────────────────────────────────────
                case "GameCube":
                    return n switch {
                        "a" => 8, "b" => 0, "x" => 9, "y" => 1, "l" => 10, "r" => 11, "z" => 12, "start" => 3,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        "analog up"     => ANALOG_LEFT_UP,    "analog down"  => ANALOG_LEFT_DOWN,
                        "analog left"   => ANALOG_LEFT_LEFT,  "analog right" => ANALOG_LEFT_RIGHT,
                        "c-stick up"    => ANALOG_RIGHT_UP,   "c-stick down" => ANALOG_RIGHT_DOWN,
                        "c-stick left"  => ANALOG_RIGHT_LEFT, "c-stick right"=> ANALOG_RIGHT_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Sega 8-bit ────────────────────────────────────────────────
                case "SMS": case "GameGear": case "SG1000":
                    return n switch {
                        "1" => 0, "2" => 8, "start" => 3,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        _ => uint.MaxValue
                    };

                // ── Atari ─────────────────────────────────────────────────────
                case "Atari2600": case "Atari7800":
                    return n switch {
                        "fire" => 0, "fire 1" => 0, "fire 2" => 1,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        _ => uint.MaxValue
                    };
                case "AtariLynx":
                    return n switch {
                        "a" => 8, "b" => 0, "option 1" => 2, "option 2" => 3, "pause" => 10,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        _ => uint.MaxValue
                    };
                case "Jaguar":
                    return n switch {
                        "a" => 0, "b" => 8, "c" => 11, "option" => 2, "pause" => 3,
                        "*" => 10, "#" => 1, "0" => 9,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        _ => uint.MaxValue
                    };

                // ── Others ────────────────────────────────────────────────────
                case "ColecoVision":
                    return n switch {
                        "l" => 10, "r" => 11, "1" => 8, "2" => 0, "3" => 9, "4" => 1,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        _ => uint.MaxValue
                    };
                case "Intellivision":
                    return n switch {
                        "top" => 8, "left side" => 0, "right side" => 1,
                        "1" => 10, "2" => 11, "3" => 9,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        _ => uint.MaxValue
                    };
                case "Vectrex":
                    return n switch {
                        "1" => 8, "2" => 0, "3" => 9, "4" => 1,
                        "analog up" => ANALOG_LEFT_UP, "analog down" => ANALOG_LEFT_DOWN,
                        "analog left" => ANALOG_LEFT_LEFT, "analog right" => ANALOG_LEFT_RIGHT,
                        _ => uint.MaxValue
                    };
                case "3DO":
                    return n switch {
                        "c" => 8, "b" => 0, "a" => 1, "x" => 9, "l" => 10, "r" => 11, "p" => 3,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        "left analog up"   => ANALOG_LEFT_UP,   "left analog down"  => ANALOG_LEFT_DOWN,
                        "left analog left" => ANALOG_LEFT_LEFT, "left analog right" => ANALOG_LEFT_RIGHT,
                        _ => uint.MaxValue
                    };
                case "NGP":
                    return n switch {
                        "a" => 8, "b" => 0, "option" => 3,
                        "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                        _ => uint.MaxValue
                    };
                case "VirtualBoy":
                    return n switch {
                        "left up" => 4, "left down" => 5, "left left" => 6, "left right" => 7,
                        "right up" => 9, "right down" => 0, "right left" => 1, "right right" => 8,
                        "a" => 8, "b" => 0, "l" => 10, "r" => 11,
                        "select" => 2, "start" => 3,
                        _ => uint.MaxValue
                    };
            }

            // Standard libretro joypad mapping (NES, SNES, GB, GBA, NDS, FDS, MSX, etc.)
            return n switch
            {
                "b" => 0, "y" => 1, "select" => 2, "start" => 3,
                "up" => 4, "down" => 5, "left" => 6, "right" => 7,
                "a" => 8, "x" => 9, "l" => 10, "r" => 11,
                "l2" => 12, "r2" => 13, "l3" => 14, "r3" => 15,
                // Analog directions (Name field has spaces: "Analog Up" → "analog up")
                "analog up"    => ANALOG_LEFT_UP,    "analog down"  => ANALOG_LEFT_DOWN,
                "analog left"  => ANALOG_LEFT_LEFT,  "analog right" => ANALOG_LEFT_RIGHT,
                _ => uint.MaxValue
            };
        }

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
                12 => false,
                13 => false,
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

        private static bool _sdl3Available;

        private static void InitSdl3()
        {
            if (_sdl3Available) return;
            try
            {
                uint needed = SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD;
                if ((SDL_WasInit(needed) & needed) != needed)
                    SDL_Init(needed);
                _sdl3Available = true;
            }
            catch (DllNotFoundException) { }
            catch { }
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
        public static List<string> GetConnectedControllers()
        {
            var result = new List<string>();

            // ── SDL3 path (preferred) ─────────────────────────────────────────
            try
            {
                InitSdl3();
                if (_sdl3Available)
                {
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
                    return result;
                }
            }
            catch { }

            // ── XInput fallback (no names, just count) ────────────────────────
            if (!_xInputInitialized || _xInputGetState == null)
                return result;

            for (uint slot = 0; slot < 4; slot++)
            {
                try
                {
                    uint code = _xInputGetState(slot, out XINPUT_STATE _);
                    if (code != 0) continue;

                    result.Add($"Controller {slot + 1}");
                }
                catch { }
            }
            return result;
        }
    }
}