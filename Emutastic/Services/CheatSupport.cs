using System;
using System.Collections.Generic;

namespace Emutastic.Services
{
    public enum CheatSupportLevel
    {
        Supported,      // Core has a real retro_cheat_set implementation
        NotSupported,   // Core stubs retro_cheat_set or is known not to honor it
        Unknown         // No info — UI shows "may not work" hint
    }

    public class CheatSupportInfo
    {
        public CheatSupportLevel Level { get; init; } = CheatSupportLevel.Unknown;
        public string FormatHint { get; init; } = "";   // e.g. "Game Genie", "GameShark", "address:value"
        public string Example { get; init; } = "";      // shown as placeholder in the Code field
    }

    /// <summary>
    /// Per-core cheat support matrix. Keyed by core .dll filename.
    /// Built from a mix of confirmed source inspection and well-established
    /// libretro community knowledge. Cores not listed default to Unknown,
    /// in which case the UI lets the user try anyway with a "may have no
    /// effect" hint.
    /// </summary>
    public static class CheatSupport
    {
        private static readonly Dictionary<string, CheatSupportInfo> _matrix = new(StringComparer.OrdinalIgnoreCase)
        {
            // ── Nintendo ─────────────────────────────────────────────────────
            ["nestopia_libretro.dll"]         = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Game Genie / raw",          Example = "SXIOPO" },
            ["fceumm_libretro.dll"]           = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Game Genie / raw",          Example = "SXIOPO" },
            ["quicknes_libretro.dll"]         = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Game Genie",                Example = "SXIOPO" },
            ["snes9x_libretro.dll"]           = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Game Genie / Pro Action Replay / raw", Example = "DD62-D4D7" },
            ["bsnes_libretro.dll"]            = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Game Genie / Pro Action Replay / raw", Example = "DD62-D4D7" },
            ["mgba_libretro.dll"]             = new() { Level = CheatSupportLevel.Supported,    FormatHint = "GameShark / Code Breaker / Action Replay / raw", Example = "32003D74 0001" },
            ["gambatte_libretro.dll"]         = new() { Level = CheatSupportLevel.Supported,    FormatHint = "GameShark / Game Genie",    Example = "010100C9" },
            ["desmume_libretro.dll"]          = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Action Replay (DS)",        Example = "020D6B7C 000003E7" },
            ["melonds_libretro.dll"]          = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Action Replay (DS)",        Example = "020D6B7C 000003E7" },
            ["parallel_n64_libretro.dll"]     = new() { Level = CheatSupportLevel.Supported,    FormatHint = "GameShark (N64)",           Example = "8033B170 0064" },
            ["mupen64plus_next_libretro.dll"] = new() { Level = CheatSupportLevel.Supported,    FormatHint = "GameShark (N64)",           Example = "8033B170 0064" },
            ["dolphin_libretro.dll"]          = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Action Replay / Gecko",     Example = "0040A8B0 00000063" },
            ["mednafen_vb_libretro.dll"]      = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Raw address:value",         Example = "00060000:01" },
            ["azahar_libretro.dll"]           = new() { Level = CheatSupportLevel.NotSupported, FormatHint = "",                          Example = "" },

            // ── Sega ─────────────────────────────────────────────────────────
            ["genesis_plus_gx_libretro.dll"]  = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Game Genie / Pro Action Replay / raw", Example = "AJWA-AA6C" },
            ["picodrive_libretro.dll"]        = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Game Genie / raw",          Example = "AJWA-AA6C" },
            ["kronos_libretro.dll"]           = new() { Level = CheatSupportLevel.Unknown,      FormatHint = "Raw address:value",         Example = "16060000:0001" },
            ["mednafen_saturn_libretro.dll"]  = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Raw address:value",         Example = "16060000:0001" },
            ["yabause_libretro.dll"]          = new() { Level = CheatSupportLevel.Unknown,      FormatHint = "Raw",                       Example = "" },
            ["flycast_libretro.dll"]          = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Raw address:value",         Example = "0CB75A22:00000063" },

            // ── Sony ─────────────────────────────────────────────────────────
            ["mednafen_psx_libretro.dll"]     = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Raw address:value",         Example = "8009A7BA:0063" },
            ["pcsx_rearmed_libretro.dll"]     = new() { Level = CheatSupportLevel.Supported,    FormatHint = "GameShark (PS1)",           Example = "8009A7BA 0063" },
            ["ppsspp_libretro.dll"]           = new() { Level = CheatSupportLevel.NotSupported, FormatHint = "",                          Example = "" },

            // ── NEC ──────────────────────────────────────────────────────────
            ["mednafen_pce_libretro.dll"]      = new() { Level = CheatSupportLevel.Supported,   FormatHint = "Raw address:value",         Example = "001F40:63" },
            ["mednafen_pce_fast_libretro.dll"] = new() { Level = CheatSupportLevel.Supported,   FormatHint = "Raw address:value",         Example = "001F40:63" },
            ["mednafen_ngp_libretro.dll"]      = new() { Level = CheatSupportLevel.Supported,   FormatHint = "Raw address:value",         Example = "006C50:63" },

            // ── Atari ────────────────────────────────────────────────────────
            ["stella_libretro.dll"]           = new() { Level = CheatSupportLevel.Supported,    FormatHint = "Raw address:value",         Example = "00F4:01" },
            ["prosystem_libretro.dll"]        = new() { Level = CheatSupportLevel.Unknown,      FormatHint = "",                          Example = "" },
            ["virtualjaguar_libretro.dll"]    = new() { Level = CheatSupportLevel.Unknown,      FormatHint = "",                          Example = "" },

            // ── Other ────────────────────────────────────────────────────────
            ["gearcoleco_libretro.dll"]       = new() { Level = CheatSupportLevel.NotSupported, FormatHint = "",                          Example = "" },
            ["bluemsx_libretro.dll"]          = new() { Level = CheatSupportLevel.Unknown,      FormatHint = "",                          Example = "" },
            ["vecx_libretro.dll"]             = new() { Level = CheatSupportLevel.NotSupported, FormatHint = "",                          Example = "" },
            ["opera_libretro.dll"]            = new() { Level = CheatSupportLevel.NotSupported, FormatHint = "",                          Example = "" },
            ["same_cdi_libretro.dll"]         = new() { Level = CheatSupportLevel.NotSupported, FormatHint = "",                          Example = "" },
            ["geolith_libretro.dll"]          = new() { Level = CheatSupportLevel.NotSupported, FormatHint = "",                          Example = "" },
            ["fbneo_libretro.dll"]            = new() { Level = CheatSupportLevel.Unknown,      FormatHint = "",                          Example = "" },
            ["dosbox_pure_libretro.dll"]      = new() { Level = CheatSupportLevel.NotSupported, FormatHint = "",                          Example = "" },
        };

        /// <summary>
        /// Looks up cheat support for a core. Path or filename both work.
        /// Cores not in the matrix return Unknown.
        /// </summary>
        public static CheatSupportInfo Lookup(string corePathOrName)
        {
            if (string.IsNullOrEmpty(corePathOrName)) return new();
            string filename = System.IO.Path.GetFileName(corePathOrName);
            return _matrix.TryGetValue(filename, out var info) ? info : new();
        }
    }
}
