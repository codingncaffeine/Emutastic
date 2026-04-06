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
    }
}
