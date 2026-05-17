using System;
using System.Reflection;

namespace Emutastic.Services
{
    /// <summary>
    /// Builds the User-Agent header value Emutastic uses when calling
    /// RetroAchievements servers.
    ///
    /// RA's hardcore-compliance policy keys two server-side decisions on this
    /// header: whether the unlock request is hardcore-eligible at all (must
    /// be a recognised emulator), and whether to downgrade hardcore unlocks
    /// to softcore (happens when the UA is missing, malformed, or has no
    /// parseable version). Format per their docs:
    ///
    ///   EmulatorName/v1.0.0 (OSName 10.0) core_name/v0.5.0
    ///
    /// We emit the first two parts here; the core-name suffix is appended
    /// at game-launch time when the libretro core for the session is known.
    ///
    /// Note: a properly-formatted UA is necessary but not sufficient for
    /// hardcore unlocks to actually count. Emutastic also has to be on RA's
    /// approved hardcore-emulator list — that's a separate one-time
    /// application via RAdmin (Discord / forums).
    /// </summary>
    public static class EmutasticUserAgent
    {
        private const string ProductName = "Emutastic";

        /// <summary>
        /// Returns the canonical User-Agent string for HTTP calls to RA.
        /// Example: <c>"Emutastic/1.6.0 (Windows 11)"</c>.
        /// </summary>
        public static string Build()
        {
            return $"{ProductName}/{ResolveVersion()} ({ResolveOs()})";
        }

        private static string ResolveVersion()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? typeof(EmutasticUserAgent).Assembly;
                var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (attr != null && !string.IsNullOrWhiteSpace(attr.InformationalVersion))
                {
                    // Strip any "+commit" suffix MSBuild appends in SourceLink builds.
                    string v = attr.InformationalVersion;
                    int plus = v.IndexOf('+');
                    if (plus > 0) v = v.Substring(0, plus);
                    return v;
                }
                var name = asm.GetName();
                if (name.Version != null)
                    return $"{name.Version.Major}.{name.Version.Minor}.{name.Version.Build}";
            }
            catch { /* fall through */ }
            return "0.0.0";
        }

        private static string ResolveOs()
        {
            try
            {
                // Distinguish Windows 11 (build >= 22000) from Windows 10
                // since RA's UA validator parses the OS bracket and the
                // version split is the major fork users will be on.
                var v = Environment.OSVersion.Version;
                if (v.Major >= 10 && v.Build >= 22000) return "Windows 11";
                if (v.Major >= 10) return "Windows 10";
                return $"Windows {v.Major}.{v.Minor}";
            }
            catch { return "Windows"; }
        }
    }
}
