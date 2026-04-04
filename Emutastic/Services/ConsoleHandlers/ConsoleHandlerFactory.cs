namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Creates the appropriate <see cref="IConsoleHandler"/> for a given console name.
    /// Add a new case here when adding a console that needs custom behaviour;
    /// the rest of the emulator stays unchanged.
    /// </summary>
    public static class ConsoleHandlerFactory
    {
        public static IConsoleHandler Create(string console) => console switch
        {
            "NES"  or "FDS"  => new NesHandler(console),
            "SNES"           => new SnesHandler(),
            "N64"            => new N64Handler(),
            "GameCube"       => new GameCubeHandler(),
            "TG16" or "TGCD" => new Tg16Handler(console),
            _                => new GenericHandler(console),
        };
    }
}
