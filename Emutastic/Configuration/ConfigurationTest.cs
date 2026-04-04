using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Emutastic.Configuration
{
    public static class ConfigurationTests
    {
        public static async Task RunBasicTestAsync()
        {
            Console.WriteLine("Starting Configuration System Test...");
            
            // Initialize logger
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole().SetMinimumLevel(LogLevel.Debug);
            });
            var logger = loggerFactory.CreateLogger(typeof(ConfigurationTests));

            try
            {
                // Test configuration service
                var configService = new JsonConfigurationService(loggerFactory.CreateLogger<JsonConfigurationService>());
                
                // Load configuration
                await configService.LoadAsync();
                logger.LogInformation("Configuration loaded successfully");

                // Test basic get/set operations
                configService.SetValue("test.string", "Hello World");
                configService.SetValue("test.number", 42);
                configService.SetValue("test.boolean", true);

                var stringValue = configService.GetValue<string>("test.string");
                var numberValue = configService.GetValue<int>("test.number");
                var boolValue = configService.GetValue<bool>("test.boolean");

                Console.WriteLine($"String: {stringValue}");
                Console.WriteLine($"Number: {numberValue}");
                Console.WriteLine($"Boolean: {boolValue}");

                // Test typed configuration
                var userPrefs = configService.GetUserPreferences();
                userPrefs.DefaultLibraryPath = @"C:\ROMs";
                userPrefs.Theme = "Dark";
                userPrefs.EnableDebugLogging = true;
                configService.SetUserPreferences(userPrefs);

                var displayConfig = configService.GetDisplayConfiguration();
                displayConfig.FullscreenByDefault = true;
                displayConfig.FilterType = "CRT";
                configService.SetDisplayConfiguration(displayConfig);

                var emulatorConfig = configService.GetEmulatorConfiguration();
                emulatorConfig.AutoSaveInterval = 600;
                emulatorConfig.FastForwardSpeed = 5;
                configService.SetEmulatorConfiguration(emulatorConfig);

                // Test input configuration for NES
                var inputConfig = configService.GetInputConfiguration("NES");
                inputConfig.ValidateMappings();
                inputConfig.ControllerDeadzone = 20;
                inputConfig.EnableRumble = true;
                configService.SetInputConfiguration("NES", inputConfig);

                // Save configuration
                await configService.SaveAsync();
                logger.LogInformation("Configuration saved successfully");

                // Test loading again
                await configService.LoadAsync();
                
                // Verify values persisted
                var loadedPrefs = configService.GetUserPreferences();
                var loadedDisplay = configService.GetDisplayConfiguration();
                var loadedEmulator = configService.GetEmulatorConfiguration();
                var loadedInput = configService.GetInputConfiguration("NES");

                Console.WriteLine($"Loaded Library Path: {loadedPrefs.DefaultLibraryPath}");
                Console.WriteLine($"Loaded Theme: {loadedPrefs.Theme}");
                Console.WriteLine($"Loaded Fullscreen: {loadedDisplay.FullscreenByDefault}");
                Console.WriteLine($"Loaded Filter: {loadedDisplay.FilterType}");
                Console.WriteLine($"Loaded AutoSave Interval: {loadedEmulator.AutoSaveInterval}");
                Console.WriteLine($"Loaded Fast Forward Speed: {loadedEmulator.FastForwardSpeed}");
                Console.WriteLine($"Loaded Controller Deadzone: {loadedInput.ControllerDeadzone}");
                Console.WriteLine($"Loaded Rumble: {loadedInput.EnableRumble}");

                Console.WriteLine("Configuration System Test completed successfully!");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Configuration system test failed");
                Console.WriteLine($"Test failed: {ex.Message}");
            }
        }

        public static void TestControllerDefinitions()
        {
            Console.WriteLine("Testing Controller Definitions...");
            
            var supportedConsoles = ControllerDefinitions.GetSupportedConsoles();
            Console.WriteLine($"Supported consoles: {string.Join(", ", supportedConsoles.Select(c => c.Name))}");

            foreach (var console in supportedConsoles.Take(3)) // Test first 3 consoles
            {
                var controllerDef = ControllerDefinitions.GetControllerDefinition(console.Tag);
                if (controllerDef != null)
                {
                    Console.WriteLine($"{console.Tag}: {controllerDef.Name} - {controllerDef.Buttons.Count} buttons");
                    
                    foreach (var button in controllerDef.Buttons.Take(5)) // Show first 5 buttons
                    {
                        Console.WriteLine($"  {button.Name}: {button.DisplayName} at ({button.X}, {button.Y})");
                    }
                }
            }
        }
    }
}
