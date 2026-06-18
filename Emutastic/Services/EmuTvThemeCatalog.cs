using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    /// <summary>One downloadable theme from the ES-DE themes-list catalog.</summary>
    public sealed class CatalogTheme
    {
        public string Name { get; init; } = "";
        public string Author { get; init; } = "";
        public string Reponame { get; init; } = "";
        public string Url { get; init; } = "";              // git repo URL (github/gitlab)
        public List<string> Variants { get; init; } = new();
        public List<string> ColorSchemes { get; init; } = new();
        public List<string> AspectRatios { get; init; } = new();
        public string? ScreenshotPath { get; init; }        // first screenshot, relative to the list repo
    }

    /// <summary>
    /// Reads the official ES-DE themes-list (the same <c>themes.json</c> manifest ES-DE's own theme
    /// downloader uses) so EmuTV can present a unified browser of installed + downloadable themes.
    /// Downloads happen over the GitHub/GitLab archive-zip API (no local git required) and are then
    /// installed through the existing <see cref="EsDeThemeImporter"/>.
    /// </summary>
    public sealed class EmuTvThemeCatalog
    {
        public static EmuTvThemeCatalog Instance { get; } = new();

        private const string ListUrl = "https://gitlab.com/es-de/themes/themes-list/-/raw/master/themes.json";
        private const string RawBase = "https://gitlab.com/es-de/themes/themes-list/-/raw/master/";

        private static readonly HttpClient Http = CreateClient();
        private IReadOnlyList<CatalogTheme>? _cache;

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.TryParseAdd("EmuTV/1.0");
            return c;
        }

        /// <summary>Fetches and caches the catalog. Returns an empty list (never throws) on failure.</summary>
        public async Task<IReadOnlyList<CatalogTheme>> FetchAsync(bool force = false)
        {
            if (_cache != null && !force) return _cache;
            try
            {
                string json = await Http.GetStringAsync(ListUrl).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var list = new List<CatalogTheme>();
                if (doc.RootElement.TryGetProperty("themes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in arr.EnumerateArray())
                    {
                        string name = Str(t, "name");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        list.Add(new CatalogTheme
                        {
                            Name = name,
                            Author = Str(t, "author"),
                            Reponame = Str(t, "reponame"),
                            Url = Str(t, "url"),
                            Variants = Arr(t, "variants"),
                            ColorSchemes = Arr(t, "colorSchemes"),
                            AspectRatios = Arr(t, "aspectRatios"),
                            ScreenshotPath = FirstScreenshot(t),
                        });
                    }
                }
                _cache = list;
                return list;
            }
            catch
            {
                return _cache ?? Array.Empty<CatalogTheme>();
            }
        }

        /// <summary>Absolute URL of a catalog theme's preview screenshot, or null if it has none.</summary>
        public string? ScreenshotUrl(CatalogTheme t) =>
            string.IsNullOrEmpty(t.ScreenshotPath) ? null : RawBase + t.ScreenshotPath.Replace("\\", "/");

        /// <summary>Downloads raw bytes (e.g. a preview screenshot), or null on failure. Uses the
        /// pooled HttpClient so rapid preview loads don't stall on the legacy 2-connection limit that
        /// WPF's built-in BitmapImage URL downloader is subject to.</summary>
        public async Task<byte[]?> GetBytesAsync(string url)
        {
            try { return await Http.GetByteArrayAsync(url).ConfigureAwait(false); }
            catch { return null; }
        }

        /// <summary>Downloads the theme's repository archive and installs it via the ES-DE importer.</summary>
        public async Task<EsDeImportResult> DownloadAndInstallAsync(CatalogTheme t)
        {
            var r = new EsDeImportResult();
            string? zipUrl = ArchiveUrl(t.Url);
            if (zipUrl == null) { r.Message = $"Don't know how to download from {t.Url}"; return r; }

            string tmp = Path.Combine(Path.GetTempPath(), "emutv-dl-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                byte[] bytes = await Http.GetByteArrayAsync(zipUrl).ConfigureAwait(false);
                await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
                // The importer flattens the single {repo}-{sha} wrapper folder the archive contains.
                return EsDeThemeImporter.ImportFromZip(tmp);
            }
            catch (Exception ex)
            {
                r.Message = "Download failed: " + ex.Message;
                return r;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // Resolve a git repo URL to its default-branch archive-zip endpoint (no branch needed, no git).
        private static string? ArchiveUrl(string gitUrl)
        {
            if (string.IsNullOrWhiteSpace(gitUrl)) return null;
            string u = gitUrl.Trim();
            if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u[..^4];
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return null;
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            string owner = parts[0], repo = parts[1];
            if (uri.Host.Contains("github", StringComparison.OrdinalIgnoreCase))
                return $"https://api.github.com/repos/{owner}/{repo}/zipball";          // 302 → default branch zip
            if (uri.Host.Contains("gitlab", StringComparison.OrdinalIgnoreCase))
                return $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(owner + "/" + repo)}/repository/archive.zip";
            return null;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

        private static List<string> Arr(JsonElement e, string name)
        {
            var list = new List<string>();
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var x in v.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString() ?? "");
            return list;
        }

        private static string? FirstScreenshot(JsonElement e)
        {
            if (e.TryGetProperty("screenshots", out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var s in v.EnumerateArray())
                    if (s.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.String)
                        return img.GetString();
            return null;
        }
    }
}
