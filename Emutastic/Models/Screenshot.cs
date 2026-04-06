using System;

namespace Emutastic.Models
{
    public class Screenshot
    {
        public string FilePath  { get; set; } = "";
        public string GameTitle { get; set; } = "";
        public string Console   { get; set; } = "";
        public DateTime TakenAt { get; set; }

        public string TakenAtDisplay => TakenAt.ToString("MMM d, yyyy  h:mm tt");
    }
}
