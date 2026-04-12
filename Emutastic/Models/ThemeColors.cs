using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emutastic.Models
{
    /// <summary>
    /// Deserialized from colors.json inside a .emutheme package or embedded resource.
    /// Every property is nullable — only provided values override the default theme.
    /// </summary>
    public class ThemeColors
    {
        // ── Core palette ──
        public string? BgPrimary { get; set; }
        public string? BgSecondary { get; set; }
        public string? BgTertiary { get; set; }
        public string? BgQuaternary { get; set; }
        public string? BorderSubtle { get; set; }
        public string? BorderNormal { get; set; }
        public string? TextPrimary { get; set; }
        public string? TextSecondary { get; set; }
        public string? TextMuted { get; set; }
        public string? Accent { get; set; }
        public string? AccentHover { get; set; }
        public string? Green { get; set; }

        // ── Scrollbar ──
        public string? ScrollThumb { get; set; }
        public string? ScrollThumbHover { get; set; }
        public string? ScrollThumbDrag { get; set; }
        public string? ScrollTrack { get; set; }

        // ── Play Button ──
        public string? PlayBtnBg { get; set; }
        public string? PlayBtnBorder { get; set; }
        public string? PlayBtnHoverBg { get; set; }
        public string? PlayBtnHoverBorder { get; set; }
        public string? PlayBtnPressedBg { get; set; }

        // ── Accent variants ──
        public string? AccentPressed { get; set; }
        public string? AccentDisabled { get; set; }

        // ── Traffic lights ──
        public string? TrafficYellow { get; set; }
        public string? TrafficYellowHover { get; set; }
        public string? TrafficGreenHover { get; set; }
        public string? TrafficRed { get; set; }
        public string? TrafficRedHover { get; set; }

        // ── Overlay ──
        public string? OverlayBg { get; set; }

        // ── Shadow ──
        public string? Shadow { get; set; }

        // ── Pill controls ──
        public string? PillBg { get; set; }
        public string? PillBorder { get; set; }
        public string? PillHoverBg { get; set; }
        public string? PillPressedBg { get; set; }
        public string? PillFg { get; set; }
        public string? PillMutedFg { get; set; }

        // ── Surfaces ──
        public string? Surface { get; set; }
        public string? SurfaceHover { get; set; }
        public string? SurfaceActive { get; set; }
        public string? ContentBg { get; set; }
        public string? Warning { get; set; }

        // ── Misc ──
        public string? PillGroupBg { get; set; }
        public string? AchievementGold { get; set; }
        public string? FavoriteHeart { get; set; }

        /// <summary>
        /// Console-specific color overrides. Key = console display name (e.g. "Game Boy").
        /// Only Accent, AccentHover, BgPrimary, BgSecondary are typically overridden.
        /// </summary>
        [JsonPropertyName("consoleOverrides")]
        public Dictionary<string, ThemeColors>? ConsoleOverrides { get; set; }
    }
}
