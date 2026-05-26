using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    public class PspHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "PSP";
        public override bool UsesAnalogStick => true;

        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            ("ppsspp_internal_resolution", "Internal Resolution"),
            ("ppsspp_texture_filtering", "Texture Filter"),
            ("ppsspp_mulitsample_level", "Anti-Aliasing"),
            ("ppsspp_texture_scaling_level", "Texture Upscaling"),
        };
    }
}
