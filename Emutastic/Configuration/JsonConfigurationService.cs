using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Emutastic.Configuration
{
    /// <summary>
    /// Strongly-typed root object that maps 1:1 to what we write to disk.
    /// Using concrete types avoids System.Text.Json's object→{} serialization bug.
    /// </summary>
    internal class ConfigData
    {
        public string Version { get; set; } = "1.0";
        public DateTime LastSaved { get; set; } = DateTime.UtcNow;
        public UserPreferences UserPreferences { get; set; } = new();
        public DisplayConfiguration DisplayConfiguration { get; set; } = new();
        public EmulatorConfiguration EmulatorConfiguration { get; set; } = new();
        public CorePreferences CorePreferences { get; set; } = new();
        public LibraryConfiguration LibraryConfiguration { get; set; } = new();
        public ThemeConfiguration ThemeConfiguration { get; set; } = new();
        // Per-console input configs keyed by ConfigKey (e.g. "SNES_P1")
        public SnapConfiguration SnapConfiguration { get; set; } = new();
        public Dictionary<string, InputConfiguration> InputConfigurations { get; set; } = new();
        // Generic string→JsonElement store for arbitrary SetValue<T> callers
        public Dictionary<string, JsonElement> Extra { get; set; } = new();
    }

    public class JsonConfigurationService : IConfigurationService
    {
        private readonly string _configPath;
        private readonly ILogger<JsonConfigurationService>? _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        private ConfigData _data = new();

        public JsonConfigurationService(ILogger<JsonConfigurationService>? logger = null)
        {
            _logger = logger;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appData, "Emutastic");
            Directory.CreateDirectory(appFolder);
            _configPath = Path.Combine(appFolder, "config.json");

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
        }

        // ── Persistence ───────────────────────────────────────────────────────

        public async Task SaveAsync()
        {
            try
            {
                _data.LastSaved = DateTime.UtcNow;
                string json = JsonSerializer.Serialize(_data, _jsonOptions);
                await File.WriteAllTextAsync(_configPath, json);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error saving configuration");
                throw;
            }
        }

        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    _data = new ConfigData();
                    await SaveAsync();
                    return;
                }

                string json = await File.ReadAllTextAsync(_configPath);
                var loaded = JsonSerializer.Deserialize<ConfigData>(json, _jsonOptions);
                _data = loaded ?? new ConfigData();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error loading configuration — using defaults");
                _data = new ConfigData();
            }
        }

        // ── Typed accessors ───────────────────────────────────────────────────

        public InputConfiguration GetInputConfiguration(string consoleName)
        {
            if (_data.InputConfigurations.TryGetValue(consoleName, out var cfg))
                return cfg;
            return new InputConfiguration { ConsoleName = consoleName };
        }

        public void SetInputConfiguration(string consoleName, InputConfiguration config)
        {
            config.ConsoleName = consoleName;
            config.LastModified = DateTime.UtcNow;
            _data.InputConfigurations[consoleName] = config;
        }

        public DisplayConfiguration GetDisplayConfiguration() => _data.DisplayConfiguration;
        public void SetDisplayConfiguration(DisplayConfiguration config)
        {
            config.LastModified = DateTime.UtcNow;
            _data.DisplayConfiguration = config;
        }

        public EmulatorConfiguration GetEmulatorConfiguration() => _data.EmulatorConfiguration;
        public void SetEmulatorConfiguration(EmulatorConfiguration config)
        {
            config.LastModified = DateTime.UtcNow;
            _data.EmulatorConfiguration = config;
        }

        public UserPreferences GetUserPreferences() => _data.UserPreferences;
        public void SetUserPreferences(UserPreferences preferences)
        {
            preferences.LastModified = DateTime.UtcNow;
            _data.UserPreferences = preferences;
        }

        public CorePreferences GetCorePreferences() => _data.CorePreferences;
        public void SetCorePreferences(CorePreferences preferences)
        {
            preferences.LastModified = DateTime.UtcNow;
            _data.CorePreferences = preferences;
        }

        public LibraryConfiguration GetLibraryConfiguration() => _data.LibraryConfiguration;
        public void SetLibraryConfiguration(LibraryConfiguration config)
        {
            config.LastModified = DateTime.UtcNow;
            _data.LibraryConfiguration = config;
        }

        public ThemeConfiguration GetThemeConfiguration() => _data.ThemeConfiguration;
        public void SetThemeConfiguration(ThemeConfiguration config)
        {
            config.LastModified = DateTime.UtcNow;
            _data.ThemeConfiguration = config;
        }

        public SnapConfiguration GetSnapConfiguration() => _data.SnapConfiguration;
        public void SetSnapConfiguration(SnapConfiguration config)
        {
            config.LastModified = DateTime.UtcNow;
            _data.SnapConfiguration = config;
        }

        // ── Generic key/value (for arbitrary callers) ─────────────────────────

        public T GetValue<T>(string key, T? defaultValue = default)
        {
            try
            {
                if (_data.Extra.TryGetValue(key, out var element))
                {
                    var result = JsonSerializer.Deserialize<T>(element.GetRawText(), _jsonOptions);
                    return result ?? defaultValue;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Error getting value for key '{key}'");
            }
            return defaultValue;
        }

        public void SetValue<T>(string key, T value)
        {
            try
            {
                string json = JsonSerializer.Serialize(value, _jsonOptions);
                _data.Extra[key] = JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Error setting value for key '{key}'");
            }
        }

        public bool HasKey(string key) => _data.Extra.ContainsKey(key);
        public void RemoveKey(string key) => _data.Extra.Remove(key);
        public void Clear()
        {
            _data.Extra.Clear();
            _data.InputConfigurations.Clear();
            _data.UserPreferences = new();
            _data.DisplayConfiguration = new();
            _data.EmulatorConfiguration = new();
            _data.CorePreferences = new();
            _data.LibraryConfiguration = new();
        }
    }
}
