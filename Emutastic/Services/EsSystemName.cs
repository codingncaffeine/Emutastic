using System;
using System.Linq;

namespace Emutastic.Services
{
    /// <summary>
    /// Maps an Emutastic console name to the ES-DE "system theme" name that ES-DE themes use for
    /// <c>${system.theme}</c> (per-system backgrounds, logos, etc.). Falls back to a normalised
    /// form of the name, then the theme's own <c>_default</c> asset.
    /// </summary>
    public static class EsSystemName
    {
        // First contained keyword wins, so list more specific names before broader ones.
        private static readonly (string kw, string es)[] Map =
        {
            ("nintendo entertainment", "nes"), ("famicom", "nes"),
            ("super nintendo", "snes"), ("super famicom", "snes"), ("snes", "snes"),
            ("nintendo 64", "n64"), ("n64", "n64"),
            ("gamecube", "gc"),
            ("game boy advance", "gba"), ("game boy color", "gbc"), ("game boy", "gb"),
            ("nintendo ds", "nds"), ("3ds", "n3ds"),
            ("mega drive", "megadrive"), ("megadrive", "megadrive"), ("genesis", "genesis"),
            ("sega cd", "segacd"), ("mega cd", "segacd"),
            ("32x", "sega32x"),
            ("saturn", "saturn"),
            ("dreamcast", "dreamcast"),
            ("master system", "mastersystem"), ("sms", "mastersystem"), ("mark iii", "mark3"),
            ("game gear", "gamegear"),
            ("sg-1000", "sg-1000"), ("sg1000", "sg-1000"),
            ("playstation 2", "ps2"), ("playstation portable", "psp"), ("psp", "psp"),
            ("playstation", "psx"), ("ps1", "psx"), ("psx", "psx"),
            ("neo geo cd", "neogeocd"), ("neogeo cd", "neogeocd"), ("neocd", "neogeocd"),
            ("neo geo", "neogeo"), ("neogeo", "neogeo"),
            ("turbografx-cd", "tg-cd"), ("turbografx cd", "tg-cd"), ("pc engine cd", "pcenginecd"),
            ("tgcd", "tg-cd"), ("pcecd", "pcenginecd"),
            ("turbografx", "tg16"), ("pc engine", "tg16"), ("pc-engine", "tg16"),
            ("3do", "3do"),
            ("vectrex", "vectrex"),
            ("wonderswan", "wonderswan"),
            ("atari 2600", "atari2600"), ("atari 5200", "atari5200"), ("atari 7800", "atari7800"),
            ("atari lynx", "atarilynx"), ("lynx", "atarilynx"),
            ("atari jaguar", "atarijaguar"), ("jaguar", "atarijaguar"),
            ("colecovision", "colecovision"),
            ("intellivision", "intellivision"),
            ("cd-i", "cdimono1"), ("cdi", "cdimono1"),
            ("commodore 64", "c64"), ("c64", "c64"),
            ("amiga", "amiga"),
            ("msx", "msx"),
            ("arcade", "arcade"), ("mame", "arcade"), ("fbneo", "arcade"), ("final burn", "arcade"),
        };

        public static string For(string? consoleName)
        {
            if (string.IsNullOrWhiteSpace(consoleName)) return "_default";
            string n = consoleName.ToLowerInvariant();
            foreach (var (kw, es) in Map) if (n.Contains(kw)) return es;
            // Normalised fallback (e.g. "Atari 800" -> "atari800"), then the theme default.
            string norm = new string(n.Where(char.IsLetterOrDigit).ToArray());
            return norm.Length > 0 ? norm : "_default";
        }
    }
}
