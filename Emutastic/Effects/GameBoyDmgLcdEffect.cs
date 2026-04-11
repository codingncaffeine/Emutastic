using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Emutastic.Effects;

public class GameBoyDmgLcdEffect : ShaderEffect
{
    private static readonly PixelShader _shader = new()
    {
        UriSource = new Uri("pack://application:,,,/Shaders/Compiled/GameBoyDmgLcd.ps")
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("input", typeof(GameBoyDmgLcdEffect), 0);

    public static readonly DependencyProperty ScreenHeightProperty =
        DependencyProperty.Register(nameof(ScreenHeight), typeof(double), typeof(GameBoyDmgLcdEffect),
            new UIPropertyMetadata(144.0, PixelShaderConstantCallback(0)));

    public GameBoyDmgLcdEffect()
    {
        PixelShader = _shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(ScreenHeightProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public double ScreenHeight
    {
        get => (double)GetValue(ScreenHeightProperty);
        set => SetValue(ScreenHeightProperty, value);
    }
}
