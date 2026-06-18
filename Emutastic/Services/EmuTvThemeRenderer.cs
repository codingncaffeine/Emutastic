using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Emutastic.Models.EmuTv;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Emutastic.Services
{
    /// <summary>The realized WPF tree for one theme view, plus a name→element lookup.</summary>
    public sealed class RenderedView
    {
        public Canvas Root { get; } = new();
        public ThemeViewKind Kind { get; init; }
        public Dictionary<string, FrameworkElement> Named { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The first video element's Image, for the host to drive live playback into.</summary>
        public Image? VideoTarget { get; set; }

        public T? Find<T>(string name) where T : FrameworkElement
            => Named.TryGetValue(name, out var fe) ? fe as T : null;

        // Every FrameworkElement in the tree (the faithful engine binds data during render, so this
        // is mostly diagnostic now, but kept for any host-side slot filling).
        public IEnumerable<FrameworkElement> AllElements()
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (d is FrameworkElement fe) yield return fe;
                switch (d)
                {
                    case Panel p:           foreach (UIElement c in p.Children) stack.Push(c); break;
                    case Border b:          if (b.Child != null) stack.Push(b.Child); break;
                    case Viewbox vb:        if (vb.Child != null) stack.Push(vb.Child); break;
                    case ContentControl cc: if (cc.Content is DependencyObject cd) stack.Push(cd); break;
                    case Decorator dec:     if (dec.Child != null) stack.Push(dec.Child); break;
                }
            }
        }
    }

    // ── Data the host snapshots from the live library for the engine to bind ──────
    public sealed class ThemeItemData
    {
        public IReadOnlyList<ThemeSystemEntry> Systems { get; init; } = Array.Empty<ThemeSystemEntry>();
        public int SelectedSystem { get; init; }
        public IReadOnlyList<ThemeGameEntry> Games { get; init; } = Array.Empty<ThemeGameEntry>();
        public int SelectedGame { get; init; }
        public string SystemName { get; init; } = "";   // selected console display name
        public string SystemGameCount { get; init; } = ""; // e.g. "42 games"
        public string HelpText { get; init; } = "";
    }

    public sealed class ThemeSystemEntry
    {
        public string Label { get; init; } = "";
        public string EsName { get; init; } = "_default";
    }

    public sealed class ThemeGameEntry
    {
        public string Label { get; init; } = "";
        public string? CoverPath { get; init; }
        public string? ScreenshotPath { get; init; }
        public string? Box3dPath { get; init; }
        public string? MarqueePath { get; init; }
        public string? Developer { get; init; }
        public string? Publisher { get; init; }
        public string? Genre { get; init; }
        public string? Description { get; init; }
        public int Year { get; init; }
        public string? RatingStars { get; init; }
        public double Rating { get; init; }   // 0..1
        public bool Favorite { get; init; }
        public bool Completed { get; init; }
        public bool Kidgame { get; init; }
        public bool Broken { get; init; }
        public int Players { get; init; }
    }

    /// <summary>
    /// Faithful, data-driven renderer for ES-DE theme views. Every element is placed by the ES-DE
    /// transform model (pos/size/origin/rotation normalized to the screen) and rendered per its
    /// declared type — carousels honour their <c>type</c> (horizontal/vertical/horizontalWheel/
    /// verticalWheel), images honour size/maxSize/cropSize, text binds to game/system data. No
    /// EmuTV-semantic assumptions are baked in, so arbitrary ES-DE themes render from data alone.
    /// </summary>
    public sealed class EmuTvThemeRenderer
    {
        private readonly string _themeRoot;
        private double _w = 1920, _h = 1080;
        private string? _systemTheme;          // selected console ES-DE name, for ${system.theme}
        private ThemeItemData? _items;
        private ThemeViewKind _viewKind;

        public EmuTvThemeRenderer(string themeRoot) => _themeRoot = Path.GetFullPath(themeRoot);

        public RenderedView Render(ThemeView view, double width, double height,
            string? systemTheme = null, ThemeItemData? items = null)
        {
            _w = width > 0 ? width : 1920;
            _h = height > 0 ? height : 1080;
            _systemTheme = systemTheme;
            _items = items;
            _viewKind = view.Kind;

            var rv = new RenderedView { Kind = view.Kind };
            rv.Root.Width = _w;
            rv.Root.Height = _h;

            // Render in ascending zIndex (ES-DE default order: image/video 30 … carousel 50, help on top).
            // The WHOLE per-element body is guarded: one bad element must never blank the entire view.
            int placed = 0, failed = 0;
            foreach (var el in view.Elements.OrderBy(EffectiveZ))
            {
                try
                {
                    if (el.Visible == false) continue;
                    var fe = BuildElement(el);
                    if (fe == null) continue;
                    if (el.Opacity is { } op) fe.Opacity = Math.Clamp(op, 0, 1) * fe.Opacity;
                    Place(fe, el);
                    Panel.SetZIndex(fe, (int)Math.Round(EffectiveZ(el)));
                    rv.Root.Children.Add(fe);
                    if (!string.IsNullOrWhiteSpace(el.Name)) rv.Named[el.Name] = fe;
                    if (el is VideoElement && rv.VideoTarget == null) rv.VideoTarget = _pendingVideoImage;
                    placed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    RenderLog($"ELEM-FAIL {el.GetType().Name.Replace("Element", "")} '{el.Name}': {ex.GetType().Name}: {ex.Message}");
                }
            }
            RenderLog($"view={view.Kind} elems={view.Elements.Count} placed={placed} failed={failed}");
            return rv;
        }

        // Temporary render diagnostics (theme bring-up). Bounded best-effort; safe to leave off in prod.
        private static void RenderLog(string msg)
        {
            try { File.AppendAllText(@"D:\Emutastic Data\Logs\emutv-render.log", $"{DateTime.Now:HH:mm:ss} {msg}\n"); }
            catch { }
        }

        // ES-DE default zIndex per element type (THEMES.md "Element rendering order").
        private static double EffectiveZ(ThemeElement el) => el.ZIndex ?? el switch
        {
            ImageElement or VideoElement => 30,
            BadgesElement => 35,
            TextElement or DateTimeElement => 40,
            RatingElement or GamelistInfoElement => 45,
            AnimationElement => 35,
            CarouselElement or GridElement or TextListElement => 50,
            HelpSystemElement => 10000,         // always on top
            _ => 40,
        };

        // ── Universal transform: pos/size/origin/rotation normalized to the screen ──
        private void Place(FrameworkElement fe, ThemeElement el)
        {
            double bw = fe.Width, bh = fe.Height;
            if (double.IsNaN(bw) || double.IsNaN(bh))
            {
                fe.Measure(new Size(_w, _h));
                if (double.IsNaN(bw)) bw = fe.DesiredSize.Width;
                if (double.IsNaN(bh)) bh = fe.DesiredSize.Height;
            }

            double px = (el.Pos?.X ?? 0) * _w;
            double py = (el.Pos?.Y ?? 0) * _h;
            double ox = el.Origin?.X ?? 0;
            double oy = el.Origin?.Y ?? 0;

            Canvas.SetLeft(fe, px - ox * bw);
            Canvas.SetTop(fe, py - oy * bh);

            if (el.Rotation is { } rot && Math.Abs(rot) > 0.01)
            {
                var ro = el.RotationOrigin ?? new Vec2(0.5, 0.5);
                fe.RenderTransformOrigin = new Point(ro.X, ro.Y);
                fe.RenderTransform = new RotateTransform(rot);
            }
        }

        // ── Element dispatch ────────────────────────────────────────────────────
        private FrameworkElement? BuildElement(ThemeElement el) => el switch
        {
            ImageElement im      => BuildImage(im),
            VideoElement vd      => BuildVideo(vd),
            TextElement tx       => BuildText(tx),
            RatingElement ra     => BuildRating(ra),
            CarouselElement ca   => BuildCarousel(ca),
            GridElement gr       => BuildGrid(gr),
            TextListElement tl   => BuildTextList(tl),
            HelpSystemElement hs => BuildHelp(hs),
            DateTimeElement dt   => BuildDateTime(dt),
            GamelistInfoElement gi => BuildGamelistInfo(gi),
            AnimationElement an  => BuildAnimation(an),
            BadgesElement bg     => BuildBadges(bg),
            _ => null,
        };

        // ════════════════════════ secondary elements ════════════════════════════

        private FrameworkElement? BuildImage(ImageElement im)
        {
            // Resolve the source: explicit path, bound game art (imageType), or the default fallback.
            ImageSource? src = null;
            if (!string.IsNullOrEmpty(im.Path)) src = LoadImage(im.Path);
            if (src == null && im.ImageTypes.Count > 0) src = ResolveGameArt(SelectedGame, im.ImageTypes);
            if (src == null && !string.IsNullOrEmpty(im.DefaultImage)) src = LoadImage(im.DefaultImage);

            bool hasColor = im.Color != null || im.ColorEnd != null;

            // Pure colour fill (a panel / gradient with no usable image and no media binding).
            if (src == null && hasColor && im.ImageTypes.Count == 0 && string.IsNullOrEmpty(im.Path))
            {
                double cw = (im.Size?.X ?? 1) * _w, ch = (im.Size?.Y ?? 1) * _h;
                return new Border { Width = cw, Height = ch, Background = BuildBrush(im.Color, im.ColorEnd, im.Gradient) };
            }
            if (src == null) return null;

            if (im.Tile == true) return BuildTiledImage(im, src);

            double aspect = AspectOf(src);
            var (boxW, boxH) = ImageBox(im.Size, im.MaxSize, im.CropSize, aspect);
            bool crop = im.CropSize != null && im.Size == null;

            var img = new Image
            {
                Source = src,
                Width = boxW,
                Height = boxH,
                Stretch = crop ? Stretch.UniformToFill
                        : (im.Size is { X: > 0, Y: > 0 } ? Stretch.Fill : Stretch.Uniform),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(img, ScalingOf(im.Interpolation));

            // Colour tint (multiply). ES applies <color> as a per-pixel multiply.
            if (hasColor)
            {
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = boxW,
                    Height = boxH,
                    Fill = BuildBrush(im.Color, im.ColorEnd, im.Gradient),
                    OpacityMask = new ImageBrush(src)
                    {
                        Stretch = crop ? Stretch.UniformToFill
                                : (im.Size is { X: > 0, Y: > 0 } ? Stretch.Fill : Stretch.Uniform),
                    },
                };
                return Boxed(rect, boxW, boxH, crop);
            }

            return Boxed(img, boxW, boxH, crop);
        }

        // Wrap in a clipping box of the content size (so cropSize crops and origin offsets are exact).
        private static FrameworkElement Boxed(FrameworkElement inner, double w, double h, bool clip)
        {
            if (!clip) return inner;
            return new Border { Width = w, Height = h, ClipToBounds = true, Child = inner };
        }

        // Tiled (repeated) image — backgrounds / bands / footers. ES-DE's <tile> repeats the source at
        // tileSize across the element area instead of stretching it.
        private FrameworkElement BuildTiledImage(ImageElement im, ImageSource src)
        {
            double tw = (im.Size?.X ?? 1) * _w, th = (im.Size?.Y ?? 1) * _h;
            double cellW = (im.TileSize?.X ?? 0) * _w, cellH = (im.TileSize?.Y ?? 0) * _h;
            if (cellW <= 0 || cellH <= 0)
            {
                if (src is BitmapSource bs && bs.PixelWidth > 0) { cellW = bs.PixelWidth; cellH = bs.PixelHeight; }
                else { cellH = 0.1 * _h; cellW = cellH * AspectOf(src); }
            }
            var tiled = new ImageBrush(src)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, cellW, cellH),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
            };
            // ES-DE multiplies the tile by <color>. The very common "tiled white/neutral spacer + a
            // background colour" pattern (art-book-next) is effectively a solid colour fill; reproduce it
            // by masking the colour brush with the tile so the colour shows wherever the spacer is opaque.
            if (im.Color != null || im.ColorEnd != null)
                return new System.Windows.Shapes.Rectangle
                {
                    Width = tw, Height = th,
                    Fill = BuildBrush(im.Color, im.ColorEnd, im.Gradient),
                    OpacityMask = tiled,
                };
            return new System.Windows.Shapes.Rectangle { Width = tw, Height = th, Fill = tiled };
        }

        private FrameworkElement? BuildVideo(VideoElement v)
        {
            // Static poster (imageType art or defaultImage); the host swaps in live video frames.
            // Always realize the Image even with no poster so it can serve as a live-video target.
            ImageSource? src = v.ImageTypes.Count > 0 ? ResolveGameArt(SelectedGame, v.ImageTypes) : null;
            src ??= !string.IsNullOrEmpty(v.DefaultImage) ? LoadImage(v.DefaultImage) : null;
            src ??= ResolveGameArt(SelectedGame, new List<string> { "screenshot", "titlescreen", "cover" });

            double aspect = src != null ? AspectOf(src) : 16.0 / 9.0;
            var (boxW, boxH) = ImageBox(v.Size, v.MaxSize, v.CropSize, aspect);
            bool crop = v.CropSize != null && v.Size == null;
            var img = new Image
            {
                Source = src, Width = boxW, Height = boxH,
                Stretch = crop ? Stretch.UniformToFill
                        : (v.Size is { X: > 0, Y: > 0 } ? Stretch.Fill : Stretch.Uniform),
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            _pendingVideoImage = img;     // host drives live playback into this Image
            return Boxed(img, boxW, boxH, crop);
        }

        private FrameworkElement? BuildText(TextElement tx)
        {
            string text = ResolveText(tx);
            if (text.Contains(":space")) text = text.Replace(":space:", " ").Replace(":space", " ");  // ES-DE blank token
            if (text.Contains("${"))                      // strip residual unresolved tokens, keep the rest
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\$\{[^}]*\}", "");
            if (string.IsNullOrWhiteSpace(text)) return null;

            text = ApplyCase(text, tx.LetterCase);
            double fontPx = Math.Max(1, (tx.FontSize ?? 0.045) * _h);
            double lineSpacing = tx.LineSpacing ?? 1.5;
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontPx,
                // ES-DE's <text> default colour is BLACK (000000FF); themes set a light colour explicitly.
                Foreground = new SolidColorBrush(ColorFromHex(tx.Color ?? "000000FF")),
                TextAlignment = tx.Alignment switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left },
                LineHeight = fontPx * lineSpacing,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            var font = LoadFont(tx.FontPath);
            if (font != null) tb.FontFamily = font;
            if (!string.IsNullOrEmpty(tx.BackgroundColor) && ColorFromHex(tx.BackgroundColor).A > 0)
                tb.Background = new SolidColorBrush(ColorFromHex(tx.BackgroundColor));

            // size: "0 0" auto one line; "w 0" wrap; "w h" box (truncate single line).
            double sw = (tx.Size?.X ?? 0) * _w, sh = (tx.Size?.Y ?? 0) * _h;
            if (sw > 0)
            {
                tb.Width = sw;
                if (sh > 0 && sh <= fontPx * 1.4)
                    tb.TextTrimming = TextTrimming.CharacterEllipsis;   // single-line box
                else
                {
                    tb.TextWrapping = TextWrapping.Wrap;                 // multi-line / description
                    if (sh > 0) tb.Height = sh;
                }
            }
            if (tx.Container == true && sw > 0) tb.TextWrapping = TextWrapping.Wrap;
            return tb;
        }

        private FrameworkElement? BuildRating(RatingElement ra)
        {
            var g = SelectedGame;
            double value = Math.Clamp(g?.Rating ?? 0, 0, 1);   // 0..1
            // size: Y axis takes precedence; height drives the icon size.
            double h = (ra.Size?.Y ?? 0.06) * _h;
            if (h <= 0) h = (ra.Size?.X ?? 0.2) * _w / 5.0;
            double star = Math.Max(1, h);
            double fullW = star * 5;

            var filledSrc = !string.IsNullOrEmpty(ra.FilledImage) ? LoadImage(ra.FilledImage) : null;
            var unfilledSrc = !string.IsNullOrEmpty(ra.UnfilledImage) ? LoadImage(ra.UnfilledImage) : null;
            string? tintHex = NonWhite(ra.Color);

            if (filledSrc != null || unfilledSrc != null)
            {
                // Fractional fill: unfilled row underneath, filled row on top clipped to the value.
                var grid = new Grid { Width = fullW, Height = star };
                if (unfilledSrc != null) grid.Children.Add(StarRow(unfilledSrc, star, fullW, tintHex));
                if (filledSrc != null)
                {
                    var filled = StarRow(filledSrc, star, fullW, tintHex);
                    filled.HorizontalAlignment = HorizontalAlignment.Left;
                    filled.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, value * fullW), star));
                    grid.Children.Add(filled);
                }
                return grid;
            }

            // No rating graphics supplied — fractional glyph stars.
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Width = fullW, Height = star };
            var brush = new SolidColorBrush(tintHex != null ? ColorFromHex(tintHex) : Colors.White);
            double filledStars = value * 5.0;
            for (int i = 0; i < 5; i++)
                panel.Children.Add(new TextBlock
                {
                    Text = i < filledStars ? "★" : "☆", FontSize = star * 0.9,
                    Width = star, TextAlignment = TextAlignment.Center, Foreground = brush,
                });
            return panel;
        }

        // A horizontal row of 5 identical star images, optionally colourised (white = no tint).
        private FrameworkElement StarRow(ImageSource src, double star, double fullW, string? tintHex)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Width = fullW, Height = star };
            var tint = tintHex != null ? new SolidColorBrush(ColorFromHex(tintHex)) : null;
            for (int i = 0; i < 5; i++)
            {
                if (tint != null)
                    row.Children.Add(new System.Windows.Shapes.Rectangle
                    { Width = star, Height = star, Fill = tint, OpacityMask = new ImageBrush(src) { Stretch = Stretch.Uniform } });
                else
                {
                    var img = new Image { Source = src, Width = star, Height = star, Stretch = Stretch.Uniform };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    row.Children.Add(img);
                }
            }
            return row;
        }

        private FrameworkElement? BuildHelp(HelpSystemElement hs)
        {
            // scope: shared/view show during normal navigation; menu = only while a menu is open;
            // none = hidden. We don't render the ES-DE menu, so menu/none-scoped help bars must NOT
            // appear in the view — otherwise themes that add a menu-styling helpsystem (art-book-next)
            // render a duplicate prompt row.
            string scope = (hs.Scope ?? "shared").ToLowerInvariant();
            if (scope is "menu" or "none") return null;

            string text = _items?.HelpText ?? "";
            if (string.IsNullOrEmpty(text)) return null;
            text = ApplyCase(text, hs.LetterCase ?? "uppercase");      // ES-DE helpsystem default is uppercase
            double fontPx = Math.Max(10, (hs.FontSize ?? 0.035) * _h);
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontPx,
                // ES-DE helpsystem default colour is grey 777777FF.
                Foreground = new SolidColorBrush(ColorFromHex(hs.TextColor ?? "777777FF")),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Left,
            };
            var font = LoadFont(hs.FontPath);
            if (font != null) tb.FontFamily = font;
            if (!string.IsNullOrEmpty(hs.BackgroundColor) && ColorFromHex(hs.BackgroundColor).A > 0)
                tb.Background = new SolidColorBrush(ColorFromHex(hs.BackgroundColor));
            return tb;
        }

        private FrameworkElement? BuildDateTime(DateTimeElement dt)
        {
            var g = SelectedGame;
            if (g == null) return null;
            // We carry a release year, not a full date/time history. Map releasedate → year; fields we
            // don't track (lastplayed, playtime) are left blank.
            string text = dt.Metadata?.ToLowerInvariant() switch
            {
                "lastplayed" or "playtime" => "",
                _ => g.Year > 0 ? g.Year.ToString() : "",
            };
            if (string.IsNullOrEmpty(text)) return null;
            double fontPx = Math.Max(1, (dt.FontSize ?? 0.04) * _h);
            return new TextBlock
            {
                Text = text,
                FontSize = fontPx,
                Foreground = new SolidColorBrush(ColorFromHex(dt.Color ?? "FFFFFFFF")),
                TextAlignment = TextAlignment.Center,
            };
        }

        private FrameworkElement? BuildGamelistInfo(GamelistInfoElement gi)
        {
            string text = _items?.SystemGameCount ?? "";
            if (string.IsNullOrEmpty(text)) return null;
            double fontPx = Math.Max(1, (gi.FontSize ?? 0.045) * _h);
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontPx,
                Foreground = new SolidColorBrush(ColorFromHex(gi.Color ?? "000000FF")),
                TextAlignment = gi.Alignment switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left },
            };
            var font = LoadFont(gi.FontPath);
            if (font != null) tb.FontFamily = font;
            if (!string.IsNullOrEmpty(gi.BackgroundColor) && ColorFromHex(gi.BackgroundColor).A > 0)
                tb.Background = new SolidColorBrush(ColorFromHex(gi.BackgroundColor));
            return tb;
        }

        // GIF/Lottie animation — rendered as a still (first GIF frame) for preview fidelity. Lottie
        // (.json) has no decodable still frame and is skipped.
        private FrameworkElement? BuildAnimation(AnimationElement an)
        {
            if (string.IsNullOrEmpty(an.Path) || an.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
            var src = LoadImage(an.Path);
            if (src == null) return null;
            double aspect = AspectOf(src);
            var (boxW, boxH) = ImageBox(an.Size, an.MaxSize, null, aspect);
            var img = new Image
            {
                Source = src, Width = boxW, Height = boxH,
                Stretch = an.Size is { X: > 0, Y: > 0 } ? Stretch.Fill : Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        // Metadata badges (gamelist) — render the active game flags using the theme's customBadgeIcon
        // set. We ship no default badge icons, so a slot the theme didn't give an icon for is skipped.
        private FrameworkElement? BuildBadges(BadgesElement bd)
        {
            var g = SelectedGame;
            if (g == null) return null;

            var active = new List<string>();
            foreach (var slot in SplitSlots(bd.Slots))
            {
                bool on = slot switch
                {
                    "favorite" => g.Favorite,
                    "completed" => g.Completed,
                    "kidgame" => g.Kidgame,
                    "broken" => g.Broken,
                    _ => false,            // folder/collection/controller/altemulator/manual not tracked yet
                };
                if (on && bd.CustomBadgeIcons.ContainsKey(slot)) active.Add(slot);
            }
            if (active.Count == 0) return null;

            double areaW = (bd.Size?.X ?? 0.15) * _w, areaH = (bd.Size?.Y ?? 0.20) * _h;
            int perLine = Math.Max(1, bd.ItemsPerLine ?? 4);
            int lines = Math.Max(1, bd.Lines ?? 3);
            double mx = (bd.ItemMargin?.X ?? 0.01) * _w, my = (bd.ItemMargin?.Y ?? 0.01) * _h;
            if (mx < 0) mx = my; else if (my < 0) my = mx;
            double cell = Math.Max(1, Math.Min((areaW - mx * (perLine - 1)) / perLine, areaH / lines));

            string? tintHex = NonWhite(bd.IconColor);
            var tint = tintHex != null ? BuildBrush(tintHex, bd.IconColorEnd ?? tintHex, GradientType.None) : null;
            bool column = string.Equals(bd.Direction, "column", StringComparison.OrdinalIgnoreCase);

            var canvas = new Canvas { Width = areaW, Height = areaH };
            for (int i = 0; i < active.Count; i++)
            {
                var src = LoadImage(bd.CustomBadgeIcons[active[i]]);
                if (src == null) continue;
                int row = column ? i % lines : i / perLine;
                int col = column ? i / lines : i % perLine;
                FrameworkElement icon;
                if (tint != null)
                    icon = new System.Windows.Shapes.Rectangle
                    { Width = cell, Height = cell, Fill = tint, OpacityMask = new ImageBrush(src) { Stretch = Stretch.Uniform } };
                else
                {
                    var img = new Image { Source = src, Width = cell, Height = cell, Stretch = Stretch.Uniform };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    icon = img;
                }
                Canvas.SetLeft(icon, col * (cell + mx));
                Canvas.SetTop(icon, row * (cell + my));
                canvas.Children.Add(icon);
            }
            return canvas.Children.Count > 0 ? canvas : null;
        }

        private static readonly string[] AllBadgeSlots =
            { "collection", "folder", "favorite", "completed", "kidgame", "broken", "controller", "altemulator", "manual" };
        private static List<string> SplitSlots(string? slots)
        {
            if (string.IsNullOrWhiteSpace(slots)) return new List<string>(AllBadgeSlots);
            var list = new List<string>();
            foreach (var p in slots.Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = p.Trim().ToLowerInvariant();
                if (t == "all") return new List<string>(AllBadgeSlots);
                list.Add(t);
            }
            return list;
        }

        // ════════════════════════ primary elements ══════════════════════════════

        private FrameworkElement? BuildCarousel(CarouselElement c)
        {
            _currentStatic = c.StaticImage; _currentDefault = c.DefaultImage; _currentImageTypes = c.ImageTypes;
            _currentFont = LoadFont(c.FontPath);
            _currentScaling = ScalingOf(c.ImageInterpolation);
            _currentImageColor = c.ImageColor; _currentImageColorEnd = c.ImageColorEnd;
            _currentImageSelectedColor = c.ImageSelectedColor; _currentImageGradient = c.ImageGradient;
            int count = ItemCount();
            if (count == 0) return null;
            int sel = Math.Clamp(SelectedIndex(), 0, count - 1);

            double areaW = (c.Size?.X ?? 1) * _w;
            double areaH = (c.Size?.Y ?? 0.2324) * _h;
            var area = new Canvas { Width = areaW, Height = areaH, ClipToBounds = true };

            // Optional background panel.
            if (c.Color != null && ColorFromHex(c.Color).A > 0)
                area.Children.Add(new System.Windows.Shapes.Rectangle
                { Width = areaW, Height = areaH, Fill = BuildBrush(c.Color, c.ColorEnd ?? c.Color, c.Gradient) });

            var itemSize = c.ItemSize ?? new Vec2(0.25, 0.155);
            double itemW = itemSize.X * _w, itemH = itemSize.Y * _h;
            double itemScale = c.ItemScale ?? 1.2;
            double unfocOpacity = c.UnfocusedItemOpacity ?? 0.5;
            string fit = c.ImageFit ?? "contain";
            Color textColor = ColorFromHex(c.TextColor ?? "000000FF");
            double fontPx = Math.Max(1, (c.FontSize ?? 0.085) * _h);

            if (c.Type is CarouselType.HorizontalWheel or CarouselType.VerticalWheel)
                LayoutWheel(area, c, count, sel, itemW, itemH, itemScale, unfocOpacity, fit, textColor, fontPx, areaW, areaH);
            else
                LayoutStrip(area, c, count, sel, itemW, itemH, itemScale, unfocOpacity, fit, textColor, fontPx, areaW, areaH);

            return area;
        }

        // horizontal / vertical: items evenly spaced (maxItemCount across the area), selected centered.
        private void LayoutStrip(Canvas area, CarouselElement c, int count, int sel,
            double itemW, double itemH, double itemScale, double unfocOpacity, string fit,
            Color textColor, double fontPx, double areaW, double areaH)
        {
            bool horiz = c.Type == CarouselType.Horizontal;
            double maxItems = Math.Clamp(c.MaxItemCount ?? 3, 0.5, 30);
            double pitch = (horiz ? areaW : areaH) / maxItems;
            double hOff = (c.HorizontalOffset ?? 0) * areaW;
            double vOff = (c.VerticalOffset ?? 0) * areaH;
            double centerX = areaW / 2 + hOff;
            double centerY = areaH / 2 + vOff;

            // How many items reach beyond the centre before leaving the area.
            int span = (int)Math.Ceiling(maxItems / 2) + 1;
            for (int k = -span; k <= span; k++)
            {
                int idx = sel + k;
                if (idx < 0 || idx >= count) continue;
                bool selected = k == 0;
                double scale = selected ? itemScale : 1.0;
                double iw = itemW * scale, ih = itemH * scale;
                double cx, cy;
                if (horiz) { cx = centerX + k * pitch; cy = ItemAxisY(c, areaH, ih); }
                else       { cy = centerY + k * pitch; cx = ItemAxisX(c, areaW, iw); }

                var item = MakeItem(idx, iw, ih, fit, selected, textColor, fontPx, c.Text, c.LetterCase);
                item.Opacity = selected ? 1.0 : unfocOpacity;
                Canvas.SetLeft(item, cx - iw / 2);
                Canvas.SetTop(item, cy - ih / 2);
                Panel.SetZIndex(item, selected ? 100 : 50 - Math.Abs(k));
                area.Children.Add(item);
            }
        }

        // horizontalWheel / verticalWheel: items on an arc rotated around a pivot off to the side.
        private void LayoutWheel(Canvas area, CarouselElement c, int count, int sel,
            double itemW, double itemH, double itemScale, double unfocOpacity, string fit,
            Color textColor, double fontPx, double areaW, double areaH)
        {
            bool vertical = c.Type == CarouselType.VerticalWheel;
            int before = Math.Clamp(c.ItemsBeforeCenter ?? 8, 0, 20);
            int after = Math.Clamp(c.ItemsAfterCenter ?? 8, 0, 20);
            double itemRot = c.ItemRotation ?? 7.5;
            var rotOrigin = c.ItemRotationOrigin ?? new Vec2(-3, 0.5);
            bool axisHoriz = c.ItemAxisHorizontal ?? false;
            double hOff = (c.HorizontalOffset ?? 0) * areaW;
            double vOff = (c.VerticalOffset ?? 0) * areaH;

            // Selected item centre, aligned within the area.
            double selCx = AlignFrac(c.WheelHorizontalAlignment, 0.5) * areaW + hOff;
            double selCy = AlignFrac(c.WheelVerticalAlignment, 0.5) * areaH + vOff;

            // Pivot: rotOrigin.X is the distance from the item's LEFT edge to the wheel centre in
            // multiples of itemW (negative → wheel to the left, positive → to the right).
            double pivotX, pivotY;
            if (vertical) { pivotX = (selCx - itemW / 2) + rotOrigin.X * itemW; pivotY = selCy + (rotOrigin.Y - 0.5) * itemH; }
            else          { pivotX = selCx + (rotOrigin.Y - 0.5) * itemW;       pivotY = (selCy - itemH / 2) + rotOrigin.X * itemH; }

            for (int k = -before; k <= after; k++)
            {
                int idx = sel + k;
                if (idx < 0 || idx >= count) continue;
                bool selected = k == 0;
                double theta = k * itemRot;                       // degrees
                double rad = theta * Math.PI / 180.0;
                double dx = selCx - pivotX, dy = selCy - pivotY;
                double cx = pivotX + dx * Math.Cos(rad) - dy * Math.Sin(rad);
                double cy = pivotY + dx * Math.Sin(rad) + dy * Math.Cos(rad);

                double scale = selected ? itemScale : 1.0;
                double iw = itemW * scale, ih = itemH * scale;
                var item = MakeItem(idx, iw, ih, fit, selected, textColor, fontPx, c.Text, c.LetterCase);
                item.Opacity = selected ? 1.0 : unfocOpacity;
                Canvas.SetLeft(item, cx - iw / 2);
                Canvas.SetTop(item, cy - ih / 2);
                if (!axisHoriz && Math.Abs(theta) > 0.01)
                {
                    item.RenderTransformOrigin = new Point(0.5, 0.5);
                    item.RenderTransform = new RotateTransform(theta);
                }
                Panel.SetZIndex(item, selected ? 100 : 50 - Math.Abs(k));
                area.Children.Add(item);
            }
        }

        private FrameworkElement? BuildGrid(GridElement g)
        {
            _currentStatic = g.StaticImage; _currentDefault = g.DefaultImage; _currentImageTypes = g.ImageTypes;
            _currentFont = LoadFont(g.FontPath);
            _currentScaling = BitmapScalingMode.HighQuality;
            _currentImageColor = null; _currentImageColorEnd = null; _currentImageSelectedColor = null;
            _currentImageGradient = GradientType.None;
            int count = ItemCount();
            if (count == 0) return null;
            int sel = Math.Clamp(SelectedIndex(), 0, count - 1);

            double areaW = (g.Size?.X ?? 1) * _w;
            double areaH = (g.Size?.Y ?? 0.8) * _h;
            var itemSize = g.ItemSize ?? new Vec2(0.15, 0.25);
            double itemW = itemSize.X * _w, itemH = itemSize.Y * _h;
            var spacing = g.ItemSpacing ?? new Vec2(0.01, 0.01);
            double spX = spacing.X * _w, spY = spacing.Y * _h;
            double itemScale = g.ItemScale ?? 1.05;
            double unfoc = g.UnfocusedItemOpacity ?? 1.0;
            string fit = g.ImageFit ?? "contain";
            Color textColor = ColorFromHex(g.TextColor ?? "000000FF");
            double fontPx = Math.Max(1, (g.FontSize ?? 0.045) * _h);

            int cols = Math.Max(1, (int)Math.Floor((areaW + spX) / (itemW + spX)));
            int rows = Math.Max(1, (int)Math.Floor((areaH + spY) / (itemH + spY)));
            int perPage = cols * rows;

            var area = new Canvas { Width = areaW, Height = areaH, ClipToBounds = true };
            // Scroll so the selected item's page/row is visible.
            int firstRow = Math.Max(0, (sel / cols) - (rows - 1) / 2);
            int firstIdx = firstRow * cols;
            for (int n = 0; n < perPage && firstIdx + n < count; n++)
            {
                int idx = firstIdx + n;
                int col = n % cols, row = n / cols;
                bool selected = idx == sel;
                double scale = selected ? itemScale : 1.0;
                double iw = itemW * scale, ih = itemH * scale;
                double cellX = col * (itemW + spX) + itemW / 2;
                double cellY = row * (itemH + spY) + itemH / 2;
                // Selector highlight behind the focused item so the selection is visible.
                if (selected && g.SelectorColor != null && ColorFromHex(g.SelectorColor).A > 0)
                {
                    var selRect = new System.Windows.Shapes.Rectangle
                    {
                        Width = iw + 6, Height = ih + 6, RadiusX = 4, RadiusY = 4,
                        Fill = new SolidColorBrush(ColorFromHex(g.SelectorColor)),
                    };
                    Canvas.SetLeft(selRect, cellX - iw / 2 - 3);
                    Canvas.SetTop(selRect, cellY - ih / 2 - 3);
                    Panel.SetZIndex(selRect, 99);
                    area.Children.Add(selRect);
                }
                var item = MakeItem(idx, iw, ih, fit, selected, textColor, fontPx, g.Text, g.LetterCase);
                item.Opacity = selected ? 1.0 : unfoc;
                Canvas.SetLeft(item, cellX - iw / 2);
                Canvas.SetTop(item, cellY - ih / 2);
                Panel.SetZIndex(item, selected ? 100 : 50);
                area.Children.Add(item);
            }
            return area;
        }

        private FrameworkElement? BuildTextList(TextListElement t)
        {
            int count = ItemCount();
            if (count == 0) return null;
            int sel = Math.Clamp(SelectedIndex(), 0, count - 1);

            double areaW = (t.Size?.X ?? 1) * _w;
            double areaH = (t.Size?.Y ?? 0.8) * _h;
            double fontPx = Math.Max(1, (t.FontSize ?? 0.045) * _h);
            double lineSpacing = t.LineSpacing ?? 1.5;
            double rowH = fontPx * lineSpacing;
            int rows = Math.Max(1, (int)(areaH / rowH));

            var primary = new SolidColorBrush(ColorFromHex(t.PrimaryColor ?? "0000FFFF"));
            var selectedCol = new SolidColorBrush(ColorFromHex(t.SelectedColor ?? t.PrimaryColor ?? "FFFFFFFF"));
            var selectorCol = new SolidColorBrush(ColorFromHex(t.SelectorColor ?? "333333FF"));
            var align = t.Alignment switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left };

            var area = new Canvas { Width = areaW, Height = areaH, ClipToBounds = true };
            int first = Math.Max(0, sel - rows / 2);
            if (first + rows > count) first = Math.Max(0, count - rows);
            for (int r = 0; r < rows && first + r < count; r++)
            {
                int idx = first + r;
                bool selected = idx == sel;
                double y = r * rowH;
                if (selected)
                {
                    var selBar = new System.Windows.Shapes.Rectangle { Width = areaW, Height = rowH, Fill = selectorCol };
                    Canvas.SetLeft(selBar, 0);
                    Canvas.SetTop(selBar, y);
                    area.Children.Add(selBar);
                }
                var tb = new TextBlock
                {
                    Text = ApplyCase(ItemLabel(idx), t.LetterCase),
                    Width = areaW, FontSize = fontPx,
                    Foreground = selected ? selectedCol : primary,
                    TextAlignment = align, TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Canvas.SetLeft(tb, 0);
                Canvas.SetTop(tb, y + (rowH - fontPx * 1.2) / 2);
                area.Children.Add(tb);
            }
            return area;
        }

        // ── per-item visual (image with text fallback) shared by carousel & grid ──
        private FrameworkElement MakeItem(int idx, double w, double h, string fit, bool selected,
            Color textColor, double fontPx, string? literal, string? letterCase)
        {
            var src = ItemImage(idx);
            if (src != null)
            {
                var stretch = fit switch { "fill" => Stretch.Fill, "cover" => Stretch.UniformToFill, _ => Stretch.Uniform };
                // imageColor / imageSelectedColor: ES-DE multiplies each item image by the colour. For the
                // common case (monochrome wheel-logo SVGs, e.g. CodyWheel) we colourise via an OpacityMask,
                // which reproduces the tint for white/transparent logos.
                string? tintHex = selected ? (_currentImageSelectedColor ?? NonWhite(_currentImageColor))
                                           : NonWhite(_currentImageColor);
                FrameworkElement visual;
                if (tintHex != null)
                {
                    visual = new System.Windows.Shapes.Rectangle
                    {
                        Width = w, Height = h,
                        Fill = BuildBrush(tintHex, _currentImageColorEnd ?? tintHex, _currentImageGradient),
                        OpacityMask = new ImageBrush(src) { Stretch = stretch },
                    };
                }
                else
                {
                    var img = new Image
                    {
                        Source = src, Width = w, Height = h, Stretch = stretch,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    };
                    RenderOptions.SetBitmapScalingMode(img, _currentScaling);
                    visual = img;
                }
                if (fit == "cover") return new Border { Width = w, Height = h, ClipToBounds = true, Child = visual };
                return visual;
            }
            // text fallback (system: literal or system name; gamelist: game name).
            string label = _viewKind == ThemeViewKind.System
                ? (literal ?? ItemLabel(idx))
                : ItemLabel(idx);
            var tb = new TextBlock
            {
                Text = ApplyCase(label, letterCase),
                FontSize = Math.Clamp(fontPx, 10, h * 0.9),
                Foreground = new SolidColorBrush(textColor.A == 0 ? Colors.White : textColor),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            if (_currentFont != null) tb.FontFamily = _currentFont;
            return new Grid { Width = w, Height = h, Children = { tb } };
        }

        // ════════════════════════ data binding helpers ══════════════════════════

        private int ItemCount() => _viewKind == ThemeViewKind.System ? (_items?.Systems.Count ?? 0) : (_items?.Games.Count ?? 0);
        private int SelectedIndex() => _viewKind == ThemeViewKind.System ? (_items?.SelectedSystem ?? 0) : (_items?.SelectedGame ?? 0);
        private ThemeGameEntry? SelectedGame => _viewKind == ThemeViewKind.Gamelist && _items != null
            && _items.SelectedGame >= 0 && _items.SelectedGame < _items.Games.Count ? _items.Games[_items.SelectedGame] : null;

        private string ItemLabel(int idx)
        {
            if (_viewKind == ThemeViewKind.System)
                return idx >= 0 && idx < (_items?.Systems.Count ?? 0) ? _items!.Systems[idx].Label : "";
            return idx >= 0 && idx < (_items?.Games.Count ?? 0) ? _items!.Games[idx].Label : "";
        }

        // The image for carousel/grid item idx: per-system logo, or per-game art by imageType.
        private ImageSource? ItemImage(int idx)
        {
            if (_viewKind == ThemeViewKind.System)
            {
                if (_items == null || idx < 0 || idx >= _items.Systems.Count) return null;
                string es = _items.Systems[idx].EsName;
                return LoadImageForSystem(_currentStatic, es) ?? LoadImageForSystem(_currentDefault, es);
            }
            if (_items == null || idx < 0 || idx >= _items.Games.Count) return null;
            return ResolveGameArt(_items.Games[idx], _currentImageTypes);
        }

        // Set by BuildCarousel/BuildGrid so MakeItem/ItemImage know which media to resolve.
        private string? _currentStatic, _currentDefault;
        private List<string> _currentImageTypes = new();
        private Image? _pendingVideoImage;   // last video element's Image (host drives playback into it)

        // Per-primary item styling context (set by BuildCarousel/BuildGrid, read by MakeItem).
        private System.Windows.Media.FontFamily? _currentFont;
        private BitmapScalingMode _currentScaling = BitmapScalingMode.HighQuality;
        private string? _currentImageColor, _currentImageColorEnd, _currentImageSelectedColor;
        private GradientType _currentImageGradient = GradientType.None;

        private ImageSource? ResolveGameArt(ThemeGameEntry? g, List<string> types)
        {
            if (g == null) return null;
            var order = types.Count > 0 ? types : new List<string> { "marquee" };
            foreach (var t in order)
            {
                string? p = t.Trim().ToLowerInvariant() switch
                {
                    "marquee" => g.MarqueePath ?? g.CoverPath,
                    "cover" => g.CoverPath,
                    "3dbox" => g.Box3dPath ?? g.CoverPath,
                    "screenshot" or "titlescreen" or "miximage" or "image" or "fanart" => g.ScreenshotPath,
                    _ => g.CoverPath ?? g.ScreenshotPath,
                };
                var src = LoadUserImage(p);
                if (src != null) return src;
            }
            return null;
        }

        private string ResolveText(TextElement tx)
        {
            if (tx.SystemData != null)
                return tx.SystemData.ToLowerInvariant() switch
                {
                    "gamecount" or "gamecountgames" or "gamecountgamesnotext" => _items?.SystemGameCount ?? "",
                    "gamecountfavorites" or "gamecountfavoritesnotext" => "",   // favorites not tracked
                    _ => _items?.SystemName ?? "",     // name / fullname
                };
            if (tx.Metadata != null)
            {
                var g = SelectedGame;
                string val = tx.Metadata.ToLowerInvariant() switch
                {
                    "name" or "title" => g?.Label ?? _items?.SystemName ?? "",
                    "developer" => g?.Developer ?? "",
                    "publisher" => g?.Publisher ?? "",
                    "genre" => g?.Genre ?? "",
                    "description" => g?.Description ?? "",
                    "rating" => g != null ? (g.Rating * 5).ToString("0.#") : "",
                    "releasedate" or "year" => g is { Year: > 0 } ? g.Year.ToString() : "",
                    "system" or "systemname" or "systemfullname" => _items?.SystemName ?? "",
                    "favorite" => g?.Favorite == true ? "yes" : "no",
                    "completed" => g?.Completed == true ? "yes" : "no",
                    "kidgame" => g?.Kidgame == true ? "yes" : "no",
                    "broken" => g?.Broken == true ? "yes" : "no",
                    "players" => g is { Players: > 0 } ? g.Players.ToString() : "",
                    _ => "",
                };
                return string.IsNullOrEmpty(val) ? (tx.DefaultValue ?? "") : val;
            }
            return ResolveSystemTokens(tx.Text ?? "");
        }

        // ES-DE system variables usable in literal text: ${system.name}/${system.fullName} resolve to
        // the current console and ${system.theme} to its ES name. Any other unresolved ${…} (per-system
        // metadata we don't carry, e.g. ${systemReleaseYear}) is stripped rather than blanking the line.
        private string ResolveSystemTokens(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.Contains("${")) return s;
            string name = _items?.SystemName ?? "";
            s = s.Replace("${system.fullName}", name).Replace("${system.name}", name);
            if (_systemTheme != null) s = s.Replace("${system.theme}", _systemTheme);
            return System.Text.RegularExpressions.Regex.Replace(s, @"\$\{[^}]*\}", "");
        }

        // ════════════════════════ sizing / colour / loading ═════════════════════

        // Content box (px) for an image given its intrinsic aspect and the ES sizing model.
        private (double, double) ImageBox(Vec2? size, Vec2? maxSize, Vec2? cropSize, double aspect)
        {
            if (aspect <= 0) aspect = 1;
            if (size is { } s)
            {
                double sx = s.X * _w, sy = s.Y * _h;
                if (s.X > 0 && s.Y > 0) return (sx, sy);            // exact
                if (s.X > 0) return (sx, sx / aspect);              // width fixed → height from aspect
                if (s.Y > 0) return (sy * aspect, sy);             // height fixed → width from aspect
            }
            if (maxSize is { } m)                                   // fit within, preserve aspect
            {
                double bw = m.X * _w, bh = m.Y * _h;
                double wpx = Math.Min(bw, bh * aspect);
                return (wpx, wpx / aspect);
            }
            if (cropSize is { } cs) return (cs.X * _w, cs.Y * _h);  // exact (cover + crop)
            double dh = 0.2 * _h;                                   // no size → modest default
            return (dh * aspect, dh);
        }

        private static double AspectOf(ImageSource src) => src switch
        {
            BitmapSource bs when bs.PixelHeight > 0 => (double)bs.PixelWidth / bs.PixelHeight,
            DrawingImage di when di.Height > 0 => di.Width / di.Height,
            _ => 1.0,
        };

        private static double AlignFrac(string? a, double dflt) => a switch
        {
            "left" or "top" => 0.0, "center" => 0.5, "right" or "bottom" => 1.0, _ => dflt,
        };

        private static double ItemAxisX(CarouselElement c, double areaW, double iw) =>
            AlignFrac(c.ItemHorizontalAlignment, 0.5) * (areaW - iw) + iw / 2;
        private static double ItemAxisY(CarouselElement c, double areaH, double ih) =>
            AlignFrac(c.ItemVerticalAlignment, 0.5) * (areaH - ih) + ih / 2;

        private static string ApplyCase(string s, string? letterCase) => letterCase switch
        {
            "uppercase" => s.ToUpperInvariant(),
            "lowercase" => s.ToLowerInvariant(),
            "capitalize" => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLowerInvariant()),
            _ => s,
        };

        // null unless hex is a real, non-white colour (white is ES-DE's "no tint" multiply identity).
        private static string? NonWhite(string? hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            var h = hex.Trim().TrimStart('#');
            if (h.Length == 6) h += "FF";
            return string.Equals(h, "FFFFFFFF", StringComparison.OrdinalIgnoreCase) ? null : hex;
        }

        // ES-DE's default interpolation is nearest, but to avoid softening box-art-heavy themes we keep
        // high-quality unless a theme explicitly asks for nearest (pixel-art).
        private static BitmapScalingMode ScalingOf(string? interp) =>
            string.Equals(interp, "nearest", StringComparison.OrdinalIgnoreCase)
                ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;

        // Resolve a theme-relative path to an absolute path inside the sandbox (no image load).
        private string? ResolveThemePath(string? rel)
        {
            if (string.IsNullOrEmpty(rel) || rel.Contains("${")) return null;
            string full;
            try { full = Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(_themeRoot, rel)); }
            catch { return null; }
            string sep = _themeRoot.EndsWith(Path.DirectorySeparatorChar) ? _themeRoot : _themeRoot + Path.DirectorySeparatorChar;
            return full.StartsWith(sep, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        // Load a theme-bundled .ttf/.otf as a WPF FontFamily (cached). Custom fonts are central to most
        // ES-DE themes; without this every theme falls back to the default system font.
        private readonly Dictionary<string, System.Windows.Media.FontFamily?> _fontCache = new(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Media.FontFamily? LoadFont(string? rel)
        {
            string? full = ResolveThemePath(rel);
            if (full == null || !File.Exists(full)) return null;
            if (_fontCache.TryGetValue(full, out var cached)) return cached;
            System.Windows.Media.FontFamily? fam = null;
            try { foreach (var f in System.Windows.Media.Fonts.GetFontFamilies(full)) { fam = f; break; } }
            catch { }
            _fontCache[full] = fam;
            return fam;
        }

        private static Brush BuildBrush(string? color, string? colorEnd, GradientType gradient)
        {
            var c1 = ColorFromHex(color ?? colorEnd ?? "FFFFFFFF");
            // ES-DE creates a gradient whenever colorEnd differs from color — even with no explicit
            // gradientType (it defaults to horizontal). Without this, fade panels (black → transparent)
            // render as solid fills that smother whatever is behind them.
            if (color != null && colorEnd != null && !string.Equals(color, colorEnd, StringComparison.OrdinalIgnoreCase))
                return new LinearGradientBrush(c1, ColorFromHex(colorEnd), gradient == GradientType.Vertical ? 90 : 0);
            return new SolidColorBrush(c1);
        }

        // ES colours are RRGGBB or RRGGBBAA (NOT AARRGGBB). 6 digits ⇒ opaque.
        private static Color ColorFromHex(string? hex)
        {
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length == 6) hex += "FF";
            if (hex.Length != 8) return Colors.White;
            try
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                byte a = Convert.ToByte(hex.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
            catch { return Colors.White; }
        }

        // Theme art keyed by the selected console's ES-DE name (sandboxed to the theme root).
        private ImageSource? LoadImageForSystem(string? path, string esName)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.Contains("${system.theme}"))
                return TryLoad(path.Replace("${system.theme}", esName)) ?? TryLoad(path.Replace("${system.theme}", "_default"));
            return TryLoad(path);
        }

        // General theme asset (honours a render-time ${system.theme} token).
        private ImageSource? LoadImage(string? rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            if (_systemTheme != null && rel.Contains("${system.theme}"))
                return TryLoad(rel.Replace("${system.theme}", _systemTheme))
                       ?? TryLoad(rel.Replace("${system.theme}", "_default"));
            return TryLoad(rel);
        }

        // User library art (absolute path outside the theme — not sandboxed).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> _imageCache = new(StringComparer.OrdinalIgnoreCase);
        private static ImageSource? LoadUserImage(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_imageCache.TryGetValue(path, out var cached)) return cached;
            if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) { QueueWebp(path, null); return null; }
            ImageSource? result = null;
            if (File.Exists(path))
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(path);
                    b.EndInit(); b.Freeze(); result = b;
                }
                catch { }
            _imageCache[path] = result;
            return result;
        }

        private ImageSource? TryLoad(string rel)
        {
            if (rel.Contains("${")) return null;             // unresolved token
            string full;
            try { full = Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(_themeRoot, rel)); }
            catch { return null; }
            if (_imageCache.TryGetValue(full, out var cached)) return cached;
            // WebP is decoded off-thread (dwebp spawn must never block the UI); see QueueWebp.
            if (full.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) { QueueWebp(full, _themeRoot); return null; }
            var result = LoadUncached(full);
            _imageCache[full] = result;
            return result;
        }

        private ImageSource? LoadUncached(string full)
        {
            try
            {
                string rootSep = _themeRoot.EndsWith(Path.DirectorySeparatorChar)
                    ? _themeRoot : _themeRoot + Path.DirectorySeparatorChar;
                if (!full.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase)) return null;  // sandbox
                if (!File.Exists(full)) return null;
                if (full.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return LoadSvg(full);
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(full);
                bmp.EndInit(); bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // WPF has no native WebP decoder, and many ES-DE themes (Aura, etc.) ship webp art. We decode
        // it with Google's bundled dwebp.exe — but spawning a process must never block the couch UI,
        // so webp decodes on a background thread, caches the result, then OnAsyncImageReady asks the
        // host to re-render so the image appears.
        private static readonly System.Collections.Generic.HashSet<string> _webpPending = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string DwebpPath = ExtractDwebp();

        // dwebp.exe is embedded in the assembly; extract it once to a temp cache so nothing ships
        // loose next to the exe. Versioned name + size check re-extracts on upgrade.
        private static string ExtractDwebp()
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "dwebp.exe");
            try
            {
                var asm = typeof(EmuTvThemeRenderer).Assembly;
                string? res = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(".dwebp.exe", StringComparison.OrdinalIgnoreCase));
                if (res == null) return fallback;
                string dir = Path.Combine(Path.GetTempPath(), "Emutastic");
                Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, "dwebp-1.5.0.exe");
                using var rs = asm.GetManifestResourceStream(res);
                if (rs == null) return fallback;
                if (!File.Exists(dst) || new FileInfo(dst).Length != rs.Length)
                {
                    using var fs = File.Create(dst);
                    rs.CopyTo(fs);
                }
                return dst;
            }
            catch { return fallback; }
        }

        /// <summary>Raised (off the UI thread) when an async webp decode finishes; the host re-renders.</summary>
        public static Action? OnAsyncImageReady;

        private static void QueueWebp(string full, string? sandboxRoot)
        {
            lock (_webpPending) { if (!_webpPending.Add(full)) return; }
            System.Threading.Tasks.Task.Run(() =>
            {
                ImageSource? img = null;
                try
                {
                    bool ok = File.Exists(full);
                    if (ok && sandboxRoot != null)
                    {
                        string sep = sandboxRoot.EndsWith(Path.DirectorySeparatorChar) ? sandboxRoot : sandboxRoot + Path.DirectorySeparatorChar;
                        if (!full.StartsWith(sep, StringComparison.OrdinalIgnoreCase)) ok = false;   // sandbox
                    }
                    if (ok) img = LoadViaDwebp(full);
                }
                catch { }
                _imageCache[full] = img;
                lock (_webpPending) { _webpPending.Remove(full); }
                try { OnAsyncImageReady?.Invoke(); } catch { }
            });
        }

        // Decode a webp to a temp PNG via dwebp.exe, then load it as a frozen (cross-thread) image.
        private static ImageSource? LoadViaDwebp(string webpPath)
        {
            if (!File.Exists(DwebpPath)) return null;
            string tmp = Path.Combine(Path.GetTempPath(), "emutv-webp-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                { FileName = DwebpPath, UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(webpPath);
                psi.ArgumentList.Add("-quiet");
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add(tmp);
                using (var p = System.Diagnostics.Process.Start(psi)) { p?.WaitForExit(8000); }
                if (!File.Exists(tmp)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(tmp);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private static ImageSource? LoadSvg(string full)
        {
            try
            {
                var settings = new WpfDrawingSettings { IncludeRuntime = true, TextAsGeometry = true };
                using var reader = new FileSvgReader(settings);
                var drawing = reader.Read(full);
                if (drawing == null) return null;
                var di = new DrawingImage(drawing);
                di.Freeze();
                return di;
            }
            catch { return null; }
        }
    }
}
