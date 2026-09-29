using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace Emutastic.Configuration
{
    public static class ConfigurationExtensions
    {
        // Legacy support - convert old InputMapping to new ButtonMapping
        public static ButtonMapping ToButtonMapping(this Services.InputMapping oldMapping)
        {
            return new ButtonMapping
            {
                ButtonName = oldMapping.ButtonName,
                InputIdentifier = oldMapping.InputType == Services.InputType.Keyboard 
                    ? ((Key)oldMapping.Key).ToString() 
                    : oldMapping.ControllerButtonId.ToString(),
                InputType = oldMapping.InputType == Services.InputType.Keyboard 
                    ? InputType.Keyboard 
                    : InputType.Controller,
                DisplayName = oldMapping.DisplayText,
                ModifierKeys = 0 // TODO: Extract modifier keys if needed
            };
        }

        // Convert new ButtonMapping back to legacy InputMapping
        public static Services.InputMapping ToLegacyInputMapping(this ButtonMapping newMapping)
        {
            return new Services.InputMapping
            {
                ButtonName = newMapping.ButtonName,
                InputType = newMapping.InputType == InputType.Keyboard 
                    ? Services.InputType.Keyboard 
                    : Services.InputType.Controller,
                Key = newMapping.InputType == InputType.Keyboard && Enum.TryParse<Key>(newMapping.InputIdentifier, out var key)
                    ? key 
                    : Key.None,
                ControllerButtonId = newMapping.InputType == InputType.Controller && uint.TryParse(newMapping.InputIdentifier, out var buttonId)
                    ? buttonId 
                    : 0,
                DisplayText = newMapping.DisplayName
            };
        }

        // Get default controller mappings for a console
        public static List<ButtonMapping> GetDefaultControllerMappings(string consoleName)
        {
            return consoleName switch
            {
                "NES" => new List<ButtonMapping>
                {
                    new() { ButtonName = "Up", InputIdentifier = "4", InputType = InputType.Controller, DisplayName = "D-Pad Up" },
                    new() { ButtonName = "Down", InputIdentifier = "5", InputType = InputType.Controller, DisplayName = "D-Pad Down" },
                    new() { ButtonName = "Left", InputIdentifier = "6", InputType = InputType.Controller, DisplayName = "D-Pad Left" },
                    new() { ButtonName = "Right", InputIdentifier = "7", InputType = InputType.Controller, DisplayName = "D-Pad Right" },
                    new() { ButtonName = "Select", InputIdentifier = "2", InputType = InputType.Controller, DisplayName = "Back" },
                    new() { ButtonName = "Start", InputIdentifier = "3", InputType = InputType.Controller, DisplayName = "Start" },
                    new() { ButtonName = "B", InputIdentifier = "0", InputType = InputType.Controller, DisplayName = "B" },
                    new() { ButtonName = "A", InputIdentifier = "8", InputType = InputType.Controller, DisplayName = "A" },
                },
                "SNES" => new List<ButtonMapping>
                {
                    new() { ButtonName = "Up", InputIdentifier = "4", InputType = InputType.Controller, DisplayName = "D-Pad Up" },
                    new() { ButtonName = "Down", InputIdentifier = "5", InputType = InputType.Controller, DisplayName = "D-Pad Down" },
                    new() { ButtonName = "Left", InputIdentifier = "6", InputType = InputType.Controller, DisplayName = "D-Pad Left" },
                    new() { ButtonName = "Right", InputIdentifier = "7", InputType = InputType.Controller, DisplayName = "D-Pad Right" },
                    new() { ButtonName = "Select", InputIdentifier = "2", InputType = InputType.Controller, DisplayName = "Back" },
                    new() { ButtonName = "Start", InputIdentifier = "3", InputType = InputType.Controller, DisplayName = "Start" },
                    new() { ButtonName = "Y", InputIdentifier = "9", InputType = InputType.Controller, DisplayName = "Y" },
                    new() { ButtonName = "X", InputIdentifier = "8", InputType = InputType.Controller, DisplayName = "X" },
                    new() { ButtonName = "B", InputIdentifier = "0", InputType = InputType.Controller, DisplayName = "B" },
                    new() { ButtonName = "A", InputIdentifier = "10", InputType = InputType.Controller, DisplayName = "A" },
                    new() { ButtonName = "L", InputIdentifier = "11", InputType = InputType.Controller, DisplayName = "LB" },
                    new() { ButtonName = "R", InputIdentifier = "12", InputType = InputType.Controller, DisplayName = "RB" },
                },
                "2600" => new List<ButtonMapping>
                {
                    new() { ButtonName = "Up", InputIdentifier = "4", InputType = InputType.Controller, DisplayName = "D-Pad Up" },
                    new() { ButtonName = "Down", InputIdentifier = "5", InputType = InputType.Controller, DisplayName = "D-Pad Down" },
                    new() { ButtonName = "Left", InputIdentifier = "6", InputType = InputType.Controller, DisplayName = "D-Pad Left" },
                    new() { ButtonName = "Right", InputIdentifier = "7", InputType = InputType.Controller, DisplayName = "D-Pad Right" },
                    new() { ButtonName = "Fire", InputIdentifier = "0", InputType = InputType.Controller, DisplayName = "A" },
                    new() { ButtonName = "Select", InputIdentifier = "2", InputType = InputType.Controller, DisplayName = "Back" },
                    new() { ButtonName = "Reset", InputIdentifier = "3", InputType = InputType.Controller, DisplayName = "Start" },
                },
                "Genesis" => new List<ButtonMapping>
                {
                    new() { ButtonName = "Up", InputIdentifier = "4", InputType = InputType.Controller, DisplayName = "D-Pad Up" },
                    new() { ButtonName = "Down", InputIdentifier = "5", InputType = InputType.Controller, DisplayName = "D-Pad Down" },
                    new() { ButtonName = "Left", InputIdentifier = "6", InputType = InputType.Controller, DisplayName = "D-Pad Left" },
                    new() { ButtonName = "Right", InputIdentifier = "7", InputType = InputType.Controller, DisplayName = "D-Pad Right" },
                    new() { ButtonName = "Select", InputIdentifier = "2", InputType = InputType.Controller, DisplayName = "Back" },
                    new() { ButtonName = "Start", InputIdentifier = "3", InputType = InputType.Controller, DisplayName = "Start" },
                    new() { ButtonName = "A", InputIdentifier = "0", InputType = InputType.Controller, DisplayName = "A" },
                    new() { ButtonName = "B", InputIdentifier = "1", InputType = InputType.Controller, DisplayName = "B" },
                    new() { ButtonName = "C", InputIdentifier = "8", InputType = InputType.Controller, DisplayName = "C" },
                },
                "N64" => new List<ButtonMapping>
                {
                    new() { ButtonName = "Up", InputIdentifier = "4", InputType = InputType.Controller, DisplayName = "D-Pad Up" },
                    new() { ButtonName = "Down", InputIdentifier = "5", InputType = InputType.Controller, DisplayName = "D-Pad Down" },
                    new() { ButtonName = "Left", InputIdentifier = "6", InputType = InputType.Controller, DisplayName = "D-Pad Left" },
                    new() { ButtonName = "Right", InputIdentifier = "7", InputType = InputType.Controller, DisplayName = "D-Pad Right" },
                    new() { ButtonName = "Select", InputIdentifier = "2", InputType = InputType.Controller, DisplayName = "Back" },
                    new() { ButtonName = "Start", InputIdentifier = "3", InputType = InputType.Controller, DisplayName = "Start" },
                    new() { ButtonName = "A", InputIdentifier = "10", InputType = InputType.Controller, DisplayName = "A" },
                    new() { ButtonName = "B", InputIdentifier = "9", InputType = InputType.Controller, DisplayName = "B" },
                    new() { ButtonName = "Z", InputIdentifier = "13", InputType = InputType.Controller, DisplayName = "Z" },
                    new() { ButtonName = "L", InputIdentifier = "11", InputType = InputType.Controller, DisplayName = "L" },
                    new() { ButtonName = "R", InputIdentifier = "12", InputType = InputType.Controller, DisplayName = "R" },
                    new() { ButtonName = "C Up", InputIdentifier = "14", InputType = InputType.Controller, DisplayName = "C Up" },
                    new() { ButtonName = "C Down", InputIdentifier = "15", InputType = InputType.Controller, DisplayName = "C Down" },
                    new() { ButtonName = "C Left", InputIdentifier = "16", InputType = InputType.Controller, DisplayName = "C Left" },
                    new() { ButtonName = "C Right", InputIdentifier = "17", InputType = InputType.Controller, DisplayName = "C Right" },
                },
                "Saturn" or "SegaCD" or "Sega32X" => new List<ButtonMapping>
                {
                    new() { ButtonName = "Up",     InputIdentifier = "4",  InputType = InputType.Controller, DisplayName = "D-Pad Up" },
                    new() { ButtonName = "Down",   InputIdentifier = "5",  InputType = InputType.Controller, DisplayName = "D-Pad Down" },
                    new() { ButtonName = "Left",   InputIdentifier = "6",  InputType = InputType.Controller, DisplayName = "D-Pad Left" },
                    new() { ButtonName = "Right",  InputIdentifier = "7",  InputType = InputType.Controller, DisplayName = "D-Pad Right" },
                    new() { ButtonName = "Start",  InputIdentifier = "3",  InputType = InputType.Controller, DisplayName = "Start" },
                    new() { ButtonName = "A",      InputIdentifier = "8",  InputType = InputType.Controller, DisplayName = "A" },
                    new() { ButtonName = "B",      InputIdentifier = "0",  InputType = InputType.Controller, DisplayName = "B" },
                    new() { ButtonName = "C",      InputIdentifier = "9",  InputType = InputType.Controller, DisplayName = "X" },
                    new() { ButtonName = "X",      InputIdentifier = "1",  InputType = InputType.Controller, DisplayName = "Y" },
                    new() { ButtonName = "Y",      InputIdentifier = "10", InputType = InputType.Controller, DisplayName = "LB" },
                    new() { ButtonName = "Z",      InputIdentifier = "11", InputType = InputType.Controller, DisplayName = "RB" },
                    new() { ButtonName = "L",      InputIdentifier = "12", InputType = InputType.Controller, DisplayName = "LT" },
                    new() { ButtonName = "R",      InputIdentifier = "13", InputType = InputType.Controller, DisplayName = "RT" },
                },
                // Add more console defaults as needed
                _ => GetDefaultControllerMappings("NES") // Default to NES layout
            };
        }

        // Validate and fix button mappings
        public static void ValidateMappings(this InputConfiguration config)
        {
            var controllerDef = ControllerDefinitions.GetControllerDefinition(config.ConsoleName);
            if (controllerDef == null) return;

            // Remove mappings for buttons that don't exist
            config.KeyboardMappings.RemoveAll(m => !controllerDef.Buttons.Any(b => b.Name == m.ButtonName));
            config.ControllerMappings.RemoveAll(m => !controllerDef.Buttons.Any(b => b.Name == m.ButtonName));

            // Add missing controller mappings with defaults. A button with no
            // keyboard bind needs none: the game window's built-in keys play it
            // (Services.KeyboardBindings), and a stored copy would retire them.
            foreach (var button in controllerDef.Buttons)
            {
                if (!config.ControllerMappings.Any(m => m.ButtonName == button.Name))
                {
                    var defaultMappings = GetDefaultControllerMappings(config.ConsoleName);
                    var defaultMapping = defaultMappings.FirstOrDefault(m => m.ButtonName == button.Name);
                    if (defaultMapping != null)
                    {
                        config.ControllerMappings.Add(defaultMapping);
                    }
                }
            }
        }
    }
}
