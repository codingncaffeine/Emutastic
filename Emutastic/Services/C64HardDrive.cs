using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// Commodore 64 hard drives (a CMD HD on the serial bus, Kernal64): one per game, the file
    /// "Saves/C64/&lt;content name&gt;.dhd" the core looks for at launch ("Kernal64.dhd" for an entry started with no
    /// content, the BASIC entry). They are made by the core itself (its kernal64_make_hard_drive export, not part
    /// of the libretro API), which installs the drive's DOS and formats its partitions with the drive's own
    /// programs; that needs the CMD HD boot ROM and utilities disk in the System folder. Hard drives stay out of
    /// the GitHub backup (GitHubSyncService.IsC64HardDrive).
    /// </summary>
    public static class C64HardDrive
    {
        public const long MinBytes = 20L << 20;
        public const long MaxBytes = 1L << 30;   // the drive's DOS reaches 1 GB of a disk

        /// <summary>The sizes offered: label and bytes.</summary>
        public static readonly (string Label, long Bytes)[] Sizes =
        {
            ("20 MB", 20L << 20), ("40 MB", 40L << 20), ("100 MB", 100L << 20), ("200 MB", 200L << 20),
            ("500 MB", 500L << 20), ("1 GB", 1L << 30),
        };

        public static string PathFor(Game game)
        {
            string stem = LibretroCore.IsNoContentMarker(game.RomPath) ? "Kernal64" : Path.GetFileNameWithoutExtension(game.RomPath);
            return Path.Combine(AppPaths.GetFolder("Saves", "C64"), stem + ".dhd");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int RequirementsFn([MarshalAs(UnmanagedType.LPUTF8Str)] string systemDir);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ProgressFn(int percent, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int MakeFn([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ulong bytes,
                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string systemDir, IntPtr progress, IntPtr user,
                                    IntPtr error, nuint errorSize);

        private static T? Export<T>(string corePath, string name) where T : Delegate
        {
            if (!File.Exists(corePath)) return null;
            IntPtr lib = NativeLibrary.Load(corePath);
            return NativeLibrary.TryGetExport(lib, name, out IntPtr fn) ? Marshal.GetDelegateForFunctionPointer<T>(fn) : null;
        }

        /// <summary>What is missing to make a drive (empty = nothing), as words for the user; null = the core
        /// cannot make drives (an older Kernal64).</summary>
        public static string? Missing(string corePath)
        {
            var fn = Export<RequirementsFn>(corePath, "kernal64_hard_drive_requirements");
            if (fn == null) return null;
            int have = fn(AppPaths.GetFolder("System"));
            var sb = new StringBuilder();
            if ((have & 4) == 0) sb.Append("the C64's system ROMs, ");
            if ((have & 1) == 0) sb.Append("the CMD HD boot ROM, ");
            if ((have & 2) == 0) sb.Append("the CMD HD utilities disk, ");
            return sb.Length == 0 ? "" : sb.ToString(0, sb.Length - 2);
        }

        /// <summary>Makes the drive (blocking: call it off the UI thread). Returns the number of partitions, or
        /// throws with the core's reason. Cancelling removes the half-made file.</summary>
        public static int Make(string corePath, string path, long bytes, Action<int> progress, CancellationToken cancel)
        {
            var fn = Export<MakeFn>(corePath, "kernal64_make_hard_drive")
                     ?? throw new InvalidOperationException("this Kernal64 cannot make hard drives");
            ProgressFn cb = (p, _) => { progress(p); return cancel.IsCancellationRequested ? 0 : 1; };
            IntPtr err = Marshal.AllocHGlobal(512);
            try
            {
                Marshal.WriteByte(err, 0);
                int parts = fn(path, (ulong)bytes, AppPaths.GetFolder("System"), Marshal.GetFunctionPointerForDelegate(cb),
                               IntPtr.Zero, err, 512);
                GC.KeepAlive(cb);
                if (parts > 0) return parts;
                throw new InvalidOperationException(Marshal.PtrToStringUTF8(err) ?? "the drive was not made");
            }
            finally { Marshal.FreeHGlobal(err); }
        }
    }
}
