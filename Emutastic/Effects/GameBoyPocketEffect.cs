using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Emutastic.Effects;

public class GameBoyPocketEffect : ShaderEffect
{
    private static readonly PixelShader _shader = new()
    {
        UriSource = new Uri("pack://application:,,,/Shaders/Compiled/GameBoyPocket.ps")
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("input", typeof(GameBoyPocketEffect), 0);

    public GameBoyPocketEffect()
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
