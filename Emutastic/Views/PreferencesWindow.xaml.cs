using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using Emutastic.Services;
using Emutastic.Models;
using Emutastic.Configuration;
using Microsoft.Extensions.Logging;

namespace Emutastic.Views
{
    public partial class PreferencesWindow : Window
    {
        // ── State ─────────────────────────────────────────────────────────────
        private readonly DatabaseService _db;
        private readonly ControllerManager? _controllerManager;
        private readonly IConfigurationService _configService;
        private readonly ILogger<PreferencesWindow>? _logger;

        private bool _suppressAutoSave;
        private bool _snapsLoaded;
        private bool _achievementsLoaded;

        private string _currentConsole = "SNES";
        private int    _currentPlayer  = 1;            // 1-based
        private bool   _isKeyboardMode = true;
        private string _selectedDevice = "Keyboard";

        // Current mappings: buttonName → InputMapping
        private Dictionary<string, InputMapping> _mappings = new();

        // Rows built for the current console
        private record MappingRow(string ButtonName, Border Box, TextBlock BoxLabel);
        private List<MappingRow> _rows = new();
        private int _waitingRowIndex = -1;             // index into _rows, or -1

        // Chord-capture state, used only for the "Disk Swap" row. The user presses
        // the first input (stored here), then a second different input while still
        // holding the first → both halves are committed as a chord. Any input event
        // before the second press just updates the "first half" candidate.
        private Key?  _chordFirstKey;
        private uint? _chordFirstCtrl;
        private string? _chordFirstDisplay;

        // After capturing an analog direction, ignore all further analog events for
        // this many milliseconds so the stick's return-to-center doesn't cascade.
        private static readonly TimeSpan AnalogCooldown = TimeSpan.FromMilliseconds(600);
        private DateTime _analogLastCapture = DateTime.MinValue;

        // Brushes (resolved once after Loaded)
        private SolidColorBrush _brushUnmapped  = new(Color.FromRgb(0x27, 0x27, 0x29));
        private SolidColorBrush _brushMapped    = new(Color.FromRgb(0x1F, 0x1F, 0x21));
        private SolidColorBrush _brushWaiting   = new(Color.FromRgb(0xE0, 0x35, 0x35));
        private SolidColorBrush _brushText      = new(Color.FromRgb(0xF0, 0xF0, 0xF0));
        private SolidColorBrush _brushTextMuted = new(Color.FromRgb(0x55, 0x55, 0x58));

        // ── Controller hotplug polling ────────────────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _controllerPollTimer;
        private List<string> _lastKnownDevices = new();

        // ── Section navigation ────────────────────────────────────────────────
        private enum PrefSection { Controls, SystemFiles, Cores, Library, Theme, Snaps, CoreOptions, Achievements, Media, Backups, EmuTv, About }
        private PrefSection _activeSection = PrefSection.Controls;

        // Lazy-tab gating: build each panel at most once per cache lifetime,
        // and never let two simultaneous clicks stack two builders.
        private readonly HashSet<PrefSection> _sectionLoaded = new();
        private readonly HashSet<PrefSection> _sectionLoading = new();

        // Cancellation for in-flight async work scoped to this window's lifetime.
        // Signalled in the Closed handler so HTTP / Task.Run continuations bail
        // before they touch the now-disposed visual tree.
        private readonly CancellationTokenSource _windowCts = new();

        // ── Core Options state ────────────────────────────────────────────────
        private string _selectedCoreOptionsName = "";
        private Dictionary<string, string> _pendingCoreOptionValues = new();

        // ── Constructors ──────────────────────────────────────────────────────
        public PreferencesWindow(DatabaseService db, ControllerManager controllerManager,
            IConfigurationService configService, ILogger<PreferencesWindow>? logger = null,
            string? initialConsole = null)
        {
            InitializeComponent();
            ApplyWindowsChrome();
            _db              = db;
            _controllerManager = controllerManager;
            _configService   = configService;
            _logger          = logger;
            if (initialConsole != null) _currentConsole = initialConsole;

            Loaded += OnLoaded;
            KeyDown += OnWindowKeyDown;
            PreviewKeyDown += OnPreviewKeyDown;

            if (_controllerManager != null)
                _controllerManager.ButtonChanged += OnControllerButtonChanged;

            // Refresh the Theme tab live when the user saves a custom theme
            // from the Theme Editor — no close/reopen of Preferences required.
            Services.ThemeService.Instance.ThemesChanged += OnThemesChanged;

            Closed += (_, _) =>
            {
                try { _windowCts.Cancel(); } catch { }
                try { _windowCts.Dispose(); } catch { }
                _controllerPollTimer?.Stop();
                if (_controllerManager != null)
                {
                    _controllerManager.RawMode = false;
                    _controllerManager.ButtonChanged -= OnControllerButtonChanged;
                }
                Services.ThemeService.Instance.ThemesChanged -= OnThemesChanged;
                // Tear down the pause effect preview runner so its
                // CompositionTarget.Rendering subscription doesn't outlive the window.
                try { _pauseEffectPreviewRunner?.Dispose(); _pauseEffectPreviewRunner = null; } catch { }
                // Cancel pending debounced writes so their delayed Dispatcher.Invoke can't
                // run on a torn-down dispatcher after close (the explicit saves below flush).
                try { _toastSaveCts?.Cancel(); _toastSaveCts?.Dispose(); } catch { }
                try { _raApiKeyDebounceCts?.Cancel(); _raApiKeyDebounceCts?.Dispose(); } catch { }
                // Save any pending credential changes on close
                SaveSnapSettings();
                SaveAchievementsSettings();
            };
        }

        private void OnThemesChanged(object? sender, EventArgs e)
        {
            // ThemeService might fire from any thread (file IO inside SaveCustomTheme).
            // Marshal back to the dispatcher before touching XAML elements.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    PreferencesCache.InvalidateThemes();
                    InvalidateSection(PrefSection.Theme);
                    if (_activeSection == PrefSection.Theme)
                        _ = LoadSectionAsync(PrefSection.Theme);
                }
                catch { }
            }));
        }

        public PreferencesWindow(DatabaseService db, ControllerManager controllerManager)
            : this(db, controllerManager, App.Configuration
                  ?? throw new InvalidOperationException("Configuration not initialized"))
        { }

        // ── Initialisation ────────────────────────────────────────────────────
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            PopulateSystemComboBox();
            PlayerComboBox.SelectedIndex = 0;
            await PopulateInputDevicesAsync();
        }

        private void PopulateSystemComboBox()
        {
            var consoles = ControllerDefinitions.GetSupportedConsoles();
            SystemComboBox.ItemsSource = consoles
                .Select(c => new ConsoleItem(c.Tag, c.Name, ConsoleItem.ResolveIconUri(c.Tag)))
                .ToList();

            // Select previously-used console or default to SNES
            int idx = consoles.FindIndex(c => c.Tag == _currentConsole);
            SystemComboBox.SelectedIndex = idx >= 0 ? idx : 0;
        }

        private void PopulateInputDevices() => _ = PopulateInputDevicesAsync();

        private async Task PopulateInputDevicesAsync()
        {
            var devices = await PreferencesCache
                .GetControllerDevicesAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(true);
            if (!IsLoaded) return;

            InputDeviceComboBox.ItemsSource = devices;

            // Restore an explicit controller choice if it's still connected.
            // "Keyboard" is only a passive default — if a controller is present prefer it.
            if (_selectedDevice != "Keyboard" && devices.Contains(_selectedDevice))
                InputDeviceComboBox.SelectedItem = _selectedDevice;
            else if (devices.Count > 1)
                InputDeviceComboBox.SelectedItem = devices[1];           // first controller
            else
                InputDeviceComboBox.SelectedItem = "Keyboard";
        }

        // ── Console / layout ──────────────────────────────────────────────────
        private void LoadConsole(string consoleTag)
        {
            _currentConsole   = consoleTag;
            _waitingRowIndex  = -1;

            var def = ControllerDefinitions.GetControllerDefinition(consoleTag);
            if (def == null) return;

            // Controller image
            try
            {
                ControllerImage.Source = new BitmapImage(new Uri(def.ControllerImage, UriKind.Relative));
                // FDS image is too zoomed-in compared to other consoles — scale it down
                ControllerImage.RenderTransformOrigin = new Point(0.5, 0.5);
                ControllerImage.RenderTransform = consoleTag == "FDS"
                    ? new ScaleTransform(0.7, 0.7)
                    : null;
            }
            catch { ControllerImage.Source = null; }

            // Load saved mappings for this console+player
            LoadMappingsFromConfig();

            // Rebuild the button rows
            RebuildButtonsPanel(def);
        }

        private void RebuildButtonsPanel(ControllerDefinition def)
        {
            ButtonsPanel.Children.Clear();
            _rows.Clear();

            // Group buttons by their Group property
            var groups = def.Buttons
                .GroupBy(b => string.IsNullOrEmpty(b.Group) ? "Buttons" : b.Group)
                .ToList();

            foreach (var group in groups)
            {
                // Group header
                var header = new TextBlock
                {
                    Text       = group.Key.ToUpperInvariant(),
                    Style      = (Style)FindResource("GroupHeader"),
                };
                ButtonsPanel.Children.Add(header);

                // Divider under header
                ButtonsPanel.Children.Add(new Rectangle
                {
                    Height  = 1,
                    Fill    = (SolidColorBrush)FindResource("BorderNormalBrush"),
                    Margin  = new Thickness(0, 0, 0, 6),
                });

                foreach (var btn in group)
                {
                    var row = BuildMappingRow(btn.Name);
                    ButtonsPanel.Children.Add(row.grid);
                    _rows.Add(new MappingRow(btn.Name, row.box, row.label));
                }
            }

            // Frontend-only Disk Swap action — added at the bottom for consoles whose
            // cores expose the libretro disk control interface (FDS, PSX, Saturn,
            // SegaCD, Amiga). Default trigger is Start+Select chord; this row lets the
            // user bind a single key / controller button as an alternative.
            if (EmulatorWindow.ConsoleSupportsDiskSwap(_currentConsole))
            {
                ButtonsPanel.Children.Add(new TextBlock
                {
                    Text  = "FRONTEND",
                    Style = (Style)FindResource("GroupHeader"),
                });
                ButtonsPanel.Children.Add(new Rectangle
                {
                    Height = 1,
                    Fill   = (SolidColorBrush)FindResource("BorderNormalBrush"),
                    Margin = new Thickness(0, 0, 0, 6),
                });
                var diskRow = BuildMappingRow("Disk Swap");
                ButtonsPanel.Children.Add(diskRow.grid);
                _rows.Add(new MappingRow("Disk Swap", diskRow.box, diskRow.label));
            }

            // Frontend-only save/load hotkeys — on every console. In-game: hold the
            // Hotkey button ~1s, then the Save State / Load State button fires.
            // Unbound rows use the defaults L3 (Hotkey) / R2 (Save) / L2 (Load).
            ButtonsPanel.Children.Add(new TextBlock
            {
                Text  = "SAVE / LOAD HOTKEYS",
                Style = (Style)FindResource("GroupHeader"),
            });
            ButtonsPanel.Children.Add(new Rectangle
            {
                Height = 1,
                Fill   = (SolidColorBrush)FindResource("BorderNormalBrush"),
                Margin = new Thickness(0, 0, 0, 6),
            });
            foreach (var hk in new[] { "Hotkey", "Save State", "Load State" })
            {
                var hkRow = BuildMappingRow(hk);
                ButtonsPanel.Children.Add(hkRow.grid);
                _rows.Add(new MappingRow(hk, hkRow.box, hkRow.label));
            }

            // Refresh display text on all rows
            RefreshAllRows();
        }

        private (Grid grid, Border box, TextBlock label) BuildMappingRow(string buttonName)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

            // Button name label
            var nameLabel = new TextBlock
            {
                Text               = buttonName,
                Foreground         = _brushText,
                FontSize           = 12,
                VerticalAlignment  = VerticalAlignment.Center,
                TextTrimming       = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(nameLabel, 0);

            // Mapping box
            var boxLabel = new TextBlock
            {
                Text              = "—",
                Foreground        = _brushTextMuted,
                FontSize          = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                TextTrimming        = TextTrimming.CharacterEllipsis,
            };

            var box = new Border
            {
                Background      = _brushUnmapped,
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(8, 4, 8, 4),
                Cursor          = Cursors.Hand,
                Child           = boxLabel,
                Tag             = buttonName,
            };
            Grid.SetColumn(box, 1);

            box.MouseLeftButtonDown += MappingBox_Click;

            grid.Children.Add(nameLabel);
            grid.Children.Add(box);

            return (grid, box, boxLabel);
        }

        // ── Mapping persistence ───────────────────────────────────────────────
        private string ConfigKey => $"{_currentConsole}_P{_currentPlayer}";

        private void LoadMappingsFromConfig()
        {
            _mappings.Clear();
            var config = _configService.GetInputConfiguration(ConfigKey);

            var source = _isKeyboardMode ? config.KeyboardMappings : config.ControllerMappings;
            foreach (var m in source)
            {
                bool isChord = !string.IsNullOrEmpty(m.InputIdentifier) && m.InputIdentifier.Contains('+');
                _mappings[m.ButtonName] = new InputMapping
                {
                    ConsoleName         = _currentConsole,
                    ButtonName          = m.ButtonName,
                    InputType           = _isKeyboardMode ? Services.InputType.Keyboard : Services.InputType.Controller,
                    Key                 = _isKeyboardMode && !isChord && Enum.TryParse<Key>(m.InputIdentifier, out var k) ? k : Key.None,
                    ControllerButtonId  = !_isKeyboardMode && !isChord && uint.TryParse(m.InputIdentifier, out var bid) ? bid : 0,
                    DisplayText         = m.DisplayName,
                    ChordIdentifier     = isChord ? m.InputIdentifier : null,
                };
            }

            // Update controller slot picker: -1 = Default (index 0), 0-3 = Controller 1-4 (indices 1-4)
            _suppressAutoSave = true;
            int slot = config.ControllerSlot;
            ControllerSlotComboBox.SelectedIndex = (slot >= 0 && slot <= 3) ? slot + 1 : 0;
            _suppressAutoSave = false;
        }

        private void SaveMappingsToConfig()
        {
            var config = _configService.GetInputConfiguration(ConfigKey);

            if (_isKeyboardMode)
            {
                config.KeyboardMappings.Clear();
                foreach (var m in _mappings.Values.Where(m => m.InputType == Services.InputType.Keyboard && (m.Key != Key.None || !string.IsNullOrEmpty(m.ChordIdentifier))))
                {
                    config.KeyboardMappings.Add(new ButtonMapping
                    {
                        ButtonName      = m.ButtonName,
                        InputIdentifier = !string.IsNullOrEmpty(m.ChordIdentifier)
                            ? m.ChordIdentifier
                            : m.Key.ToString(),
                        InputType       = Configuration.InputType.Keyboard,
                        DisplayName     = m.DisplayText,
                    });
                }
            }
            else
            {
                config.ControllerMappings.Clear();
                foreach (var m in _mappings.Values.Where(m => m.InputType == Services.InputType.Controller))
                {
                    config.ControllerMappings.Add(new ButtonMapping
                    {
                        ButtonName      = m.ButtonName,
                        InputIdentifier = !string.IsNullOrEmpty(m.ChordIdentifier)
                            ? m.ChordIdentifier
                            : m.ControllerButtonId.ToString(),
                        InputType       = Configuration.InputType.Controller,
                        DisplayName     = m.DisplayText,
                    });
                }
            }

            _configService.SetInputConfiguration(ConfigKey, config);
        }

        // ── Row display ───────────────────────────────────────────────────────
        private void RefreshAllRows()
        {
            for (int i = 0; i < _rows.Count; i++)
                RefreshRow(i);
        }

        private void RefreshRow(int idx)
        {
            var row = _rows[idx];
            bool waiting = idx == _waitingRowIndex;

            if (waiting)
            {
                row.Box.Background   = _brushWaiting;
                row.BoxLabel.Text       = "Press a button…";
                row.BoxLabel.Foreground = Brushes.White;
                return;
            }

            if (_mappings.TryGetValue(row.ButtonName, out var m) && !string.IsNullOrEmpty(m.DisplayText) && m.DisplayText != "Not mapped")
            {
                row.Box.Background      = _brushMapped;
                row.BoxLabel.Text       = m.DisplayText;
                row.BoxLabel.Foreground = _brushText;
            }
            else
            {
                row.Box.Background      = _brushUnmapped;
                row.BoxLabel.Text       = "—";
                row.BoxLabel.Foreground = _brushTextMuted;
            }
        }

        // ── Input capture flow ────────────────────────────────────────────────
        private void StartWaiting(int rowIndex)
        {
            int prev = _waitingRowIndex;
            _waitingRowIndex = rowIndex;
            if (_controllerManager != null) _controllerManager.RawMode = true;
            if (prev >= 0 && prev < _rows.Count) RefreshRow(prev);
            RefreshRow(rowIndex);
        }

        private void StopWaiting()
        {
            int prev = _waitingRowIndex;
            _waitingRowIndex    = -1;
            _analogLastCapture  = DateTime.MinValue;
            _chordFirstKey      = null;
            _chordFirstCtrl     = null;
            _chordFirstDisplay  = null;
            if (_controllerManager != null) _controllerManager.RawMode = false;
            if (prev >= 0 && prev < _rows.Count) RefreshRow(prev);
        }

        private void CommitMapping(string buttonName, string displayText,
            Key key = Key.None, uint controllerId = 0, bool skipAutoAdvance = false)
        {
            _mappings[buttonName] = new InputMapping
            {
                ConsoleName        = _currentConsole,
                ButtonName         = buttonName,
                InputType          = _isKeyboardMode ? Services.InputType.Keyboard : Services.InputType.Controller,
                Key                = key,
                ControllerButtonId = controllerId,
                DisplayText        = displayText,
            };

            int cur = _waitingRowIndex;
            _waitingRowIndex = -1;  // clear BEFORE RefreshRow so row shows new mapping, not "Press a button…"
            RefreshRow(cur);

            if (!skipAutoAdvance)
                AdvanceFromRow(cur);
        }

        private void AdvanceFromRow(int from)
        {
            int next = from + 1;
            if (next < _rows.Count)
                StartWaiting(next);
            // else end of list — leave _waitingRowIndex = -1
        }

        // ── Event: mapping box clicked ────────────────────────────────────────
        private void MappingBox_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border box || box.Tag is not string btnName) return;
            int idx = _rows.FindIndex(r => r.ButtonName == btnName);
            if (idx < 0) return;
            StartWaiting(idx);
            Focus(); // ensure window has keyboard focus
        }

        // ── Event: key pressed ────────────────────────────────────────────────
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_isKeyboardMode && _waitingRowIndex >= 0)
                e.Handled = true; // prevent system handling
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isKeyboardMode || _waitingRowIndex < 0) return;

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { _chordFirstKey = null; _chordFirstDisplay = null; StopWaiting(); return; }

            string display = KeyToDisplayString(key);
            string btnName = _rows[_waitingRowIndex].ButtonName;

            // Disk Swap is captured as a CHORD: first press becomes the candidate
            // first half, second different press commits both as "A+B".
            if (string.Equals(btnName, "Disk Swap", StringComparison.OrdinalIgnoreCase))
            {
                if (_chordFirstKey == null)
                {
                    _chordFirstKey = key;
                    _chordFirstDisplay = display;
                    var row = _rows[_waitingRowIndex];
                    row.BoxLabel.Text = $"{display} + …";
                    return;
                }
                if (key == _chordFirstKey.Value) return; // ignore repeat of same key
                CommitChordMapping(btnName,
                    keyA: _chordFirstKey.Value, keyB: key,
                    display: $"{_chordFirstDisplay} + {display}");
                _chordFirstKey = null;
                _chordFirstDisplay = null;
                return;
            }

            CommitMapping(btnName, display, key: key);
        }

        private void OnControllerButtonChanged(uint buttonId, bool isPressed)
        {
            if (_isKeyboardMode) return;

            Dispatcher.BeginInvoke(() =>
            {
                if (!isPressed || _waitingRowIndex < 0) return;

                bool isAnalogDir = buttonId >= ControllerManager.ANALOG_LEFT_UP;

                // After capturing an analog direction, ignore further analog events for
                // a short cooldown so the stick returning to centre doesn't cascade.
                if (isAnalogDir && (DateTime.UtcNow - _analogLastCapture) < AnalogCooldown)
                    return;

                string btnName = _rows[_waitingRowIndex].ButtonName;
                string display = isAnalogDir ? AnalogDirToString(buttonId) : $"Button {buttonId}";

                // Disk Swap is captured as a CHORD on controller too.
                if (string.Equals(btnName, "Disk Swap", StringComparison.OrdinalIgnoreCase))
                {
                    if (_chordFirstCtrl == null)
                    {
                        _chordFirstCtrl = buttonId;
                        _chordFirstDisplay = display;
                        var row = _rows[_waitingRowIndex];
                        row.BoxLabel.Text = $"{display} + …";
                        if (isAnalogDir) _analogLastCapture = DateTime.UtcNow;
                        return;
                    }
                    if (buttonId == _chordFirstCtrl.Value) return; // ignore repeat
                    CommitChordMapping(btnName,
                        ctrlA: _chordFirstCtrl.Value, ctrlB: buttonId,
                        display: $"{_chordFirstDisplay} + {display}");
                    _chordFirstCtrl = null;
                    _chordFirstDisplay = null;
                    if (isAnalogDir) _analogLastCapture = DateTime.UtcNow;
                    return;
                }

                CommitMapping(btnName, display, controllerId: buttonId);

                if (isAnalogDir)
                    _analogLastCapture = DateTime.UtcNow;
            });
        }

        // Commit a two-input chord mapping. Stored in InputIdentifier as "A+B" so the
        // existing config schema doesn't need a new field; runtime parsers split on '+'.
        private void CommitChordMapping(string buttonName, string display,
            Key keyA = Key.None, Key keyB = Key.None,
            uint ctrlA = uint.MaxValue, uint ctrlB = uint.MaxValue)
        {
            // Re-use InputMapping shape: pack chord halves into the existing fields.
            // SaveMappingsToConfig writes Key/ControllerButtonId; for chords we instead
            // need a composite identifier — handled in a small special case there.
            _mappings[buttonName] = new InputMapping
            {
                ConsoleName        = _currentConsole,
                ButtonName         = buttonName,
                InputType          = _isKeyboardMode ? Services.InputType.Keyboard : Services.InputType.Controller,
                Key                = keyA, // first half (KeyB stored via ChordIdentifier below)
                ControllerButtonId = ctrlA,
                DisplayText        = display,
                ChordIdentifier    = _isKeyboardMode
                    ? $"{keyA}+{keyB}"
                    : $"{ctrlA}+{ctrlB}",
            };

            int cur = _waitingRowIndex;
            _waitingRowIndex = -1;
            RefreshRow(cur);
            // No auto-advance after Disk Swap — it's the last row.
        }

        private static string AnalogDirToString(uint buttonId) => buttonId switch
        {
            ControllerManager.ANALOG_LEFT_UP    => "L Stick ↑",
            ControllerManager.ANALOG_LEFT_DOWN  => "L Stick ↓",
            ControllerManager.ANALOG_LEFT_LEFT  => "L Stick ←",
            ControllerManager.ANALOG_LEFT_RIGHT => "L Stick →",
            ControllerManager.ANALOG_RIGHT_UP   => "R Stick ↑",
            ControllerManager.ANALOG_RIGHT_DOWN => "R Stick ↓",
            ControllerManager.ANALOG_RIGHT_LEFT => "R Stick ←",
            ControllerManager.ANALOG_RIGHT_RIGHT=> "R Stick →",
            _ => $"Analog {buttonId}"
        };

        private static string KeyToDisplayString(Key key) => key switch
        {
            Key.Space       => "Space",
            Key.Return      => "Enter",
            Key.Back        => "Backspace",
            Key.Escape      => "Escape",
            Key.Tab         => "Tab",
            Key.LeftShift   => "L Shift",
            Key.RightShift  => "R Shift",
            Key.LeftCtrl    => "L Ctrl",
            Key.RightCtrl   => "R Ctrl",
            Key.LeftAlt     => "L Alt",
            Key.RightAlt    => "R Alt",
            Key.Up          => "↑",
            Key.Down        => "↓",
            Key.Left        => "←",
            Key.Right       => "→",
            _               => key.ToString(),
        };

        // ── Combo-box event handlers ──────────────────────────────────────────
        private void SystemComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SystemComboBox.SelectedItem is not ConsoleItem item) return;
            StopWaiting();
            LoadConsole(item.Tag);
        }

        private void PlayerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _currentPlayer = PlayerComboBox.SelectedIndex + 1;
            StopWaiting();
            LoadMappingsFromConfig();
            RefreshAllRows();
        }

        private void ControllerSlotComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAutoSave || ControllerSlotComboBox.SelectedIndex < 0) return;
            var config = _configService.GetInputConfiguration(ConfigKey);
            // Index 0 = Default (-1), Index 1-4 = Controller 1-4 (slot 0-3)
            config.ControllerSlot = ControllerSlotComboBox.SelectedIndex - 1;
            _configService.SetInputConfiguration(ConfigKey, config);
        }

        private void InputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (InputDeviceComboBox.SelectedItem is not string device) return;
            _selectedDevice  = device;
            _isKeyboardMode  = device == "Keyboard";
            StopWaiting();
            LoadMappingsFromConfig();
            RefreshAllRows();
        }

        private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
            => PopulateInputDevices();

        // ── Windows chrome mode ───────────────────────────────────────────────
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void ApplyWindowsChrome()
        {
            var theme = App.Configuration?.GetThemeConfiguration();
            if (theme?.UseWindowsChrome != true) return;

            WindowStyle = System.Windows.WindowStyle.SingleBorderWindow;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.CanResize;

            OuterBorder.Margin = new Thickness(0);
            OuterBorder.CornerRadius = new CornerRadius(0);
            OuterBorder.BorderThickness = new Thickness(0);
            OuterBorder.Effect = null;

            CustomTitleBar.Visibility = Visibility.Collapsed;
            RootGrid.RowDefinitions[0].Height = new GridLength(0);

            SourceInitialized += (_, _) => ApplyDarkTitleBar();
        }

        private void ApplyDarkTitleBar()
        {
            if (new WindowInteropHelper(this).Handle is var hwnd && hwnd != IntPtr.Zero)
            {
                int value = 1;
                DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
            }
        }

        // ── Title bar ─────────────────────────────────────────────────────────
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => DragMove();

        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => Close();

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        // ── Save / Reset ──────────────────────────────────────────────────────
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveMappingsToConfig();
                _configService.SaveAsync();
                Close();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save preferences");
                MessageBox.Show($"Failed to save: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            _mappings.Clear();

            var defaults = _isKeyboardMode
                ? ConfigurationExtensions.GetDefaultKeyboardMappings(_currentConsole)
                : ConfigurationExtensions.GetDefaultControllerMappings(_currentConsole);

            foreach (var d in defaults)
            {
                _mappings[d.ButtonName] = new InputMapping
                {
                    ConsoleName        = _currentConsole,
                    ButtonName         = d.ButtonName,
                    InputType          = _isKeyboardMode ? Services.InputType.Keyboard : Services.InputType.Controller,
                    Key                = _isKeyboardMode && Enum.TryParse<Key>(d.InputIdentifier, out var k) ? k : Key.None,
                    ControllerButtonId = !_isKeyboardMode && uint.TryParse(d.InputIdentifier, out var bid) ? bid : 0,
                    DisplayText        = d.DisplayName,
                };
            }

            StopWaiting();
            RefreshAllRows();
        }

        // ── Section navigation ────────────────────────────────────────────────

        /// <summary>
        /// Programmatic entry point for opening a specific preferences section.
        /// Used by the bottom-left core-update banner click handler. Names match
        /// the Nav* button identifiers (case-insensitive).
        /// </summary>
        public void OpenSection(string sectionName)
        {
            // Callers open sections before ShowDialog (e.g. the core-update
            // banner). Checking the nav toggle here would kick the async
            // section builder while IsLoaded is still false, making it bail
            // against its post-await guard. Defer until Loaded so the builder
            // runs against a live window.
            if (!IsLoaded)
            {
                Loaded += (_, _) => OpenSection(sectionName);
                return;
            }
            switch (sectionName.Trim().ToLowerInvariant())
            {
                case "controls":      NavControls.IsChecked = true; break;
                case "systemfiles":
                case "system files":  NavSystemFiles.IsChecked = true; break;
                case "cores":         NavCores.IsChecked = true; break;
                case "library":       NavLibrary.IsChecked = true; break;
                case "theme":         NavTheme.IsChecked = true; break;
                case "snaps":         NavSnaps.IsChecked = true; break;
                case "coreoptions":
                case "core options":  NavCoreOptions.IsChecked = true; break;
                case "achievements":  NavAchievements.IsChecked = true; break;
                case "media":
                case "folders":       NavMedia.IsChecked = true; break;
                case "backups":
                case "backup":
                case "cloud sync":    NavBackups.IsChecked = true; break;
                case "about":         NavAbout.IsChecked = true; break;
            }
        }

        private void NavBtn_Checked(object sender, RoutedEventArgs e)
        {
            if (sender == NavControls)         ShowSection(PrefSection.Controls);
            else if (sender == NavSystemFiles)  ShowSection(PrefSection.SystemFiles);
            else if (sender == NavCores)        ShowSection(PrefSection.Cores);
            else if (sender == NavLibrary)      ShowSection(PrefSection.Library);
            else if (sender == NavTheme)        ShowSection(PrefSection.Theme);
            else if (sender == NavSnaps)        ShowSection(PrefSection.Snaps);
            else if (sender == NavCoreOptions)  ShowSection(PrefSection.CoreOptions);
            else if (sender == NavAchievements) ShowSection(PrefSection.Achievements);
            else if (sender == NavMedia)        ShowSection(PrefSection.Media);
            else if (sender == NavBackups)      ShowSection(PrefSection.Backups);
            else if (sender == NavEmuTv)        ShowSection(PrefSection.EmuTv);
            else if (sender == NavAbout)        ShowSection(PrefSection.About);
        }

        private void ShowSection(PrefSection section)
        {
            if (PanelControls == null) return; // fired during InitializeComponent before panels exist
            _activeSection = section;
            PanelControls.Visibility    = section == PrefSection.Controls    ? Visibility.Visible : Visibility.Collapsed;
            PanelSystemFiles.Visibility = section == PrefSection.SystemFiles ? Visibility.Visible : Visibility.Collapsed;
            PanelCores.Visibility       = section == PrefSection.Cores       ? Visibility.Visible : Visibility.Collapsed;
            PanelLibrary.Visibility     = section == PrefSection.Library     ? Visibility.Visible : Visibility.Collapsed;
            PanelTheme.Visibility       = section == PrefSection.Theme       ? Visibility.Visible : Visibility.Collapsed;
            PanelSnaps.Visibility       = section == PrefSection.Snaps       ? Visibility.Visible : Visibility.Collapsed;
            PanelCoreOptions.Visibility = section == PrefSection.CoreOptions ? Visibility.Visible : Visibility.Collapsed;
            PanelAchievements.Visibility = section == PrefSection.Achievements ? Visibility.Visible : Visibility.Collapsed;
            PanelMedia.Visibility       = section == PrefSection.Media        ? Visibility.Visible : Visibility.Collapsed;
            PanelBackups.Visibility     = section == PrefSection.Backups      ? Visibility.Visible : Visibility.Collapsed;
            PanelEmuTv.Visibility       = section == PrefSection.EmuTv        ? Visibility.Visible : Visibility.Collapsed;
            PanelAbout.Visibility       = section == PrefSection.About        ? Visibility.Visible : Visibility.Collapsed;

            // Lazy builder dispatch — never block the dispatcher inline. The
            // builders themselves dispatch heavy work (DB, disk fan-out, HTTP,
            // controller enum) via Task.Run / async / per-call CTS, and a
            // "Loading…" placeholder fills the panel while we wait.
            _ = LoadSectionAsync(section);

            // Controller hotplug poll is dispatcher-affine (Keyboard label +
            // device-list rebind), but the actual enumeration is async-cached.
            if (section == PrefSection.Controls)
            {
                if (_controllerPollTimer == null)
                {
                    _controllerPollTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(2)
                    };
                    _controllerPollTimer.Tick += async (_, _) =>
                    {
                        // Force a fresh enumeration off-thread; cache TTL on
                        // its own would skip the actual SDL/XInput call.
                        PreferencesCache.InvalidateControllers();
                        var current = await PreferencesCache
                            .GetControllerDevicesAsync(TimeSpan.FromSeconds(0))
                            .ConfigureAwait(true);
                        if (!IsLoaded) return;
                        if (!current.SequenceEqual(_lastKnownDevices))
                        {
                            _lastKnownDevices = current;
                            await PopulateInputDevicesAsync().ConfigureAwait(true);
                        }
                    };
                }
                _controllerPollTimer.Start();
                // Seed _lastKnownDevices synchronously on the dispatcher so the
                // very first hot-plug tick has something to compare against.
                // SDL3 hot-plug requires a thread with a message loop, so this
                // MUST run on the dispatcher (not Task.Run).
                _lastKnownDevices = PreferencesCache
                    .GetControllerDevicesAsync(TimeSpan.FromSeconds(2))
                    .GetAwaiter().GetResult();
            }
            else
            {
                _controllerPollTimer?.Stop();
            }
        }

        // ── Lazy section loader ───────────────────────────────────────────────
        // Returns the inner container that should host a "Loading…" placeholder
        // for the given section. Returning null skips the placeholder for
        // sections whose builders mutate scattered XAML elements rather than a
        // single child panel.
        private Panel? GetSectionContainer(PrefSection s) => s switch
        {
            PrefSection.SystemFiles => BiosPanel,
            PrefSection.Cores       => CoresListPanel,
            _ => null
        };

        private static StackPanel BuildLoadingPlaceholder()
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 24, 0, 0),
                Tag = "__loadingPlaceholder"
            };
            stack.Children.Add(new ProgressBar
            {
                IsIndeterminate = true,
                Width = 120,
                Height = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Loading…",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                VerticalAlignment = VerticalAlignment.Center
            });
            return stack;
        }

        private void InvalidateSection(PrefSection s) => _sectionLoaded.Remove(s);

        private async Task LoadSectionAsync(PrefSection section)
        {
            if (_sectionLoaded.Contains(section) || _sectionLoading.Contains(section))
                return;
            _sectionLoading.Add(section);

            // Insert placeholder for the two builders that fan out to disk/DB.
            var container = GetSectionContainer(section);
            StackPanel? placeholder = null;
            if (container != null)
            {
                container.Children.Clear();
                placeholder = BuildLoadingPlaceholder();
                container.Children.Add(placeholder);
            }

            try
            {
                // The two async builders can bail without populating (window not
                // loaded yet, or the user switched sections mid-scan). A bailed
                // build must NOT mark the section loaded, or every revisit hits
                // the _sectionLoaded early-return and the panel stays blank for
                // the lifetime of the window.
                bool completed = true;
                switch (section)
                {
                    case PrefSection.SystemFiles: completed = await BuildBiosPanelAsync(); break;
                    case PrefSection.Cores:       completed = await BuildCoresPanelAsync(); break;
                    case PrefSection.Theme:       LoadThemeSettings(); break;
                    case PrefSection.Media:       LoadFoldersSettings(); break;
                    case PrefSection.Library:     LoadLibrarySettings(); break;
                    case PrefSection.Snaps:       LoadSnapsSettings(); break;
                    case PrefSection.CoreOptions: BuildCoreOptionsTab(); break;
                    case PrefSection.Achievements: LoadAchievementsSettings(); break;
                    case PrefSection.Backups:     LoadBackupsSettings(); break;
                    case PrefSection.EmuTv:       LoadEmuTvSettings(); break;
                    case PrefSection.About:       LoadAboutSettings(); break;
                    case PrefSection.Controls:    /* dispatcher-affine init lives in OnLoaded */ break;
                }
                if (completed)
                    _sectionLoaded.Add(section);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Prefs] LoadSection {section} failed: {ex}");
            }
            finally
            {
                // BIOS/Cores builders rebuild their own container, so the
                // placeholder is already gone — but if the builder threw before
                // clearing, strip it here.
                if (container != null && placeholder != null && container.Children.Contains(placeholder))
                    container.Children.Remove(placeholder);
                _sectionLoading.Remove(section);
            }
        }

        // ── BIOS panel ────────────────────────────────────────────────────────
        // BIOS category groupings matching the cores tab
        private static readonly (string Category, string[] ConsoleDisplays)[] BiosCategories =
        {
            ("Nintendo",  new[] { "Famicom Disk System", "Game Boy Advance" }),
            ("Sega",      new[] { "Sega CD", "Saturn" }),
            ("Sony",      new[] { "PlayStation", "PlayStation 2" }),
            ("NEC",       new[] { "TurboGrafx-CD" }),
            ("Arcade",    new[] { "Neo Geo" }),
            ("Other",     new[] { "3DO", "Philips CD-i" }),
        };

        private void BuildBiosPanel() => _ = BuildBiosPanelAsync();

        /// <returns>
        /// true when the panel was actually populated; false when the build
        /// bailed (window not loaded / section switched away mid-scan) so the
        /// caller must not mark the section as loaded.
        /// </returns>
        private async Task<bool> BuildBiosPanelAsync()
        {
            BiosPanel.Children.Clear();
            string sysDir = AppPaths.GetFolder("System");

            // Async fan-out: DB query + ROM-dir enumeration + every File.Exists
            // candidate path resolves once on a worker thread, returns a
            // pre-checked existence set we can look up by string.
            var scan = await PreferencesCache
                .GetBiosScanAsync(_db, sysDir, KnownBios.All, _windowCts.Token)
                .ConfigureAwait(true);
            if (!IsLoaded || _activeSection != PrefSection.SystemFiles) return false;
            BiosPanel.Children.Clear(); // strip the "Loading…" placeholder
            var romDirsByConsole = scan.RomDirsByConsole;
            bool BiosExists(string path) => scan.ExistingPathsLower.Contains(path);

            // Info banner
            var accent = (Color)FindResource("AccentColor");
            var banner = new Border
            {
                Background  = new SolidColorBrush(Color.FromArgb(0x18, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding  = new Thickness(14, 10, 14, 10),
                Margin   = new Thickness(0, 0, 0, 12)
            };
            var bannerStack = new StackPanel();
            bannerStack.Children.Add(new TextBlock
            {
                Text = "Where to place BIOS files",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = _brushText,
                Margin = new Thickness(0, 0, 0, 4)
            });
            bannerStack.Children.Add(new TextBlock
            {
                Text = $"System folder (recommended):  {sysDir}",
                FontSize = 11,
                Foreground = _brushTextMuted,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap
            });
            bannerStack.Children.Add(new TextBlock
            {
                Text = "Alternatively, place a BIOS file in the same folder as the ROMs for that system — it will be found automatically.",
                FontSize = 11,
                Foreground = _brushTextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            bannerStack.Children.Add(new TextBlock
            {
                Text = "Or just drag and drop BIOS files anywhere on this panel — they'll be recognized and copied here automatically.",
                FontSize = 11,
                Foreground = _brushTextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            banner.Child = bannerStack;
            BiosPanel.Children.Add(banner);

            // (ROM dirs already resolved on a background thread via PreferencesCache above.)

            // Build lookup: ConsoleDisplay → BIOS entries
            var biosGroups = KnownBios.All.GroupBy(b => b.ConsoleDisplay)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Two-level accordion: Category → Console → BIOS files
            foreach (var (category, consoleDisplays) in BiosCategories)
            {
                var activeDisplays = consoleDisplays
                    .Where(d => biosGroups.ContainsKey(d))
                    .ToList();
                if (activeDisplays.Count == 0) continue;

                // Count found/total across the category
                int catFound = 0, catTotal = 0;
                foreach (string display in activeDisplays)
                {
                    foreach (var entry in biosGroups[display])
                    {
                        catTotal++;
                        string path = System.IO.Path.Combine(sysDir, entry.Filename);
                        if (BiosExists(path))
                            catFound++;
                        else
                        {
                            // Check ROM dirs
                            var consoleTags = KnownBios.All
                                .Where(b => b.ConsoleDisplay == display)
                                .Select(b => b.Console).Distinct();
                            var romDirs = consoleTags
                                .SelectMany(tag => romDirsByConsole.TryGetValue(tag, out var dirs) ? dirs : Array.Empty<string>())
                                .Where(d => d != null)
                                .Distinct(StringComparer.OrdinalIgnoreCase);
                            if (romDirs.Any(dir => dir != null && BiosExists(
                                System.IO.Path.Combine(dir, System.IO.Path.GetFileName(entry.Filename)))))
                                catFound++;
                        }
                    }
                }

                // Category accordion header
                var catBody = new StackPanel { Visibility = Visibility.Collapsed };
                var catChevron = new TextBlock
                {
                    Text = "▸", FontSize = 14,
                    Foreground = _brushText,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(0)
                };

                var catHeaderGrid = new Grid { Cursor = Cursors.Hand };
                catHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                catHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                catHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                catHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Grid.SetColumn(catChevron, 0);

                var catLabel = new TextBlock
                {
                    Text = category,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _brushText,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(catLabel, 1);

                var catSummary = new TextBlock
                {
                    Text = $"{activeDisplays.Count} {(activeDisplays.Count == 1 ? "system" : "systems")}",
                    FontSize = 11,
                    Foreground = _brushTextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                Grid.SetColumn(catSummary, 2);

                var catBadge = new Border
                {
                    Background = catFound == catTotal && catTotal > 0
                        ? new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58))
                        : catFound > 0
                            ? new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xA5, 0x00))
                            : new SolidColorBrush(Color.FromArgb(0x22, 0x88, 0x88, 0x88)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                catBadge.Child = new TextBlock
                {
                    Text = $"{catFound}/{catTotal}",
                    FontSize = 10,
                    Foreground = catFound == catTotal && catTotal > 0
                        ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                        : catFound > 0
                            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00))
                            : _brushTextMuted
                };
                Grid.SetColumn(catBadge, 3);

                catHeaderGrid.Children.Add(catChevron);
                catHeaderGrid.Children.Add(catLabel);
                catHeaderGrid.Children.Add(catSummary);
                catHeaderGrid.Children.Add(catBadge);

                var catHeaderBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x21)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 12, 14, 12),
                    Margin = new Thickness(0, 6, 0, 0)
                };
                catHeaderBorder.Child = catHeaderGrid;

                var capturedCatBody = catBody;
                var capturedCatChevron = catChevron;
                catHeaderBorder.MouseLeftButtonUp += (_, _) =>
                {
                    bool expanding = capturedCatBody.Visibility == Visibility.Collapsed;
                    capturedCatBody.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
                    ((RotateTransform)capturedCatChevron.RenderTransform).Angle = expanding ? 90 : 0;
                };

                BiosPanel.Children.Add(catHeaderBorder);

                // Console accordions inside the category
                foreach (string consoleDisplay in activeDisplays)
                {
                    var entries = biosGroups[consoleDisplay];

                    // ROM dirs for this console group
                    var consoleTags2 = entries.Select(b => b.Console).Distinct();
                    var romDirs2 = consoleTags2
                        .SelectMany(tag => romDirsByConsole.TryGetValue(tag, out var dirs) ? dirs : Array.Empty<string>())
                        .Where(d => d != null)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    // Count found for this console
                    int consoleFound = 0;
                    foreach (var entry in entries)
                    {
                        string path = System.IO.Path.Combine(sysDir, entry.Filename);
                        if (BiosExists(path) || romDirs2.Any(dir => dir != null &&
                            BiosExists(System.IO.Path.Combine(dir, System.IO.Path.GetFileName(entry.Filename)))))
                            consoleFound++;
                    }

                    // Single-BIOS consoles: show the file inline without a nested accordion
                    if (entries.Count == 1)
                    {
                        var inlineCard = new Border
                        {
                            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1C)),
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(12, 8, 14, 8),
                            Margin = new Thickness(12, 2, 0, 2)
                        };
                        var inlineGrid = new Grid();
                        inlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        inlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var inlineRow = (FrameworkElement)BuildBiosRow(entries[0], sysDir, romDirs2!);
                        inlineRow.Margin = new Thickness(0);
                        Grid.SetColumn(inlineRow, 0);

                        var inlineBadge = new Border
                        {
                            Background = consoleFound == 1
                                ? new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58))
                                : new SolidColorBrush(Color.FromArgb(0x22, 0xE0, 0x35, 0x35)),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(6, 2, 6, 2),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        inlineBadge.Child = new TextBlock
                        {
                            Text = consoleFound == 1 ? "found" : "missing",
                            FontSize = 10,
                            Foreground = consoleFound == 1
                                ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                                : new SolidColorBrush(Color.FromRgb(0xE0, 0x35, 0x35))
                        };
                        Grid.SetColumn(inlineBadge, 1);

                        inlineGrid.Children.Add(inlineRow);
                        inlineGrid.Children.Add(inlineBadge);
                        inlineCard.Child = inlineGrid;
                        catBody.Children.Add(inlineCard);
                        continue;
                    }

                    // Multi-BIOS consoles: nested accordion
                    var consoleBody = new StackPanel { Visibility = Visibility.Collapsed };
                    var consoleChevron = new TextBlock
                    {
                        Text = "▸", FontSize = 12,
                        Foreground = _brushTextMuted,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0),
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        RenderTransform = new RotateTransform(0)
                    };

                    var consoleHeaderGrid = new Grid { Cursor = Cursors.Hand };
                    consoleHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    consoleHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    consoleHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    Grid.SetColumn(consoleChevron, 0);

                    var consoleLbl = new TextBlock
                    {
                        Text = consoleDisplay,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = _brushText,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(consoleLbl, 1);

                    var consoleBadge = new Border
                    {
                        Background = consoleFound == entries.Count
                            ? new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58))
                            : consoleFound > 0
                                ? new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xA5, 0x00))
                                : new SolidColorBrush(Color.FromArgb(0x22, 0xE0, 0x35, 0x35)),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    consoleBadge.Child = new TextBlock
                    {
                        Text = consoleFound == entries.Count
                            ? $"{entries.Count} found"
                            : consoleFound > 0
                                ? $"{consoleFound}/{entries.Count} found"
                                : "missing",
                        FontSize = 10,
                        Foreground = consoleFound == entries.Count
                            ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                            : consoleFound > 0
                                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00))
                                : new SolidColorBrush(Color.FromRgb(0xE0, 0x35, 0x35))
                    };
                    Grid.SetColumn(consoleBadge, 2);

                    consoleHeaderGrid.Children.Add(consoleChevron);
                    consoleHeaderGrid.Children.Add(consoleLbl);
                    consoleHeaderGrid.Children.Add(consoleBadge);

                    var consoleHeaderBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1C)),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 8, 14, 8),
                        Margin = new Thickness(12, 2, 0, 2)
                    };
                    consoleHeaderBorder.Child = consoleHeaderGrid;

                    var capturedConsoleBody = consoleBody;
                    var capturedConsoleChevron = consoleChevron;
                    consoleHeaderBorder.MouseLeftButtonUp += (_, _) =>
                    {
                        bool expanding = capturedConsoleBody.Visibility == Visibility.Collapsed;
                        capturedConsoleBody.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
                        ((RotateTransform)capturedConsoleChevron.RenderTransform).Angle = expanding ? 90 : 0;
                    };

                    catBody.Children.Add(consoleHeaderBorder);

                    // BIOS file rows inside the console accordion
                    var bodyCard = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x19)),
                        CornerRadius = new CornerRadius(0, 0, 6, 6),
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(24, 0, 0, 4)
                    };
                    var bodyStack = new StackPanel();

                    foreach (var entry in entries)
                        bodyStack.Children.Add(BuildBiosRow(entry, sysDir, romDirs2!));

                    bodyCard.Child = bodyStack;
                    consoleBody.Children.Add(bodyCard);
                    catBody.Children.Add(consoleBody);
                }

                BiosPanel.Children.Add(catBody);
            }
            return true;
        }

        private UIElement BuildBiosRow(BiosEntry entry, string sysDir, string[]? romDirs = null)
        {
            // Prefer the cached existence set so per-row paint doesn't re-hit
            // the FS — the set is populated by BuildBiosPanelAsync just above.
            // Fall back to live File.Exists if the cache is unavailable
            // (e.g., the row is built from a code path that didn't warm it).
            HashSet<string>? cache = null;
            try { cache = Emutastic.Services.PreferencesCache.GetBiosScanSnapshot()?.ExistingPathsLower; } catch { }
            bool Exists(string p) => cache != null ? cache.Contains(p) : System.IO.File.Exists(p);

            string fullPath = System.IO.Path.Combine(sysDir, entry.Filename);
            bool existsInSysDir = Exists(fullPath);
            bool existsInRomDir = !existsInSysDir && (romDirs?.Any(dir =>
                Exists(System.IO.Path.Combine(dir, System.IO.Path.GetFileName(entry.Filename)))) == true);
            bool exists = existsInSysDir || existsInRomDir;

            bool verified = false;
            string? foundPath = existsInSysDir ? fullPath
                : existsInRomDir ? romDirs!.Select(dir =>
                    System.IO.Path.Combine(dir, System.IO.Path.GetFileName(entry.Filename)))
                    .FirstOrDefault(Exists)
                : null;
            if (foundPath != null && entry.Md5 != null)
                verified = VerifyMd5(foundPath, entry.Md5);
            else if (exists)
                verified = true; // presence-only check

            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x21)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 200 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Status icon
            var icon = new TextBlock
            {
                Text = verified ? "✓" : "⚠",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = verified
                    ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                    : new SolidColorBrush(Color.FromRgb(0xE0, 0x35, 0x35)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);

            // Filename
            var filename = new TextBlock
            {
                Text = System.IO.Path.GetFileName(entry.Filename),
                FontSize = 13,
                Foreground = _brushText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 16, 0)
            };
            Grid.SetColumn(filename, 1);

            // Description
            string descText = entry.Description;
            if (!exists) descText += (descText.Length > 0 ? " — " : "") + "Missing";
            else if (entry.Md5 != null && !verified) descText += (descText.Length > 0 ? " — " : "") + "Hash mismatch";
            else if (existsInRomDir) descText += (descText.Length > 0 ? " — " : "") + "found in game folder";
            var desc = new TextBlock
            {
                Text = descText,
                FontSize = 12,
                Foreground = _brushTextMuted,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(desc, 2);

            // Size
            var size = new TextBlock
            {
                Text = entry.ExpectedSize > 0 ? $"{entry.ExpectedSize / 1024} KB" : "—",
                FontSize = 12,
                Foreground = _brushTextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0)
            };
            Grid.SetColumn(size, 3);

            // Hash — truncated by default, click to reveal full MD5
            string shortHash = entry.Md5 != null ? entry.Md5[..8] + "…" : "any";
            string fullHash  = entry.Md5 != null ? entry.Md5 : "any";
            bool expanded = false;
            var hash = new TextBlock
            {
                Text = $"MD5: {shortHash}",
                FontSize = 11,
                Foreground = _brushTextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
                FontFamily = new FontFamily("Consolas"),
                Cursor = entry.Md5 != null ? System.Windows.Input.Cursors.Hand : null,
                ToolTip = entry.Md5 != null ? "Click to reveal full MD5" : null
            };
            if (entry.Md5 != null)
            {
                hash.MouseLeftButtonUp += (_, _) =>
                {
                    expanded = !expanded;
                    hash.Text = expanded ? $"MD5: {fullHash}" : $"MD5: {shortHash}";
                    hash.ToolTip = expanded ? "Click to hide" : "Click to reveal full MD5";
                };
            }
            Grid.SetColumn(hash, 4);

            grid.Children.Add(icon);
            grid.Children.Add(filename);
            grid.Children.Add(desc);
            grid.Children.Add(size);
            grid.Children.Add(hash);
            row.Child = grid;
            return row;
        }

        private static bool VerifyMd5(string path, string expectedMd5)
        {
            try
            {
                using var md5 = System.Security.Cryptography.MD5.Create();
                using var stream = System.IO.File.OpenRead(path);
                byte[] hash = md5.ComputeHash(stream);
                string actual = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                return string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string? ComputeMd5(string path)
        {
            try
            {
                using var md5 = System.Security.Cryptography.MD5.Create();
                using var stream = System.IO.File.OpenRead(path);
                return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch { return null; }
        }

        // ── BIOS drag-drop ────────────────────────────────────────────────────
        // Drop any file on the System Files panel — it gets identified by MD5 →
        // filename+size → filename, and copied to the system folder with the
        // canonical name.
        private void BiosPanel_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                ? System.Windows.DragDropEffects.Copy
                : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void BiosPanel_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths) return;

            string sysDir = AppPaths.GetFolder("System");
            int imported = 0, skipped = 0;
            var messages = new System.Collections.Generic.List<string>();

            foreach (string src in paths)
            {
                if (!System.IO.File.Exists(src)) { skipped++; continue; }
                ProcessDroppedFile(src, sysDir, messages, ref imported, ref skipped);
            }

            if (imported > 0)
            {
                PreferencesCache.InvalidateBiosScan();
                InvalidateSection(PrefSection.SystemFiles);
                _ = LoadSectionAsync(PrefSection.SystemFiles); // refresh status badges
            }

            string summary = imported > 0
                ? $"Imported {imported} file{(imported == 1 ? "" : "s")}"
                  + (skipped > 0 ? $" ({skipped} skipped)" : "")
                : "No files imported";
            System.Windows.MessageBox.Show(
                summary + "\n\n" + string.Join("\n", messages),
                "BIOS drop",
                System.Windows.MessageBoxButton.OK,
                imported > 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        }

        // Returns the best KnownBios match for (filename, size, md5). md5 may be null
        // when the caller hasn't computed it yet — tier 1 is skipped in that case.
        private static BiosEntry? MatchKnownBios(string entryName, long size, string? md5)
        {
            if (md5 != null)
            {
                var hashMatch = KnownBios.All.FirstOrDefault(b =>
                    b.Md5 != null && string.Equals(b.Md5, md5, StringComparison.OrdinalIgnoreCase));
                if (hashMatch != null) return hashMatch;
            }

            var sizeMatch = KnownBios.All.FirstOrDefault(b =>
                string.Equals(System.IO.Path.GetFileName(b.Filename), entryName, StringComparison.OrdinalIgnoreCase)
                && (b.ExpectedSize == 0 || b.ExpectedSize == size));
            if (sizeMatch != null) return sizeMatch;

            return KnownBios.All.FirstOrDefault(b =>
                string.Equals(System.IO.Path.GetFileName(b.Filename), entryName, StringComparison.OrdinalIgnoreCase));
        }

        private static void CopyEntryToSystem(BiosEntry match, System.IO.Stream source, string sysDir)
        {
            string destPath = System.IO.Path.Combine(sysDir, match.Filename);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destPath)!);
            using var dest = System.IO.File.Create(destPath);
            source.CopyTo(dest);
        }

        private void ProcessDroppedFile(string src, string sysDir,
            System.Collections.Generic.List<string> messages,
            ref int imported, ref int skipped)
        {
            long size;
            try { size = new System.IO.FileInfo(src).Length; }
            catch { skipped++; return; }

            string srcName = System.IO.Path.GetFileName(src);
            string srcExt  = System.IO.Path.GetExtension(srcName);
            bool isArchive = srcExt.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                          || srcExt.Equals(".7z",  StringComparison.OrdinalIgnoreCase)
                          || srcExt.Equals(".rar", StringComparison.OrdinalIgnoreCase);

            // For archives: peek inside first. PS1 BIOS bundles, etc. typically ship zipped.
            // If any entry matches a known BIOS, extract THAT. Fall back to treating the archive itself
            // as the BIOS (covers neogeo.zip / cdibios.zip cases where the archive IS the expected file).
            if (isArchive)
            {
                int extractedHere = 0;
                try
                {
                    using var archive = Emutastic.Services.Archives.RomArchive.Open(src);
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.IsDirectory || entry.Key == null) continue;
                        string entryName = System.IO.Path.GetFileName(entry.Key);
                        if (string.IsNullOrEmpty(entryName)) continue;

                        // Tier 1: MD5 from entry stream (only if any KnownBios entry pins an MD5)
                        string? entryMd5 = null;
                        if (KnownBios.All.Any(b => b.Md5 != null))
                        {
                            try
                            {
                                using var mdStream = entry.OpenEntryStream();
                                using var md5 = System.Security.Cryptography.MD5.Create();
                                entryMd5 = BitConverter.ToString(md5.ComputeHash(mdStream))
                                    .Replace("-", "").ToLowerInvariant();
                            }
                            catch { }
                        }

                        var match = MatchKnownBios(entryName, entry.Size, entryMd5);
                        if (match == null) continue;

                        try
                        {
                            using var es = entry.OpenEntryStream();
                            CopyEntryToSystem(match, es, sysDir);
                            messages.Add($"✓ {srcName} → {System.IO.Path.GetFileName(match.Filename)} ({match.ConsoleDisplay})");
                            imported++;
                            extractedHere++;
                        }
                        catch (Exception ex)
                        {
                            messages.Add($"⚠ {srcName}:{entryName}: {ex.Message}");
                            skipped++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    messages.Add($"⚠ {srcName}: archive open failed ({ex.Message})");
                }

                if (extractedHere > 0) return;
                // No entries matched — fall through to treat the archive itself as a BIOS
                // (e.g. neogeo.zip, cdibios.zip).
            }

            // Loose file (or archive that IS the BIOS)
            string? fileMd5 = null;
            if (KnownBios.All.Any(b => b.Md5 != null)) fileMd5 = ComputeMd5(src);
            var fileMatch = MatchKnownBios(srcName, size, fileMd5);

            string destPath;
            string label;
            if (fileMatch != null)
            {
                destPath = System.IO.Path.Combine(sysDir, fileMatch.Filename);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destPath)!);
                label = $"{System.IO.Path.GetFileName(fileMatch.Filename)} → {fileMatch.ConsoleDisplay}";
            }
            else
            {
                messages.Add($"⚠ {srcName}: not recognized as a known BIOS ({size / 1024} KB)");
                skipped++;
                return;
            }

            try
            {
                System.IO.File.Copy(src, destPath, overwrite: true);
                messages.Add($"✓ {label}");
                imported++;
            }
            catch (Exception ex)
            {
                messages.Add($"⚠ {srcName}: {ex.Message}");
                skipped++;
            }
        }

        // ── Cores panel ───────────────────────────────────────────────────────

        // Per-console plugin/option definitions
        private static readonly Dictionary<string, List<(string Key, string Label, List<string> Values, string Default, Dictionary<string, string> Descs)>> CoreSpecificOptions = new()
        {
        };

        // ── Core downloader ───────────────────────────────────────────────────
        private readonly CoreDownloadService _downloader = new();
        private CancellationTokenSource? _downloadAllCts;

        // Console-to-category mapping for accordion grouping
        private static readonly (string Category, string[] Consoles)[] ConsoleCategories =
        {
            ("Nintendo",  new[] { "NES", "FDS", "SNES", "N64", "GameCube", "GB", "GBC", "GBA", "NDS", "3DS", "VirtualBoy" }),
            ("Sega",      new[] { "Genesis", "SegaCD", "Sega32X", "Saturn", "SMS", "GameGear", "SG1000", "Dreamcast" }),
            ("Sony",      new[] { "PS1", "PS2", "PSP" }),
            ("NEC",       new[] { "TG16", "TGCD" }),
            ("Atari",     new[] { "Atari2600", "Atari7800", "Jaguar" }),
            ("Arcade",    new[] { "Arcade", "NeoGeo", "NeoCD" }),
            ("Other",     new[] { "NGP", "ColecoVision", "Vectrex", "3DO", "CDi" }),
        };

        private void BuildCoresPanel() => _ = BuildCoresPanelAsync();

        /// <returns>
        /// true when the panel was actually populated; false when the build
        /// bailed (window not loaded / section switched away mid-scan) so the
        /// caller must not mark the section as loaded.
        /// </returns>
        private async Task<bool> BuildCoresPanelAsync()
        {
            CoresListPanel.Children.Clear();
            string coresFolder = Emutastic.AppPaths.GetCoresFolder();
            var prefs = _configService.GetCorePreferences();

            // Single Directory.EnumerateFiles in place of ~100+ File.Exists.
            var installedDlls = await PreferencesCache
                .GetInstalledCoresAsync(coresFolder)
                .ConfigureAwait(true);
            if (!IsLoaded || _activeSection != PrefSection.Cores) return false;
            CoresListPanel.Children.Clear(); // strip "Loading…" placeholder
            bool IsInstalled(string dll) => installedDlls.Contains(dll);

            // ── Download All button + progress ──
            var recommended = CoreDownloadService.Catalog.Where(c => c.Recommended).ToList();
            int installedCount = recommended.Count(c => IsInstalled(c.FileName));

            var dlAllRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            dlAllRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            dlAllRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dlAllRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dlAllBtn = new Button
            {
                Content = "Download All Recommended",
                Style = (Style)FindResource("AccentButton"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dlAllBtn, 0);

            var dlSummary = new TextBlock
            {
                Text = $"{installedCount} of {recommended.Count} installed",
                FontSize = 11,
                Foreground = _brushTextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(dlSummary, 1);

            // Visible only when CheckAllForUpdatesAsync returns ≥1 stale core.
            // Triggered click runs the per-row download path for each, in
            // parallel, reusing the existing per-row progress bars.
            var updateAllBtn = new Button
            {
                Content = "Update All",
                Style = (Style)FindResource("AccentButton"),
                Margin = new Thickness(8, 0, 0, 0),
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Re-download every core that has a newer build available"
            };
            Grid.SetColumn(updateAllBtn, 2);

            dlAllRow.Children.Add(dlAllBtn);
            dlAllRow.Children.Add(dlSummary);
            dlAllRow.Children.Add(updateAllBtn);
            CoresListPanel.Children.Add(dlAllRow);

            // Per-DLL "Update available" pill, populated below as rows are built
            // and revealed once CheckAllForUpdatesAsync returns.
            var updatePillMap = new Dictionary<string, TextBlock>();

            // Awaitable per-row download closures used by the Update All flow
            // so it can Task.WhenAll all in-flight downloads, collect outcomes
            // (success/failure + error message), and rebuild the panel ONCE at
            // the end. Single ⟳ click still goes through the dl button click
            // → StartSingleDownload path unchanged.
            var dlActionMap = new Dictionary<string, Func<Task<(bool ok, string? error)>>>();

            var allProgressBar = new ProgressBar
            {
                Height = 4, Minimum = 0, Maximum = 100, Value = 0,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 4)
            };
            var allStatusText = new TextBlock
            {
                FontSize = 11, Foreground = _brushTextMuted,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 8)
            };
            CoresListPanel.Children.Add(allProgressBar);
            CoresListPanel.Children.Add(allStatusText);

            // Track per-core download UI for "Download All"
            var dlRowMap = new Dictionary<string, (ProgressBar Bar, TextBlock Status, Button Btn)>();

            // ── Two-level accordion: Category → Console → Cores ──
            foreach (var (category, consoleList) in ConsoleCategories)
            {
                var categoryConsoles = consoleList
                    .Where(c => Services.CoreManager.ConsoleCoreMap.ContainsKey(c))
                    .ToList();
                if (categoryConsoles.Count == 0) continue;

                // Count installed across entire category
                int catInstalled = 0, catTotal = 0;
                foreach (string c in categoryConsoles)
                {
                    var cores = Services.CoreManager.ConsoleCoreMap[c];
                    catTotal += cores.Length;
                    catInstalled += cores.Count(dll => IsInstalled(dll));
                }

                // Category accordion header
                var catBody = new StackPanel { Visibility = Visibility.Collapsed };
                var catChevron = new TextBlock
                {
                    Text = "▸", FontSize = 14,
                    Foreground = _brushText,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(0)
                };

                var catHeaderGrid = new Grid { Cursor = Cursors.Hand };
                catHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                catHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                catHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                catHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Grid.SetColumn(catChevron, 0);

                var catLabel = new TextBlock
                {
                    Text = category,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _brushText,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(catLabel, 1);

                var catConsoleSummary = new TextBlock
                {
                    Text = $"{categoryConsoles.Count} {(categoryConsoles.Count == 1 ? "system" : "systems")}",
                    FontSize = 11,
                    Foreground = _brushTextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                Grid.SetColumn(catConsoleSummary, 2);

                var catBadge = new Border
                {
                    Background = catInstalled > 0
                        ? new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58))
                        : new SolidColorBrush(Color.FromArgb(0x22, 0x88, 0x88, 0x88)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                catBadge.Child = new TextBlock
                {
                    Text = catInstalled == catTotal ? $"{catTotal}" : $"{catInstalled}/{catTotal}",
                    FontSize = 10,
                    Foreground = catInstalled > 0
                        ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                        : _brushTextMuted
                };
                Grid.SetColumn(catBadge, 3);

                catHeaderGrid.Children.Add(catChevron);
                catHeaderGrid.Children.Add(catLabel);
                catHeaderGrid.Children.Add(catConsoleSummary);
                catHeaderGrid.Children.Add(catBadge);

                var catHeaderBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x21)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 12, 14, 12),
                    Margin = new Thickness(0, 6, 0, 0)
                };
                catHeaderBorder.Child = catHeaderGrid;

                var capturedCatBody = catBody;
                var capturedCatChevron = catChevron;
                catHeaderBorder.MouseLeftButtonUp += (_, _) =>
                {
                    bool expanding = capturedCatBody.Visibility == Visibility.Collapsed;
                    capturedCatBody.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
                    ((RotateTransform)capturedCatChevron.RenderTransform).Angle = expanding ? 90 : 0;
                };

                CoresListPanel.Children.Add(catHeaderBorder);

                // Console accordions inside the category body
                foreach (string consoleName in categoryConsoles)
                {
                    string[] candidates = Services.CoreManager.ConsoleCoreMap[consoleName];

                    // Build full core list: ALL candidates from ConsoleCoreMap + download catalog
                    var allCores = candidates
                        .Select(dll => new
                        {
                            Dll = dll,
                            Path = System.IO.Path.Combine(coresFolder, dll),
                            Friendly = FormatCoreName(dll),
                            Installed = IsInstalled(dll),
                            CatalogEntry = CoreDownloadService.Catalog.FirstOrDefault(
                                c => c.FileName.Equals(dll, StringComparison.OrdinalIgnoreCase))
                        })
                        .ToList();

                    int coreCount = allCores.Count;
                    int coreInstalled = allCores.Count(c => c.Installed);

                    // Determine preferred/active core name
                    string? savedPref = prefs.PreferredCores.TryGetValue(consoleName, out var p) ? p : null;
                    var activeCoreObj = allCores.FirstOrDefault(c => c.Dll == savedPref && c.Installed)
                        ?? allCores.FirstOrDefault(c => c.Installed);
                    string activeCoreName = activeCoreObj?.Friendly ?? "Not installed";

                    // ── Accordion header ──
                    var bodyPanel = new StackPanel { Visibility = Visibility.Collapsed };
                    var chevron = new TextBlock
                    {
                        Text = "▸",
                        FontSize = 12,
                        Foreground = _brushTextMuted,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0),
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        RenderTransform = new RotateTransform(0)
                    };

                    var headerGrid = new Grid
                    {
                        Margin = new Thickness(0, 0, 0, 0),
                        Cursor = Cursors.Hand
                    };
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // chevron
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // console name
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // active core
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // count badge

                    Grid.SetColumn(chevron, 0);

                    var consoleLbl = new TextBlock
                    {
                        Text = consoleName,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = _brushText,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(consoleLbl, 1);

                    var activeLbl = new TextBlock
                    {
                        Text = activeCoreName,
                        FontSize = 11,
                        Foreground = _brushTextMuted,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0)
                    };
                    Grid.SetColumn(activeLbl, 2);

                    var countBadge = new Border
                    {
                        Background = coreInstalled > 0
                            ? new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58))
                            : new SolidColorBrush(Color.FromArgb(0x22, 0x88, 0x88, 0x88)),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    countBadge.Child = new TextBlock
                    {
                        Text = coreInstalled == coreCount
                            ? $"{coreCount}"
                            : $"{coreInstalled}/{coreCount}",
                        FontSize = 10,
                        Foreground = coreInstalled > 0
                            ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                            : _brushTextMuted
                    };
                    Grid.SetColumn(countBadge, 3);

                    headerGrid.Children.Add(chevron);
                    headerGrid.Children.Add(consoleLbl);
                    headerGrid.Children.Add(activeLbl);
                    headerGrid.Children.Add(countBadge);

                    var headerBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1C)),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 8, 14, 8),
                        Margin = new Thickness(12, 2, 0, 2)
                    };
                    headerBorder.Child = headerGrid;

                    // Toggle expand/collapse
                    var capturedBody = bodyPanel;
                    var capturedChevron = chevron;
                    headerBorder.MouseLeftButtonUp += (_, _) =>
                    {
                        bool expanding = capturedBody.Visibility == Visibility.Collapsed;
                        capturedBody.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
                        ((RotateTransform)capturedChevron.RenderTransform).Angle = expanding ? 90 : 0;
                    };

                    catBody.Children.Add(headerBorder);

                    // ── Accordion body ──
                    var bodyCard = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x19)),
                        CornerRadius = new CornerRadius(0, 0, 6, 6),
                        Padding = new Thickness(14, 8, 14, 10),
                        Margin = new Thickness(24, 0, 0, 4)
                    };
                    var bodyStack = new StackPanel();

                    // Core rows inside the accordion
                    var installedCores = allCores.Where(c => c.Installed).ToList();

                    for (int i = 0; i < allCores.Count; i++)
                    {
                        var core = allCores[i];

                        var coreRow = new Grid { Margin = new Thickness(0, i > 0 ? 6 : 0, 0, 0) };
                        coreRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // preferred indicator
                        coreRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
                        coreRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // version
                        coreRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // buttons

                        // Preferred indicator (filled circle for preferred, empty for others)
                        bool isPreferred = core.Installed && core.Dll == (activeCoreObj?.Dll ?? "");
                        var prefIndicator = new TextBlock
                        {
                            Text = core.Installed
                                ? (isPreferred ? "●" : "○")
                                : " ",
                            FontSize = 10,
                            Foreground = isPreferred
                                ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                                : _brushTextMuted,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 8, 0),
                            Cursor = core.Installed && installedCores.Count > 1 ? Cursors.Hand : Cursors.Arrow,
                            ToolTip = core.Installed && installedCores.Count > 1
                                ? "Click to set as preferred core"
                                : null
                        };
                        Grid.SetColumn(prefIndicator, 0);

                        // Click to set preferred
                        if (core.Installed && installedCores.Count > 1)
                        {
                            string capturedDll = core.Dll;
                            string capturedConsole = consoleName;
                            prefIndicator.MouseLeftButtonUp += (_, e) =>
                            {
                                var current = _configService.GetCorePreferences();
                                current.PreferredCores[capturedConsole] = capturedDll;
                                _configService.SetCorePreferences(current);
                                _ = _configService.SaveAsync();
                                InvalidateSection(PrefSection.Cores);
                                _ = LoadSectionAsync(PrefSection.Cores);
                                e.Handled = true;
                            };
                        }

                        // Core name
                        var nameBlock = new TextBlock
                        {
                            Text = core.Friendly,
                            FontSize = 12,
                            FontWeight = core.Installed ? FontWeights.SemiBold : FontWeights.Normal,
                            Foreground = core.Installed ? _brushText : _brushTextMuted,
                            VerticalAlignment = VerticalAlignment.Center,
                            ToolTip = core.Dll // DLL name on hover
                        };
                        Grid.SetColumn(nameBlock, 1);

                        // Version (installed only)
                        var versionBlock = new TextBlock
                        {
                            Text = core.Installed ? GetCoreVersion(core.Path) : "",
                            FontSize = 10,
                            Foreground = _brushTextMuted,
                            VerticalAlignment = VerticalAlignment.Center,
                            FontFamily = new FontFamily("Consolas"),
                            Margin = new Thickness(8, 0, 8, 0)
                        };
                        Grid.SetColumn(versionBlock, 2);

                        // Buttons
                        var btnPanel = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            VerticalAlignment = VerticalAlignment.Center
                        };

                        // PS2: renderer picker right on the core row so the GS
                        // backend (DirectX / OpenGL) can be switched WITHOUT
                        // launching a game — the recovery path when a renderer
                        // crashes on boot. Writes pcsx2_renderer to the same
                        // values.json the in-game cog and the PS2 handler read;
                        // applies on the next launch.
                        if (core.Installed && core.Dll.Equals("pcsx2_libretro.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            var rendCombo = new ComboBox
                            {
                                Style = (Style)FindResource("PrefComboBox"),
                                Width = 110,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(0, 0, 8, 0),
                                ToolTip = "GS renderer — applies on next launch",
                                ItemsSource = new[] { "DirectX", "OpenGL" }
                            };
                            string curRend = App.CoreOptions.LoadValues("pcsx2_libretro")
                                .TryGetValue("pcsx2_renderer", out var rv) ? rv : "D3D11";
                            rendCombo.SelectedIndex =
                                curRend.Equals("OpenGL", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                            rendCombo.SelectionChanged += (_, _) =>
                            {
                                string val = rendCombo.SelectedIndex == 1 ? "OpenGL" : "D3D11";
                                App.CoreOptions.SaveValues("pcsx2_libretro",
                                    new Dictionary<string, string> { ["pcsx2_renderer"] = val });
                            };
                            btnPanel.Children.Add(rendCombo);
                        }

                        if (core.CatalogEntry != null)
                        {
                            bool hasBackup = core.Installed && CoreDownloadService.HasBackup(coresFolder, core.Dll);

                            var rowProgress = new ProgressBar
                            {
                                Height = 3, Width = 60, Minimum = 0, Maximum = 100, Value = 0,
                                Visibility = Visibility.Collapsed,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(0, 0, 6, 0)
                            };
                            var rowStatus = new TextBlock
                            {
                                FontSize = 10, Foreground = _brushTextMuted,
                                Visibility = Visibility.Collapsed,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(0, 0, 6, 0)
                            };

                            var revertBtn = new Button
                            {
                                Content = "Revert",
                                Style = (Style)FindResource("SmallOutlineButton"),
                                Margin = new Thickness(0, 0, 4, 0),
                                Visibility = hasBackup ? Visibility.Visible : Visibility.Collapsed,
                                ToolTip = "Restore the previous version of this core"
                            };
                            string capturedFileName = core.Dll;
                            var capturedRevertStatus = rowStatus;
                            revertBtn.Click += (_, _) =>
                            {
                                try
                                {
                                    CoreDownloadService.Revert(coresFolder, capturedFileName);
                                    revertBtn.Visibility = Visibility.Collapsed;
                                    capturedRevertStatus.Text = "Reverted";
                                    capturedRevertStatus.Visibility = Visibility.Visible;
                                }
                                catch (Exception ex)
                                {
                                    capturedRevertStatus.Text = $"Failed: {ex.Message}";
                                    capturedRevertStatus.Visibility = Visibility.Visible;
                                }
                            };

                            var badge = MakeBadge(core.Installed);
                            var dlBtn = new Button
                            {
                                Content = core.Installed ? "⟳" : "↓",
                                Style = (Style)FindResource("SmallOutlineButton"),
                                Width = 28, Height = 24,
                                Padding = new Thickness(0),
                                ToolTip = core.Installed ? "Re-download" : "Download",
                                VerticalAlignment = VerticalAlignment.Center
                            };

                            dlBtn.Click += (_, _) => StartSingleDownload(
                                core.CatalogEntry, coresFolder, badge, rowProgress, rowStatus, dlBtn, revertBtn);

                            dlRowMap[core.Dll] = (rowProgress, rowStatus, dlBtn);

                            // Capture all per-row params so Update All can await this
                            // download (the regular Click handler goes through async void).
                            var capturedEntry = core.CatalogEntry;
                            var capturedBadge = badge;
                            var capturedBar = rowProgress;
                            var capturedStatus = rowStatus;
                            var capturedDlBtn = dlBtn;
                            var capturedRevert = revertBtn;
                            dlActionMap[core.Dll] = () => StartSingleDownloadAsync(
                                capturedEntry, coresFolder, capturedBadge, capturedBar,
                                capturedStatus, capturedDlBtn, capturedRevert);

                            // "Update available" pill, hidden until the async
                            // CheckAllForUpdatesAsync result lands and reveals it.
                            // Only meaningful for cores that are actually installed —
                            // CheckAsync returns NotInstalled otherwise.
                            var updatePill = new TextBlock
                            {
                                Text = "Update available",
                                FontSize = 10,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(0, 0, 8, 0),
                                Visibility = Visibility.Collapsed
                            };
                            if (core.Installed)
                                updatePillMap[core.Dll] = updatePill;

                            btnPanel.Children.Add(updatePill);
                            btnPanel.Children.Add(rowProgress);
                            btnPanel.Children.Add(rowStatus);
                            btnPanel.Children.Add(revertBtn);
                            btnPanel.Children.Add(dlBtn);
                        }
                        Grid.SetColumn(btnPanel, 3);

                        coreRow.Children.Add(prefIndicator);
                        coreRow.Children.Add(nameBlock);
                        coreRow.Children.Add(versionBlock);
                        coreRow.Children.Add(btnPanel);
                        bodyStack.Children.Add(coreRow);
                    }

                    // ── Console-specific plugin options ──
                    if (CoreSpecificOptions.TryGetValue(consoleName, out var options))
                    {
                        prefs.CoreOptionOverrides.TryGetValue(consoleName, out var savedOverrides);

                        foreach (var opt in options)
                        {
                            bodyStack.Children.Add(new Rectangle
                            {
                                Height = 1,
                                Fill = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x33)),
                                Margin = new Thickness(0, 8, 0, 8)
                            });

                            var optRow = new StackPanel { Orientation = Orientation.Horizontal };
                            optRow.Children.Add(new TextBlock
                            {
                                Text = opt.Label,
                                FontSize = 12,
                                Foreground = _brushText,
                                VerticalAlignment = VerticalAlignment.Center,
                                Width = 140
                            });

                            var optStack = new StackPanel();
                            var optCombo = new ComboBox
                            {
                                Style = (Style)FindResource("PrefComboBox"),
                                Width = 200,
                                ItemsSource = opt.Values
                            };

                            string currentVal = savedOverrides != null && savedOverrides.TryGetValue(opt.Key, out var sv) ? sv : opt.Default;
                            optCombo.SelectedIndex = opt.Values.IndexOf(opt.Values.Contains(currentVal) ? currentVal : opt.Default);

                            var descBlock = new TextBlock
                            {
                                Text = opt.Descs.TryGetValue(currentVal, out var d) ? d : "",
                                FontSize = 11,
                                Foreground = _brushTextMuted,
                                Margin = new Thickness(0, 4, 0, 0),
                                TextWrapping = TextWrapping.Wrap,
                                Width = 200
                            };

                            string capturedOptConsole = consoleName;
                            optCombo.SelectionChanged += (_, _) =>
                            {
                                string val = opt.Values[optCombo.SelectedIndex];
                                descBlock.Text = opt.Descs.TryGetValue(val, out var desc) ? desc : "";
                                var current = _configService.GetCorePreferences();
                                if (!current.CoreOptionOverrides.TryGetValue(capturedOptConsole, out var overrides))
                                    current.CoreOptionOverrides[capturedOptConsole] = overrides = new();
                                overrides[opt.Key] = val;
                                _configService.SetCorePreferences(current);
                                _ = _configService.SaveAsync();
                            };

                            optStack.Children.Add(optCombo);
                            optStack.Children.Add(descBlock);
                            optRow.Children.Add(optStack);
                            bodyStack.Children.Add(optRow);
                        }
                    }

                    bodyCard.Child = bodyStack;
                    bodyPanel.Children.Add(bodyCard);
                    catBody.Children.Add(bodyPanel);
                }

                CoresListPanel.Children.Add(catBody);
            }

            // ── "Download All" handler ──
            dlAllBtn.Click += async (_, _) =>
            {
                _downloadAllCts?.Cancel();
                _downloadAllCts = new CancellationTokenSource();
                var ct = _downloadAllCts.Token;

                dlAllBtn.IsEnabled = false;
                allProgressBar.Visibility = Visibility.Visible;
                allStatusText.Visibility = Visibility.Visible;

                int done = 0;
                foreach (var entry in recommended)
                {
                    if (ct.IsCancellationRequested) break;
                    allStatusText.Text = $"Downloading {entry.DisplayName}… ({done + 1}/{recommended.Count})";

                    if (dlRowMap.TryGetValue(entry.FileName, out var r))
                    {
                        r.Bar.Visibility = Visibility.Visible;
                        r.Status.Visibility = Visibility.Visible;
                        r.Btn.IsEnabled = false;
                    }

                    try
                    {
                        var prog = new Progress<int>(v =>
                        {
                            if (dlRowMap.TryGetValue(entry.FileName, out var r2)) r2.Bar.Value = v;
                            allProgressBar.Value = (done * 100 + v) / recommended.Count;
                        });
                        await _downloader.DownloadAsync(entry, coresFolder, prog, ct);

                        if (dlRowMap.TryGetValue(entry.FileName, out var r3))
                        {
                            r3.Bar.Visibility = Visibility.Collapsed;
                            r3.Status.Text = "Done";
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        if (dlRowMap.TryGetValue(entry.FileName, out var r4))
                            r4.Status.Text = $"Error: {ex.Message}";
                    }

                    done++;
                }

                allProgressBar.Value = 100;
                allStatusText.Text = ct.IsCancellationRequested
                    ? "Cancelled."
                    : $"Done — {done}/{recommended.Count} cores downloaded.";
                dlAllBtn.IsEnabled = true;
                PreferencesCache.InvalidateCores();
                InvalidateSection(PrefSection.Cores);
                _ = LoadSectionAsync(PrefSection.Cores);
            };

            BuildExtrasSection();

            // ── Async stale-core check (~14d threshold) ──────────────────────
            // Reveal "Update available" pills on rows + the Update All button
            // once the per-core HEAD checks return. Cached at class level so
            // re-opening this section doesn't re-hit the buildbot every time.
            _ = DecorateUpdatesAvailableAsync(coresFolder, updatePillMap, updateAllBtn, dlActionMap);
            return true;
        }

        private async Task DecorateUpdatesAvailableAsync(
            string coresFolder,
            Dictionary<string, TextBlock> updatePillMap,
            Button updateAllBtn,
            Dictionary<string, Func<Task<(bool ok, string? error)>>> dlActionMap)
        {
            try
            {
                var updates = await PreferencesCache.GetCoreUpdatesAsync(
                    _downloader, coresFolder, _windowCts.Token).ConfigureAwait(true);
                if (!IsLoaded || _activeSection != PrefSection.Cores) return;
                if (updates.Count == 0) return;

                foreach (var entry in updates)
                {
                    if (updatePillMap.TryGetValue(entry.FileName, out var pill))
                        pill.Visibility = Visibility.Visible;
                }

                updateAllBtn.Visibility = Visibility.Visible;
                updateAllBtn.Click += async (_, _) =>
                {
                    var mvm = (Application.Current.MainWindow as MainWindow)?.DataContext as Emutastic.ViewModels.MainViewModel;
                    int total = updates.Count;
                    int done = 0;
                    var failures = new System.Collections.Concurrent.ConcurrentBag<(string fileName, string error)>();

                    if (mvm != null)
                    {
                        mvm.IsCoreUpdating = true;
                        mvm.CoreUpdateText = $"Updating cores… 0 of {total}";
                        mvm.CoreUpdateProgressPercent = 0;
                    }

                    // Suppress per-download panel rebuilds — each successful
                    // download would otherwise call BuildCoresPanel() while
                    // peer downloads are still writing into the now-orphaned
                    // row controls. Single rebuild at the end covers everyone.
                    updateAllBtn.IsEnabled = false;
                    _suppressPanelRebuildDuringDownload = true;
                    try
                    {
                        var tasks = updates.Select(async e =>
                        {
                            // Prefer the per-row closure (preserves progress UI in the
                            // Cores tab). Fall back to a direct download for cores that
                            // are in the catalog but not iterated in BuildCoresPanel
                            // (config drift between Catalog / ConsoleCoreMap / ConsoleCategories).
                            bool ok;
                            string? err;
                            if (dlActionMap.TryGetValue(e.FileName, out var f))
                            {
                                (ok, err) = await f();
                            }
                            else
                            {
                                try
                                {
                                    await _downloader.DownloadAsync(e, coresFolder);
                                    PreferencesCache.RemoveCoreUpdate(e.FileName);
                                    ok = true; err = null;
                                }
                                catch (Exception ex)
                                {
                                    ok = false; err = ex.Message;
                                    System.Diagnostics.Trace.WriteLine($"[CoreUpdateAll] direct download failed {e.FileName}: {ex}");
                                }
                            }
                            if (!ok) failures.Add((e.FileName, err ?? "unknown"));

                            int n = System.Threading.Interlocked.Increment(ref done);
                            if (mvm != null)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    mvm.CoreUpdateText = ok
                                        ? $"Updated {e.FileName} — {n} of {total}"
                                        : $"Failed {e.FileName} — {n} of {total}";
                                    mvm.CoreUpdateProgressPercent = (double)n / total * 100;
                                });
                            }
                        }).ToList();

                        await Task.WhenAll(tasks);
                    }
                    finally
                    {
                        _suppressPanelRebuildDuringDownload = false;
                        // Successful downloads already removed themselves from
                        // the staleness cache; remaining entries are failures,
                        // so the next rebuild will keep their pills.
                        PreferencesCache.InvalidateCores();
                        InvalidateSection(PrefSection.Cores);
                        _ = LoadSectionAsync(PrefSection.Cores);

                        // Surface final result via banner. Dwell ~20s so the
                        // user can read failure summary before it auto-clears.
                        if (mvm != null)
                        {
                            int succeeded = total - failures.Count;
                            string finalText;
                            if (failures.IsEmpty)
                            {
                                finalText = $"Done — {succeeded} core{(succeeded == 1 ? "" : "s")} updated";
                            }
                            else
                            {
                                var names = failures.Select(f => f.fileName).Take(3).ToList();
                                string list = string.Join(", ", names);
                                if (failures.Count > 3) list += $", +{failures.Count - 3} more";
                                finalText = $"Updated {succeeded} of {total}. Failed: {list}";

                                // Full per-core failure detail goes to the trace log.
                                foreach (var (fn, err) in failures)
                                    System.Diagnostics.Trace.WriteLine($"[CoreUpdateAll] FAILED {fn}: {err}");
                            }
                            Dispatcher.Invoke(() =>
                            {
                                mvm.CoreUpdateText = finalText;
                                mvm.CoreUpdateProgressPercent = 100;
                            });

                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(20_000);
                                if (Dispatcher.HasShutdownStarted) return;
                                Dispatcher.Invoke(() =>
                                {
                                    mvm.IsCoreUpdating = false;
                                    mvm.CoreUpdateText = "";
                                    mvm.CoreUpdateProgressPercent = 0;
                                });
                            });
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[CoresUpdateCheck] {ex.Message}");
            }
        }

        // Set during an "Update All" run so per-download success doesn't
        // rebuild the panel mid-flight (which would orphan the still-running
        // rows' UI controls). Update All performs a single rebuild after all
        // downloads complete.
        private bool _suppressPanelRebuildDuringDownload;

        private async void StartSingleDownload(CoreEntry entry, string coresFolder,
            Border badge, ProgressBar bar, TextBlock statusText, Button dlBtn, Button? revertBtn = null)
        {
            await StartSingleDownloadAsync(entry, coresFolder, badge, bar, statusText, dlBtn, revertBtn);
        }

        private async Task<(bool ok, string? error)> StartSingleDownloadAsync(
            CoreEntry entry, string coresFolder,
            Border badge, ProgressBar bar, TextBlock statusText, Button dlBtn, Button? revertBtn = null)
        {
            dlBtn.IsEnabled = false;
            bar.Visibility = Visibility.Visible;
            statusText.Visibility = Visibility.Visible;
            statusText.Text = "…";
            bar.Value = 0;

            try
            {
                var prog = new Progress<int>(v => bar.Value = v);
                await _downloader.DownloadAsync(entry, coresFolder, prog);
                statusText.Text = "Done";
                bar.Visibility = Visibility.Collapsed;
                dlBtn.Content = "⟳";
                dlBtn.ToolTip = "Re-download";
                dlBtn.IsEnabled = true;
                if (revertBtn != null)
                    revertBtn.Visibility = Visibility.Visible;

                // Drop this core from the staleness cache so the next panel
                // rebuild doesn't show its pill again immediately after update.
                PreferencesCache.RemoveCoreUpdate(entry.FileName);
                PreferencesCache.InvalidateCores();

                if (!_suppressPanelRebuildDuringDownload)
                {
                    InvalidateSection(PrefSection.Cores);
                    _ = LoadSectionAsync(PrefSection.Cores);
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                statusText.Text = $"Error: {ex.Message}";
                dlBtn.IsEnabled = true;
                System.Diagnostics.Trace.WriteLine($"[CoreDownload] FAILED {entry.FileName}: {ex.Message}");
                return (false, ex.Message);
            }
        }

        // ── Extras section (SDL3 + DAT files) ────────────────────────────────

        private static readonly (string Tag, string Label, string? RedumpSlug, string? DirectUrl)[] KnownDats =
        {
            ("Arcade",       "Arcade (FBNeo)",          null, "https://raw.githubusercontent.com/libretro/FBNeo/master/dats/FinalBurn%20Neo%20(ClrMame%20Pro%20XML%2C%20Arcade%20only).dat"),
            ("mame2003plus", "Arcade (MAME 2003-Plus)", null, "https://raw.githubusercontent.com/libretro/mame2003-plus-libretro/master/metadata/mame2003-plus.xml"),
            ("NeoGeo",       "Neo Geo (Geolith)",       null, "https://raw.githubusercontent.com/libretro/libretro-database/master/dat/SNK%20-%20Neo%20Geo.dat"),
            ("NeoCD",        "Neo Geo CD",              null, "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/redump/SNK%20-%20Neo%20Geo%20CD.dat"),
            ("SegaCD", "Sega CD / Mega CD",  "mcd",  null),
            ("Saturn", "Sega Saturn",        "ss",   null),
            ("PS1",    "PlayStation",         "psx",  null),
            ("PS2",    "PlayStation 2",       "ps2",  null),
            ("TGCD",   "TurboGrafx-CD",      "pce",  null),
            ("3DO",    "3DO",                 "3do",  null),
            ("CDi",    "Philips CD-i",        "cdi",  null),
            ("NGP",    "Neo Geo Pocket",       null, "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/no-intro/SNK%20-%20Neo%20Geo%20Pocket.dat"),
            ("NGPC",   "Neo Geo Pocket Color", null, "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/no-intro/SNK%20-%20Neo%20Geo%20Pocket%20Color.dat"),
        };

        private void BuildExtrasSection()
        {
            // v1.4.6: native assets (SDL3.dll, ffmpeg.exe) and DATs/ live under
            // [DataRoot] now (was [exe]). Path is portable-mode aware via AppPaths.
            string nativeDir = AppPaths.GetNativeFolder();
            string datsDir   = AppPaths.GetDatsFolder();

            // ── Section header ──
            CoresListPanel.Children.Add(new Rectangle
            {
                Height = 1,
                Fill   = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x33)),
                Margin = new Thickness(0, 20, 0, 0)
            });
            CoresListPanel.Children.Add(new TextBlock
            {
                Text       = "EXTRAS",
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = _brushTextMuted,
                Margin     = new Thickness(0, 12, 0, 8)
            });

            var extrasCard = new Border
            {
                Background   = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x21)),
                CornerRadius = new CornerRadius(6),
                Padding      = new Thickness(14, 10, 14, 10),
                Margin       = new Thickness(0, 0, 0, 4)
            };
            var extrasStack = new StackPanel();

            // ── SDL3.dll row ──
            string sdl3Path     = System.IO.Path.Combine(nativeDir, "SDL3.dll");
            bool   sdl3Present  = System.IO.File.Exists(sdl3Path);

            var sdl3StatusText  = new TextBlock { FontSize = 10, Foreground = _brushTextMuted, Visibility = Visibility.Collapsed };
            var sdl3Progress    = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Value = 0, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            var sdl3Badge       = MakeBadge(sdl3Present);
            var sdl3Btn         = new Button
            {
                Content = sdl3Present ? "Re-download" : "Download",
                Style   = (Style)FindResource("SmallOutlineButton"),
                VerticalAlignment = VerticalAlignment.Center
            };

            sdl3Btn.Click += async (_, _) =>
            {
                sdl3Btn.IsEnabled          = false;
                sdl3Progress.Visibility    = Visibility.Visible;
                sdl3StatusText.Visibility  = Visibility.Visible;
                sdl3StatusText.Text        = "Fetching latest SDL3 release…";
                sdl3Progress.Value         = 0;
                try
                {
                    using var http = new System.Net.Http.HttpClient();
                    http.DefaultRequestHeaders.Add("User-Agent", "Emutastic");
                    var rel   = await http.GetStringAsync("https://api.github.com/repos/libsdl-org/SDL/releases/latest");
                    var doc   = System.Text.Json.JsonDocument.Parse(rel);
                    var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
                                   .FirstOrDefault(a => a.GetProperty("name").GetString() is string n
                                                     && n.Contains("win32-x64") && n.EndsWith(".zip"));
                    string? url = asset.ValueKind != System.Text.Json.JsonValueKind.Undefined
                                  ? asset.GetProperty("browser_download_url").GetString()
                                  : null;
                    if (url == null) throw new Exception("SDL3 win32-x64 zip not found in latest release.");

                    sdl3StatusText.Text = "Downloading…";
                    sdl3Progress.Value  = 20;

                    var zipBytes = await http.GetByteArrayAsync(url);
                    sdl3Progress.Value  = 80;

                    using var ms      = new System.IO.MemoryStream(zipBytes);
                    using var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("SDL3.dll", StringComparison.OrdinalIgnoreCase));
                    if (entry == null) throw new Exception("SDL3.dll not found inside the zip.");

                    using var dst = System.IO.File.Create(sdl3Path);
                    using var src = entry.Open();
                    await src.CopyToAsync(dst);

                    sdl3Progress.Value  = 100;
                    sdl3StatusText.Text = "Downloaded successfully.";
                    sdl3Badge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58));
                    ((TextBlock)sdl3Badge.Child).Text       = "Present";
                    ((TextBlock)sdl3Badge.Child).Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                    sdl3Btn.Content = "Re-download";
                }
                catch (Exception ex)
                {
                    sdl3StatusText.Text = $"Failed: {ex.Message}";
                }
                finally { sdl3Btn.IsEnabled = true; }
            };

            extrasStack.Children.Add(MakeExtrasRow(
                "SDL3.dll",
                "Controller name detection — without this, controllers show as \"Controller 1\", etc.",
                sdl3Badge, sdl3Progress, sdl3StatusText, sdl3Btn,
                isLast: false));

            // ── ffmpeg.exe row ──
            string ffmpegPath     = System.IO.Path.Combine(nativeDir, "ffmpeg.exe");
            bool   ffmpegPresent  = System.IO.File.Exists(ffmpegPath);

            var ffmpegStatusText  = new TextBlock { FontSize = 10, Foreground = _brushTextMuted, Visibility = Visibility.Collapsed };
            var ffmpegProgress    = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Value = 0, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            var ffmpegBadge       = MakeBadge(ffmpegPresent);
            var ffmpegBtn         = new Button
            {
                Content = ffmpegPresent ? "Re-download" : "Download",
                Style   = (Style)FindResource("SmallOutlineButton"),
                VerticalAlignment = VerticalAlignment.Center
            };

            ffmpegBtn.Click += async (_, _) =>
            {
                ffmpegBtn.IsEnabled          = false;
                ffmpegProgress.Visibility    = Visibility.Visible;
                ffmpegStatusText.Visibility  = Visibility.Visible;
                ffmpegStatusText.Text        = "Fetching latest FFmpeg build…";
                ffmpegProgress.Value         = 0;
                try
                {
                    using var http = new System.Net.Http.HttpClient();
                    http.DefaultRequestHeaders.Add("User-Agent", "Emutastic");

                    // BtbN's auto-builds — reliable, always up to date
                    string url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

                    ffmpegStatusText.Text = "Downloading (~80 MB, this may take a minute)…";
                    ffmpegProgress.Value  = 10;

                    // Stream the download so we can show progress
                    using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    long totalBytes = resp.Content.Headers.ContentLength ?? -1;

                    using var ms = new System.IO.MemoryStream();
                    using var stream = await resp.Content.ReadAsStreamAsync();
                    byte[] buffer = new byte[81920];
                    long downloaded = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        ms.Write(buffer, 0, read);
                        downloaded += read;
                        if (totalBytes > 0)
                        {
                            int pct = (int)(downloaded * 80 / totalBytes) + 10; // 10-90 range
                            ffmpegProgress.Value = Math.Min(pct, 90);
                            ffmpegStatusText.Text = $"Downloading… {downloaded / (1024 * 1024)} / {totalBytes / (1024 * 1024)} MB";
                        }
                    }

                    ffmpegStatusText.Text = "Extracting ffmpeg.exe…";
                    ffmpegProgress.Value  = 92;

                    ms.Position = 0;
                    using var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
                    var entry = archive.Entries.FirstOrDefault(e =>
                        e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                    if (entry == null) throw new Exception("ffmpeg.exe not found inside the archive.");

                    using var dst = System.IO.File.Create(ffmpegPath);
                    using var src = entry.Open();
                    await src.CopyToAsync(dst);

                    ffmpegProgress.Value  = 100;
                    ffmpegStatusText.Text = "Downloaded successfully — recording is ready to use.";
                    ffmpegBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58));
                    ((TextBlock)ffmpegBadge.Child).Text       = "Present";
                    ((TextBlock)ffmpegBadge.Child).Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                    ffmpegBtn.Content = "Re-download";
                }
                catch (Exception ex)
                {
                    ffmpegStatusText.Text = $"Failed: {ex.Message}";
                }
                finally { ffmpegBtn.IsEnabled = true; }
            };

            extrasStack.Children.Add(MakeExtrasRow(
                "ffmpeg.exe",
                "Required for game recording (F9). Downloads a ~80 MB build from GitHub.",
                ffmpegBadge, ffmpegProgress, ffmpegStatusText, ffmpegBtn,
                isLast: false));

            // ── DAT files separator ──
            extrasStack.Children.Add(new Rectangle
            {
                Height = 1,
                Fill   = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x33)),
                Margin = new Thickness(0, 8, 0, 8)
            });

            // Count found DATs for the header badge
            System.IO.Directory.CreateDirectory(datsDir);
            int datsFound = 0;
            foreach (var (tag, _, _, _) in KnownDats)
                if (System.IO.File.Exists(System.IO.Path.Combine(datsDir, $"{tag}.dat")))
                    datsFound++;

            // Collapsible accordion header matching the BIOS category pattern
            var datBody = new StackPanel { Visibility = Visibility.Collapsed };
            var datChevron = new TextBlock
            {
                Text = "▸", FontSize = 14,
                Foreground = _brushText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(0)
            };

            var datHeaderGrid = new Grid { Cursor = Cursors.Hand };
            datHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            datHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            datHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            datHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(datChevron, 0);

            var datLabel = new TextBlock
            {
                Text = "Game Databases (DAT files)",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _brushText,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(datLabel, 1);

            var datSummary = new TextBlock
            {
                Text = $"{KnownDats.Length} systems",
                FontSize = 11,
                Foreground = _brushTextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(datSummary, 2);

            var datHeaderBadge = new Border
            {
                Background = datsFound == KnownDats.Length
                    ? new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58))
                    : datsFound > 0
                        ? new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xA5, 0x00))
                        : new SolidColorBrush(Color.FromArgb(0x22, 0x88, 0x88, 0x88)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            datHeaderBadge.Child = new TextBlock
            {
                Text = $"{datsFound}/{KnownDats.Length}",
                FontSize = 10,
                Foreground = datsFound == KnownDats.Length
                    ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                    : datsFound > 0
                        ? new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00))
                        : _brushTextMuted
            };
            Grid.SetColumn(datHeaderBadge, 3);

            datHeaderGrid.Children.Add(datChevron);
            datHeaderGrid.Children.Add(datLabel);
            datHeaderGrid.Children.Add(datSummary);
            datHeaderGrid.Children.Add(datHeaderBadge);

            var datHeaderBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1A)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 4)
            };
            datHeaderBorder.Child = datHeaderGrid;

            var capturedDatBody = datBody;
            var capturedDatChevron = datChevron;
            datHeaderBorder.MouseLeftButtonUp += (_, _) =>
            {
                bool expanding = capturedDatBody.Visibility == Visibility.Collapsed;
                capturedDatBody.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
                ((RotateTransform)capturedDatChevron.RenderTransform).Angle = expanding ? 90 : 0;
            };

            extrasStack.Children.Add(datHeaderBorder);
            extrasStack.Children.Add(datBody);

            datBody.Children.Add(new TextBlock
            {
                Text         = "DAT files are game databases used during import to auto-detect which console a disc image belongs to and to resolve ROM names for artwork lookup. Without these, some games may show missing cover art or hero images.",
                FontSize     = 11,
                Foreground   = _brushTextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(4, 6, 0, 10)
            });

            // Shared download routine — used by each row's button AND the
            // "Download All" button below. Each call updates its own row's
            // progress/badge so a batch run shows per-system status.
            async Task DownloadDatAsync(
                string? slug, string? directUrl, string datPath,
                ProgressBar progress, TextBlock status, Border badge, Button btn)
            {
                btn.IsEnabled       = false;
                progress.Visibility = Visibility.Visible;
                status.Visibility   = Visibility.Visible;
                status.Text         = "Downloading…";
                progress.Value      = 10;
                try
                {
                    using var http = new System.Net.Http.HttpClient();
                    http.DefaultRequestHeaders.Add("User-Agent", "Emutastic");
                    string url = directUrl ?? $"http://redump.org/datfile/{slug}/";
                    var bytes = await http.GetByteArrayAsync(url);
                    progress.Value = 90;

                    // redump.org serves DATs as a .zip (Content-Type: application/zip)
                    // containing a single .dat. The DAT matcher parses the saved file
                    // as raw XML, so unwrap the archive before writing — otherwise the
                    // saved "{tag}.dat" is a zip the matcher silently fails to load.
                    // Direct GitHub URLs (Arcade/NeoGeo/NGP/…) are already raw .dat/.xml.
                    if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K' &&
                        bytes[2] == 0x03 && bytes[3] == 0x04)
                    {
                        using var zipMs   = new System.IO.MemoryStream(bytes);
                        using var archive = new System.IO.Compression.ZipArchive(zipMs, System.IO.Compression.ZipArchiveMode.Read);
                        var entry = archive.Entries.FirstOrDefault(e =>
                                        e.Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                                        e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                                    ?? archive.Entries.FirstOrDefault();
                        if (entry == null) throw new Exception("Downloaded archive was empty.");
                        using var entryStream = entry.Open();
                        using var outMs       = new System.IO.MemoryStream();
                        await entryStream.CopyToAsync(outMs);
                        bytes = outMs.ToArray();
                    }

                    await System.IO.File.WriteAllBytesAsync(datPath, bytes);
                    progress.Value      = 100;
                    status.Text         = "Downloaded successfully.";
                    badge.Background    = new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58));
                    ((TextBlock)badge.Child).Text       = "Present";
                    ((TextBlock)badge.Child).Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                    btn.Content = "Re-download";
                }
                catch (Exception ex)
                {
                    status.Text = $"Failed: {ex.Message}";
                }
                finally { btn.IsEnabled = true; }
            }

            // Recompute the header badge (n/N + colour) after a batch download.
            void RefreshDatHeaderBadge()
            {
                int found = 0;
                foreach (var (t, _, _, _) in KnownDats)
                    if (System.IO.File.Exists(System.IO.Path.Combine(datsDir, $"{t}.dat")))
                        found++;
                datHeaderBadge.Background = found == KnownDats.Length
                    ? new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58))
                    : found > 0
                        ? new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xA5, 0x00))
                        : new SolidColorBrush(Color.FromArgb(0x22, 0x88, 0x88, 0x88));
                var hb = (TextBlock)datHeaderBadge.Child;
                hb.Text       = $"{found}/{KnownDats.Length}";
                hb.Foreground = found == KnownDats.Length
                    ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                    : found > 0
                        ? new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00))
                        : _brushTextMuted;
            }

            // Per-row download actions, collected for the "Download All" button.
            var datDownloadActions = new List<Func<Task>>();

            // ── Download All row ──
            var downloadAllBtn = new Button
            {
                Content             = "Download All",
                Style               = (Style)FindResource("SmallOutlineButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(4, 0, 0, 12)
            };
            downloadAllBtn.Click += async (_, _) =>
            {
                downloadAllBtn.IsEnabled = false;
                int n = datDownloadActions.Count;
                for (int k = 0; k < n; k++)
                {
                    downloadAllBtn.Content = $"Downloading… ({k + 1}/{n})";
                    // Sequential — keeps us gentle on redump.org and lets each
                    // row report its own progress as it goes.
                    await datDownloadActions[k]();
                }
                RefreshDatHeaderBadge();
                downloadAllBtn.Content   = "Download All";
                downloadAllBtn.IsEnabled = true;
            };
            datBody.Children.Add(downloadAllBtn);

            // Per-DAT rows
            for (int i = 0; i < KnownDats.Length; i++)
            {
                var (tag, label, slug, directUrl) = KnownDats[i];
                string datPath = System.IO.Path.Combine(datsDir, $"{tag}.dat");
                bool   present = System.IO.File.Exists(datPath);

                var datBadge    = MakeBadge(present);
                var datProgress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Value = 0, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
                var datStatus   = new TextBlock   { FontSize = 10, Foreground = _brushTextMuted, Visibility = Visibility.Collapsed };
                var datBtn      = new Button
                {
                    Content           = present ? "Re-download" : "Download",
                    Style             = (Style)FindResource("SmallOutlineButton"),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var capturedSlug     = slug;
                var capturedDirectUrl = directUrl;
                var capturedDatPath  = datPath;
                var capturedBadge    = datBadge;
                var capturedProgress = datProgress;
                var capturedStatus   = datStatus;
                var capturedBtn      = datBtn;

                datBtn.Click += async (_, _) => await DownloadDatAsync(
                    capturedSlug, capturedDirectUrl, capturedDatPath,
                    capturedProgress, capturedStatus, capturedBadge, capturedBtn);

                datDownloadActions.Add(() => DownloadDatAsync(
                    capturedSlug, capturedDirectUrl, capturedDatPath,
                    capturedProgress, capturedStatus, capturedBadge, capturedBtn));

                datBody.Children.Add(MakeExtrasRow(
                    $"{tag}.dat",
                    label,
                    datBadge,
                    datProgress,
                    datStatus,
                    datBtn,
                    isLast: (i == KnownDats.Length - 1)));
            }

            // ── Vectrex Overlays row ──
            extrasStack.Children.Add(new Rectangle
            {
                Height = 1,
                Fill   = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x33)),
                Margin = new Thickness(0, 8, 0, 8)
            });

            string overlayDir    = AppPaths.GetFolder("Overlays", "Vectrex");
            int    overlayCount  = System.IO.Directory.GetFiles(overlayDir, "*.png").Length;
            bool   overlaysPresent = overlayCount >= 30; // expect ~38

            var ovlBadge    = MakeBadge(overlaysPresent);
            var ovlProgress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Value = 0, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            var ovlStatus   = new TextBlock   { FontSize = 10, Foreground = _brushTextMuted, Visibility = Visibility.Collapsed };
            var ovlBtn      = new Button
            {
                Content           = overlaysPresent ? "Re-download" : "Download",
                Style             = (Style)FindResource("SmallOutlineButton"),
                VerticalAlignment = VerticalAlignment.Center
            };

            ovlBtn.Click += async (_, _) =>
            {
                ovlBtn.IsEnabled        = false;
                ovlProgress.Visibility  = Visibility.Visible;
                ovlStatus.Visibility    = Visibility.Visible;
                ovlStatus.Text          = "Fetching overlay list…";
                ovlProgress.Value       = 0;
                try
                {
                    using var http = new System.Net.Http.HttpClient();
                    http.DefaultRequestHeaders.Add("User-Agent", "Emutastic");

                    // Get directory listing from GitHub API
                    string apiUrl = "https://api.github.com/repos/libretro/overlay-borders/contents/1080%20GCE%20Vectrex/Game%20Overlay";
                    var json = await http.GetStringAsync(apiUrl);
                    var files = System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateArray()
                        .Where(e => e.GetProperty("name").GetString()?.EndsWith(".png") == true)
                        .ToList();

                    if (files.Count == 0) throw new Exception("No overlay PNGs found in repository.");

                    int done = 0;
                    foreach (var file in files)
                    {
                        string name = file.GetProperty("name").GetString()!;
                        string downloadUrl = file.GetProperty("download_url").GetString()!;
                        string destPath = System.IO.Path.Combine(overlayDir, name);

                        ovlStatus.Text = $"Downloading {name} ({done + 1}/{files.Count})…";
                        var bytes = await http.GetByteArrayAsync(downloadUrl);
                        await System.IO.File.WriteAllBytesAsync(destPath, bytes);

                        done++;
                        ovlProgress.Value = (int)(done * 100.0 / files.Count);
                    }

                    ovlStatus.Text = $"Downloaded {done} overlays successfully.";
                    ovlProgress.Value = 100;
                    ovlBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58));
                    ((TextBlock)ovlBadge.Child).Text       = "Present";
                    ((TextBlock)ovlBadge.Child).Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                    ovlBtn.Content = "Re-download";
                }
                catch (Exception ex)
                {
                    ovlStatus.Text = $"Failed: {ex.Message}";
                }
                finally { ovlBtn.IsEnabled = true; }
            };

            extrasStack.Children.Add(MakeExtrasRow(
                "Vectrex Overlays",
                "Game-specific screen overlays for Vectrex — enabled by default when present.",
                ovlBadge, ovlProgress, ovlStatus, ovlBtn,
                isLast: false));

            // ── Video Shaders (libretro slang) row ─────────────────────────────
            // Multi-pass .slangp shader collection from the libretro buildbot
            // (~51 MB). Rendered via librashader on supported systems (later
            // phase); the built-in WPF shaders stay available without this.
            string slangDir     = AppPaths.GetFolder("Shaders", "slang");
            string slangMarker  = System.IO.Path.Combine(slangDir, ".installed");
            bool   slangPresent = System.IO.File.Exists(slangMarker);

            var slangStatus   = new TextBlock   { FontSize = 10, Foreground = _brushTextMuted, Visibility = Visibility.Collapsed };
            var slangProgress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Value = 0, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            var slangBadge    = MakeBadge(slangPresent);
            var slangBtn      = new Button
            {
                Content           = slangPresent ? "Re-download" : "Download",
                Style             = (Style)FindResource("SmallOutlineButton"),
                VerticalAlignment = VerticalAlignment.Center
            };

            slangBtn.Click += async (_, _) =>
            {
                slangBtn.IsEnabled       = false;
                slangProgress.Visibility = Visibility.Visible;
                slangStatus.Visibility   = Visibility.Visible;
                slangStatus.Text         = "Downloading shader pack…";
                slangProgress.Value      = 0;
                try
                {
                    const string url = "https://buildbot.libretro.com/assets/frontend/shaders_slang.zip";
                    string tmpZip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emutastic_shaders_slang.zip");

                    using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                    {
                        http.DefaultRequestHeaders.Add("User-Agent", "Emutastic");
                        using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                        resp.EnsureSuccessStatusCode();
                        long total = resp.Content.Headers.ContentLength ?? -1;
                        long got = 0;
                        await using var src = await resp.Content.ReadAsStreamAsync();
                        await using var dst = System.IO.File.Create(tmpZip);
                        var buf = new byte[81920];
                        int read;
                        while ((read = await src.ReadAsync(buf)) > 0)
                        {
                            await dst.WriteAsync(buf.AsMemory(0, read));
                            got += read;
                            if (total > 0) slangProgress.Value = (int)(got * 90 / total);
                        }
                    }

                    slangStatus.Text = "Extracting…";
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        foreach (var d in System.IO.Directory.GetDirectories(slangDir))
                            System.IO.Directory.Delete(d, recursive: true);
                        System.IO.Compression.ZipFile.ExtractToDirectory(tmpZip, slangDir, overwriteFiles: true);
                        try { System.IO.File.Delete(tmpZip); } catch { /* temp cleanup best-effort */ }
                    });

                    // Also fetch the librashader runtime DLL (the engine that runs
                    // these presets) into the Shaders/ root. 0.6.1 is the newest
                    // prebuilt Windows binary (shipped in the v0.6.2 release).
                    slangStatus.Text = "Downloading shader engine…";
                    const string runtimeUrl = "https://github.com/SnowflakePowered/librashader/releases/download/librashader-v0.6.2/librashader-x86_64-windows-0.6.1-optimized.zip";
                    string tmpRt = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emutastic_librashader_rt.zip");
                    string shadersRoot = AppPaths.GetFolder("Shaders");
                    using (var http2 = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                    {
                        http2.DefaultRequestHeaders.Add("User-Agent", "Emutastic");
                        var rtBytes = await http2.GetByteArrayAsync(runtimeUrl);
                        await System.IO.File.WriteAllBytesAsync(tmpRt, rtBytes);
                    }
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        using (var z = System.IO.Compression.ZipFile.OpenRead(tmpRt))
                        {
                            var dllEntry = z.Entries.FirstOrDefault(e =>
                                e.Name.Equals("librashader.dll", StringComparison.OrdinalIgnoreCase));
                            if (dllEntry == null)
                                throw new Exception("librashader.dll not found in the runtime package.");
                            using var es = dllEntry.Open();
                            using var fs = System.IO.File.Create(System.IO.Path.Combine(shadersRoot, "librashader.dll"));
                            es.CopyTo(fs);
                        }
                        try { System.IO.File.Delete(tmpRt); } catch { /* best effort */ }
                        // Mark installed only after BOTH the presets and the engine DLL landed.
                        System.IO.File.WriteAllText(slangMarker, DateTime.UtcNow.ToString("o"));
                    });

                    int presetCount = System.IO.Directory.GetFiles(slangDir, "*.slangp", System.IO.SearchOption.AllDirectories).Length;
                    slangProgress.Value = 100;
                    slangStatus.Text = $"Installed — {presetCount} presets.";
                    slangBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58));
                    ((TextBlock)slangBadge.Child).Text       = "Present";
                    ((TextBlock)slangBadge.Child).Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                    slangBtn.Content = "Re-download";
                }
                catch (Exception ex)
                {
                    slangStatus.Text = $"Failed: {ex.Message}";
                }
                finally { slangBtn.IsEnabled = true; }
            };

            extrasStack.Children.Add(MakeExtrasRow(
                "Video Shaders (libretro)",
                "Community multi-pass shader pack from libretro (~51 MB): CRT, LCD, NTSC, scalers and more. The built-in shaders stay available without this download.",
                slangBadge, slangProgress, slangStatus, slangBtn,
                isLast: false));

            // ── Arcade / Neo Geo Bezels row ────────────────────────────────────
            // Per-game bezel frames from The Bezel Project. The ~2 GB pack is NOT
            // bundled. "Enable" turns on per-game on-demand fetch (each ~1-4 MB bezel
            // downloads on first launch + caches); "Download all" pre-fetches every
            // arcade/NeoGeo bezel (~1.5 GB) into the same cache for offline use.
            bool bezelsOn    = BezelService.FeatureEnabled;
            int  bezelCached = BezelService.CachedCount();

            var bezelStatus = new TextBlock
            {
                FontSize   = 10,
                Foreground = _brushTextMuted,
                Visibility = bezelCached > 0 ? Visibility.Visible : Visibility.Collapsed,
                Text       = bezelCached > 0 ? $"{bezelCached} bezels cached" : ""
            };
            var bezelProgress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Value = 0, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            var bezelBadge    = MakeBadge(bezelsOn);
            ((TextBlock)bezelBadge.Child).Text = bezelsOn ? "On" : "Off";

            var bezelBtn = new Button
            {
                Content           = bezelsOn ? "Disable" : "Enable",
                Style             = (Style)FindResource("SmallOutlineButton"),
                VerticalAlignment = VerticalAlignment.Center
            };
            bezelBtn.Click += (_, _) =>
            {
                bool now = !BezelService.FeatureEnabled;
                BezelService.FeatureEnabled = now;
                bezelBtn.Content = now ? "Disable" : "Enable";
                bezelBadge.Background = now
                    ? new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58))
                    : new SolidColorBrush(Color.FromArgb(0x22, 0x88, 0x88, 0x88));
                ((TextBlock)bezelBadge.Child).Text       = now ? "On" : "Off";
                ((TextBlock)bezelBadge.Child).Foreground = now
                    ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                    : _brushTextMuted;
            };

            var bezelDownloadBtn = new Button
            {
                Content           = "Download all",
                Style             = (Style)FindResource("SmallOutlineButton"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0)
            };
            bezelDownloadBtn.Click += async (_, _) =>
            {
                bezelDownloadBtn.IsEnabled = false;
                bezelBtn.IsEnabled         = false;
                bezelProgress.Visibility   = Visibility.Visible;
                bezelStatus.Visibility     = Visibility.Visible;
                bezelStatus.Text           = "Listing bezels…";
                bezelProgress.Value        = 0;
                try
                {
                    var progress = new Progress<(int done, int total)>(p =>
                    {
                        if (p.total > 0)
                        {
                            bezelProgress.Value = (double)p.done * 100 / p.total;
                            bezelStatus.Text    = $"Downloading bezels… {p.done}/{p.total}";
                        }
                    });
                    var (_, total) = await BezelService.DownloadAllAsync(progress);
                    bezelProgress.Value = 100;
                    bezelStatus.Text    = total > 0
                        ? $"{BezelService.CachedCount()} bezels cached."
                        : "No bezels found.";
                    // Downloading the set implies the feature is on.
                    bezelBtn.Content      = "Disable";
                    bezelBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58));
                    ((TextBlock)bezelBadge.Child).Text       = "On";
                    ((TextBlock)bezelBadge.Child).Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                }
                catch (Exception ex)
                {
                    bezelStatus.Text = $"Failed: {ex.Message}";
                }
                finally
                {
                    bezelDownloadBtn.IsEnabled = true;
                    bezelBtn.IsEnabled         = true;
                }
            };

            var bezelActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            bezelActions.Children.Add(bezelDownloadBtn);
            bezelActions.Children.Add(bezelBtn);

            extrasStack.Children.Add(MakeExtrasRow(
                "Arcade / Neo Geo Bezels",
                "Arcade cabinet & Neo Geo bezel frames from The Bezel Project. Enable for per-game on-demand download (~1-4 MB each, cached), or Download all (~1.5 GB) for full offline use. Toggle per game from the in-game cog menu.",
                bezelBadge, bezelProgress, bezelStatus, bezelActions,
                isLast: false));

            // ── Cheats Database row ────────────────────────────────────────────
            // Single-file download of the libretro community cheats database
            // (~37 MB). Per-game cheats import from the in-game / library
            // cheats menu after this is installed.
            int cheatFiles    = CheatDatabaseService.TotalFileCount();
            int cheatSystems  = CheatDatabaseService.InstalledSystemCount();
            bool cheatsHere   = CheatDatabaseService.IsInstalled() && cheatFiles > 0;

            var cheatsStatus = new TextBlock
            {
                FontSize    = 10,
                Foreground  = _brushTextMuted,
                Visibility  = cheatsHere ? Visibility.Visible : Visibility.Collapsed,
                Text        = cheatsHere ? $"{cheatFiles} cheat files for {cheatSystems} systems" : "",
            };
            var cheatsProgress = new ProgressBar
            {
                Height = 4, Minimum = 0, Maximum = 100, Value = 0,
                Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0),
            };
            var cheatsBadge = MakeBadge(cheatsHere);
            var cheatsBtn = new Button
            {
                Content           = cheatsHere ? "Update" : "Download",
                Style             = (Style)FindResource("SmallOutlineButton"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            cheatsBtn.Click += async (_, _) =>
            {
                cheatsBtn.IsEnabled       = false;
                cheatsProgress.Visibility = Visibility.Visible;
                cheatsStatus.Visibility   = Visibility.Visible;
                cheatsStatus.Text         = "Connecting…";
                cheatsProgress.Value      = 0;

                try
                {
                    int extracted = await CheatDatabaseService.DownloadAndExtractAsync(
                        (pct, msg) => Dispatcher.Invoke(() =>
                        {
                            cheatsProgress.Value = pct;
                            cheatsStatus.Text    = msg;
                        }));

                    int sysNow = CheatDatabaseService.InstalledSystemCount();
                    cheatsStatus.Text = $"{extracted} cheat files for {sysNow} systems";
                    ((TextBlock)cheatsBadge.Child).Text = "✓ Installed";
                    ((TextBlock)cheatsBadge.Child).Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                    cheatsBtn.Content = "Update";
                }
                catch (Exception ex)
                {
                    cheatsStatus.Text = $"Failed: {ex.Message}";
                }
                finally { cheatsBtn.IsEnabled = true; }
            };

            extrasStack.Children.Add(MakeExtrasRow(
                "Cheats Database",
                "Community cheat code database from libretro (~37 MB, single download). Per-game cheats can be imported from the in-game cheats menu after installing.",
                cheatsBadge, cheatsProgress, cheatsStatus, cheatsBtn,
                isLast: true));

            extrasCard.Child = extrasStack;
            CoresListPanel.Children.Add(extrasCard);

            BuildCompatibilitySection();
        }

        /// <summary>
        /// Per-core compatibility toggles for GPU/driver edge cases.
        /// Each checkbox here changes how a console's render path behaves —
        /// not a setting most users should touch unless they're hitting a
        /// specific bug their hardware exposes.
        /// </summary>
        private void BuildCompatibilitySection()
        {
            CoresListPanel.Children.Add(new Rectangle
            {
                Height = 1,
                Fill   = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x33)),
                Margin = new Thickness(0, 20, 0, 0)
            });
            CoresListPanel.Children.Add(new TextBlock
            {
                Text       = "COMPATIBILITY",
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = _brushTextMuted,
                Margin     = new Thickness(0, 12, 0, 8)
            });

            var compatCard = new Border
            {
                Background   = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x21)),
                CornerRadius = new CornerRadius(6),
                Padding      = new Thickness(14, 10, 14, 10),
                Margin       = new Thickness(0, 0, 0, 4)
            };
            var compatStack = new StackPanel();

            // ── AMD/Intel GPU compatibility (covers all OpenGL HW cores) ──
            var emuConfig = _configService.GetEmulatorConfiguration();
            var gcCheck = new CheckBox
            {
                Content    = "AMD/Intel GPU compatibility (all hardware-rendered cores)",
                Foreground = _brushText,
                FontSize   = 13,
                IsChecked  = emuConfig.ResolveAmdIntelCompat(),
                Cursor     = System.Windows.Input.Cursors.Hand,
            };
            var gcDesc = new TextBlock
            {
                Text         = "Fixes a video rendering bug AMD and Intel GPU drivers exhibit with hardware-rendered cores (GameCube, PSX HW, Dreamcast) where the game renders only in part of the window. Cost: the in-game GL overlay (cog menu, save/load slots, cheats panel) is disabled and presentation falls back to a slower readback path while this is on. NVIDIA users should leave this off.",
                FontSize     = 11,
                Foreground   = _brushTextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(22, 4, 0, 0),
            };
            gcCheck.Checked   += (_, _) => SaveCompatToggle(c =>
            {
                c.AmdIntelGpuCompatibility    = true;
                c.GameCubeUseDefaultFramebuffer = false; // legacy field — clear it so ResolveAmdIntelCompat is sourced from the new one only
            });
            gcCheck.Unchecked += (_, _) => SaveCompatToggle(c =>
            {
                c.AmdIntelGpuCompatibility    = false;
                c.GameCubeUseDefaultFramebuffer = false;
            });

            compatStack.Children.Add(gcCheck);
            compatStack.Children.Add(gcDesc);

            compatCard.Child = compatStack;
            CoresListPanel.Children.Add(compatCard);
        }

        private void SaveCompatToggle(System.Action<Configuration.EmulatorConfiguration> mutate)
        {
            var cfg = _configService.GetEmulatorConfiguration();
            mutate(cfg);
            _configService.SetEmulatorConfiguration(cfg);
            _ = _configService.SaveAsync();
        }

        private Grid MakeExtrasRow(string name, string description, Border badge,
                                   ProgressBar? progress, TextBlock? status, FrameworkElement btn, bool isLast)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, isLast ? 0 : 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock { Text = name,        FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _brushText });
            nameStack.Children.Add(new TextBlock { Text = description, FontSize = 10, Foreground = _brushTextMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(nameStack, 0);

            var statusStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            statusStack.Children.Add(badge);
            if (progress != null) statusStack.Children.Add(progress);
            if (status  != null) statusStack.Children.Add(status);
            Grid.SetColumn(statusStack, 1);

            Grid.SetColumn(btn, 2);

            row.Children.Add(nameStack);
            row.Children.Add(statusStack);
            row.Children.Add(btn);
            return row;
        }

        private Border MakeBadge(bool present) => new()
        {
            CornerRadius = new CornerRadius(4),
            Padding      = new Thickness(8, 3, 8, 3),
            Background   = present
                ? new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58))
                : new SolidColorBrush(Color.FromArgb(0x22, 0x88, 0x88, 0x88)),
            Child = new TextBlock
            {
                Text       = present ? "Present" : "Not found",
                FontSize   = 10,
                Foreground = present
                    ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                    : _brushTextMuted
            }
        };

        private static string GetCoreVersion(string path)
        {
            try
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(vi.FileVersion)) return vi.FileVersion;
            }
            catch { }
            try { return System.IO.File.GetLastWriteTime(path).ToString("yyyy-MM-dd"); }
            catch { return "—"; }
        }

        private static string FormatCoreName(string dllName)
        {
            string name = dllName.Replace("_libretro.dll", "", StringComparison.OrdinalIgnoreCase);
            return name switch
            {
                "nestopia"          => "Nestopia",
                "fceumm"            => "FCE Ultra MM",
                "quicknes"          => "QuickNES",
                "snes9x"            => "Snes9x",
                "snes9x2002"        => "Snes9x 2002",
                "snes9x2005"        => "Snes9x 2005",
                "snes9x2005_plus"   => "Snes9x 2005 Plus",
                "snes9x2010"        => "Snes9x 2010",
                "bsnes"             => "bsnes",
                "parallel_n64"      => "Parallel N64",
                "mupen64plus_next"  => "Mupen64Plus-Next",
                "dolphin"           => "Dolphin",
                "mgba"              => "mGBA",
                "gambatte"          => "Gambatte",
                "sameboy"           => "SameBoy",
                "desmume"           => "DeSmuME",
                "melonds"           => "melonDS",
                "azahar"            => "Azahar (3DS)",
                "mednafen_vb"       => "Mednafen Virtual Boy",
                "genesis_plus_gx"   => "Genesis Plus GX",
                "picodrive"         => "PicoDrive",
                "kronos"            => "Kronos",
                "mednafen_saturn"   => "Mednafen Saturn",
                "yabause"           => "Yabause",
                "mednafen_psx"      => "Mednafen PSX (Beetle)",
                "pcsx_rearmed"      => "PCSX-ReARMed",
                "ppsspp"            => "PPSSPP",
                "mednafen_pce"      => "Mednafen PCE",
                "mednafen_pce_fast" => "Mednafen PCE Fast",
                "mednafen_ngp"      => "Mednafen Neo Geo Pocket",
                "gearcoleco"        => "GearColeco",
                "stella"            => "Stella",
                "stella2014"        => "Stella 2014",
                "stella2023"        => "Stella 2023",
                "prosystem"         => "ProSystem",
                "flycast"           => "Flycast (Dreamcast)",
                "virtualjaguar"     => "Virtual Jaguar",
                "bluemsx"           => "blueMSX",
                "vecx"              => "Vecx",
                "opera"             => "Opera (3DO)",
                "same_cdi"          => "SAME CDi",
                "fbneo"             => "FBNeo (Final Burn Neo)",
                "geolith"           => "Geolith (Neo Geo)",
                "smsplus"           => "SMS Plus",
                _ => char.ToUpper(name[0]) + name[1..].Replace("_", " ")
            };
        }

        // ── Library panel ─────────────────────────────────────────────────────
        private void LoadLibrarySettings()
        {
            // Data directory display (read-only)
            DataDirPathText.Text = AppPaths.DataRoot;

            var lib = _configService.GetLibraryConfiguration();
            LibraryPathText.Text = string.IsNullOrEmpty(lib.LibraryPath) ? "Not set" : lib.LibraryPath;
            LibraryPathText.Foreground = string.IsNullOrEmpty(lib.LibraryPath) ? _brushTextMuted : _brushText;
            LibraryCopyFiles.IsChecked = lib.CopyToLibrary;
            LibraryKeepInPlace.IsChecked = !lib.CopyToLibrary;
            LibraryOrganizeByConsole.IsChecked = lib.OrganizeByConsole;
            LibraryOrganizeByConsole.IsEnabled = lib.CopyToLibrary;
            LibraryCopyFiles.Checked += (_, _) => LibraryOrganizeByConsole.IsEnabled = true;
            LibraryKeepInPlace.Checked += (_, _) => LibraryOrganizeByConsole.IsEnabled = false;
        }

        private void BrowseLibraryBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select game library folder",
            };
            var lib = _configService.GetLibraryConfiguration();
            if (!string.IsNullOrEmpty(lib.LibraryPath) && System.IO.Directory.Exists(lib.LibraryPath))
                dialog.InitialDirectory = lib.LibraryPath;

            if (dialog.ShowDialog() == true)
            {
                LibraryPathText.Text = dialog.FolderName;
                LibraryPathText.Foreground = _brushText;
            }
        }

        private void LibrarySaveBtn_Click(object sender, RoutedEventArgs e)
        {
            var lib = _configService.GetLibraryConfiguration();
            lib.LibraryPath = LibraryPathText.Text == "Not set" ? "" : LibraryPathText.Text;
            lib.CopyToLibrary = LibraryCopyFiles.IsChecked == true;
            lib.OrganizeByConsole = LibraryOrganizeByConsole.IsChecked == true;
            _configService.SetLibraryConfiguration(lib);
            _ = _configService.SaveAsync();
        }

        private void BrowseBackupFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select backup folder" };
            var prefs = _configService.GetUserPreferences();
            if (!string.IsNullOrEmpty(prefs.BackupFolder) && System.IO.Directory.Exists(prefs.BackupFolder))
                dialog.InitialDirectory = prefs.BackupFolder;

            if (dialog.ShowDialog(this) != true) return;

            prefs.BackupFolder = dialog.FolderName;
            _ = _configService.SaveAsync();

            BackupFolderPathText.Text = dialog.FolderName;
            BackupFolderPathText.Foreground = _brushText;
            BackupNowBtn.IsEnabled = true;
            RestoreBackupBtn.IsEnabled = true;
            BackupFolderStatusText.Text = "";
        }

        private void ClearBackupFolder_Click(object sender, RoutedEventArgs e)
        {
            var prefs = _configService.GetUserPreferences();
            prefs.BackupFolder = "";
            _ = _configService.SaveAsync();

            BackupFolderPathText.Text = "Not set";
            BackupFolderPathText.Foreground = _brushTextMuted;
            BackupNowBtn.IsEnabled = false;
            RestoreBackupBtn.IsEnabled = false;
            BackupFolderStatusText.Text = "";
        }

        private async void BackupNow_Click(object sender, RoutedEventArgs e)
        {
            var prefs = _configService.GetUserPreferences();
            if (string.IsNullOrEmpty(prefs.BackupFolder)) return;

            BackupNowBtn.IsEnabled = false;
            BackupFolderStatusText.Text = "Backing up\u2026";

            try
            {
                string dest = prefs.BackupFolder;
                await Task.Run(() =>
                {
                    // Battery saves
                    string batterySrc = System.IO.Path.Combine(AppPaths.DataRoot, "BatterySaves");
                    if (System.IO.Directory.Exists(batterySrc))
                        CopyDirectoryRecursive(batterySrc, System.IO.Path.Combine(dest, "BatterySaves"));

                    // Save states
                    string statesSrc = System.IO.Path.Combine(AppPaths.DataRoot, "Save States");
                    if (System.IO.Directory.Exists(statesSrc))
                        CopyDirectoryRecursive(statesSrc, System.IO.Path.Combine(dest, "Save States"));

                    // Library database
                    string dbSrc = System.IO.Path.Combine(AppPaths.DataRoot, "library.db");
                    if (System.IO.File.Exists(dbSrc))
                        System.IO.File.Copy(dbSrc, System.IO.Path.Combine(dest, "library.db"), overwrite: true);
                });

                BackupFolderStatusText.Text = $"Backup complete \u2014 {DateTime.Now:g}";
            }
            catch (Exception ex)
            {
                BackupFolderStatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                BackupNowBtn.IsEnabled = true;
            }
        }

        private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            var prefs = _configService.GetUserPreferences();
            if (string.IsNullOrEmpty(prefs.BackupFolder)) return;

            string src = prefs.BackupFolder;

            // Check that backup folder actually has data
            bool hasDb = System.IO.File.Exists(System.IO.Path.Combine(src, "library.db"));
            bool hasSaves = System.IO.Directory.Exists(System.IO.Path.Combine(src, "BatterySaves"));
            bool hasStates = System.IO.Directory.Exists(System.IO.Path.Combine(src, "Save States"));

            if (!hasDb && !hasSaves && !hasStates)
            {
                BackupFolderStatusText.Text = "No backup data found in that folder.";
                return;
            }

            var parts = new System.Collections.Generic.List<string>();
            if (hasDb) parts.Add("library database");
            if (hasSaves) parts.Add("battery saves");
            if (hasStates) parts.Add("save states");

            var result = MessageBox.Show(
                $"This will overwrite your current {string.Join(", ", parts)} with the backup copy.\n\nContinue?",
                "Restore from Backup",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            RestoreBackupBtn.IsEnabled = false;
            BackupFolderStatusText.Text = "Restoring\u2026";

            try
            {
                await Task.Run(() =>
                {
                    if (hasSaves)
                        CopyDirectoryRecursive(System.IO.Path.Combine(src, "BatterySaves"),
                            System.IO.Path.Combine(AppPaths.DataRoot, "BatterySaves"));

                    if (hasStates)
                        CopyDirectoryRecursive(System.IO.Path.Combine(src, "Save States"),
                            System.IO.Path.Combine(AppPaths.DataRoot, "Save States"));

                    if (hasDb)
                        System.IO.File.Copy(System.IO.Path.Combine(src, "library.db"),
                            System.IO.Path.Combine(AppPaths.DataRoot, "library.db"), overwrite: true);
                });

                BackupFolderStatusText.Text = $"Restore complete \u2014 restart recommended";
            }
            catch (Exception ex)
            {
                BackupFolderStatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                RestoreBackupBtn.IsEnabled = true;
            }
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            System.IO.Directory.CreateDirectory(destDir);

            foreach (string file in System.IO.Directory.GetFiles(sourceDir))
            {
                string destFile = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(file));
                System.IO.File.Copy(file, destFile, overwrite: true);
            }

            foreach (string dir in System.IO.Directory.GetDirectories(sourceDir))
            {
                string destSubDir = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(dir));
                CopyDirectoryRecursive(dir, destSubDir);
            }
        }

        // ── Cloud Sync (Backups tab) ──────────────────────────────────────────

        private void LoadBackupsSettings()
        {
            _suppressAutoSave = true;
            try
            {
                // Load local backup folder path
                var prefs = App.Configuration?.GetUserPreferences();
                BackupFolderPathText.Text = string.IsNullOrEmpty(prefs?.BackupFolder)
                    ? "Not set" : prefs!.BackupFolder;

                // Load cloud sync state
                var cfg = App.Configuration?.GetCloudSyncConfiguration();
                var svc = Services.GitHubSyncService.Instance;

                if (svc.IsAuthenticated && !string.IsNullOrEmpty(svc.Username))
                {
                    CloudSyncStatusText.Text = $"Signed in as {svc.Username}";
                    CloudSyncSignInBtn.Content = "Sign Out";
                    CloudSyncSettingsPanel.Visibility = Visibility.Visible;
                }

                if (cfg != null)
                {
                    SyncOnClose.IsChecked = cfg.SyncTiming == "on_close";
                    SyncPeriodic.IsChecked = cfg.SyncTiming == "periodic";
                    SyncManual.IsChecked = cfg.SyncTiming == "manual";
                    SyncEncryptionEnabled.IsChecked = cfg.EncryptionEnabled;
                    PassphrasePanel.Visibility = cfg.EncryptionEnabled
                        ? Visibility.Visible : Visibility.Collapsed;
                    SyncPerPcRepo.IsChecked = cfg.UsePerPcRepo;
                    UpdatePerPcRepoCopy(cfg.UsePerPcRepo);
                }
            }
            finally { _suppressAutoSave = false; }
        }

        /// <summary>
        /// Mode-specific explainer under the per-PC repo toggle. The wording
        /// must make the tradeoff unmistakable: shared = follows you between
        /// PCs, separate = backup unique to this machine that other PCs never
        /// touch.
        /// </summary>
        private void UpdatePerPcRepoCopy(bool perPc)
        {
            SyncRepoExplainText.Text = perPc
                ? "On: this PC backs up to its own repository. Saves and the game "
                  + "library on this PC stay unique to it — they will not appear on "
                  + "your other machines, and other machines can't overwrite them."
                : "Off: this PC shares one cloud repository with your other PCs — "
                  + "saves and your game library follow you between machines.";
            SyncRepoNameText.Text =
                $"Repository in use: {Services.GitHubSyncService.EffectiveRepoName}";
        }

        private void SyncPerPcRepo_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressAutoSave) return;
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (cfg == null) return;
            cfg.UsePerPcRepo = SyncPerPcRepo.IsChecked == true;
            App.Configuration?.SetCloudSyncConfiguration(cfg);
            _ = App.Configuration?.SaveAsync();

            // Everything cached from the previous repo is now wrong.
            var svc = Services.GitHubSyncService.Instance;
            svc.ResetRepoBinding();
            UpdatePerPcRepoCopy(cfg.UsePerPcRepo);

            // The shared repo was created at sign-in; a per-PC repo may not
            // exist yet — create it now so the first sync doesn't 404.
            if (svc.IsAuthenticated && cfg.UsePerPcRepo)
                _ = svc.EnsureRepoExistsAsync();
        }

        private async void CloudSyncSignIn_Click(object sender, RoutedEventArgs e)
        {
            var svc = Services.GitHubSyncService.Instance;

            if (svc.IsAuthenticated)
            {
                svc.SignOut();
                CloudSyncStatusText.Text = "Not signed in";
                CloudSyncSignInBtn.Content = "Sign in with GitHub";
                CloudSyncSettingsPanel.Visibility = Visibility.Collapsed;
                DeviceFlowPanel.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                CloudSyncSignInBtn.IsEnabled = false;
                var flow = await svc.BeginDeviceFlowAsync();

                DeviceFlowCodeText.Text = flow.UserCode;
                DeviceFlowPanel.Visibility = Visibility.Visible;

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    flow.VerificationUri) { UseShellExecute = true });

                bool success = await svc.PollForTokenAsync(
                    flow.DeviceCode, flow.Interval, flow.ExpiresIn);

                DeviceFlowPanel.Visibility = Visibility.Collapsed;

                if (success)
                {
                    await svc.EnsureRepoExistsAsync();
                    await svc.RefreshShaCacheAsync();

                    CloudSyncStatusText.Text = $"Signed in as {svc.Username}";
                    CloudSyncSignInBtn.Content = "Sign Out";
                    CloudSyncSettingsPanel.Visibility = Visibility.Visible;

                    // Pull everything down right after first login (fresh-PC restore),
                    // off the UI thread — progress shows in the main window's banner.
                    svc.StartBackgroundSync(new Services.DatabaseService());
                }
                else
                {
                    CloudSyncStatusText.Text = "Authorization failed or timed out";
                }
            }
            catch (Exception ex)
            {
                DeviceFlowPanel.Visibility = Visibility.Collapsed;
                CloudSyncStatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                CloudSyncSignInBtn.IsEnabled = true;
            }
        }

        private void SyncTiming_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressAutoSave) return;
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (cfg == null) return;
            if (SyncOnClose.IsChecked == true) cfg.SyncTiming = "on_close";
            else if (SyncPeriodic.IsChecked == true) cfg.SyncTiming = "periodic";
            else if (SyncManual.IsChecked == true) cfg.SyncTiming = "manual";
            App.Configuration?.SetCloudSyncConfiguration(cfg);
            _ = App.Configuration?.SaveAsync();
        }



        private void SyncEncryption_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressAutoSave) return;
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (cfg == null) return;
            cfg.EncryptionEnabled = SyncEncryptionEnabled.IsChecked == true;
            PassphrasePanel.Visibility = cfg.EncryptionEnabled
                ? Visibility.Visible : Visibility.Collapsed;
            App.Configuration?.SetCloudSyncConfiguration(cfg);
            _ = App.Configuration?.SaveAsync();
        }

        private void SyncPassphraseSave_Click(object sender, RoutedEventArgs e)
        {
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (cfg == null) return;
            string passphrase = SyncPassphraseBox.Password;
            if (string.IsNullOrEmpty(passphrase)) return;
            cfg.PassphraseProtected = Services.GitHubSyncService.ProtectString(passphrase);
            App.Configuration?.SetCloudSyncConfiguration(cfg);
            _ = App.Configuration?.SaveAsync();
            SyncPassphraseBox.Password = "";
            SyncStatusText.Text = "Passphrase saved";
        }

        private async void SyncNow_Click(object sender, RoutedEventArgs e)
        {
            var svc = Services.GitHubSyncService.Instance;
            if (!svc.IsAuthenticated) return;

            SyncNowBtn.IsEnabled = false;
            SyncStatusText.Text = "Syncing...";

            try
            {
                var db = new Services.DatabaseService();
                var result = await Task.Run(() => svc.FullSyncAsync(db));
                SyncStatusText.Text = $"Synced at {DateTime.Now:h:mm tt} — {result.Uploaded} up, {result.Downloaded} down"
                    + (result.Errors > 0 ? $", {result.Errors} errors" : "");
            }
            catch (Exception ex)
            {
                SyncStatusText.Text = $"Sync failed: {ex.Message}";
            }
            finally
            {
                SyncNowBtn.IsEnabled = true;
            }
        }

        // ── Theme panel ───────────────────────────────────────────────────────
        private void LoadThemeSettings()
        {
            var theme = _configService.GetThemeConfiguration();

            // Theme dropdown
            var themes = Services.ThemeService.Instance.GetAvailableThemes();
            ThemeCombo.Items.Clear();
            int selectedIdx = 0;
            for (int i = 0; i < themes.Count; i++)
            {
                ThemeCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                {
                    Content = themes[i].Name,
                    Tag = themes[i].Id
                });
                if (themes[i].Id == theme.ActiveThemeId) selectedIdx = i;
            }
            ThemeCombo.SelectedIndex = selectedIdx;

            PopulateInstalledThemes();
            PopulatePauseEffectsCombo();

            // Background image — show absolute path so the user can see exactly which
            // file is referenced; storage form may be relative under DataRoot.
            BgImagePathLabel.Text = string.IsNullOrWhiteSpace(theme.BackgroundImagePath)
                ? "No image selected"
                : Emutastic.AppPaths.FromStoragePath(theme.BackgroundImagePath);
            BgOpacitySlider.Value = Math.Clamp(theme.BackgroundImageOpacity * 100, 0, 100);
            BgOpacityValueLabel.Text = $"{(int)BgOpacitySlider.Value}%";

            BgStretchCombo.Items.Clear();
            foreach (var s in new[] { "UniformToFill", "Uniform", "Fill", "None" })
                BgStretchCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = s, Tag = s });
            var stretchIdx = theme.BackgroundImageStretch switch
            {
                "Uniform" => 1,
                "Fill" => 2,
                "None" => 3,
                _ => 0
            };
            BgStretchCombo.SelectedIndex = stretchIdx;

            BgRepeatToggle.IsChecked = theme.BackgroundImageRepeat;
            BgZoomSlider.Value = Math.Clamp(theme.BackgroundImageZoom * 100, 50, 500);
            BgZoomValueLabel.Text = $"{(int)BgZoomSlider.Value}%";
            BgOffsetXSlider.Value = Math.Clamp(theme.BackgroundImageOffsetX, -500, 500);
            BgOffsetXValueLabel.Text = $"{(int)BgOffsetXSlider.Value}";
            BgOffsetYSlider.Value = Math.Clamp(theme.BackgroundImageOffsetY, -500, 500);
            BgOffsetYValueLabel.Text = $"{(int)BgOffsetYSlider.Value}";

            // Clamp to valid range in case config was edited manually.
            PaddingSlider.Value  = Math.Clamp(theme.GridPadding, 8, 64);
            SpacingSlider.Value  = Math.Clamp(theme.CardSpacing, 4, 96);
            CardSizeSlider.Value = Math.Clamp(theme.CardWidth, 148, 280);
            WindowsChromeToggle.IsChecked = theme.UseWindowsChrome;
            UpdateSliderLabels();
            ChromeRestartNote.Visibility = Visibility.Collapsed;
        }

        private void UpdateSliderLabels()
        {
            if (PaddingValueLabel  != null) PaddingValueLabel.Text  = $"{(int)PaddingSlider.Value}px";
            if (SpacingValueLabel  != null) SpacingValueLabel.Text  = $"{(int)SpacingSlider.Value}px";
            if (CardSizeValueLabel != null) CardSizeValueLabel.Text = $"{(int)CardSizeSlider.Value}px";
        }

        private void PaddingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => UpdateSliderLabels();

        private void SpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => UpdateSliderLabels();

        private void CardSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => UpdateSliderLabels();

        private void WindowsChromeToggle_Changed(object sender, RoutedEventArgs e)
        {
            // Show a reminder that chrome changes need a restart to take effect.
            ChromeRestartNote.Visibility = Visibility.Visible;
        }

        private void CustomizeThemeBtn_Click(object sender, RoutedEventArgs e)
        {
            var editor = new ThemeEditorWindow { Owner = this };
            editor.ShowDialog();
        }

        private void BgImagePickBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose Background Image",
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == true)
            {
                // Auto-copy into [DataRoot]/Backgrounds/ if the source lives
                // outside DataRoot. Keeps the background working when the user
                // moves a portable install to another drive, or changes their
                // custom data directory after picking the image.
                try
                {
                    string imported = Emutastic.AppPaths.ImportFileToDataRoot(dlg.FileName, "Backgrounds");
                    BgImagePathLabel.Text = imported;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"Background image import failed: {ex.Message}");
                    // Fall back to the raw picker path so the user isn't blocked.
                    BgImagePathLabel.Text = dlg.FileName;
                }
            }
        }

        private void BgImageClearBtn_Click(object sender, RoutedEventArgs e)
        {
            BgImagePathLabel.Text = "No image selected";
        }

        private void BgOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgOpacityValueLabel != null)
                BgOpacityValueLabel.Text = $"{(int)BgOpacitySlider.Value}%";
        }

        // ── Pause effect picker ───────────────────────────────────────────────
        private Views.PauseEffects.PauseEffectRunner? _pauseEffectPreviewRunner;

        private void PopulatePauseEffectsCombo()
        {
            if (PauseEffectCombo == null) return;
            // Avoid the SelectionChanged handler firing while we initialize.
            PauseEffectCombo.SelectionChanged -= PauseEffectCombo_SelectionChanged;
            PauseEffectCombo.Items.Clear();
            foreach (var entry in Views.PauseEffects.PauseEffectRegistry.All)
            {
                PauseEffectCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                {
                    Content = entry.DisplayName,
                    Tag     = entry.Id,
                });
            }
            string savedId = _configService.GetValue("pauseEffect",
                Views.PauseEffects.PauseEffectRegistry.NoneId);
            for (int i = 0; i < PauseEffectCombo.Items.Count; i++)
            {
                if (PauseEffectCombo.Items[i] is System.Windows.Controls.ComboBoxItem cbi
                    && string.Equals((string?)cbi.Tag, savedId, System.StringComparison.OrdinalIgnoreCase))
                {
                    PauseEffectCombo.SelectedIndex = i;
                    break;
                }
            }
            if (PauseEffectCombo.SelectedIndex < 0) PauseEffectCombo.SelectedIndex = 0;

            double savedIntensity = _configService.GetValue("pauseEffectIntensity", 1.0);
            int sliderVal = (int)System.Math.Round(savedIntensity * 100.0);
            if (sliderVal < 50) sliderVal = 50;
            if (sliderVal > 200) sliderVal = 200;
            PauseEffectIntensitySlider.Value = sliderVal;
            PauseEffectIntensityValueLabel.Text = $"{sliderVal}%";

            PauseEffectCombo.SelectionChanged += PauseEffectCombo_SelectionChanged;
            RestartPauseEffectPreview();
        }

        private void PauseEffectCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (PauseEffectCombo?.SelectedItem is not System.Windows.Controls.ComboBoxItem cbi) return;
            string id = (string?)cbi.Tag ?? Views.PauseEffects.PauseEffectRegistry.NoneId;
            _configService.SetValue("pauseEffect", id);
            RestartPauseEffectPreview();
        }

        private void PauseEffectIntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PauseEffectIntensityValueLabel == null) return;
            int pct = (int)PauseEffectIntensitySlider.Value;
            PauseEffectIntensityValueLabel.Text = $"{pct}%";
            _configService.SetValue("pauseEffectIntensity", pct / 100.0);
            RestartPauseEffectPreview();
        }

        private void RestartPauseEffectPreview()
        {
            if (PauseEffectPreview == null) return;
            try
            {
                _pauseEffectPreviewRunner ??= new Views.PauseEffects.PauseEffectRunner(PauseEffectPreview);
                _pauseEffectPreviewRunner.Stop();
                if (PauseEffectCombo?.SelectedItem is not System.Windows.Controls.ComboBoxItem cbi) return;
                string id = (string?)cbi.Tag ?? Views.PauseEffects.PauseEffectRegistry.NoneId;
                if (string.Equals(id, Views.PauseEffects.PauseEffectRegistry.NoneId,
                                  System.StringComparison.OrdinalIgnoreCase))
                {
                    PauseEffectPreview.Visibility = System.Windows.Visibility.Collapsed;
                    return;
                }
                PauseEffectPreview.Visibility = System.Windows.Visibility.Visible;
                double intensity = PauseEffectIntensitySlider.Value / 100.0;
                var instance = Views.PauseEffects.PauseEffectRegistry.Create(id);
                if (instance is Views.PauseEffects.IPauseEffect vec)
                    _pauseEffectPreviewRunner.Start(vec, intensity);
                else if (instance is Views.PauseEffects.IPixelPauseEffect pix)
                    _pauseEffectPreviewRunner.Start(pix, intensity);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"PauseEffect preview failed: {ex.Message}");
            }
        }

        private void BgZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgZoomValueLabel != null)
                BgZoomValueLabel.Text = $"{(int)BgZoomSlider.Value}%";
        }

        private void BgOffsetXSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgOffsetXValueLabel != null)
                BgOffsetXValueLabel.Text = $"{(int)BgOffsetXSlider.Value}";
        }

        private void BgOffsetYSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgOffsetYValueLabel != null)
                BgOffsetYValueLabel.Text = $"{(int)BgOffsetYSlider.Value}";
        }

        private void PopulateInstalledThemes()
        {
            InstalledThemesPanel.Children.Clear();
            var themes = Services.ThemeService.Instance.GetAvailableThemes();
            var activeId = Services.ThemeService.Instance.ActiveThemeId;
            // Pre-parsed Color[] per theme — saves N x 5 ColorConverter.ConvertFromString
            // calls on every Theme-tab open. Cache is rebuilt on ThemesChanged.
            var swatchMap = PreferencesCache.GetThemeSwatches();

            foreach (var (id, name) in themes)
            {
                var colors = swatchMap.TryGetValue(id, out var arr) ? arr : Array.Empty<Color>();

                var card = new Border
                {
                    Width = 120,
                    Margin = new Thickness(0, 0, 8, 8),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(id == activeId ? 2 : 1),
                    BorderBrush = id == activeId
                        ? (Brush)FindResource("AccentBrush")
                        : (Brush)FindResource("BorderNormalBrush"),
                    Background = (Brush)FindResource("BgTertiaryBrush"),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Padding = new Thickness(8),
                    Tag = id,
                };

                var stack = new StackPanel();

                // Color preview strip
                var colorStrip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                foreach (var c in colors)
                {
                    var swatch = new System.Windows.Shapes.Rectangle
                    {
                        Width = 16, Height = 16,
                        RadiusX = 3, RadiusY = 3,
                        Margin = new Thickness(0, 0, 3, 0),
                        Fill = new SolidColorBrush(c),
                    };
                    colorStrip.Children.Add(swatch);
                }
                stack.Children.Add(colorStrip);

                // Theme name
                stack.Children.Add(new TextBlock
                {
                    Text = name,
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = (Brush)FindResource("TextPrimaryBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                // Built-in label or delete
                if (id.StartsWith("builtin."))
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "Built-in",
                        FontSize = 9,
                        Foreground = (Brush)FindResource("TextMutedBrush"),
                    });
                }
                else
                {
                    var delBtn = new Button
                    {
                        Content = "Remove",
                        FontSize = 9,
                        Padding = new Thickness(4, 1, 4, 1),
                        Margin = new Thickness(0, 2, 0, 0),
                        Background = Brushes.Transparent,
                        Foreground = (Brush)FindResource("TrafficRedBrush"),
                        BorderThickness = new Thickness(0),
                        Cursor = System.Windows.Input.Cursors.Hand,
                        Tag = id,
                    };
                    delBtn.Click += RemoveTheme_Click;
                    stack.Children.Add(delBtn);
                }

                card.Child = stack;
                card.MouseLeftButtonDown += ThemeCard_Click;
                InstalledThemesPanel.Children.Add(card);
            }
        }

        private void ThemeCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not Border card || card.Tag is not string id) return;

            // Update the combo to match
            for (int i = 0; i < ThemeCombo.Items.Count; i++)
            {
                if (ThemeCombo.Items[i] is ComboBoxItem item && item.Tag is string itemId && itemId == id)
                {
                    ThemeCombo.SelectedIndex = i;
                    break;
                }
            }

            // Apply immediately
            var themeSvc = Services.ThemeService.Instance;
            themeSvc.LoadAndApplyTheme(id);

            // Save to config
            var theme = _configService.GetThemeConfiguration();
            theme.ActiveThemeId = id;
            _configService.SetThemeConfiguration(theme);
            _ = _configService.SaveAsync();

            // Refresh card highlights
            PopulateInstalledThemes();
        }

        private void RemoveTheme_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string id) return;
            var result = MessageBox.Show($"Remove this theme?", "Remove Theme",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            Services.ThemeService.Instance.UninstallTheme(id);
            LoadThemeSettings(); // Refresh dropdown + cards (includes PopulateInstalledThemes)
        }

        private static string[] GetThemePreviewColors(string themeId)
        {
            var colors = Services.ThemeService.Instance.GetColorsForTheme(themeId);
            return new[] { colors.BgPrimary ?? "#0F0F10", colors.Accent ?? "#E03535", colors.TextPrimary ?? "#F0F0F0",
                           colors.BgSecondary ?? "#181819", colors.Green ?? "#28C840" };
        }

        private void ImportThemeBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Emutastic Theme (*.emutheme)|*.emutheme",
                Title = "Import Theme",
            };

            if (dlg.ShowDialog() != true) return;

            var id = Services.ThemeService.Instance.InstallTheme(dlg.FileName);
            if (id != null)
            {
                LoadThemeSettings();
                PopulateInstalledThemes();
                MessageBox.Show("Theme installed!", "Import Theme", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Could not install theme. Check the file format.",
                    "Import Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ThemeSaveBtn_Click(object sender, RoutedEventArgs e)
        {
            var theme = _configService.GetThemeConfiguration();
            theme.GridPadding = Math.Clamp((int)PaddingSlider.Value,  8,   64);
            theme.CardSpacing = Math.Clamp((int)SpacingSlider.Value,  4,   96);
            theme.CardWidth   = Math.Clamp((int)CardSizeSlider.Value, 148, 280);
            theme.UseWindowsChrome = WindowsChromeToggle.IsChecked == true;

            // Theme selection
            var selectedThemeId = "builtin.dark";
            if (ThemeCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string id)
                selectedThemeId = id;
            theme.ActiveThemeId = selectedThemeId;

            // Background image settings — relativize against DataRoot so portable installs
            // survive drive-letter changes; absolute external paths pass through unchanged.
            var bgPath = BgImagePathLabel.Text;
            theme.BackgroundImagePath = (bgPath == "No image selected")
                ? ""
                : Emutastic.AppPaths.ToStoragePath(bgPath);
            theme.BackgroundImageOpacity = Math.Clamp(BgOpacitySlider.Value / 100.0, 0.0, 1.0);
            if (BgStretchCombo.SelectedItem is System.Windows.Controls.ComboBoxItem stretchItem
                && stretchItem.Tag is string stretchVal)
                theme.BackgroundImageStretch = stretchVal;
            theme.BackgroundImageRepeat = BgRepeatToggle.IsChecked == true;
            theme.BackgroundImageZoom = Math.Clamp(BgZoomSlider.Value / 100.0, 0.5, 5.0);
            theme.BackgroundImageOffsetX = Math.Clamp(BgOffsetXSlider.Value, -500, 500);
            theme.BackgroundImageOffsetY = Math.Clamp(BgOffsetYSlider.Value, -500, 500);

            _configService.SetThemeConfiguration(theme);
            _ = _configService.SaveAsync();
            App.ApplyThemeResources();

            // Apply the selected theme colors
            var themeSvc = Services.ThemeService.Instance;
            themeSvc.LoadAndApplyTheme(selectedThemeId);

            // Apply background image to MainWindow
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ApplyBackgroundImage();
        }

        // ── Snaps panel ───────────────────────────────────────────────────────
        private void LoadSnapsSettings()
        {
            _suppressAutoSave = true;
            _snapsLoaded = true;
            var snap = _configService.GetSnapConfiguration();
            SSEnabledToggle.IsChecked = snap.ScreenScraperEnabled;
            SSUsernameBox.Text        = snap.ScreenScraperUser;
            SSPasswordBox.Password    = snap.ScreenScraperPassword;
            SSPrefer2DToggle.IsChecked = snap.PreferScreenScraper2D;
            // Only enable the 2D preference toggle when SS credentials are configured
            SSPrefer2DToggle.IsEnabled = snap.ScreenScraperEnabled
                && !string.IsNullOrWhiteSpace(snap.ScreenScraperUser);
            _suppressAutoSave = false;
        }

        private void SnapProvider_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelSS == null || PanelEM == null) return;
            bool isSS = sender == SnapProviderSS;
            PanelSS.Visibility = isSS ? Visibility.Visible : Visibility.Collapsed;
            PanelEM.Visibility = isSS ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SSEnabled_Changed(object sender, RoutedEventArgs e)
        {
            // Enable/disable the 2D preference toggle based on SS enabled state + credentials
            if (SSPrefer2DToggle != null)
            {
                bool ssOn = SSEnabledToggle.IsChecked == true
                    && !string.IsNullOrWhiteSpace(SSUsernameBox.Text);
                SSPrefer2DToggle.IsEnabled = ssOn;
                if (!ssOn) SSPrefer2DToggle.IsChecked = false;
            }
            SaveSnapSettings();
        }

        private async void SSTestBtn_Click(object sender, RoutedEventArgs e)
        {
            SSTestBtn.IsEnabled = false;
            SSStatusLabel.Text  = "Testing…";
            SSStatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");

            var ss = new Emutastic.Services.ScreenScraperService();
            var (error, maxThreads) = await ss.TestLoginAsync(SSUsernameBox.Text.Trim(), SSPasswordBox.Password);

            if (error == null)
            {
                SSStatusLabel.Text = $"Verified — {maxThreads} thread{(maxThreads == 1 ? "" : "s")} available";
                // Save the thread limit and apply it immediately
                var snap = _configService.GetSnapConfiguration();
                snap.ScreenScraperMaxThreads = maxThreads;
                _configService.SetSnapConfiguration(snap);
                _ = _configService.SaveAsync();
                Services.ScreenScraperService.SetMaxThreads(maxThreads);
            }
            else
            {
                SSStatusLabel.Text = error;
            }
            SSStatusLabel.Foreground = error == null
                ? System.Windows.Media.Brushes.LightGreen
                : (System.Windows.Media.Brush)FindResource("AccentBrush");
            SSTestBtn.IsEnabled = true;

            if (error == null)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                timer.Tick += (_, _) => { SSStatusLabel.Text = ""; timer.Stop(); };
                timer.Start();
            }
        }

        private void SaveSnapSettings()
        {
            if (SSEnabledToggle == null || _suppressAutoSave || !_snapsLoaded) return;
            var snap = _configService.GetSnapConfiguration();
            snap.ScreenScraperEnabled  = SSEnabledToggle.IsChecked == true;
            snap.ScreenScraperUser     = SSUsernameBox.Text.Trim();
            snap.ScreenScraperPassword = SSPasswordBox.Password;
            snap.PreferScreenScraper2D = SSPrefer2DToggle.IsChecked == true;
            _configService.SetSnapConfiguration(snap);
            _ = _configService.SaveAsync();
            Models.Game.PreferScreenScraper2D = snap.PreferScreenScraper2D;
        }

        private void SSPrefer2D_Changed(object sender, RoutedEventArgs e)
            => SaveSnapSettings();

        // ── EmuTV panel ───────────────────────────────────────────────────────
        // Buttons offered for the EmuTV navigation rebinds (A/B are reserved for play/back).
        private static readonly string[] EmuTvButtons = { "Y", "X", "Back", "Start", "L1", "R1" };

        private void LoadEmuTvSettings()
        {
            _suppressAutoSave = true;
            var cfg = _configService.GetEmuTvConfiguration();
            SgdbEnabledToggle.IsChecked = cfg.SteamGridDbEnabled;
            SgdbTokenBox.Text           = cfg.SteamGridDbToken;
            if (HkThemeBrowser.Items.Count == 0)
                foreach (var b in EmuTvButtons) { HkThemeBrowser.Items.Add(b); HkSaveStates.Items.Add(b); }
            HkThemeBrowser.SelectedItem = cfg.HotkeyOverrides.TryGetValue("theme_browser", out var tb) ? tb : "Y";
            HkSaveStates.SelectedItem   = cfg.HotkeyOverrides.TryGetValue("save_states", out var ss) ? ss : "Start";
            _suppressAutoSave = false;
        }

        private void EmuTvSetting_Changed(object sender, RoutedEventArgs e) => SaveEmuTvSettings();
        private void EmuTvHotkey_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SaveEmuTvSettings();

        private void SaveEmuTvSettings()
        {
            if (_suppressAutoSave) return;       // only suppressed while LoadEmuTvSettings populates controls
            var cfg = _configService.GetEmuTvConfiguration();
            cfg.SteamGridDbEnabled = SgdbEnabledToggle.IsChecked == true;
            cfg.SteamGridDbToken   = SgdbTokenBox.Text.Trim();
            cfg.HotkeyOverrides["theme_browser"] = HkThemeBrowser.SelectedItem as string ?? "Y";
            cfg.HotkeyOverrides["save_states"]   = HkSaveStates.SelectedItem as string ?? "Start";
            _configService.SetEmuTvConfiguration(cfg);
            _ = _configService.SaveAsync();
        }

        private async void SgdbTestBtn_Click(object sender, RoutedEventArgs e)
        {
            // Persist the token directly and AWAIT the write, so a verified token is always saved
            // regardless of panel-load state (the auto-save path can be skipped/fire-and-forget).
            var saveCfg = _configService.GetEmuTvConfiguration();
            saveCfg.SteamGridDbToken   = SgdbTokenBox.Text.Trim();
            saveCfg.SteamGridDbEnabled = SgdbEnabledToggle.IsChecked == true;
            _configService.SetEmuTvConfiguration(saveCfg);
            await _configService.SaveAsync();

            SgdbTestBtn.IsEnabled = false;
            SgdbStatusLabel.Text = "Verifying…";
            SgdbStatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");

            string? error = await new Services.SteamGridDbService().TestTokenAsync(SgdbTokenBox.Text);

            SgdbStatusLabel.Text = error ?? "Verified — your SteamGridDB token works.";
            SgdbStatusLabel.Foreground = error == null
                ? System.Windows.Media.Brushes.LightGreen
                : (System.Windows.Media.Brush)FindResource("AccentBrush");
            SgdbTestBtn.IsEnabled = true;

            if (error == null)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timer.Tick += (_, _) => { SgdbStatusLabel.Text = ""; timer.Stop(); };
                timer.Start();
            }
        }

        private void SnapsSaveBtn_Click(object sender, RoutedEventArgs e)
            => SaveSnapSettings();

        // ── Achievements tab ─────────────────────────────────────────────────

        private void LoadAchievementsSettings()
        {
            _suppressAutoSave = true;
            _achievementsLoaded = true;
            var ra = _configService.GetRetroAchievementsConfiguration();
            RAUsernameBox.Text         = ra.Username;
            RAPasswordBox.Password     = ra.Password;
            RAApiKeyBox.Password       = ra.ApiKey;
            RAHardcoreToggle.IsChecked = ra.HardcoreMode;
            RASyncFollowsToggle.IsChecked = ra.SyncFollowsOnLaunch;
            RATokenStatus.Text = !string.IsNullOrEmpty(ra.Token)
                ? "Login token saved — password not required for future sessions."
                : "No login token yet — password required for first login.";

            // Toast-appearance customizer (built once, repopulated on every entry).
            BuildToastAppearanceUI();
            PopulateToastAppearance();

            _suppressAutoSave = false;
        }

        private async void RATestBtn_Click(object sender, RoutedEventArgs e)
        {
            RATestBtn.IsEnabled = false;
            RAStatusLabel.Text  = "Testing…";
            RAStatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");

            var svc = new Services.RetroAchievementsService();
            var (error, token) = await svc.TestLoginAsync(RAUsernameBox.Text.Trim(), RAPasswordBox.Password);

            if (error == null && !string.IsNullOrEmpty(token))
            {
                var ra = _configService.GetRetroAchievementsConfiguration();
                ra.Token = token;
                ra.Username = RAUsernameBox.Text.Trim();
                ra.Password = RAPasswordBox.Password;
                _configService.SetRetroAchievementsConfiguration(ra);
                _ = _configService.SaveAsync();
                RATokenStatus.Text = "Login token saved — password not required for future sessions.";
            }

            RAStatusLabel.Text = error == null ? "Connected" : error;
            RAStatusLabel.Foreground = error == null
                ? System.Windows.Media.Brushes.LightGreen
                : (System.Windows.Media.Brush)FindResource("AccentBrush");
            RATestBtn.IsEnabled = true;
        }

        private void SaveAchievementsSettings()
        {
            if (RAUsernameBox == null || _suppressAutoSave || !_achievementsLoaded) return;
            var ra = _configService.GetRetroAchievementsConfiguration();
            ra.Username     = RAUsernameBox.Text.Trim();
            ra.Password     = RAPasswordBox.Password;
            ra.ApiKey       = RAApiKeyBox.Password.Trim();
            ra.HardcoreMode = RAHardcoreToggle.IsChecked == true;
            ra.SyncFollowsOnLaunch = RASyncFollowsToggle.IsChecked == true;
            _configService.SetRetroAchievementsConfiguration(ra);
            _ = _configService.SaveAsync();
        }

        private void RAHardcore_Changed(object sender, RoutedEventArgs e)
            => SaveAchievementsSettings();

        // Toggle handler: writes config only — never triggers a sync.
        // The actual sync fires from MainWindow.OnLoaded so "takes effect
        // next launch" is honest and predictable. Re-firing during
        // LoadAchievementsSettings is suppressed by the _suppressAutoSave
        // gate inside SaveAchievementsSettings.
        private void RASyncFollows_Changed(object sender, RoutedEventArgs e)
            => SaveAchievementsSettings();

        // Pasting the API key fires PasswordChanged once per character; debounce
        // so we don't write a half-typed value to settings.json. 400ms after the
        // last keystroke we commit. Cancelled and restarted on each keystroke.
        private System.Threading.CancellationTokenSource? _raApiKeyDebounceCts;
        private void RAApiKey_Changed(object sender, RoutedEventArgs e)
        {
            _raApiKeyDebounceCts?.Cancel();
            _raApiKeyDebounceCts = new System.Threading.CancellationTokenSource();
            var token = _raApiKeyDebounceCts.Token;
            _ = System.Threading.Tasks.Task.Delay(400, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Dispatcher.Invoke(SaveAchievementsSettings);
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private void RASaveBtn_Click(object sender, RoutedEventArgs e)
            => SaveAchievementsSettings();

        // ── Toast appearance customizer ────────────────────────────────────────
        // Controls are built procedurally into TsAppearanceHost (ThemeEditor pattern)
        // so load/save stays centralized and we avoid ~30 named XAML elements. Each
        // control writes straight to the live ToastStyle and debounces the disk write.
        private bool _toastUiBuilt;
        private bool _populatingToast;
        private readonly List<Action> _toastPopulators = new();
        private System.Threading.CancellationTokenSource? _toastSaveCts;
        private List<string>? _systemFontNamesCache;
        private List<string> SystemFontNames =>
            _systemFontNamesCache ??= System.Windows.Media.Fonts.SystemFontFamilies
                .Select(f => f.Source).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        // Live config's style object. GetRetroAchievementsConfiguration returns the
        // live instance, so mutating .ToastStyle here is what the toast reads.
        private AchievementToastStyle TsStyle => _configService.GetRetroAchievementsConfiguration().ToastStyle;

        private void BuildToastAppearanceUI()
        {
            if (_toastUiBuilt || TsAppearanceHost == null) return;
            _toastUiBuilt = true;

            AddComboRow("Shape", new[] { "Card", "Pill", "Rounded", "Sharp", "Custom" },
                s => s.Shape, (s, v) => s.Shape = v);
            _rowCornerRadius = AddSliderRow("Corner radius (Custom shape)", 0, 40, 1, true, "",
                s => s.CornerRadius, (s, v) => s.CornerRadius = v);

            AddSectionGap("Background");
            AddToggleRow("Use gradient (off = solid color)", s => s.UseGradient, (s, v) => s.UseGradient = v);
            _rowGradStart = AddColorRow("Gradient start", s => s.GradientStart, (s, v) => s.GradientStart = v);
            _rowGradEnd = AddColorRow("Gradient end", s => s.GradientEnd, (s, v) => s.GradientEnd = v);
            _rowSolidColor = AddColorRow("Solid color", s => s.BackgroundColor, (s, v) => s.BackgroundColor = v);
            // Always-visible overall transparency (applies to gradient, solid, and image).
            AddSliderRow("Background opacity", 0, 100, 1, true, "%",
                s => s.BackgroundOpacity, (s, v) => s.BackgroundOpacity = (int)Math.Round(v));
            AddImageRow();

            AddSectionGap("Border");
            AddColorRow("Border color (blank = theme)", s => s.BorderColor, (s, v) => s.BorderColor = v);
            AddSliderRow("Border thickness", 0, 5, 0.5, false, "",
                s => s.BorderThickness, (s, v) => s.BorderThickness = v);

            AddSectionGap("Drop shadow");
            AddToggleRow("Enable drop shadow", s => s.ShadowEnabled, (s, v) => s.ShadowEnabled = v);
            AddColorRow("Shadow color", s => s.ShadowColor, (s, v) => s.ShadowColor = v);
            AddSliderRow("Shadow opacity", 0, 100, 1, true, "%",
                s => s.ShadowOpacity, (s, v) => s.ShadowOpacity = (int)Math.Round(v));
            AddSliderRow("Shadow blur", 0, 60, 1, true, "",
                s => s.ShadowBlur, (s, v) => s.ShadowBlur = v);
            AddSliderRow("Shadow depth", 0, 30, 1, true, "",
                s => s.ShadowDepth, (s, v) => s.ShadowDepth = v);

            AddSectionGap("Badge");
            AddToggleRow("Show badge", s => s.ShowBadge, (s, v) => s.ShowBadge = v);
            _rowBadgeColor = AddColorRow("Badge frame (blank = theme)", s => s.BadgeFrameColor, (s, v) => s.BadgeFrameColor = v);

            AddSectionGap("Header");
            AddToggleRow("Show header", s => s.ShowHeader, (s, v) => s.ShowHeader = v);
            _rowHeaderColor = AddColorRow("Header color (blank = theme)", s => s.HeaderColor, (s, v) => s.HeaderColor = v);
            _rowHeaderSize = AddSliderRow("Header size", 6, 20, 0.5, false, "",
                s => s.HeaderSize, (s, v) => s.HeaderSize = v);

            AddSectionGap("Title");
            AddColorRow("Title color", s => s.TitleColor, (s, v) => s.TitleColor = v);
            AddFontRow("Title font", s => s.TitleFont, (s, v) => s.TitleFont = v);
            AddSliderRow("Title size", 8, 32, 0.5, false, "",
                s => s.TitleSize, (s, v) => s.TitleSize = v);

            AddSectionGap("Description");
            AddColorRow("Description color", s => s.DescColor, (s, v) => s.DescColor = v);
            AddFontRow("Description font", s => s.DescFont, (s, v) => s.DescFont = v);
            AddSliderRow("Description size", 8, 24, 0.5, false, "",
                s => s.DescSize, (s, v) => s.DescSize = v);

            AddSectionGap("Points");
            AddColorRow("Points color (blank = theme)", s => s.PointsColor, (s, v) => s.PointsColor = v);
            AddSliderRow("Points size", 6, 20, 0.5, false, "",
                s => s.PointsSize, (s, v) => s.PointsSize = v);

            AddSectionGap("Layout");
            AddComboRow("Position",
                new[] { "TopLeft", "TopCenter", "TopRight", "BottomLeft", "BottomCenter", "BottomRight" },
                s => s.Position, (s, v) => s.Position = v);
            AddSliderRow("Duration (seconds)", 1, 12, 0.5, false, " s",
                s => s.DurationSec, (s, v) => s.DurationSec = v);

            BuildToastPreview();
        }

        // Dependent rows shown/hidden based on current selections so the panel only
        // surfaces controls that actually apply.
        private FrameworkElement? _rowGradStart, _rowGradEnd, _rowSolidColor,
                                 _rowCornerRadius, _rowBadgeColor,
                                 _rowHeaderColor, _rowHeaderSize;

        private void UpdateToastConditionalRows()
        {
            if (!_toastUiBuilt) return;
            var s = TsStyle;
            void Show(FrameworkElement? el, bool vis)
            {
                if (el != null) el.Visibility = vis ? Visibility.Visible : Visibility.Collapsed;
            }

            bool gradient = s.UseGradient;
            Show(_rowGradStart, gradient);
            Show(_rowGradEnd, gradient);
            Show(_rowSolidColor, !gradient);
            // Background-opacity slider is always visible (applies to every mode).

            Show(_rowCornerRadius, string.Equals(s.Shape, "Custom", StringComparison.OrdinalIgnoreCase));
            Show(_rowBadgeColor, s.ShowBadge);
            Show(_rowHeaderColor, s.ShowHeader);
            Show(_rowHeaderSize, s.ShowHeader);
        }

        private void PopulateToastAppearance()
        {
            if (!_toastUiBuilt) return;
            _populatingToast = true;
            try { foreach (var p in _toastPopulators) p(); }
            finally { _populatingToast = false; }
            UpdateToastConditionalRows();
            RefreshToastPreview();
        }

        // ── Live preview ───────────────────────────────────────────────────────
        // A non-interactive replica of the toast rendered by the SAME
        // ToastStyleRenderer the live toast uses, so what you see is what you get.
        private Border? _pvRoot, _pvBadge;
        private TextBlock? _pvHeader, _pvTitle, _pvDesc, _pvPoints;
        private readonly Dictionary<string, ImageSource> _previewImageCache = new();

        private void BuildToastPreview()
        {
            if (_pvRoot != null || TsPreviewHost == null) return;

            // Neutral backdrop so transparency / shadow read against something.
            var backdrop = new Border
            {
                Background = (Brush)FindResource("BgTertiaryBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                MinHeight = 120,
            };

            _pvRoot = new Border
            {
                MinWidth = 320, MaxWidth = 480,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var grid = new Grid { Margin = new Thickness(10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _pvBadge = new Border
            {
                Width = 58, Height = 58, CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center,
                Child = new MaterialDesignThemes.Wpf.PackIcon
                {
                    Kind = MaterialDesignThemes.Wpf.PackIconKind.Trophy,
                    Width = 30, Height = 30,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White,
                },
            };
            Grid.SetColumn(_pvBadge, 0);

            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
            Grid.SetColumn(panel, 1);
            _pvHeader = new TextBlock { Text = "ACHIEVEMENT UNLOCKED", FontWeight = FontWeights.SemiBold, Opacity = 0.8 };
            _pvTitle  = new TextBlock { Text = "Sample Achievement", FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            _pvDesc   = new TextBlock { Text = "Earn this by doing the thing.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            _pvPoints = new TextBlock { Text = "10 points", Opacity = 0.9, Margin = new Thickness(0, 3, 0, 0) };
            panel.Children.Add(_pvHeader);
            panel.Children.Add(_pvTitle);
            panel.Children.Add(_pvDesc);
            panel.Children.Add(_pvPoints);

            grid.Children.Add(_pvBadge);
            grid.Children.Add(panel);
            _pvRoot.Child = grid;
            backdrop.Child = _pvRoot;
            TsPreviewHost.Content = backdrop;
        }

        private void RefreshToastPreview()
        {
            if (_pvRoot == null) return;
            // Clear any leftover fade animation so edits are always visible.
            _pvRoot.BeginAnimation(OpacityProperty, null);
            _pvRoot.Opacity = 1;

            Services.ToastStyleRenderer.ApplyTo(
                _pvRoot, _pvBadge!, _pvHeader!, _pvTitle!, _pvDesc!, _pvPoints!,
                TsStyle, LoadPreviewImage);

            // The renderer sets alignment/margin for on-screen Position; in the preview
            // box we always keep the replica centered (Position is described by its label).
            _pvRoot.HorizontalAlignment = HorizontalAlignment.Center;
            _pvRoot.VerticalAlignment   = VerticalAlignment.Center;
            _pvRoot.Margin              = new Thickness(0);
        }

        private ImageSource? LoadPreviewImage(string path)
        {
            try
            {
                if (_previewImageCache.TryGetValue(path, out var cached)) return cached;
                if (!System.IO.File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                if (bmp.CanFreeze) bmp.Freeze();
                _previewImageCache[path] = bmp;
                return bmp;
            }
            catch { return null; }
        }

        private System.Windows.Threading.DispatcherTimer? _pvAnimTimer;
        private void TsPreviewAnim_Click(object sender, RoutedEventArgs e)
        {
            if (_pvRoot == null) return;
            // Reuse a single timer so spamming the button restarts cleanly rather than
            // stacking overlapping fade-out cycles.
            _pvAnimTimer?.Stop();
            RefreshToastPreview();
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
            _pvRoot.BeginAnimation(OpacityProperty, fadeIn);
            _pvAnimTimer ??= new System.Windows.Threading.DispatcherTimer();
            _pvAnimTimer.Interval = Services.ToastStyleRenderer.Duration(TsStyle);
            _pvAnimTimer.Tick -= PvAnimTimer_Tick;
            _pvAnimTimer.Tick += PvAnimTimer_Tick;
            _pvAnimTimer.Start();
        }

        private void PvAnimTimer_Tick(object? sender, EventArgs e)
        {
            _pvAnimTimer?.Stop();
            if (_pvRoot == null) return;
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
            _pvRoot.BeginAnimation(OpacityProperty, fadeOut);
        }

        // Sets the live ToastStyle dirty and debounces the disk write (400ms after the
        // last change) so dragging a slider doesn't thrash settings.json.
        private void CommitToastStyle()
        {
            if (_suppressAutoSave || !_achievementsLoaded) return;
            UpdateToastConditionalRows();
            RefreshToastPreview();
            _configService.SetRetroAchievementsConfiguration(_configService.GetRetroAchievementsConfiguration());
            _toastSaveCts?.Cancel();
            _toastSaveCts = new System.Threading.CancellationTokenSource();
            var token = _toastSaveCts.Token;
            _ = System.Threading.Tasks.Task.Delay(400, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Dispatcher.Invoke(() => { _ = _configService.SaveAsync(); });
            }, System.Threading.Tasks.TaskScheduler.Default);
            // Phase 5 will refresh the live preview here.
        }

        private void TsReset_Click(object sender, RoutedEventArgs e)
        {
            var ra = _configService.GetRetroAchievementsConfiguration();
            ra.ToastStyle = new AchievementToastStyle();
            _configService.SetRetroAchievementsConfiguration(ra);
            PopulateToastAppearance();
            _ = _configService.SaveAsync();
        }

        // ── Row builders ───────────────────────────────────────────────────────
        // Returns the row container so callers can toggle its visibility for
        // dependent/conditional controls.
        private FrameworkElement AddRow(string label, FrameworkElement control)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 0),
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            };
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(control, 1);
            control.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(lbl);
            grid.Children.Add(control);
            TsAppearanceHost.Children.Add(grid);
            return grid;
        }

        private void AddSectionGap(string header)
        {
            TsAppearanceHost.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                Margin = new Thickness(0, 14, 0, 8),
            });
        }

        private FrameworkElement AddColorRow(string label,
            Func<AchievementToastStyle, string> get, Action<AchievementToastStyle, string> set)
        {
            var swatch = new System.Windows.Shapes.Rectangle
            {
                Width = 22, Height = 22, RadiusX = 4, RadiusY = 4,
                Stroke = (Brush)FindResource("BorderNormalBrush"), StrokeThickness = 1,
                Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            var hex = new TextBox { Width = 110, Style = (Style)FindResource("DarkTextBox") };
            // Mouse and keyboard both open the picker (the hex box is also editable).
            swatch.Focusable = true;
            swatch.MouseLeftButtonUp += (_, __) => PickColorInto(hex);
            swatch.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter || ke.Key == Key.Space) { PickColorInto(hex); ke.Handled = true; }
            };
            hex.TextChanged += (_, __) =>
            {
                UpdateSwatch(swatch, hex.Text);
                if (_populatingToast) return;
                set(TsStyle, hex.Text.Trim());
                CommitToastStyle();
            };
            _toastPopulators.Add(() => { hex.Text = get(TsStyle); UpdateSwatch(swatch, hex.Text); });

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(swatch);
            panel.Children.Add(hex);
            return AddRow(label, panel);
        }

        private void UpdateSwatch(System.Windows.Shapes.Rectangle swatch, string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                swatch.Fill = Brushes.Transparent;
                swatch.ToolTip = "Blank = theme default";
                return;
            }
            try
            {
                swatch.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex.Trim()));
                swatch.ToolTip = hex.Trim();
            }
            catch
            {
                swatch.Fill = Brushes.Transparent;
                swatch.ToolTip = "Invalid color";
            }
        }

        // WinForms ColorDialog (RGB-only) — same picker ThemeEditor uses. Writing the
        // hex back triggers the TextChanged handler, which updates the swatch and saves.
        private void PickColorInto(TextBox hex)
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
            try
            {
                if (!string.IsNullOrWhiteSpace(hex.Text))
                {
                    var c = (Color)ColorConverter.ConvertFromString(hex.Text.Trim());
                    dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
                }
            }
            catch { /* seed left at default */ }

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var c = dlg.Color;
                hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
        }

        private FrameworkElement AddSliderRow(string label, double min, double max, double step, bool integer, string suffix,
            Func<AchievementToastStyle, double> get, Action<AchievementToastStyle, double> set)
        {
            var slider = new Slider
            {
                Minimum = min, Maximum = max, Width = 160,
                TickFrequency = step, IsSnapToTickEnabled = step > 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var valueLbl = new TextBlock
            {
                Width = 54, FontSize = 12, Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            };
            string Fmt(double v) => (integer ? ((int)Math.Round(v)).ToString() : v.ToString("0.#")) + suffix;
            slider.ValueChanged += (_, __) =>
            {
                valueLbl.Text = Fmt(slider.Value);
                if (_populatingToast) return;
                set(TsStyle, slider.Value);
                CommitToastStyle();
            };
            _toastPopulators.Add(() => { slider.Value = get(TsStyle); valueLbl.Text = Fmt(slider.Value); });

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(slider);
            panel.Children.Add(valueLbl);
            return AddRow(label, panel);
        }

        private void AddToggleRow(string label,
            Func<AchievementToastStyle, bool> get, Action<AchievementToastStyle, bool> set)
        {
            var t = new System.Windows.Controls.Primitives.ToggleButton
            {
                Style = (Style)FindResource("ToggleSwitch"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            RoutedEventHandler h = (_, __) =>
            {
                if (_populatingToast) return;
                set(TsStyle, t.IsChecked == true);
                CommitToastStyle();
            };
            t.Checked += h;
            t.Unchecked += h;
            _toastPopulators.Add(() => t.IsChecked = get(TsStyle));
            AddRow(label, t);
        }

        private void AddComboRow(string label, string[] items,
            Func<AchievementToastStyle, string> get, Action<AchievementToastStyle, string> set)
        {
            var combo = new ComboBox
            {
                Width = 180,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("PrefComboBox"),
            };
            foreach (var it in items) combo.Items.Add(it);
            combo.SelectionChanged += (_, __) =>
            {
                if (_populatingToast) return;
                if (combo.SelectedItem is string v) { set(TsStyle, v); CommitToastStyle(); }
            };
            _toastPopulators.Add(() =>
            {
                var cur = get(TsStyle);
                combo.SelectedItem = items.FirstOrDefault(x => string.Equals(x, cur, StringComparison.OrdinalIgnoreCase))
                                     ?? items.First();
            });
            AddRow(label, combo);
        }

        private void AddFontRow(string label,
            Func<AchievementToastStyle, string> get, Action<AchievementToastStyle, string> set)
        {
            const string sentinel = "Default (theme)";
            var combo = new ComboBox
            {
                Width = 180,
                MaxDropDownHeight = 300,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("PrefComboBox"),
            };
            combo.Items.Add(sentinel);
            foreach (var fam in SystemFontNames) combo.Items.Add(fam);
            combo.SelectionChanged += (_, __) =>
            {
                if (_populatingToast) return;
                if (combo.SelectedItem is string v) { set(TsStyle, v == sentinel ? "" : v); CommitToastStyle(); }
            };
            _toastPopulators.Add(() =>
            {
                var cur = get(TsStyle);
                combo.SelectedItem = string.IsNullOrWhiteSpace(cur)
                    ? sentinel
                    : (SystemFontNames.Contains(cur) ? cur : sentinel);
            });
            AddRow(label, combo);
        }

        private void AddImageRow()
        {
            var pathLbl = new TextBlock
            {
                Text = "No image", FontSize = 11, MaxWidth = 150,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                Foreground = (Brush)FindResource("TextMutedBrush"),
            };
            var browse = new Button
            {
                Content = "Browse…", Style = (Style)FindResource("SecondaryBtn"),
                Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 0),
            };
            var clear = new Button
            {
                Content = "Clear", Style = (Style)FindResource("SecondaryBtn"),
                Padding = new Thickness(10, 4, 10, 4),
            };
            browse.Click += (_, __) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Choose Toast Background Image",
                    Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files (*.*)|*.*",
                };
                if (dlg.ShowDialog(this) == true)
                {
                    try { TsStyle.BackgroundImage = Emutastic.AppPaths.ImportFileToDataRoot(dlg.FileName, "ToastBackgrounds"); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"Toast background import failed: {ex.Message}");
                        TsStyle.BackgroundImage = dlg.FileName;
                    }
                    pathLbl.Text = System.IO.Path.GetFileName(TsStyle.BackgroundImage);
                    CommitToastStyle();
                }
            };
            clear.Click += (_, __) =>
            {
                TsStyle.BackgroundImage = "";
                pathLbl.Text = "No image";
                CommitToastStyle();
            };
            _toastPopulators.Add(() => pathLbl.Text = string.IsNullOrWhiteSpace(TsStyle.BackgroundImage)
                ? "No image" : System.IO.Path.GetFileName(TsStyle.BackgroundImage));

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(pathLbl);
            panel.Children.Add(browse);
            panel.Children.Add(clear);
            AddRow("Background image", panel);
        }

        // ── About tab ─────────────────────────────────────────────────────────
        // Shows the installed app version, queries GitHub for the latest release,
        // and provides links to the release page + repo. Read-only — no auto-update.

        private const string GitHubRepoUrl = "https://github.com/codingncaffeine/Emutastic";
        private const string GitHubLatestApiUrl = "https://api.github.com/repos/codingncaffeine/Emutastic/releases/latest";
        private const string GitHubReleasesUrl = "https://github.com/codingncaffeine/Emutastic/releases";

        private static readonly System.Net.Http.HttpClient _aboutHttp = CreateAboutHttp();
        private string? _latestReleaseUrl;
        private bool _aboutLoaded;

        private static System.Net.Http.HttpClient CreateAboutHttp()
        {
            // Per-call cancellation enforces a 3s budget via PreferencesCache;
            // an HttpClient-wide Timeout would tear down concurrent requests too.
            var http = new System.Net.Http.HttpClient
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan
            };
            // GitHub API rejects requests without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Emutastic/about-tab");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        private void LoadAboutSettings()
        {
            // Installed version comes from the assembly (set in csproj via auto-versioning,
            // or defaults to 1.0.0.0 in dev builds). Strip the trailing ".0" if present
            // so it reads as "1.3.10" instead of "1.3.10.0".
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string installedDisplay = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v?.?.?";
            AboutInstalledVersionText.Text = installedDisplay;

            // Only hit GitHub once per window lifetime — re-clicking the tab shouldn't
            // hammer the API. The "Check Again" button forces a re-fetch.
            if (!_aboutLoaded)
            {
                _aboutLoaded = true;
                _ = CheckLatestReleaseAsync();
            }
        }

        private async Task CheckLatestReleaseAsync()
        {
            AboutLatestVersionText.Text = "Checking…";
            AboutUpdateStatusText.Text = "";
            AboutOpenLatestReleaseBtn.Visibility = Visibility.Collapsed;
            AboutRecheckBtn.IsEnabled = false;

            try
            {
                var release = await PreferencesCache.GetGitHubLatestAsync(
                    _aboutHttp, GitHubLatestApiUrl, _windowCts.Token).ConfigureAwait(true);

                // User may have closed Preferences while the request was in flight.
                if (!IsLoaded) return;

                if (release == null)
                {
                    AboutLatestVersionText.Text = "—";
                    AboutUpdateStatusText.Text = "Could not reach GitHub. Check your connection or try again later.";
                    return;
                }

                string tagName = release.Tag;
                _latestReleaseUrl = string.IsNullOrWhiteSpace(release.Url) ? GitHubReleasesUrl : release.Url;

                AboutLatestVersionText.Text = tagName;

                if (TryCompareVersions(tagName, out int comparison))
                {
                    if (comparison > 0)
                    {
                        AboutUpdateStatusText.Text = "A newer release is available.";
                        AboutUpdateStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
                        AboutOpenLatestReleaseBtn.Visibility = Visibility.Visible;
                    }
                    else if (comparison < 0)
                    {
                        AboutUpdateStatusText.Text = "Your installed version is newer than the latest release. (You're probably running a development build.)";
                        AboutUpdateStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                    }
                    else
                    {
                        AboutUpdateStatusText.Text = "You're running the latest release.";
                        AboutUpdateStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                    }
                }
                else
                {
                    AboutUpdateStatusText.Text = "Could not compare versions — open the release on GitHub for details.";
                    AboutOpenLatestReleaseBtn.Visibility = Visibility.Visible;
                }
            }
            catch (OperationCanceledException)
            {
                if (!IsLoaded) return;
                AboutLatestVersionText.Text = "—";
                AboutUpdateStatusText.Text = "Network request timed out. Try again later.";
            }
            catch (Exception ex)
            {
                if (!IsLoaded) return;
                AboutLatestVersionText.Text = "—";
                AboutUpdateStatusText.Text = $"Could not check for updates: {ex.Message}";
            }
            finally
            {
                if (IsLoaded) AboutRecheckBtn.IsEnabled = true;
            }
        }

        /// <summary>
        /// Compare installed version (Assembly) against a GitHub tag like "v1.3.10".
        /// comparison: positive = remote newer, negative = local newer, zero = equal.
        /// Returns false when either side is unparseable.
        /// </summary>
        private static bool TryCompareVersions(string remoteTag, out int comparison)
        {
            comparison = 0;
            string trimmed = remoteTag.TrimStart('v', 'V').Trim();
            if (!Version.TryParse(trimmed, out var remote)) return false;
            var local = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (local == null) return false;
            // Only compare Major.Minor.Build — the user-visible release scheme is 3-part.
            var localTrimmed  = new Version(local.Major,  local.Minor,  local.Build);
            var remoteTrimmed = new Version(remote.Major, remote.Minor, remote.Build);
            comparison = remoteTrimmed.CompareTo(localTrimmed);
            return true;
        }

        private void AboutOpenLatestRelease_Click(object sender, RoutedEventArgs e)
        {
            string url = _latestReleaseUrl ?? GitHubReleasesUrl;
            OpenUrl(url);
        }

        private void AboutOpenRepo_Click(object sender, RoutedEventArgs e)
            => OpenUrl(GitHubRepoUrl);

        // Surface the privacy commitment, license, and core-license listing
        // one click from the About panel. The privacy policy and cores pages
        // are required by RetroAchievements' hardcore-compliance review;
        // making them reachable in-app means users (and reviewers) don't
        // have to know to navigate to the GitHub wiki to find them.
        private void AboutPrivacyPolicy_Click(object sender, RoutedEventArgs e)
            => OpenUrl("https://github.com/codingncaffeine/Emutastic/wiki/Privacy-Policy");

        private void AboutLicense_Click(object sender, RoutedEventArgs e)
            => OpenUrl("https://github.com/codingncaffeine/Emutastic/blob/main/LICENSE.txt");

        private void AboutCores_Click(object sender, RoutedEventArgs e)
            => OpenUrl("https://github.com/codingncaffeine/Emutastic/wiki/Cores");

        private void AboutRecheck_Click(object sender, RoutedEventArgs e)
        {
            PreferencesCache.InvalidateGitHubLatest();
            _ = CheckLatestReleaseAsync();
        }

        private static void OpenUrl(string url)
        {
            // Defense in depth: only allow http(s) URLs through ShellExecute. The
            // hardcoded constants are already https; the latest-release URL comes
            // from a parsed GitHub JSON response, which a hostile MITM/poisoned
            // mirror could substitute with file:// or javascript:. Validate the
            // scheme before launching anything.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                System.Diagnostics.Trace.WriteLine($"[About] OpenUrl refused non-http(s) URL: {url}");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = uri.AbsoluteUri,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[About] OpenUrl failed: {ex.Message}");
            }
        }

        // ── Media tab ─────────────────────────────────────────────────────────
        // Holds media folder paths (screenshots, recordings) and recording quality settings.

        private bool _loadingMediaSettings;

        private void LoadFoldersSettings()
        {
            _loadingMediaSettings = true;
            try
            {
                LoadMediaSettingsCore();
            }
            finally
            {
                _loadingMediaSettings = false;
            }
        }

        private void LoadMediaSettingsCore()
        {
            var prefs = _configService.GetUserPreferences();
            var brushText = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
            var brushMuted = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");

            ScreenshotsDefaultText.Text = $"Default: {System.IO.Path.Combine(AppPaths.DataRoot, "Screenshots")}";
            RecordingsDefaultText.Text  = $"Default: {System.IO.Path.Combine(AppPaths.DataRoot, "Recordings")}";

            if (!string.IsNullOrEmpty(prefs.ScreenshotsFolder))
            {
                ScreenshotsFolderText.Text = prefs.ScreenshotsFolder;
                ScreenshotsFolderText.Foreground = brushText;
            }
            else
            {
                ScreenshotsFolderText.Text = "Default";
                ScreenshotsFolderText.Foreground = brushMuted;
            }

            // Screenshot hotkey — show saved key or "F12 (default)" hint.
            if (ScreenshotHotkeyBox != null)
                ScreenshotHotkeyBox.Text = string.IsNullOrEmpty(prefs.ScreenshotKey)
                    ? "F12 (default)"
                    : prefs.ScreenshotKey;

            if (!string.IsNullOrEmpty(prefs.RecordingsFolder))
            {
                RecordingsFolderText.Text = prefs.RecordingsFolder;
                RecordingsFolderText.Foreground = brushText;
            }
            else
            {
                RecordingsFolderText.Text = "Default";
                RecordingsFolderText.Foreground = brushMuted;
            }

            // Recording quality settings
            var rec = _configService.GetRecordingConfiguration();

            RecQualityCombo.SelectedIndex = rec.Quality switch
            {
                "Low"      => 0,
                "Medium"   => 1,
                "Lossless" => 3,
                _          => 2, // High
            };

            int scale = Math.Clamp(rec.OutputScale, 1, 4);
            RecScaleCombo.SelectedIndex = scale - 1;

            RecEncoderCombo.SelectedIndex = rec.Encoder switch
            {
                "NVENC" => 1,
                "AMF"   => 2,
                "QSV"   => 3,
                "x264"  => 4,
                _       => 0, // Auto
            };

            RecHighChromaCheck.IsChecked = rec.HighChroma;

            RecAudioBitrateCombo.SelectedIndex = rec.AudioBitrateKbps switch
            {
                128 => 0,
                256 => 2,
                320 => 3,
                _   => 1, // 192
            };
        }

        private void RecSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingMediaSettings) return;
            SaveRecordingSettings();
        }

        private void SaveRecordingSettings()
        {
            var rec = _configService.GetRecordingConfiguration();

            rec.Quality = RecQualityCombo.SelectedIndex switch
            {
                0 => "Low",
                1 => "Medium",
                3 => "Lossless",
                _ => "High",
            };

            rec.OutputScale = Math.Clamp(RecScaleCombo.SelectedIndex + 1, 1, 4);

            rec.Encoder = RecEncoderCombo.SelectedIndex switch
            {
                1 => "NVENC",
                2 => "AMF",
                3 => "QSV",
                4 => "x264",
                _ => "Auto",
            };

            rec.HighChroma = RecHighChromaCheck.IsChecked == true;

            rec.AudioBitrateKbps = RecAudioBitrateCombo.SelectedIndex switch
            {
                0 => 128,
                2 => 256,
                3 => 320,
                _ => 192,
            };

            _configService.SetRecordingConfiguration(rec);
            _ = _configService.SaveAsync();
        }

        private void BrowseScreenshotsFolder_Click(object sender, RoutedEventArgs e)
        {
            var prefs = _configService.GetUserPreferences();
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select screenshots folder" };
            if (!string.IsNullOrEmpty(prefs.ScreenshotsFolder) && System.IO.Directory.Exists(prefs.ScreenshotsFolder))
                dialog.InitialDirectory = prefs.ScreenshotsFolder;
            if (dialog.ShowDialog(this) != true) return;

            ScreenshotsFolderText.Text = dialog.FolderName;
            ScreenshotsFolderText.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
            SaveMediaFolders();
        }

        private void ClearScreenshotsFolder_Click(object sender, RoutedEventArgs e)
        {
            ScreenshotsFolderText.Text = "Default";
            ScreenshotsFolderText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            SaveMediaFolders();
        }

        private void BrowseRecordingsFolder_Click(object sender, RoutedEventArgs e)
        {
            var prefs = _configService.GetUserPreferences();
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select recordings folder" };
            if (!string.IsNullOrEmpty(prefs.RecordingsFolder) && System.IO.Directory.Exists(prefs.RecordingsFolder))
                dialog.InitialDirectory = prefs.RecordingsFolder;
            if (dialog.ShowDialog(this) != true) return;

            RecordingsFolderText.Text = dialog.FolderName;
            RecordingsFolderText.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
            SaveMediaFolders();
        }

        private void ClearRecordingsFolder_Click(object sender, RoutedEventArgs e)
        {
            RecordingsFolderText.Text = "Default";
            RecordingsFolderText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            SaveMediaFolders();
        }

        private void SaveMediaFolders()
        {
            var prefs = _configService.GetUserPreferences();

            string ssText = ScreenshotsFolderText.Text;
            prefs.ScreenshotsFolder = ssText == "Default" ? "" : ssText;
            AppPaths.SetScreenshotsFolder(prefs.ScreenshotsFolder);

            string recText = RecordingsFolderText.Text;
            prefs.RecordingsFolder = recText == "Default" ? "" : recText;
            AppPaths.SetRecordingsFolder(prefs.RecordingsFolder);

            _configService.SetUserPreferences(prefs);
            _ = _configService.SaveAsync();
        }

        // ── Screenshot hotkey ────────────────────────────────────────────
        private void ScreenshotHotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Eat the key event so the underlying TextBox doesn't try to handle
            // it as text input, and so Tab/Escape don't fall through to focus.
            e.Handled = true;

            // Ignore modifier-only presses — we want an actual triggerable key.
            if (e.Key == System.Windows.Input.Key.LeftShift   || e.Key == System.Windows.Input.Key.RightShift
             || e.Key == System.Windows.Input.Key.LeftCtrl    || e.Key == System.Windows.Input.Key.RightCtrl
             || e.Key == System.Windows.Input.Key.LeftAlt     || e.Key == System.Windows.Input.Key.RightAlt
             || e.Key == System.Windows.Input.Key.LWin        || e.Key == System.Windows.Input.Key.RWin
             || e.Key == System.Windows.Input.Key.System)
                return;

            // Escape clears back to default.
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                SetScreenshotHotkey("");
                return;
            }

            SetScreenshotHotkey(e.Key.ToString());
        }

        private void ResetScreenshotHotkey_Click(object sender, RoutedEventArgs e) => SetScreenshotHotkey("");

        private void SetScreenshotHotkey(string keyName)
        {
            var prefs = _configService.GetUserPreferences();
            prefs.ScreenshotKey = keyName;
            _configService.SetUserPreferences(prefs);
            _ = _configService.SaveAsync();

            if (ScreenshotHotkeyBox != null)
                ScreenshotHotkeyBox.Text = string.IsNullOrEmpty(keyName) ? "F12 (default)" : keyName;
        }

        // ── Core Options tab ──────────────────────────────────────────────────

        private void BuildCoreOptionsTab()
        {
            CoreOptionsCoreList.Children.Clear();
            CoreOptionsOptionList.Children.Clear();
            CoreOptionsResetBtn.IsEnabled = false;
            CoreOptionsSaveBtn.IsEnabled  = false;
            _selectedCoreOptionsName = "";
            _pendingCoreOptionValues = new();

            var cores = App.CoreOptions.GetCoresWithSchema();

            if (cores.Count == 0)
            {
                CoreOptionsOptionList.Children.Add(new TextBlock
                {
                    Text = "No core options have been discovered yet.\n\nLaunch a game for any system — options will be captured automatically the first time a core loads.",
                    FontSize = 12,
                    Foreground = _brushTextMuted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 16, 0, 0)
                });
                return;
            }

            // Build a console→category lookup from ConsoleCategories
            var consoleToCat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cat, consoles) in ConsoleCategories)
                foreach (var c in consoles)
                    consoleToCat[c] = cat;

            // Group cores by manufacturer category, preserving ConsoleCategories order
            var coresByCategory = new Dictionary<string, List<(string CoreName, string DisplayName, string ConsoleName)>>();
            foreach (var core in cores)
            {
                string cat = consoleToCat.GetValueOrDefault(core.ConsoleName, "Other");
                if (!coresByCategory.ContainsKey(cat))
                    coresByCategory[cat] = new();
                coresByCategory[cat].Add(core);
            }

            // Render grouped list — category headers with cores underneath
            string? firstCoreName = null;
            var categoryOrder = ConsoleCategories.Select(c => c.Category).ToList();
            // Add "Other" if not already present
            if (!categoryOrder.Contains("Other")) categoryOrder.Add("Other");

            foreach (var category in categoryOrder)
            {
                if (!coresByCategory.TryGetValue(category, out var catCores)) continue;

                // Category header label
                CoreOptionsCoreList.Children.Add(new TextBlock
                {
                    Text = category.ToUpperInvariant(),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _brushTextMuted,
                    Margin = new Thickness(10, 12, 0, 4)
                });

                foreach (var (coreName, displayName, consoleName) in catCores)
                {
                    firstCoreName ??= coreName;
                    string capturedName = coreName;
                    string label = consoleName.Length > 0 ? $"{displayName} ({consoleName})" : displayName;
                    var btn = new Button
                    {
                        Content = label,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Foreground = _brushText,
                        FontSize = 12,
                        Padding = new Thickness(10, 8, 10, 8),
                        Cursor = System.Windows.Input.Cursors.Hand
                    };
                    btn.Click += (_, _) => LoadCoreOptionsForCore(capturedName);
                    CoreOptionsCoreList.Children.Add(btn);
                }
            }

            // Auto-select the first core
            if (firstCoreName != null)
                LoadCoreOptionsForCore(firstCoreName);
        }

        private void LoadCoreOptionsForCore(string coreName)
        {
            _selectedCoreOptionsName = coreName;
            CoreOptionsOptionList.Children.Clear();

            var schema = App.CoreOptions.LoadSchema(coreName);
            if (schema == null || schema.Options.Count == 0)
            {
                CoreOptionsOptionList.Children.Add(new TextBlock
                {
                    Text = "No options found for this core.",
                    FontSize = 12,
                    Foreground = _brushTextMuted,
                    Margin = new Thickness(0, 16, 0, 0)
                });
                CoreOptionsResetBtn.IsEnabled = false;
                CoreOptionsResetBtn.Content   = "Reset to Defaults";
                CoreOptionsSaveBtn.IsEnabled  = false;
                return;
            }

            // Start from saved user values, fall back to defaults
            var saved = App.CoreOptions.LoadValues(coreName);
            _pendingCoreOptionValues = new Dictionary<string, string>(saved);

            // Highlight selected core in the list
            foreach (Button b in CoreOptionsCoreList.Children.OfType<Button>())
                b.Background = Brushes.Transparent;

            CoreOptionsResetBtn.IsEnabled = true;
            CoreOptionsResetBtn.Content = $"Reset {schema.DisplayName} to Defaults";
            CoreOptionsSaveBtn.IsEnabled  = true;

            // Section header
            CoreOptionsOptionList.Children.Add(new TextBlock
            {
                Text = schema.DisplayName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _brushText,
                Margin = new Thickness(0, 0, 0, 16)
            });

            var pco = _pendingCoreOptionValues;
            var comboStyle = TryFindResource("PrefComboBox") as Style;

            foreach (var opt in schema.Options)
            {
                var section = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

                section.Children.Add(new TextBlock
                {
                    Text = opt.Description,
                    FontSize = 12,
                    Foreground = _brushText,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                });

                var combo = new ComboBox { Height = 32, MaxWidth = 400, HorizontalAlignment = HorizontalAlignment.Left };
                if (comboStyle != null) combo.Style = comboStyle;

                foreach (var val in opt.ValidValues)
                    combo.Items.Add(val);

                string current = pco.TryGetValue(opt.Key, out string? sv) ? sv : opt.DefaultValue;
                combo.SelectedItem = current;
                if (combo.SelectedItem == null && combo.Items.Count > 0)
                    combo.SelectedIndex = 0;

                string capturedKey = opt.Key;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is string v)
                        pco[capturedKey] = v;
                };

                section.Children.Add(combo);
                CoreOptionsOptionList.Children.Add(section);
            }
        }

        private void CoreOptionsReset_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedCoreOptionsName)) return;

            // Wipe saved user values so defaults are used on the next game launch.
            // Do NOT push defaults into a running game — some cores (PPSSPP, Dolphin)
            // crash when critical options (backend, resolution, etc.) change mid-session.
            // The reset takes effect on the next launch, which is the safe behavior.
            App.CoreOptions.DeleteValues(_selectedCoreOptionsName);

            LoadCoreOptionsForCore(_selectedCoreOptionsName);
        }

        private void CoreOptionsSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedCoreOptionsName)) return;
            App.CoreOptions.SaveValues(_selectedCoreOptionsName, _pendingCoreOptionValues);
        }
    }

    // Small data class for the system combo box
    public record ConsoleItem(string Tag, string Name, string? IconPath = null)
    {
        public override string ToString() => Name;

        // Pack-URI mapping for the small console icon shown alongside the name in
        // the Preferences → Controls system picker. Mirrors the table baked into
        // MainWindow's nav sidebar so both surfaces stay visually consistent.
        public static string? ResolveIconUri(string tag) => tag switch
        {
            "Atari2600"    => "pack://application:,,,/Assets/system_icons/atari2600.jpg",
            "Atari7800"    => "pack://application:,,,/Assets/system_icons/atari7800.jpg",
            "Jaguar"       => "pack://application:,,,/Assets/system_icons/systemicons1_13.jpg",
            "NES"          => "pack://application:,,,/Assets/system_icons/nes_icon.jpg",
            "FDS"          => "pack://application:,,,/Assets/system_icons/famicon disk system.jpg",
            "SNES"         => "pack://application:,,,/Assets/system_icons/snes.jpg",
            "N64"          => "pack://application:,,,/Assets/system_icons/n64.jpg",
            "GameCube"     => "pack://application:,,,/Assets/system_icons/gamecube.jpg",
            "GB"           => "pack://application:,,,/Assets/system_icons/gameboy.jpg",
            "GBC"          => "pack://application:,,,/Assets/system_icons/gbc.jpeg",
            "GBA"          => "pack://application:,,,/Assets/system_icons/gba.jpg",
            "3DS"          => "pack://application:,,,/Assets/system_icons/3ds_icon.jpg",
            "NDS"          => "pack://application:,,,/Assets/system_icons/nds.jpg",
            "VirtualBoy"   => "pack://application:,,,/Assets/system_icons/virtualboy.jpg",
            "SMS"          => "pack://application:,,,/Assets/system_icons/sms.jpg",
            "Genesis"      => "pack://application:,,,/Assets/system_icons/genesis.jpg",
            "SegaCD"       => "pack://application:,,,/Assets/system_icons/genesis.jpg",
            "Sega32X"      => "pack://application:,,,/Assets/system_icons/32x.jpg",
            "Saturn"       => "pack://application:,,,/Assets/system_icons/saturn.jpg",
            "GameGear"     => "pack://application:,,,/Assets/system_icons/sms.jpg",
            "SG1000"       => "pack://application:,,,/Assets/system_icons/sg-1000.png",
            "Dreamcast"    => "pack://application:,,,/Assets/system_icons/dreamcast.jpg",
            "PS1"          => "pack://application:,,,/Assets/system_icons/ps1.jpg",
            "PS2"          => "pack://application:,,,/Assets/system_icons/ps2.png",
            "PSP"          => "pack://application:,,,/Assets/system_icons/psp.jpg",
            "TG16"         => "pack://application:,,,/Assets/system_icons/tg16.png",
            "TGCD"         => "pack://application:,,,/Assets/system_icons/tg16.png",
            "NeoGeo"       => "pack://application:,,,/Assets/system_icons/neogeo.jpg",
            "NeoCD"        => "pack://application:,,,/Assets/system_icons/neogeo_cd.png",
            "NGP"          => "pack://application:,,,/Assets/system_icons/neo geo pocket.jpg",
            "NGPC"         => "pack://application:,,,/Assets/system_icons/neo geo pocket.jpg",
            "3DO"          => "pack://application:,,,/Assets/system_icons/3d0.jpg",
            "CDi"          => "pack://application:,,,/Assets/system_icons/cdi_icon.jpg",
            "ColecoVision" => "pack://application:,,,/Assets/system_icons/coleco.jpg",
            "Vectrex"      => "pack://application:,,,/Assets/system_icons/vectrex.jpg",
            "Arcade"       => "pack://application:,,,/Assets/system_icons/arcade.png",
            _              => null,
        };
    }

    // ── BIOS data ─────────────────────────────────────────────────────────────
    internal record BiosEntry(
        string Console,
        string ConsoleDisplay,
        string Filename,
        string Description,
        long ExpectedSize,
        string? Md5);      // null = presence-only check

    internal static class KnownBios
    {
        public static readonly List<BiosEntry> All = new()
        {
            // PlayStation
            new("PS1","PlayStation","scph5501.bin","USA v3.0 (recommended)",524288,"490f666e1afb15b7362b406ed1cea246"),
            new("PS1","PlayStation","scph5500.bin","Japan v3.0",524288,"8dd7d5296a650fac7319bce665a6a53c"),
            new("PS1","PlayStation","scph5502.bin","Europe v3.0",524288,"32736f17079d0b2b7024407c39bd3050"),
            new("PS1","PlayStation","scph1001.bin","USA v2.2",524288,"37157331b6d4d325cb9f597ea42cd597"),
            new("PS1","PlayStation","scph7001.bin","USA v4.1",524288,"502224b6d23561a46e5a7ba01a1fed62"),
            // PlayStation 2 — dumps live in the pcsx2/bios/ subfolder. Any valid
            // 4 MB dump works; these are common known-good ones (any one is enough).
            new("PS2","PlayStation 2","pcsx2/bios/ps2-0230a-20080220.bin","USA v2.30 (recommended)",4194304,"21038400dc633070a78ad53090c53017"),
            new("PS2","PlayStation 2","pcsx2/bios/ps2-0230e-20080220.bin","Europe v2.30",4194304,"dc69f0643a3030aaa4797501b483d6c4"),
            new("PS2","PlayStation 2","pcsx2/bios/ps2-0230j-20080220.bin","Japan v2.30",4194304,"80ac46fa7e77b8ab4366e86948e54f83"),
            new("PS2","PlayStation 2","pcsx2/bios/ps2-0220a-20060905.bin","USA v2.20",4194304,"40c11c063b3b9409aa5e4058e984e30c"),
            new("PS2","PlayStation 2","pcsx2/bios/ps2-0170a-20030325.bin","USA v1.70",4194304,"8aa12ce243210128c5074552d3b86251"),
            // Sega CD
            new("SegaCD","Sega CD","bios_CD_U.bin","USA",131072,"2efd74e3232ff260e371b99f84024f7f"),
            new("SegaCD","Sega CD","bios_CD_J.bin","Japan",131072,"278a9397d192149e84e820ac621a8edd"),
            new("SegaCD","Sega CD","bios_CD_E.bin","Europe",131072,"e66fa1dc5820d254611fdcdba0662372"),
            // Saturn
            new("Saturn","Saturn","sega_101.bin","Japan v1.00",524288,"85ec9ca47d8f6807718151cbcca8b964"),
            new("Saturn","Saturn","mpr-17933.bin","Japan v1.01",524288,"3240872c70984b6cbfda1586cab68dbe"),
            new("Saturn","Saturn","mpr-17941.bin","USA/Europe v1.01 (recommended)",524288,"4df44ac9af0e58fc63b0e2af9cec25a9"),
            new("Saturn","Saturn","kronos/saturn_bios.bin","Kronos (any region)",524288,null),
            // Famicom Disk System
            new("FDS","Famicom Disk System","disksys.rom","",8192,"ca30b50f880eb660a320674ed365ef7a"),
            // TurboGrafx-CD
            new("TGCD","TurboGrafx-CD","syscard3.pce","System Card v3.0 (recommended)",262144,"0754f903b52e3b3342202bdafb13efa5"),
            new("TGCD","TurboGrafx-CD","syscard2.pce","System Card v2.1",131072,null),
            new("TGCD","TurboGrafx-CD","syscard1.pce","System Card v1.0",131072,null),
            // 3DO
            new("3DO","3DO","panafz10.bin","Panasonic FZ-10",1048576,"51f2f43ae2f3508a14d9f56597e2d3ce"),
            new("3DO","3DO","panafz1j.bin","Panasonic FZ-1 (Japan)",1048576,null),
            new("3DO","3DO","goldstar.bin","GoldStar",1048576,null),

            // Philips CD-i (SAME CDi / MAME — place cdibios.zip in System folder)
            new("CDi","Philips CD-i","cdibios.zip","CD-i BIOS (required)",0,null),

            // Neo Geo (Geolith)
            new("NeoGeo","Neo Geo","neogeo.zip","Neo Geo BIOS (required)",0,null),
            new("NeoGeo","Neo Geo","aes.zip","AES BIOS (required)",0,null),

            // Neo Geo CD (Geolith, same core, CD-mode auto-detected from content).
            // Cart BIOS is still required because Geolith reuses it in CD mode,
            // and the CDZ BIOS archive (neocdz.zip — a MAME-style romset
            // containing neocd.bin + 000-lo.lo etc.) provides the disc-side
            // BIOS Geolith loads at CD-mode start.
            new("NeoCD","Neo Geo CD","neogeo.zip","Cart BIOS (required, same as Neo Geo)",0,null),
            new("NeoCD","Neo Geo CD","aes.zip","AES BIOS (required, same as Neo Geo)",0,null),
            new("NeoCD","Neo Geo CD","neocdz.zip","CDZ BIOS archive (required for CD games)",0,null),

            // Game Boy Advance (optional — mgba has built-in HLE BIOS)
            new("GBA","Game Boy Advance","gba_bios.bin","BIOS (optional, improves compatibility)",16384,"a860e8c0b6d573d191e4ec7db1b1e4f6"),

        };
    }
}
