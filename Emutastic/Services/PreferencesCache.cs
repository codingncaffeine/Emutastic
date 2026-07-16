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

            // Auto-import: recognize BIOS files parked in (sub)folders of the ROM
            // directories — same identification the System Files drag-drop uses
            // (MD5 / GameCube IPL content sniff / canonical name+size) — and copy
            // matches into the System folder's canonical layout, so the panel,
            // the launch checks and per-console launch syncs all see them.
            // Non-destructive: originals stay put; existing System-folder files
            // are never overwritten. Runs on this scan's worker thread.
            try
            {
                var baseRomDirs = games
                    .Where(g => !string.IsNullOrEmpty(g.RomPath))
                    .Select(g => Path.GetDirectoryName(g.RomPath))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                AutoImportRomDirBios(baseRomDirs!, sysDir, biosEntries);
            }
            catch { /* the sweep must never break the scan */ }

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

        // Recognize-and-import sweep over the ROM directories (see call site in
        // BuildBiosScan). Each ROM base dir is walked up to three subfolder
        // levels, so BIOS packs nested a few folders deep (e.g.
        // Roms\GameCube\BIOS\GC\USA\IPL.bin) are still found. Identification is
        // KnownBios.MatchKnownBios — hashing is only attempted on files whose
        // size exactly matches a known dump, so multi-GB ROMs are never read.
        private static void AutoImportRomDirBios(
            IEnumerable<string> baseRomDirs, string sysDir,
            IReadOnlyList<BiosEntry> biosEntries)
        {
            // Candidate gates are built from entries whose System-folder file is
            // still MISSING — once a size/name class is fully satisfied, files of
            // that size are never even hashed again (steady state: sweep is free).
            var missing = biosEntries
                .Where(b => !SafeExists(Path.Combine(sysDir, b.Filename)))
                .ToList();
            if (missing.Count == 0) return;

            var knownSizes = missing.Where(b => b.ExpectedSize > 0)
                .Select(b => b.ExpectedSize).ToHashSet();
            var knownNames = missing
                .Select(b => Path.GetFileName(b.Filename))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string sysPrefix;
            try
            {
                sysPrefix = Path.GetFullPath(sysDir)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            }
            catch { return; }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in baseRomDirs.Distinct(StringComparer.OrdinalIgnoreCase))
                Walk(root, 3);

            void Walk(string dir, int remainingDepth)
            {
                string full;
                try { full = Path.GetFullPath(dir); } catch { return; }
                if (!visited.Add(full)) return;
                // Never treat the System folder itself as an import source.
                if ((full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                        .StartsWith(sysPrefix, StringComparison.OrdinalIgnoreCase)) return;

                IEnumerable<FileInfo> files;
                try { files = new DirectoryInfo(full).EnumerateFiles(); }
                catch { return; }
                foreach (var fi in files) Consider(fi);

                if (remainingDepth <= 0) return;
                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(full); }
                catch { return; }
                foreach (var sub in subs) Walk(sub, remainingDepth - 1);
            }

            void Consider(FileInfo fi)
            {
                long len;
                try { len = fi.Length; } catch { return; }
                bool sizeCandidate = knownSizes.Contains(len);
                bool nameCandidate = knownNames.Contains(fi.Name);
                if (!sizeCandidate && !nameCandidate) return;

                string? md5 = sizeCandidate ? Md5Of(fi.FullName) : null;
                var match = KnownBios.MatchKnownBios(fi.Name, len, md5,
                    () => File.OpenRead(fi.FullName));
                if (match == null) return;
                // The passive sweep is stricter than an explicit drop: never
                // import a name-only match whose size doesn't fit the entry.
                if (match.ExpectedSize > 0 && match.ExpectedSize != len) return;

                foreach (var target in KnownBios.GcIplTargets(match))
                {
                    string dest = Path.Combine(sysDir, target.Filename);
                    if (SafeExists(dest)) continue;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(fi.FullName, dest);
                        System.Diagnostics.Trace.WriteLine(
                            $"[BiosScan] Auto-imported {fi.FullName} → {dest}");
                    }
                    catch { /* locked or unwritable — retried on a later scan */ }
                }
            }

            static string? Md5Of(string path)
            {
                try
                {
                    using var md5 = System.Security.Cryptography.MD5.Create();
                    using var stream = File.OpenRead(path);
                    return BitConverter.ToString(md5.ComputeHash(stream))
                        .Replace("-", "").ToLowerInvariant();
                }
                catch { return null; }
            }
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
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[CTRL-DIAG] PreferencesCache.GetControllerDevicesAsync served from cache (age={(DateTime.UtcNow - _controllersAt).TotalMilliseconds:F0}ms, max={maxAge.TotalMilliseconds:F0}ms)");
                return Task.FromResult(new List<string>(_controllers));
            }

            // Synchronous enumeration on the caller's thread — must be the
            // dispatcher for SDL3 hot-plug to see device-change events.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var l = new List<string> { "Keyboard" };
            try { l.AddRange(ControllerManager.GetConnectedControllers()); } catch { }
            _controllers = l;
            _controllersAt = DateTime.UtcNow;
            sw.Stop();
            System.Diagnostics.Trace.WriteLine(
                $"[CTRL-DIAG] PreferencesCache.GetControllerDevicesAsync fresh enumeration took {sw.ElapsedMilliseconds}ms count={l.Count} devices=[{string.Join(", ", l)}]");
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
