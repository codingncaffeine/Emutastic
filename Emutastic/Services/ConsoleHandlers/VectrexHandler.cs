using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    public class VectrexHandler : ConsoleHandlerBase
    {
        public override string ConsoleName => "Vectrex";

        public override Dictionary<string, string> GetDefaultCoreOptions() => new()
        {
            { "vecx_res_multi", "3" },
        };

        // The vecx HW renderer passes its actual render dimensions (e.g. 869×1080)
        // in the video callback. Reading the full square FBO captures extra black columns
        // on the right, making the game appear shifted left. Use the callback dimensions.
        public override bool UseFullFboReadback => false;
    }
}
