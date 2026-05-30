using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Emutastic.Configuration;

namespace Emutastic.Services
{
    /// <summary>
    /// Single source of truth for turning an <see cref="AchievementToastStyle"/> into the
    /// visual state of the achievement-toast controls. Used by BOTH the live in-game toast
    /// (<c>EmulatorWindow</c>) and the settings live-preview (<c>PreferencesWindow</c>), so
    /// the two can never drift.
    ///
    /// Visibility semantics: <see cref="ApplyTo"/> sets badge/header visibility from the
    /// STYLE toggles (ShowBadge / ShowHeader). Callers that have per-unlock content rules
    /// (e.g. "no badge URL on this toast", "no header text") may further COLLAPSE afterwards —
    /// they must never force-show, so the effective rule is (ShowX AND has-content).
    ///
    /// Freezable rule: brushes are built in code and assigned to instance properties; we never
    /// attach DynamicResource to a Color/Brush (that breaks hit-testing per project rules).
    /// Theme brushes are looked up by key and assigned by reference (frozen, shared — safe).
    /// </summary>
    public static class ToastStyleRenderer
    {
        // Hardcoded, non-customizable layout constants that reproduce the shipped toast.
        // (Phase-1 audit carry-forward: keep these so the default look is pixel-identical.)
        private const double EdgeMargin = 20;     // gap from the chosen screen corner/edge

        public static void ApplyTo(
            Border root,
            Border badge,
            TextBlock header,
            TextBlock title,
            TextBlock desc,
            TextBlock points,
            AchievementToastStyle? style,
            Func<string, ImageSource?>? imageLoader)
        {
            // Null-guard: a hand-edited "toastStyle": null must never throw into the
            // unlock path (Phase-1 audit SHOULD-FIX #1).
            var s = style ?? new AchievementToastStyle();

            // ── Background ────────────────────────────────────────────────
            root.Background = BuildBackground(s, imageLoader);

            // ── Border + shape ───────────────────────────────────────────
            root.BorderBrush     = ResolveBrush(s.BorderColor, "AccentBrush");
            root.BorderThickness = new Thickness(s.BorderThickness);
            root.CornerRadius    = new CornerRadius(ResolveRadius(s));

            // ── Drop shadow ──────────────────────────────────────────────
            // Direction is fixed at 270 (downward) — no model field, matches the
            // shipped toast.
            root.Effect = s.ShadowEnabled
                ? new DropShadowEffect
                {
                    BlurRadius  = s.ShadowBlur,
                    ShadowDepth = s.ShadowDepth,
                    Direction   = 270,
                    Opacity     = Clamp01(s.ShadowOpacity / 100.0),
                    Color       = ColorFromHex(s.ShadowColor, Colors.Black)
                }
                : null;

            // ── Position (6-anchor) ──────────────────────────────────────
            ApplyPosition(root, s.Position);

            // ── Badge frame + style-driven visibility ────────────────────
            badge.Visibility  = s.ShowBadge ? Visibility.Visible : Visibility.Collapsed;
            badge.BorderBrush = ResolveBrush(s.BadgeFrameColor, "AchievementGoldBrush");

            // ── Header ───────────────────────────────────────────────────
            header.Visibility = s.ShowHeader ? Visibility.Visible : Visibility.Collapsed;
            header.Foreground = ResolveBrush(s.HeaderColor, "AchievementGoldBrush");
            header.FontSize   = s.HeaderSize;

            // ── Title ────────────────────────────────────────────────────
            title.Foreground = ResolveBrush(s.TitleColor, null) ?? Brushes.White;
            title.FontFamily = ResolveFont(s.TitleFont);
            title.FontSize   = s.TitleSize;

            // ── Description ──────────────────────────────────────────────
            desc.Foreground = ResolveBrush(s.DescColor, null) ?? Brushes.White;
            desc.FontFamily = ResolveFont(s.DescFont);
            desc.FontSize   = s.DescSize;

            // ── Points ───────────────────────────────────────────────────
            points.Foreground = ResolveBrush(s.PointsColor, "AchievementGoldBrush");
            points.FontSize   = s.PointsSize;
        }

        /// <summary>Display duration as a TimeSpan, clamped to a sane floor.</summary>
        public static TimeSpan Duration(AchievementToastStyle? style)
        {
            var s = style ?? new AchievementToastStyle();
            double secs = s.DurationSec > 0.5 ? s.DurationSec : 4;
            return TimeSpan.FromSeconds(secs);
        }

        // ── Background builder ───────────────────────────────────────────
        private static Brush BuildBackground(AchievementToastStyle s, Func<string, ImageSource?>? imageLoader)
        {
            // One overall transparency control applied to every background mode.
            // (Gradient stops keep their own baked alpha; this multiplies on top, so the
            // default 100 leaves the shipped gradient untouched.)
            double bgOpacity = Clamp01(s.BackgroundOpacity / 100.0);

            // Image wins when set and loadable.
            if (!string.IsNullOrWhiteSpace(s.BackgroundImage) && imageLoader != null)
            {
                var img = imageLoader(s.BackgroundImage);
                if (img != null)
                {
                    var ib = new ImageBrush(img)
                    {
                        Stretch = Stretch.UniformToFill,
                        Opacity = bgOpacity
                    };
                    // Safe to freeze: imageLoader supplies a frozen ImageSource (LoadBadge
                    // precedent). Freezing keeps the brush cheap and cross-thread-safe.
                    if (ib.CanFreeze) ib.Freeze();
                    return ib;
                }
            }

            // Gradient (default — reproduces the shipped 2-stop horizontal gradient).
            if (s.UseGradient)
            {
                var g = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint   = new Point(1, 0),
                    Opacity    = bgOpacity
                };
                g.GradientStops.Add(new GradientStop(ColorFromHex(s.GradientStart, Color.FromArgb(0xF2, 0x1A, 0x1A, 0x2E)), 0));
                g.GradientStops.Add(new GradientStop(ColorFromHex(s.GradientEnd,   Color.FromArgb(0xC8, 0x1A, 0x1A, 0x2E)), 1));
                if (g.CanFreeze) g.Freeze();
                return g;
            }

            // Solid: the slider drives the brush opacity directly.
            var baseColor = ColorFromHex(s.BackgroundColor, Color.FromRgb(0x1A, 0x1A, 0x2E));
            var solid = new SolidColorBrush(baseColor) { Opacity = bgOpacity };
            if (solid.CanFreeze) solid.Freeze();
            return solid;
        }

        // ── Shape → corner radius ────────────────────────────────────────
        private static double ResolveRadius(AchievementToastStyle s)
        {
            switch ((s.Shape ?? "Card").Trim().ToLowerInvariant())
            {
                case "sharp":   return 0;
                case "rounded": return 20;
                // WPF Border clamps corner radii to fit the box, so a very large value
                // yields a stadium/pill silhouette (radius == height/2) at any size.
                case "pill":    return 1000;
                case "custom":  return Math.Max(0, s.CornerRadius);
                case "card":
                default:        return 12;
            }
        }

        // ── Position → alignment + margin ────────────────────────────────
        private static void ApplyPosition(Border root, string? position)
        {
            HorizontalAlignment h;
            VerticalAlignment v;
            double l = 0, t = 0, r = 0, b = 0;

            switch ((position ?? "TopCenter").Trim().ToLowerInvariant())
            {
                case "topleft":      h = HorizontalAlignment.Left;   v = VerticalAlignment.Top;    l = EdgeMargin; t = EdgeMargin; break;
                case "topright":     h = HorizontalAlignment.Right;  v = VerticalAlignment.Top;    r = EdgeMargin; t = EdgeMargin; break;
                case "bottomleft":   h = HorizontalAlignment.Left;   v = VerticalAlignment.Bottom; l = EdgeMargin; b = EdgeMargin; break;
                case "bottomcenter": h = HorizontalAlignment.Center; v = VerticalAlignment.Bottom; b = EdgeMargin; break;
                case "bottomright":  h = HorizontalAlignment.Right;  v = VerticalAlignment.Bottom; r = EdgeMargin; b = EdgeMargin; break;
                case "topcenter":
                default:             h = HorizontalAlignment.Center; v = VerticalAlignment.Top;    t = EdgeMargin; break;
            }

            root.HorizontalAlignment = h;
            root.VerticalAlignment   = v;
            root.Margin              = new Thickness(l, t, r, b);
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Resolve a color spec to a Brush. Empty/whitespace = "use the live theme brush"
        /// identified by <paramref name="themeKey"/> (so an untouched toast tracks the theme);
        /// otherwise parse the hex. Returns null only when empty AND no theme key is given.
        /// </summary>
        private static Brush? ResolveBrush(string? hex, string? themeKey)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                if (themeKey != null && Application.Current?.TryFindResource(themeKey) is Brush themed)
                    return themed;
                return null;
            }

            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex.Trim())!;
                var brush = new SolidColorBrush(c);
                if (brush.CanFreeze) brush.Freeze();
                return brush;
            }
            catch
            {
                // Bad hex falls back to the theme brush if available, else null.
                if (themeKey != null && Application.Current?.TryFindResource(themeKey) is Brush themed)
                    return themed;
                return null;
            }
        }

        private static Color ColorFromHex(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            try { return (Color)ColorConverter.ConvertFromString(hex.Trim())!; }
            catch { return fallback; }
        }

        /// <summary>Empty = the app PrimaryFont chain; otherwise the named family
        /// (WPF substitutes gracefully if the family isn't installed on this machine).</summary>
        private static FontFamily ResolveFont(string? family)
        {
            if (string.IsNullOrWhiteSpace(family))
            {
                if (Application.Current?.TryFindResource("PrimaryFont") is FontFamily pf)
                    return pf;
                return new FontFamily("Segoe UI");
            }
            try { return new FontFamily(family); }
            catch { return new FontFamily("Segoe UI"); }
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
