using System;
using System.Windows;
using System.Windows.Media;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Vertical rain streaks with slight wind shear. Faster and more aggressive
    /// than Snow — a different mood for the same particle pattern.
    /// </summary>
    public sealed class Rain : IPauseEffect
    {
        public string Id => "rain";
        public string DisplayName => "Rain";

        private struct Drop { public double X, Y, Length, VelocityY, Lean; public double Opacity; }
        private Drop[] _drops = Array.Empty<Drop>();
        private Size _canvas;
        private readonly Random _rng = new();
        private readonly Pen _pen;

        public Rain()
        {
            var brush = new SolidColorBrush(Color.FromArgb(0xC0, 0xCB, 0xD8, 0xEA));
            brush.Freeze();
            _pen = new Pen(brush, 1.2);
            _pen.Freeze();
        }

        public void Init(Size canvasSize, double intensity)
        {
            _canvas = canvasSize;
            int count = (int)(280 * intensity * (_canvas.Width * _canvas.Height) / (1920.0 * 1080.0));
            count = Math.Max(60, count);
            _drops = new Drop[count];
            for (int i = 0; i < count; i++) _drops[i] = NewDrop(_rng.NextDouble() * _canvas.Height);
        }

        private Drop NewDrop(double y) => new()
        {
            X = _rng.NextDouble() * _canvas.Width,
            Y = y,
            Length = 8 + _rng.NextDouble() * 18,
            VelocityY = 380 + _rng.NextDouble() * 320,
            Lean = -2 + _rng.NextDouble() * 4,
            Opacity = 0.35 + _rng.NextDouble() * 0.55,
        };

        public void Tick(double dt, DrawingContext dc)
        {
            for (int i = 0; i < _drops.Length; i++)
            {
                ref var d = ref _drops[i];
                d.Y += d.VelocityY * dt;
                if (d.Y - d.Length > _canvas.Height) d = NewDrop(-d.Length);
                dc.PushOpacity(d.Opacity);
                dc.DrawLine(_pen, new Point(d.X, d.Y), new Point(d.X + d.Lean, d.Y + d.Length));
                dc.Pop();
            }
        }

        public void Dispose() { _drops = Array.Empty<Drop>(); }
    }
}
