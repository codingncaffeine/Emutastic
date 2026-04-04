using System;

namespace Emutastic.Models
{
    public class SaveState
    {
        public int      Id             { get; set; }
        public int      GameId         { get; set; }
        public string   GameTitle      { get; set; } = "";
        public string   ConsoleName    { get; set; } = "";
        public string   Name           { get; set; } = "";
        public string   StatePath      { get; set; } = "";
        public string   ScreenshotPath { get; set; } = "";
        public string   CoreName       { get; set; } = "";
        public string   RomHash        { get; set; } = "";
        public DateTime CreatedAt      { get; set; } = DateTime.Now;

        public string RelativeTime
        {
            get
            {
                var diff = DateTime.Now - CreatedAt;
                if (diff.TotalSeconds < 60)  return "just now";
                if (diff.TotalMinutes < 60)  return $"{(int)diff.TotalMinutes}m ago";
                if (diff.TotalHours   < 24)  return $"{(int)diff.TotalHours}h ago";
                if (diff.TotalDays    < 7)   return $"{(int)diff.TotalDays}d ago";
                return CreatedAt.ToString("MMM d, yyyy");
            }
        }
    }
}
