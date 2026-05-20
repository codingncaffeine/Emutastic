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
        /// <summary>
        /// Which XInput controller slot (0-3) this player uses.
        /// -1 means "use default" (Player 1 → slot 0, Player 2 → slot 1, etc.)
        /// </summary>
        public int ControllerSlot { get; set; } = -1;
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

        /// <summary>
        /// AMD/Intel GPU compatibility for ALL OpenGL hardware cores
        /// (GameCube/Dolphin, PSX/Beetle PSX HW, Dreamcast/Flycast, etc.).
        /// When true, those cores render directly to FBO 0 instead of our
        /// managed FBO — fixes the bottom-left / partial-window rendering
        /// bug AMD and Intel GL drivers exhibit when binding non-zero FBOs.
        /// Cost: disables the direct-GPU-present overlay path for affected
        /// cores, falling back to the slower glReadPixels readback. NVIDIA
        /// users leave this off and keep the fast direct-present path.
        /// </summary>
        public bool AmdIntelGpuCompatibility { get; set; } = false;

        /// <summary>
        /// Legacy GameCube-only compatibility flag, kept so saved configs
        /// from older builds don't lose the user's preference. On load,
        /// EmulatorConfiguration.ResolveAmdIntelCompat() OR-s this into
        /// the new AmdIntelGpuCompatibility flag. Don't read this field
        /// directly from console handlers — use the new flag.
        /// </summary>
        public bool GameCubeUseDefaultFramebuffer { get; set; } = false;

        /// <summary>
        /// Returns true if the AMD/Intel GPU compatibility mode should be
        /// active for HW OpenGL cores. Honors both the new generic flag
        /// and the legacy GameCube-only flag for back-compat.
        /// </summary>
        public bool ResolveAmdIntelCompat()
            => AmdIntelGpuCompatibility || GameCubeUseDefaultFramebuffer;
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
        public string ScreenshotsFolder { get; set; } = "";
        public string RecordingsFolder { get; set; } = "";
        /// <summary>
        /// In-game key that triggers a screenshot. Stored as the
        /// System.Windows.Input.Key enum name (e.g. "F12", "F11", "PrintScreen").
        /// Empty means "default": F12 fires (PrintScreen continues to fire too).
        /// </summary>
        public string ScreenshotKey { get; set; } = "";
    }

    // Recording configuration — controls FFmpeg encode quality for the
    // 2D/software-render recording path (RecordingService). The WGC path
    // used by GL/Vulkan cores has its own MediaFoundation pipeline and
    // ignores these settings.
    public class RecordingConfiguration : ConfigurationBase
    {
        /// <summary>Quality preset: "Low", "Medium", "High", "Lossless".</summary>
        public string Quality { get; set; } = "High";

        /// <summary>
        /// Integer upscale applied at encode time using nearest-neighbor.
        /// 1 = native, 2/3/4 = 2x/3x/4x. Bigger output = sharper after platform
        /// re-encode (e.g. YouTube), at the cost of file size and encode time.
        /// </summary>
        public int OutputScale { get; set; } = 2;

        /// <summary>"Auto" (NVENC if available, else x264), "NVENC", or "x264".</summary>
        public string Encoder { get; set; } = "Auto";

        /// <summary>
        /// When true, encode with yuv444p (full chroma) instead of yuv420p.
        /// Sharper color edges on pixel art; some players don't decode 444.
        /// </summary>
        public bool HighChroma { get; set; } = false;

        /// <summary>AAC audio bitrate in kbps. 128 / 192 / 256 / 320.</summary>
        public int AudioBitrateKbps { get; set; } = 192;
    }

    // Theme configuration
    public class ThemeConfiguration : ConfigurationBase
    {
        /// <summary>Grid edge padding in pixels. Clamped 8–64 by the UI.</summary>
        public int GridPadding { get; set; } = 28;
        /// <summary>Right + bottom gap between game cards in pixels — used as the
        /// fallback when a console hasn't been individually tuned via the
        /// toolbar slider. Clamped 4–48 by the UI.</summary>
        public int CardSpacing { get; set; } = 20;
        /// <summary>
        /// Per-console card-spacing override. Key = console id ("PS1", "SNES",
        /// etc.), value = "H,V" pixel pair (e.g. "32,12"). When the user is
        /// browsing a console listed here, MainWindow ignores CardSpacing and
        /// applies these values to LibraryCardMargin. Edited from the toolbar's
        /// H/V slider.
        /// </summary>
        public Dictionary<string, string> PerConsoleSpacing { get; set; } = new();
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

    // RetroAchievements friends configuration — list membership + polling
    // prefs. Per-friend mutable state (cached profile snapshot, unseen
    // counter) lives in SQLite ra_cache under key "friend:{userId}" instead,
    // so the config file isn't hot-churned by every poll cycle.
    public class FriendsConfiguration : ConfigurationBase
    {
        public List<FriendEntry> Friends { get; set; } = new();
        public bool PollingEnabled       { get; set; } = true;
        public int  PollIntervalMin      { get; set; } = 5;
        public bool ToastOnUnlock        { get; set; } = true;
        public bool HardcoreOnlyToast    { get; set; } = false;
        public bool RecentUnlocksExpanded { get; set; } = true;

        // Phase 6b — Leaderboard toasts. Defaults err toward enabled with
        // sound, since the feature is opt-in by virtue of being in the
        // friends list at all.
        public bool LbToastWhenYouBeat   { get; set; } = true;
        public bool LbToastWhenBeaten    { get; set; } = true;
        public bool LbToastForProximity  { get; set; } = false;
        public int  LbToastProximityPct  { get; set; } = 5;
        public int  LbToastCooldownSec   { get; set; } = 30;
        public bool LbToastSoundEnabled  { get; set; } = true;
        public int  LbToastSoundVolume   { get; set; } = 85; // 0-100
    }

    // List-membership row. The integer UserId from RA's GetUserProfile
    // response is the stable PK; Username is refreshed every poll cycle
    // since RA allows username changes.
    public class FriendEntry
    {
        public int    UserId   { get; set; }
        public string Username { get; set; } = "";
        // ISO-8601 timestamp of the most-recent unlock we've already
        // surfaced; new unlocks are anything with Date > this.
        public string LastSeenUnlockDate   { get; set; } = "";
        // First-poll suppression: true between AddAsync and the first
        // successful HTTP 200 poll. While set, the poll seeds
        // LastSeenUnlockDate without firing notifications.
        public bool   JustAdded            { get; set; } = true;
        public bool   IsPrivate            { get; set; }
        public bool   IsInvalid            { get; set; }
        public int    ConsecutiveFailures  { get; set; }
        public string LastError            { get; set; } = "";
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
