using System;
using System.Windows.Media.Imaging;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Aurora borealis style ribbons of color flowing horizontally with vertical
    /// undulation. Per-pixel WriteableBitmap. Lower-frequency sine waves than
    /// Plasma plus a vertical falloff to keep the bands near the upper portion
    /// of the canvas, mimicking the real-world phenomenon.
    /// </summary>
    public sealed class Aurora : IPixelPauseEffect
    {
        public string Id => "aurora";
        public string DisplayName => "Aurora";

        private int _w, _h;
        private double _t;
        private double _intensity = 1.0;
        private byte[] _buffer = Array.Empty<byte>();

        public void Init(int width, int height, double intensity)
        {
            _w = width; _h = height;
            _intensity = intensity;
            _buffer = new byte[width * height * 4];
            _t = 0;
        }

        public void Tick(double dt, WriteableBitmap target)
        {
            _t += dt * 0.4 * _intensity;
            for (int y = 0; y < _h; y++)
            {
                double fy = y / (double)_h;
                // Vertical falloff: aurora concentrated above the midline, fades to bottom.
                double falloff = 1.0 - Math.Min(1.0, Math.Max(0.0, (fy - 0.15) * 1.6));
                falloff *= falloff;
                for (int x = 0; x < _w; x++)
                {
                    double fx = x / (double)_w;
                    double wave = Math.Sin(fx * 6 + _t * 1.1)
                                + Math.Sin(fx * 2.2 - _t * 0.8 + fy * 3)
                                + Math.Sin((fx + fy) * 4 + _t * 0.5);
                    wave = wave * 0.166 + 0.5;
                    double bandY = 0.25 + 0.18 * Math.Sin(_t * 0.4 + fx * 4);
                    double bandWidth = 0.18;
                    double band = Math.Max(0, 1.0 - Math.Abs(fy - bandY) / bandWidth);
                    band *= band;
                    double mix = band * 0.85 + wave * 0.25 * falloff;
                    mix = Math.Min(1.0, mix);

                    // Aurora palette: greens to teals to violets shift over time.
                    double hue = 0.30 + 0.18 * Math.Sin(_t * 0.2 + fx * 1.2);
                    // Bgra32 is straight (not pre-multiplied) alpha. RGB stays at
                    // full color; the alpha channel does the falloff alone — without
                    // this the bottom of the canvas was double-darkened.
                    HsvToBgr(hue, 0.7, mix, out byte b, out byte g, out byte r);
                    int o = (y * _w + x) * 4;
                    _buffer[o + 0] = b;
                    _buffer[o + 1] = g;
                    _buffer[o + 2] = r;
                    _buffer[o + 3] = (byte)(falloff * 0xD0);
                }
            }
            target.WritePixels(new System.Windows.Int32Rect(0, 0, _w, _h),
                _buffer, _w * 4, 0);
        }

        private static void HsvToBgr(double h, double s, double v, out byte b, out byte g, out byte r)
        {
            double hh = h * 6.0;
            int i = (int)Math.Floor(hh);
            double f = hh - i;
            double p = v * (1 - s);
            double q = v * (1 - s * f);
            double t = v * (1 - s * (1 - f));
            double rr = 0, gg = 0, bb = 0;
            switch (((i % 6) + 6) % 6)
            {
                case 0: rr = v; gg = t; bb = p; break;
                case 1: rr = q; gg = v; bb = p; break;
                case 2: rr = p; gg = v; bb = t; break;
                case 3: rr = p; gg = q; bb = v; break;
                case 4: rr = t; gg = p; bb = v; break;
                case 5: rr = v; gg = p; bb = q; break;
            }
            r = (byte)(Math.Clamp(rr, 0, 1) * 255);
            g = (byte)(Math.Clamp(gg, 0, 1) * 255);
            b = (byte)(Math.Clamp(bb, 0, 1) * 255);
        }

        public void Dispose() { _buffer = Array.Empty<byte>(); }
    }
}
