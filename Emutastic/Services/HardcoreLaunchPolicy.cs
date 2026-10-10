using System;
using System.Collections.Generic;

namespace Emutastic.Services
{
    /// <summary>Why a launch that asked for hardcore runs as casual instead.</summary>
    public enum HardcoreDemotion
    {
        None,
        /// <summary>PPSSPP reads cheats/&lt;DiscID&gt;.ini itself; the frontend cannot gate that path.</summary>
        Psp,
        /// <summary>The active HD mod carries a &lt;patch&gt;: the running code differs from the hashed file.</summary>
        RomPatchMod,
    }

    /// <summary>
    /// The launch-time hardcore decision and the core options a hardcore session locks, in one
    /// place so the emulator window, the self-test and the Hardcore Compliance wiki page describe
    /// the same rules. RetroAchievements' hardcore-compliance requirements (Section B) forbid cheats
    /// in hardcore, a core's built-in cheat engine included; several cores switch theirs on through
    /// a core option, which the libretro cheat API never sees.
    /// </summary>
    public static class HardcoreLaunchPolicy
    {
        /// <summary>
        /// Whether the session runs in hardcore, given the user's setting and the launch. Only the
        /// two carve-outs demote; a session that starts casual stays casual.
        /// </summary>
        public static (bool Hardcore, HardcoreDemotion Reason) Evaluate(bool hardcoreSetting, string? console, bool activeModPatchesRom)
        {
            if (!hardcoreSetting) return (false, HardcoreDemotion.None);
            if (string.Equals(console, "PSP", StringComparison.Ordinal)) return (false, HardcoreDemotion.Psp);
            if (activeModPatchesRom) return (false, HardcoreDemotion.RomPatchMod);
            return (true, HardcoreDemotion.None);
        }

        /// <summary>The on-screen line for a demotion; null when there is none.</summary>
        public static string? DemotionMessage(HardcoreDemotion reason) => reason switch
        {
            HardcoreDemotion.Psp         => "Hardcore Mode is disabled for PSP titles — achievements still track",
            HardcoreDemotion.RomPatchMod => "Hardcore disabled — the active HD mod patches the ROM; achievements still track",
            _ => null,
        };

        // Core options that switch on a core's own cheat engine, each read from the core's
        // option table (October 2026):
        //   dolphin_cheats_enabled           Dolphin          "Internal Cheats" — Gecko / Action Replay
        //                                                     codes from the game's INI
        //   ppsspp_cheats                    PPSSPP           "Internal Cheats Support" — CWCheat .ini;
        //                                                     PSP already demotes, this is a second lock
        //   mame_cheats_enable               MAME             "Cheats" — the cheat.7z collection
        //   mame2003-plus_cheat_input_ports  MAME 2003-Plus   "Dip Switch/Cheat Input Ports" — the
        //                                                     IPF_CHEAT ports in the MAME menu
        //   fbneo-cheat-<n>-<driver>-<name>  FBNeo            one option per cheat in the driver's
        //                                                     cheat file (libretro.cpp builds the key)
        private static readonly HashSet<string> CheatOptionKeys = new(StringComparer.Ordinal)
        {
            "dolphin_cheats_enabled",
            "ppsspp_cheats",
            "mame_cheats_enable",
            "mame2003-plus_cheat_input_ports",
        };
        private const string FbneoCheatPrefix = "fbneo-cheat-";

        /// <summary>True for a core option that enables or selects a cheat.</summary>
        public static bool IsCheatCoreOption(string? key)
            => !string.IsNullOrEmpty(key)
               && (CheatOptionKeys.Contains(key) || key.StartsWith(FbneoCheatPrefix, StringComparison.Ordinal));

        /// <summary>
        /// The value a locked option is served in hardcore: "disabled" in the core's own spelling
        /// when the core lists it, otherwise the core's default (the first listed value), otherwise
        /// "disabled".
        /// </summary>
        public static string LockedValue(IReadOnlyList<string>? validValues)
        {
            if (validValues != null)
            {
                foreach (string v in validValues)
                    if (string.Equals(v, "disabled", StringComparison.OrdinalIgnoreCase)) return v;
                if (validValues.Count > 0) return validValues[0];
            }
            return "disabled";
        }
    }
}
