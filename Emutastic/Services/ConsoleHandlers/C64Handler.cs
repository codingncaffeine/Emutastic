namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for the Commodore 64 (Kernal64, kernal64_libretro). A computer: the window sends its keys to the
    /// machine (EmulatorWindow.IsComputer). Two control ports, each a RetroPad joystick; the left stick moves the
    /// joystick too and turns paddles, the right stick the second paddle. The picture's aspect is the core's
    /// (it follows the chip model and the border option).
    /// </summary>
    public class C64Handler : ConsoleHandlerBase
    {
        private const uint RETRO_DEVICE_JOYPAD = 1;

        public override string ConsoleName => "C64";

        public override bool UsesAnalogStick => true;

        public override void ConfigureControllerPorts(LibretroCore core)
        {
            for (uint port = 0; port < 2; port++)
                core.SetControllerPortDevice(port, RETRO_DEVICE_JOYPAD);
        }
    }
}
