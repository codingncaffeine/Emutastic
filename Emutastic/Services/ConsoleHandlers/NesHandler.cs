namespace Emutastic.Services.ConsoleHandlers
{
    public class NesHandler : ConsoleHandlerBase
    {
        private readonly string _consoleName;

        public NesHandler(string consoleName)
        {
            _consoleName = consoleName;
        }

        public override string ConsoleName => _consoleName;
    }
}
