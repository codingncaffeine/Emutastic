using System;
using System.Threading.Tasks;
using Emutastic.Configuration;
using Microsoft.Extensions.Logging;

namespace Emutastic
{
    public static class TestConfiguration
    {
        public static async Task MainTest()
        {
            Console.WriteLine("Testing Configuration System...");
            
            try
            {
                // Test basic configuration service
                using var loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.AddConsole().SetMinimumLevel(LogLevel.Information);
                });
                
                var configService = new JsonConfigurationService(loggerFactory.CreateLogger<JsonConfigurationService>());
                await configService.LoadAsync();
                
                // Test basic operations
                configService.SetValue("test.value", "Hello Configuration!");
                var testValue = configService.GetValue<string>("test.value");
                Console.WriteLine($"Test value: {testValue}");
                
                // Test user preferences
                var prefs = configService.GetUserPreferences();
                prefs.Theme = "Dark";
                prefs.DefaultLibraryPath = @"C:\ROMs";
                configService.SetUserPreferences(prefs);
                
                // Test display configuration
                var display = configService.GetDisplayConfiguration();
                display.FullscreenByDefault = false;
                display.FilterType = "Linear";
                configService.SetDisplayConfiguration(display);
                
                // Test input configuration
                var input = configService.GetInputConfiguration("NES");
                input.ControllerDeadzone = 15;
                input.EnableRumble = true;
                configService.SetInputConfiguration("NES", input);
                
                // Save and reload
                await configService.SaveAsync();
                await configService.LoadAsync();
                
                // Verify
                var loadedPrefs = configService.GetUserPreferences();
                var loadedDisplay = configService.GetDisplayConfiguration();
                var loadedInput = configService.GetInputConfiguration("NES");
                
                Console.WriteLine($"Loaded theme: {loadedPrefs.Theme}");
                Console.WriteLine($"Loaded library path: {loadedPrefs.DefaultLibraryPath}");
                Console.WriteLine($"Loaded fullscreen: {loadedDisplay.FullscreenByDefault}");
                Console.WriteLine($"Loaded filter: {loadedDisplay.FilterType}");
                Console.WriteLine($"Loaded deadzone: {loadedInput.ControllerDeadzone}");
                Console.WriteLine($"Loaded rumble: {loadedInput.EnableRumble}");
                
                // Test controller definitions
                Console.WriteLine("\nTesting Controller Definitions:");
                var consoles = ControllerDefinitions.GetSupportedConsoles();
                Console.WriteLine($"Supported consoles: {consoles.Count}");
                
                foreach (var console in consoles.Take(5))
                {
                    var def = ControllerDefinitions.GetControllerDefinition(console.Tag);
                    Console.WriteLine($"  {console.Tag}: {def?.Name} ({def?.Buttons.Count} buttons)");
                }
                
                Console.WriteLine("\nConfiguration system test completed successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Test failed: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }
    }
}
