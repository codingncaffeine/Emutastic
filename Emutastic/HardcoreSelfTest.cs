using Emutastic.Services;
using System;
using System.IO;

namespace Emutastic
{
    /// <summary>
    /// <c>Emutastic.exe --selftest-hardcore [report.log]</c>: the RetroAchievements hardcore rules
    /// that live in pure code — the launch decision (the setting, the PSP and ROM-patch carve-outs),
    /// the core options a hardcore session locks and the value they are served, and the disc file a
    /// swap hands rcheevos (playlist entry → file: comments, labels, relative and absolute entries,
    /// a missing file, an index past the end, a launch that is not a playlist). Works in a throwaway
    /// folder under the temp directory, removed afterwards; no window, no PortableData. Exit 0 =
    /// every check passed, 1 = a check failed. The report ends with "=== PASS ===" or "=== FAIL ===".
    /// </summary>
    internal static class HardcoreSelfTest
    {
        public static int Run(string? reportPath)
        {
            StreamWriter? report = null;
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                try
                {
                    string full = Path.GetFullPath(reportPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
                    report = new StreamWriter(full, append: false) { AutoFlush = true };
                }
                catch (Exception ex) { Console.WriteLine($"[hardcore-selftest] cannot open report '{reportPath}': {ex.Message}"); }
            }
            void Line(string s)
            {
                Console.WriteLine(s);
                report?.WriteLine(s);
            }
            int failed;
            try { failed = RunChecks(Line); }
            catch (Exception ex)
            {
                Line($"  [FAIL] unhandled: {ex}");
                failed = 1;
            }
            Line(failed == 0 ? "=== PASS ===" : "=== FAIL ===");
            report?.Dispose();
            return failed == 0 ? 0 : 1;
        }

        /// <summary>Every check, reported through <paramref name="line"/>; returns how many failed.</summary>
        public static int RunChecks(Action<string> line)
        {
            int pass = 0, fail = 0;
            void Check(string what, bool ok)
            {
                if (ok) pass++; else fail++;
                line($"  [{(ok ? "ok  " : "FAIL")}] {what}");
            }

            line("--- launch decision (HardcoreLaunchPolicy.Evaluate)");
            Check("a casual setting stays casual",
                HardcoreLaunchPolicy.Evaluate(false, "NES", false) == (false, HardcoreDemotion.None));
            Check("a casual setting on PSP is casual with no demotion to explain",
                HardcoreLaunchPolicy.Evaluate(false, "PSP", false) == (false, HardcoreDemotion.None));
            Check("a hardcore setting runs hardcore",
                HardcoreLaunchPolicy.Evaluate(true, "NES", false) == (true, HardcoreDemotion.None));
            Check("PSP demotes to casual (PPSSPP reads its own cheat files)",
                HardcoreLaunchPolicy.Evaluate(true, "PSP", false) == (false, HardcoreDemotion.Psp));
            Check("an HD mod that patches the ROM demotes to casual",
                HardcoreLaunchPolicy.Evaluate(true, "NES", true) == (false, HardcoreDemotion.RomPatchMod));
            Check("PSP is reported before the ROM patch when both apply",
                HardcoreLaunchPolicy.Evaluate(true, "PSP", true) == (false, HardcoreDemotion.Psp));
            Check("every demotion has an on-screen message, no demotion has none",
                HardcoreLaunchPolicy.DemotionMessage(HardcoreDemotion.Psp) != null
                && HardcoreLaunchPolicy.DemotionMessage(HardcoreDemotion.RomPatchMod) != null
                && HardcoreLaunchPolicy.DemotionMessage(HardcoreDemotion.None) == null);

            line("--- cheat core options (HardcoreLaunchPolicy.IsCheatCoreOption / LockedValue)");
            foreach (string key in new[] { "dolphin_cheats_enabled", "ppsspp_cheats", "mame_cheats_enable",
                                           "mame2003-plus_cheat_input_ports", "fbneo-cheat-0-sfa3-Infinite_Time" })
                Check($"{key} is a cheat option", HardcoreLaunchPolicy.IsCheatCoreOption(key));
            foreach (string key in new[] { "fbneo-dipswitch-sfa3-Difficulty", "dolphin_gfx_backend", "ppsspp_internal_resolution",
                                           "mame2003-plus_skip_disclaimer", "fbneo-cheat", "" })
                Check($"'{key}' is not a cheat option", !HardcoreLaunchPolicy.IsCheatCoreOption(key));
            Check("null is not a cheat option", !HardcoreLaunchPolicy.IsCheatCoreOption(null));
            Check("locked value prefers 'disabled' wherever the core lists it",
                HardcoreLaunchPolicy.LockedValue(new[] { "enabled", "disabled" }) == "disabled");
            Check("locked value keeps the core's spelling of disabled",
                HardcoreLaunchPolicy.LockedValue(new[] { "Enabled", "Disabled" }) == "Disabled");
            Check("locked value falls back to the core's default (first value)",
                HardcoreLaunchPolicy.LockedValue(new[] { "0 - Off", "1 - Infinite lives" }) == "0 - Off");
            Check("locked value is 'disabled' when the core listed nothing",
                HardcoreLaunchPolicy.LockedValue(Array.Empty<string>()) == "disabled"
                && HardcoreLaunchPolicy.LockedValue(null) == "disabled");

            line("--- disc playlist (DiscPlaylist.ResolveDiscPath)");
            string root = Path.Combine(Path.GetTempPath(), $"emutastic-hardcore-selftest-{Environment.ProcessId}");
            string nested = Path.Combine(root, "discs");
            string disc1 = Path.Combine(root, "Game (Disc 1).cue");
            string disc2 = Path.Combine(nested, "Game (Disc 2).chd");
            string disc3 = Path.Combine(root, "Game (Disc 3).cue");
            string m3u = Path.Combine(root, "Game.m3u");
            string missingM3u = Path.Combine(root, "Other.m3u");
            try
            {
                Directory.CreateDirectory(nested);
                File.WriteAllText(disc1, "FILE \"Game (Disc 1).bin\" BINARY\n");
                File.WriteAllBytes(disc2, new byte[] { 1, 2, 3 });
                File.WriteAllText(disc3, "FILE \"Game (Disc 3).bin\" BINARY\n");
                File.WriteAllLines(m3u, new[]
                {
                    "# a comment the cores skip",
                    "",
                    "Game (Disc 1).cue",
                    disc2 + "|Disc 2",                 // absolute, with a label
                    "   discs/../Game (Disc 3).cue  ", // relative with a dot segment and padding
                    "Game (Disc 4).cue",               // listed but not on disk
                });

                Check("entry 0 resolves the relative file next to the playlist",
                    HardcoreLaunchPolicy_PathEquals(DiscPlaylist.ResolveDiscPath(m3u, 0), disc1));
                Check("entry 1 resolves an absolute file and drops its label",
                    HardcoreLaunchPolicy_PathEquals(DiscPlaylist.ResolveDiscPath(m3u, 1), disc2));
                Check("entry 2 resolves a padded relative path with a dot segment",
                    HardcoreLaunchPolicy_PathEquals(DiscPlaylist.ResolveDiscPath(m3u, 2), disc3));
                Check("entry 3 is listed but missing on disk → null",
                    DiscPlaylist.ResolveDiscPath(m3u, 3) == null);
                Check("an index past the end → null",
                    DiscPlaylist.ResolveDiscPath(m3u, 4) == null && DiscPlaylist.ResolveDiscPath(m3u, uint.MaxValue) == null);
                Check("a launch that is not a playlist → null",
                    DiscPlaylist.ResolveDiscPath(disc1, 0) == null);
                Check("a playlist that does not exist → null",
                    DiscPlaylist.ResolveDiscPath(missingM3u, 0) == null);
                Check("no launch path → null",
                    DiscPlaylist.ResolveDiscPath(null, 0) == null && DiscPlaylist.ResolveDiscPath("", 0) == null);
                Check("comment and blank lines do not count as discs",
                    DiscPlaylist.ResolveEntry(root, new[] { "#x", "", "Game (Disc 1).cue" }, 0) != null
                    && DiscPlaylist.ResolveEntry(root, new[] { "#x", "", "Game (Disc 1).cue" }, 1) == null);
            }
            finally
            {
                foreach (string f in new[] { m3u, disc1, disc2, disc3 })
                    try { File.Delete(f); } catch { /* best effort */ }
                try { Directory.Delete(nested); } catch { /* best effort */ }
                try { Directory.Delete(root); } catch { /* best effort */ }
            }

            line($"{pass} passed, {fail} failed");
            return fail;
        }

        private static bool HardcoreLaunchPolicy_PathEquals(string? actual, string expected)
            => actual != null && string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }
}
