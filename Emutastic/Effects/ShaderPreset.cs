namespace Emutastic.Effects;

public enum ShaderPreset
{
    None,
    CrtScanlines,
    GameBoyDmg,
    GameBoyDmgLcd,
    GameBoyPocket,
    LcdGrid,
    Smooth
}

public static class ShaderPresetExtensions
{
    public static string DisplayName(this ShaderPreset preset) => preset switch
    {
        ShaderPreset.None => "None",
        ShaderPreset.CrtScanlines => "CRT Scanlines",
        ShaderPreset.GameBoyDmg => "Game Boy (DMG)",
        ShaderPreset.GameBoyDmgLcd => "Game Boy (DMG LCD)",
        ShaderPreset.GameBoyPocket => "Game Boy Pocket",
        ShaderPreset.LcdGrid => "LCD Grid",
        ShaderPreset.Smooth => "Smooth",
        _ => preset.ToString()
    };
}
