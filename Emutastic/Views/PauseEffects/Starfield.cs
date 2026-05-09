using System;
using System.Windows;
using System.Windows.Media;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Classic Win95-style 3D starfield: stars zoom outward from center, growing
    /// in size and brightness as their pseudo-z approaches zero.
    /// </summary>
    public sealed class Starfield : IPauseEffect
    {
        public string Id => "starfield";
        public string DisplayName => "Starfield";

        private struct Star { public double X, Y, Z, Vz; }
        private Star[] _stars = Array.Empty<Star>();
        private Size _canvas;
        private readonly Random _rng = new();
        private readonly Brush _white;

        public Starfield()
        {
            _white = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            _white.Freeze();
        }

        public void Init(Size canvasSize, double intensity)
        {
            _canvas = canvasSize;
            int count = (int)(450 * intensity);
            count = Math.Max(120, count);
            _stars = new Star[count];
            for (int i = 0; i < count; i++) _stars[i] = NewStar(_rng.NextDouble() * 1.0);
        }

        private Star NewStar(double zStart) => new()
        {
            X = (_rng.NextDouble() * 2 - 1) * _canvas.Width,
            Y = (_rng.NextDouble() * 2 - 1) * _canvas.Height,
            Z = zStart < 0.05 ? 1.0 : zStart, // start far away
            Vz = 0.4 + _rng.NextDouble() * 0.6,
        };

        public void Tick(double dt, DrawingContext dc)
        {
            double cx = _canvas.Width / 2.0;
            double cy = _canvas.Height / 2.0;
            for (int i = 0; i < _stars.Length; i++)
            {
                ref var s = ref _stars[i];
                s.Z -= s.Vz * dt;
                if (s.Z <= 0.05) { s = NewStar(1.0); continue; }
                double k = 1.0 / s.Z;
                double sx = cx + s.X * k * 0.1;
                double sy = cy + s.Y * k * 0.1;
                if (sx < 0 || sx > _canvas.Width || sy < 0 || sy > _canvas.Height)
                {
                    s = NewStar(1.0);
                    continue;
                }
                double size = (1.0 - s.Z) * 2.4 + 0.4;
                double opacity = (1.0 - s.Z) * 0.85 + 0.15;
                dc.PushOpacity(opacity);
                dc.DrawEllipse(_white, null, new Point(sx, sy), size, size);
                dc.Pop();
            }
        }

        public void Dispose() { _stars = Array.Empty<Star>(); }
    }
}
