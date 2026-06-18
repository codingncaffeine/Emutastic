using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Emutastic.Models;

namespace Emutastic.Services
{
    public sealed class EsDeImportResult
    {
        public bool Ok { get; set; }
        public string? Id { get; set; }
        public string Message { get; set; } = "";
        public List<string> Notes { get; } = new();
    }

    /// <summary>
    /// Imports a real ES-DE theme into EmuTV. Because our format is deliberately ES-DE-shaped and
    /// our parser reads ES-DE themes directly, "importing" = copy the theme tree into
    /// <c>EmuTvThemes/{id}/</c>, drop in our <c>theme.json</c> manifest, and rescan. Handles a
    /// theme folder or a .zip. Asset/path adaptation is unnecessary (the parser leaves runtime
    /// tokens intact); the only structural fix is synthesising a root <c>theme.xml</c> when a
    /// theme only ships per-system ones.
    /// </summary>
    public static class EsDeThemeImporter
    {
        public static EsDeImportResult ImportFromFolder(string esdeThemeFolder)
        {
            var r = new EsDeImportResult();
            try
            {
                if (string.IsNullOrWhiteSpace(esdeThemeFolder) || !Directory.Exists(esdeThemeFolder))
                { r.Message = "Theme folder not found."; return r; }

                string? root = FindThemeRoot(esdeThemeFolder);
                if (root == null) { r.Message = "No capabilities.xml or theme.xml found in that folder."; return r; }

                string name = ReadThemeName(root) ?? new DirectoryInfo(root).Name;
                string id = Slugify(name);
                if (string.IsNullOrEmpty(id)) id = Slugify(new DirectoryInfo(root).Name);
                if (string.IsNullOrEmpty(id)) { r.Message = "Could not derive a theme id."; return r; }
                if (id.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase)) id = "esde-" + id;

                string dest = Path.Combine(EmuTvThemeService.ThemesFolder, id);
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                CopyTree(root, dest);

                EnsureRootThemeXml(dest, r);
                WriteManifest(dest, id, name);

                EmuTvThemeService.Instance.ScanInstalledThemes();
                r.Ok = true; r.Id = id; r.Message = $"Imported “{name}”.";
                return r;
            }
            catch (Exception ex) { r.Message = "Import failed: " + ex.Message; return r; }
        }

        public static EsDeImportResult ImportFromZip(string zipPath)
        {
            var r = new EsDeImportResult();
            string? temp = null;
            try
            {
                if (!File.Exists(zipPath)) { r.Message = "Zip not found."; return r; }
                temp = Path.Combine(Path.GetTempPath(), "emutv-import-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                ZipFile.ExtractToDirectory(zipPath, temp); // .NET guards against zip-slip
                return ImportFromFolder(temp);
            }
            catch (Exception ex) { r.Message = "Import failed: " + ex.Message; return r; }
            finally { try { if (temp != null && Directory.Exists(temp)) Directory.Delete(temp, true); } catch { } }
        }

        // ── helpers ──────────────────────────────────────────────────────────

        /// <summary>Locates the theme root: the folder itself, a single subfolder, or a folder
        /// that contains per-system theme.xml dirs.</summary>
        private static string? FindThemeRoot(string folder)
        {
            if (HasThemeFiles(folder)) return folder;
            foreach (var d in Directory.GetDirectories(folder))
                if (HasThemeFiles(d)) return d;                       // zip-wrapped one level deep
            foreach (var d in Directory.GetDirectories(folder))
                if (File.Exists(Path.Combine(d, "theme.xml"))) return folder; // per-system layout
            return null;
        }

        private static bool HasThemeFiles(string d) =>
            File.Exists(Path.Combine(d, "capabilities.xml")) || File.Exists(Path.Combine(d, "theme.xml"));

        private static string? ReadThemeName(string root)
        {
            string caps = Path.Combine(root, "capabilities.xml");
            if (!File.Exists(caps)) return null;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var s = File.OpenRead(caps);
                using var rd = XmlReader.Create(s, settings);
                var name = XDocument.Load(rd).Root?.Element("themeName")?.Value.Trim();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch { return null; }
        }

        /// <summary>ES-DE themes may ship only per-system theme.xml (no root). Our parser reads the
        /// root theme.xml, so copy the largest per-system one up as the root default.</summary>
        private static void EnsureRootThemeXml(string dest, EsDeImportResult r)
        {
            if (File.Exists(Path.Combine(dest, "theme.xml"))) return;
            var candidates = Directory.EnumerateFiles(dest, "theme.xml", SearchOption.AllDirectories)
                .OrderByDescending(f => new FileInfo(f).Length).ToList();
            if (candidates.Count == 0) { r.Notes.Add("No root or per-system theme.xml found — theme may not render."); return; }
            File.Copy(candidates[0], Path.Combine(dest, "theme.xml"), true);
            r.Notes.Add($"Synthesised a root layout from '{Path.GetFileName(Path.GetDirectoryName(candidates[0])!)}/theme.xml'.");
        }

        private static void WriteManifest(string dest, string id, string name)
        {
            var manifest = new ThemeManifest
            {
                Id = id, Name = name, Author = "Imported (ES-DE)",
                Version = "1.0.0", Description = "Imported ES-DE theme", ApiVersion = 1,
            };
            File.WriteAllText(Path.Combine(dest, "theme.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void CopyTree(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            {
                if (IsGit(dir)) continue;
                Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(src, dir)));
            }
            foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                if (IsGit(file)) continue;
                var target = Path.Combine(dest, Path.GetRelativePath(src, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }

        private static bool IsGit(string p) =>
            p.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)
            || p.EndsWith(Path.DirectorySeparatorChar + ".git");

        private static string Slugify(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var bad = Path.GetInvalidFileNameChars().Concat(new[] { ' ' }).ToArray();
            var parts = name.ToLowerInvariant().Split(bad, StringSplitOptions.RemoveEmptyEntries);
            return string.Join("-", parts).Replace("..", "-").Trim('-');
        }
    }
}
