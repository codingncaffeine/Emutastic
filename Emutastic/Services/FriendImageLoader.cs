using System;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Emutastic.Services
{
    /// <summary>
    /// Friend-feature image loader. Matches the working pattern at
    /// <c>MainWindow.xaml.cs:2572</c> (RAProfileAvatar) — UriSource
    /// before CacheOption, no DecodePixelWidth, no Freeze. Those
    /// "optimizations" race the async download for remote HTTPS URIs
    /// and silently fail through DownloadFailed/DecodeFailed events
    /// that synchronous try/catch can't see.
    ///
    /// All Friends-tab images route through here so future tweaks don't
    /// re-introduce the bad pattern.
    /// </summary>
    public static class FriendImageLoader
    {
        /// <summary>
        /// Loads the image at <paramref name="url"/> into <paramref name="target"/>.
        /// Logs every async stage to ra.log under the given <paramref name="label"/>
        /// so the user can see what's happening from a release build.
        /// </summary>
        public static void Load(Image target, string? url, string label, string? context = null)
        {
            if (target == null) return;
            if (string.IsNullOrWhiteSpace(url))
            {
                RaLog.Write($"[FriendImg:{label}] url empty {(context ?? "")}".TrimEnd());
                return;
            }
            try
            {
                string urlSnap = url;
                string labelSnap = label;
                string ctxSnap = context ?? "";
                var bmp = new BitmapImage();
                bmp.DownloadFailed += (s, ev) =>
                    RaLog.Write($"[FriendImg:{labelSnap}] DownloadFailed {ctxSnap} url=[{urlSnap}]: {ev.ErrorException?.Message}");
                bmp.DecodeFailed += (s, ev) =>
                    RaLog.Write($"[FriendImg:{labelSnap}] DecodeFailed {ctxSnap} url=[{urlSnap}]: {ev.ErrorException?.Message}");
                bmp.DownloadCompleted += (s, ev) =>
                    RaLog.Write($"[FriendImg:{labelSnap}] DownloadCompleted {ctxSnap} url=[{urlSnap}]");
                bmp.BeginInit();
                bmp.UriSource = new Uri(urlSnap);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                target.Source = bmp;
                RaLog.Write($"[FriendImg:{labelSnap}] init {ctxSnap} url=[{urlSnap}]");
            }
            catch (Exception ex)
            {
                RaLog.Write($"[FriendImg:{label}] EX {context ?? ""} url=[{url}]: {ex.GetType().Name} {ex.Message}");
            }
        }
    }
}
