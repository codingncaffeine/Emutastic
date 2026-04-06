using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Emutastic.Configuration
{
    public interface IConfigurationService
    {
        T GetValue<T>(string key, T defaultValue = default);
        void SetValue<T>(string key, T value);
        Task SaveAsync();
        Task LoadAsync();
        bool HasKey(string key);
        void RemoveKey(string key);
        void Clear();
        
        // Typed configuration accessors
        InputConfiguration GetInputConfiguration(string consoleName);
        void SetInputConfiguration(string consoleName, InputConfiguration config);
        
        DisplayConfiguration GetDisplayConfiguration();
        void SetDisplayConfiguration(DisplayConfiguration config);
        
        EmulatorConfiguration GetEmulatorConfiguration();
        void SetEmulatorConfiguration(EmulatorConfiguration config);
        
        UserPreferences GetUserPreferences();
        void SetUserPreferences(UserPreferences preferences);
        
        CorePreferences GetCorePreferences();
        void SetCorePreferences(CorePreferences preferences);

        LibraryConfiguration GetLibraryConfiguration();
        void SetLibraryConfiguration(LibraryConfiguration config);

        ThemeConfiguration GetThemeConfiguration();
        void SetThemeConfiguration(ThemeConfiguration config);

        SnapConfiguration GetSnapConfiguration();
        void SetSnapConfiguration(SnapConfiguration config);
    }
}
