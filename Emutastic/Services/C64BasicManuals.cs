using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// The manuals of the built-in "Commodore 64 BASIC" entry (<see cref="C64BasicEntry"/>): Commodore's own User's
    /// Guide and Programmer's Reference Guide, scans from zimmers.net (the long-standing Commodore archive). The
    /// entry's right-click menu offers each one on its own in place of the usual manual search: downloaded the first
    /// time into Manuals/C64/Commodore 64 BASIC/, opened in the system viewer from then on.
    /// </summary>
    public static class C64BasicManuals
    {
        public sealed record Manual(string Title, string Url, string FileName);

        public static readonly Manual[] All =
        {
            new("User's Guide", "https://www.zimmers.net/anonftp/pub/cbm/manuals/c64/C64_Users_Guide.pdf",
                "Commodore 64 User's Guide.pdf"),
            new("Programmer's Reference Guide", "https://www.zimmers.net/anonftp/pub/cbm/manuals/c64/C64_Programmers_Reference_Guide.pdf",
                "Commodore 64 Programmer's Reference Guide.pdf"),
        };

        private static readonly HttpClient Http = CreateClient();
        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.Add("User-Agent", "Emutastic");
            return c;
        }

        public static bool IsBasicEntry(Game g) =>
            string.Equals(g.RomHash, C64BasicEntry.Hash, StringComparison.Ordinal) && string.Equals(g.Console, "C64", StringComparison.OrdinalIgnoreCase);

        public static string PathFor(Manual m) =>
            Path.Combine(AppPaths.GetFolder("Manuals", "C64", C64BasicEntry.Title), m.FileName);

        public static bool Has(Manual m)
        {
            var f = new FileInfo(PathFor(m));
            return f.Exists && f.Length > 0;
        }

        /// <summary>Downloads the manual (to a temporary name, kept only if it is a PDF). Progress 0-100.
        /// Throws with a reason the user can read.</summary>
        public static async Task DownloadAsync(Manual m, Action<double>? progress, CancellationToken cancel = default)
        {
            string path = PathFor(m), tmp = path + ".part";
            try
            {
                using var resp = await Http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"the server answered {(int)resp.StatusCode}");
                long total = resp.Content.Headers.ContentLength ?? -1;
                await using (var src = await resp.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
                await using (var dst = File.Create(tmp))
                {
                    var buf = new byte[81920];
                    long got = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, cancel).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), cancel).ConfigureAwait(false);
                        got += n;
                        if (total > 0) progress?.Invoke(got * 100.0 / total);
                    }
                }
                var head = new byte[5];
                using (var f = File.OpenRead(tmp)) _ = f.Read(head, 0, 5);
                if (head[0] != (byte)'%' || head[1] != (byte)'P' || head[2] != (byte)'D' || head[3] != (byte)'F')
                    throw new InvalidOperationException("the download was not a PDF");
                File.Move(tmp, path, overwrite: true);
            }
            catch (HttpRequestException ex) { throw new InvalidOperationException("no connection to zimmers.net (" + ex.Message + ")"); }
            catch (TaskCanceledException) when (!cancel.IsCancellationRequested) { throw new InvalidOperationException("the download timed out"); }
            finally { try { File.Delete(tmp); } catch { } }
        }
    }
}
