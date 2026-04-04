using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Emutastic.Configuration;
using Emutastic.Services;

namespace Emutastic.Views
{
    public partial class CorePreferencesWindow : Window
    {
        private IConfigurationService? _configService;
        private ObservableCollection<ConsoleCoreViewModel> _consoles;

        // Per-console core options that are user-configurable
        private static readonly Dictionary<string, List<CoreOptionDefinition>> ConsoleSpecificOptions = new()
        {
            ["N64"] = new()
            {
                new CoreOptionDefinition
                {
                    Key = "parallel-n64-gfxplugin",
                    DisplayName = "GFX Plugin",
                    ValidValues = new() { "glide64", "angrylion", "rice" },
                    DefaultValue = "glide64",
                    Descriptions = new()
                    {
                        ["glide64"] = "GPU (OpenGL, good balance)",
                        ["angrylion"] = "Software (accurate, very slow)",
                        ["rice"] = "GPU (OpenGL, fast, less accurate)",
                    }
                }
            }
        };

        public CorePreferencesWindow()
        {
            InitializeComponent();
            _consoles = new ObservableCollection<ConsoleCoreViewModel>();
            ConsoleList.ItemsSource = _consoles;
        }

        public void Initialize(IConfigurationService configService)
        {
            _configService = configService;
            LoadConsoles();
        }

        private void LoadConsoles()
        {
            _consoles.Clear();

            var coresFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cores");
            var preferences = _configService?.GetCorePreferences() ?? new CorePreferences();

            // Group cores by console and create view models
            foreach (var console in CoreManager.ConsoleCoreMap.OrderBy(c => c.Key))
            {
                string consoleName = console.Key;
                string[] coreDlls = console.Value;

                // Find which cores are actually available
                var availableCores = new List<CoreOption>();
                string? preferredCore = preferences.PreferredCores.TryGetValue(consoleName, out var pref) ? pref : null;

                foreach (var dll in coreDlls)
                {
                    string path = Path.Combine(coresFolder, dll);
                    bool exists = File.Exists(path);
                    string displayName = FormatCoreName(dll);

                    availableCores.Add(new CoreOption
                    {
                        DllName = dll,
                        DisplayName = displayName,
                        IsInstalled = exists
                    });
                }

                // Get the selected core (preferred if set and available, otherwise first available)
                CoreOption? selectedCore = null;
                if (!string.IsNullOrEmpty(preferredCore))
                {
                    selectedCore = availableCores.FirstOrDefault(c => c.DllName == preferredCore && c.IsInstalled)
                                ?? availableCores.FirstOrDefault(c => c.DllName == preferredCore);
                }
                selectedCore ??= availableCores.FirstOrDefault(c => c.IsInstalled) ?? availableCores.First();

                // Build per-core option view models
                var coreOptions = new ObservableCollection<CoreOptionViewModel>();
                if (ConsoleSpecificOptions.TryGetValue(consoleName, out var defs))
                {
                    // Load any saved overrides
                    preferences.CoreOptionOverrides.TryGetValue(consoleName, out var savedOverrides);

                    foreach (var def in defs)
                    {
                        string currentValue = savedOverrides != null && savedOverrides.TryGetValue(def.Key, out var saved)
                            ? saved : def.DefaultValue;

                        var vm = new CoreOptionViewModel
                        {
                            Key = def.Key,
                            DisplayName = def.DisplayName,
                            ValidValues = def.ValidValues,
                            SelectedValue = def.ValidValues.Contains(currentValue) ? currentValue : def.DefaultValue,
                            Descriptions = def.Descriptions
                        };
                        vm.PropertyChanged += OnCoreOptionChanged;
                        coreOptions.Add(vm);
                    }
                }

                var viewModel = new ConsoleCoreViewModel
                {
                    ConsoleName = consoleName,
                    AvailableCores = availableCores,
                    SelectedCore = selectedCore,
                    CoreOptions = coreOptions
                };

                viewModel.PropertyChanged += OnViewModelPropertyChanged;
                _consoles.Add(viewModel);
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ConsoleCoreViewModel.SelectedCore))
            {
                SavePreferences();
            }
        }

        private void OnCoreOptionChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CoreOptionViewModel.SelectedValue))
            {
                SavePreferences();
            }
        }

        private void SavePreferences()
        {
            if (_configService == null) return;

            var preferences = _configService.GetCorePreferences();
            preferences.PreferredCores.Clear();
            preferences.CoreOptionOverrides.Clear();

            foreach (var console in _consoles)
            {
                if (console.SelectedCore != null)
                {
                    preferences.PreferredCores[console.ConsoleName] = console.SelectedCore.DllName;
                }

                // Save core option overrides
                if (console.CoreOptions.Count > 0)
                {
                    var overrides = new Dictionary<string, string>();
                    foreach (var opt in console.CoreOptions)
                        overrides[opt.Key] = opt.SelectedValue;
                    preferences.CoreOptionOverrides[console.ConsoleName] = overrides;
                }
            }

            _configService.SetCorePreferences(preferences);
            _ = _configService.SaveAsync();

            System.Diagnostics.Debug.WriteLine("Core preferences saved");
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            // Clear all preferences and reload
            if (_configService != null)
            {
                var preferences = new CorePreferences();
                _configService.SetCorePreferences(preferences);
                _ = _configService.SaveAsync();
            }

            LoadConsoles();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static string FormatCoreName(string dllName)
        {
            // Remove _libretro.dll suffix and format nicely
            string name = dllName.Replace("_libretro.dll", "", StringComparison.OrdinalIgnoreCase);
            
            // Handle special cases
            name = name switch
            {
                "mesen" => "Mesen",
                "nestopia" => "Nestopia",
                "fceumm" => "FCE Ultra MM",
                "snes9x" => "Snes9x",
                "bsnes" => "bsnes",
                "parallel_n64" => "Parallel N64",
                "mupen64plus_next" => "Mupen64Plus-Next",
                "dolphin" => "Dolphin",
                "mgba" => "mGBA",
                "gambatte" => "Gambatte",
                "desmume" => "DeSmuME",
                "melonds" => "melonDS",
                "mednafen_vb" => "Mednafen Virtual Boy",
                "genesis_plus_gx" => "Genesis Plus GX",
                "picodrive" => "PicoDrive",
                "mednafen_saturn" => "Mednafen Saturn",
                "yabause" => "Yabause",
                "mednafen_psx" => "Mednafen PSX",
                "pcsx_rearmed" => "PCSX-ReARMed",
                "ppsspp" => "PPSSPP",
                "mednafen_pce" => "Mednafen PCE",
                "mednafen_pce_fast" => "Mednafen PCE Fast",
                "mednafen_pcfx" => "Mednafen PC-FX",
                "mednafen_ngp" => "Mednafen Neo Geo Pocket",
                "gearcoleco" => "GearColeco",
                "gearlynx" => "GearLynx",
                "holani" => "Holani",
                "handy" => "Handy",
                "stella" => "Stella",
                "prosystem" => "ProSystem",
                "virtualjaguar" => "Virtual Jaguar",
                "bluemsx" => "blueMSX",
                "freeintv" => "FreeIntv",
                "vecx" => "Vecx",
                "opera" => "Opera (3DO)",
                "quicknes" => "QuickNES",
                _ => char.ToUpper(name[0]) + name.Substring(1).Replace("_", " ")
            };

            return name;
        }
    }

    public class CoreOptionDefinition
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public List<string> ValidValues { get; set; } = new();
        public string DefaultValue { get; set; } = "";
        public Dictionary<string, string> Descriptions { get; set; } = new();
    }

    public class CoreOptionViewModel : INotifyPropertyChanged
    {
        private string _selectedValue = "";

        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public List<string> ValidValues { get; set; } = new();
        public Dictionary<string, string> Descriptions { get; set; } = new();

        public string SelectedValue
        {
            get => _selectedValue;
            set
            {
                if (_selectedValue != value)
                {
                    _selectedValue = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Description));
                }
            }
        }

        public string Description =>
            Descriptions.TryGetValue(_selectedValue, out var desc) ? desc : "";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class InstalledStatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is CoreOption core)
            {
                return core.IsInstalled ? "installed" : "not installed";
            }
            return "unknown";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
