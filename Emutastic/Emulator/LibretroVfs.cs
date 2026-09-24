using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Emutastic.Emulator
{
    /// <summary>
    /// The libretro virtual file system (RETRO_ENVIRONMENT_GET_VFS_INTERFACE, 45|EXPERIMENTAL),
    /// API v3 — the same surface RetroArch hands to cores. A core that asks for it routes ALL of
    /// its file I/O through these callbacks (libretro-common's filestream/dirent wrappers switch
    /// over wholesale), and Stella's libretro port has required it since 2026-08-23: its file node
    /// only learns "this is a file" from our stat(), so without the interface every ROM load fails
    /// with "Unrecognized ROM file type".
    ///
    /// One process-wide function table, allocated on first use and never freed — a core keeps the
    /// pointer for its whole life. Handles are GCHandles to <see cref="VfsFile"/> / <see cref="VfsDir"/>
    /// objects, opaque to the core exactly like a FILE*. Every callback is UnmanagedCallersOnly and
    /// catches everything: an exception escaping into the core would take the game host down.
    ///
    /// Semantics follow RetroArch's vfs_implementation.c: READ = "rb", WRITE = "wb", READ_WRITE =
    /// "w+b", either write mode with UPDATE_EXISTING = "r+b"; mkdir answers -2 for an existing
    /// directory; readdir skips dot entries unless include_hidden. Character-special detection is
    /// not available from .NET on Unix, so a device node reports as a plain file (no core opens /dev).
    ///
    /// EMUTASTIC_NO_VFS=1 refuses the interface (the pre-fix behaviour): the negative control for the
    /// Stella case, and a diagnostic should a core ever misbehave under the VFS.
    /// </summary>
    internal static unsafe class LibretroVfs
    {
        public const uint Version = 3;

        // libretro.h: RETRO_VFS_FILE_ACCESS_*, RETRO_VFS_SEEK_POSITION_*, RETRO_VFS_STAT_*
        const uint AccessRead = 1, AccessWrite = 2, AccessUpdateExisting = 4;
        const int SeekStart = 0, SeekCurrent = 1, SeekEnd = 2;
        public const int StatIsValid = 1, StatIsDirectory = 2;

        // struct retro_vfs_interface carries 26 pointer slots through API v5; the 19 of v1–v3 are
        // filled, the rest stay null — a core that negotiated v3 never reads them.
        const int TableSlots = 26;
        const int LogLimit = 20;

        static IntPtr _table;
        static readonly object _gate = new();
        static long _ops, _errors, _logged;

        /// <summary>Instrument: callbacks served / callbacks that failed unexpectedly (a read-open
        /// of a missing file is an ordinary probe and is not counted).</summary>
        public static long Ops => Interlocked.Read(ref _ops);
        public static long Errors => Interlocked.Read(ref _errors);
        public static bool Disabled => Environment.GetEnvironmentVariable("EMUTASTIC_NO_VFS") == "1";

        sealed class VfsFile
        {
            public required FileStream Stream;
            public required string Path;
            public IntPtr PathUtf8;   // get_path() must stay valid while the handle is open
        }

        sealed class VfsDir
        {
            public readonly List<(IntPtr NameUtf8, bool IsDir)> Entries = new();
            public int Index = -1;    // readdir advances before the first entry becomes current
        }

        /// <summary>
        /// Answers GET_VFS_INTERFACE. <paramref name="data"/> is a retro_vfs_interface_info
        /// { uint32 required_interface_version; retro_vfs_interface* iface }: refused when the core
        /// needs a newer API than v3, otherwise the version actually provided and the table are
        /// written back (RetroArch's contract).
        /// </summary>
        public static bool TryProvide(IntPtr data)
        {
            if (data == IntPtr.Zero) return false;
            uint required = (uint)Marshal.ReadInt32(data);
            if (Disabled)
            {
                Trace.WriteLine($"[VFS] core asked for VFS v{required}; refused (EMUTASTIC_NO_VFS=1)");
                return false;
            }
            if (required > Version)
            {
                Trace.WriteLine($"[VFS] core requires VFS v{required}, this host provides v{Version}; refused");
                return false;
            }
            Marshal.WriteInt32(data, (int)Version);
            Marshal.WriteIntPtr(data, IntPtr.Size, Table);   // the pointer field is pointer-aligned
            Trace.WriteLine($"[VFS] interface v{Version} handed to the core (it asked for v{required})");
            return true;
        }

        /// <summary>The function table, built once. The self-test calls through these same unmanaged
        /// pointers, exactly as a core does.</summary>
        public static IntPtr Table
        {
            get
            {
                if (_table != IntPtr.Zero) return _table;
                lock (_gate)
                {
                    if (_table != IntPtr.Zero) return _table;
                    var t = (IntPtr*)NativeMemory.AllocZeroed((nuint)(TableSlots * sizeof(IntPtr)));
                    t[0]  = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)&GetPath;
                    t[1]  = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, uint, IntPtr>)&Open;
                    t[2]  = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&Close;
                    t[3]  = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, long>)&Size;
                    t[4]  = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, long>)&Tell;
                    t[5]  = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, long, int, long>)&Seek;
                    t[6]  = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void*, ulong, long>)&Read;
                    t[7]  = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void*, ulong, long>)&Write;
                    t[8]  = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&Flush;
                    t[9]  = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&Remove;
                    t[10] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&Rename;
                    t[11] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, long, long>)&Truncate;
                    t[12] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int*, int>)&Stat;
                    t[13] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&MkDir;
                    t[14] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte, IntPtr>)&OpenDir;
                    t[15] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte>)&ReadDir;
                    t[16] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)&DirentGetName;
                    t[17] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte>)&DirentIsDir;
                    t[18] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&CloseDir;
                    _table = (IntPtr)t;
                    return _table;
                }
            }
        }

        // ---- helpers ----

        static VfsFile? FileOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return null;
            try { return GCHandle.FromIntPtr(h).Target as VfsFile; } catch { return null; }
        }

        static VfsDir? DirOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return null;
            try { return GCHandle.FromIntPtr(h).Target as VfsDir; } catch { return null; }
        }

        static string? PathOf(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

        static void Count() => Interlocked.Increment(ref _ops);

        static void Fail(string op, Exception ex, string? path = null)
        {
            Interlocked.Increment(ref _errors);
            if (Interlocked.Increment(ref _logged) > LogLimit) return;
            string where = path != null ? $" for '{path}'" : "";
            Trace.WriteLine($"[VFS] {op} failed{where}: {ex.GetType().Name}: {ex.Message}");
        }

        // ---- API v1 ----

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static IntPtr GetPath(IntPtr h)
        {
            Count();
            return FileOf(h)?.PathUtf8 ?? IntPtr.Zero;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static IntPtr Open(IntPtr pathPtr, uint mode, uint hints)
        {
            Count();
            string? path = null;
            try
            {
                path = PathOf(pathPtr);
                if (string.IsNullOrEmpty(path)) return IntPtr.Zero;
                bool write = (mode & AccessWrite) != 0;
                if (!write && (mode & AccessRead) == 0) return IntPtr.Zero;
                FileMode fm; FileAccess fa;
                if (!write)                                  { fm = FileMode.Open;   fa = FileAccess.Read; }       // "rb"
                else if ((mode & AccessUpdateExisting) != 0) { fm = FileMode.Open;   fa = FileAccess.ReadWrite; }  // "r+b": keep the contents
                else                                         { fm = FileMode.Create; fa = FileAccess.ReadWrite; }  // "wb" / "w+b": create or truncate
                var stream = new FileStream(path, fm, fa, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                var f = new VfsFile { Stream = stream, Path = path, PathUtf8 = Marshal.StringToCoTaskMemUTF8(path) };
                return GCHandle.ToIntPtr(GCHandle.Alloc(f));
            }
            catch (Exception ex)
            {
                // A missing file on a read-open is an ordinary probe — cores look for optional
                // files (per-game settings, hiscore tables, BIOS variants) and cope with NULL.
                if (!(ex is FileNotFoundException or DirectoryNotFoundException)) Fail("open", ex, path);
                return IntPtr.Zero;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int Close(IntPtr h)
        {
            Count();
            try
            {
                if (h == IntPtr.Zero) return -1;
                var gh = GCHandle.FromIntPtr(h);
                if (gh.Target is not VfsFile f) return -1;
                f.Stream.Dispose();
                if (f.PathUtf8 != IntPtr.Zero) { Marshal.FreeCoTaskMem(f.PathUtf8); f.PathUtf8 = IntPtr.Zero; }
                gh.Free();
                return 0;
            }
            catch (Exception ex) { Fail("close", ex); return -1; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static long Size(IntPtr h)
        {
            Count();
            var f = FileOf(h); if (f == null) return -1;
            try { return f.Stream.Length; }
            catch (Exception ex) { Fail("size", ex, f.Path); return -1; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static long Tell(IntPtr h)
        {
            Count();
            var f = FileOf(h); if (f == null) return -1;
            try { return f.Stream.Position; }
            catch (Exception ex) { Fail("tell", ex, f.Path); return -1; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static long Seek(IntPtr h, long offset, int whence)
        {
            Count();
            var f = FileOf(h); if (f == null) return -1;
            SeekOrigin? origin = whence switch
            {
                SeekStart   => SeekOrigin.Begin,
                SeekCurrent => SeekOrigin.Current,
                SeekEnd     => SeekOrigin.End,
                _           => null,
            };
            if (origin is null) return -1;
            // fseek contract (libretro.h): 0 on success, never the new position — that is tell's
            // job. Cores test `seek(...) == 0`, so answering the position reads as a failure for
            // every non-zero target (PPSSPP then sized its ISO as 0 and refused to boot it).
            try
            {
                long target = origin.Value switch
                {
                    SeekOrigin.Begin   => offset,
                    SeekOrigin.Current => f.Stream.Position + offset,
                    _                  => f.Stream.Length + offset,
                };
                if (target < 0) return -1;   // before the start: a plain negative, like fseek's EINVAL
                f.Stream.Seek(target, SeekOrigin.Begin);
                return 0;
            }
            catch (Exception ex) { Fail("seek", ex, f.Path); return -1; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static long Read(IntPtr h, void* buffer, ulong length)
        {
            Count();
            var f = FileOf(h); if (f == null) return -1;
            if (length == 0) return 0;
            if (buffer == null) return -1;
            try
            {
                long total = 0; byte* p = (byte*)buffer;
                while ((ulong)total < length)
                {
                    int chunk = (int)Math.Min(length - (ulong)total, 1 << 30);
                    int n = f.Stream.Read(new Span<byte>(p + total, chunk));
                    if (n <= 0) break;   // EOF: a short read, like fread
                    total += n;
                }
                return total;
            }
            catch (Exception ex) { Fail("read", ex, f.Path); return -1; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static long Write(IntPtr h, void* buffer, ulong length)
        {
            Count();
            var f = FileOf(h); if (f == null) return -1;
            if (length == 0) return 0;
            if (buffer == null) return -1;
            try
            {
                long total = 0; byte* p = (byte*)buffer;
                while ((ulong)total < length)
                {
                    int chunk = (int)Math.Min(length - (ulong)total, 1 << 30);
                    f.Stream.Write(new ReadOnlySpan<byte>(p + total, chunk));
                    total += chunk;
                }
                return total;
            }
            catch (Exception ex) { Fail("write", ex, f.Path); return -1; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int Flush(IntPtr h)
        {
            Count();
            var f = FileOf(h); if (f == null) return -1;
            try { f.Stream.Flush(); return 0; }
            catch (Exception ex) { Fail("flush", ex, f.Path); return -1; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int Remove(IntPtr pathPtr)
        {
            Count();
            string? path = null;
            try
            {
                path = PathOf(pathPtr);
                if (string.IsNullOrEmpty(path)) return -1;
                if (Directory.Exists(path)) { Directory.Delete(path); return 0; }   // C remove(): an empty directory goes too
                if (!File.Exists(path)) return -1;
                File.Delete(path);
                return 0;
            }
            catch (Exception ex) { Fail("remove", ex, path); return -1; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int Rename(IntPtr oldPtr, IntPtr newPtr)
        {
            Count();
            string? oldPath = null;
            try
            {
                oldPath = PathOf(oldPtr);
                string? newPath = PathOf(newPtr);
                if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return -1;
                if (Directory.Exists(oldPath)) Directory.Move(oldPath, newPath);
                else if (File.Exists(oldPath)) File.Move(oldPath, newPath, overwrite: true);   // POSIX rename() replaces the target
                else return -1;   // nothing to rename: a plain negative, not an error
                return 0;
            }
            catch (Exception ex) { Fail("rename", ex, oldPath); return -1; }
        }

        // ---- API v2 ----

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static long Truncate(IntPtr h, long length)
        {
            Count();
            var f = FileOf(h); if (f == null) return -1;
            try { f.Stream.SetLength(length); return 0; }
            catch (Exception ex) { Fail("truncate", ex, f.Path); return -1; }
        }

        // ---- API v3 ----

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int Stat(IntPtr pathPtr, int* size)
        {
            Count();
            try
            {
                string? path = PathOf(pathPtr);
                if (string.IsNullOrEmpty(path)) return 0;
                if (Directory.Exists(path))
                {
                    if (size != null) *size = 0;
                    return StatIsValid | StatIsDirectory;
                }
                var info = new FileInfo(path);
                if (!info.Exists) return 0;
                if (size != null) *size = info.Length > int.MaxValue ? int.MaxValue : (int)info.Length;
                return StatIsValid;
            }
            catch (Exception ex) { Fail("stat", ex); return 0; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int MkDir(IntPtr dirPtr)
        {
            Count();
            string? dir = null;
            try
            {
                dir = PathOf(dirPtr);
                if (string.IsNullOrEmpty(dir)) return -1;
                if (Directory.Exists(dir)) return -2;   // libretro.h: -2 = already exists
                Directory.CreateDirectory(dir);
                return 0;
            }
            catch (Exception ex) { Fail("mkdir", ex, dir); return -1; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static IntPtr OpenDir(IntPtr dirPtr, byte includeHidden)
        {
            Count();
            string? dir = null;
            try
            {
                dir = PathOf(dirPtr);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return IntPtr.Zero;
                var d = new VfsDir();
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    string name = entry.Name;
                    if (name is "." or "..") continue;
                    if (includeHidden == 0 && name.StartsWith('.')) continue;
                    bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
                    d.Entries.Add((Marshal.StringToCoTaskMemUTF8(name), isDir));
                }
                return GCHandle.ToIntPtr(GCHandle.Alloc(d));
            }
            catch (Exception ex) { Fail("opendir", ex, dir); return IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte ReadDir(IntPtr h)
        {
            Count();
            var d = DirOf(h); if (d == null) return 0;
            if (d.Index < d.Entries.Count) d.Index++;
            return (byte)(d.Index < d.Entries.Count ? 1 : 0);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static IntPtr DirentGetName(IntPtr h)
        {
            Count();
            var d = DirOf(h);
            if (d == null || d.Index < 0 || d.Index >= d.Entries.Count) return IntPtr.Zero;
            return d.Entries[d.Index].NameUtf8;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte DirentIsDir(IntPtr h)
        {
            Count();
            var d = DirOf(h);
            if (d == null || d.Index < 0 || d.Index >= d.Entries.Count) return 0;
            return (byte)(d.Entries[d.Index].IsDir ? 1 : 0);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int CloseDir(IntPtr h)
        {
            Count();
            try
            {
                if (h == IntPtr.Zero) return -1;
                var gh = GCHandle.FromIntPtr(h);
                if (gh.Target is not VfsDir d) return -1;
                foreach (var e in d.Entries) Marshal.FreeCoTaskMem(e.NameUtf8);
                d.Entries.Clear();
                gh.Free();
                return 0;
            }
            catch (Exception ex) { Fail("closedir", ex); return -1; }
        }
    }
}
