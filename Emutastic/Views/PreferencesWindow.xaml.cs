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
        private enum PrefSection { Controls, SystemFiles, Cores, Library, Theme, Snaps, CoreOptions, Achievements, Folders }
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
                // Save any pending credential changes on close
                SaveSnapSettings();
                SaveAchievementsSettings();
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
            else if (sender == NavFolders)      ShowSection(PrefSection.Folders);
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
            PanelFolders.Visibility     = section == PrefSection.Folders      ? Visibility.Visible : Visibility.Collapsed;

            if (section == PrefSection.SystemFiles) BuildBiosPanel();
            if (section == PrefSection.Folders)     LoadFoldersSettings();
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
        // BIOS category groupings matching the cores tab
        private static readonly (string Category, string[] ConsoleDisplays)[] BiosCategories =
        {
            ("Nintendo",  new[] { "Famicom Disk System", "Game Boy Advance" }),
            ("Sega",      new[] { "Sega CD", "Saturn" }),
            ("Sony",      new[] { "PlayStation" }),
            ("NEC",       new[] { "TurboGrafx-CD" }),
            ("Arcade",    new[] { "Neo Geo" }),
            ("Other",     new[] { "3DO", "Philips CD-i" }),
        };

        private void BuildBiosPanel()
        {
            BiosPanel.Children.Clear();
            string sysDir = AppPaths.GetFolder("System");

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
                        if (System.IO.File.Exists(path))
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
                            if (romDirs.Any(dir => dir != null && System.IO.File.Exists(
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
                        if (System.IO.File.Exists(path) || romDirs2.Any(dir => dir != null &&
                            System.IO.File.Exists(System.IO.Path.Combine(dir, System.IO.Path.GetFileName(entry.Filename)))))
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
        };

        // ── Core downloader ───────────────────────────────────────────────────
        private readonly CoreDownloadService _downloader = new();
        private CancellationTokenSource? _downloadAllCts;

        // Console-to-category mapping for accordion grouping
        private static readonly (string Category, string[] Consoles)[] ConsoleCategories =
        {
            ("Nintendo",  new[] { "NES", "FDS", "SNES", "N64", "GameCube", "GB", "GBC", "GBA", "NDS", "3DS", "VirtualBoy" }),
            ("Sega",      new[] { "Genesis", "SegaCD", "Sega32X", "Saturn", "SMS", "GameGear", "SG1000", "Dreamcast" }),
            ("Sony",      new[] { "PS1", "PSP" }),
            ("NEC",       new[] { "TG16", "TGCD" }),
            ("Atari",     new[] { "Atari2600", "Atari7800", "Jaguar" }),
            ("Arcade",    new[] { "Arcade", "NeoGeo" }),
            ("Other",     new[] { "NGP", "ColecoVision", "Vectrex", "3DO", "CDi" }),
        };

        private void BuildCoresPanel()
        {
            CoresListPanel.Children.Clear();
            string coresFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cores");
            var prefs = _configService.GetCorePreferences();

            // ── Download All button + progress ──
            var recommended = CoreDownloadService.Catalog.Where(c => c.Recommended).ToList();
            int installedCount = recommended.Count(c => System.IO.File.Exists(
                System.IO.Path.Combine(coresFolder, c.FileName)));

            var dlAllRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            dlAllRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            dlAllRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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

            dlAllRow.Children.Add(dlAllBtn);
            dlAllRow.Children.Add(dlSummary);
            CoresListPanel.Children.Add(dlAllRow);

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
                    catInstalled += cores.Count(dll =>
                        System.IO.File.Exists(System.IO.Path.Combine(coresFolder, dll)));
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
                            Installed = System.IO.File.Exists(System.IO.Path.Combine(coresFolder, dll)),
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
                                BuildCoresPanel();
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
                BuildCoresPanel();
            };

            BuildExtrasSection();
        }

        private async void StartSingleDownload(CoreEntry entry, string coresFolder,
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
                BuildCoresPanel();
            }
            catch (Exception ex)
            {
                statusText.Text = $"Error: {ex.Message}";
                dlBtn.IsEnabled = true;
            }
        }

        // ── Extras section (SDL3 + DAT files) ────────────────────────────────

        private static readonly (string Tag, string Label, string? RedumpSlug, string? DirectUrl)[] KnownDats =
        {
            ("Arcade", "Arcade (FBNeo)",     null, "https://raw.githubusercontent.com/libretro/FBNeo/master/dats/FinalBurn%20Neo%20(ClrMame%20Pro%20XML%2C%20Arcade%20only).dat"),
            ("NeoGeo", "Neo Geo (Geolith)", null, "https://raw.githubusercontent.com/libretro/libretro-database/master/dat/SNK%20-%20Neo%20Geo.dat"),
            ("SegaCD", "Sega CD / Mega CD",  "mcd",  null),
            ("Saturn", "Sega Saturn",        "ss",   null),
            ("PS1",    "PlayStation",         "psx",  null),
            ("TGCD",   "TurboGrafx-CD",      "pce",  null),
            ("3DO",    "3DO",                 "3do",  null),
            ("CDi",    "Philips CD-i",        "cdi",  null),
            ("NGP",    "Neo Geo Pocket",       null, "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/no-intro/SNK%20-%20Neo%20Geo%20Pocket.dat"),
            ("NGPC",   "Neo Geo Pocket Color", null, "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/no-intro/SNK%20-%20Neo%20Geo%20Pocket%20Color.dat"),
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

            // ── ffmpeg.exe row ──
            string ffmpegPath     = System.IO.Path.Combine(baseDir, "ffmpeg.exe");
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
            extrasStack.Children.Add(new TextBlock
            {
                Text         = "DAT files are game databases used during import to auto-detect which console a disc image belongs to and to resolve ROM names for artwork lookup. Without these, some games may show missing cover art or hero images.",
                FontSize     = 11,
                Foreground   = _brushTextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 10)
            });

            // Per-DAT rows
            System.IO.Directory.CreateDirectory(datsDir);
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

                var capturedTag      = tag;
                var capturedSlug     = slug;
                var capturedDirectUrl = directUrl;
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
                        string url = capturedDirectUrl ?? $"http://redump.org/datfile/{capturedSlug}/";
                        var bytes = await http.GetByteArrayAsync(url);
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
                    isLast: false));
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
                isLast: true));

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

            // Backup folder
            var prefs = _configService.GetUserPreferences();
            string backupDir = prefs.BackupFolder;
            if (string.IsNullOrEmpty(backupDir))
            {
                BackupFolderPathText.Text = "Not set";
                BackupFolderPathText.Foreground = _brushTextMuted;
                BackupNowBtn.IsEnabled = false;
                RestoreBackupBtn.IsEnabled = false;
            }
            else
            {
                BackupFolderPathText.Text = backupDir;
                BackupFolderPathText.Foreground = _brushText;
                BackupNowBtn.IsEnabled = true;
                RestoreBackupBtn.IsEnabled = true;
            }
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

            // Background image
            BgImagePathLabel.Text = string.IsNullOrWhiteSpace(theme.BackgroundImagePath)
                ? "No image selected" : theme.BackgroundImagePath;
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
                BgImagePathLabel.Text = dlg.FileName;
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

            foreach (var (id, name) in themes)
            {
                // Get first 3 colors for a mini-swatch
                var colors = GetThemePreviewColors(id);

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
                foreach (var hex in colors)
                {
                    try
                    {
                        var swatch = new System.Windows.Shapes.Rectangle
                        {
                            Width = 16, Height = 16,
                            RadiusX = 3, RadiusY = 3,
                            Margin = new Thickness(0, 0, 3, 0),
                            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                        };
                        colorStrip.Children.Add(swatch);
                    }
                    catch { }
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
            theme.CardSpacing = Math.Clamp((int)SpacingSlider.Value,  4,   48);
            theme.CardWidth   = Math.Clamp((int)CardSizeSlider.Value, 148, 280);
            theme.UseWindowsChrome = WindowsChromeToggle.IsChecked == true;

            // Theme selection
            var selectedThemeId = "builtin.dark";
            if (ThemeCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string id)
                selectedThemeId = id;
            theme.ActiveThemeId = selectedThemeId;

            // Background image settings
            var bgPath = BgImagePathLabel.Text;
            theme.BackgroundImagePath = (bgPath == "No image selected") ? "" : bgPath;
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

        private void SnapsSaveBtn_Click(object sender, RoutedEventArgs e)
            => SaveSnapSettings();

        // ── Achievements tab ─────────────────────────────────────────────────

        private void LoadAchievementsSettings()
        {
            _suppressAutoSave = true;
            _achievementsLoaded = true;
            var ra = _configService.GetRetroAchievementsConfiguration();
            RAEnabledToggle.IsChecked  = ra.Enabled;
            RAUsernameBox.Text         = ra.Username;
            RAPasswordBox.Password     = ra.Password;
            RAHardcoreToggle.IsChecked = ra.HardcoreMode;
            RATokenStatus.Text = !string.IsNullOrEmpty(ra.Token)
                ? "Login token saved — password not required for future sessions."
                : "No login token yet — password required for first login.";
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
            if (RAEnabledToggle == null || _suppressAutoSave || !_achievementsLoaded) return;
            var ra = _configService.GetRetroAchievementsConfiguration();
            ra.Enabled      = RAEnabledToggle.IsChecked == true;
            ra.Username     = RAUsernameBox.Text.Trim();
            ra.Password     = RAPasswordBox.Password;
            ra.HardcoreMode = RAHardcoreToggle.IsChecked == true;
            _configService.SetRetroAchievementsConfiguration(ra);
            _ = _configService.SaveAsync();
        }

        private void RAEnabled_Changed(object sender, RoutedEventArgs e)
            => SaveAchievementsSettings();

        private void RAHardcore_Changed(object sender, RoutedEventArgs e)
            => SaveAchievementsSettings();

        private void RASaveBtn_Click(object sender, RoutedEventArgs e)
            => SaveAchievementsSettings();

        // ── Folders tab ───────────────────────────────────────────────────────

        private void LoadFoldersSettings()
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
        }

        private void ClearScreenshotsFolder_Click(object sender, RoutedEventArgs e)
        {
            ScreenshotsFolderText.Text = "Default";
            ScreenshotsFolderText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
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
        }

        private void ClearRecordingsFolder_Click(object sender, RoutedEventArgs e)
        {
            RecordingsFolderText.Text = "Default";
            RecordingsFolderText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        }

        private void FoldersSaveBtn_Click(object sender, RoutedEventArgs e)
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

            // Neo Geo (Geolith)
            new("NeoGeo","Neo Geo","neogeo.zip","Neo Geo BIOS (required)",0,null),
            new("NeoGeo","Neo Geo","aes.zip","AES BIOS (required)",0,null),

            // Game Boy Advance (optional — mgba has built-in HLE BIOS)
            new("GBA","Game Boy Advance","gba_bios.bin","BIOS (optional, improves compatibility)",16384,"a860e8c0b6d573d191e4ec7db1b1e4f6"),
        };
    }
}
