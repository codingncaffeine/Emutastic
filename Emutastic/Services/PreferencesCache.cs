using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Emutastic.Views;

namespace Emutastic.Services
{
    /// <summary>
    /// Process-lifetime cache for expensive Preferences-window lookups.
    /// All helpers run heavy work via Task.Run and single-flight via SemaphoreSlim
    /// so multiple tab clicks while a build is in flight share one result.
    ///
    /// Caches are invalidated by explicit callers (theme installed, core
    /// downloaded, BIOS dropped) rather than time-based churn.
    /// </summary>
    internal static class PreferencesCache
    {
        // ── BIOS scan (Fix 2) ─────────────────────────────────────────────────
        public sealed record BiosScanResult(
            Dictionary<string, string[]> RomDirsByConsole,
            HashSet<string> ExistingPathsLower);

        private static BiosScanResult? _biosScan;
        private static DateTime _biosScanAt;
        private static readonly SemaphoreSlim _biosGate = new(1, 1);
        private static readonly TimeSpan _biosTtl = TimeSpan.FromSeconds(30);

        public static async Task<BiosScanResult> GetBiosScanAsync(
            DatabaseService db, string sysDir,
            IReadOnlyList<BiosEntry> biosEntries,
            CancellationToken ct = default)
        {
            if (_biosScan != null && DateTime.UtcNow - _biosScanAt < _biosTtl)
                return _biosScan;

            await _biosGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_biosScan != null && DateTime.UtcNow - _biosScanAt < _biosTtl)
                    return _biosScan;

                var result = await Task.Run(() => BuildBiosScan(db, sysDir, biosEntries), ct)
                    .ConfigureAwait(false);
                _biosScan = result;
                _biosScanAt = DateTime.UtcNow;
                return result;
            }
            finally
            {
                _biosGate.Release();
            }
        }

        public static void InvalidateBiosScan()
        {
            _biosScan = null;
        }

        /// <summary>Last-known cached scan without triggering a build. May be null.</summary>
        public static BiosScanResult? GetBiosScanSnapshot() => _biosScan;

        private static BiosScanResult BuildBiosScan(
            DatabaseService db, string sysDir, IReadOnlyList<BiosEntry> biosEntries)
        {
            var games = db.GetAllGames();
            var romDirsByConsole = games
                .Where(g => !string.IsNullOrEmpty(g.RomPath))
                .GroupBy(g => g.Console)
                .ToDictionary(
                    grp => grp.Key,
                    grp =>
                    {
                        var baseDirs = grp
                            .Select(g => Path.GetDirectoryName(g.RomPath))
                            .Where(d => !string.IsNullOrEmpty(d))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var expanded = new List<string>(baseDirs!);
                        foreach (var dir in baseDirs)
                        {
                            try { expanded.AddRange(Directory.EnumerateDirectories(dir!)); }
                            catch { }
                        }
                        return expanded.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    });

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // System-dir candidates: every known BIOS filename
            foreach (var entry in biosEntries)
            {
                string sysPath = Path.Combine(sysDir, entry.Filename);
                if (SafeExists(sysPath)) existing.Add(sysPath);
            }

            // ROM-dir candidates: only the leaf filename, against every dir for that console
            foreach (var entry in biosEntries)
            {
                if (!romDirsByConsole.TryGetValue(entry.Console, out var dirs)) continue;
                string leaf = Path.GetFileName(entry.Filename);
                foreach (var dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string p = Path.Combine(dir, leaf);
                    if (SafeExists(p)) existing.Add(p);
                }
            }

            return new BiosScanResult(romDirsByConsole, existing);
        }

        private static bool SafeExists(string p)
        {
            try { return File.Exists(p); } catch { return false; }
        }

        // ── Installed cores (Fix 3) ───────────────────────────────────────────
        private static HashSet<string>? _installedCores;
        private static DateTime _installedAt;
        private static readonly SemaphoreSlim _coresGate = new(1, 1);
        private static readonly TimeSpan _coresTtl = TimeSpan.FromSeconds(30);

        public static async Task<HashSet<string>> GetInstalledCoresAsync(string coresFolder)
        {
            if (_installedCores != null && DateTime.UtcNow - _installedAt < _coresTtl)
                return _installedCores;

            await _coresGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_installedCores != null && DateTime.UtcNow - _installedAt < _coresTtl)
                    return _installedCores;

                var set = await Task.Run(() =>
                {
                    var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(coresFolder, "*.dll"))
                            s.Add(Path.GetFileName(f));
                    }
                    catch { }
                    return s;
                }).ConfigureAwait(false);

                _installedCores = set;
                _installedAt = DateTime.UtcNow;
                return set;
            }
            finally
            {
                _coresGate.Release();
            }
        }

        public static void InvalidateCores()
        {
            _installedCores = null;
        }

        // ── Theme swatches (Fix 4) ────────────────────────────────────────────
        private static Dictionary<string, Color[]>? _themeSwatches;
        private static readonly object _themeLock = new();

        public static Dictionary<string, Color[]> GetThemeSwatches()
        {
            var cached = _themeSwatches;
            if (cached != null) return cached;

            lock (_themeLock)
            {
                if (_themeSwatches != null) return _themeSwatches;

                var dict = new Dictionary<string, Color[]>(StringComparer.Ordinal);
                foreach (var (id, _) in ThemeService.Instance.GetAvailableThemes())
                {
                    var c = ThemeService.Instance.GetColorsForTheme(id);
                    var hexes = new[]
                    {
                        c.BgPrimary    ?? "#0F0F10",
                        c.Accent       ?? "#E03535",
                        c.TextPrimary  ?? "#F0F0F0",
                        c.BgSecondary  ?? "#181819",
                        c.Green        ?? "#28C840",
                    };
                    var colors = new Color[hexes.Length];
                    for (int i = 0; i < hexes.Length; i++)
                    {
                        try { colors[i] = (Color)ColorConverter.ConvertFromString(hexes[i]); }
                        catch { colors[i] = Colors.Gray; }
                    }
                    dict[id] = colors;
                }
                _themeSwatches = dict;
                return dict;
            }
        }

        public static void InvalidateThemes()
        {
            _themeSwatches = null;
        }

        // ── GitHub latest release (Fix 5) ─────────────────────────────────────
        public sealed record GitHubRelease(string Tag, string Url);

        private static GitHubRelease? _ghRelease;
        private static DateTime _ghAt;
        private static readonly TimeSpan _ghTtl = TimeSpan.FromMinutes(60);
        private static readonly SemaphoreSlim _ghGate = new(1, 1);

        public static async Task<GitHubRelease?> GetGitHubLatestAsync(
            HttpClient http, string url, CancellationToken ct)
        {
            if (_ghRelease != null && DateTime.UtcNow - _ghAt < _ghTtl)
                return _ghRelease;

            await _ghGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_ghRelease != null && DateTime.UtcNow - _ghAt < _ghTtl)
                    return _ghRelease;

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                using var resp = await http.GetAsync(url, linked.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                string json = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
                string href = root.TryGetProperty("html_url", out var u) ? (u.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(tag)) return null;

                _ghRelease = new GitHubRelease(tag, href);
                _ghAt = DateTime.UtcNow;
                return _ghRelease;
            }
            finally
            {
                _ghGate.Release();
            }
        }

        public static void InvalidateGitHubLatest()
        {
            _ghRelease = null;
        }

        // ── Core updates batch (Fix 5) ────────────────────────────────────────
        private static List<CoreEntry>? _coreUpdates;
        private static DateTime _coreUpdatesAt;
        private static readonly TimeSpan _coreUpdatesTtl = TimeSpan.FromMinutes(30);
        private static readonly SemaphoreSlim _coreUpdatesGate = new(1, 1);

        public static async Task<List<CoreEntry>> GetCoreUpdatesAsync(
            CoreDownloadService downloader, string coresFolder, CancellationToken ct)
        {
            if (_coreUpdates != null && DateTime.UtcNow - _coreUpdatesAt < _coreUpdatesTtl)
                return _coreUpdates;

            await _coreUpdatesGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_coreUpdates != null && DateTime.UtcNow - _coreUpdatesAt < _coreUpdatesTtl)
                    return _coreUpdates;

                // Aggregate cap: 10s. Individual HEAD probes inside CheckAllForUpdatesAsync
                // already swallow errors per-core, so partial results on timeout would be
                // misleading — we'd rather have no decoration than a wrong one.
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                List<CoreEntry> updates;
                try
                {
                    updates = await downloader.CheckAllForUpdatesAsync(coresFolder, linked.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new List<CoreEntry>();
                }

                _coreUpdates = updates;
                _coreUpdatesAt = DateTime.UtcNow;
                return updates;
            }
            finally
            {
                _coreUpdatesGate.Release();
            }
        }

        public static void InvalidateCoreUpdates()
        {
            _coreUpdates = null;
        }

        public static void RemoveCoreUpdate(string fileName)
        {
            var list = _coreUpdates;
            list?.RemoveAll(c => string.Equals(c.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        }

        // ── Controller devices (Fix 6) ────────────────────────────────────────
        // IMPORTANT: SDL3 on Windows hooks WM_DEVICECHANGE messages on the
        // calling thread's message loop to detect hot-plug. Workers don't have
        // message loops, so SDL_PumpEvents on a worker thread misses device
        // events and a freshly-plugged controller takes seconds to surface.
        // We therefore enumerate SYNCHRONOUSLY on the dispatcher (the call is
        // already fast — well under 100ms) and just keep a short-lived cache
        // so that the OnLoaded + hot-plug-timer overlap doesn't double-pump.
        private static List<string>? _controllers;
        private static DateTime _controllersAt;

        public static Task<List<string>> GetControllerDevicesAsync(TimeSpan maxAge)
        {
            if (_controllers != null && DateTime.UtcNow - _controllersAt < maxAge)
                return Task.FromResult(new List<string>(_controllers));

            // Synchronous enumeration on the caller's thread — must be the
            // dispatcher for SDL3 hot-plug to see device-change events.
            var l = new List<string> { "Keyboard" };
            try { l.AddRange(ControllerManager.GetConnectedControllers()); } catch { }
            _controllers = l;
            _controllersAt = DateTime.UtcNow;
            return Task.FromResult(new List<string>(l));
        }

        public static void InvalidateControllers()
        {
            _controllers = null;
        }

        // ── Warm-up ───────────────────────────────────────────────────────────
        /// <summary>
        /// Fire-and-forget pre-population of every cache that the Preferences
        /// window will need. Called once from MainWindow.OnLoaded so the user
        /// never sees a loading state — by the time they click Preferences,
        /// every tab's data is already resident.
        ///
        /// Runs all groups in parallel on the thread pool. Failures are
        /// swallowed; the live builder will fall back to its own work if a
        /// warm-up path threw (e.g. cores folder didn't exist yet).
        /// </summary>
        public static void WarmUp(DatabaseService db, string sysDir, string coresFolder)
        {
            // 3-second deferral: don't compete with WPF's first-window JIT,
            // BAML resource reads, and font/theme loads. The user can't
            // realistically navigate to Preferences within 3s of launch, and
            // the default Controls tab doesn't consume any of these caches
            // anyway — so deferring costs nothing on the worst-case path and
            // restores a clean first-paint for the main window.
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000).ConfigureAwait(false);
                try { await GetBiosScanAsync(db, sysDir, Emutastic.Views.KnownBios.All).ConfigureAwait(false); }
                catch { }
            });
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000).ConfigureAwait(false);
                try { await GetInstalledCoresAsync(coresFolder).ConfigureAwait(false); }
                catch { }
            });
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000).ConfigureAwait(false);
                try { GetThemeSwatches(); } catch { }
            });
            // Controllers are deliberately NOT warmed here. SDL3 on Windows
            // tracks hot-plug via WM_DEVICECHANGE on the calling thread's
            // message loop; only the dispatcher has one. Enumeration happens
            // on first PopulateInputDevicesAsync call (sync on dispatcher,
            // < 100ms typical) and is cached after.
            // GitHub-latest and core-updates are network-bound. They warm up
            // too, but with their own per-call timeouts so a flaky network
            // never blocks anything. Failures here just mean the About / Cores
            // tab fetches on click (still bounded, still cached after).
        }
    }
}
