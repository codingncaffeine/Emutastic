namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Fallback handler for consoles with no special requirements.
    /// Uses all defaults from ConsoleHandlerBase.
    /// </summary>
    public class GenericHandler : ConsoleHandlerBase
    {
        private readonly string _consoleName;

        public GenericHandler(string consoleName)
        {
            _consoleName = consoleName;
        }

        public override string ConsoleName => _consoleName;
    }
}
