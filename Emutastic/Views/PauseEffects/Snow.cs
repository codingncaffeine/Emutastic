using System;
using System.Windows;
using System.Windows.Media;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Classic ZSNES-style snowfall: white particles drift downward with a
    /// per-flake sine-wave horizontal sway, varied size and fall speed for depth.
    /// Light enough to run on top of a paused Vulkan present without breaking
    /// frame budget on integrated GPUs.
    /// </summary>
    public sealed class Snow : IPauseEffect
    {
        public string Id => "snow";
        public string DisplayName => "Snow";

        private struct Flake
        {
            public double X, Y;            // position
            public double VelocityY;       // px/s
            public double DriftPhase;      // current sine input
            public double DriftRate;       // rad/s
            public double DriftAmplitude;  // px
            public double Radius;          // px
            public double Opacity;         // 0..1
        }

        private Flake[] _flakes = Array.Empty<Flake>();
        private Size _canvas;
        private readonly Random _rng = new();
        private readonly Brush _flakeBrush;

        public Snow()
        {
            // White-with-slight-blue, partially transparent so it doesn't blot out
            // the paused frame underneath. Frozen for cheaper rendering.
            _flakeBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0xF0, 0xF6, 0xFF));
            _flakeBrush.Freeze();
        }

        public void Init(Size canvasSize, double intensity)
        {
            _canvas = canvasSize;
            // 1.0 intensity ≈ 220 flakes on a 1920×1080 canvas; scales with area.
            int baseCount = (int)(220 * (_canvas.Width * _canvas.Height) / (1920.0 * 1080.0));
            int count = Math.Max(40, (int)(baseCount * intensity));
            _flakes = new Flake[count];
            for (int i = 0; i < count; i++)
                _flakes[i] = SpawnFlake(initialY: _rng.NextDouble() * _canvas.Height);
        }

        private Flake SpawnFlake(double initialY)
        {
            // Three discrete pixel sizes — 1px, 2px, 3px — for the ZSNES pixel-art
            // snow look. Each flake sticks with its size for life so the rendered
            // pixels are crisp and integer-aligned.
            int sizeBucket = _rng.Next(3);
            double size = sizeBucket == 0 ? 1.0 : sizeBucket == 1 ? 2.0 : 3.0;
            double depth = sizeBucket / 2.0; // 0, 0.5, 1.0
            return new Flake
            {
                X = _rng.NextDouble() * _canvas.Width,
                Y = initialY,
                VelocityY = 25 + depth * 90,                // 25–115 px/s
                DriftPhase = _rng.NextDouble() * Math.PI * 2,
                DriftRate = 0.4 + _rng.NextDouble() * 1.2,  // rad/s
                DriftAmplitude = 8 + _rng.NextDouble() * 22, // px
                Radius = size,                              // square side length
                Opacity = 0.6 + depth * 0.4,                // 0.6–1.0 (front flakes brighter)
            };
        }

        public void Tick(double deltaSeconds, DrawingContext dc)
        {
            for (int i = 0; i < _flakes.Length; i++)
            {
                ref var f = ref _flakes[i];
                f.Y += f.VelocityY * deltaSeconds;
                f.DriftPhase += f.DriftRate * deltaSeconds;
                if (f.Y - f.Radius > _canvas.Height)
                {
                    f = SpawnFlake(initialY: -f.Radius);
                }
                double drawX = f.X + Math.Sin(f.DriftPhase) * f.DriftAmplitude;
                if (drawX < -f.Radius) drawX += _canvas.Width + f.Radius * 2;
                else if (drawX > _canvas.Width + f.Radius) drawX -= _canvas.Width + f.Radius * 2;

                // Snap to integer pixels so squares are crisp — that's the ZSNES vibe.
                double px = Math.Floor(drawX);
                double py = Math.Floor(f.Y);
                var rect = new Rect(px, py, f.Radius, f.Radius);
                if (f.Opacity < 1.0)
                {
                    dc.PushOpacity(f.Opacity);
                    dc.DrawRectangle(_flakeBrush, null, rect);
                    dc.Pop();
                }
                else
                {
                    dc.DrawRectangle(_flakeBrush, null, rect);
                }
            }
        }

        public void Dispose() { _flakes = Array.Empty<Flake>(); }
    }
}
