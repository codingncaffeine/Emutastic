using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Emutastic.Effects;

public class LcdGridEffect : ShaderEffect
{
    private static readonly PixelShader _shader = new()
    {
        UriSource = new Uri("pack://application:,,,/Shaders/Compiled/LcdGrid.ps")
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("input", typeof(LcdGridEffect), 0);

    public static readonly DependencyProperty ScreenHeightProperty =
        DependencyProperty.Register(nameof(ScreenHeight), typeof(double), typeof(LcdGridEffect),
            new UIPropertyMetadata(240.0, PixelShaderConstantCallback(0)));

    public LcdGridEffect()
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
