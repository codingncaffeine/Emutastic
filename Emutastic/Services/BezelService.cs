using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    /// <summary>
    /// Arcade / Neo Geo bezel overlays from The Bezel Project (thebezelproject/
    /// bezelproject-MAME). The full pack is ~2 GB, so bezels are fetched PER GAME
    /// on demand (by ROM short-name) from GitHub raw and cached locally. The PNG is
    /// a 1920x1080 frame with a transparent window; the game renders at its normal
    /// aspect, centered, and lands in that window (these bezels carry no custom
    /// viewport — they assume standard AR-centered rendering).
    ///
    /// Modeled on <see cref="VectrexOverlayService"/>: per-game enable persisted in
    /// JSON, feature gated by a global toggle (Preferences -> Cores/Extras).
    /// </summary>
    public static class BezelService
    {
        private const string RawBase =
            "https://raw.githubusercontent.com/thebezelproject/bezelproject-MAME/master/retroarch/overlay/ArcadeBezels/";

        private const string SettingsFile = "bezel_settings.json";

        // Consoles this applies to (software-rendered arcade family). HW cores excluded.
        public static bool AppliesTo(string? console) =>
            console is "Arcade" or "NeoGeo" or "NeoCD";

        // Single shared cache: arcade + NeoGeo bezels all live in The Bezel Project's
        // ArcadeBezels folder, keyed by MAME short-name (globally unique), so one folder
        // serves both consoles — and the bulk "Download all" fills the same cache.
        private static string CacheDir()
            => AppPaths.GetFolder("Overlays", "Bezels");

        // ── Per-game / feature settings ────────────────────────────────────────
        private sealed class Settings
        {
            public bool FeatureEnabled { get; set; }
            public Dictionary<int, bool> PerGame { get; set; } = new();
        }

        private static Settings? _settings;
        private static readonly object _gate = new();

        private static Settings Load()
        {
            lock (_gate)
            {
                if (_settings != null) return _settings;
                try
                {
                    string path = Path.Combine(AppPaths.GetFolder("Overlays"), SettingsFile);
                    if (File.Exists(path))
                        _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
                    else
                        _settings = new Settings();
                }
                catch { _settings = new Settings(); }
                return _settings;
            }
        }

        private static void Save()
        {
            try
            {
                string path = Path.Combine(AppPaths.GetFolder("Overlays"), SettingsFile);
                lock (_gate)
                    File.WriteAllText(path, JsonSerializer.Serialize(_settings ?? new Settings(),
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* non-fatal */ }
        }

        /// <summary>Global on/off for the bezel feature (the Cores/Extras toggle).</summary>
        public static bool FeatureEnabled
        {
            get => Load().FeatureEnabled;
            set { Load().FeatureEnabled = value; Save(); }
        }

        /// <summary>Whether bezels should show for this game (default on when the feature is enabled).</summary>
        public static bool IsEnabledForGame(int gameId)
            => Load().FeatureEnabled && Load().PerGame.GetValueOrDefault(gameId, true);

        public static void SetEnabledForGame(int gameId, bool enabled)
        {
            Load().PerGame[gameId] = enabled;
            Save();
        }

        // ── Fetch + cache ──────────────────────────────────────────────────────
        /// <summary>
        /// Returns the local path to this game's bezel PNG, fetching+caching it from
        /// The Bezel Project on first use. Returns null if the game has no bezel
        /// (cached as a ".miss" marker so we don't re-hit the network every launch)
        /// or on any failure. Safe to call off the UI thread.
        /// </summary>
        public static async Task<string?> EnsureBezelAsync(string romPath, string console)
        {
            try
            {
                string stem = Path.GetFileNameWithoutExtension(romPath);
                if (string.IsNullOrWhiteSpace(stem)) return null;

                string dir  = CacheDir();
                string png  = Path.Combine(dir, stem + ".png");
                string miss = Path.Combine(dir, stem + ".miss");
                if (File.Exists(png))  return png;
                if (File.Exists(miss)) return null;

                string url = RawBase + Uri.EscapeDataString(stem) + ".png";
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
                http.DefaultRequestHeaders.Add("User-Agent", "Emutastic");

                using var resp = await http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    try { File.WriteAllText(miss, ""); } catch { }
                    return null;
                }
                byte[] bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                // Guard against a stray HTML/error body masquerading as a PNG.
                if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50)
                {
                    try { File.WriteAllText(miss, ""); } catch { }
                    return null;
                }
                await File.WriteAllBytesAsync(png, bytes).ConfigureAwait(false);
                return png;
            }
            catch
            {
                return null;
            }
        }

        // ── Bulk "Download all" (offline use) ───────────────────────────────────
        /// <summary>
        /// Downloads every arcade/NeoGeo bezel PNG into the shared cache so the feature
        /// works fully offline. Lists the Bezel Project's ArcadeBezels folder (2 GitHub
        /// API calls), then fetches each PNG with bounded concurrency, skipping any
        /// already cached. Reports (done,total) progress. Safe to call off the UI thread.
        /// </summary>
        public static async Task<(int downloaded, int total)> DownloadAllAsync(
            IProgress<(int done, int total)>? progress, CancellationToken ct = default)
        {
            var names = await ListBezelFileNamesAsync(ct).ConfigureAwait(false);
            int total = names.Count;
            if (total == 0) return (0, 0);

            string dir = CacheDir();
            int done = 0;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            http.DefaultRequestHeaders.Add("User-Agent", "Emutastic");
            using var sem = new SemaphoreSlim(6);

            var tasks = new List<Task>(total);
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                await sem.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        string png = Path.Combine(dir, name);
                        if (!File.Exists(png))
                        {
                            byte[] bytes = await http.GetByteArrayAsync(
                                RawBase + Uri.EscapeDataString(name), ct).ConfigureAwait(false);
                            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50)
                                await File.WriteAllBytesAsync(png, bytes, ct).ConfigureAwait(false);
                        }
                    }
                    catch { /* skip individual failures */ }
                    finally
                    {
                        sem.Release();
                        progress?.Report((Interlocked.Increment(ref done), total));
                    }
                }, ct));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            FeatureEnabled = true; // downloading the set implies the feature is wanted
            return (done, total);
        }

        /// <summary>Lists the .png filenames in the Bezel Project's ArcadeBezels folder.</summary>
        private static async Task<List<string>> ListBezelFileNamesAsync(CancellationToken ct)
        {
            var names = new List<string>();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Add("User-Agent", "Emutastic");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            // 1) Resolve the ArcadeBezels directory's tree SHA.
            string overview = await http.GetStringAsync(
                "https://api.github.com/repos/thebezelproject/bezelproject-MAME/contents/retroarch/overlay",
                ct).ConfigureAwait(false);
            string? sha = null;
            using (var doc = JsonDocument.Parse(overview))
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.GetProperty("name").GetString() == "ArcadeBezels" &&
                        el.GetProperty("type").GetString() == "dir")
                    { sha = el.GetProperty("sha").GetString(); break; }
            if (sha == null) return names;

            // 2) List that tree's PNG blobs (flat folder, so path == filename).
            string tree = await http.GetStringAsync(
                $"https://api.github.com/repos/thebezelproject/bezelproject-MAME/git/trees/{sha}",
                ct).ConfigureAwait(false);
            using (var doc = JsonDocument.Parse(tree))
                if (doc.RootElement.TryGetProperty("tree", out var arr))
                    foreach (var el in arr.EnumerateArray())
                    {
                        string? p = el.GetProperty("path").GetString();
                        if (p != null && p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                            names.Add(p);
                    }
            return names;
        }

        /// <summary>How many bezel PNGs are currently cached locally.</summary>
        public static int CachedCount()
        {
            try { return Directory.GetFiles(CacheDir(), "*.png").Length; }
            catch { return 0; }
        }
    }
}
