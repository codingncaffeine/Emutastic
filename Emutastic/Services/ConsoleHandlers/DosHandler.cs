namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// DOS support has been removed from the app. This stub remains only so that
    /// the various `_consoleHandler is DosHandler` type checks scattered through
    /// EmulatorWindow continue to compile — those checks now resolve to false at
    /// runtime because <see cref="ConsoleHandlerFactory"/> no longer dispatches
    /// "DOS" to anything. The dependent code blocks become dead, but the
    /// strip-them-all refactor is intentionally deferred (high-touch, low-impact).
    /// </summary>
    public class DosHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "DOS";

        public bool UseVoodooOpenGL { get; set; }
    }
}
