using Emutastic.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Emutastic.Converters
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class StringNotEmptyToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => !string.IsNullOrEmpty(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value?.ToString())
                ? Visibility.Visible
                : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class NotNullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value?.ToString())
                ? Visibility.Collapsed
                : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class StringToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is string colorStr && !string.IsNullOrWhiteSpace(colorStr))
                    return (System.Windows.Media.Color)
                        System.Windows.Media.ColorConverter.ConvertFromString(colorStr)!;
            }
            catch { }
            return System.Windows.Media.Colors.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class PathToImageConverter : IValueConverter
    {
        // Two-tier cache:
        //   Weak tier: ConcurrentDictionary<path, WeakReference<BitmapImage>> — same as
        //              before, lets GC reclaim images that fell out of all containers.
        //   Strong tier: an LRU keyed by path that pins the most recently-touched N
        //              decoded bitmaps so list virtualization recycling, console
        //              switches, and rapid scroll don't trigger re-decode.
        // The strong tier is the prefetch target for PreloadAsync.
        private static readonly ConcurrentDictionary<string, WeakReference<BitmapImage>> _weak = new();

        private const int StrongCapacity = 256;
        private static readonly object _strongLock = new();
        private static readonly Dictionary<string, LinkedListNode<string>> _strongIndex
            = new(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> _strongOrder = new();
        private static readonly Dictionary<string, BitmapImage> _strong
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Clears the entire image cache (call after bulk artwork changes).</summary>
        public static void ClearCache()
        {
            _weak.Clear();
            lock (_strongLock)
            {
                _strong.Clear();
                _strongIndex.Clear();
                _strongOrder.Clear();
            }
        }

        /// <summary>Evicts a single path from the cache (call when artwork is re-downloaded).</summary>
        public static void Evict(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _weak.TryRemove(path, out _);
            lock (_strongLock)
            {
                if (_strongIndex.TryGetValue(path, out var node))
                {
                    _strongOrder.Remove(node);
                    _strongIndex.Remove(path);
                }
                _strong.Remove(path);
            }
        }

        // Promote (or insert) path → bitmap in the strong-ref MRU tier.
        // Caller must already hold a reference to the bitmap; this method
        // freezes it if it isn't already frozen.
        private static void Promote(string path, BitmapImage bitmap)
        {
            if (!bitmap.IsFrozen) { try { bitmap.Freeze(); } catch { return; } }
            lock (_strongLock)
            {
                if (_strongIndex.TryGetValue(path, out var existing))
                {
                    _strongOrder.Remove(existing);
                    _strongOrder.AddFirst(existing);
                    _strong[path] = bitmap;
                    return;
                }
                var node = _strongOrder.AddFirst(path);
                _strongIndex[path] = node;
                _strong[path] = bitmap;
                while (_strongOrder.Count > StrongCapacity)
                {
                    var last = _strongOrder.Last!;
                    _strongOrder.RemoveLast();
                    _strongIndex.Remove(last.Value);
                    _strong.Remove(last.Value);
                    // Weak tier still holds it; GC can reclaim later.
                }
            }
        }

        // Internal decode routine — runs synchronously on whatever thread the
        // caller is on. BitmapImage with BeginInit/EndInit/Freeze is the
        // documented thread-safe-after-freeze pattern in WPF.
        private static BitmapImage? Decode(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 300; // cards are ≤148px; 300 covers 2x DPI scaling
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }

        /// <summary>
        /// Background-decodes the given paths and pins them in the strong-ref
        /// tier so the next on-screen request hits a warm cache. Skips paths
        /// already cached. Failures are silent.
        /// </summary>
        public static Task PreloadAsync(IEnumerable<string?> paths)
        {
            // Snapshot the input so the worker doesn't see a mutating sequence.
            var copy = new List<string>();
            foreach (var p in paths)
                if (!string.IsNullOrWhiteSpace(p)) copy.Add(p!);
            if (copy.Count == 0) return Task.CompletedTask;

            return Task.Run(() =>
            {
                foreach (var path in copy)
                {
                    // Already strong-cached → just promote.
                    BitmapImage? hit = null;
                    lock (_strongLock)
                    {
                        if (_strong.TryGetValue(path, out var s)) hit = s;
                    }
                    if (hit != null) { Promote(path, hit); continue; }

                    // Live in weak tier → reuse + promote.
                    if (_weak.TryGetValue(path, out var weakRef)
                        && weakRef.TryGetTarget(out var alive))
                    {
                        Promote(path, alive);
                        continue;
                    }

                    var bmp = Decode(path);
                    if (bmp != null)
                    {
                        _weak[path] = new WeakReference<BitmapImage>(bmp);
                        Promote(path, bmp);
                    }
                }
            });
        }

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is not string path || string.IsNullOrWhiteSpace(path))
                    return null;

                // Strong tier — promote on hit, returns instantly.
                BitmapImage? strongHit = null;
                lock (_strongLock)
                {
                    if (_strong.TryGetValue(path, out strongHit)
                        && _strongIndex.TryGetValue(path, out var node))
                    {
                        _strongOrder.Remove(node);
                        _strongOrder.AddFirst(node);
                    }
                }
                if (strongHit != null) return strongHit;

                // Weak tier — promote into strong on hit.
                if (_weak.TryGetValue(path, out var weakRef))
                {
                    if (weakRef.TryGetTarget(out var cached))
                    {
                        Promote(path, cached);
                        return cached;
                    }
                    _weak.TryRemove(path, out _);
                }

                var bitmap = Decode(path);
                if (bitmap == null) return null;

                _weak[path] = new WeakReference<BitmapImage>(bitmap);
                Promote(path, bitmap);
                return bitmap;
            }
            catch { }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Converts a console tag string to the art area height for the game grid card.
    /// Width is fixed at 148px; height = 148 / boxRatio.
    /// </summary>
    /// <summary>
    /// Proxy that exposes a DynamicResource as a bindable source so MultiBinding can consume it.
    /// Usage: &lt;local:BindingProxy x:Key="..." Data="{DynamicResource SomeKey}"/&gt;
    /// </summary>
    public class BindingProxy : System.Windows.Freezable
    {
        protected override System.Windows.Freezable CreateInstanceCore() => new BindingProxy();

        public static readonly System.Windows.DependencyProperty DataProperty =
            System.Windows.DependencyProperty.Register(
                nameof(Data), typeof(object), typeof(BindingProxy));

        public object Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    }

    /// <summary>
    /// Converts (Console, CardWidth) → card art height, preserving each console's box art aspect ratio.
    /// Used as IMultiValueConverter in the library DataTemplate so the height re-evaluates live
    /// whenever LibraryCardWidth changes.
    /// </summary>
    /// <summary>
    /// Display-time title normalizer: only re-cases titles that are uniformly
    /// lowercase or uniformly uppercase ("metal slug x" → "Metal Slug X",
    /// "SUPER MARIO" → "Super Mario"). Mixed-case titles are left alone so
    /// "Tetris DX", "FIFA 99", or anything the user has explicitly renamed
    /// keeps its intended capitalization.
    /// </summary>
    public class SmartTitleCaseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string ?? "";
            if (s.Length == 0) return s;
            bool allLower = true, allUpper = true;
            foreach (char c in s)
            {
                if (char.IsLetter(c))
                {
                    if (!char.IsLower(c)) allLower = false;
                    if (!char.IsUpper(c)) allUpper = false;
                }
            }
            if (allLower || allUpper)
                return culture.TextInfo.ToTitleCase(s.ToLowerInvariant());
            return s;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class ConsoleToArtHeightConverter : IMultiValueConverter
    {
        // values[0] = Console (string)
        // values[1] = CardWidth (double, the parent Border's Width DP — i.e. the
        //             LibraryCardWidth resource value, NOT ActualWidth).
        //             During VirtualizingWrapPanel realize/recycle passes
        //             triggered by margin changes (H/V spacing slider), this
        //             binding can transiently resolve to 0 before the
        //             DynamicResource settles. When that happens we fall back
        //             to looking the resource up directly on each Convert call
        //             so the height never collapses to zero — and we hard-min
        //             the final value to 1px so ClipToBounds on the inner art
        //             Border can't slice the image invisible.
        // values[2] = IsMixedView (bool) — when true, use uniform height so mixed-console
        //             views don't clip taller box art
        private const double MixedViewRatio = 0.73; // DVD keepcase — most common shape

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string console   = values.Length > 0 ? (values[0] as string ?? "") : "";
            double cardWidth = values.Length > 1 && values[1] is double d ? d : 0.0;
            bool   isMixed   = values.Length > 2 && values[2] is bool b && b;

            // Race-condition guard: if the bound Width is zero/NaN (transient
            // during layout) look up the resource directly. If that still
            // fails, fall back to the historical hard default of 148.
            if (!(cardWidth > 0.0) || double.IsNaN(cardWidth))
            {
                if (System.Windows.Application.Current?.TryFindResource("LibraryCardWidth")
                    is double r && r > 0.0)
                {
                    cardWidth = r;
                }
                else
                {
                    cardWidth = 148.0;
                }
            }

            double ratio = isMixed ? MixedViewRatio : RomService.GetBoxRatio(console);
            if (!(ratio > 0.0) || double.IsNaN(ratio)) ratio = 0.73;
            return Math.Max(1.0, Math.Round(cardWidth / ratio));
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Same inputs as ConsoleToArtHeight, but returns the TOTAL card height
    /// including the title caption beneath the art. Apply to the outer Border
    /// so VirtualizingWrapPanel has a deterministic cell size — without an
    /// explicit outer height, the panel's measurement caching can leave the
    /// title clipped on some consoles depending on the order cards were first
    /// realized.
    /// </summary>
    public class ConsoleToCardHeightConverter : IMultiValueConverter
    {
        // Caption area below the art: title TextBlock (Margin top 8 +
        // MaxHeight 34 = 42) plus the star-rating Grid (Margin top 3 + ~14
        // glyph height = 17), with a few extra px of breathing room. With
        // the prior 40 px budget, two-line titles caused the StackPanel to
        // overflow downward and the next row's card painted over the
        // previous row's caption, which the user perceived as the box art
        // getting cut off — most visible at small CardSize (148-170, where
        // more titles wrap to 2 lines) and small Spacing (4-8, where rows
        // have no gap to absorb the overflow).
        private const double TitleArea = 64.0;

        private static readonly ConsoleToArtHeightConverter _artHeight = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var art = _artHeight.Convert(values, targetType, parameter, culture);
            double h = art is double d ? d : 200.0;
            return h + TitleArea;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // ── List view (OpenEmu-style) cell formatters ────────────────────────────

    /// <summary>
    /// Game.Console string ("PS1", "SNES", …) → pack URI of the small system
    /// icon shown in the System column. Mirrors PreferencesWindow's mapping so
    /// nav sidebar, controls picker, and list view stay visually consistent.
    /// </summary>
    public class ConsoleTagToIconConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string tag || string.IsNullOrEmpty(tag)) return null;
            string? uri = tag switch
            {
                "Atari2600"    => "pack://application:,,,/Assets/system_icons/atari2600.jpg",
                "Atari7800"    => "pack://application:,,,/Assets/system_icons/atari7800.jpg",
                "Jaguar"       => "pack://application:,,,/Assets/system_icons/systemicons1_13.jpg",
                "NES"          => "pack://application:,,,/Assets/system_icons/nes_icon.jpg",
                "FDS"          => "pack://application:,,,/Assets/system_icons/famicon disk system.jpg",
                "SNES"         => "pack://application:,,,/Assets/system_icons/snes.jpg",
                "N64"          => "pack://application:,,,/Assets/system_icons/n64.jpg",
                "GameCube"     => "pack://application:,,,/Assets/system_icons/gamecube.jpg",
                "GB"           => "pack://application:,,,/Assets/system_icons/gameboy.jpg",
                "GBC"          => "pack://application:,,,/Assets/system_icons/gameboy.jpg",
                "GBA"          => "pack://application:,,,/Assets/system_icons/gba.jpg",
                "3DS"          => "pack://application:,,,/Assets/system_icons/3ds_icon.jpg",
                "NDS"          => "pack://application:,,,/Assets/system_icons/nds.jpg",
                "VirtualBoy"   => "pack://application:,,,/Assets/system_icons/virtualboy.jpg",
                "SMS"          => "pack://application:,,,/Assets/system_icons/sms.jpg",
                "Genesis"      => "pack://application:,,,/Assets/system_icons/genesis.jpg",
                "SegaCD"       => "pack://application:,,,/Assets/system_icons/genesis.jpg",
                "Sega32X"      => "pack://application:,,,/Assets/system_icons/32x.jpg",
                "Saturn"       => "pack://application:,,,/Assets/system_icons/saturn.jpg",
                "GameGear"     => "pack://application:,,,/Assets/system_icons/sms.jpg",
                "SG1000"       => "pack://application:,,,/Assets/system_icons/sms.jpg",
                "Dreamcast"    => "pack://application:,,,/Assets/system_icons/dreamcast.jpg",
                "PS1"          => "pack://application:,,,/Assets/system_icons/ps1.jpg",
                "PSP"          => "pack://application:,,,/Assets/system_icons/psp.jpg",
                "TG16"         => "pack://application:,,,/Assets/system_icons/TG16.jpg",
                "TGCD"         => "pack://application:,,,/Assets/system_icons/TG16.jpg",
                "NeoGeo"       => "pack://application:,,,/Assets/system_icons/neogeo.jpg",
                "NeoCD"        => "pack://application:,,,/Assets/system_icons/neogeo_cd.png",
                "NGP"          => "pack://application:,,,/Assets/system_icons/neo geo pocket.jpg",
                "NGPC"         => "pack://application:,,,/Assets/system_icons/neo geo pocket.jpg",
                "3DO"          => "pack://application:,,,/Assets/system_icons/3d0.jpg",
                "CDi"          => "pack://application:,,,/Assets/system_icons/cdi_icon.jpg",
                "ColecoVision" => "pack://application:,,,/Assets/system_icons/coleco.jpg",
                "Vectrex"      => "pack://application:,,,/Assets/system_icons/vectrex.jpg",
                _              => null,
            };
            if (uri == null) return null;
            try { return new BitmapImage(new Uri(uri, UriKind.Absolute)); } catch { return null; }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Rating int (0–5) → 5-glyph star string with filled (★) / empty (☆) stars.
    /// </summary>
    public class RatingToStarsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int r = value is int n ? Math.Clamp(n, 0, 5) : 0;
            return new string('★', r) + new string('☆', 5 - r);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Rating int (0–5) → string of N filled '★' glyphs only (no trailing
    /// empties). Used for the white-overlay layer when the rating cell is
    /// rendered as two stacked TextBlocks (concave-grey empties below, white
    /// filled stars on top — the empty layer shows through past the Nth star).
    /// </summary>
    public class RatingToFilledStarsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int r = value is int n ? Math.Clamp(n, 0, 5) : 0;
            return new string('★', r);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// DateTime? → medium-format date ("Mar 5, 2024") or empty string when null.
    /// Matches OpenEmu's NSDateFormatter dateStyle = .medium.
    /// </summary>
    public class LastPlayedToMediumDateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime dt) return dt.ToString("MMM d, yyyy", culture);
            return string.Empty;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Integer → string, with 0 rendered as blank. Mirrors OpenEmu's behavior
    /// of leaving Play Count / Save State Count empty when the value is zero.
    /// </summary>
    public class ZeroToBlankConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int n && n != 0) return n.ToString("N0", culture);
            return string.Empty;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}