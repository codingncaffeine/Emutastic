using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Emutastic.Emulator
{
    /// <summary>
    /// `Emutastic --selftest-vfs`: drives every <see cref="LibretroVfs"/> callback through the
    /// unmanaged function table exactly as a core does (open/read/write/seek/tell/size/truncate/
    /// flush/get_path/close, stat/mkdir/opendir/readdir/dirent_*/closedir, rename/remove), including
    /// the negative answers cores rely on: NULL for a missing read-open, 0 from stat, -2 from mkdir
    /// on an existing directory, -1 from a bad seek. Works in a throwaway folder under the temp
    /// directory, removed afterwards. Exit 0 = every check passed, 1 = a check failed.
    /// </summary>
    internal static unsafe class VfsSelfTest
    {
        static int _pass, _fail;
        static StreamWriter? _report;

        static void Line(string s)
        {
            Console.WriteLine(s);
            _report?.WriteLine(s);
        }

        static void Check(string what, bool ok)
        {
            if (ok) _pass++; else _fail++;
            Line($"[vfs-selftest] {(ok ? "ok  " : "FAIL")} {what}");
        }

        /// <param name="reportPath">Optional file that also receives every line: the Windows build
        /// has no console, and CI reads the report.</param>
        public static int Run(string? reportPath = null)
        {
            _pass = _fail = 0;
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                try
                {
                    string full = Path.GetFullPath(reportPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
                    _report = new StreamWriter(full, append: false) { AutoFlush = true };
                }
                catch (Exception ex) { Console.WriteLine($"[vfs-selftest] cannot open report '{reportPath}': {ex.Message}"); }
            }
            string root = Path.Combine(Path.GetTempPath(), $"emutastic-vfs-selftest-{Environment.ProcessId}");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Directory.CreateDirectory(root);
            try
            {
                Exercise(root);
            }
            catch (Exception ex)
            {
                Line($"[vfs-selftest] FAIL exception: {ex}");
                _fail++;
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
            }
            Line($"[vfs-selftest] {_pass} passed, {_fail} failed: {(_fail == 0 ? "PASS" : "FAIL")}");
            _report?.Dispose();
            _report = null;
            return _fail == 0 ? 0 : 1;
        }

        static void Exercise(string root)
        {
            var t = (IntPtr*)LibretroVfs.Table;
            var get_path = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)t[0];
            var open     = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint, IntPtr>)t[1];
            var close    = (delegate* unmanaged[Cdecl]<IntPtr, int>)t[2];
            var size     = (delegate* unmanaged[Cdecl]<IntPtr, long>)t[3];
            var tell     = (delegate* unmanaged[Cdecl]<IntPtr, long>)t[4];
            var seek     = (delegate* unmanaged[Cdecl]<IntPtr, long, int, long>)t[5];
            var read     = (delegate* unmanaged[Cdecl]<IntPtr, void*, ulong, long>)t[6];
            var write    = (delegate* unmanaged[Cdecl]<IntPtr, void*, ulong, long>)t[7];
            var flush    = (delegate* unmanaged[Cdecl]<IntPtr, int>)t[8];
            var remove   = (delegate* unmanaged[Cdecl]<IntPtr, int>)t[9];
            var rename   = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)t[10];
            var truncate = (delegate* unmanaged[Cdecl]<IntPtr, long, long>)t[11];
            var stat     = (delegate* unmanaged[Cdecl]<IntPtr, int*, int>)t[12];
            var mkdir    = (delegate* unmanaged[Cdecl]<IntPtr, int>)t[13];
            var opendir  = (delegate* unmanaged[Cdecl]<IntPtr, byte, IntPtr>)t[14];
            var readdir  = (delegate* unmanaged[Cdecl]<IntPtr, byte>)t[15];
            var dname    = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)t[16];
            var disdir   = (delegate* unmanaged[Cdecl]<IntPtr, byte>)t[17];
            var closedir = (delegate* unmanaged[Cdecl]<IntPtr, int>)t[18];

            Check("table has the 19 v1-v3 entries and nothing past v3", AllSet(t, 19) && t[19] == IntPtr.Zero);

            string dir   = Path.Combine(root, "sub");
            string file  = Path.Combine(dir, "save.srm");
            string file2 = Path.Combine(dir, "renamed.srm");
            using var pDir     = new Utf8(dir);
            using var pFile    = new Utf8(file);
            using var pFile2   = new Utf8(file2);
            using var pMissing = new Utf8(Path.Combine(root, "missing.bin"));

            int sz = -1;
            Check("stat(missing) == 0", stat(pMissing, &sz) == 0);
            Check("open(missing, READ) == NULL", open(pMissing, 1, 0) == IntPtr.Zero);
            Check("mkdir(new) == 0", mkdir(pDir) == 0);
            Check("mkdir(existing) == -2", mkdir(pDir) == -2);
            Check("stat(dir) == VALID|DIRECTORY", stat(pDir, &sz) == (LibretroVfs.StatIsValid | LibretroVfs.StatIsDirectory));

            // READ_WRITE on a new file ("w+b"): write, then read back through the same handle.
            IntPtr h = open(pFile, 3, 0);
            Check("open(new, READ_WRITE) != NULL", h != IntPtr.Zero);
            byte[] hello = Encoding.ASCII.GetBytes("hello world");
            fixed (byte* p = hello) Check("write 11 bytes returns 11", write(h, p, 11) == 11);
            Check("tell == 11 after the write", tell(h) == 11);
            Check("size == 11", size(h) == 11);
            Check("seek(0, START) == 0", seek(h, 0, 0) == 0);
            Check("tell == 0 after it", tell(h) == 0);
            byte[] buf = new byte[32];
            long n;
            fixed (byte* p = buf) n = read(h, p, 32);
            Check("read(32) at the start is a short read of the 11 bytes present", n == 11 && Encoding.ASCII.GetString(buf, 0, 11) == "hello world");
            fixed (byte* p = buf) Check("read at EOF returns 0", read(h, p, 32) == 0);
            Check("seek(-5, END) == 0 (fseek contract: success is 0, never the position)", seek(h, -5, 2) == 0);
            Check("tell == 6 after it", tell(h) == 6);
            fixed (byte* p = buf) n = read(h, p, 5);
            Check("read 5 from there gives 'world'", n == 5 && Encoding.ASCII.GetString(buf, 0, 5) == "world");
            Check("seek(2, CURRENT) past the end is allowed and returns 0", seek(h, 2, 1) == 0);
            Check("tell == 13 after it", tell(h) == 13);
            Check("seek with a bad whence == -1", seek(h, 0, 7) == -1);
            Check("seek before the start == -1", seek(h, -1, 0) == -1);
            Check("truncate(5) == 0", truncate(h, 5) == 0);
            Check("size == 5 after truncate", size(h) == 5);
            Check("flush == 0", flush(h) == 0);
            Check("get_path returns the opened path", Marshal.PtrToStringUTF8(get_path(h)) == file);
            Check("close == 0", close(h) == 0);
            Check("close(NULL) == -1", close(IntPtr.Zero) == -1);
            Check("stat(file) == VALID with size 5", stat(pFile, &sz) == LibretroVfs.StatIsValid && sz == 5);

            // WRITE|UPDATE_EXISTING keeps the contents ("r+b") and never creates.
            h = open(pFile, 2 | 4, 0);
            Check("open(existing, WRITE|UPDATE_EXISTING) != NULL", h != IntPtr.Zero);
            Check("... and keeps the 5 bytes", size(h) == 5);
            Check("seek(0, END) == 0", seek(h, 0, 2) == 0);
            Check("tell == 5 at the end", tell(h) == 5);
            fixed (byte* p = hello) Check("append 1 byte", write(h, p, 1) == 1);
            Check("size == 6", size(h) == 6);
            close(h);
            Check("open(missing, WRITE|UPDATE_EXISTING) == NULL", open(pMissing, 2 | 4, 0) == IntPtr.Zero);

            // Plain WRITE truncates ("wb").
            h = open(pFile, 2, 0);
            Check("open(existing, WRITE) != NULL", h != IntPtr.Zero);
            Check("... and truncates to 0", size(h) == 0);
            fixed (byte* p = hello) write(h, p, 3);
            close(h);
            Check("stat(file) size == 3 after the rewrite", stat(pFile, &sz) == LibretroVfs.StatIsValid && sz == 3);

            // Directory listing: hidden entries only with include_hidden.
            File.WriteAllText(Path.Combine(dir, ".hidden"), "x");
            Directory.CreateDirectory(Path.Combine(dir, "nested"));
            Check("opendir(missing) == NULL", opendir(pMissing, 0) == IntPtr.Zero);
            var seen = List(opendir, readdir, dname, disdir, closedir, pDir, 0);
            Check("readdir without hidden lists save.srm + nested only",
                seen.Count == 2 && seen.TryGetValue("save.srm", out bool f1) && !f1 && seen.TryGetValue("nested", out bool d1) && d1);
            var seenAll = List(opendir, readdir, dname, disdir, closedir, pDir, 1);
            Check("readdir with include_hidden also lists .hidden", seenAll.Count == 3 && seenAll.ContainsKey(".hidden"));

            Check("rename(file, file2) == 0", rename(pFile, pFile2) == 0);
            Check("stat(file) == 0 after rename", stat(pFile, &sz) == 0);
            Check("stat(file2) != 0 after rename", stat(pFile2, &sz) != 0);
            Check("remove(file2) == 0", remove(pFile2) == 0);
            Check("remove(file2) again == -1", remove(pFile2) == -1);
            Check("rename(missing) == -1", rename(pMissing, pFile) == -1);
            Check("ops counter advanced", LibretroVfs.Ops > 40);
            Check("no unexpected failures were counted", LibretroVfs.Errors == 0);
        }

        static bool AllSet(IntPtr* t, int count)
        {
            for (int i = 0; i < count; i++) if (t[i] == IntPtr.Zero) return false;
            return true;
        }

        static Dictionary<string, bool> List(
            delegate* unmanaged[Cdecl]<IntPtr, byte, IntPtr> opendir,
            delegate* unmanaged[Cdecl]<IntPtr, byte> readdir,
            delegate* unmanaged[Cdecl]<IntPtr, IntPtr> dname,
            delegate* unmanaged[Cdecl]<IntPtr, byte> disdir,
            delegate* unmanaged[Cdecl]<IntPtr, int> closedir,
            IntPtr dir, byte includeHidden)
        {
            var result = new Dictionary<string, bool>();
            IntPtr d = opendir(dir, includeHidden);
            Check("opendir(existing) != NULL", d != IntPtr.Zero);
            if (d == IntPtr.Zero) return result;
            int guard = 0;
            while (readdir(d) != 0 && guard++ < 100)
                result[Marshal.PtrToStringUTF8(dname(d)) ?? "?"] = disdir(d) != 0;
            Check("dirent_get_name after the end == NULL", dname(d) == IntPtr.Zero);
            Check("closedir == 0", closedir(d) == 0);
            return result;
        }

        /// <summary>A UTF-8 C string for the duration of a test step.</summary>
        sealed class Utf8 : IDisposable
        {
            public readonly IntPtr Ptr;
            public Utf8(string s) { Ptr = Marshal.StringToCoTaskMemUTF8(s); }
            public static implicit operator IntPtr(Utf8 u) => u.Ptr;
            public void Dispose() => Marshal.FreeCoTaskMem(Ptr);
        }
    }
}
