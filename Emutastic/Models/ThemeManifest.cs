using System.Text.Json.Serialization;

namespace Emutastic.Models
{
    /// <summary>
    /// Deserialized from theme.json inside a .emutheme package.
    /// </summary>
    public class ThemeManifest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("author")]
        public string Author { get; set; } = "";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("apiVersion")]
        public int ApiVersion { get; set; } = 1;

        [JsonPropertyName("previewImage")]
        public string? PreviewImage { get; set; }
    }
}
