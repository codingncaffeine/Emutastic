namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Arcade routing: covers FBNeo + MAME 2003-Plus games. The original
    /// hardware used a digital joystick (4-way or 8-way per board), so the
    /// libretro JOYPAD device is the right interface. The one extra wrinkle
    /// vs. the generic fallback: modern controllers' left analog stick is
    /// the natural way to drive these games on a couch, so we promote stick
    /// directions to JOYPAD_UP/DOWN/LEFT/RIGHT whenever the d-pad isn't held.
    /// Diagonals survive because the X and Y axis deadzone checks fire
    /// independently in ControllerManager — pushing the stick NE produces
    /// both ANALOG_LEFT_UP and ANALOG_LEFT_RIGHT, which become both
    /// JOYPAD_UP and JOYPAD_RIGHT in the same input poll.
    /// </summary>
    public class ArcadeHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "Arcade";
        public override bool PromoteAnalogStickToDpad => true;
    }
}
