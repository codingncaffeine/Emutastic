using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for Philips CD-i (SAME CDi / MAME).
    ///
    /// SAME CDi sources confirm:
    ///   - retro_set_controller_port_device is a no-op stub — ignore it.
    ///   - Thumbpad cursor is driven via RETRO_DEVICE_MOUSE queries on port 0.
    ///   - The mouse path is gated by the "same_cdi_mouse_enable" core option
    ///     which defaults to "disabled" — we must override it to "enabled".
    ///   - JOYPAD is also polled on port 0 in parallel (buttons + d-pad still work).
    /// </summary>
    public class CdiHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "CDi";
        public override bool UsesAnalogStick => true;

        public override Dictionary<string, string> GetDefaultCoreOptions()
            => new()
            {
                // Enables the MAME mouse-input path that drives the CD-i thumbpad cursor.
                // Without this the core ignores all RETRO_DEVICE_MOUSE queries.
                ["same_cdi_mouse_enable"] = "enabled",
            };
    }
}
