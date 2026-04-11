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
    public class ConsoleToArtHeightConverter : IMultiValueConverter
    {
        // values[0] = Console (string), values[1] = CardWidth (double via BindingProxy)
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string console   = values.Length > 0 ? (values[0] as string ?? "") : "";
            double cardWidth = values.Length > 1 && values[1] is double d ? d : 148.0;
            double ratio     = RomService.GetBoxRatio(console);
            return Math.Round(cardWidth / ratio);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}