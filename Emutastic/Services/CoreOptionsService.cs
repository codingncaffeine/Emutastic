using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// Schema saved once per core (after first game launch) so the Preferences
    /// UI can display options without needing a game to be running.
    /// </summary>
    public class CoreOptionsSchema
    {
        public string DisplayName { get; set; } = "";
        public string ConsoleName { get; set; } = "";
        public List<CoreOptionEntry> Options { get; set; } = new();
    }

    /// <summary>
    /// Persists per-core option schemas and user-chosen values to
    /// %AppData%\Emutastic\CoreOptions\.
    /// </summary>
    public class CoreOptionsService
    {
        private readonly string _dir;
        private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

        public CoreOptionsService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _dir = Path.Combine(appData, "Emutastic", "CoreOptions");
            Directory.CreateDirectory(_dir);
        }

        // ── Schema ────────────────────────────────────────────────────────────────

        public void SaveSchema(string coreName, CoreOptionsSchema schema)
        {
            try
            {
                File.WriteAllText(Path.Combine(_dir, $"{coreName}.schema.json"),
                    JsonSerializer.Serialize(schema, _json));
            }
            catch { /* non-fatal */ }
        }

        public CoreOptionsSchema? LoadSchema(string coreName)
        {
            string path = Path.Combine(_dir, $"{coreName}.schema.json");
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<CoreOptionsSchema>(File.ReadAllText(path)); }
            catch { return null; }
        }

        /// <summary>Returns (coreName, displayName, consoleName) tuples for every core that has a saved schema.</summary>
        public List<(string CoreName, string DisplayName, string ConsoleName)> GetCoresWithSchema()
        {
            try
            {
                return Directory.EnumerateFiles(_dir, "*.schema.json")
                    .Select(f =>
                    {
                        string cn = Path.GetFileNameWithoutExtension(
                            Path.GetFileNameWithoutExtension(f)); // strip .schema then .json
                        var schema = LoadSchema(cn);
                        string dn = schema?.DisplayName is { Length: > 0 } d ? d : cn;
                        string console = schema?.ConsoleName ?? "";
                        return (cn, dn, console);
                    })
                    .OrderBy(x => x.Item2)
                    .ToList();
            }
            catch { return new(); }
        }

        // ── Values ────────────────────────────────────────────────────────────────

        public Dictionary<string, string> LoadValues(string coreName)
        {
            string path = Path.Combine(_dir, $"{coreName}.values.json");
            if (!File.Exists(path)) return new();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path)) ?? new();
            }
            catch { return new(); }
        }

        public void SaveValues(string coreName, Dictionary<string, string> values)
        {
            try
            {
                File.WriteAllText(Path.Combine(_dir, $"{coreName}.values.json"),
                    JsonSerializer.Serialize(values, _json));
            }
            catch { /* non-fatal */ }
        }

        public void DeleteValues(string coreName)
        {
            try
            {
                string path = Path.Combine(_dir, $"{coreName}.values.json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }
}
