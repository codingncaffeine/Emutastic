using Emutastic.Services;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
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
        // Thread-safe cache: once an image is decoded + frozen it's shareable across threads.
        // WeakReference lets the GC reclaim images that are no longer displayed.
        private static readonly ConcurrentDictionary<string, WeakReference<BitmapImage>> _cache = new();

        /// <summary>Clears the entire image cache (call after bulk artwork changes).</summary>
        public static void ClearCache() => _cache.Clear();

        /// <summary>Evicts a single path from the cache (call when artwork is re-downloaded).</summary>
        public static void Evict(string? path)
        {
            if (!string.IsNullOrEmpty(path))
                _cache.TryRemove(path, out _);
        }

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is not string path || string.IsNullOrWhiteSpace(path))
                    return null;

                // Cache hit — return immediately, no File.Exists, no decode.
                if (_cache.TryGetValue(path, out var weakRef))
                {
                    if (weakRef.TryGetTarget(out var cached))
                        return cached;
                    _cache.TryRemove(path, out _); // GC collected the image; remove dead entry
                }

                if (!File.Exists(path))
                    return null;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 300; // cards are ≤148px; 300 covers 2x DPI scaling
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                _cache[path] = new WeakReference<BitmapImage>(bitmap);
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
        // values[0] = Console (string), values[1] = CardWidth (double, from parent ActualWidth),
        // values[2] = IsMixedView (bool) — when true, use uniform height so mixed-console
        //             views don't clip taller box art
        private const double MixedViewRatio = 0.73; // DVD keepcase — most common shape

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string console   = values.Length > 0 ? (values[0] as string ?? "") : "";
            double cardWidth = values.Length > 1 && values[1] is double d ? d : 148.0;
            bool   isMixed   = values.Length > 2 && values[2] is bool b && b;
            double ratio     = isMixed ? MixedViewRatio : RomService.GetBoxRatio(console);
            return Math.Round(cardWidth / ratio);
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
        private const double TitleArea = 40.0; // 12pt title + margin + breathing room

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