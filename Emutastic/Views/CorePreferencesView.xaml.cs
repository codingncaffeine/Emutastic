using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Emutastic.Configuration;
using Emutastic.Services;

namespace Emutastic.Views
{
    public partial class CorePreferencesView : UserControl
    {
        private IConfigurationService? _configService;
        private CoreManager _coreManager;
        private ObservableCollection<ConsoleCoreViewModel> _consoles;

        public CorePreferencesView()
        {
            InitializeComponent();
            _coreManager = new CoreManager();
            _consoles = new ObservableCollection<ConsoleCoreViewModel>();
            ConsoleList.ItemsSource = _consoles;
        }

        public void Initialize(IConfigurationService configService)
        {
            _configService = configService;
            _coreManager = new CoreManager(configService);
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
                        DisplayName = displayName + (exists ? "" : " (not installed)"),
                        IsInstalled = exists
                    });
                }

                // Get the selected core (preferred if set and available, otherwise first available)
                CoreOption? selectedCore = null;
                if (!string.IsNullOrEmpty(preferredCore))
                {
                    selectedCore = availableCores.FirstOrDefault(c => c.DllName == preferredCore && c.IsInstalled);
                }
                selectedCore ??= availableCores.FirstOrDefault(c => c.IsInstalled) ?? availableCores.First();

                var viewModel = new ConsoleCoreViewModel
                {
                    ConsoleName = consoleName,
                    AvailableCores = availableCores,
                    SelectedCore = selectedCore
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

        private void SavePreferences()
        {
            if (_configService == null) return;

            var preferences = _configService.GetCorePreferences();
            preferences.PreferredCores.Clear();

            foreach (var console in _consoles)
            {
                if (console.SelectedCore != null)
                {
                    preferences.PreferredCores[console.ConsoleName] = console.SelectedCore.DllName;
                }
            }

            _configService.SetCorePreferences(preferences);
            _ = _configService.SaveAsync();

            System.Diagnostics.Debug.WriteLine("Core preferences saved");
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
                "mednafen_ngp" => "Mednafen Neo Geo Pocket",
                "gearcoleco" => "GearColeco",
                "stella" => "Stella",
                "a5200" => "a5200",
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

    public class ConsoleCoreViewModel : INotifyPropertyChanged
    {
        private CoreOption? _selectedCore;

        public string ConsoleName { get; set; } = "";
        public List<CoreOption> AvailableCores { get; set; } = new();
        public ObservableCollection<CoreOptionViewModel> CoreOptions { get; set; } = new();
        public bool HasCoreOptions => CoreOptions.Count > 0;

        public CoreOption? SelectedCore
        {
            get => _selectedCore;
            set
            {
                if (_selectedCore != value)
                {
                    _selectedCore = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class CoreOption
    {
        public string DllName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsInstalled { get; set; }
        public string StatusText => IsInstalled ? "installed" : "not installed";
    }
}
