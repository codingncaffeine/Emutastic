using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Emutastic.Effects;

public class GameBoyDmgEffect : ShaderEffect
{
    private static readonly PixelShader _shader = new()
    {
        UriSource = new Uri("pack://application:,,,/Shaders/Compiled/GameBoyDmg.ps")
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("input", typeof(GameBoyDmgEffect), 0);

    public GameBoyDmgEffect()
    {
        PixelShader = _shader;
        UpdateShaderValue(InputProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }
}
