using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Hosts whichever pause effect is active. Two render paths share one element:
    ///   - Vector: a single DrawingVisual that gets reopened each tick via
    ///     <see cref="GetVectorContext"/>; effects draw with DrawingContext primitives.
    ///   - Pixel:  a WriteableBitmap shown through an Image child; effects write
    ///     into the bitmap pixel buffer.
    /// Switching paths is cheap — only the relevant child is visible at a time.
    /// </summary>
    public sealed class PauseEffectHost : Grid
    {
        private readonly DrawingVisualHost _vectorHost = new();
        private readonly Image _pixelImage = new()
        {
            Stretch = Stretch.Fill,
            Visibility = Visibility.Collapsed,
            UseLayoutRounding = true,
        };
        private WriteableBitmap? _pixelBitmap;
        // Subtle dark wash behind the animation so light particles (snow, stars)
        // pop against bright paused frames. Sits behind the effect; the effect
        // composites on top so the visible game frame still shows through.
        private readonly System.Windows.Shapes.Rectangle _shade = new()
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x4D, 0x00, 0x00, 0x00)), // ~30% black
            IsHitTestVisible = false,
        };

        public PauseEffectHost()
        {
            // Transparent so the paused game frame shows through.
            Background = Brushes.Transparent;
            IsHitTestVisible = false;   // never intercept input — pause overlay is decorative
            ClipToBounds = true;
            Children.Add(_shade);       // bottom layer
            Children.Add(_vectorHost);  // vector effect on top of shade
            Children.Add(_pixelImage);  // pixel effect on top of shade (mutually exclusive with vector)
            ((SolidColorBrush)_shade.Fill).Freeze();
        }

        public void UseVectorPath()
        {
            _vectorHost.Visibility = Visibility.Visible;
            _pixelImage.Visibility = Visibility.Collapsed;
            _vectorHost.Clear();
        }

        /// <summary>
        /// Switch to the pixel-bitmap path with a backing buffer at the requested
        /// dimensions. The bitmap is upscaled by the Image's Stretch=Fill so the
        /// effect can render at a coarse internal resolution (e.g. 320×240) while
        /// covering the full canvas.
        /// </summary>
        public WriteableBitmap UsePixelPath(int width, int height)
        {
            // Reuse the existing bitmap if dimensions match — avoids reallocation
            // when the host resizes by a couple of pixels each frame.
            if (_pixelBitmap == null
                || _pixelBitmap.PixelWidth != width
                || _pixelBitmap.PixelHeight != height)
            {
                _pixelBitmap = new WriteableBitmap(
                    width, height, 96, 96, PixelFormats.Bgra32, null);
                _pixelImage.Source = _pixelBitmap;
            }
            _vectorHost.Visibility = Visibility.Collapsed;
            _pixelImage.Visibility = Visibility.Visible;
            return _pixelBitmap;
        }

        public DrawingContext GetVectorContext() => _vectorHost.OpenVisual();

        public void EndVectorContext() => _vectorHost.CloseVisual();

        public void Clear()
        {
            _vectorHost.Clear();
        }

        // Internal: tiny FrameworkElement that hosts a single DrawingVisual and
        // exposes Open/Close methods so callers don't need a `using` per frame.
        private sealed class DrawingVisualHost : FrameworkElement
        {
            private readonly DrawingVisual _visual = new();
            private readonly VisualCollection _children;
            private DrawingContext? _open;

            public DrawingVisualHost()
            {
                _children = new VisualCollection(this) { _visual };
                IsHitTestVisible = false;
            }

            protected override int VisualChildrenCount => _children.Count;
            protected override Visual GetVisualChild(int index) => _children[index];

            public DrawingContext OpenVisual()
            {
                _open = _visual.RenderOpen();
                return _open;
            }
            public void CloseVisual()
            {
                _open?.Close();
                _open = null;
            }
            public void Clear()
            {
                using var dc = _visual.RenderOpen();
            }
        }
    }
}
