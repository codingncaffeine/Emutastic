using Emutastic.Models.EmuTv;

namespace Emutastic.Services
{
    /// <summary>A resolved pixel rectangle.</summary>
    public readonly struct PxRect
    {
        public double X { get; }
        public double Y { get; }
        public double W { get; }
        public double H { get; }
        public PxRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
    }

    /// <summary>
    /// The responsive layout pass: converts an element's normalized 0–1 geometry into pixels
    /// against the live container size, so ONE layout adapts to every aspect ratio (no per-AR
    /// theme files). Pure (no WPF) so it's unit-testable.
    /// </summary>
    public static class EmuTvLayout
    {
        /// <summary>Normalized <paramref name="pos"/> → pixel point in the container (default 0,0).</summary>
        public static (double x, double y) Point(Vec2? pos, double cw, double ch)
            => ((pos?.X ?? 0) * cw, (pos?.Y ?? 0) * ch);

        /// <summary>Normalized <paramref name="size"/> → pixel size, or null if unset.</summary>
        public static (double w, double h)? SizePx(Vec2? size, double cw, double ch)
            => size is { } s ? (s.X * cw, s.Y * ch) : null;

        /// <summary>Top-left of a box of size (w,h) placed so that <paramref name="origin"/>
        /// (0–1 within the box) sits at point (px,py). Origin defaults to 0,0 (top-left).</summary>
        public static (double left, double top) TopLeft(double px, double py, double w, double h, Vec2? origin)
            => (px - (origin?.X ?? 0) * w, py - (origin?.Y ?? 0) * h);

        /// <summary>Full pixel rect for an element that declares an explicit <c>size</c>;
        /// null when size is content-driven (handled by the renderer after measuring).</summary>
        public static PxRect? RectFromSize(ThemeElement el, double cw, double ch)
        {
            if (SizePx(el.Size, cw, ch) is not { } s) return null;
            var (px, py) = Point(el.Pos, cw, ch);
            var (left, top) = TopLeft(px, py, s.w, s.h, el.Origin);
            return new PxRect(left, top, s.w, s.h);
        }
    }
}
