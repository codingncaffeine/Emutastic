using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Emutastic.Models.EmuTv;

namespace Emutastic.Services
{
    /// <summary>The user's current pick for each customization axis. Null = use the theme's default.</summary>
    public sealed class ThemeSelection
    {
        public string? Variant { get; set; }
        public string? ColorScheme { get; set; }
        public string? AspectRatio { get; set; }
        public string? FontSize { get; set; }
    }

    public sealed class EmuTvThemeParseResult
    {
        public EmuTvTheme Theme { get; set; } = new();
        public List<string> Warnings { get; } = new();
        public bool Ok { get; set; } = true;
    }

    /// <summary>
    /// Parses an ES-DE-shaped EmuTV theme (capabilities.xml + theme.xml + includes) into the
    /// <see cref="EmuTvTheme"/> model for one axis selection. Pure: depends only on System.* and
    /// the model, so it is unit-testable without WPF or the rest of the app.
    ///
    /// Security: XML is loaded with DTD/XXE disabled; &lt;include&gt; targets are sandboxed to the
    /// theme root. Element asset paths are author-${var}-resolved here but final path sandboxing
    /// and ${system.*} substitution happen at render time (per item), since ${system.theme}
    /// depends on which console/game is in focus.
    /// </summary>
    public sealed class EmuTvThemeParser
    {
        private readonly string _root;
        private readonly List<string> _warnings = new();
        private readonly HashSet<string> _warnedTags = new();
        private int _includeDepth;
        private bool _quiet; // suppress warnings during the variables-only pre-pass
        private const int MaxIncludeDepth = 24;

        public EmuTvThemeParser(string themeRootPath) => _root = Path.GetFullPath(themeRootPath);

        // ── public API ───────────────────────────────────────────────────────

        public EmuTvThemeParseResult Parse(ThemeSelection? selection = null)
        {
            var result = new EmuTvThemeParseResult();
            var theme = result.Theme;
            theme.RootPath = _root;

            var caps = ParseCapabilities();
            theme.Capabilities = caps;
            theme.Name = caps.ThemeName;

            var sel = selection ?? new ThemeSelection();
            string variantName = sel.Variant ?? FirstSelectable(caps.Variants) ?? "Default";
            string? scheme = sel.ColorScheme ?? caps.ColorSchemes.FirstOrDefault()?.Name;
            string ar = sel.AspectRatio ?? Prefer(caps.AspectRatios, "16:9");
            string fs = sel.FontSize ?? Prefer(caps.FontSizes, "medium");

            var ctx = new Ctx(variantName, scheme, ar, fs);
            // ES-DE exposes ${rootpath} as a built-in (the theme root), available even before a theme
            // redeclares it — includes/assets reference it above their own <variables> block. Seed it
            // so those paths resolve; a theme's own <rootpath> (usually "./") re-resolves to the same.
            ctx.Vars["rootpath"] = _root;
            var acc = new Dictionary<ThemeViewKind, OrderedElements>();

            string themeXml = Path.Combine(_root, "theme.xml");
            if (File.Exists(themeXml))
            {
                try
                {
                    // Phase 1: build the complete variable map. ES-DE resolves variables,
                    // colorSchemes and fontSizes BEFORE views, so values like font-size vars are
                    // ready wherever they're referenced. Warnings are suppressed in this pass.
                    _quiet = true;
                    ProcessFile(themeXml, ctx, acc, varsOnly: true);
                    // Phase 2: collect elements using the now-complete variable map.
                    _quiet = false;
                    _includeDepth = 0;
                    ProcessFile(themeXml, ctx, acc, varsOnly: false);
                }
                catch (Exception ex) { Warn($"theme.xml: {ex.Message}"); result.Ok = false; }
            }
            else { Warn("theme.xml not found in theme root."); result.Ok = false; }

            var variant = new ThemeVariant { Name = variantName };
            foreach (var kind in new[] { ThemeViewKind.System, ThemeViewKind.Gamelist })
                if (acc.TryGetValue(kind, out var oe))
                    variant.Views.Add(new ThemeView { Kind = kind, Elements = oe.InOrder() });
            theme.Variants[variantName] = variant;

            result.Warnings.AddRange(_warnings);
            return result;
        }

        public ThemeCapabilities ParseCapabilities()
        {
            var caps = new ThemeCapabilities();
            string path = Path.Combine(_root, "capabilities.xml");
            if (!File.Exists(path)) { Warn("capabilities.xml not found."); return caps; }

            XDocument doc;
            try { doc = LoadXml(path); }
            catch (Exception ex) { Warn($"capabilities.xml: {ex.Message}"); return caps; }
            var root = doc.Root;
            if (root == null) return caps;

            caps.ThemeName = root.Element("themeName")?.Value.Trim() ?? "";
            foreach (var e in root.Elements("aspectRatio")) AddIf(caps.AspectRatios, e.Value);
            foreach (var e in root.Elements("fontSize")) AddIf(caps.FontSizes, e.Value);

            foreach (var e in root.Elements("colorScheme"))
            {
                string name = (string?)e.Attribute("name") ?? "";
                if (name.Length == 0) continue;
                caps.ColorSchemes.Add(new ColorSchemeDef
                {
                    Name = name,
                    Label = e.Element("label")?.Value.Trim() ?? name,
                });
            }

            foreach (var e in root.Elements("variant"))
            {
                string name = (string?)e.Attribute("name") ?? "";
                if (name.Length == 0) continue;
                var vd = new VariantDef
                {
                    Name = name,
                    Label = e.Element("label")?.Value.Trim() ?? name,
                    Selectable = ParseBool(e.Element("selectable")?.Value) ?? true,
                };
                var ov = e.Element("override");
                if (ov != null)
                    vd.Override = new NoMediaOverride
                    {
                        Trigger = ov.Element("trigger")?.Value.Trim() ?? "noMedia",
                        UseVariant = ov.Element("useVariant")?.Value.Trim() ?? "",
                        MediaTypes = SplitCsv(ov.Element("mediaType")?.Value),
                    };
                caps.Variants.Add(vd);
            }

            foreach (var e in root.Elements("transitions"))
            {
                string name = (string?)e.Attribute("name") ?? "";
                caps.Transitions.Add(new TransitionsProfile
                {
                    Name = name,
                    Label = e.Element("label")?.Value.Trim() ?? name,
                    Selectable = ParseBool(e.Element("selectable")?.Value) ?? true,
                    SystemToSystem = e.Element("systemToSystem")?.Value.Trim(),
                    SystemToGamelist = e.Element("systemToGamelist")?.Value.Trim(),
                    GamelistToGamelist = e.Element("gamelistToGamelist")?.Value.Trim(),
                    GamelistToSystem = e.Element("gamelistToSystem")?.Value.Trim(),
                    StartupToSystem = e.Element("startupToSystem")?.Value.Trim(),
                    StartupToGamelist = e.Element("startupToGamelist")?.Value.Trim(),
                });
            }
            return caps;
        }

        // ── resolution context ────────────────────────────────────────────────

        private sealed class Ctx
        {
            public readonly Dictionary<string, string> Vars = new(StringComparer.Ordinal);
            public readonly string Variant;
            public readonly string? Scheme;
            public readonly string AspectRatio;
            public readonly string FontSize;
            public Ctx(string variant, string? scheme, string ar, string fs)
            { Variant = variant; Scheme = scheme; AspectRatio = ar; FontSize = fs; }
        }

        /// <summary>
        /// Preserves first-seen order of (tag,name) elements within a view while letting later
        /// rules merge onto the same element — ES-DE's cascading-by-name + "latest wins".
        /// </summary>
        private sealed class OrderedElements
        {
            private readonly List<string> _order = new();
            private readonly Dictionary<string, ThemeElement> _map = new(StringComparer.Ordinal);
            public ThemeElement? GetOrAdd(string key, Func<ThemeElement?> create)
            {
                if (_map.TryGetValue(key, out var existing)) return existing;
                var el = create();
                if (el == null) return null;
                _map[key] = el; _order.Add(key);
                return el;
            }
            public List<ThemeElement> InOrder() => _order.Select(k => _map[k]).ToList();
        }

        // ── walk ──────────────────────────────────────────────────────────────

        private void ProcessFile(string path, Ctx ctx, Dictionary<ThemeViewKind, OrderedElements> acc, bool varsOnly)
        {
            var doc = LoadXml(path);
            var root = doc.Root;
            if (root == null || root.Name.LocalName != "theme")
            { Warn($"{Path.GetFileName(path)}: root is not <theme>."); return; }
            ProcessChildren(root, ctx, acc, Path.GetDirectoryName(path)!, varsOnly);
        }

        private void ProcessChildren(XElement container, Ctx ctx, Dictionary<ThemeViewKind, OrderedElements> acc, string baseDir, bool varsOnly)
        {
            foreach (var el in container.Elements())
            {
                switch (el.Name.LocalName)
                {
                    case "include": HandleInclude(el, ctx, acc, baseDir, varsOnly); break;
                    case "variables": if (varsOnly) MergeVariables(el, ctx, baseDir); break;
                    case "colorScheme": if (varsOnly) HandleColorScheme(el, ctx, baseDir); break;
                    case "variant":
                        if (NameMatches(el, ctx.Variant) || NameMatches(el, "all"))
                            ProcessChildren(el, ctx, acc, baseDir, varsOnly);
                        break;
                    case "aspectRatio":
                        if (NameAbsentOrMatches(el, ctx.AspectRatio))
                            ProcessChildren(el, ctx, acc, baseDir, varsOnly);
                        break;
                    case "fontSize":
                        if (NameAbsentOrMatches(el, ctx.FontSize))
                            ProcessChildren(el, ctx, acc, baseDir, varsOnly);
                        break;
                    case "view": if (!varsOnly) HandleView(el, ctx, acc, baseDir); break;
                }
            }
        }

        private void HandleInclude(XElement el, Ctx ctx, Dictionary<ThemeViewKind, OrderedElements> acc, string baseDir, bool varsOnly)
        {
            string rel = ResolveVars(el.Value.Trim(), ctx.Vars);
            if (rel.Length == 0 || rel.Contains("${")) return;
            string? abs = SandboxResolve(rel, baseDir);
            if (abs == null) { Warn($"include outside theme root ignored: {rel}"); return; }
            if (!File.Exists(abs)) { Warn($"include not found: {rel}"); return; }
            if (_includeDepth >= MaxIncludeDepth) { Warn("include depth limit reached."); return; }
            _includeDepth++;
            try { ProcessFile(abs, ctx, acc, varsOnly); }
            catch (Exception ex) { Warn($"include {rel}: {ex.Message}"); }
            finally { _includeDepth--; }
        }

        private void HandleColorScheme(XElement el, Ctx ctx, string baseDir)
        {
            if (ctx.Scheme == null) return;
            if (!SplitCsv((string?)el.Attribute("name")).Contains(ctx.Scheme)) return;
            foreach (var vars in el.Elements("variables")) MergeVariables(vars, ctx, baseDir);
        }

        private void MergeVariables(XElement varsEl, Ctx ctx, string baseDir)
        {
            foreach (var v in varsEl.Elements())
            {
                string val = ResolveVars(v.Value.Trim(), ctx.Vars);
                // Asset paths are relative to the FILE that declares them (ES-DE convention), not
                // the theme root. Normalise a relative path to absolute against this file's dir so
                // a "./../assets/..." written inside a subfolder doesn't escape the theme root and
                // fail the renderer's sandbox. (Tokens like ${system.theme} are kept literal.)
                if (!val.StartsWith("${") && (val.Contains('/') || val.Contains('\\')) && !Path.IsPathRooted(val))
                {
                    try { val = Path.GetFullPath(Path.Combine(baseDir, val)); } catch { /* keep raw */ }
                }
                ctx.Vars[v.Name.LocalName] = val;
            }
        }

        private void HandleView(XElement viewEl, Ctx ctx, Dictionary<ThemeViewKind, OrderedElements> acc, string baseDir)
        {
            var kinds = ParseViewKinds((string?)viewEl.Attribute("name"));
            if (kinds.Count == 0) return;

            foreach (var child in viewEl.Elements())
            {
                string tag = child.Name.LocalName;
                var names = SplitCsv((string?)child.Attribute("name"));
                if (names.Count == 0) names.Add(""); // unnamed singleton

                foreach (var kind in kinds)
                {
                    var oe = acc.TryGetValue(kind, out var x) ? x : (acc[kind] = new OrderedElements());
                    foreach (var nm in names)
                    {
                        var element = oe.GetOrAdd(tag + "#" + nm, () => CreateElement(tag, nm));
                        if (element == null) { WarnTag(tag); continue; }
                        ReadCommon(element, child, ctx);
                        ReadSpecific(element, child, ctx);
                    }
                }
            }
        }

        // ── element construction & property reading ────────────────────────────

        private static ThemeElement? CreateElement(string tag, string name) => tag switch
        {
            "carousel" => new CarouselElement { Name = name },
            "grid" => new GridElement { Name = name },
            "textlist" => new TextListElement { Name = name },
            "image" => new ImageElement { Name = name },
            "video" => new VideoElement { Name = name },
            "text" => new TextElement { Name = name },
            "badges" => new BadgesElement { Name = name },
            "rating" => new RatingElement { Name = name },
            "datetime" => new DateTimeElement { Name = name },
            "gamelistinfo" => new GamelistInfoElement { Name = name },
            "animation" => new AnimationElement { Name = name },
            "helpsystem" => new HelpSystemElement { Name = name },
            "tvComposite" => new TvCompositeElement { Name = name },
            "saveStateCarousel" => new SaveStateCarouselElement { Name = name },
            "continueTile" => new ContinueTileElement { Name = name },
            _ => null,
        };

        private void ReadCommon(ThemeElement el, XElement b, Ctx c)
        {
            el.Pos = Vc(b, "pos", c) ?? el.Pos;
            el.Size = Vc(b, "size", c) ?? el.Size;
            el.Origin = Vc(b, "origin", c) ?? el.Origin;
            el.ZIndex = D(b, "zIndex", c) ?? el.ZIndex;
            el.Opacity = D(b, "opacity", c) ?? el.Opacity;
            el.Rotation = D(b, "rotation", c) ?? el.Rotation;
            el.RotationOrigin = Vc(b, "rotationOrigin", c) ?? el.RotationOrigin;
            el.Visible = Bl(b, "visible", c) ?? el.Visible;
        }

        private void ReadSpecific(ThemeElement el, XElement b, Ctx c)
        {
            switch (el)
            {
                case CarouselElement x:
                    x.Type = ParseCarouselType(S(b, "type", c)) ?? x.Type;
                    AddCsv(x.ImageTypes, S(b, "imageType", c));
                    x.StaticImage = S(b, "staticImage", c) ?? x.StaticImage;
                    x.DefaultImage = S(b, "defaultImage", c) ?? x.DefaultImage;
                    x.MaxItemCount = D(b, "maxItemCount", c) ?? x.MaxItemCount;
                    x.ItemsBeforeCenter = (int?)D(b, "itemsBeforeCenter", c) ?? x.ItemsBeforeCenter;
                    x.ItemsAfterCenter = (int?)D(b, "itemsAfterCenter", c) ?? x.ItemsAfterCenter;
                    x.ItemSize = Vc(b, "itemSize", c) ?? x.ItemSize;
                    x.ItemSpacing = Vc(b, "itemSpacing", c) ?? x.ItemSpacing;
                    x.ItemScale = D(b, "itemScale", c) ?? x.ItemScale;
                    x.ItemRotation = D(b, "itemRotation", c) ?? x.ItemRotation;
                    x.ItemRotationOrigin = Vc(b, "itemRotationOrigin", c) ?? x.ItemRotationOrigin;
                    x.ItemAxisHorizontal = Bl(b, "itemAxisHorizontal", c) ?? x.ItemAxisHorizontal;
                    x.ItemHorizontalAlignment = S(b, "itemHorizontalAlignment", c) ?? x.ItemHorizontalAlignment;
                    x.ItemVerticalAlignment = S(b, "itemVerticalAlignment", c) ?? x.ItemVerticalAlignment;
                    x.WheelHorizontalAlignment = S(b, "wheelHorizontalAlignment", c) ?? x.WheelHorizontalAlignment;
                    x.WheelVerticalAlignment = S(b, "wheelVerticalAlignment", c) ?? x.WheelVerticalAlignment;
                    x.HorizontalOffset = D(b, "horizontalOffset", c) ?? x.HorizontalOffset;
                    x.VerticalOffset = D(b, "verticalOffset", c) ?? x.VerticalOffset;
                    x.ImageFit = S(b, "imageFit", c) ?? x.ImageFit;
                    x.Color = S(b, "color", c) ?? x.Color;
                    x.ColorEnd = S(b, "colorEnd", c) ?? x.ColorEnd;
                    x.TextColor = S(b, "textColor", c) ?? x.TextColor;
                    x.FontSize = D(b, "fontSize", c) ?? x.FontSize;
                    x.LetterCase = S(b, "letterCase", c) ?? x.LetterCase;
                    x.Text = S(b, "text", c) ?? x.Text;
                    x.SelectorColor = S(b, "selectorColor", c) ?? x.SelectorColor;
                    x.UnfocusedItemOpacity = D(b, "unfocusedItemOpacity", c) ?? x.UnfocusedItemOpacity;
                    x.FontPath = S(b, "fontPath", c) ?? x.FontPath;
                    x.LineSpacing = D(b, "lineSpacing", c) ?? x.LineSpacing;
                    x.SystemNameSuffix = Bl(b, "systemNameSuffix", c) ?? x.SystemNameSuffix;
                    x.Gradient = ParseGradient(S(b, "gradientType", c)) ?? x.Gradient;
                    x.ImageColor = S(b, "imageColor", c) ?? x.ImageColor;
                    x.ImageColorEnd = S(b, "imageColorEnd", c) ?? x.ImageColorEnd;
                    x.ImageGradient = ParseGradient(S(b, "imageGradientType", c)) ?? x.ImageGradient;
                    x.ImageSelectedColor = S(b, "imageSelectedColor", c) ?? x.ImageSelectedColor;
                    x.ImageInterpolation = S(b, "imageInterpolation", c) ?? x.ImageInterpolation;
                    x.ImageSaturation = D(b, "imageSaturation", c) ?? x.ImageSaturation;
                    x.ImageBrightness = D(b, "imageBrightness", c) ?? x.ImageBrightness;
                    x.ImageCornerRadius = D(b, "imageCornerRadius", c) ?? x.ImageCornerRadius;
                    x.ImageCropPos = Vc(b, "imageCropPos", c) ?? x.ImageCropPos;
                    x.UnfocusedItemSaturation = D(b, "unfocusedItemSaturation", c) ?? x.UnfocusedItemSaturation;
                    x.UnfocusedItemDimming = D(b, "unfocusedItemDimming", c) ?? x.UnfocusedItemDimming;
                    x.TextBackgroundColor = S(b, "textBackgroundColor", c) ?? x.TextBackgroundColor;
                    x.TextSelectedColor = S(b, "textSelectedColor", c) ?? x.TextSelectedColor;
                    x.TextSelectedBackgroundColor = S(b, "textSelectedBackgroundColor", c) ?? x.TextSelectedBackgroundColor;
                    break;
                case GridElement x:
                    AddCsv(x.ImageTypes, S(b, "imageType", c));
                    x.StaticImage = S(b, "staticImage", c) ?? x.StaticImage;
                    x.DefaultImage = S(b, "defaultImage", c) ?? x.DefaultImage;
                    x.ItemSize = Vc(b, "itemSize", c) ?? x.ItemSize;
                    x.ItemSpacing = Vc(b, "itemSpacing", c) ?? x.ItemSpacing;
                    x.ItemScale = D(b, "itemScale", c) ?? x.ItemScale;
                    x.SelectorColor = S(b, "selectorColor", c) ?? x.SelectorColor;
                    x.ImageFit = S(b, "imageFit", c) ?? x.ImageFit;
                    x.UnfocusedItemOpacity = D(b, "unfocusedItemOpacity", c) ?? x.UnfocusedItemOpacity;
                    x.TextColor = S(b, "textColor", c) ?? x.TextColor;
                    x.FontSize = D(b, "fontSize", c) ?? x.FontSize;
                    x.LetterCase = S(b, "letterCase", c) ?? x.LetterCase;
                    x.FontPath = S(b, "fontPath", c) ?? x.FontPath;
                    x.Text = S(b, "text", c) ?? x.Text;
                    break;
                case TextListElement x:
                    x.FontPath = S(b, "fontPath", c) ?? x.FontPath;
                    x.FontSize = D(b, "fontSize", c) ?? x.FontSize;
                    x.LineSpacing = D(b, "lineSpacing", c) ?? x.LineSpacing;
                    x.PrimaryColor = S(b, "primaryColor", c) ?? x.PrimaryColor;
                    x.SelectedColor = S(b, "selectedColor", c) ?? x.SelectedColor;
                    x.SelectorColor = S(b, "selectorColor", c) ?? x.SelectorColor;
                    x.SelectedBackgroundColor = S(b, "selectedBackgroundColor", c) ?? x.SelectedBackgroundColor;
                    x.SecondaryColor = S(b, "secondaryColor", c) ?? x.SecondaryColor;
                    x.Alignment = S(b, "horizontalAlignment", c) ?? S(b, "alignment", c) ?? x.Alignment;
                    x.LetterCase = S(b, "letterCase", c) ?? x.LetterCase;
                    break;
                case ImageElement x:
                    x.Path = S(b, "path", c) ?? x.Path;
                    AddCsv(x.ImageTypes, S(b, "imageType", c));
                    // ES-DE's image fallback tag is <default> (some themes also use <defaultImage>).
                    x.DefaultImage = S(b, "default", c) ?? S(b, "defaultImage", c) ?? x.DefaultImage;
                    x.MaxSize = Vc(b, "maxSize", c) ?? x.MaxSize;
                    x.CropSize = Vc(b, "cropSize", c) ?? x.CropSize;
                    x.CropPos = Vc(b, "cropPos", c) ?? x.CropPos;
                    x.Tile = Bl(b, "tile", c) ?? x.Tile;
                    x.Color = S(b, "color", c) ?? x.Color;
                    x.ColorEnd = S(b, "colorEnd", c) ?? x.ColorEnd;
                    x.Gradient = ParseGradient(S(b, "gradientType", c)) ?? x.Gradient;
                    x.Interpolation = S(b, "interpolation", c) ?? x.Interpolation;
                    x.TileSize = Vc(b, "tileSize", c) ?? x.TileSize;
                    x.TileHorizontalAlignment = S(b, "tileHorizontalAlignment", c) ?? x.TileHorizontalAlignment;
                    x.TileVerticalAlignment = S(b, "tileVerticalAlignment", c) ?? x.TileVerticalAlignment;
                    break;
                case VideoElement x:
                    AddCsv(x.ImageTypes, S(b, "imageType", c));
                    x.DefaultImage = S(b, "default", c) ?? S(b, "defaultImage", c) ?? x.DefaultImage;
                    x.MaxSize = Vc(b, "maxSize", c) ?? x.MaxSize;
                    x.CropSize = Vc(b, "cropSize", c) ?? x.CropSize;
                    x.Audio = Bl(b, "audio", c) ?? x.Audio;
                    break;
                case TextElement x:
                    x.Text = S(b, "text", c) ?? x.Text;
                    x.Metadata = S(b, "metadata", c) ?? x.Metadata;
                    x.SystemData = S(b, "systemdata", c) ?? x.SystemData;
                    x.FontPath = S(b, "fontPath", c) ?? x.FontPath;
                    x.FontSize = D(b, "fontSize", c) ?? x.FontSize;
                    x.Color = S(b, "color", c) ?? x.Color;
                    x.Alignment = S(b, "horizontalAlignment", c) ?? S(b, "alignment", c) ?? x.Alignment;
                    x.Container = Bl(b, "container", c) ?? x.Container;
                    x.ContainerType = S(b, "containerType", c) ?? x.ContainerType;
                    x.DefaultValue = S(b, "defaultValue", c) ?? x.DefaultValue;
                    x.LetterCase = S(b, "letterCase", c) ?? x.LetterCase;
                    x.LineSpacing = D(b, "lineSpacing", c) ?? x.LineSpacing;
                    x.VerticalAlignment = S(b, "verticalAlignment", c) ?? x.VerticalAlignment;
                    x.BackgroundColor = S(b, "backgroundColor", c) ?? x.BackgroundColor;
                    break;
                case BadgesElement x:
                    x.Lines = (int?)D(b, "lines", c) ?? x.Lines;
                    x.ItemsPerLine = (int?)D(b, "itemsPerLine", c) ?? x.ItemsPerLine;
                    x.Slots = S(b, "slots", c) ?? x.Slots;
                    x.IconColor = S(b, "badgeIconColor", c) ?? x.IconColor;
                    x.IconColorEnd = S(b, "badgeIconColorEnd", c) ?? x.IconColorEnd;
                    x.HorizontalAlignment = S(b, "horizontalAlignment", c) ?? x.HorizontalAlignment;
                    x.Direction = S(b, "direction", c) ?? x.Direction;
                    x.ItemMargin = Vc(b, "itemMargin", c) ?? x.ItemMargin;
                    // customBadgeIcon repeats with a badge="..." attribute, so read all siblings.
                    foreach (var ci in b.Elements())
                    {
                        if (ci.Name.LocalName != "customBadgeIcon") continue;
                        string? badge = (string?)ci.Attribute("badge");
                        string val = ResolveVars(ci.Value.Trim(), c.Vars);
                        if (!string.IsNullOrEmpty(badge) && !string.IsNullOrEmpty(val) && !val.Contains("${"))
                            x.CustomBadgeIcons[badge.Trim().ToLowerInvariant()] = val;
                    }
                    break;
                case RatingElement x:
                    x.Color = S(b, "color", c) ?? x.Color;
                    x.FilledImage = S(b, "filledPath", c) ?? x.FilledImage;
                    x.UnfilledImage = S(b, "unfilledPath", c) ?? x.UnfilledImage;
                    break;
                case DateTimeElement x:
                    x.Metadata = S(b, "metadata", c) ?? x.Metadata;
                    x.Format = S(b, "format", c) ?? x.Format;
                    x.Color = S(b, "color", c) ?? x.Color;
                    x.FontSize = D(b, "fontSize", c) ?? x.FontSize;
                    x.FontPath = S(b, "fontPath", c) ?? x.FontPath;
                    break;
                case GamelistInfoElement x:
                    x.Color = S(b, "color", c) ?? x.Color;
                    x.BackgroundColor = S(b, "backgroundColor", c) ?? x.BackgroundColor;
                    x.FontSize = D(b, "fontSize", c) ?? x.FontSize;
                    x.FontPath = S(b, "fontPath", c) ?? x.FontPath;
                    x.Alignment = S(b, "horizontalAlignment", c) ?? x.Alignment;
                    break;
                case AnimationElement x:
                    x.Path = S(b, "path", c) ?? x.Path;
                    x.MaxSize = Vc(b, "maxSize", c) ?? x.MaxSize;
                    break;
                case HelpSystemElement x:
                    x.TextColor = S(b, "textColor", c) ?? x.TextColor;
                    x.IconColor = S(b, "iconColor", c) ?? x.IconColor;
                    x.FontSize = D(b, "fontSize", c) ?? x.FontSize;
                    x.FontPath = S(b, "fontPath", c) ?? x.FontPath;
                    x.Scope = S(b, "scope", c) ?? x.Scope;
                    x.LetterCase = S(b, "letterCase", c) ?? x.LetterCase;
                    x.BackgroundColor = S(b, "backgroundColor", c) ?? x.BackgroundColor;
                    break;
                case TvCompositeElement x:
                    x.BezelImage = S(b, "bezelImage", c) ?? x.BezelImage;
                    x.ScreenPos = Vc(b, "screenPos", c) ?? x.ScreenPos;
                    x.ScreenSize = Vc(b, "screenSize", c) ?? x.ScreenSize;
                    AddCsv(x.ImageTypes, S(b, "imageType", c));
                    x.StandByImage = S(b, "standByImage", c) ?? x.StandByImage;
                    break;
                case SaveStateCarouselElement x:
                    x.LabelImage = S(b, "labelImage", c) ?? x.LabelImage;
                    x.ItemSize = Vc(b, "itemSize", c) ?? x.ItemSize;
                    x.ItemSpacing = Vc(b, "itemSpacing", c) ?? x.ItemSpacing;
                    x.SelectorColor = S(b, "selectorColor", c) ?? x.SelectorColor;
                    x.EmptyText = S(b, "emptyText", c) ?? x.EmptyText;
                    break;
                case ContinueTileElement x:
                    x.Label = S(b, "label", c) ?? x.Label;
                    x.Color = S(b, "color", c) ?? x.Color;
                    break;
            }
        }

        // ── value helpers ──────────────────────────────────────────────────────

        private static string? Txt(XElement b, string tag)
        {
            var e = b.Elements().LastOrDefault(x => x.Name.LocalName == tag); // last wins
            var v = e?.Value.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }

        private string? S(XElement b, string tag, Ctx c)
        {
            var raw = Txt(b, tag);
            if (raw == null) return null;
            var s = ResolveVars(raw, c.Vars);
            return s.Length == 0 ? null : s;
        }

        private double? D(XElement b, string tag, Ctx c)
        {
            var s = S(b, tag, c);
            if (s == null) return null;
            if (s.Contains("${")) { Warn($"<{tag}> has unresolved variable '{s}'."); return null; }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        private Vec2? Vc(XElement b, string tag, Ctx c)
        {
            var s = S(b, tag, c);
            if (s == null) return null;
            if (s.Contains("${")) { Warn($"<{tag}> has unresolved variable '{s}'."); return null; }
            var parts = s.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                return new Vec2(x, y);
            return null;
        }

        private bool? Bl(XElement b, string tag, Ctx c) => ParseBool(S(b, tag, c));

        private static readonly Regex VarRx = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

        /// <summary>Substitutes ${name} from the variable map (nested, up to a depth cap).
        /// Unknown variables (e.g. runtime ${system.theme}) are left intact for the renderer.</summary>
        private static string ResolveVars(string? value, Dictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            string s = value;
            for (int i = 0; i < 10 && s.Contains("${"); i++)
            {
                string next = VarRx.Replace(s, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
                if (next == s) break;
                s = next;
            }
            return s;
        }

        private XDocument LoadXml(string path)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
            };
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader);
        }

        /// <summary>Resolves <paramref name="rel"/> against <paramref name="baseDir"/> and returns
        /// it only if it stays inside the theme root; otherwise null (blocks path traversal).</summary>
        private string? SandboxResolve(string rel, string baseDir)
        {
            if (Path.IsPathRooted(rel)) return null;
            string full;
            try { full = Path.GetFullPath(Path.Combine(baseDir, rel)); }
            catch { return null; }
            string rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
            return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) || full.Equals(_root, StringComparison.OrdinalIgnoreCase)
                ? full : null;
        }

        // ── small parse helpers ──────────────────────────────────────────────

        private static CarouselType? ParseCarouselType(string? s) => s switch
        {
            "horizontal" => CarouselType.Horizontal,
            "vertical" => CarouselType.Vertical,
            "horizontalWheel" => CarouselType.HorizontalWheel,
            "verticalWheel" => CarouselType.VerticalWheel,
            _ => null,
        };

        private static GradientType? ParseGradient(string? s) => s switch
        {
            "horizontal" => GradientType.Horizontal,
            "vertical" => GradientType.Vertical,
            _ => null,
        };

        private static List<ThemeViewKind> ParseViewKinds(string? name)
        {
            var kinds = new List<ThemeViewKind>();
            var toks = SplitCsv(name).Select(t => t.ToLowerInvariant()).ToHashSet();
            bool all = toks.Contains("all");
            if (all || toks.Contains("system")) kinds.Add(ThemeViewKind.System);
            if (all || toks.Contains("gamelist")) kinds.Add(ThemeViewKind.Gamelist);
            return kinds;
        }

        private bool NameMatches(XElement el, string target)
            => SplitCsv((string?)el.Attribute("name")).Contains(target);

        private bool NameAbsentOrMatches(XElement el, string target)
            => el.Attribute("name") == null || NameMatches(el, target);

        private static List<string> SplitCsv(string? s)
            => string.IsNullOrWhiteSpace(s)
                ? new List<string>()
                : s.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        private static void AddCsv(List<string> list, string? csv)
        {
            if (csv == null) return;
            foreach (var v in SplitCsv(csv)) if (!list.Contains(v)) list.Add(v);
        }

        private static bool? ParseBool(string? s) => s?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => null,
        };

        private static string? FirstSelectable(List<VariantDef> variants)
            => (variants.FirstOrDefault(v => v.Selectable) ?? variants.FirstOrDefault())?.Name;

        private static string Prefer(List<string> list, string fav)
            => list.Contains(fav) ? fav : (list.FirstOrDefault() ?? fav);

        private static void AddIf(List<string> list, string? val)
        {
            var v = val?.Trim();
            if (!string.IsNullOrEmpty(v) && !list.Contains(v)) list.Add(v);
        }

        private void Warn(string msg) { if (!_quiet) _warnings.Add(msg); }
        private void WarnTag(string tag) { if (!_quiet && _warnedTags.Add(tag)) _warnings.Add($"unsupported element <{tag}> skipped."); }
    }
}
