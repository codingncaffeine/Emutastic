using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// Per-game cheat list persistence.
    /// Stored as JSON at [DataRoot]/Cheats/{Console}/{GameId}.json so cheats
    /// follow a game even if the rom file is renamed; deleting a game from
    /// the library does NOT delete its cheats (matches how artwork behaves).
    /// </summary>
    public static class CheatService
    {
        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private class CheatFile { public List<Cheat> Cheats { get; set; } = new(); }

        private static string PathFor(Game game)
        {
            string console = string.IsNullOrEmpty(game.Console) ? "Unknown" : game.Console;
            string folder = AppPaths.GetFolder("Cheats", console);
            return System.IO.Path.Combine(folder, $"{game.Id}.json");
        }

        public static List<Cheat> Load(Game game)
        {
            try
            {
                string path = PathFor(game);
                if (!File.Exists(path)) return new List<Cheat>();
                var data = JsonSerializer.Deserialize<CheatFile>(File.ReadAllText(path), _opts);
                return data?.Cheats ?? new List<Cheat>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CheatService.Load failed: {ex.Message}");
                return new List<Cheat>();
            }
        }

        public static void Save(Game game, List<Cheat> cheats)
        {
            try
            {
                string path = PathFor(game);
                File.WriteAllText(path, JsonSerializer.Serialize(new CheatFile { Cheats = cheats }, _opts));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CheatService.Save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// A pre-parsed Action Replay / PAR cheat: write Value (1 or 2 bytes,
        /// big-endian for word writes on Genesis-like systems) into the system
        /// RAM region at offset Address &amp; (ramSize - 1) after every retro_run.
        /// Used for the frontend-handled cheat path that bypasses cores like
        /// genesis_plus_gx where retro_cheat_set is unreliable for AR codes —
        /// matches what RetroArch does for "RetroArch handled" cheats.
        /// </summary>
        public readonly record struct ParsedAr(uint Address, uint Value, byte ByteCount);

        /// <summary>True if a code looks like Action Replay / PAR (XXXXXX:XXXX)
        /// rather than Game Genie. Multi-line codes joined with `+` are also AR.</summary>
        public static bool IsActionReplayCode(string code) =>
            !string.IsNullOrEmpty(code) && code.IndexOf(':') >= 0;

        /// <summary>
        /// Splits an enabled cheat list into the two handling paths:
        /// AR codes get parsed for direct RAM writes; everything else
        /// (Game Genie, GameShark, raw, etc.) gets passed through to
        /// retro_cheat_set for the core to handle.
        /// AR segments targeting addresses outside the system's RAM range
        /// (i.e. ROM-patching cheats) are silently skipped — writing them
        /// into system RAM via the offset mask would corrupt unrelated state.
        /// </summary>
        public static (List<Cheat> coreHandled, List<ParsedAr> frontendAr) Sort(IList<Cheat> cheats, string console = "")
        {
            var core = new List<Cheat>();
            var ar   = new List<ParsedAr>();
            foreach (var c in cheats)
            {
                if (!c.Enabled || string.IsNullOrWhiteSpace(c.Code)) continue;
                if (IsActionReplayCode(c.Code))
                {
                    foreach (var seg in c.Code.Split('+'))
                    {
                        if (TryParseArSegment(seg.Trim(), out var parsed)
                            && IsRamAddress(parsed.Address, console))
                        {
                            ar.Add(parsed);
                        }
                    }
                }
                else
                {
                    core.Add(c);
                }
            }
            return (core, ar);
        }

        /// <summary>
        /// True when the given AR code address targets system work RAM
        /// (so a frontend write into system_ram + offset is safe). False
        /// for ROM-patching cheats whose addresses fall in cartridge space —
        /// those need actual ROM-byte patching and can't be applied via
        /// the frontend RAM-write path.
        /// </summary>
        private static bool IsRamAddress(uint address, string console)
        {
            // Genesis-family (M68K): ROM 0x000000-0x3FFFFF, Work RAM 0xFF0000-0xFFFFFF.
            // ROM-patch cheats (e.g. Sonic "invincibility" at 0x0039F0) need to modify
            // cartridge bytes — silently skipping them beats corrupting work RAM.
            if (console == "Genesis" || console == "SegaCD" || console == "Sega32X")
                return address >= 0xFF0000;

            // Other systems with `:` AR cheats are uncommon enough that we don't have
            // good data on their RAM/ROM split. Default to "treat as RAM" until a
            // real bug shows otherwise.
            return true;
        }

        /// <summary>
        /// Parses one AR segment of the form XXXXXX:XX or XXXXXX:XXXX.
        /// Returns false on malformed input rather than throwing — bad codes
        /// just get silently dropped.
        /// </summary>
        private static bool TryParseArSegment(string segment, out ParsedAr parsed)
        {
            parsed = default;
            int colon = segment.IndexOf(':');
            if (colon <= 0 || colon >= segment.Length - 1) return false;
            string addrStr = segment[..colon].Trim();
            string valStr  = segment[(colon + 1)..].Trim();
            if (!uint.TryParse(addrStr, System.Globalization.NumberStyles.HexNumber, null, out uint addr)) return false;
            if (!uint.TryParse(valStr,  System.Globalization.NumberStyles.HexNumber, null, out uint val))  return false;
            byte byteCount = (byte)(valStr.Length <= 2 ? 1 : 2);
            parsed = new ParsedAr(addr, val, byteCount);
            return true;
        }

        /// <summary>
        /// Clears the core's active cheats and re-applies every enabled
        /// core-handled entry via retro_cheat_set. AR codes are NOT sent
        /// here — they're handled frontend-side via direct RAM writes after
        /// retro_run; see <see cref="Sort"/>. Safe to call after
        /// retro_load_game and after state loads.
        /// </summary>
        public static void Apply(LibretroCore core, IList<Cheat> coreHandledCheats)
        {
            if (core == null) return;
            try
            {
                core.CheatReset();
                // Only count enabled cheats — most cores expect dense indexing
                // starting at 0; a gap can confuse cores that track cheat_count
                // from the highest index seen (mednafen, pcsx_rearmed).
                uint idx = 0;
                foreach (var c in coreHandledCheats)
                {
                    if (!c.Enabled || string.IsNullOrWhiteSpace(c.Code)) continue;
                    core.CheatSet(idx, true, c.Code);
                    idx++;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CheatService.Apply failed: {ex.Message}");
            }
        }
    }
}
