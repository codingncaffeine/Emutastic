using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Fallback handler for consoles with no special requirements.
    /// Uses all defaults from ConsoleHandlerBase.
    /// </summary>
    public class GenericHandler : ConsoleHandlerBase
    {
        // Digital-only consoles that should accept the left analog stick as
        // a movement input. Same idea as ArcadeHandler / CdiHandler — the
        // original hardware had no analog stick, but modern controllers do,
        // and folks who don't like D-pads should still be able to play.
        // Listed explicitly (rather than blanket-true) so we don't promote
        // analog on consoles that route through GenericHandler but actually
        // have native analog stick support (e.g. 3DS, PSP, PS2 — those use
        // RETRO_DEVICE_ANALOG directly and don't need a JOYPAD fallback) and
        // so the Vectrex / 3DO carve-outs stay digital-only as the user
        // explicitly asked.
        private static readonly HashSet<string> _promoteAnalog = new()
        {
            // Sega
            "Genesis", "SegaCD", "Sega32X", "SMS", "GameGear", "SG1000", "Saturn",
            // Nintendo handhelds
            "GB", "GBC", "GBA", "NDS", "VirtualBoy",
            // SNK
            "NeoGeo", "NeoCD", "NGP", "NGPC",
            // Atari / 80s home consoles
            "Atari2600", "Atari7800", "Jaguar", "ColecoVision",
        };

        private readonly string _consoleName;

        public GenericHandler(string consoleName)
        {
            _consoleName = consoleName;
        }

        public override string ConsoleName => _consoleName;
        public override bool PromoteAnalogStickToDpad => _promoteAnalog.Contains(_consoleName);
    }
}
