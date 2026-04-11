using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Emutastic.Effects;

public class CrtScanlinesEffect : ShaderEffect
{
    private static readonly PixelShader _shader = new()
    {
        UriSource = new Uri("pack://application:,,,/Shaders/Compiled/CrtScanlines.ps")
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("input", typeof(CrtScanlinesEffect), 0);

    public static readonly DependencyProperty ScreenHeightProperty =
        DependencyProperty.Register(nameof(ScreenHeight), typeof(double), typeof(CrtScanlinesEffect),
            new UIPropertyMetadata(240.0, PixelShaderConstantCallback(0)));

    public CrtScanlinesEffect()
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
