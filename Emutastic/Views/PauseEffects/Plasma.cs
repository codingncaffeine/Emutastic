using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Demoscene-style plasma: combine several sine waves into a per-pixel hue,
    /// scrolling in time. Looks great as a slow ambient pause overlay. Renders
    /// at 320×240 internal — the host upscales via Stretch=Fill so the visual
    /// looks soft and CRT-ish at typical game window sizes.
    /// </summary>
    public sealed class Plasma : IPixelPauseEffect
    {
        public string Id => "plasma";
        public string DisplayName => "Plasma";

        private int _w, _h;
        private double _t;
        private double _intensity = 1.0;
        private byte[] _buffer = Array.Empty<byte>();

        public void Init(int width, int height, double intensity)
        {
            _w = width; _h = height;
            _intensity = intensity;
            _buffer = new byte[width * height * 4]; // BGRA
            _t = 0;
        }

        public void Tick(double dt, WriteableBitmap target)
        {
            _t += dt * 0.6 * _intensity;

            // Per-pixel sine combo. Three contributing waves give a non-trivial
            // pattern; HSV→RGB conversion at the end produces smooth colors.
            // Split per row so the inner X loop is tight.
            for (int y = 0; y < _h; y++)
            {
                double fy = y / (double)_h;
                for (int x = 0; x < _w; x++)
                {
                    double fx = x / (double)_w;
                    double v = Math.Sin((fx * 10 + _t))
                             + Math.Sin((fy * 10 + _t * 0.7))
                             + Math.Sin(((fx + fy) * 8 + _t * 1.3))
                             + Math.Sin(Math.Sqrt((fx - 0.5) * (fx - 0.5) + (fy - 0.5) * (fy - 0.5)) * 16 + _t * 0.4);
                    v = v * 0.25 + 0.5; // -1..1 → 0..1
                    // Hue rotates slowly; saturation/value full for vivid plasma.
                    double hue = (v + _t * 0.05) % 1.0;
                    HsvToBgra(hue, 0.85, 0.85, out byte b, out byte g, out byte r);
                    int o = (y * _w + x) * 4;
                    _buffer[o + 0] = b;
                    _buffer[o + 1] = g;
                    _buffer[o + 2] = r;
                    _buffer[o + 3] = 0xC8; // ~78% alpha so paused frame still shows
                }
            }

            target.WritePixels(new System.Windows.Int32Rect(0, 0, _w, _h),
                _buffer, _w * 4, 0);
        }

        // Hue 0..1 → BGR
        private static void HsvToBgra(double h, double s, double v, out byte b, out byte g, out byte r)
        {
            double hh = h * 6.0;
            int i = (int)Math.Floor(hh);
            double f = hh - i;
            double p = v * (1 - s);
            double q = v * (1 - s * f);
            double t = v * (1 - s * (1 - f));
            double rr = 0, gg = 0, bb = 0;
            switch (i % 6)
            {
                case 0: rr = v; gg = t; bb = p; break;
                case 1: rr = q; gg = v; bb = p; break;
                case 2: rr = p; gg = v; bb = t; break;
                case 3: rr = p; gg = q; bb = v; break;
                case 4: rr = t; gg = p; bb = v; break;
                case 5: rr = v; gg = p; bb = q; break;
            }
            r = (byte)(rr * 255);
            g = (byte)(gg * 255);
            b = (byte)(bb * 255);
        }

        public void Dispose() { _buffer = Array.Empty<byte>(); }
    }
}
