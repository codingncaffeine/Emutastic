using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emutastic.Configuration
{
    // Base configuration class
    public abstract class ConfigurationBase
    {
        public string Version { get; set; } = "1.0";
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
    }

    // Input configuration for each console
    public class InputConfiguration : ConfigurationBase
    {
        public string ConsoleName { get; set; } = "";
        public List<ButtonMapping> KeyboardMappings { get; set; } = new();
        public List<ButtonMapping> ControllerMappings { get; set; } = new();
        public int ControllerDeadzone { get; set; } = 15;
        public bool EnableRumble { get; set; } = true;
        public int ControllerSensitivity { get; set; } = 100;
    }

    // Display configuration
    public class DisplayConfiguration : ConfigurationBase
    {
        public bool FullscreenByDefault { get; set; } = false;
        public bool MaintainAspectRatio { get; set; } = true;
        public bool IntegerScaling { get; set; } = false;
        public string FilterType { get; set; } = "Linear"; // Linear, Nearest, CRT, etc.
        public int DisplayScale { get; set; } = 2;
        public bool VSyncEnabled { get; set; } = true;
        public int FrameRate { get; set; } = 60;
        public string ShaderPreset { get; set; } = "";
    }

    // Emulator configuration
    public class EmulatorConfiguration : ConfigurationBase
    {
        public bool AutoSaveEnabled { get; set; } = true;
        public int AutoSaveInterval { get; set; } = 300; // seconds
        public int MaxSaveStates { get; set; } = 10;
        public bool FastForwardEnabled { get; set; } = true;
        public int FastForwardSpeed { get; set; } = 3;
        public bool RewindEnabled { get; set; } = false;
        public int RewindBufferSize { get; set; } = 10; // seconds
        public string DefaultCoreDirectory { get; set; } = "Cores";
        public bool LoadCheatsAutomatically { get; set; } = false;
    }

    // User preferences
    public class UserPreferences : ConfigurationBase
    {
        public string DefaultLibraryPath { get; set; } = "";
        public string CustomDataDirectory { get; set; } = "";
        public bool ScanLibraryOnStartup { get; set; } = true;
        public bool ShowHiddenFiles { get; set; } = false;
        public string Theme { get; set; } = "Dark"; // Light, Dark, System
        public string Language { get; set; } = "en-US";
        public bool CheckForUpdates { get; set; } = true;
        public bool SendAnonymousUsageData { get; set; } = false;
        public bool EnableDebugLogging { get; set; } = false;
        public int RecentGamesLimit { get; set; } = 20;
        public List<string> FavoriteConsoles { get; set; } = new();
        public string BackupFolder { get; set; } = "";
    }

    // Theme configuration
    public class ThemeConfiguration : ConfigurationBase
    {
        /// <summary>Grid edge padding in pixels. Clamped 8–64 by the UI.</summary>
        public int GridPadding { get; set; } = 28;
        /// <summary>Right + bottom gap between game cards in pixels. Clamped 4–48 by the UI.</summary>
        public int CardSpacing { get; set; } = 20;
        /// <summary>Width of each game card in pixels. Clamped 148–280 by the UI.</summary>
        public int CardWidth { get; set; } = 148;
        /// <summary>
        /// When true, uses standard Windows chrome (system title bar + min/max/close buttons)
        /// instead of the custom macOS-style frameless window.
        /// Applied on next launch.
        /// </summary>
        public bool UseWindowsChrome { get; set; } = false;
        /// <summary>Active theme ID (e.g. "builtin.dark", "builtin.light").</summary>
        public string ActiveThemeId { get; set; } = "builtin.dark";
        /// <summary>Optional path to a background image displayed behind the game grid.</summary>
        public string BackgroundImagePath { get; set; } = "";
        /// <summary>Opacity of the background image (0.0–1.0). Default 1.0 — the image is the hero background.</summary>
        public double BackgroundImageOpacity { get; set; } = 1.0;
        /// <summary>How the background image is stretched. UniformToFill (default), Uniform, Fill, None.</summary>
        public string BackgroundImageStretch { get; set; } = "UniformToFill";
        /// <summary>Zoom level for the background image (1.0 = 100%, 2.0 = 200%).</summary>
        public double BackgroundImageZoom { get; set; } = 1.0;
        /// <summary>Horizontal offset for the background image (-100 to 100, percentage of image width).</summary>
        public double BackgroundImageOffsetX { get; set; } = 0.0;
        /// <summary>Vertical offset for the background image (-100 to 100, percentage of image height).</summary>
        public double BackgroundImageOffsetY { get; set; } = 0.0;
        /// <summary>Whether the background image tiles/repeats instead of stretching.</summary>
        public bool BackgroundImageRepeat { get; set; } = false;
    }

    // Library configuration
    public class LibraryConfiguration : ConfigurationBase
    {
        public string LibraryPath { get; set; } = "";
        public bool CopyToLibrary { get; set; } = false;
        public bool OrganizeByConsole { get; set; } = true;
    }

    // Core preferences - preferred core per console
    public class CorePreferences : ConfigurationBase
    {
        // Dictionary mapping console name to preferred core DLL name
        public Dictionary<string, string> PreferredCores { get; set; } = new();

        // Per-console core option overrides, e.g. "N64" -> { "parallel-n64-gfxplugin" -> "glide64" }
        public Dictionary<string, Dictionary<string, string>> CoreOptionOverrides { get; set; } = new();
    }

    // RetroAchievements configuration
    public class RetroAchievementsConfiguration : ConfigurationBase
    {
        public bool Enabled { get; set; } = false;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        /// <summary>API token returned by rcheevos after a successful password login.</summary>
        public string Token { get; set; } = "";
        /// <summary>Web API Key from retroachievements.org settings (used for Test Connection only).</summary>
        public string ApiKey { get; set; } = "";
        public bool HardcoreMode { get; set; } = false;
    }

    // Video snap provider configuration
    public class SnapConfiguration : ConfigurationBase
    {
        // ScreenScraper — active provider
        public string ScreenScraperUser     { get; set; } = "";
        public string ScreenScraperPassword { get; set; } = "";
        public bool   ScreenScraperEnabled  { get; set; } = false;
        public int    ScreenScraperMaxThreads { get; set; } = 1;

        /// <summary>When true, use ScreenScraper 2D box art instead of libretro thumbnails.</summary>
        public bool PreferScreenScraper2D { get; set; } = false;

        // Per-console 3D box art preference — list of console tags that prefer 3D
        public List<string> Use3DBoxArtConsoles { get; set; } = new();

        // EmuMovies — scaffolded, not yet active
        public string EmuMoviesUser         { get; set; } = "";
        public string EmuMoviesPassword     { get; set; } = "";
        public bool   EmuMoviesEnabled      { get; set; } = false;
    }

    // Button mapping definition
    public class ButtonMapping
    {
        public string ButtonName { get; set; } = "";
        public string InputIdentifier { get; set; } = ""; // Key code or controller button
        public InputType InputType { get; set; } = InputType.Keyboard;
        public string DisplayName { get; set; } = "";
        public int ModifierKeys { get; set; } = 0; // For keyboard modifiers
    }

    public enum InputType
    {
        Keyboard,
        Controller,
        Mouse
    }

    // Controller definition (moved from PreferencesWindow)
    public class ControllerDefinition
    {
        public string Name { get; set; } = "";
        public string ControllerImage { get; set; } = "";
        public List<ButtonDefinition> Buttons { get; set; } = new();
    }

    public class ButtonDefinition
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public ButtonType Type { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Group { get; set; } = "";

        public ButtonDefinition(string name, string displayName, int x, int y, ButtonType type, int width, int height, string group = "")
        {
            Name = name;
            DisplayName = displayName;
            X = x;
            Y = y;
            Type = type;
            Width = width;
            Height = height;
            Group = group;
        }
    }

    public enum ButtonType
    {
        Button,
        DPad,
        Trigger,
        Shoulder,
        Analog,
        AnalogDirection
    }
}
