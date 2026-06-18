using System.Collections.Generic;

namespace Emutastic.Models.EmuTv
{
    // ─────────────────────────────────────────────────────────────────────────
    //  EmuTV theme model — the parsed, in-memory representation of an EmuTV theme.
    //
    //  Deliberately ES-DE-shaped (same views/elements/axes/property names) so that
    //  (a) authors who know ES-DE feel at home and (b) the ES-DE importer is a
    //  near-mechanical mapping. This is DATA only — the parser fills it from XML and
    //  the renderer turns it into a WPF tree. Nothing here touches WPF or XAML.
    //
    //  Property conventions:
    //   • A null property means "unset → inherit / use default" (matches ES-DE,
    //     where you only declare a property to override it).
    //   • Coordinates are stored already-resolved as Vec2 — the parser substitutes
    //     ${variables} and parses the numbers before populating the model.
    //   • Open-ended bindings (media slots, metadata fields) stay as strings so an
    //     imported theme with an unfamiliar value degrades instead of throwing.
    //
    //  Properties grow alongside the parser/renderer.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>A normalized 0–1 (or off-screen) coordinate pair, resolved from XML.</summary>
    public readonly struct Vec2
    {
        public double X { get; }
        public double Y { get; }
        public Vec2(double x, double y) { X = x; Y = y; }
        public override string ToString() => $"{X} {Y}";
    }

    public enum ThemeViewKind { System, Gamelist, Both }

    public enum CarouselType { Horizontal, Vertical, HorizontalWheel, VerticalWheel }

    public enum GradientType { None, Horizontal, Vertical }

    /// <summary>Discriminator so the renderer can switch without reflection.</summary>
    public enum ElementKind
    {
        // primary (navigable — one per view)
        Carousel, Grid, TextList,
        // secondary
        Image, Video, Text, Badges, Rating, DateTime, GamelistInfo, Animation,
        // special
        HelpSystem,
        // EmuTV-only (no ES-DE equivalent; importer skips these)
        TvComposite, SaveStateCarousel, ContinueTile,
    }

    // ── Top-level theme ──────────────────────────────────────────────────────

    /// <summary>A fully parsed EmuTV theme package (one folder under EmuTvThemes/).</summary>
    public sealed class EmuTvTheme
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Author { get; set; } = "";
        public string Version { get; set; } = "1.0.0";
        public string Description { get; set; } = "";

        /// <summary>Absolute path to the theme's root folder on disk (for asset resolution).</summary>
        public string RootPath { get; set; } = "";

        /// <summary>True for the shipped "EmuTV Default" (and other seed) themes.</summary>
        public bool IsBuiltin { get; set; }

        /// <summary>The customization axes the user picks from (capabilities.xml).</summary>
        public ThemeCapabilities Capabilities { get; set; } = new();

        /// <summary>Resolved layouts keyed by variant name.</summary>
        public Dictionary<string, ThemeVariant> Variants { get; set; } = new();
    }

    // ── Axes (capabilities.xml) ──────────────────────────────────────────────

    public sealed class ThemeCapabilities
    {
        public string ThemeName { get; set; } = "";
        public List<string> AspectRatios { get; set; } = new();   // "16:9", "4:3", …
        public List<string> FontSizes { get; set; } = new();      // "small".."x-large"
        public List<ColorSchemeDef> ColorSchemes { get; set; } = new();
        public List<VariantDef> Variants { get; set; } = new();
        public List<TransitionsProfile> Transitions { get; set; } = new();
    }

    public sealed class ColorSchemeDef
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
    }

    public sealed class VariantDef
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
        public bool Selectable { get; set; } = true;

        /// <summary>Graceful auto-fallback when the selected media is missing.</summary>
        public NoMediaOverride? Override { get; set; }
    }

    /// <summary>
    /// ES-DE's <c>&lt;override&gt;&lt;trigger&gt;noMedia&lt;/trigger&gt;…</c>: if every listed
    /// media type is absent for a game, switch to <see cref="UseVariant"/>. This is the pattern
    /// that lets a hi-res-art layout fall back to a text/basic layout when art is missing.
    /// </summary>
    public sealed class NoMediaOverride
    {
        public string Trigger { get; set; } = "noMedia";
        public List<string> MediaTypes { get; set; } = new();
        public string UseVariant { get; set; } = "";
    }

    public sealed class TransitionsProfile
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
        public bool Selectable { get; set; } = true;
        // Per view-pair transition styles ("instant" | "slide" | "fade"); null = engine default.
        public string? SystemToSystem { get; set; }
        public string? SystemToGamelist { get; set; }
        public string? GamelistToGamelist { get; set; }
        public string? GamelistToSystem { get; set; }
        public string? StartupToSystem { get; set; }
        public string? StartupToGamelist { get; set; }
    }

    // ── Layout (theme.xml) ───────────────────────────────────────────────────

    /// <summary>One named layout. A theme ships many; the user picks one per the variant axis.</summary>
    public sealed class ThemeVariant
    {
        public string Name { get; set; } = "";
        public List<ThemeView> Views { get; set; } = new();
    }

    public sealed class ThemeView
    {
        public ThemeViewKind Kind { get; set; }
        public List<ThemeElement> Elements { get; set; } = new();
    }

    // ── Elements ─────────────────────────────────────────────────────────────

    /// <summary>Common base for every placeable element. Null = inherit/default.</summary>
    public abstract class ThemeElement
    {
        public abstract ElementKind Kind { get; }

        public string Name { get; set; } = "";
        public Vec2? Pos { get; set; }
        public Vec2? Size { get; set; }
        public Vec2? Origin { get; set; }
        public double? ZIndex { get; set; }
        public double? Opacity { get; set; }
        public double? Rotation { get; set; }
        public Vec2? RotationOrigin { get; set; }
        public bool? Visible { get; set; }
    }

    // primary --------------------------------------------------------------

    public sealed class CarouselElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.Carousel;
        public CarouselType Type { get; set; } = CarouselType.Horizontal;
        /// <summary>Logical media slots (priority order) for gamelist items, e.g. ["cover","screenshot"].</summary>
        public List<string> ImageTypes { get; set; } = new();
        /// <summary>System-view static image path (e.g. a console logo), pre-${var}-resolved.</summary>
        public string? StaticImage { get; set; }
        public string? DefaultImage { get; set; }
        public double? MaxItemCount { get; set; }            // horizontal/vertical
        public int? ItemsBeforeCenter { get; set; }          // wheels
        public int? ItemsAfterCenter { get; set; }           // wheels
        public Vec2? ItemSize { get; set; }
        public Vec2? ItemSpacing { get; set; }
        public double? ItemScale { get; set; }               // selected item scale (default 1.2)
        public double? ItemRotation { get; set; }            // wheels (default 7.5)
        public Vec2? ItemRotationOrigin { get; set; }        // wheels (default -3 0.5)
        public bool? ItemAxisHorizontal { get; set; }        // wheels: keep items upright
        public string? ItemHorizontalAlignment { get; set; } // left|center|right
        public string? ItemVerticalAlignment { get; set; }   // top|center|bottom
        public string? WheelHorizontalAlignment { get; set; }
        public string? WheelVerticalAlignment { get; set; }
        public double? HorizontalOffset { get; set; }        // carousel offset within its area
        public double? VerticalOffset { get; set; }
        public string? ImageFit { get; set; }                // contain|fill|cover
        public string? Color { get; set; }                   // background panel color
        public string? ColorEnd { get; set; }
        public string? TextColor { get; set; }
        public double? FontSize { get; set; }
        public string? LetterCase { get; set; }
        public string? Text { get; set; }                    // literal fallback (system view)
        public string? SelectorColor { get; set; }
        public double? UnfocusedItemOpacity { get; set; }    // default 0.5
        // typography
        public string? FontPath { get; set; }
        public double? LineSpacing { get; set; }
        public bool? SystemNameSuffix { get; set; }
        // panel background gradient (pairs with Color/ColorEnd)
        public GradientType Gradient { get; set; } = GradientType.None;
        // per-item image tint / processing
        public string? ImageColor { get; set; }
        public string? ImageColorEnd { get; set; }
        public GradientType ImageGradient { get; set; } = GradientType.None;
        public string? ImageSelectedColor { get; set; }
        public string? ImageInterpolation { get; set; }      // nearest|linear
        public double? ImageSaturation { get; set; }
        public double? ImageBrightness { get; set; }
        public double? ImageCornerRadius { get; set; }
        public Vec2? ImageCropPos { get; set; }
        public double? UnfocusedItemSaturation { get; set; }
        public double? UnfocusedItemDimming { get; set; }
        // selected/unselected text styling
        public string? TextBackgroundColor { get; set; }
        public string? TextSelectedColor { get; set; }
        public string? TextSelectedBackgroundColor { get; set; }
    }

    public sealed class GridElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.Grid;
        public List<string> ImageTypes { get; set; } = new();
        public string? StaticImage { get; set; }
        public string? DefaultImage { get; set; }
        public Vec2? ItemSize { get; set; }
        public Vec2? ItemSpacing { get; set; }
        public double? ItemScale { get; set; }          // default 1.05
        public string? SelectorColor { get; set; }
        public string? ImageFit { get; set; }          // "contain" | "fill" | "cover"
        public double? UnfocusedItemOpacity { get; set; }  // default 1
        public string? TextColor { get; set; }
        public double? FontSize { get; set; }
        public string? LetterCase { get; set; }
        public string? FontPath { get; set; }
        public string? Text { get; set; }                  // literal fallback (system view)
    }

    public sealed class TextListElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.TextList;
        public string? FontPath { get; set; }
        public double? FontSize { get; set; }
        public double? LineSpacing { get; set; }        // default 1.5
        public string? PrimaryColor { get; set; }       // unselected row text
        public string? SelectedColor { get; set; }      // selected row text
        public string? SelectorColor { get; set; }      // selection bar
        public string? SelectedBackgroundColor { get; set; }
        public string? SecondaryColor { get; set; }     // right-aligned secondary text
        public string? Alignment { get; set; }          // "left" | "center" | "right"
        public string? LetterCase { get; set; }
    }

    // secondary ------------------------------------------------------------

    public sealed class ImageElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.Image;
        /// <summary>Static asset path (mutually exclusive with <see cref="ImageTypes"/>), pre-resolved.</summary>
        public string? Path { get; set; }
        /// <summary>Logical media slots (priority order) when bound to game art.</summary>
        public List<string> ImageTypes { get; set; } = new();
        public string? DefaultImage { get; set; }
        public Vec2? MaxSize { get; set; }
        public Vec2? CropSize { get; set; }
        public Vec2? CropPos { get; set; }
        public bool? Tile { get; set; }
        public string? Color { get; set; }              // tint / solid (hex)
        public string? ColorEnd { get; set; }           // gradient end (hex)
        public GradientType Gradient { get; set; } = GradientType.None;
        public string? Interpolation { get; set; }       // nearest|linear
        public Vec2? TileSize { get; set; }
        public string? TileHorizontalAlignment { get; set; }
        public string? TileVerticalAlignment { get; set; }
    }

    public sealed class VideoElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.Video;
        public List<string> ImageTypes { get; set; } = new();   // poster/source slots
        public string? DefaultImage { get; set; }
        public Vec2? MaxSize { get; set; }
        public Vec2? CropSize { get; set; }
        public bool? Audio { get; set; }
    }

    public sealed class TextElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.Text;
        /// <summary>Literal text (e.g. a label), pre-${var}-resolved. Null when data-bound.</summary>
        public string? Text { get; set; }
        /// <summary>Bound metadata field (e.g. "name", "genre", "description"). Null when literal.</summary>
        public string? Metadata { get; set; }
        /// <summary>Bound SYSTEM-view data field (e.g. "fullname", "gamecount"). Null when not bound.</summary>
        public string? SystemData { get; set; }
        public string? FontPath { get; set; }
        public double? FontSize { get; set; }
        public string? Color { get; set; }
        public string? Alignment { get; set; }          // horizontal alignment
        public bool? Container { get; set; }            // scroll/marquee long text
        public string? ContainerType { get; set; }      // "horizontal" | "vertical"
        public string? DefaultValue { get; set; }
        public string? LetterCase { get; set; }
        public double? LineSpacing { get; set; }         // default 1.5
        public string? VerticalAlignment { get; set; }   // top|center|bottom
        public string? BackgroundColor { get; set; }
    }

    public sealed class BadgesElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.Badges;
        public int? Lines { get; set; }
        public int? ItemsPerLine { get; set; }
        public string? Slots { get; set; }              // "all" or a comma list
        public string? IconColor { get; set; }
        public string? IconColorEnd { get; set; }
        public string? HorizontalAlignment { get; set; }
        public string? Direction { get; set; }          // "row" | "column"
        public Vec2? ItemMargin { get; set; }
        /// <summary>Per-slot icon overrides, keyed by badge name (folder/favorite/completed/…).</summary>
        public Dictionary<string, string> CustomBadgeIcons { get; } = new();
    }

    public sealed class RatingElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.Rating;
        public string? Color { get; set; }
        public string? FilledImage { get; set; }
        public string? UnfilledImage { get; set; }
    }

    public sealed class DateTimeElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.DateTime;
        public string? Metadata { get; set; }           // "releasedate" | "lastplayed" | …
        public string? Format { get; set; }
        public string? Color { get; set; }
        public double? FontSize { get; set; }
        public string? FontPath { get; set; }
    }

    /// <summary>Self-populating game-count / filter info line (gamelist view only).</summary>
    public sealed class GamelistInfoElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.GamelistInfo;
        public string? Color { get; set; }
        public string? BackgroundColor { get; set; }
        public double? FontSize { get; set; }
        public string? FontPath { get; set; }
        public string? Alignment { get; set; }           // horizontal alignment
    }

    /// <summary>GIF or Lottie animation. We render a still (first GIF frame) for preview fidelity.</summary>
    public sealed class AnimationElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.Animation;
        public string? Path { get; set; }
        public Vec2? MaxSize { get; set; }
    }

    // special --------------------------------------------------------------

    public sealed class HelpSystemElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.HelpSystem;
        public string? TextColor { get; set; }
        public string? IconColor { get; set; }
        public double? FontSize { get; set; }
        public string? FontPath { get; set; }
        public string? Scope { get; set; }               // shared|view|menu|none
        public string? LetterCase { get; set; }
        public string? BackgroundColor { get; set; }
    }

    // EmuTV-only -----------------------------------------------------------

    /// <summary>
    /// The signature EmuTV preview: a transparent-cutout bezel image laid over a "screen" rect
    /// that shows a video (or fallback cover). In ES-DE terms it's a <c>video</c>/<c>image</c>
    /// stacked under an <c>image</c> by z-order; we model it as one element for authoring ease.
    /// </summary>
    public sealed class TvCompositeElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.TvComposite;
        public string? BezelImage { get; set; }         // transparent-screen frame asset
        /// <summary>Screen rectangle within the bezel, normalized to the composite (x,y,w,h).</summary>
        public Vec2? ScreenPos { get; set; }
        public Vec2? ScreenSize { get; set; }
        public List<string> ImageTypes { get; set; } = new();  // video/poster source slots
        public string? StandByImage { get; set; }       // shown while a snap downloads
    }

    public sealed class SaveStateCarouselElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.SaveStateCarousel;
        public string? LabelImage { get; set; }         // the "VCR tape" label asset
        public Vec2? ItemSize { get; set; }
        public Vec2? ItemSpacing { get; set; }
        public string? SelectorColor { get; set; }
        public string? EmptyText { get; set; }          // "No save states yet"
    }

    public sealed class ContinueTileElement : ThemeElement
    {
        public override ElementKind Kind => ElementKind.ContinueTile;
        public string? Label { get; set; }              // "Continue"
        public string? Color { get; set; }
    }
}
