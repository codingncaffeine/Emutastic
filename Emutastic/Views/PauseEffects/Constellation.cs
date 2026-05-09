using System;
using System.Windows;
using System.Windows.Media;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// "Geometric network" effect: nodes drift and bounce off the edges; lines
    /// are drawn between any two nodes within a threshold distance, with line
    /// opacity falling off with distance. Common modern screensaver pattern.
    /// </summary>
    public sealed class Constellation : IPauseEffect
    {
        public string Id => "constellation";
        public string DisplayName => "Constellation";

        private struct Node { public double X, Y, Vx, Vy; }
        private Node[] _nodes = Array.Empty<Node>();
        private Size _canvas;
        private readonly Random _rng = new();
        private readonly Brush _nodeBrush;
        private readonly Color _lineColor = Color.FromArgb(0xFF, 0x9C, 0xCB, 0xFF);
        // Pre-baked alpha-bucket pens. Constellation drew O(N²) lines per frame;
        // allocating a fresh Pen+SolidColorBrush per line was 100k+ heap allocs/sec
        // at N=70. Bucket the line opacity into 16 levels and reuse frozen pens.
        private const int AlphaBuckets = 16;
        private readonly Pen[] _pensByAlpha = new Pen[AlphaBuckets];

        private const double ConnectDistance = 130.0;

        public Constellation()
        {
            _nodeBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xCC, 0xE0, 0xFF));
            _nodeBrush.Freeze();
            for (int i = 0; i < AlphaBuckets; i++)
            {
                byte a = (byte)((i + 1) * 0xC0 / AlphaBuckets);
                var c = Color.FromArgb(a, _lineColor.R, _lineColor.G, _lineColor.B);
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                var pen = new Pen(brush, 0.8);
                pen.Freeze();
                _pensByAlpha[i] = pen;
            }
        }

        public void Init(Size canvasSize, double intensity)
        {
            _canvas = canvasSize;
            int count = (int)(70 * intensity * Math.Sqrt(_canvas.Width * _canvas.Height) / Math.Sqrt(1920 * 1080));
            count = Math.Max(20, Math.Min(180, count));
            _nodes = new Node[count];
            for (int i = 0; i < count; i++) _nodes[i] = NewNode();
        }

        private Node NewNode() => new()
        {
            X = _rng.NextDouble() * _canvas.Width,
            Y = _rng.NextDouble() * _canvas.Height,
            Vx = (-1 + _rng.NextDouble() * 2) * 35,
            Vy = (-1 + _rng.NextDouble() * 2) * 35,
        };

        public void Tick(double dt, DrawingContext dc)
        {
            // Update positions, bounce off edges
            for (int i = 0; i < _nodes.Length; i++)
            {
                ref var n = ref _nodes[i];
                n.X += n.Vx * dt;
                n.Y += n.Vy * dt;
                if (n.X < 0)             { n.X = 0; n.Vx = -n.Vx; }
                if (n.X > _canvas.Width) { n.X = _canvas.Width; n.Vx = -n.Vx; }
                if (n.Y < 0)             { n.Y = 0; n.Vy = -n.Vy; }
                if (n.Y > _canvas.Height){ n.Y = _canvas.Height; n.Vy = -n.Vy; }
            }

            // Draw connecting lines (O(N²) but N is small ~70)
            double connectSq = ConnectDistance * ConnectDistance;
            for (int i = 0; i < _nodes.Length; i++)
            {
                for (int j = i + 1; j < _nodes.Length; j++)
                {
                    double dx = _nodes[i].X - _nodes[j].X;
                    double dy = _nodes[i].Y - _nodes[j].Y;
                    double dsq = dx * dx + dy * dy;
                    if (dsq > connectSq) continue;
                    double d = Math.Sqrt(dsq);
                    double a = 1.0 - d / ConnectDistance;
                    int bucket = Math.Clamp((int)(a * AlphaBuckets), 0, AlphaBuckets - 1);
                    dc.DrawLine(_pensByAlpha[bucket],
                        new Point(_nodes[i].X, _nodes[i].Y),
                        new Point(_nodes[j].X, _nodes[j].Y));
                }
            }

            // Draw nodes
            for (int i = 0; i < _nodes.Length; i++)
                dc.DrawEllipse(_nodeBrush, null, new Point(_nodes[i].X, _nodes[i].Y), 1.6, 1.6);
        }

        public void Dispose() { _nodes = Array.Empty<Node>(); }
    }
}
