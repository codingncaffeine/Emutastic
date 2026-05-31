using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emutastic.Effects.Librashader;

/// <summary>
/// One selectable entry in the shader picker — either a built-in WPF effect or a
/// downloaded libretro <c>.slangp</c> preset. Bindable (getters) for the picker ListBox.
/// </summary>
public sealed class ShaderPresetItem
{
    public string Display { get; init; } = "";
    /// <summary>Group header in the picker (e.g. "Built-in", "crt", "handheld").</summary>
    public string Category { get; init; } = "";
    public bool IsBuiltin { get; init; }
    public ShaderPreset Builtin { get; init; }
    /// <summary>Absolute path to the .slangp (downloaded items only).</summary>
    public string? AbsolutePath { get; init; }
    /// <summary>Path relative to the slang root, '/'-normalized — the persistence key (downloaded only).</summary>
    public string? RelativePath { get; init; }
}

/// <summary>
/// Enumerates the downloaded libretro slang shader pack into picker entries.
///
/// The pack is huge (~2500 .slangp) and most of it (bezel/, presets/, shared
/// includes, test/spec/reshade/hdr) is not "pick one effect" material, so those
/// top-level folders are filtered out. Results are cached and invalidated when the
/// pack is re-downloaded (keyed off the <c>.installed</c> marker's timestamp).
///
/// Call <see cref="GetDownloaded"/> off the UI thread (it walks the tree).
/// </summary>
public static class ShaderCatalog
{
    // Top-level folders under the slang root that aren't standalone single-effect
    // presets usable in this picker. "bezel/presets/include/test/spec/reshade/hdr"
    // are decorative/shared/auxiliary; "nes_raw_palette" requires the core to emit
    // raw PPU palette indices (a special output mode we don't enable) — fed a normal
    // RGB frame it produces wrong/missing colors, so it's hidden.
    private static readonly HashSet<string> ExcludedCategories =
        new(StringComparer.OrdinalIgnoreCase)
        { "bezel", "presets", "include", "test", "spec", "reshade", "hdr", "nes_raw_palette" };

    private static List<ShaderPresetItem>? _cache;
    private static long _cacheStamp = -1;
    private static readonly object _gate = new();

    /// <summary>
    /// Returns the filtered, category-grouped downloaded presets, or an empty list
    /// if the pack isn't installed. Cached until the pack is re-downloaded.
    /// </summary>
    public static IReadOnlyList<ShaderPresetItem> GetDownloaded(string slangRoot)
    {
        try
        {
            string marker = Path.Combine(slangRoot, ".installed");
            if (!File.Exists(marker)) return Array.Empty<ShaderPresetItem>();
            long stamp = File.GetLastWriteTimeUtc(marker).Ticks;

            lock (_gate)
                if (_cache != null && _cacheStamp == stamp) return _cache;

            var list = new List<ShaderPresetItem>();
            foreach (var file in Directory.EnumerateFiles(slangRoot, "*.slangp", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(slangRoot, file).Replace('\\', '/');
                int slash = rel.IndexOf('/');
                string cat = slash > 0 ? rel[..slash] : "misc";
                if (ExcludedCategories.Contains(cat)) continue;

                list.Add(new ShaderPresetItem
                {
                    Display      = Path.GetFileNameWithoutExtension(file),
                    Category     = cat,
                    IsBuiltin    = false,
                    AbsolutePath = file,
                    RelativePath = rel,
                });
            }
            list.Sort((a, b) =>
            {
                int c = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
            });

            lock (_gate) { _cache = list; _cacheStamp = stamp; }
            return list;
        }
        catch
        {
            return Array.Empty<ShaderPresetItem>();
        }
    }

    /// <summary>
    /// Resolves a persisted value to an absolute .slangp path. Accepts either a
    /// '/'-normalized relative path (current format) or a bare filename (legacy
    /// "slang:crt-easymode.slangp" format). Legacy resolution is deterministic:
    /// non-bezel matches, shallowest path first. Returns null if not found.
    /// </summary>
    public static string? Resolve(string slangRoot, string relativeOrName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relativeOrName)) return null;

            // Current format: a relative path under the slang root.
            string direct = Path.GetFullPath(Path.Combine(slangRoot, relativeOrName));
            if (File.Exists(direct)) return direct;

            // Legacy format: a bare filename. Pick deterministically.
            string name = Path.GetFileName(relativeOrName);
            return Directory.EnumerateFiles(slangRoot, name, SearchOption.AllDirectories)
                .Where(p => !p.Replace('\\', '/').Contains("/bezel/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Count(ch => ch == '/' || ch == '\\'))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
