using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    public class SnesHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "SNES";
        public override bool PromoteAnalogStickToDpad => true;

        // Quick visual options surfaced in the in-game cog (Visuals panel) when
        // the running core announces them — the panel is schema-gated, so the
        // bsnes_* rows appear only on bsnes-hd beta sessions; snes9x/bsnes
        // sessions never announce these keys and the rows stay hidden.
        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            ("bsnes_mode7_scale",        "HD Mode 7 Scale"),
            ("bsnes_mode7_supersample",  "Supersampling"),
            ("bsnes_mode7_perspective",  "Perspective Correction"),
            ("bsnes_mode7_widescreen",   "Widescreen"),
            ("bsnes_mode7_wsMode",       "Widescreen Scenes"),
            ("bsnes_mode7_wsBgCol",      "Widescreen Edge Fill"),
            ("bsnes_ppu_no_sprite_limit","Remove Sprite Limit"),
        };

        public override Dictionary<string, string> GetDefaultCoreOptions()
        {
            return new Dictionary<string, string>
            {
                // SNES-specific performance options based on Gemini's recommendations
                { "snes9x_overclock_cycles", "disabled" },          // Disabled to reduce host CPU load
                { "snes9x_reduce_sprite_flicker", "disabled" },     // Default behavior
                { "snes9x_hires_blend", "disabled" },                // Default blending
                { "snes9x_audio_interpolation", "linear" },         // Linear for better performance vs gaussian
                { "snes9x_overscan", "enabled" },                    // Crop overscan for clean output
                { "snes9x_up_down_allowed", "disabled" },            // Prevent glitches
                { "snes9x_blargg", "disabled" },                     // No NTSC filter

                // bsnes-hd beta: its whole point is HD Mode 7, but the core ships
                // with it DISABLED — seed a visible 2x so the core does something
                // out of the box at modest cost. Widescreen stays off (its default;
                // game-dependent HUD behavior — user opt-in via the cog/Core
                // Options). Keys are ignored by snes9x/bsnes sessions.
                { "bsnes_mode7_scale", "2x" }
            };
        }
    }
}
