using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    public class ThreeDsHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "3DS";
        public override bool UsesAnalogStick => true;

        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            ("citra_resolution_factor", "Internal Resolution"),
            ("citra_texture_filter", "Texture Filter"),
        };
    }
}
