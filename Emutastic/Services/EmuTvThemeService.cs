using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Emutastic.Configuration;
using Emutastic.Models;
using Emutastic.Models.EmuTv;

namespace Emutastic.Services
{
    /// <summary>Lightweight summary of an installed EmuTV theme for pickers/browsers.</summary>
    public sealed class EmuTvThemeInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Author { get; init; } = "";
        public bool IsBuiltin { get; init; }
        public string RootPath { get; init; } = "";
        public ThemeCapabilities Capabilities { get; init; } = new();
    }

    /// <summary>
    /// Loads, scans, and installs EmuTV themes — the controller-shell counterpart to the desktop
    /// (colors-only) <see cref="ThemeService"/>, which is left untouched. Themes live under
    /// <c>[DataRoot]/EmuTvThemes/{id}/</c> as ES-DE-shaped packages (capabilities.xml + theme.xml +
    /// assets). Built-in themes are registered in-process. The user's axis selection
    /// (variant/colorScheme/aspectRatio/fontSize) and the active theme id persist in config.
    /// </summary>
    public sealed class EmuTvThemeService
    {
        private static EmuTvThemeService? _instance;
        public static EmuTvThemeService Instance => _instance ??= new EmuTvThemeService();

        /// <summary>Folder holding installed EmuTV themes (created on demand, portable-safe).</summary>
        public static string ThemesFolder => AppPaths.GetFolder("EmuTvThemes");

        /// <summary>Raised when the installed-theme set changes (install/uninstall/rescan) or the
        /// active theme / selection changes — so an open EmuTV window can rebuild.</summary>
        public event EventHandler? ThemesChanged;

        private readonly Dictionary<string, EmuTvThemeInfo> _builtins = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, EmuTvThemeInfo> _installed = new(StringComparer.OrdinalIgnoreCase);

        private EmuTvThemeService()
        {
            RegisterBuiltinThemes();
            ScanInstalledThemes();
        }

        private void RegisterBuiltinThemes()
        {
            // The shipped default theme — the EmuTV layout authored under
            // Assets/emutv-themes/default (copied next to the exe as loose files).
            var root = Path.Combine(AppContext.BaseDirectory, "Assets", "emutv-themes", "default");
            if (Directory.Exists(root)) RegisterBuiltin("builtin.emutv-default", root);
        }

        // ── discovery ──────────────────────────────────────────────────────────

        /// <summary>Built-ins first, then installed community themes, ordered by name.</summary>
        public IReadOnlyList<EmuTvThemeInfo> AvailableThemes =>
            _builtins.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(_installed.Values
                .Where(t => !_builtins.ContainsKey(t.Id))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        /// <summary>Registers a shipped built-in theme rooted at <paramref name="rootPath"/>
        /// (e.g. an extracted/bundled folder). Populated by the renderer layer in P2+.</summary>
        public void RegisterBuiltin(string id, string rootPath)
        {
            var info = BuildInfo(rootPath, isBuiltin: true, idOverride: id);
            if (info != null) _builtins[id] = info;
        }

        /// <summary>Rescans the themes folder. Cheap — reads each theme's capabilities.xml only,
        /// not its full layout.</summary>
        public void ScanInstalledThemes()
        {
            _installed.Clear();
            string dir = ThemesFolder;
            if (!Directory.Exists(dir)) return;

            foreach (var d in Directory.GetDirectories(dir))
            {
                try
                {
                    bool isTheme = File.Exists(Path.Combine(d, "capabilities.xml"))
                                || File.Exists(Path.Combine(d, "theme.xml"));
                    if (!isTheme) continue;
                    var info = BuildInfo(d, isBuiltin: false);
                    if (info != null) _installed[info.Id] = info;
                }
                catch { /* skip a malformed theme rather than breaking the scan */ }
            }
        }

        private EmuTvThemeInfo? BuildInfo(string root, bool isBuiltin, string? idOverride = null)
        {
            string id = "", name = "", author = "";
            var manifestPath = Path.Combine(root, "theme.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<ThemeManifest>(File.ReadAllText(manifestPath));
                    if (m != null) { id = m.Id; name = m.Name; author = m.Author; }
                }
                catch { /* fall back to capabilities/folder below */ }
            }

            var caps = new EmuTvThemeParser(root).ParseCapabilities();

            if (idOverride != null) id = idOverride;
            if (string.IsNullOrWhiteSpace(id)) id = Slugify(caps.ThemeName);
            if (string.IsNullOrWhiteSpace(id)) id = Path.GetFileName(root);
            if (string.IsNullOrWhiteSpace(name)) name = caps.ThemeName;
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(root);

            return new EmuTvThemeInfo
            {
                Id = id, Name = name, Author = author,
                IsBuiltin = isBuiltin, RootPath = root, Capabilities = caps,
            };
        }

        private string? ResolveRoot(string id)
        {
            if (_builtins.TryGetValue(id, out var b)) return b.RootPath;
            if (_installed.TryGetValue(id, out var i)) return i.RootPath;
            string dir = Path.Combine(ThemesFolder, id);
            return Directory.Exists(dir) ? dir : null;
        }

        // ── active theme + axis selection (persisted) ───────────────────────────

        /// <summary>The active theme id, falling back to the first available theme.</summary>
        public string ActiveThemeId
        {
            get
            {
                var id = App.Configuration?.GetThemeConfiguration().EmuTvThemeId;
                if (!string.IsNullOrWhiteSpace(id) && ResolveRoot(id) != null) return id!;
                return _builtins.Keys.FirstOrDefault() ?? _installed.Keys.FirstOrDefault() ?? "";
            }
        }

        public void SetActiveTheme(string id)
        {
            var cfg = App.Configuration?.GetThemeConfiguration();
            if (cfg == null) return;
            cfg.EmuTvThemeId = id ?? "";
            Persist(cfg);
            ThemesChanged?.Invoke(this, EventArgs.Empty);
        }

        public ThemeSelection GetSelection()
        {
            var cfg = App.Configuration?.GetThemeConfiguration();
            return new ThemeSelection
            {
                Variant = NullIfEmpty(cfg?.EmuTvVariant),
                ColorScheme = NullIfEmpty(cfg?.EmuTvColorScheme),
                AspectRatio = NullIfEmpty(cfg?.EmuTvAspectRatio),
                FontSize = NullIfEmpty(cfg?.EmuTvFontSize),
            };
        }

        public void SetSelection(ThemeSelection sel)
        {
            var cfg = App.Configuration?.GetThemeConfiguration();
            if (cfg == null) return;
            cfg.EmuTvVariant = sel.Variant ?? "";
            cfg.EmuTvColorScheme = sel.ColorScheme ?? "";
            cfg.EmuTvAspectRatio = sel.AspectRatio ?? "";
            cfg.EmuTvFontSize = sel.FontSize ?? "";
            Persist(cfg);
            ThemesChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── loading (full parse) ────────────────────────────────────────────────

        /// <summary>Parses a theme by id for the given selection (or the persisted one).</summary>
        public EmuTvThemeParseResult? LoadTheme(string id, ThemeSelection? selection = null)
        {
            string? root = ResolveRoot(id);
            if (root == null) return null;
            var res = new EmuTvThemeParser(root).Parse(selection ?? GetSelection());
            res.Theme.Id = id;
            res.Theme.IsBuiltin = _builtins.ContainsKey(id);
            return res;
        }

        /// <summary>Parses the active theme for the persisted selection. Null if nothing's installed.</summary>
        public EmuTvThemeParseResult? LoadActiveTheme()
        {
            string id = ActiveThemeId;
            return string.IsNullOrEmpty(id) ? null : LoadTheme(id, GetSelection());
        }

        private readonly Dictionary<string, ThemeCapabilities> _capsCache = new();

        /// <summary>Parses + caches a theme's capabilities.xml — the axis options (colour schemes,
        /// variants, aspect ratios) the EmuTV axis picker cycles through. Cheap (capabilities.xml only).</summary>
        public ThemeCapabilities? GetCapabilities(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_capsCache.TryGetValue(id, out var cached)) return cached;
            string? root = ResolveRoot(id);
            if (root == null) return null;
            var caps = new EmuTvThemeParser(root).ParseCapabilities();
            _capsCache[id] = caps;
            return caps;
        }

        // ── install (drop-in .emutvtheme zip) ───────────────────────────────────

        /// <summary>Installs a .emutvtheme (or ES-DE theme) zip into the themes folder.
        /// Returns the installed id, or null on failure. Id is sanitized to block traversal
        /// and built-in spoofing; .NET's ExtractToDirectory guards against zip-slip.</summary>
        public string? InstallTheme(string zipPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);

                string? id = null;
                var manifestEntry = zip.GetEntry("theme.json");
                if (manifestEntry != null)
                {
                    using var reader = new StreamReader(manifestEntry.Open());
                    var manifest = JsonSerializer.Deserialize<ThemeManifest>(reader.ReadToEnd());
                    if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Id)) id = manifest.Id;
                }
                if (string.IsNullOrWhiteSpace(id))
                    id = Slugify(Path.GetFileNameWithoutExtension(zipPath));
                if (string.IsNullOrWhiteSpace(id)) return null;

                // Reject traversal and any attempt to pose as a built-in.
                if (id.Contains("..") || id.Contains('/') || id.Contains('\\')) return null;
                if (id.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase)) return null;
                id = string.Join("_", id.Split(Path.GetInvalidFileNameChars()));

                var dest = Path.Combine(ThemesFolder, id);
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                Directory.CreateDirectory(dest);
                zip.ExtractToDirectory(dest, overwriteFiles: true);

                // A theme may be nested one folder deep inside the zip — flatten if so.
                NormalizeExtractedRoot(dest);

                ScanInstalledThemes();
                ThemesChanged?.Invoke(this, EventArgs.Empty);
                return id;
            }
            catch { return null; }
        }

        public bool UninstallTheme(string id)
        {
            if (_builtins.ContainsKey(id)) return false; // built-ins can't be removed
            var dir = Path.Combine(ThemesFolder, id);
            if (!Directory.Exists(dir)) return false;
            try
            {
                Directory.Delete(dir, true);
                _installed.Remove(id);
                ThemesChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch { return false; }
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        /// <summary>If a zip wrapped the theme in a single subfolder (no capabilities.xml/theme.xml
        /// at the root), promote that subfolder's contents to the root.</summary>
        private static void NormalizeExtractedRoot(string dest)
        {
            if (File.Exists(Path.Combine(dest, "capabilities.xml")) ||
                File.Exists(Path.Combine(dest, "theme.xml"))) return;

            var subdirs = Directory.GetDirectories(dest);
            if (subdirs.Length != 1) return;
            var inner = subdirs[0];
            if (!File.Exists(Path.Combine(inner, "capabilities.xml")) &&
                !File.Exists(Path.Combine(inner, "theme.xml"))) return;

            foreach (var f in Directory.GetFiles(inner))
                File.Move(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
            foreach (var d in Directory.GetDirectories(inner))
                Directory.Move(d, Path.Combine(dest, Path.GetFileName(d)));
            try { Directory.Delete(inner, true); } catch { /* best effort */ }
        }

        private static void Persist(ThemeConfiguration cfg)
        {
            App.Configuration?.SetThemeConfiguration(cfg);
            _ = App.Configuration?.SaveAsync(); // fire-and-forget; never blocks the UI thread
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static string Slugify(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var parts = name.ToLowerInvariant()
                .Split(Path.GetInvalidFileNameChars().Concat(new[] { ' ' }).ToArray(),
                       StringSplitOptions.RemoveEmptyEntries);
            return string.Join("-", parts);
        }
    }
}
