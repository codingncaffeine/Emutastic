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
        public bool ScanLibraryOnStartup { get; set; } = true;
        public bool ShowHiddenFiles { get; set; } = false;
        public string Theme { get; set; } = "Dark"; // Light, Dark, System
        public string Language { get; set; } = "en-US";
        public bool CheckForUpdates { get; set; } = true;
        public bool SendAnonymousUsageData { get; set; } = false;
        public bool EnableDebugLogging { get; set; } = false;
        public int RecentGamesLimit { get; set; } = 20;
        public List<string> FavoriteConsoles { get; set; } = new();
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
