using System.Collections.ObjectModel;

namespace Emutastic.Models
{
    public class ConsoleGroup
    {
        public string ConsoleName { get; init; } = "";
        public ObservableCollection<Game> Games { get; init; } = new();
    }
}
