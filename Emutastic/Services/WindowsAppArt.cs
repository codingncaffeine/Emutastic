using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Emutastic.Services
{
    /// <summary>
    /// Generated covers for Windows apps: the app's own shell icon (the one Explorer shows, so
    /// shortcuts and Steam links get theirs too) centred on a 2:3 card in the platform's colours.
    /// It gives every app a recognisable card straight away, with no account and no network; a
    /// real cover found later replaces it.
    /// </summary>
    internal static class WindowsAppArt
    {
        private const int CoverWidth = 512;
        private const int CoverHeight = 768;   // 2:3, the shape of Steam and SteamGridDB covers

        /// <summary>Writes the icon cover for <paramref name="appPath"/> to <paramref name="destPng"/>.
        /// False when the shell has no icon for it or writing failed; <paramref name="failure"/>
        /// then says why.</summary>
        public static bool TryWriteIconCover(string appPath, string destPng, out string? failure)
        {
            // Shell icon extraction wants a COM single-threaded apartment, and WPF drawing wants an
            // STA thread too; import runs on the thread pool, so give each cover its own STA thread.
            // One at a time: a cover takes a fraction of a second, and the shell's image list is
            // shared by the whole process.
            string? why = null;
            lock (Gate)
            {
                var thread = new Thread(() =>
                {
                    try { why = WriteIconCover(appPath, destPng); }
                    catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message}"; }
                    finally { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
                })
                { IsBackground = true, Name = "WindowsAppArt" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
            }
            failure = why;
            return why == null;
        }

        private static readonly object Gate = new();

        /// <returns>Null on success, else why no cover was written.</returns>
        private static string? WriteIconCover(string appPath, string destPng)
        {
            BitmapSource? icon = ExtractIcon(appPath, out string? why);
            if (icon == null) return why ?? "the shell has no icon for it";

            // An icon smaller than the requested size comes back drawn in the corner of a
            // transparent canvas; use just the drawn part, and don't blow a tiny icon up past 4×.
            icon = TrimToContent(icon, out int contentSize);
            if (contentSize == 0) return "the icon is blank";
            double drawn = Math.Min(260, contentSize * 4.0);

            var (_, accentHex) = RomService.GetConsoleColors(WindowsApps.ConsoleTag);
            var accent = (Color)ColorConverter.ConvertFromString(accentHex);

            var group = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.HighQuality);
            using (var dc = group.Open())
            {
                var full = new Rect(0, 0, CoverWidth, CoverHeight);
                dc.DrawRectangle(new LinearGradientBrush(Shade(accent, 0.42), Shade(accent, 0.10), 90), null, full);

                var centre = new Point(CoverWidth / 2.0, CoverHeight * 0.46);
                var glow = new RadialGradientBrush(Color.FromArgb(110, accent.R, accent.G, accent.B), Colors.Transparent);
                dc.DrawEllipse(glow, null, centre, drawn * 0.95, drawn * 0.95);

                dc.DrawImage(icon, new Rect(centre.X - drawn / 2, centre.Y - drawn / 2, drawn, drawn));
            }

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawDrawing(group);
            var target = new RenderTargetBitmap(CoverWidth, CoverHeight, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            Directory.CreateDirectory(Path.GetDirectoryName(destPng)!);
            string tmp = destPng + ".tmp";
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            File.Move(tmp, destPng, overwrite: true);
            return null;
        }

        private static Color Shade(Color c, double factor)
            => Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

        /// <summary>The largest icon the shell has for the file (256 px, else 48 px), with alpha.</summary>
        private static BitmapSource? ExtractIcon(string path, out string? failure)
        {
            failure = null;
            var info = new SHFILEINFO();
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX) == IntPtr.Zero)
            {
                failure = "the shell couldn't read the file";
                return null;
            }

            foreach (int size in new[] { SHIL_JUMBO, SHIL_EXTRALARGE })
            {
                Guid iid = IID_IImageList;
                int hr = SHGetImageList(size, ref iid, out IntPtr list);
                if (hr != 0 || list == IntPtr.Zero) { failure = $"image list {size}: 0x{hr:X8}"; continue; }
                try
                {
                    hr = GetIcon(list, info.iIcon, ILD_TRANSPARENT, out IntPtr hIcon);
                    if (hr != 0 || hIcon == IntPtr.Zero) { failure = $"icon {info.iIcon} in list {size}: 0x{hr:X8}"; continue; }
                    try
                    {
                        var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        // Copy out of the icon handle before it is destroyed.
                        var copy = new WriteableBitmap(new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0));
                        copy.Freeze();
                        return copy;
                    }
                    finally { DestroyIcon(hIcon); }
                }
                finally { Marshal.Release(list); }
            }
            return null;
        }

        /// <summary>
        /// IImageList::GetIcon, called through the vtable. The system image list is one object
        /// shared by the whole process, so a .NET wrapper for it would be shared too, and
        /// releasing that wrapper on one cover thread breaks it for another running in parallel;
        /// a raw call with plain reference counting has no such wrapper.
        /// </summary>
        private static unsafe int GetIcon(IntPtr imageList, int index, int flags, out IntPtr hIcon)
        {
            // vtable: IUnknown (3 slots), then Add, ReplaceIcon, SetOverlayImage, Replace,
            // AddMasked, Draw, Remove, GetIcon — slot 10.
            IntPtr* vtable = *(IntPtr**)imageList;
            var getIcon = (delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)vtable[10];
            IntPtr icon = IntPtr.Zero;
            int hr = getIcon(imageList, index, flags, &icon);
            hIcon = icon;
            return hr;
        }

        /// <summary>
        /// Crops a square around the drawn (non-transparent) part of the icon when that part is
        /// much smaller than the canvas; otherwise returns the icon unchanged. Reports the side of
        /// the drawn square in pixels (0 = nothing drawn).
        /// </summary>
        private static BitmapSource TrimToContent(BitmapSource icon, out int contentSize)
        {
            int w = icon.PixelWidth, h = icon.PixelHeight;
            var px = new byte[w * h * 4];
            icon.CopyPixels(px, w * 4, 0);

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[(y * w + x) * 4 + 3] > 8)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }

            if (maxX < 0) { contentSize = 0; return icon; }

            int side = Math.Max(maxX - minX + 1, maxY - minY + 1);
            if (side > w / 2 && side > h / 2)
            {
                contentSize = Math.Max(w, h);
                return icon;
            }

            // Keep the drawn part centred in a square crop, clamped to the canvas.
            int cx = (minX + maxX + 1) / 2, cy = (minY + maxY + 1) / 2;
            int left = Math.Clamp(cx - side / 2, 0, Math.Max(0, w - side));
            int top = Math.Clamp(cy - side / 2, 0, Math.Max(0, h - side));
            int sw = Math.Min(side, w - left), sh = Math.Min(side, h - top);
            var cropped = new CroppedBitmap(icon, new Int32Rect(left, top, sw, sh));
            cropped.Freeze();
            contentSize = Math.Max(sw, sh);
            return cropped;
        }

        // ── Shell interop ───────────────────────────────────────────────────────────────────
        private const uint SHGFI_SYSICONINDEX = 0x000004000;
        private const int SHIL_EXTRALARGE = 0x2;   // 48 px
        private const int SHIL_JUMBO = 0x4;        // 256 px
        private const int ILD_TRANSPARENT = 0x1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

        [DllImport("shell32.dll")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
