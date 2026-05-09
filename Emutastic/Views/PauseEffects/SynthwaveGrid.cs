using System;
using System.Windows;
using System.Windows.Media;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Synthwave / Tron-style perspective neon grid receding to a horizon.
    /// Horizontal lines scroll toward the viewer; vertical lines fan out from
    /// the vanishing point. Uses one moving offset to animate the depth.
    /// </summary>
    public sealed class SynthwaveGrid : IPauseEffect
    {
        public string Id => "synthwave";
        public string DisplayName => "Synthwave Grid";

        private Size _canvas;
        private double _scroll;            // 0..1 advance toward viewer
        private double _scrollSpeed = 0.3; // cycles per second
        private readonly Pen _gridPen;
        private readonly Brush _horizonGlow;

        public SynthwaveGrid()
        {
            // Pink/magenta neon — the genre default.
            var brush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x4B, 0xCB));
            brush.Freeze();
            _gridPen = new Pen(brush, 1.2);
            _gridPen.Freeze();

            var lg = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint   = new Point(0.5, 1),
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0x99, 0x66, 0x22, 0x99), 0.0),
                    new(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 1.0),
                },
            };
            lg.Freeze();
            _horizonGlow = lg;
        }

        public void Init(Size canvasSize, double intensity)
        {
            _canvas = canvasSize;
            _scrollSpeed = 0.2 + 0.4 * intensity;
        }

        public void Tick(double dt, DrawingContext dc)
        {
            _scroll = (_scroll + _scrollSpeed * dt) % 1.0;

            double w = _canvas.Width;
            double h = _canvas.Height;
            double horizonY = h * 0.55;
            double cx = w / 2.0;

            // Horizon glow band
            dc.DrawRectangle(_horizonGlow, null,
                new Rect(0, horizonY - 12, w, h * 0.45));

            // Horizontal lines: spaced exponentially so they appear to come from
            // the horizon. Animate by sliding `scroll` through the spacing band.
            int rows = 14;
            for (int i = 0; i < rows; i++)
            {
                double t = (i + _scroll) / rows;     // 0..1, animated
                t = t * t * t;                       // perspective easing
                double y = horizonY + (h - horizonY) * t;
                if (y < horizonY || y > h) continue;
                double opacity = (1.0 - t) * 0.9 + 0.1;
                dc.PushOpacity(opacity);
                dc.DrawLine(_gridPen, new Point(0, y), new Point(w, y));
                dc.Pop();
            }

            // Vertical lines: fan from the vanishing point at (cx, horizonY) to
            // evenly spaced bottom-edge x positions.
            int cols = 22;
            for (int i = -cols; i <= cols; i++)
            {
                double bx = cx + (i / (double)cols) * (w * 1.5);
                dc.DrawLine(_gridPen, new Point(cx, horizonY), new Point(bx, h));
            }
        }

        public void Dispose() { }
    }
}
