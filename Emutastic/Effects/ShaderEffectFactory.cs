using System.Windows.Media.Effects;

namespace Emutastic.Effects;

public static class ShaderEffectFactory
{
    public static ShaderEffect? Create(ShaderPreset preset, uint sourceHeight = 240)
    {
        return preset switch
        {
            ShaderPreset.CrtScanlines => new CrtScanlinesEffect { ScreenHeight = sourceHeight },
            ShaderPreset.GameBoyDmg => new GameBoyDmgEffect(),
            ShaderPreset.GameBoyDmgLcd => new GameBoyDmgLcdEffect { ScreenHeight = sourceHeight },
            ShaderPreset.GameBoyPocket => new GameBoyPocketEffect(),
            ShaderPreset.LcdGrid => new LcdGridEffect { ScreenHeight = sourceHeight },
            _ => null // None and Smooth handled separately
        };
    }
}
