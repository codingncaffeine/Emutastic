using System;
using System.Windows;
using System.Windows.Media;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Falling green katakana columns à la The Matrix. Each column has its own
    /// fall rate and a "head" character drawn brighter than the trail behind it.
    /// </summary>
    public sealed class MatrixRain : IPauseEffect
    {
        public string Id => "matrix";
        public string DisplayName => "Matrix Rain";

        private struct Column
        {
            public double X;
            public double Head;       // y of the brightest character
            public double VelocityY;  // px/s
            public int    Length;     // trail length in characters
            public char[] Chars;      // current glyph per row (refreshed occasionally)
            public double GlyphPhase; // accumulator for the per-N-frames glyph swap
        }

        private const double GlyphSize = 14;
        private Column[] _cols = Array.Empty<Column>();
        private Size _canvas;
        private readonly Random _rng = new();
        private readonly Typeface _face = new(new FontFamily("Consolas"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private readonly Brush _bright;
        private readonly Brush _trail;
        // FormattedText is heavy (font shaping, etc). At ~80 cols × 15 visible
        // glyphs × 60Hz that's 72k allocs/sec without caching. Reuse per (char,
        // brush) since we only have two brushes and a small glyph alphabet.
        private readonly System.Collections.Generic.Dictionary<int, FormattedText> _glyphCache = new();
        // Half-width katakana — visually closest to the film's column glyphs.
        // Mixed with digits and basic Latin for texture.
        private static readonly char[] Glyphs;
        static MatrixRain()
        {
            var list = new System.Collections.Generic.List<char>();
            for (char c = 'ｦ'; c <= 'ﾝ'; c++) list.Add(c); // half-width katakana
            for (char c = '0'; c <= '9'; c++) list.Add(c);
            for (char c = 'A'; c <= 'Z'; c++) list.Add(c);
            Glyphs = list.ToArray();
        }

        public MatrixRain()
        {
            _bright = new SolidColorBrush(Color.FromArgb(0xFF, 0xC8, 0xFF, 0xC8));
            _trail  = new SolidColorBrush(Color.FromArgb(0xFF, 0x36, 0xC0, 0x42));
            _bright.Freeze();
            _trail.Freeze();
        }

        public void Init(Size canvasSize, double intensity)
        {
            _canvas = canvasSize;
            int colCount = Math.Max(8, (int)(_canvas.Width / GlyphSize));
            _cols = new Column[colCount];
            for (int i = 0; i < colCount; i++) _cols[i] = NewColumn(i, intensity, randomStartY: true);
        }

        private Column NewColumn(int index, double intensity, bool randomStartY)
        {
            int len = 6 + _rng.Next(20);
            var chars = new char[len];
            for (int i = 0; i < len; i++) chars[i] = Glyphs[_rng.Next(Glyphs.Length)];
            return new Column
            {
                X = index * GlyphSize + 2,
                Head = randomStartY ? _rng.NextDouble() * _canvas.Height : -GlyphSize,
                VelocityY = (60 + _rng.NextDouble() * 140) * (0.6 + intensity * 0.5),
                Length = len,
                Chars = chars,
                GlyphPhase = 0,
            };
        }

        public void Tick(double dt, DrawingContext dc)
        {
            for (int i = 0; i < _cols.Length; i++)
            {
                ref var col = ref _cols[i];
                col.Head += col.VelocityY * dt;
                col.GlyphPhase += dt;
                // Periodically refresh a random glyph in the trail for the "scrambling text" feel.
                if (col.GlyphPhase > 0.08)
                {
                    col.GlyphPhase = 0;
                    col.Chars[_rng.Next(col.Chars.Length)] = Glyphs[_rng.Next(Glyphs.Length)];
                }
                if (col.Head - col.Length * GlyphSize > _canvas.Height)
                {
                    col = NewColumn(i, 1.0, randomStartY: false);
                }

                for (int j = 0; j < col.Length; j++)
                {
                    double y = col.Head - j * GlyphSize;
                    if (y < -GlyphSize || y > _canvas.Height) continue;
                    bool isHead = j == 0;
                    double opacity = isHead ? 1.0 : (1.0 - (double)j / col.Length) * 0.9;
                    var brush = isHead ? _bright : _trail;
                    int key = (col.Chars[j] << 1) | (isHead ? 1 : 0);
                    if (!_glyphCache.TryGetValue(key, out var ft))
                    {
                        ft = new FormattedText(
                            col.Chars[j].ToString(),
                            System.Globalization.CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            _face, GlyphSize, brush, 1.0);
                        _glyphCache[key] = ft;
                    }
                    dc.PushOpacity(opacity);
                    dc.DrawText(ft, new Point(col.X, y));
                    dc.Pop();
                }
            }
        }

        public void Dispose() { _cols = Array.Empty<Column>(); }
    }
}
