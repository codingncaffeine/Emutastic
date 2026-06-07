using System.Collections.Generic;
using System.Linq;

namespace Emutastic.Services.ConsoleHandlers
{
    public class NdsHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "NDS";

        // DeSmuME renders on the CPU (SoftRasterizer), and both of these
        // options work on that path. The "OpenGL:" prefixed core options
        // (multisampling, texture smoothing, shadow polygons) require the GL
        // rasterizer we don't enable — deliberately not exposed.
        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            ("desmume_internal_resolution", "Internal Resolution"),
            ("desmume_gfx_texture_scaling", "Texture Scaling (xBrz)"),
        };

        // SoftRasterizer cost scales with pixel count and the core offers up
        // to 10x (2560x1920) — slideshow territory for CPU rendering. Cap the
        // picker at 4x; 2x-3x is the sweet spot for big displays.
        private static readonly HashSet<string> AllowedResolutions = new()
        {
            "256x192", "512x384", "768x576", "1024x768"
        };

        public override string[] FilterCoreOptionValues(string key, string[] values)
        {
            if (key == "desmume_internal_resolution")
                return values.Where(v => AllowedResolutions.Contains(v.Trim())).ToArray();
            return values;
        }
    }
}
