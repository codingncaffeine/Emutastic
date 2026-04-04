using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    public class SnesHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "SNES";

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
                { "snes9x_blargg", "disabled" }                      // No NTSC filter
            };
        }
    }
}
