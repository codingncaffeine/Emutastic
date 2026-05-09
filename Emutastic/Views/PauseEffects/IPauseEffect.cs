using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Animated overlay drawn on top of the paused game. Two flavors:
    ///   - <see cref="IPauseEffect"/>: vector / DrawingContext (cheap, GPU-composited)
    ///   - <see cref="IPixelPauseEffect"/>: per-pixel via <see cref="WriteableBitmap"/>
    ///     (for plasma, aurora, lava-lamp style effects)
    /// </summary>
    public interface IPauseEffect : IDisposable
    {
        /// <summary>Stable identifier used for persistence in the config.</summary>
        string Id { get; }

        /// <summary>Display label for the picker in Preferences.</summary>
        string DisplayName { get; }

        /// <summary>
        /// Initialize with the canvas size and intensity multiplier (0.5–2.0).
        /// Called whenever the size changes or the effect starts.
        /// </summary>
        void Init(Size canvasSize, double intensity);

        /// <summary>Per-frame tick. Called from CompositionTarget.Rendering.</summary>
        void Tick(double deltaSeconds, DrawingContext dc);
    }

    /// <summary>
    /// Pixel-bitmap variant of <see cref="IPauseEffect"/>. Implementers write
    /// into the supplied WriteableBitmap each frame. The host wraps the bitmap
    /// in an Image element and presents it through WPF's normal compositor.
    /// </summary>
    public interface IPixelPauseEffect : IDisposable
    {
        string Id { get; }
        string DisplayName { get; }
        void Init(int width, int height, double intensity);
        void Tick(double deltaSeconds, WriteableBitmap target);
    }
}
