using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    public class GameComHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "GameCom";

        // Tigerbyte's effective system clock is calibrated against real-hardware
        // boot recordings (nominal 4.9152 MHz, calibrated ~6.30). Surfacing it in
        // the cog as a live slider lets game speed be dialed in by ear while
        // playing; the core re-reads the option every frame.
        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            ("tigerbyte_clock", "CPU Clock (MHz)"),
        };

        public override (double Min, double Max, double Step, string Format, string Suffix)? GetNumericOption(string key)
            => key == "tigerbyte_clock" ? (4.90, 6.60, 0.01, "0.00", " MHz") : null;
    }
}
