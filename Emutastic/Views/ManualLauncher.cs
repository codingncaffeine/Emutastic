using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Emutastic.Models;
using Emutastic.Services;

namespace Emutastic.Views
{
    /// <summary>
    /// Shared entry point for opening a game's PDF manual from the library, the detail
    /// card, or in-game. Downloads it first if it isn't already cached, then shows the
    /// floating viewer. Two flavors: banner-backed (library / detail card, via
    /// ArtworkFetchService) and standalone (in-game, status via a caller callback so it
    /// can surface on the emulator HUD instead of the — invisible — main banner).
    /// </summary>
    public static class ManualLauncher
    {
        private static bool HasUsableManual(Game g)
            => !string.IsNullOrEmpty(g.ManualPath) && File.Exists(g.ManualPath);

        /// <summary>Library / detail card path: progress shown in the bottom banner.</summary>
        public static async Task OpenOrDownloadAsync(Game game, ArtworkFetchService fetch, Window? owner, bool pinned = false)
        {
            if (HasUsableManual(game)) { ManualViewerWindow.ShowFor(game, owner, pinned); return; }
            string? path = await fetch.FetchManualForGameAsync(game);
            if (path != null) ManualViewerWindow.ShowFor(game, owner, pinned);
        }

        /// <summary>In-game path: no main banner, so status text goes to a caller callback (HUD).</summary>
        public static async Task OpenOrDownloadInGameAsync(Game game, DatabaseService db, Window owner, Action<string> status)
        {
            if (HasUsableManual(game)) { ManualViewerWindow.ShowFor(game, owner, pinned: true); return; }

            var snapCfg = App.Configuration?.GetSnapConfiguration();
            if (snapCfg == null || !snapCfg.ScreenScraperEnabled
                || string.IsNullOrWhiteSpace(snapCfg.ScreenScraperUser))
            {
                status("Set up ScreenScraper in Preferences to download manuals");
                return;
            }

            status("Downloading manual…");
            var ss = new ScreenScraperService();
            var result = await Task.Run(() => ss.FetchManualAsync(
                snapCfg.ScreenScraperUser, snapCfg.ScreenScraperPassword,
                game.Console, game.Title, game.RomHash, game.RomPath,
                progress: p => status($"Downloading manual… {p:0}%")));

            if (result.LocalPath != null)
            {
                try { db.UpdateManualPath(game.Id, result.LocalPath); } catch { }
                game.ManualPath = result.LocalPath;
                status("");
                ManualViewerWindow.ShowFor(game, owner, pinned: true);
            }
            else if (result.OverQuota) status("ScreenScraper daily limit reached — try again later");
            else if (result.NotFound)  status("No manual found for this game");
            else status("Couldn't download the manual");
        }
    }
}
