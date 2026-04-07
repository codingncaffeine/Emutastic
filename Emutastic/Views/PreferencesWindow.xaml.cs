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
using System.Windows.Shapes;
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
        private enum PrefSection { Controls, SystemFiles, Cores, Library, Theme, Snaps, CoreOptions, Achievements }
        private PrefSection _activeSection = PrefSection.Controls;

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

            Closed += (_, _) =>
            {
                _controllerPollTimer?.Stop();
                if (_controllerManager != null)
                {
                    _controllerManager.RawMode = false;
                    _controllerManager.ButtonChanged -= OnControllerButtonChanged;
                }
            };
        }

        public PreferencesWindow(DatabaseService db, ControllerManager controllerManager)
            : this(db, controllerManager, App.Configuration
                  ?? throw new InvalidOperationException("Configuration not initialized"))
        { }

        // ── Initialisation ────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PopulateSystemComboBox();
            PopulateInputDevices();
            PlayerComboBox.SelectedIndex = 0;
        }

        private void PopulateSystemComboBox()
        {
            var consoles = ControllerDefinitions.GetSupportedConsoles();
            SystemComboBox.ItemsSource = consoles
                .Select(c => new ConsoleItem(c.Tag, c.Name))
                .ToList();

            // Select previously-used console or default to SNES
            int idx = consoles.FindIndex(c => c.Tag == _currentConsole);
            SystemComboBox.SelectedIndex = idx >= 0 ? idx : 0;
        }

        private void PopulateInputDevices()
        {
            var devices = new List<string> { "Keyboard" };
            devices.AddRange(ControllerManager.GetConnectedControllers());

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
                _mappings[m.ButtonName] = new InputMapping
                {
                    ConsoleName         = _currentConsole,
                    ButtonName          = m.ButtonName,
                    InputType           = _isKeyboardMode ? Services.InputType.Keyboard : Services.InputType.Controller,
                    Key                 = _isKeyboardMode && Enum.TryParse<Key>(m.InputIdentifier, out var k) ? k : Key.None,
                    ControllerButtonId  = !_isKeyboardMode && uint.TryParse(m.InputIdentifier, out var bid) ? bid : 0,
                    DisplayText         = m.DisplayName,
                };
            }
        }

        private void SaveMappingsToConfig()
        {
            var config = _configService.GetInputConfiguration(ConfigKey);

            if (_isKeyboardMode)
            {
                config.KeyboardMappings.Clear();
                foreach (var m in _mappings.Values.Where(m => m.InputType == Services.InputType.Keyboard && m.Key != Key.None))
                {
                    config.KeyboardMappings.Add(new ButtonMapping
                    {
                        ButtonName      = m.ButtonName,
                        InputIdentifier = m.Key.ToString(),
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
                        InputIdentifier = m.ControllerButtonId.ToString(),
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
            if (key == Key.Escape) { StopWaiting(); return; }

            string display = KeyToDisplayString(key);
            string btnName = _rows[_waitingRowIndex].ButtonName;
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

                CommitMapping(btnName, display, controllerId: buttonId);

                if (isAnalogDir)
                    _analogLastCapture = DateTime.UtcNow;
            });
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

            if (section == PrefSection.SystemFiles) BuildBiosPanel();
            if (section == PrefSection.Cores)       BuildCoresPanel();
            if (section == PrefSection.Library)     LoadLibrarySettings();
            if (section == PrefSection.Theme)       LoadThemeSettings();
            if (section == PrefSection.Snaps)       LoadSnapsSettings();
            if (section == PrefSection.CoreOptions) BuildCoreOptionsTab();
            if (section == PrefSection.Achievements) LoadAchievementsSettings();

            // Start controller hotplug polling only while Controls tab is visible.
            if (section == PrefSection.Controls)
            {
                if (_controllerPollTimer == null)
                {
                    _controllerPollTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(2)
                    };
                    _controllerPollTimer.Tick += (_, _) =>
                    {
                        var current = ControllerManager.GetConnectedControllers();
                        current.Insert(0, "Keyboard");
                        if (!current.SequenceEqual(_lastKnownDevices))
                        {
                            _lastKnownDevices = current;
                            PopulateInputDevices();
                        }
                    };
                }
                _lastKnownDevices = ControllerManager.GetConnectedControllers();
                _lastKnownDevices.Insert(0, "Keyboard");
                _controllerPollTimer.Start();
            }
            else
            {
                _controllerPollTimer?.Stop();
            }
        }

        // ── BIOS panel ────────────────────────────────────────────────────────
        private void BuildBiosPanel()
        {
            BiosPanel.Children.Clear();
            string sysDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Emutastic", "System");

            // Info banner
            var accent = (Color)FindResource("AccentColor");
            var banner = new Border
            {
                Background  = new SolidColorBrush(Color.FromArgb(0x18, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding  = new Thickness(14, 10, 14, 10),
                Margin   = new Thickness(0, 0, 0, 16)
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
            banner.Child = bannerStack;
            BiosPanel.Children.Add(banner);

            // Collect unique ROM directories per console tag from the library.
            var allGames = _db.GetAllGames();
            var romDirsByConsole = allGames
                .Where(g => !string.IsNullOrEmpty(g.RomPath))
                .GroupBy(g => g.Console)
                .ToDictionary(
                    grp => grp.Key,
                    grp => grp
                        .Select(g => System.IO.Path.GetDirectoryName(g.RomPath))
                        .Where(d => !string.IsNullOrEmpty(d))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                );

            var groups = KnownBios.All.GroupBy(b => b.ConsoleDisplay);
            foreach (var group in groups)
            {
                // Console group header
                var header = new TextBlock
                {
                    Text = group.Key.ToUpperInvariant(),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _brushTextMuted,
                    Margin = new Thickness(0, 14, 0, 6)
                };
                BiosPanel.Children.Add(header);

                // Gather ROM dirs for this console group (may span multiple console tags)
                var consoleTags = KnownBios.All
                    .Where(b => b.ConsoleDisplay == group.Key)
                    .Select(b => b.Console)
                    .Distinct();
                var romDirs = consoleTags
                    .SelectMany(tag => romDirsByConsole.TryGetValue(tag, out var dirs) ? dirs : Array.Empty<string>())
                    .Where(d => d != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var entry in group)
                {
                    BiosPanel.Children.Add(BuildBiosRow(entry, sysDir, romDirs!));
                }
            }
        }

        private UIElement BuildBiosRow(BiosEntry entry, string sysDir, string[]? romDirs = null)
        {
            string fullPath = System.IO.Path.Combine(sysDir, entry.Filename);
            bool existsInSysDir = System.IO.File.Exists(fullPath);
            bool existsInRomDir = !existsInSysDir && (romDirs?.Any(dir =>
                System.IO.File.Exists(System.IO.Path.Combine(dir, System.IO.Path.GetFileName(entry.Filename)))) == true);
            bool exists = existsInSysDir || existsInRomDir;

            bool verified = false;
            string? foundPath = existsInSysDir ? fullPath
                : existsInRomDir ? romDirs!.Select(dir =>
                    System.IO.Path.Combine(dir, System.IO.Path.GetFileName(entry.Filename)))
                    .FirstOrDefault(System.IO.File.Exists)
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

        // ── Cores panel ───────────────────────────────────────────────────────

        // Per-console plugin/option definitions
        private static readonly Dictionary<string, List<(string Key, string Label, List<string> Values, string Default, Dictionary<string, string> Descs)>> CoreSpecificOptions = new()
        {
            ["N64"] = new()
            {
                (
                    Key:     "parallel-n64-gfxplugin",
                    Label:   "GFX Plugin",
                    Values:  new() { "glide64", "angrylion", "rice" },
                    Default: "glide64",
                    Descs:   new() {
                        ["glide64"]    = "GPU renderer — good balance of speed and accuracy",
                        ["angrylion"]  = "Software renderer — most accurate, very slow",
                        ["rice"]       = "GPU renderer — fastest, least accurate",
                    }
                )
            }
        };

        // ── Core downloader ───────────────────────────────────────────────────
        private readonly CoreDownloadService _downloader = new();
        private CancellationTokenSource? _downloadAllCts;

        private void BuildDownloadSection(string coresFolder)
        {
            CoresListPanel.Children.Add(new TextBlock
            {
                Text = "DOWNLOAD CORES",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = _brushTextMuted,
                Margin = new Thickness(0, 0, 0, 8)
            });

            // "Download All Recommended" button row
            var dlAllBtn = new Button
            {
                Content = "Download All Recommended",
                Style = (Style)FindResource("AccentButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var allProgressBar = new ProgressBar
            {
                Height = 4,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 8)
            };

            var allStatusText = new TextBlock
            {
                FontSize = 11,
                Foreground = _brushTextMuted,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 8)
            };

            CoresListPanel.Children.Add(dlAllBtn);
            CoresListPanel.Children.Add(allProgressBar);
            CoresListPanel.Children.Add(allStatusText);

            // Per-core rows (recommended only)
            var recommended = CoreDownloadService.Catalog.Where(c => c.Recommended).ToList();
            var rowMap = new Dictionary<string, (ProgressBar Bar, TextBlock Status, Button Btn)>();

            var coreCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x21)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 4)
            };
            var coreCardStack = new StackPanel();

            for (int i = 0; i < recommended.Count; i++)
            {
                var entry = recommended[i];
                bool installed = System.IO.File.Exists(System.IO.Path.Combine(coresFolder, entry.FileName));

                bool hasBackup = CoreDownloadService.HasBackup(coresFolder, entry.FileName);

                var row = new Grid { Margin = new Thickness(0, 0, 0, i < recommended.Count - 1 ? 6 : 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Name + systems
                var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                nameStack.Children.Add(new TextBlock { Text = entry.DisplayName, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _brushText });
                nameStack.Children.Add(new TextBlock { Text = string.Join(", ", entry.Systems), FontSize = 10, Foreground = _brushTextMuted, Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetColumn(nameStack, 0);

                // Status badge + progress
                var statusStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 3, 8, 3),
                    Background = installed
                        ? new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58))
                        : new SolidColorBrush(Color.FromArgb(0x22, 0x88, 0x88, 0x88)),
                };
                badge.Child = new TextBlock
                {
                    Text = installed ? "Installed" : "Not installed",
                    FontSize = 10,
                    Foreground = installed
                        ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                        : _brushTextMuted
                };
                var rowProgress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Value = 0, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
                var rowStatus   = new TextBlock   { FontSize = 10, Foreground = _brushTextMuted, Visibility = Visibility.Collapsed };
                statusStack.Children.Add(badge);
                statusStack.Children.Add(rowProgress);
                statusStack.Children.Add(rowStatus);
                Grid.SetColumn(statusStack, 1);

                // Button panel: Download + optional Revert
                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                var revertBtn = new Button
                {
                    Content = "Revert",
                    Style = (Style)FindResource("SmallOutlineButton"),
                    Margin = new Thickness(0, 0, 6, 0),
                    Visibility = hasBackup ? Visibility.Visible : Visibility.Collapsed,
                    ToolTip = "Restore the previous version of this core"
                };
                revertBtn.Click += (_, _) =>
                {
                    try
                    {
                        CoreDownloadService.Revert(coresFolder, entry.FileName);
                        revertBtn.Visibility = Visibility.Collapsed;
                        rowStatus.Text = "Reverted to previous version";
                        rowStatus.Visibility = Visibility.Visible;
                    }
                    catch (Exception ex)
                    {
                        rowStatus.Text = $"Revert failed: {ex.Message}";
                        rowStatus.Visibility = Visibility.Visible;
                    }
                };

                var dlBtn = new Button
                {
                    Content = installed ? "Re-download" : "Download",
                    Style = (Style)FindResource("SmallOutlineButton"),
                    VerticalAlignment = VerticalAlignment.Center
                };

                dlBtn.Click += (_, _) => StartSingleDownload(entry, coresFolder, badge, rowProgress, rowStatus, dlBtn, revertBtn);

                btnPanel.Children.Add(revertBtn);
                btnPanel.Children.Add(dlBtn);
                Grid.SetColumn(btnPanel, 2);

                row.Children.Add(nameStack);
                row.Children.Add(statusStack);
                row.Children.Add(btnPanel);

                rowMap[entry.FileName] = (rowProgress, rowStatus, dlBtn);
                coreCardStack.Children.Add(row);
            }

            coreCard.Child = coreCardStack;
            CoresListPanel.Children.Add(coreCard);

            // "Download All" handler
            dlAllBtn.Click += async (_, _) =>
            {
                _downloadAllCts?.Cancel();
                _downloadAllCts = new CancellationTokenSource();
                var ct = _downloadAllCts.Token;

                dlAllBtn.IsEnabled = false;
                allProgressBar.Visibility = Visibility.Visible;
                allStatusText.Visibility  = Visibility.Visible;

                int done = 0;
                foreach (var entry in recommended)
                {
                    if (ct.IsCancellationRequested) break;
                    allStatusText.Text = $"Downloading {entry.DisplayName}… ({done + 1}/{recommended.Count})";

                    if (rowMap.TryGetValue(entry.FileName, out var r))
                    {
                        r.Bar.Visibility    = Visibility.Visible;
                        r.Status.Visibility = Visibility.Visible;
                        r.Btn.IsEnabled     = false;
                    }

                    try
                    {
                        var prog = new Progress<int>(v =>
                        {
                            if (rowMap.TryGetValue(entry.FileName, out var r2)) r2.Bar.Value = v;
                            allProgressBar.Value = (done * 100 + v) / recommended.Count;
                        });
                        await _downloader.DownloadAsync(entry, coresFolder, prog, ct);

                        if (rowMap.TryGetValue(entry.FileName, out var r3))
                        {
                            r3.Bar.Visibility = Visibility.Collapsed;
                            r3.Status.Text = "Done";
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        if (rowMap.TryGetValue(entry.FileName, out var r4))
                            r4.Status.Text = $"Error: {ex.Message}";
                    }

                    done++;
                }

                allProgressBar.Value = 100;
                allStatusText.Text = ct.IsCancellationRequested
                    ? "Cancelled."
                    : $"Done — {done}/{recommended.Count} cores downloaded.";
                dlAllBtn.IsEnabled = true;

                // Rebuild to reflect newly installed state
                BuildCoresPanel();
            };
        }

        private async void StartSingleDownload(CoreEntry entry, string coresFolder,
            Border badge, ProgressBar bar, TextBlock statusText, Button dlBtn, Button? revertBtn = null)
        {
            dlBtn.IsEnabled       = false;
            bar.Visibility        = Visibility.Visible;
            statusText.Visibility = Visibility.Visible;
            statusText.Text       = "Downloading…";
            bar.Value = 0;

            try
            {
                var prog = new Progress<int>(v => bar.Value = v);
                await _downloader.DownloadAsync(entry, coresFolder, prog);
                statusText.Text = "Updated";
                badge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58));
                if (badge.Child is TextBlock tb)
                {
                    tb.Text = "Installed";
                    tb.Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                }
                bar.Visibility = Visibility.Collapsed;
                dlBtn.Content  = "Re-download";
                dlBtn.IsEnabled = true;
                // Show revert button now that a backup exists
                if (revertBtn != null)
                    revertBtn.Visibility = Visibility.Visible;
                // Rebuild installed-cores section
                BuildCoresPanel();
            }
            catch (Exception ex)
            {
                statusText.Text = $"Error: {ex.Message}";
                dlBtn.IsEnabled = true;
            }
        }

        private void BuildCoresPanel()
        {
            CoresListPanel.Children.Clear();
            string coresFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cores");

            BuildDownloadSection(coresFolder);

            // ── Separator between download section and installed cores ──
            CoresListPanel.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x33)),
                Margin = new Thickness(0, 16, 0, 0)
            });
            CoresListPanel.Children.Add(new TextBlock
            {
                Text = "INSTALLED CORES",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = _brushTextMuted,
                Margin = new Thickness(0, 12, 0, 6)
            });
            var prefs = _configService.GetCorePreferences();

            bool anyConsole = false;

            foreach (var kv in Services.CoreManager.ConsoleCoreMap.OrderBy(x => x.Key))
            {
                string consoleName = kv.Key;
                string[] candidates = kv.Value;

                // Gather installed cores for this console
                var installed = candidates
                    .Select(dll => new {
                        Dll = dll,
                        Path = System.IO.Path.Combine(coresFolder, dll),
                        Friendly = FormatCoreName(dll)
                    })
                    .Where(c => System.IO.File.Exists(c.Path))
                    .ToList();

                if (installed.Count == 0) continue;
                anyConsole = true;

                // ── Console section header ──
                CoresListPanel.Children.Add(new TextBlock
                {
                    Text = consoleName,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _brushTextMuted,
                    Margin = new Thickness(0, 16, 0, 6)
                });

                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x21)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 12, 14, 12),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                var cardStack = new StackPanel();

                // ── Core rows ──
                foreach (var core in installed)
                {
                    string version = GetCoreVersion(core.Path);
                    var coreRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                    coreRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                    coreRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    coreRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    nameStack.Children.Add(new TextBlock { Text = core.Friendly, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _brushText });
                    nameStack.Children.Add(new TextBlock { Text = core.Dll, FontSize = 10, Foreground = _brushTextMuted, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 2, 0, 0) });
                    Grid.SetColumn(nameStack, 0);

                    var installedBadge = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58)),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8, 3, 8, 3),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    installedBadge.Child = new TextBlock { Text = "Installed", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)) };
                    Grid.SetColumn(installedBadge, 1);

                    var versionBlock = new TextBlock
                    {
                        Text = version,
                        FontSize = 11,
                        Foreground = _brushTextMuted,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontFamily = new FontFamily("Consolas"),
                        ToolTip = version.Length == 10 && version[4] == '-' ? "File last modified (no version resource)" : null
                    };
                    Grid.SetColumn(versionBlock, 2);

                    coreRow.Children.Add(nameStack);
                    coreRow.Children.Add(installedBadge);
                    coreRow.Children.Add(versionBlock);
                    cardStack.Children.Add(coreRow);
                }

                // ── Preferred core picker (only when >1 installed) ──
                if (installed.Count > 1)
                {
                    cardStack.Children.Add(new Rectangle { Height = 1, Fill = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x33)), Margin = new Thickness(0, 10, 0, 10) });

                    var pickerRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    pickerRow.Children.Add(new TextBlock
                    {
                        Text = "Preferred Core",
                        FontSize = 12,
                        Foreground = _brushText,
                        VerticalAlignment = VerticalAlignment.Center,
                        Width = 140
                    });

                    var preferredCombo = new ComboBox
                    {
                        Style = (Style)FindResource("PrefComboBox"),
                        Width = 220,
                        ItemsSource = installed.Select(c => c.Friendly).ToList()
                    };

                    // Select current preference or first installed
                    string? savedPref = prefs.PreferredCores.TryGetValue(consoleName, out var p) ? p : null;
                    int prefIdx = installed.FindIndex(c => c.Dll == savedPref);
                    preferredCombo.SelectedIndex = prefIdx >= 0 ? prefIdx : 0;

                    preferredCombo.SelectionChanged += (_, _) =>
                    {
                        int idx = preferredCombo.SelectedIndex;
                        if (idx < 0 || idx >= installed.Count) return;
                        var current = _configService.GetCorePreferences();
                        current.PreferredCores[consoleName] = installed[idx].Dll;
                        _configService.SetCorePreferences(current);
                        _ = _configService.SaveAsync();
                    };

                    pickerRow.Children.Add(preferredCombo);
                    cardStack.Children.Add(pickerRow);
                }

                // ── Console-specific plugin options (e.g. N64 GFX plugin) ──
                if (CoreSpecificOptions.TryGetValue(consoleName, out var options))
                {
                    prefs.CoreOptionOverrides.TryGetValue(consoleName, out var savedOverrides);

                    foreach (var opt in options)
                    {
                        cardStack.Children.Add(new Rectangle { Height = 1, Fill = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x33)), Margin = new Thickness(0, 10, 0, 10) });

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
                            Width = 220,
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
                            Width = 220
                        };

                        optCombo.SelectionChanged += (_, _) =>
                        {
                            string val = opt.Values[optCombo.SelectedIndex];
                            descBlock.Text = opt.Descs.TryGetValue(val, out var desc) ? desc : "";
                            var current = _configService.GetCorePreferences();
                            if (!current.CoreOptionOverrides.TryGetValue(consoleName, out var overrides))
                                current.CoreOptionOverrides[consoleName] = overrides = new();
                            overrides[opt.Key] = val;
                            _configService.SetCorePreferences(current);
                            _ = _configService.SaveAsync();
                        };

                        optStack.Children.Add(optCombo);
                        optStack.Children.Add(descBlock);
                        optRow.Children.Add(optStack);
                        cardStack.Children.Add(optRow);
                    }
                }

                card.Child = cardStack;
                CoresListPanel.Children.Add(card);
            }

            if (!anyConsole)
            {
                CoresListPanel.Children.Add(new TextBlock
                {
                    Text = "No cores found in the Cores folder.",
                    FontSize = 13,
                    Foreground = _brushTextMuted,
                    Margin = new Thickness(0, 8, 0, 0)
                });
            }

            BuildExtrasSection();
        }

        // ── Extras section (SDL3 + DAT files) ────────────────────────────────

        private static readonly (string Tag, string Label, string RedumpSlug)[] KnownDats =
        {
            ("SegaCD", "Sega CD / Mega CD", "mcd"),
            ("Saturn", "Sega Saturn",        "ss"),
            ("PS1",    "PlayStation",        "psx"),
            ("3DO",    "3DO",                "3do"),
            ("CDi",    "Philips CD-i",       "cdi"),
        };

        private void BuildExtrasSection()
        {
            string baseDir  = AppDomain.CurrentDomain.BaseDirectory;
            string datsDir  = System.IO.Path.Combine(baseDir, "DATs");

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
            string sdl3Path     = System.IO.Path.Combine(baseDir, "SDL3.dll");
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

            // ── DAT files separator ──
            extrasStack.Children.Add(new Rectangle
            {
                Height = 1,
                Fill   = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x33)),
                Margin = new Thickness(0, 8, 0, 8)
            });
            extrasStack.Children.Add(new TextBlock
            {
                Text         = "DAT files are SHA1 databases used during import to auto-detect which console a disc image belongs to. Downloaded from Redump and saved in the DATs\\ folder next to the app.",
                FontSize     = 11,
                Foreground   = _brushTextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 10)
            });

            // Per-DAT rows
            System.IO.Directory.CreateDirectory(datsDir);
            for (int i = 0; i < KnownDats.Length; i++)
            {
                var (tag, label, slug) = KnownDats[i];
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

                var capturedTag      = tag;
                var capturedSlug     = slug;
                var capturedDatPath  = datPath;
                var capturedBadge    = datBadge;
                var capturedProgress = datProgress;
                var capturedStatus   = datStatus;
                var capturedBtn      = datBtn;

                datBtn.Click += async (_, _) =>
                {
                    capturedBtn.IsEnabled       = false;
                    capturedProgress.Visibility = Visibility.Visible;
                    capturedStatus.Visibility   = Visibility.Visible;
                    capturedStatus.Text         = "Downloading…";
                    capturedProgress.Value      = 10;
                    try
                    {
                        using var http = new System.Net.Http.HttpClient();
                        http.DefaultRequestHeaders.Add("User-Agent", "Emutastic");
                        var bytes = await http.GetByteArrayAsync($"http://redump.org/datfile/{capturedSlug}/");
                        capturedProgress.Value = 90;
                        await System.IO.File.WriteAllBytesAsync(capturedDatPath, bytes);
                        capturedProgress.Value      = 100;
                        capturedStatus.Text         = "Downloaded successfully.";
                        capturedBadge.Background    = new SolidColorBrush(Color.FromArgb(0x22, 0x30, 0xD1, 0x58));
                        ((TextBlock)capturedBadge.Child).Text       = "Present";
                        ((TextBlock)capturedBadge.Child).Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
                        capturedBtn.Content = "Re-download";
                    }
                    catch (Exception ex)
                    {
                        capturedStatus.Text = $"Failed: {ex.Message}";
                    }
                    finally { capturedBtn.IsEnabled = true; }
                };

                extrasStack.Children.Add(MakeExtrasRow(
                    $"{tag}.dat",
                    label,
                    datBadge,
                    datProgress,
                    datStatus,
                    datBtn,
                    isLast: i == KnownDats.Length - 1));
            }

            extrasCard.Child = extrasStack;
            CoresListPanel.Children.Add(extrasCard);
        }

        private Grid MakeExtrasRow(string name, string description, Border badge,
                                   ProgressBar? progress, TextBlock? status, Button btn, bool isLast)
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
                "fbalpha2012"       => "FB Alpha 2012",
                "fbalpha2012_cps1"  => "FB Alpha 2012 CPS-1",
                "fbalpha2012_cps2"  => "FB Alpha 2012 CPS-2",
                "fbalpha2012_cps3"  => "FB Alpha 2012 CPS-3",
                "fbalpha2012_neogeo"=> "FB Alpha 2012 Neo Geo",
                "mame"              => "MAME (current)",
                "mame2000"          => "MAME 2000 (0.37b5)",
                "mame2003"          => "MAME 2003 (0.78)",
                "mame2003_plus"     => "MAME 2003 Plus",
                "mame2010"          => "MAME 2010 (0.139)",
                "mame2015"          => "MAME 2015 (0.160)",
                "mame2016"          => "MAME 2016 (0.174)",
                "smsplus"           => "SMS Plus",
                _ => char.ToUpper(name[0]) + name[1..].Replace("_", " ")
            };
        }

        // ── Library panel ─────────────────────────────────────────────────────
        private void LoadLibrarySettings()
        {
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

        private void BackupBtn_Click(object sender, RoutedEventArgs e)
        {
            string dbPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Emutastic", "library.db");

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Backup Library Database",
                FileName = $"library-backup-{DateTime.Now:yyyyMMdd_HHmmss}.db",
                DefaultExt = ".db",
                Filter = "SQLite Database|*.db|All Files|*.*"
            };

            if (dlg.ShowDialog(this) != true) return;

            try
            {
                System.IO.File.Copy(dbPath, dlg.FileName, overwrite: true);
                BackupStatusText.Text = "Backup saved.";
            }
            catch (Exception ex)
            {
                BackupStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void VacuumBtn_Click(object sender, RoutedEventArgs e)
        {
            VacuumBtn.IsEnabled = false;
            VacuumStatusText.Text = "Optimizing\u2026";
            try
            {
                await Task.Run(() => _db.VacuumDatabase());
                VacuumStatusText.Text = "Done \u2014 database optimized.";
            }
            catch (Exception ex)
            {
                VacuumStatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                VacuumBtn.IsEnabled = true;
            }
        }

        // ── Theme panel ───────────────────────────────────────────────────────
        private void LoadThemeSettings()
        {
            var theme = _configService.GetThemeConfiguration();

            // Clamp to valid range in case config was edited manually.
            PaddingSlider.Value  = Math.Clamp(theme.GridPadding, 8, 64);
            SpacingSlider.Value  = Math.Clamp(theme.CardSpacing, 4, 48);
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

        private void ThemeSaveBtn_Click(object sender, RoutedEventArgs e)
        {
            var theme = _configService.GetThemeConfiguration();
            theme.GridPadding = Math.Clamp((int)PaddingSlider.Value,  8,   64);
            theme.CardSpacing = Math.Clamp((int)SpacingSlider.Value,  4,   48);
            theme.CardWidth   = Math.Clamp((int)CardSizeSlider.Value, 148, 280);
            theme.UseWindowsChrome = WindowsChromeToggle.IsChecked == true;
            _configService.SetThemeConfiguration(theme);
            _ = _configService.SaveAsync();
            App.ApplyThemeResources();
        }

        // ── Snaps panel ───────────────────────────────────────────────────────
        private void LoadSnapsSettings()
        {
            var snap = _configService.GetSnapConfiguration();
            SSEnabledToggle.IsChecked = snap.ScreenScraperEnabled;
            SSUsernameBox.Text        = snap.ScreenScraperUser;
            SSPasswordBox.Password    = snap.ScreenScraperPassword;
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
            // Toggling enabled state is saved on Save — nothing live needed here.
        }

        private async void SSTestBtn_Click(object sender, RoutedEventArgs e)
        {
            SSTestBtn.IsEnabled = false;
            SSStatusLabel.Text  = "Testing…";
            SSStatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");

            var ss = new Emutastic.Services.ScreenScraperService();
            string? error = await ss.TestLoginAsync(SSUsernameBox.Text.Trim(), SSPasswordBox.Password);

            SSStatusLabel.Text = error == null ? "Connected" : error;
            SSStatusLabel.Foreground = error == null
                ? System.Windows.Media.Brushes.LightGreen
                : (System.Windows.Media.Brush)FindResource("AccentBrush");
            SSTestBtn.IsEnabled = true;
        }

        private void SnapsSaveBtn_Click(object sender, RoutedEventArgs e)
        {
            var snap = _configService.GetSnapConfiguration();
            snap.ScreenScraperEnabled  = SSEnabledToggle.IsChecked == true;
            snap.ScreenScraperUser     = SSUsernameBox.Text.Trim();
            snap.ScreenScraperPassword = SSPasswordBox.Password;
            _configService.SetSnapConfiguration(snap);
            _ = _configService.SaveAsync();
        }

        // ── Achievements tab ─────────────────────────────────────────────────

        private void LoadAchievementsSettings()
        {
            var ra = _configService.GetRetroAchievementsConfiguration();
            RAEnabledToggle.IsChecked = ra.Enabled;
            RAUsernameBox.Text        = ra.Username;
            RAApiKeyBox.Password      = ra.ApiKey;
            RAHardcoreToggle.IsChecked = ra.HardcoreMode;
        }

        private async void RATestBtn_Click(object sender, RoutedEventArgs e)
        {
            RATestBtn.IsEnabled = false;
            RAStatusLabel.Text  = "Testing…";
            RAStatusLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");

            var svc = new Services.RetroAchievementsService();
            string? error = await svc.TestLoginAsync(RAUsernameBox.Text.Trim(), RAApiKeyBox.Password);

            RAStatusLabel.Text = error == null ? "Connected" : error;
            RAStatusLabel.Foreground = error == null
                ? System.Windows.Media.Brushes.LightGreen
                : (System.Windows.Media.Brush)FindResource("AccentBrush");
            RATestBtn.IsEnabled = true;
        }

        private void RASaveBtn_Click(object sender, RoutedEventArgs e)
        {
            var ra = _configService.GetRetroAchievementsConfiguration();
            ra.Enabled      = RAEnabledToggle.IsChecked == true;
            ra.Username     = RAUsernameBox.Text.Trim();
            ra.ApiKey       = RAApiKeyBox.Password;
            ra.HardcoreMode = RAHardcoreToggle.IsChecked == true;
            _configService.SetRetroAchievementsConfiguration(ra);
            _ = _configService.SaveAsync();
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

            foreach (var (coreName, displayName, consoleName) in cores)
            {
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

            // Auto-select the first core
            if (cores.Count > 0)
                LoadCoreOptionsForCore(cores[0].Item1);
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
    public record ConsoleItem(string Tag, string Name)
    {
        public override string ToString() => Name;
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

            // Game Boy Advance (optional — mgba has built-in HLE BIOS)
            new("GBA","Game Boy Advance","gba_bios.bin","BIOS (optional, improves compatibility)",16384,"a860e8c0b6d573d191e4ec7db1b1e4f6"),
        };
    }
}
