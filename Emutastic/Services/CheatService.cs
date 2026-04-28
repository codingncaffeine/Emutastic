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
        /// Clears the core's active cheats and re-applies every enabled entry.
        /// Safe to call after retro_load_game and after a state load — both
        /// places need a re-apply to survive core-internal cheat-table resets.
        /// </summary>
        public static void Apply(LibretroCore core, IList<Cheat> cheats)
        {
            if (core == null) return;
            try
            {
                core.CheatReset();
                // Only count enabled cheats — most cores expect dense indexing starting at 0.
                // A gap (e.g. enabling cheat #2 with #1 disabled) can confuse cores that track
                // cheat_count from the highest index seen (mednafen, pcsx_rearmed).
                uint idx = 0;
                foreach (var c in cheats)
                {
                    if (c.Enabled && !string.IsNullOrWhiteSpace(c.Code))
                    {
                        core.CheatSet(idx, true, c.Code);
                        idx++;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CheatService.Apply failed: {ex.Message}");
            }
        }
    }
}
