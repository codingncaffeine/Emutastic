namespace Emutastic.Models
{
    public class CoreOptionEntry
    {
        public string   Key          { get; set; } = "";
        public string   Description  { get; set; } = "";
        public string[] ValidValues  { get; set; } = [];
        public string   DefaultValue { get; set; } = "";
    }
}
