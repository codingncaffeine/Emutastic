using Emutastic.Configuration;
using Emutastic.Models;
using Emutastic.Services;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Emutastic
{
    /// <summary>
    /// <c>Emutastic.exe --selftest-windows-apps [report.log] --portable</c>: the Windows platform
    /// and the data-driven sidebar, end to end, with no window shown. Covers which files count as
    /// Windows apps (a PS-X EXE doesn't), their titles, which program a dropped folder stands for,
    /// the import pipeline on a throwaway library (an .exe dropped on another console's nav lands
    /// under Windows; a ROM dropped on the Windows nav doesn't), launching with play-time tracking,
    /// the generated icon cover, and the sidebar the main window builds from ConsoleCatalog —
    /// default layout, customised layout, and "hide consoles with no games".
    ///
    /// Needs --portable and PortableData that has never been used (run a fresh copy of the build
    /// output); it never touches the user's library. Exit code 0 = pass, 1 = a check failed,
    /// 2 = incomplete.
    /// </summary>
    internal static class WindowsAppsSelfTest
    {
        public static int Run(string? reportPath)
        {
            string path = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportPath() : reportPath!;
            using var r = new Report(path);
            try
            {
                // Runs on the UI thread (the main window is built here) with the dispatcher pumping,
                // so work that reports back through it — the launcher — can.
                var frame = new DispatcherFrame();
                Task<int> test = RunAsync(r);
                test.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                return test.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                r.Line($"  [FAIL] unhandled: {ex}");
                r.Line("=== FAIL ===");
                return 1;
            }
        }

        private static string DefaultReportPath()
        {
            try { return Path.Combine(AppPaths.GetFolder("Logs"), "windows-apps-selftest.log"); }
            catch { return Path.Combine(AppContext.BaseDirectory, "windows-apps-selftest.log"); }
        }

        // The sidebar as it shipped in v1.8.8 (MainWindow.xaml), row for row, plus Windows.
        private static readonly (string Tag, string Name, string? Group)[] ExpectedCatalog =
        {
            ("Arcade", "Arcade", null), ("Windows", "Windows", null),
            ("Atari2600", "Atari 2600", "ATARI"), ("Atari7800", "Atari 7800", "ATARI"), ("Jaguar", "Atari Jaguar", "ATARI"),
            ("NES", "Nintendo (NES)", "NINTENDO"), ("FDS", "Famicom Disk System", "NINTENDO"), ("SNES", "Super Nintendo", "NINTENDO"),
            ("N64", "Nintendo 64", "NINTENDO"), ("GameCube", "GameCube", "NINTENDO"), ("GB", "Game Boy", "NINTENDO"),
            ("GBC", "Game Boy Color", "NINTENDO"), ("GBA", "Game Boy Advance", "NINTENDO"), ("3DS", "Nintendo 3DS", "NINTENDO"),
            ("NDS", "Nintendo DS", "NINTENDO"), ("VirtualBoy", "Virtual Boy", "NINTENDO"),
            ("SMS", "Sega Master System", "SEGA"), ("Genesis", "Sega Genesis", "SEGA"), ("SegaCD", "Sega CD", "SEGA"),
            ("Sega32X", "Sega 32X", "SEGA"), ("Saturn", "Sega Saturn", "SEGA"), ("GameGear", "Sega Game Gear", "SEGA"),
            ("SG1000", "SG-1000", "SEGA"), ("Dreamcast", "Dreamcast", "SEGA"),
            ("PS1", "PlayStation", "SONY"), ("PS2", "PlayStation 2", "SONY"), ("PS3", "PlayStation 3", "SONY"), ("PSP", "PSP", "SONY"),
            ("TG16", "TurboGrafx-16", "NEC"), ("TGCD", "TurboGrafx-CD", "NEC"),
            ("NeoGeo", "Neo Geo", "SNK"), ("NeoCD", "Neo Geo CD", "SNK"), ("NGP", "NeoGeo Pocket", "SNK"),
            ("3DO", "3DO", "OTHER"), ("CDi", "Philips CD-i", "OTHER"), ("ColecoVision", "ColecoVision", "OTHER"), ("Vectrex", "Vectrex", "OTHER"),
        };

        private static async Task<int> RunAsync(Report r)
        {
            r.Line("=== Windows apps + sidebar self-test ===");
            string root = AppPaths.DataRoot;
            if (!AppPaths.IsPortable
                || File.Exists(Path.Combine(root, "config.json"))
                || File.Exists(Path.Combine(root, "library.db")))
            {
                r.Line($"  [SKIP] needs --portable and an unused PortableData ({root}); run a fresh copy of the build output");
                r.Line("=== INCOMPLETE ===");
                return 2;
            }

            var config = new JsonConfigurationService(null);
            await config.LoadAsync();
            App.Configuration = config;
            ThemeService.Instance.ScanInstalledThemes();
            ThemeService.Instance.LoadAndApplyTheme(config.GetThemeConfiguration().ActiveThemeId);

            string fixtures = Path.Combine(root, "selftest-fixtures");
            Directory.CreateDirectory(fixtures);
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

            CatalogChecks(r);
            DetectionChecks(r, fixtures, notepad);
            FolderChecks(r, fixtures);
            await ImportChecks(r, fixtures, notepad, config);
            IconCoverChecks(r, fixtures, notepad);
            await LaunchChecks(r, fixtures);
            SidebarChecks(r, config);
            await ConfigRoundTripChecks(r, config);

            r.Line(r.Failures == 0 ? "=== PASS ===" : $"=== FAIL ({r.Failures} check(s)) ===");
            return r.Failures == 0 ? 0 : 1;
        }

        // ── Catalog ─────────────────────────────────────────────────────────────────────────
        private static void CatalogChecks(Report r)
        {
            r.Line("--- console catalog");
            var actual = ConsoleCatalog.Default.Select(e => (e.Tag, e.DisplayName, e.Group)).ToArray();
            r.Check(actual.SequenceEqual(ExpectedCatalog),
                    $"the default list is the v1.8.8 sidebar, entry for entry, plus Windows ({actual.Length} entries)");
            r.Check(ConsoleCatalog.DefaultGroupOrder.SequenceEqual(new[] { "ATARI", "NINTENDO", "SEGA", "SONY", "NEC", "SNK", "OTHER" }),
                    "headings keep their order");

            var noIcon = ConsoleCatalog.Default.Where(e => ConsoleCatalog.IconUri(e.Tag) == null).Select(e => e.Tag).ToList();
            var missingResource = ConsoleCatalog.Default
                .Select(e => ConsoleCatalog.IconUri(e.Tag))
                .Where(u => u != null && !ResourceExists(u!))
                .ToList();
            r.Check(noIcon.Count == 0 && missingResource.Count == 0,
                    $"every console has an icon that exists in the app ({string.Join(", ", noIcon.Concat(missingResource!))})");
            r.Check(ConsoleCatalog.IconUri("PS2") != null && ConsoleCatalog.IconUri("PS3") != null && ConsoleCatalog.IconUri("NGPC") != null,
                    "PS2, PS3 and NGPC have icons in the shared table (the list view's copy had none for PS2/PS3)");
            var unknown = ConsoleCatalog.Default.Where(e => !RomService.IsKnownConsoleTag(e.Tag)).Select(e => e.Tag).ToList();
            r.Check(unknown.Count == 0, $"every console is a valid import target ({string.Join(", ", unknown)})");

            var other = ConsoleCatalog.InGroup("OTHER").ToList();
            var ordered = ConsoleCatalog.InUserOrder(other, new[] { "Vectrex", "NotAConsole", "3DO" }, e => e.Tag).Select(e => e.Tag).ToArray();
            r.Check(ordered.SequenceEqual(new[] { "Vectrex", "3DO", "CDi", "ColecoVision" }),
                    $"a user order places its consoles first and keeps the rest in catalog order ({string.Join(", ", ordered)})");
            r.Check(ConsoleCatalog.InUserOrder(other, new List<string>(), e => e.Tag).SequenceEqual(other),
                    "an empty user order is the catalog order");
            r.Check(ConsoleCatalog.DisplayNameFor("SNES") == "Super Nintendo" && ConsoleCatalog.DisplayNameFor("Amiga") == "Amiga",
                    "display names come from the catalog, falling back to the tag");
        }

        private static bool ResourceExists(string packUri)
        {
            try { return Application.GetResourceStream(new Uri(packUri, UriKind.Absolute)) != null; }
            catch { return false; }
        }

        // ── Detection and titles ────────────────────────────────────────────────────────────
        private static void DetectionChecks(Report r, string fixtures, string notepad)
        {
            r.Line("--- which files are Windows apps");
            string dir = Path.Combine(fixtures, "detect");
            string fakeProgram = WriteFakeProgram(Path.Combine(dir, "hollow_knight.exe"), 4096);
            string fakeDll = WriteFakeProgram(Path.Combine(dir, "library.exe"), 4096, dll: true);
            string psxExe = Path.Combine(dir, "psx.exe");
            File.WriteAllBytes(psxExe, Encoding.ASCII.GetBytes("PS-X EXE").Concat(new byte[2040]).ToArray());
            string textExe = Path.Combine(dir, "notes.exe");
            File.WriteAllText(textExe, "not a program");
            string dosExe = Path.Combine(dir, "dosgame.exe");
            File.WriteAllBytes(dosExe, new byte[] { (byte)'M', (byte)'Z' }.Concat(new byte[1022]).ToArray());
            string bat = Path.Combine(dir, "run game.bat");
            File.WriteAllText(bat, "@echo off\r\n");
            string url = WriteUrl(Path.Combine(dir, "Celeste.url"), "steam://rungameid/504230");
            string web = WriteUrl(Path.Combine(dir, "Site.url"), "https://example.com/");
            string lnk = WriteShortcut(Path.Combine(dir, "Notepad - Shortcut.lnk"), notepad);

            r.Check(File.Exists(notepad) && WindowsApps.IsAppFile(notepad), "a real Windows program (notepad.exe) is an app");
            r.Check(WindowsApps.IsAppFile(fakeProgram), "a PE executable is an app");
            r.Check(!WindowsApps.IsAppFile(fakeDll), "a PE flagged as a DLL is not");
            r.Check(!WindowsApps.IsAppFile(psxExe), "a PlayStation PS-X EXE is not (it belongs to PS1)");
            r.Check(!WindowsApps.IsAppFile(textExe) && !WindowsApps.IsAppFile(dosExe), "a text file or a DOS-only MZ named .exe is not");
            r.Check(WindowsApps.IsAppFile(bat) && WindowsApps.IsAppFile(url) && WindowsApps.IsAppFile(lnk),
                    "batch files, internet shortcuts and shortcuts are apps");
            r.Check(!WindowsApps.IsAppFile(Path.Combine(dir, "mario.sfc")), "a ROM is not");

            r.Line("--- titles");
            r.Check(WindowsApps.TitleFor(notepad) == "Notepad", $"a program is named from its version resource (\"{WindowsApps.TitleFor(notepad)}\")");
            r.Check(WindowsApps.TitleFor(fakeProgram) == "Hollow Knight", $"…or from its file name, tidied (\"{WindowsApps.TitleFor(fakeProgram)}\")");
            string cased = WriteFakeProgram(Path.Combine(dir, "DOOMEternalx64vk.exe"), 1024);
            r.Check(WindowsApps.TitleFor(cased) == "DOOMEternalx64vk", "a file name with its own capitals is kept as it is");
            r.Check(WindowsApps.TitleFor(lnk) == "Notepad", $"a shortcut is named as the user named it, minus \" - Shortcut\" (\"{WindowsApps.TitleFor(lnk)}\")");
            r.Check(WindowsApps.TitleFor(bat) == "run game" && WindowsApps.TitleFor(url) == "Celeste", "batch files and links keep their names");

            r.Line("--- Steam links and lookups");
            r.Check(WindowsApps.TryGetSteamAppId(url, out int appId) && appId == 504230, $"a Steam game link yields its app id ({appId})");
            string nonSteam = WriteUrl(Path.Combine(dir, "Shortcut Game.url"), "steam://rungameid/12345678901234567890");
            r.Check(!WindowsApps.TryGetSteamAppId(web, out _) && !WindowsApps.TryGetSteamAppId(nonSteam, out _),
                    "a web link or a non-Steam game added to Steam yields none");
            r.Check(WindowsApps.LookupPath(@"C:\Games\HK\hollow_knight.exe", "Hollow Knight") == @"C:\Games\HK\Hollow Knight.exe",
                    "artwork lookups search by the library title");
            r.Check(WindowsApps.LibraryHash(fakeProgram) == WindowsApps.LibraryHash(fakeProgram.ToUpperInvariant())
                    && WindowsApps.LibraryHash(fakeProgram) != WindowsApps.LibraryHash(cased),
                    "an app's identity is its location, whatever the letter case");
        }

        // ── Folders ─────────────────────────────────────────────────────────────────────────
        private static void FolderChecks(Report r, string fixtures)
        {
            r.Line("--- which program a folder stands for");
            string games = Path.Combine(fixtures, "Games");
            string hk = WriteFakeProgram(Path.Combine(games, "Hollow Knight", "hollow_knight.exe"), 6000);
            WriteFakeProgram(Path.Combine(games, "Hollow Knight", "UnityCrashHandler64.exe"), 60000);
            WriteFakeProgram(Path.Combine(games, "Hollow Knight", "unins000.exe"), 70000);
            string celeste = WriteFakeProgram(Path.Combine(games, "Celeste", "Celeste.exe"), 5000);
            string deep = WriteFakeProgram(Path.Combine(games, "Deep Game", "bin", "x64", "deepgame.exe"), 5000);
            WriteFakeProgram(Path.Combine(games, "Deep Game", "_CommonRedist", "vcredist_x64.exe"), 90000);
            WriteFakeProgram(Path.Combine(games, "Big Game", "launcher.exe"), 90000);
            string big = WriteFakeProgram(Path.Combine(games, "Big Game", "BigGame.exe"), 3000);
            WriteFakeProgram(Path.Combine(games, "Big Game", "config.exe"), 4000);
            WriteFakeProgram(Path.Combine(games, "Some App", "alpha.exe"), 1000);
            string beta = WriteFakeProgram(Path.Combine(games, "Some App", "beta.exe"), 9000);
            string crash = WriteFakeProgram(Path.Combine(games, "Crash Bandicoot", "CrashBandicootNSaneTrilogy.exe"), 3000);
            WriteFakeProgram(Path.Combine(games, "Crash Bandicoot", "CrashReportClient.exe"), 90000);
            WriteFakeProgram(Path.Combine(games, "_CommonRedist", "dxsetup.exe"), 9000);
            Directory.CreateDirectory(Path.Combine(games, "Empty"));

            var found = WindowsApps.FindAppsInFolder(games).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var expected = new[] { hk, celeste, deep, big, beta, crash }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            r.Check(found.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase),
                    $"a folder of app folders yields one program per app, helpers left out:{Environment.NewLine}        "
                    + string.Join($"{Environment.NewLine}        ", found.Select(p => Path.GetRelativePath(games, p))));
            r.Check(WindowsApps.FindAppsInFolder(Path.Combine(games, "Hollow Knight")).SequenceEqual(new[] { hk }),
                    "an app's own folder yields its program, not its crash handler or uninstaller (even though both are bigger)");
            r.Check(WindowsApps.FindAppsInFolder(Path.Combine(games, "Empty")).Count == 0, "an empty folder yields nothing");

            string shortcuts = Path.Combine(fixtures, "Shortcuts");
            string steamLink = WriteUrl(Path.Combine(shortcuts, "Celeste.url"), "steam://rungameid/504230");
            WriteUrl(Path.Combine(shortcuts, "Visit Website.url"), "https://example.com/");
            WriteUrl(Path.Combine(shortcuts, "Bookmark.url"), "https://example.com/news");
            string epic = WriteUrl(Path.Combine(shortcuts, "Epic Game.url"), "com.epicgames.launcher://apps/fn?action=launch");
            WriteFakeProgram(Path.Combine(shortcuts, "stray.exe"), 1000);
            var links = WindowsApps.FindAppsInFolder(shortcuts).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            r.Check(links.SequenceEqual(new[] { steamLink, epic }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
                    $"a folder of shortcuts yields its launcher links, not web bookmarks ({string.Join(", ", links.Select(Path.GetFileName))})");
        }

        // ── Import ──────────────────────────────────────────────────────────────────────────
        private static async Task ImportChecks(Report r, string fixtures, string notepad, JsonConfigurationService config)
        {
            r.Line("--- import");
            string dir = Path.Combine(fixtures, "import");
            string racer = WriteFakeProgram(Path.Combine(dir, "Arcade Racer", "racer.exe"), 4096);
            string link = WriteUrl(Path.Combine(dir, "Celeste.url"), "steam://rungameid/504230");
            string bat = Path.Combine(dir, "Slow Tool.bat");
            File.WriteAllText(bat, "@echo off\r\nping -n 3 127.0.0.1 >nul\r\nexit /b 0\r\n");
            string lnk = WriteShortcut(Path.Combine(dir, "Notepad - Shortcut.lnk"), notepad);
            string rom = Path.Combine(dir, "Test Cart (USA).sfc");
            File.WriteAllBytes(rom, new byte[4096]);
            string psx = Path.Combine(dir, "homebrew.exe");
            File.WriteAllBytes(psx, Encoding.ASCII.GetBytes("PS-X EXE").Concat(new byte[4088]).ToArray());

            var db = new DatabaseService();
            var importer = new ImportService(db, new CoreManager(config), config);
            List<Game> All() => db.GetAllGames();
            Game? ByPath(string p) => All().FirstOrDefault(g => string.Equals(g.RomPath, p, StringComparison.OrdinalIgnoreCase));

            // Dropped while the Famicom Disk System was selected — the original report.
            bool drained = await ImportAndWait(importer, new[] { racer, link, bat, lnk }, "FDS");
            var apps = new[] { racer, link, bat, lnk }.Select(ByPath).ToList();
            r.Check(drained && apps.All(g => g?.Console == WindowsApps.ConsoleTag),
                    $"programs and shortcuts dropped on another console's nav are filed under Windows ({string.Join(", ", apps.Select(g => g?.Console ?? "missing"))})");
            r.Check(!All().Any(g => g.Console == "FDS"), "…and nothing was filed under the Famicom Disk System");
            var racerGame = ByPath(racer);
            r.Check(racerGame != null && racerGame.Title == "Racer" && racerGame.Manufacturer == WindowsApps.Manufacturer
                    && racerGame.RomHash == WindowsApps.LibraryHash(racer) && racerGame.OriginalSourcePath.Equals(racer, StringComparison.OrdinalIgnoreCase),
                    $"an app is titled, attributed and identified by its location (\"{racerGame?.Title}\", {racerGame?.Manufacturer})");
            r.Check(ByPath(lnk)?.Title == "Notepad" && ByPath(link)?.Title == "Celeste", "shortcuts keep the names they were given");

            drained = await ImportAndWait(importer, new[] { racer }, null);
            r.Check(drained && All().Count(g => string.Equals(g.RomPath, racer, StringComparison.OrdinalIgnoreCase)) == 1,
                    "adding the same app again doesn't duplicate it");

            // Dropped on the Windows nav: a folder of apps and a ROM.
            string games = Path.Combine(fixtures, "Games");
            drained = await ImportAndWait(importer, new[] { games, rom }, WindowsApps.ConsoleTag);
            var fromFolder = WindowsApps.FindAppsInFolder(games);
            r.Check(drained && fromFolder.Count == 6 && fromFolder.All(p => ByPath(p)?.Console == WindowsApps.ConsoleTag),
                    $"a folder dropped on the Windows nav adds the programs it stands for ({fromFolder.Count(p => ByPath(p) != null)} of {fromFolder.Count})");
            r.Check(!All().Any(g => Path.GetFileName(g.RomPath).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                                    || Path.GetFileName(g.RomPath).Contains("CrashHandler", StringComparison.OrdinalIgnoreCase)
                                    || Path.GetFileName(g.RomPath).StartsWith("CrashReport", StringComparison.OrdinalIgnoreCase)),
                    "…and none of their helpers (a game with \"Crash\" in its name is still a game)");
            var cart = All().FirstOrDefault(g => Path.GetFileName(g.RomPath).StartsWith("Test Cart", StringComparison.OrdinalIgnoreCase));
            r.Check(cart != null && cart.Console == "SNES",
                    $"a ROM dropped on the Windows nav is detected as usual, not filed as a program ({cart?.Console ?? "not imported"})");

            // A downloaded PC game still in its archive: not an app yet, and not an Arcade game either.
            string zipped = Path.Combine(dir, "Some PC Game.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(zipped, System.IO.Compression.ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("Some PC Game/game.exe").Open())
                entry.Write(new byte[64]);
            drained = await ImportAndWait(importer, new[] { zipped }, WindowsApps.ConsoleTag);
            r.Check(drained && !All().Any(g => Path.GetFileName(g.RomPath).StartsWith("Some PC Game", StringComparison.OrdinalIgnoreCase))
                    && !All().Any(g => g.Console == "Arcade"),
                    "an archive dropped on the Windows nav is left alone (not imported as an Arcade game)");

            // A PlayStation homebrew .exe is never a Windows app, and still imports under PS1.
            drained = await ImportAndWait(importer, new[] { psx }, null);
            r.Check(drained && !All().Any(g => Path.GetFileName(g.RomPath).Equals("homebrew.exe", StringComparison.OrdinalIgnoreCase)),
                    "a PS-X EXE dropped on All Games is not imported as a program");
            drained = await ImportAndWait(importer, new[] { psx }, "PS1");
            r.Check(drained && All().Any(g => g.Console == "PS1" && Path.GetFileName(g.RomPath).Equals("homebrew.exe", StringComparison.OrdinalIgnoreCase)),
                    "…and dropped on the PlayStation nav it still imports there");

            // Apps stay where they are installed, whatever the copy setting (portable mode copies ROMs).
            var lib = config.GetLibraryConfiguration();
            string libraryDir = Path.Combine(fixtures, "library");
            lib.CopyToLibrary = true;
            lib.LibraryPath = libraryDir;
            config.SetLibraryConfiguration(lib);
            string copyTest = WriteFakeProgram(Path.Combine(dir, "Copy Test", "copytest.exe"), 2048);
            drained = await ImportAndWait(importer, new[] { copyTest }, null);
            r.Check(drained && ByPath(copyTest) != null && !Directory.EnumerateFiles(AppPaths.DataRoot, "copytest*.exe", SearchOption.AllDirectories)
                        .Any(p => !string.Equals(p, copyTest, StringComparison.OrdinalIgnoreCase)),
                    "an app is never copied into the library, even with copying on");
            lib.CopyToLibrary = false;
            lib.LibraryPath = "";
            config.SetLibraryConfiguration(lib);

            string tempDir = Path.Combine(Path.GetTempPath(), "emutastic-selftest-" + Guid.NewGuid().ToString("N"));
            string tempApp = WriteFakeProgram(Path.Combine(tempDir, "TempApp.exe"), 2048);
            drained = await ImportAndWait(importer, new[] { tempApp }, null);
            r.Check(drained && ByPath(tempApp) == null, "a program inside a temporary folder (an open archive) is not added");
            try { Directory.Delete(tempDir, true); } catch { }

            // The generated icon cover arrives in the background, before any network lookup.
            var coverFor = new[] { racer, lnk };
            await WaitUntil(() => coverFor.All(p => ByPath(p)?.CoverArtPath is { Length: > 0 } c && File.Exists(c)), TimeSpan.FromSeconds(30));
            r.Check(coverFor.All(p => ByPath(p)?.CoverArtPath is { Length: > 0 } c && File.Exists(c)),
                    "imported apps get a cover straight away (their icon)");
            var celeste = ByPath(link);
            r.Line($"  [INFO] Steam link cover: {(celeste?.CoverArtPath is { Length: > 0 } cp ? Path.GetFileName(Path.GetDirectoryName(cp)) + "\\" + Path.GetFileName(cp) : "none yet")}, developer: \"{celeste?.Developer}\" (network-dependent, not checked)");
        }

        private static async Task<bool> ImportAndWait(ImportService importer, IEnumerable<string> paths, string? hint)
        {
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnDrained() => drained.TrySetResult();
            importer.ImportQueueDrained += OnDrained;
            try
            {
                importer.ImportFilesAsync(paths, hint);
                return await Task.WhenAny(drained.Task, Task.Delay(TimeSpan.FromSeconds(60))) == drained.Task;
            }
            finally { importer.ImportQueueDrained -= OnDrained; }
        }

        // ── Icon cover ──────────────────────────────────────────────────────────────────────
        private static void IconCoverChecks(Report r, string fixtures, string notepad)
        {
            r.Line("--- icon cover");
            string dest = Path.Combine(fixtures, "covers", "notepad.png");
            bool written = WindowsAppArt.TryWriteIconCover(notepad, dest, out string? why);
            r.Check(written && File.Exists(dest), $"a program's icon is drawn onto a cover{(why != null ? $" ({why})" : "")}");
            if (!written || !File.Exists(dest)) return;

            var frame = BitmapDecoder.Create(new Uri(dest), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            r.Check(frame.PixelWidth == 512 && frame.PixelHeight == 768, $"the cover is 2:3 ({frame.PixelWidth}×{frame.PixelHeight})");

            var bgra = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var px = new byte[512 * 768 * 4];
            bgra.CopyPixels(px, 512 * 4, 0);
            // Compare the middle (where the icon is) with the plain background beside it.
            int distinct = 0;
            for (int y = 250; y < 460; y += 6)
                for (int x = 150; x < 362; x += 6)
                {
                    int i = (y * 512 + x) * 4, j = (y * 512 + 8) * 4;
                    if (Math.Abs(px[i] - px[j]) + Math.Abs(px[i + 1] - px[j + 1]) + Math.Abs(px[i + 2] - px[j + 2]) > 60) distinct++;
                }
            r.Check(distinct > 40, $"the icon is visible on it ({distinct} sampled pixels differ from the background)");

            string plain = WriteFakeProgram(Path.Combine(fixtures, "covers", "noicon.exe"), 2048);
            r.Check(WindowsAppArt.TryWriteIconCover(plain, Path.Combine(fixtures, "covers", "noicon.png"), out why),
                    $"a program without an icon of its own still gets one (the shell's default){(why != null ? $" ({why})" : "")}");

            string lnk = WriteShortcut(Path.Combine(fixtures, "covers", "Notepad.lnk"), notepad);
            r.Check(WindowsAppArt.TryWriteIconCover(lnk, Path.Combine(fixtures, "covers", "lnk.png"), out why),
                    $"a shortcut gets its target's icon{(why != null ? $" ({why})" : "")}");
            string url = WriteUrl(Path.Combine(fixtures, "covers", "Link.url"), "steam://rungameid/504230");
            r.Check(WindowsAppArt.TryWriteIconCover(url, Path.Combine(fixtures, "covers", "url.png"), out why),
                    $"an internet shortcut gets an icon too{(why != null ? $" ({why})" : "")}");
        }

        // ── Launch ──────────────────────────────────────────────────────────────────────────
        private static async Task LaunchChecks(Report r, string fixtures)
        {
            r.Line("--- launch");
            var db = new DatabaseService();
            string bat = Path.Combine(fixtures, "import", "Slow Tool.bat");
            var game = db.GetAllGames().FirstOrDefault(g => string.Equals(g.RomPath, bat, StringComparison.OrdinalIgnoreCase));
            if (game == null) { r.Check(false, "the batch file from the import checks is in the library"); return; }

            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var launched = WindowsAppLauncher.Launch(game, null, db, secs => exited.TrySetResult(secs));
            r.Check(launched is { Tracked: true }, "starting an app hands back its process to follow");
            bool ended = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(45))) == exited.Task;
            int seconds = ended ? exited.Task.Result : -1;
            r.Check(ended && seconds >= 1, $"the app ran and its exit was reported on the UI thread ({seconds} s)");

            var stored = db.GetGameById(game.Id);
            r.Check(stored is { PlayCount: 1 } && stored.LastPlayed != null && stored.TotalPlayTimeSeconds >= 1,
                    $"the launch and the time played are recorded (plays {stored?.PlayCount}, {stored?.TotalPlayTimeSeconds} s)");
            r.Check(game.PlayCount == 1 && game.TotalPlayTimeSeconds == stored?.TotalPlayTimeSeconds,
                    "…and shown on the entry that was launched");
        }

        // ── Sidebar ─────────────────────────────────────────────────────────────────────────
        private static void SidebarChecks(Report r, JsonConfigurationService config)
        {
            r.Line("--- sidebar");
            var window = new MainWindow();   // built, never shown: OnLoaded doesn't run
            var panel = window.ConsoleNavPanel;

            var rows = ReadSidebar(panel);
            string expectedDefault = string.Join(" ", new[] { "Arcade", "Windows" }.Concat(
                ConsoleCatalog.DefaultGroupOrder.SelectMany(g => new[] { $"[{g}]" }.Concat(ConsoleCatalog.InGroup(g).Select(e => e.Tag)))));
            r.Check(string.Join(" ", rows.Select(x => x.Key)) == expectedDefault, $"the default sidebar lists every console under its heading:{Environment.NewLine}        {string.Join(" ", rows.Select(x => x.Key))}");
            r.Check(rows.Where(x => !x.Key.StartsWith("[")).All(x => x.HasIcon && x.Label == ConsoleCatalog.DisplayNameFor(x.Key)),
                    "every row has its icon and label");
            var arcade = panel.Children.OfType<Button>().FirstOrDefault(b => b.CommandParameter as string == "Arcade");
            var header = panel.Children.OfType<Button>().FirstOrDefault(b => b.CommandParameter == null);
            r.Check(arcade?.Content is StackPanel { Orientation: Orientation.Horizontal } sp
                    && sp.Children[0] is Image { Width: 20.0, Height: 20.0 } img && img.Margin == new Thickness(0, 0, 8, 0)
                    && header?.Margin == new Thickness(6, 4, 6, 0) && header.Tag is not string,
                    "rows and headings keep the old markup's shape (20×20 icon, 8 px gap, 6,4,6,0 headings, no string Tag on a heading)");

            var lib = config.GetLibraryConfiguration();
            lib.HiddenGroups = new() { "SEGA" };
            lib.HiddenConsoles = new() { "Arcade", "FDS" };
            lib.ConsoleOrder = new() { "Windows", "Arcade", "SNES", "NES" };
            lib.GroupOrder = new() { "SONY", "NINTENDO" };
            config.SetLibraryConfiguration(lib);
            window.BuildConsoleSidebar();
            string custom = string.Join(" ", ReadSidebar(panel).Select(x => x.Key));
            r.Check(custom.StartsWith("Windows [SONY] PS1 PS2 PS3 PSP [NINTENDO] SNES NES N64 ")
                    && !custom.Contains("Arcade") && !custom.Contains("FDS") && !custom.Contains("[SEGA]") && !custom.Contains("Genesis")
                    && custom.EndsWith("[ATARI] Atari2600 Atari7800 Jaguar [NEC] TG16 TGCD [SNK] NeoGeo NeoCD NGP [OTHER] 3DO CDi ColecoVision Vectrex"),
                    $"a customised layout hides, reorders and regroups exactly as asked:{Environment.NewLine}        {custom}");

            // Fold a heading, rebuild, and it stays folded.
            var nintendo = panel.Children.OfType<Button>().First(b => b.CommandParameter == null
                && (b.Content as StackPanel)?.Children.OfType<TextBlock>().LastOrDefault()?.Text == "NINTENDO");
            nintendo.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.BuildConsoleSidebar();
            var section = ReadSections(panel).FirstOrDefault(s => s.Group == "NINTENDO");
            r.Check(section.Section?.Visibility == Visibility.Collapsed && section.Arrow == "▸",
                    "a folded heading stays folded when the sidebar is rebuilt");

            // Hide consoles with no games: the library now holds Windows, SNES and PS1 games.
            lib.HiddenGroups.Clear();
            lib.HiddenConsoles.Clear();
            lib.ConsoleOrder.Clear();
            lib.GroupOrder.Clear();
            lib.HideEmptyConsoles = true;
            config.SetLibraryConfiguration(lib);
            typeof(MainWindow).GetField("_db", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(window, new DatabaseService());
            window.BuildConsoleSidebar();
            string owned = string.Join(" ", ReadSidebar(panel).Select(x => x.Key));
            r.Check(owned == "Windows [NINTENDO] SNES [SONY] PS1", $"hiding empty consoles leaves only what the library holds: {owned}");
            lib.HideEmptyConsoles = false;
            config.SetLibraryConfiguration(lib);
            window.BuildConsoleSidebar();
            r.Check(ReadSidebar(panel).Count == ExpectedCatalog.Length + ConsoleCatalog.DefaultGroupOrder.Count,
                    "clearing the options restores the full list");
        }

        private sealed record SidebarRow(string Key, string Label, bool HasIcon);

        private static List<SidebarRow> ReadSidebar(StackPanel panel)
        {
            var rows = new List<SidebarRow>();
            void AddConsoleRow(Button b)
            {
                var content = b.Content as StackPanel;
                rows.Add(new SidebarRow(b.CommandParameter as string ?? "?",
                    content?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "",
                    content?.Children.OfType<Image>().FirstOrDefault()?.Source != null));
            }
            foreach (var child in panel.Children)
            {
                if (child is Button { CommandParameter: string } b) AddConsoleRow(b);
                else if (child is Button h && h.Content is StackPanel hs)
                    rows.Add(new SidebarRow($"[{hs.Children.OfType<TextBlock>().LastOrDefault()?.Text}]", "", false));
                else if (child is StackPanel section)
                    foreach (var c in section.Children.OfType<Button>()) AddConsoleRow(c);
            }
            return rows;
        }

        private static List<(string Group, StackPanel? Section, string Arrow)> ReadSections(StackPanel panel)
        {
            var list = new List<(string, StackPanel?, string)>();
            var children = panel.Children.Cast<UIElement>().ToList();
            for (int i = 0; i < children.Count; i++)
                if (children[i] is Button { CommandParameter: null, Content: StackPanel hs })
                {
                    var texts = hs.Children.OfType<TextBlock>().ToList();
                    list.Add((texts.LastOrDefault()?.Text ?? "", i + 1 < children.Count ? children[i + 1] as StackPanel : null,
                              texts.FirstOrDefault()?.Text ?? ""));
                }
            return list;
        }

        // ── Config ──────────────────────────────────────────────────────────────────────────
        private static async Task ConfigRoundTripChecks(Report r, JsonConfigurationService config)
        {
            r.Line("--- layout settings in config.json");
            var lib = config.GetLibraryConfiguration();
            lib.ConsoleOrder = new() { "PS1", "Windows" };
            lib.GroupOrder = new() { "SONY" };
            lib.HiddenConsoles = new() { "NES" };
            lib.HiddenGroups = new() { "SEGA" };
            lib.HideEmptyConsoles = true;
            config.SetLibraryConfiguration(lib);
            await config.SaveAsync();

            string json = File.ReadAllText(Path.Combine(AppPaths.DataRoot, "config.json"));
            r.Check(new[] { "\"consoleOrder\"", "\"groupOrder\"", "\"hiddenConsoles\"", "\"hiddenGroups\"", "\"hideEmptyConsoles\"" }.All(json.Contains),
                    "saved under the same names as the Linux build");

            var reloaded = new JsonConfigurationService(null);
            await reloaded.LoadAsync();
            var back = reloaded.GetLibraryConfiguration();
            r.Check(back.ConsoleOrder.SequenceEqual(lib.ConsoleOrder) && back.GroupOrder.SequenceEqual(lib.GroupOrder)
                    && back.HiddenConsoles.SequenceEqual(lib.HiddenConsoles) && back.HiddenGroups.SequenceEqual(lib.HiddenGroups)
                    && back.HideEmptyConsoles,
                    "and read back unchanged");
            r.Check(!json.Contains("isManualTiming", StringComparison.OrdinalIgnoreCase) && !json.Contains("isPeriodicTiming", StringComparison.OrdinalIgnoreCase),
                    "the Sync Timing helpers aren't written to the file");
        }

        // ── Fixtures ────────────────────────────────────────────────────────────────────────
        /// <summary>A minimal PE image: MZ header, PE signature, x64 file header. Never run.</summary>
        private static string WriteFakeProgram(string path, int size, bool dll = false)
        {
            var bytes = new byte[Math.Max(size, 512)];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), 0x80);
            bytes[0x80] = (byte)'P';
            bytes[0x81] = (byte)'E';
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84), 0x8664);
            ushort characteristics = 0x0002 | 0x0020;
            if (dll) characteristics |= 0x2000;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84 + 18), characteristics);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static string WriteUrl(string path, string url)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"[InternetShortcut]\r\nURL={url}\r\n");
            return path;
        }

        private static string WriteShortcut(string path, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            ((IPersistFile)link).Save(path, false);
            Marshal.ReleaseComObject(link);
            return path;
        }

        private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed < timeout)
                await Task.Delay(200);
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        // ── Output: the report file, plus the parent terminal when there is one ─────────────
        private sealed class Report : IDisposable
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool AttachConsole(uint dwProcessId);
            private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

            private readonly StreamWriter? _file;
            private readonly StreamWriter? _console;
            public int Failures;

            public Report(string path)
            {
                try
                {
                    if (AttachConsole(ATTACH_PARENT_PROCESS))
                        _console = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                }
                catch { }
                try
                {
                    string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    _file = new StreamWriter(path, append: false) { AutoFlush = true };
                }
                catch { }
            }

            public void Line(string text)
            {
                try { _console?.WriteLine(text); } catch { }
                try { _file?.WriteLine(text); } catch { }
            }

            public void Check(bool ok, string what)
            {
                Line($"  [{(ok ? "PASS" : "FAIL")}] {what}");
                if (!ok) Failures++;
            }

            public void Dispose()
            {
                try { _file?.Dispose(); } catch { }
                try { _console?.Dispose(); } catch { }
            }
        }
    }
}
