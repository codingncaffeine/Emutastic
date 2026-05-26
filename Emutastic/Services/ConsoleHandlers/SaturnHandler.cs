using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    public class SaturnHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "Saturn";
        public override bool PromoteAnalogStickToDpad => true;

        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            // Kronos (HW renderer) options
            ("kronos_resolution_mode", "Internal Resolution"),
            ("kronos_meshmode", "Mesh Transparency"),
            ("kronos_bandingmode", "Color Banding Fix"),
            // Beetle Saturn (SW renderer) options — runtime validation
            // strips whichever set doesn't match the loaded core
            ("beetle_saturn_mesh_transparency", "Mesh Transparency"),
        };
    }
}
