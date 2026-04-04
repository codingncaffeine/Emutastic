using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for TurboGrafx-16 / PC Engine and TG-CD / PC Engine CD via Mednafen PCE.
    /// Forces 4:3 aspect ratio regardless of what the core reports.
    /// </summary>
    public class Tg16Handler : ConsoleHandlerBase
    {
        private readonly string _consoleName;

        public Tg16Handler(string consoleName)
        {
            _consoleName = consoleName;
        }

        public override string ConsoleName => _consoleName;

        public override Dictionary<string, string> GetDefaultCoreOptions() => new()
        {
            ["pce_hoverscan"]        = "320",
            ["pce_initial_scanline"] = "0",
            ["pce_last_scanline"]    = "239",
            ["pce_nospritelimit"]    = "enabled",
            ["pce_ocmultiplier"]     = "1",
            ["pce_forcesgx"]         = "disabled",
            ["pce_cdimagecache"]     = "disabled",
            ["pce_palette"]          = "RGB",
            ["pce_aspect_ratio"]     = "auto",
        };

        public override double GetDisplayAspectRatio(uint baseWidth, uint baseHeight, float coreAspectRatio)
            => 4.0 / 3.0;
    }
}
