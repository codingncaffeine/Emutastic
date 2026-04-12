using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// Manages theme lifecycle — loading built-in presets, applying color tokens
    /// at runtime, and handling console-specific overrides.
    /// </summary>
    public sealed class ThemeService
    {
        private static ThemeService? _instance;
        public static ThemeService Instance => _instance ??= new ThemeService();

        /// <summary>Currently active theme ID (e.g. "builtin.dark").</summary>
        public string ActiveThemeId { get; private set; } = "builtin.dark";

        /// <summary>Whether console-specific color overrides are enabled.</summary>
        public bool EnableConsoleTheming { get; set; }

        /// <summary>The resolved color set for the active theme (all tokens filled).</summary>
        private ThemeColors _activeColors = null!;

        /// <summary>Backup of base colors before a console override was applied.</summary>
        private ThemeColors? _preOverrideColors;

        /// <summary>Built-in theme definitions keyed by ID.</summary>
        private readonly Dictionary<string, BuiltinTheme> _builtinThemes = new();

        private ThemeService()
        {
            RegisterBuiltinThemes();
            _activeColors = GetDefaultColors();
        }

        // ── Built-in theme registry ──────────────────────────────────────

        private record BuiltinTheme(string Id, string Name, ThemeColors Colors);

        private void RegisterBuiltinThemes()
        {
            _builtinThemes["builtin.dark"] = new("builtin.dark", "Dark (Default)", GetDefaultColors());
            _builtinThemes["builtin.light"] = new("builtin.light", "Light", GetLightColors());
            _builtinThemes["builtin.oled"] = new("builtin.oled", "OLED Black", GetOledColors());
            _builtinThemes["builtin.midnight"] = new("builtin.midnight", "Midnight Blue", GetMidnightColors());
        }

        /// <summary>Returns the color set for a given theme ID, or defaults if not found.</summary>
        public ThemeColors GetColorsForTheme(string themeId)
        {
            return _builtinThemes.TryGetValue(themeId, out var theme) ? theme.Colors : GetDefaultColors();
        }

        /// <summary>Returns the list of available themes for the UI.</summary>
        public List<(string Id, string Name)> GetAvailableThemes()
        {
            return _builtinThemes.Values
                .Select(t => (t.Id, t.Name))
                .ToList();
        }

        // ── Theme application ────────────────────────────────────────────

        /// <summary>
        /// Loads and applies a theme by ID. For built-in themes, resolves from
        /// the embedded registry. Pushes all color/brush tokens into
        /// Application.Current.Resources.
        /// </summary>
        public void LoadAndApplyTheme(string themeId)
        {
            if (_builtinThemes.TryGetValue(themeId, out var builtin))
            {
                _activeColors = builtin.Colors;
            }
            else
            {
                // Community themes will be loaded from disk in Phase 3
                _activeColors = GetDefaultColors();
                themeId = "builtin.dark";
            }

            ActiveThemeId = themeId;
            _preOverrideColors = null;
            ApplyColors(_activeColors);

            // Apply background image from theme if present
            ApplyThemeBackgroundImage(themeId, _activeColors);
        }

        /// <summary>
        /// If the loaded theme specifies a background image, resolves its path
        /// and updates ThemeConfiguration so MainWindow can display it.
        /// </summary>
        private void ApplyThemeBackgroundImage(string themeId, ThemeColors colors)
        {
            if (string.IsNullOrWhiteSpace(colors.BackgroundImage)) return;

            var cfg = App.Configuration?.GetThemeConfiguration();
            if (cfg == null) return;

            // Resolve relative path for community themes (assets/bg.png → Themes/{id}/assets/bg.png)
            string imgPath = colors.BackgroundImage;
            if (!Path.IsPathRooted(imgPath))
            {
                var themeDir = Path.Combine(ThemesFolder, themeId);
                imgPath = Path.Combine(themeDir, imgPath);
            }

            if (File.Exists(imgPath))
            {
                cfg.BackgroundImagePath = imgPath;
                cfg.BackgroundImageOpacity = colors.BackgroundImageOpacity ?? 1.0;
                cfg.BackgroundImageStretch = colors.BackgroundImageStretch ?? "UniformToFill";
                App.Configuration?.SetThemeConfiguration(cfg);

                // Apply to MainWindow if it's loaded
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.Dispatcher.Invoke(() => mw.ApplyBackgroundImage());
            }
        }

        /// <summary>
        /// Applies a custom-edited color set (from the Theme Editor).
        /// Sets ActiveThemeId to "custom" so it won't match any built-in.
        /// </summary>
        public void ApplyEditedColors(ThemeColors colors)
        {
            ActiveThemeId = "custom";
            _activeColors = colors;
            _preOverrideColors = null;
            ApplyColors(colors);
        }

        /// <summary>
        /// Pushes all color tokens from a ThemeColors instance into
        /// Application.Current.Resources, creating SolidColorBrush for each.
        /// </summary>
        private void ApplyColors(ThemeColors colors)
        {
            var defaults = GetDefaultColors();
            var res = Application.Current.Resources;

            void Set(string tokenName, string? value, string? fallback)
            {
                var hex = value ?? fallback ?? "#FF00FF"; // magenta = missing token
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hex);
                    res[$"{tokenName}Color"] = color;
                    res[$"{tokenName}Brush"] = new SolidColorBrush(color);
                }
                catch
                {
                    // Malformed hex — skip silently
                }
            }

            // Core palette
            Set("BgPrimary", colors.BgPrimary, defaults.BgPrimary);
            Set("BgSecondary", colors.BgSecondary, defaults.BgSecondary);
            Set("BgTertiary", colors.BgTertiary, defaults.BgTertiary);
            Set("BgQuaternary", colors.BgQuaternary, defaults.BgQuaternary);
            Set("BorderSubtle", colors.BorderSubtle, defaults.BorderSubtle);
            Set("BorderNormal", colors.BorderNormal, defaults.BorderNormal);
            Set("TextPrimary", colors.TextPrimary, defaults.TextPrimary);
            Set("TextSecondary", colors.TextSecondary, defaults.TextSecondary);
            Set("TextMuted", colors.TextMuted, defaults.TextMuted);
            Set("Accent", colors.Accent, defaults.Accent);
            Set("AccentHover", colors.AccentHover, defaults.AccentHover);
            Set("Green", colors.Green, defaults.Green);

            // Scrollbar
            Set("ScrollThumb", colors.ScrollThumb, defaults.ScrollThumb);
            Set("ScrollThumbHover", colors.ScrollThumbHover, defaults.ScrollThumbHover);
            Set("ScrollThumbDrag", colors.ScrollThumbDrag, defaults.ScrollThumbDrag);
            Set("ScrollTrack", colors.ScrollTrack, defaults.ScrollTrack);

            // Play Button
            Set("PlayBtnBg", colors.PlayBtnBg, defaults.PlayBtnBg);
            Set("PlayBtnBorder", colors.PlayBtnBorder, defaults.PlayBtnBorder);
            Set("PlayBtnHoverBg", colors.PlayBtnHoverBg, defaults.PlayBtnHoverBg);
            Set("PlayBtnHoverBorder", colors.PlayBtnHoverBorder, defaults.PlayBtnHoverBorder);
            Set("PlayBtnPressedBg", colors.PlayBtnPressedBg, defaults.PlayBtnPressedBg);

            // Accent variants
            Set("AccentPressed", colors.AccentPressed, defaults.AccentPressed);
            Set("AccentDisabled", colors.AccentDisabled, defaults.AccentDisabled);

            // Traffic lights
            Set("TrafficYellow", colors.TrafficYellow, defaults.TrafficYellow);
            Set("TrafficYellowHover", colors.TrafficYellowHover, defaults.TrafficYellowHover);
            Set("TrafficGreenHover", colors.TrafficGreenHover, defaults.TrafficGreenHover);
            Set("TrafficRed", colors.TrafficRed, defaults.TrafficRed);
            Set("TrafficRedHover", colors.TrafficRedHover, defaults.TrafficRedHover);

            // Overlay / Shadow
            Set("OverlayBg", colors.OverlayBg, defaults.OverlayBg);
            Set("Shadow", colors.Shadow, defaults.Shadow);

            // Pill controls
            Set("PillBg", colors.PillBg, defaults.PillBg);
            Set("PillBorder", colors.PillBorder, defaults.PillBorder);
            Set("PillHoverBg", colors.PillHoverBg, defaults.PillHoverBg);
            Set("PillPressedBg", colors.PillPressedBg, defaults.PillPressedBg);
            Set("PillFg", colors.PillFg, defaults.PillFg);
            Set("PillMutedFg", colors.PillMutedFg, defaults.PillMutedFg);

            // Surfaces
            Set("Surface", colors.Surface, defaults.Surface);
            Set("SurfaceHover", colors.SurfaceHover, defaults.SurfaceHover);
            Set("SurfaceActive", colors.SurfaceActive, defaults.SurfaceActive);
            Set("ContentBg", colors.ContentBg, defaults.ContentBg);
            Set("Warning", colors.Warning, defaults.Warning);

            // Misc
            Set("PillGroupBg", colors.PillGroupBg, defaults.PillGroupBg);
            Set("AchievementGold", colors.AchievementGold, defaults.AchievementGold);
            Set("FavoriteHeart", colors.FavoriteHeart, defaults.FavoriteHeart);
        }

        // ── Console-specific overrides ───────────────────────────────────

        /// <summary>
        /// Temporarily applies console-specific color overrides (accent, bg) when
        /// the user navigates to a console library. Only active if EnableConsoleTheming is true.
        /// </summary>
        public void ApplyConsoleOverride(string consoleName)
        {
            if (!EnableConsoleTheming) return;
            if (_activeColors.ConsoleOverrides == null) return;
            if (!_activeColors.ConsoleOverrides.TryGetValue(consoleName, out var overrides)) return;

            // Save current state so we can restore later
            _preOverrideColors ??= CloneColors(_activeColors);

            // Merge only the non-null override values
            var merged = CloneColors(_activeColors);
            MergeOverrides(merged, overrides);
            ApplyColors(merged);
        }

        /// <summary>Restores the base theme colors after a console override.</summary>
        public void ResetConsoleOverride()
        {
            if (_preOverrideColors == null) return;
            ApplyColors(_preOverrideColors);
            _preOverrideColors = null;
        }

        // ── Theme installation & scanning ───────────────────────────────

        /// <summary>Themes directory under DataRoot.</summary>
        private static string ThemesFolder => AppPaths.GetFolder("Themes");

        /// <summary>
        /// Installs a .emutheme file by extracting it into the Themes directory.
        /// Returns the installed theme ID, or null on failure.
        /// </summary>
        public string? InstallTheme(string emuthemePath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(emuthemePath);

                // Read manifest
                var manifestEntry = zip.GetEntry("theme.json");
                if (manifestEntry == null) return null;

                ThemeManifest? manifest;
                using (var reader = new StreamReader(manifestEntry.Open()))
                {
                    manifest = JsonSerializer.Deserialize<ThemeManifest>(reader.ReadToEnd());
                }
                if (manifest == null || string.IsNullOrEmpty(manifest.Id)) return null;

                // Sanitize ID — reject path traversal attempts
                if (manifest.Id.Contains("..") || manifest.Id.Contains('/') || manifest.Id.Contains('\\'))
                    return null;
                manifest.Id = string.Join("_", manifest.Id.Split(Path.GetInvalidFileNameChars()));

                // Extract to Themes/{id}/
                var destDir = Path.Combine(ThemesFolder, manifest.Id);
                if (Directory.Exists(destDir))
                    Directory.Delete(destDir, true);
                Directory.CreateDirectory(destDir);

                zip.ExtractToDirectory(destDir, overwriteFiles: true);
                ScanInstalledThemes();
                return manifest.Id;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Scans the Themes folder for installed community themes and adds them to available list.</summary>
        public void ScanInstalledThemes()
        {
            // Remove previously scanned community themes (keep builtins)
            var toRemove = _builtinThemes.Keys.Where(k => !k.StartsWith("builtin.")).ToList();
            foreach (var key in toRemove)
                _builtinThemes.Remove(key);

            if (!Directory.Exists(ThemesFolder)) return;

            foreach (var dir in Directory.GetDirectories(ThemesFolder))
            {
                try
                {
                    var manifestPath = Path.Combine(dir, "theme.json");
                    var colorsPath = Path.Combine(dir, "colors.json");
                    if (!File.Exists(manifestPath)) continue;

                    var manifest = JsonSerializer.Deserialize<ThemeManifest>(File.ReadAllText(manifestPath));
                    if (manifest == null || string.IsNullOrEmpty(manifest.Id)) continue;

                    ThemeColors colors;
                    if (File.Exists(colorsPath))
                    {
                        colors = JsonSerializer.Deserialize<ThemeColors>(File.ReadAllText(colorsPath))
                                 ?? GetDefaultColors();
                    }
                    else
                    {
                        colors = GetDefaultColors();
                    }

                    _builtinThemes[manifest.Id] = new BuiltinTheme(manifest.Id, manifest.Name, colors);
                }
                catch { /* skip malformed themes */ }
            }
        }

        /// <summary>Removes an installed community theme.</summary>
        public bool UninstallTheme(string themeId)
        {
            if (themeId.StartsWith("builtin.")) return false;
            var dir = Path.Combine(ThemesFolder, themeId);
            if (!Directory.Exists(dir)) return false;
            try
            {
                Directory.Delete(dir, true);
                _builtinThemes.Remove(themeId);
                return true;
            }
            catch { return false; }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static ThemeColors CloneColors(ThemeColors src)
        {
            var json = JsonSerializer.Serialize(src);
            return JsonSerializer.Deserialize<ThemeColors>(json) ?? new ThemeColors();
        }

        private static void MergeOverrides(ThemeColors target, ThemeColors overrides)
        {
            // Use reflection to copy non-null string properties
            foreach (var prop in typeof(ThemeColors).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(string)) continue;
                var val = prop.GetValue(overrides) as string;
                if (val != null)
                    prop.SetValue(target, val);
            }
        }

        // ── Default (Dark) theme colors ──────────────────────────────────
        // These MUST match DarkTheme.xaml exactly — pixel-identical.

        public static ThemeColors GetDefaultColors() => new()
        {
            BgPrimary = "#0F0F10",
            BgSecondary = "#181819",
            BgTertiary = "#1F1F21",
            BgQuaternary = "#272729",
            BorderSubtle = "#1A1A1C",
            BorderNormal = "#2A2A2D",
            TextPrimary = "#F0F0F0",
            TextSecondary = "#8A8A90",
            TextMuted = "#555558",
            Accent = "#E03535",
            AccentHover = "#EB5555",
            Green = "#28C840",
            ScrollThumb = "#484849",
            ScrollThumbHover = "#666668",
            ScrollThumbDrag = "#888889",
            ScrollTrack = "#222224",
            PlayBtnBg = "#2C2C2E",
            PlayBtnBorder = "#3A3A3C",
            PlayBtnHoverBg = "#3A3A3C",
            PlayBtnHoverBorder = "#48484A",
            PlayBtnPressedBg = "#1C1C1E",
            AccentPressed = "#C02020",
            AccentDisabled = "#6B2020",
            TrafficYellow = "#FEBC2E",
            TrafficYellowHover = "#E5A800",
            TrafficGreenHover = "#1FA832",
            TrafficRed = "#FF5F57",
            TrafficRedHover = "#E5453D",
            OverlayBg = "#121214",
            Shadow = "#000000",
            PillBg = "#2D2D2D",
            PillBorder = "#1A1A1A",
            PillHoverBg = "#555555",
            PillPressedBg = "#3D3D3D",
            PillFg = "#E0E0E0",
            PillMutedFg = "#A0A0A0",
            Surface = "#3A3A3E",
            SurfaceHover = "#2E2E32",
            SurfaceActive = "#4A4A4F",
            ContentBg = "#0C0C0E",
            Warning = "#FFB347",
            PillGroupBg = "#1E1E1E",
            AchievementGold = "#FFD700",
            FavoriteHeart = "#FF6B6B",
            ConsoleOverrides = GetConsoleOverrides(),
        };

        // ── Light theme ──────────────────────────────────────────────────

        private static ThemeColors GetLightColors() => new()
        {
            BgPrimary = "#E8E8EC",
            BgSecondary = "#DDDDE2",
            BgTertiary = "#D2D2D8",
            BgQuaternary = "#C8C8CF",
            BorderSubtle = "#C0C0C8",
            BorderNormal = "#A8A8B2",
            TextPrimary = "#1A1A1E",
            TextSecondary = "#55555E",
            TextMuted = "#888890",
            Accent = "#E03535",
            AccentHover = "#C02020",
            Green = "#1E9E35",
            ScrollThumb = "#A0A0A8",
            ScrollThumbHover = "#888890",
            ScrollThumbDrag = "#707078",
            ScrollTrack = "#D5D5DB",
            PlayBtnBg = "#D0D0D6",
            PlayBtnBorder = "#B0B0B8",
            PlayBtnHoverBg = "#C0C0C8",
            PlayBtnHoverBorder = "#A0A0A8",
            PlayBtnPressedBg = "#D8D8DE",
            AccentPressed = "#A01818",
            AccentDisabled = "#D09090",
            TrafficYellow = "#E0A820",
            TrafficYellowHover = "#C89018",
            TrafficGreenHover = "#18922C",
            TrafficRed = "#E04840",
            TrafficRedHover = "#C83830",
            OverlayBg = "#D0D0D6",
            Shadow = "#000000",
            PillBg = "#D0D0D6",
            PillBorder = "#B8B8C0",
            PillHoverBg = "#B8B8C0",
            PillPressedBg = "#C5C5CC",
            PillFg = "#2A2A30",
            PillMutedFg = "#707078",
            Surface = "#C5C5CC",
            SurfaceHover = "#CDCDD4",
            SurfaceActive = "#B8B8C0",
            ContentBg = "#E0E0E5",
            Warning = "#D07800",
            PillGroupBg = "#D5D5DB",
            AchievementGold = "#C89800",
            FavoriteHeart = "#E05555",
            ConsoleOverrides = GetConsoleOverrides(),
        };

        // ── OLED Black theme ─────────────────────────────────────────────

        private static ThemeColors GetOledColors() => new()
        {
            BgPrimary = "#000000",
            BgSecondary = "#0A0A0A",
            BgTertiary = "#141414",
            BgQuaternary = "#1C1C1C",
            BorderSubtle = "#1A1A1A",
            BorderNormal = "#2A2A2A",
            TextPrimary = "#E5E5E5",
            TextSecondary = "#808080",
            TextMuted = "#4D4D4D",
            Accent = "#E03535",
            AccentHover = "#EB5555",
            Green = "#28C840",
            ScrollThumb = "#3A3A3A",
            ScrollThumbHover = "#555555",
            ScrollThumbDrag = "#6E6E6E",
            ScrollTrack = "#0F0F0F",
            PlayBtnBg = "#1A1A1A",
            PlayBtnBorder = "#2A2A2A",
            PlayBtnHoverBg = "#2A2A2A",
            PlayBtnHoverBorder = "#3A3A3A",
            PlayBtnPressedBg = "#0F0F0F",
            AccentPressed = "#C02020",
            AccentDisabled = "#6B2020",
            TrafficYellow = "#FEBC2E",
            TrafficYellowHover = "#E5A800",
            TrafficGreenHover = "#1FA832",
            TrafficRed = "#FF5F57",
            TrafficRedHover = "#E5453D",
            OverlayBg = "#050505",
            Shadow = "#000000",
            PillBg = "#1A1A1A",
            PillBorder = "#0F0F0F",
            PillHoverBg = "#3A3A3A",
            PillPressedBg = "#2A2A2A",
            PillFg = "#D0D0D0",
            PillMutedFg = "#808080",
            Surface = "#2A2A2A",
            SurfaceHover = "#1E1E1E",
            SurfaceActive = "#3A3A3A",
            ContentBg = "#050505",
            Warning = "#FFB347",
            PillGroupBg = "#0F0F0F",
            AchievementGold = "#FFD700",
            FavoriteHeart = "#FF6B6B",
            ConsoleOverrides = GetConsoleOverrides(),
        };

        // ── Midnight Blue theme ──────────────────────────────────────────

        private static ThemeColors GetMidnightColors() => new()
        {
            BgPrimary = "#0A0E1A",
            BgSecondary = "#111827",
            BgTertiary = "#1E293B",
            BgQuaternary = "#334155",
            BorderSubtle = "#1E293B",
            BorderNormal = "#334155",
            TextPrimary = "#F1F5F9",
            TextSecondary = "#94A3B8",
            TextMuted = "#64748B",
            Accent = "#3B82F6",
            AccentHover = "#60A5FA",
            Green = "#22C55E",
            ScrollThumb = "#475569",
            ScrollThumbHover = "#64748B",
            ScrollThumbDrag = "#94A3B8",
            ScrollTrack = "#1E293B",
            PlayBtnBg = "#1E293B",
            PlayBtnBorder = "#334155",
            PlayBtnHoverBg = "#334155",
            PlayBtnHoverBorder = "#475569",
            PlayBtnPressedBg = "#0F172A",
            AccentPressed = "#2563EB",
            AccentDisabled = "#1E3A5F",
            TrafficYellow = "#FEBC2E",
            TrafficYellowHover = "#E5A800",
            TrafficGreenHover = "#16A34A",
            TrafficRed = "#FF5F57",
            TrafficRedHover = "#E5453D",
            OverlayBg = "#0F172A",
            Shadow = "#000000",
            PillBg = "#1E293B",
            PillBorder = "#0F172A",
            PillHoverBg = "#475569",
            PillPressedBg = "#334155",
            PillFg = "#E2E8F0",
            PillMutedFg = "#94A3B8",
            Surface = "#334155",
            SurfaceHover = "#1E293B",
            SurfaceActive = "#475569",
            ContentBg = "#060A14",
            Warning = "#F59E0B",
            PillGroupBg = "#0F172A",
            AchievementGold = "#FFD700",
            FavoriteHeart = "#FF6B6B",
            ConsoleOverrides = GetConsoleOverrides(),
        };

        /// <summary>
        /// Shared console-specific color overrides used by all built-in themes.
        /// Keys match the console tags used in sidebar navigation (NES, SNES, GB, etc.).
        /// Only Accent and AccentHover are overridden — backgrounds stay with the base theme.
        /// </summary>
        private static Dictionary<string, ThemeColors> GetConsoleOverrides() => new()
        {
            ["NES"] = new() { Accent = "#C8102E", AccentHover = "#E0302E" },
            ["FDS"] = new() { Accent = "#C8102E", AccentHover = "#E0302E" },
            ["SNES"] = new() { Accent = "#7B2FBE", AccentHover = "#9B4FDE" },
            ["N64"] = new() { Accent = "#007934", AccentHover = "#209944" },
            ["GameCube"] = new() { Accent = "#6A0DAD", AccentHover = "#8A2DCD" },
            ["GB"] = new() { Accent = "#9BBC0F", AccentHover = "#ABCC2F" },
            ["GBC"] = new() { Accent = "#6A5ACD", AccentHover = "#8A7AED" },
            ["GBA"] = new() { Accent = "#4A00A0", AccentHover = "#6A20C0" },
            ["NDS"] = new() { Accent = "#B0B0B0", AccentHover = "#D0D0D0" },
            ["Genesis"] = new() { Accent = "#2196F3", AccentHover = "#42B6FF" },
            ["SegaCD"] = new() { Accent = "#00BCD4", AccentHover = "#26D6E8" },
            ["Sega32X"] = new() { Accent = "#1565C0", AccentHover = "#3585E0" },
            ["Saturn"] = new() { Accent = "#FF9800", AccentHover = "#FFB840" },
            ["SMS"] = new() { Accent = "#3F51B5", AccentHover = "#5F71D5" },
            ["GameGear"] = new() { Accent = "#E91E63", AccentHover = "#FF3E83" },
            ["SG1000"] = new() { Accent = "#3F51B5", AccentHover = "#5F71D5" },
            ["PS1"] = new() { Accent = "#006FCD", AccentHover = "#0090ED" },
            ["PSP"] = new() { Accent = "#00BCD4", AccentHover = "#26D6E8" },
            ["TG16"] = new() { Accent = "#FF6600", AccentHover = "#FF8830" },
            ["TGCD"] = new() { Accent = "#FF6600", AccentHover = "#FF8830" },
            ["Dreamcast"] = new() { Accent = "#FF6600", AccentHover = "#FF8830" },
            ["Atari2600"] = new() { Accent = "#FF5722", AccentHover = "#FF7742" },
            ["Atari7800"] = new() { Accent = "#FF5722", AccentHover = "#FF7742" },
            ["Jaguar"] = new() { Accent = "#CC0000", AccentHover = "#EC2020" },
            ["NeoGeo"] = new() { Accent = "#FFD700", AccentHover = "#FFE740" },
            ["NGP"] = new() { Accent = "#2196F3", AccentHover = "#42B6FF" },
            ["Arcade"] = new() { Accent = "#FF1744", AccentHover = "#FF4060" },
            ["Vectrex"] = new() { Accent = "#00E5FF", AccentHover = "#40F5FF" },
            ["3DO"] = new() { Accent = "#FF9800", AccentHover = "#FFB840" },
            ["CDi"] = new() { Accent = "#00897B", AccentHover = "#20A99B" },
            ["VirtualBoy"] = new() { Accent = "#FF0000", AccentHover = "#FF3030" },
            ["ColecoVision"] = new() { Accent = "#1976D2", AccentHover = "#3996F2" },
        };
    }
}
