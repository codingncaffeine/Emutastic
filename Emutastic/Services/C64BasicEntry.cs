using System;
using System.IO;
using System.Windows;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// The Commodore 64's built-in library entry: "Commodore 64 BASIC", the machine as it powers on, at the BASIC
    /// prompt with nothing loaded, for writing programs. It appears once the C64's system ROMs are in the System
    /// folder. Its library file is a marker (<see cref="LibretroCore.IsNoContentMarker"/>): the core is started
    /// with no content. Its picture is the start-up screen as Kernal64 draws it, shipped with the app, so nothing
    /// is looked up for it online.
    /// </summary>
    public static class C64BasicEntry
    {
        public const string Title = "Commodore 64 BASIC";
        /// <summary>The entry's fixed hash: the key of its video in the snap cache (Snaps/C64/c64basic.mp4).</summary>
        public const string Hash = "c64basic";

        public static string MarkerPath => Path.Combine(AppPaths.GetFolder("BuiltIn", "C64"), Title + ".nocontent");

        /// <summary>Adds the entry when the ROMs are present and it is not in the library yet. True = added.</summary>
        public static bool Ensure(DatabaseService db)
        {
            try
            {
                if (C64Roms.Missing(AppPaths.GetFolder("System")).Count > 0) return false;
                string marker = MarkerPath;
                if (db.RomPathExists(marker)) return false;

                File.WriteAllText(marker, "Emutastic built-in entry: starts the Commodore 64 with nothing loaded.\n");
                string art = Path.Combine(AppPaths.GetFolder("Artwork", "C64"), "Commodore 64 BASIC.png");
                CopyAsset("c64_basic_screen.png", art);
                var (bg, accent) = RomService.GetConsoleColors("C64");
                db.InsertGame(new Game
                {
                    Title = Title,
                    Console = "C64",
                    Manufacturer = "Commodore",
                    Year = 1982,
                    RomPath = marker,
                    OriginalSourcePath = marker,
                    RomHash = Hash,
                    CoverArtPath = art,
                    BoxArt3DPath = art,
                    BackgroundColor = bg,
                    AccentColor = accent,
                    Developer = "Commodore",
                    Publisher = "Commodore",
                    Genre = "Programming",
                    Description = "The Commodore 64 as it powers on: BASIC V2 at the READY prompt, with nothing loaded. " +
                                  "Type programs and run them; save them to a disk or a tape.",
                });
                System.Diagnostics.Trace.WriteLine("[C64] built-in entry added: " + Title);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("[C64] built-in entry not added: " + ex.Message);
                return false;
            }
        }

        private static void CopyAsset(string name, string dest)
        {
            if (File.Exists(dest)) return;
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/images/C64/" + name));
            if (res == null) return;
            using var src = res.Stream;
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
    }
}
