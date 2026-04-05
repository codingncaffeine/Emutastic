using System.Collections.ObjectModel;

namespace Emutastic.Models
{
    public class ConsoleGroup
    {
        public string ConsoleName { get; init; } = "";
        public ObservableCollection<Game> Games { get; init; } = new();
        /// <summary>Full count for this console — may exceed Games.Count when Games is a preview slice.</summary>
        public int TotalCount { get; init; }
        public bool HasMore => TotalCount > Games.Count;
        public string MoreText => HasMore ? $"+{TotalCount - Games.Count} more" : "";
    }
}
