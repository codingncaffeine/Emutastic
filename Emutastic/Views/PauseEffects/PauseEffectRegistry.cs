using System;
using System.Collections.Generic;
using System.Linq;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Catalog of available pause effects. Adding a new effect = add a factory
    /// entry here. The Preferences picker enumerates this list, and the runner
    /// looks up by Id when the user has a saved preference.
    /// </summary>
    public static class PauseEffectRegistry
    {
        public const string NoneId = "none";

        public sealed record Entry(string Id, string DisplayName, Func<object> Factory, bool IsPixel);

        private static readonly List<Entry> _entries = new()
        {
            new(NoneId,          "None — paused frame only", () => null!,                false),
            new("snow",          "Snow",                     () => new Snow(),           false),
            new("rain",          "Rain",                     () => new Rain(),           false),
            new("starfield",     "Starfield",                () => new Starfield(),      false),
            new("matrix",        "Matrix Rain",              () => new MatrixRain(),     false),
            new("constellation", "Constellation",            () => new Constellation(),  false),
            new("synthwave",     "Synthwave Grid",           () => new SynthwaveGrid(),  false),
            new("fireworks",     "Fireworks",                () => new Fireworks(),      false),
            new("plasma",        "Plasma",                   () => new Plasma(),         true),
            new("aurora",        "Aurora",                   () => new Aurora(),         true),
        };

        public static IReadOnlyList<Entry> All => _entries;

        public static Entry? Find(string id)
            => _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

        public static object? Create(string id)
        {
            var entry = Find(id);
            if (entry == null || entry.Id == NoneId) return null;
            return entry.Factory();
        }
    }
}
