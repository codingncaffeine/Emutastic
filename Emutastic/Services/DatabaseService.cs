using Microsoft.Data.Sqlite;
using Emutastic.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emutastic.Services
{
    public class DatabaseService
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public DatabaseService()
        {
            try
            {
                string appFolder = AppPaths.GetFolder();
                _dbPath = Path.Combine(appFolder, "library.db");
                _connectionString = $"Data Source={_dbPath}";
                InitializeDatabase();
                System.Diagnostics.Debug.WriteLine($"DatabaseService: Initialized database at {_dbPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DatabaseService: Failed to initialize: {ex.Message}");
                throw;
            }
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var pragma = connection.CreateCommand();
            // WAL lets background services (RA refresher, artwork fetch, save-state
            // sweeper) write to the DB concurrent with UI reads from the library
            // grid without hitting "database is locked". synchronous=NORMAL is
            // the SQLite-recommended pairing — keeps WAL's write-throughput win
            // (default FULL still fsyncs per commit, killing the benefit) while
            // capping the worst-case loss to the last in-flight transaction on
            // a power cut. busy_timeout is the safety net if a write blocks
            // behind a checkpoint.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
            return connection;
        }

        private void InitializeDatabase()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Games (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title           TEXT NOT NULL,
                    Console         TEXT NOT NULL,
                    Manufacturer    TEXT,
                    Year            INTEGER,
                    RomPath         TEXT NOT NULL,
                    OriginalSourcePath TEXT DEFAULT '',
                    RomHash         TEXT,
                    CoverArtPath    TEXT,
                    BackgroundColor TEXT DEFAULT '#1F1F21',
                    AccentColor     TEXT DEFAULT '#E03535',
                    PlayCount       INTEGER DEFAULT 0,
                    SaveCount       INTEGER DEFAULT 0,
                    IsFavorite      INTEGER DEFAULT 0,
                    Rating          INTEGER DEFAULT 0,
                    Collection      TEXT DEFAULT '',
                    LastPlayed      TEXT,
                    DateAdded       TEXT
                );

                CREATE TABLE IF NOT EXISTS SaveStates (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    GameId      INTEGER NOT NULL,
                    Slot        INTEGER NOT NULL,
                    FilePath    TEXT NOT NULL,
                    Screenshot  TEXT,
                    CreatedAt   TEXT,
                    FOREIGN KEY(GameId) REFERENCES Games(Id)
                );

                CREATE TABLE IF NOT EXISTS InputMappings (
                    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    ConsoleName         TEXT NOT NULL,
                    ButtonName          TEXT NOT NULL,
                    InputType           TEXT NOT NULL,
                    KeyCode             INTEGER,
                    ControllerButtonId  INTEGER,
                    DisplayText         TEXT,
                    UNIQUE(ConsoleName, ButtonName)
                );

                CREATE TABLE IF NOT EXISTS Collections (
                    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name      TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    SortOrder INTEGER DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS GameCollections (
                    GameId       INTEGER NOT NULL,
                    CollectionId INTEGER NOT NULL,
                    PRIMARY KEY (GameId, CollectionId),
                    FOREIGN KEY (GameId)       REFERENCES Games(Id)       ON DELETE CASCADE,
                    FOREIGN KEY (CollectionId) REFERENCES Collections(Id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_gamecollections_collection
                    ON GameCollections(CollectionId);";
            cmd.ExecuteNonQuery();

            // Schema migrations — safe to run every launch, silently ignored if column exists.
            try
            {
                var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE Games ADD COLUMN ArtworkAttempts INTEGER DEFAULT 0;";
                migrate.ExecuteNonQuery();
            }
            catch { /* column already exists */ }

            // Indexes for common query patterns — safe to run every launch (IF NOT EXISTS).
            foreach (var ddl in new[]
            {
                "CREATE INDEX IF NOT EXISTS idx_games_console    ON Games(Console);",
                "CREATE INDEX IF NOT EXISTS idx_games_title      ON Games(Title COLLATE NOCASE);",
                "CREATE INDEX IF NOT EXISTS idx_games_last_played ON Games(LastPlayed DESC);",
                "CREATE INDEX IF NOT EXISTS idx_games_date_added  ON Games(DateAdded DESC);"
            })
            {
                var idxCmd = connection.CreateCommand();
                idxCmd.CommandText = ddl;
                idxCmd.ExecuteNonQuery();
            }

            // One-time cleanup: remove Arcade-tagged entries that aren't .zip or .7z files.
            // FBNeo arcade ROMs are always archives; anything else was misidentified on import.
            var cleanCmd = connection.CreateCommand();
            cleanCmd.CommandText = "DELETE FROM Games WHERE Console = 'Arcade' AND RomPath NOT LIKE '%.zip' AND RomPath NOT LIKE '%.7z';";
            cleanCmd.ExecuteNonQuery();

            // One-time path migration: fix paths that still reference the old AppData folder name.
            var pathFixCmd = connection.CreateCommand();
            pathFixCmd.CommandText =
                "UPDATE Games SET CoverArtPath = REPLACE(CoverArtPath, '\\OpenEmuWindows\\', '\\Emutastic\\') " +
                "WHERE CoverArtPath LIKE '%OpenEmuWindows%';" +
                "UPDATE Games SET RomPath = REPLACE(RomPath, '\\OpenEmuWindows\\', '\\Emutastic\\') " +
                "WHERE RomPath LIKE '%OpenEmuWindows%';" +
                "UPDATE SaveStates SET FilePath   = REPLACE(FilePath,   '\\OpenEmuWindows\\', '\\Emutastic\\') " +
                "WHERE FilePath   LIKE '%OpenEmuWindows%';" +
                "UPDATE SaveStates SET Screenshot = REPLACE(Screenshot, '\\OpenEmuWindows\\', '\\Emutastic\\') " +
                "WHERE Screenshot LIKE '%OpenEmuWindows%';";
            pathFixCmd.ExecuteNonQuery();


            TryAddColumn(connection, "Games", "Rating", "INTEGER DEFAULT 0");
            TryAddColumn(connection, "Games", "Collection", "TEXT DEFAULT ''");

            TryAddColumn(connection, "Games", "BoxArt3DPath", "TEXT DEFAULT ''");
            TryAddColumn(connection, "Games", "ScreenScraperArtPath", "TEXT DEFAULT ''");

            TryAddColumn(connection, "Games", "Developer", "TEXT DEFAULT ''");
            TryAddColumn(connection, "Games", "Publisher", "TEXT DEFAULT ''");
            TryAddColumn(connection, "Games", "Genre", "TEXT DEFAULT ''");
            TryAddColumn(connection, "Games", "Description", "TEXT DEFAULT ''");
            TryAddColumn(connection, "Games", "OriginalSourcePath", "TEXT DEFAULT ''");
            TryAddColumn(connection, "Games", "MetadataAttempts",   "INTEGER DEFAULT 0");
            TryAddColumn(connection, "Games", "PreferredCore",      "TEXT DEFAULT ''");

            // RetroAchievements cache. RAGameId is captured at launch from
            // rcheevos's identify-game callback; the *Json columns hold the
            // last fetched API responses; *FetchedAt are unix seconds for TTL
            // checks (24 h for progression, 1 h for user-progress).
            TryAddColumn(connection, "Games", "RAGameId",                 "INTEGER DEFAULT 0");
            TryAddColumn(connection, "Games", "RAProgressionJson",        "TEXT DEFAULT ''");
            TryAddColumn(connection, "Games", "RAProgressionFetchedAt",   "INTEGER DEFAULT 0");
            TryAddColumn(connection, "Games", "RAUserProgressJson",       "TEXT DEFAULT ''");
            TryAddColumn(connection, "Games", "RAUserProgressFetchedAt",  "INTEGER DEFAULT 0");

            // Live in-game progress snapshot (Phase 2). Captured from rcheevos
            // ACHIEVEMENT_PROGRESS_INDICATOR_UPDATE events during play and
            // flushed once on emulator close. Lets the "Coming up" picker show
            // achievements the player is genuinely close to unlocking
            // (e.g. "73% · 3 of 5") rather than community-median proxies.
            TryAddColumn(connection, "Games", "RALiveProgressJson",       "TEXT DEFAULT ''");
            TryAddColumn(connection, "Games", "RALiveProgressFetchedAt",  "INTEGER DEFAULT 0");

            // Speed up the Achievements-tab library cross-ref. The "across
            // every game you own, which ones are RA-tracked" lookup runs on
            // every tab refresh; a partial index over the populated subset
            // (RAGameId > 0) keeps the scan tight on a 500-game library.
            try
            {
                var idx = connection.CreateCommand();
                idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_games_ra_game_id ON Games(RAGameId) WHERE RAGameId > 0;";
                idx.ExecuteNonQuery();
            }
            catch { /* old SQLite without partial-index support — full table scan still works */ }

            // Generic web-API response cache for the Achievements tab. cache_key
            // encodes endpoint + args (e.g. "user_completion_progress:user=Foo:offset=0").
            // owner groups rows for wipe-on-logout / API-key-change ('user:Foo',
            // 'global', 'game:1234'). fetched_at + ttl_seconds drive freshness checks.
            var raCache = connection.CreateCommand();
            raCache.CommandText = @"
                CREATE TABLE IF NOT EXISTS ra_cache (
                    cache_key      TEXT PRIMARY KEY,
                    owner          TEXT NOT NULL DEFAULT 'global',
                    payload_json   TEXT NOT NULL DEFAULT '',
                    fetched_at     INTEGER NOT NULL DEFAULT 0,
                    ttl_seconds    INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_ra_cache_owner ON ra_cache(owner);";
            raCache.ExecuteNonQuery();

            // Heatmap daily-aggregate persistence. GetAchievementsEarnedBetween
            // for a 90-day range returns 1000+ per-achievement rows; the heatmap
            // UI only needs (date, count) so we derive the aggregate once and
            // keep it forever. Composite PK lets us upsert today's row while
            // past days stay frozen.
            var raHeat = connection.CreateCommand();
            raHeat.CommandText = @"
                CREATE TABLE IF NOT EXISTS ra_heatmap_daily (
                    user   TEXT NOT NULL,
                    date   TEXT NOT NULL,
                    count  INTEGER NOT NULL,
                    PRIMARY KEY(user, date)
                );";
            raHeat.ExecuteNonQuery();

            // One-shot migration: games whose RomPath lives in a mame2003-plus folder
            // should route to that core regardless of DAT membership. Earlier import
            // logic preferred FBNeo whenever FBNeo's DAT listed the game, even when
            // the actual ROM file matched MAME 2003-Plus's romset (different CRCs).
            // Symptom: launches into FBNeo and fails to load. Fix the persisted state
            // here so the user doesn't have to delete + re-import every affected game.
            try
            {
                var routeFix = connection.CreateCommand();
                routeFix.CommandText = @"
                    UPDATE Games
                    SET PreferredCore = 'mame2003_plus_libretro.dll'
                    WHERE Console = 'Arcade'
                      AND (LOWER(RomPath) LIKE '%mame2003plus%'
                           OR LOWER(RomPath) LIKE '%mame2003-plus%')
                      AND PreferredCore <> 'mame2003_plus_libretro.dll';";
                int rowsFixed = routeFix.ExecuteNonQuery();
                if (rowsFixed > 0)
                    System.Diagnostics.Trace.WriteLine(
                        $"[DatabaseService] Re-routed {rowsFixed} arcade game(s) to mame2003-plus based on folder path.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DatabaseService] PreferredCore migration failed: {ex.Message}");
            }

            TryAddColumn(connection, "SaveStates", "Name",        "TEXT NOT NULL DEFAULT ''");
            TryAddColumn(connection, "SaveStates", "GameTitle",   "TEXT NOT NULL DEFAULT ''");
            TryAddColumn(connection, "SaveStates", "ConsoleName", "TEXT NOT NULL DEFAULT ''");
            TryAddColumn(connection, "SaveStates", "CoreName",    "TEXT NOT NULL DEFAULT ''");
            TryAddColumn(connection, "SaveStates", "RomHash",     "TEXT NOT NULL DEFAULT ''");

            // One-time migration: move old Collection column data into the new join table.
            MigrateCollectionsToJoinTable(connection);

            // One-time migration: move artwork/snaps from flat folders into console subfolders.
            MigrateArtworkToConsoleFolders(connection);

            // One-time migration: deduplicate games with the same RomHash (from ~ alternate title ROMs).
            DeduplicateByRomHash(connection);

            // One-time migration: clear BoxArt3DPath entries that are actually 2D art (_2d suffix).
            // Old FetchBoxArt2DAsync stored 2D images in the BoxArt3D folder; these aren't 3D box art
            // and prevent the real 3D fetch from running.
            CleanupBogus3DPaths(connection);

            // Portable mode v2 (v1.3.3): rewrite absolute paths that live under DataRoot
            // as relative so the DB survives drive-letter changes (USB on PC1=E:, PC2=F:).
            // Idempotent — paths already relative or outside DataRoot are skipped.
            RelativizePathsUnderDataRoot(connection);
        }

        private void MigrateCollectionsToJoinTable(SqliteConnection connection)
        {
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT Id, Collection FROM Games WHERE Collection != '' AND Collection IS NOT NULL;";
            using var reader = checkCmd.ExecuteReader();
            var toMigrate = new List<(int gameId, string collection)>();
            while (reader.Read())
                toMigrate.Add((reader.GetInt32(0), reader.GetString(1)));
            reader.Close();

            if (toMigrate.Count == 0) return;

            using var tx = connection.BeginTransaction();
            foreach (var (gameId, collName) in toMigrate)
            {
                // Find or create collection
                var findCmd = connection.CreateCommand();
                findCmd.CommandText = "SELECT Id FROM Collections WHERE Name = $name;";
                findCmd.Parameters.AddWithValue("$name", collName);
                var existing = findCmd.ExecuteScalar();
                int collectionId;
                if (existing != null)
                {
                    collectionId = Convert.ToInt32(existing);
                }
                else
                {
                    var insCmd = connection.CreateCommand();
                    insCmd.CommandText = "INSERT INTO Collections (Name) VALUES ($name);";
                    insCmd.Parameters.AddWithValue("$name", collName);
                    insCmd.ExecuteNonQuery();
                    var idCmd = connection.CreateCommand();
                    idCmd.CommandText = "SELECT last_insert_rowid();";
                    collectionId = (int)(long)idCmd.ExecuteScalar()!;
                }

                // Insert join row
                var joinCmd = connection.CreateCommand();
                joinCmd.CommandText = "INSERT OR IGNORE INTO GameCollections (GameId, CollectionId) VALUES ($gid, $cid);";
                joinCmd.Parameters.AddWithValue("$gid", gameId);
                joinCmd.Parameters.AddWithValue("$cid", collectionId);
                joinCmd.ExecuteNonQuery();

                // Clear old column so migration doesn't re-run
                var clearCmd = connection.CreateCommand();
                clearCmd.CommandText = "UPDATE Games SET Collection = '' WHERE Id = $id;";
                clearCmd.Parameters.AddWithValue("$id", gameId);
                clearCmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        /// <summary>
        /// One-time migration: moves artwork, 3D box art, screenshots, and snap files
        /// from flat folders into per-console subfolders and updates DB paths.
        /// Idempotent — files already in a subfolder are skipped.
        /// </summary>
        private void MigrateArtworkToConsoleFolders(SqliteConnection connection)
        {
            try
            {
                // ── 1. Artwork (CoverArtPath), BoxArt3D, and ss2d (ScreenScraperArtPath) — DB-tracked ──
                string artworkRoot  = AppPaths.GetFolder("Artwork");
                string boxArt3DRoot = AppPaths.GetFolder("BoxArt3D");
                string ss2dRoot     = AppPaths.GetFolder("ss2d");

                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Id, Console, CoverArtPath, BoxArt3DPath, ScreenScraperArtPath FROM Games;";
                using var reader = cmd.ExecuteReader();

                var updates = new List<(int id, string? newCover, string? new3D, string? newSS)>();
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    string console = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    // Resolve to absolute so MoveFileToConsoleSubfolder can locate the file on disk.
                    string coverPath = AppPaths.FromStoragePath(reader.IsDBNull(2) ? "" : reader.GetString(2));
                    string art3DPath = AppPaths.FromStoragePath(reader.IsDBNull(3) ? "" : reader.GetString(3));
                    string ssArtPath = AppPaths.FromStoragePath(reader.IsDBNull(4) ? "" : reader.GetString(4));

                    if (string.IsNullOrWhiteSpace(console)) continue;

                    string? newCover = MoveFileToConsoleSubfolder(coverPath, artworkRoot, console);
                    string? new3D   = MoveFileToConsoleSubfolder(art3DPath, boxArt3DRoot, console);
                    string? newSS   = MoveFileToConsoleSubfolder(ssArtPath, ss2dRoot, console);

                    if (newCover != null || new3D != null || newSS != null)
                        updates.Add((id, newCover, new3D, newSS));
                }
                reader.Close();

                if (updates.Count > 0)
                {
                    using var tx = connection.BeginTransaction();
                    foreach (var (id, newCover, new3D, newSS) in updates)
                    {
                        if (newCover != null)
                        {
                            var u = connection.CreateCommand();
                            u.CommandText = "UPDATE Games SET CoverArtPath = $path WHERE Id = $id;";
                            u.Parameters.AddWithValue("$path", AppPaths.ToStoragePath(newCover));
                            u.Parameters.AddWithValue("$id", id);
                            u.ExecuteNonQuery();
                        }
                        if (new3D != null)
                        {
                            var u = connection.CreateCommand();
                            u.CommandText = "UPDATE Games SET BoxArt3DPath = $path WHERE Id = $id;";
                            u.Parameters.AddWithValue("$path", AppPaths.ToStoragePath(new3D));
                            u.Parameters.AddWithValue("$id", id);
                            u.ExecuteNonQuery();
                        }
                        if (newSS != null)
                        {
                            var u = connection.CreateCommand();
                            u.CommandText = "UPDATE Games SET ScreenScraperArtPath = $path WHERE Id = $id;";
                            u.Parameters.AddWithValue("$path", AppPaths.ToStoragePath(newSS));
                            u.Parameters.AddWithValue("$id", id);
                            u.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                    System.Diagnostics.Debug.WriteLine(
                        $"[Migration] Moved {updates.Count} artwork paths to console subfolders");
                }

                // ── 2. Sweep remaining flat files by matching filename (hash) to games ──
                SweepOrphanedArtworkFiles(connection, artworkRoot, "Artwork", "CoverArtPath");
                SweepOrphanedArtworkFiles(connection, boxArt3DRoot, "BoxArt3D", "BoxArt3DPath");
                SweepOrphanedArtworkFiles(connection, ss2dRoot, "ss2d", "ScreenScraperArtPath");

                // ── 3. Snaps (no DB column — file-only migration) ──
                MigrateSnapFiles(connection);

                // ── 4. Screenshots (no DB column — file-only migration) ──
                MigrateScreenshotFiles();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Migration] ArtworkToConsoleFolders error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sweeps any remaining files in the flat root folder that weren't caught by the
        /// DB-path migration. Matches filenames (hash-based) to games and moves them into
        /// console subfolders. Also repairs the DB path if the column is empty.
        /// </summary>
        private void SweepOrphanedArtworkFiles(SqliteConnection connection, string rootFolder,
            string folderName, string dbColumn)
        {
            var flatFiles = System.IO.Directory.EnumerateFiles(rootFolder, "*.*").ToList();
            if (flatFiles.Count == 0) return;

            // Build hash→(id, console) lookup — the filename stem is typically the romHash
            var hashLookup = new Dictionary<string, (int id, string console)>(StringComparer.OrdinalIgnoreCase);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, RomHash, Console FROM Games WHERE RomHash != '' AND Console != '';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string hash = reader.GetString(1);
                string console = reader.GetString(2);
                hashLookup.TryAdd(hash, (reader.GetInt32(0), console));
            }
            reader.Close();

            int moved = 0;
            using var tx = connection.BeginTransaction();
            foreach (string file in flatFiles)
            {
                string stem = System.IO.Path.GetFileNameWithoutExtension(file);
                // Strip suffixes like "_custom", "_2d", or "_XXXXXXXX" (url hash)
                string baseHash = stem.Contains('_') ? stem[..stem.IndexOf('_')] : stem;

                if (!hashLookup.TryGetValue(baseHash, out var match)) continue;

                string destFolder = AppPaths.GetFolder(folderName, match.console);
                string destPath = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(file));
                try
                {
                    if (!System.IO.File.Exists(destPath))
                        System.IO.File.Move(file, destPath);
                    else
                        System.IO.File.Delete(file); // duplicate — subfolder copy wins

                    // If the DB column for this game is empty, fill it in
                    var checkCmd = connection.CreateCommand();
                    checkCmd.CommandText = $"SELECT {dbColumn} FROM Games WHERE Id = $id;";
                    checkCmd.Parameters.AddWithValue("$id", match.id);
                    string? existingPath = checkCmd.ExecuteScalar() as string;
                    if (string.IsNullOrWhiteSpace(existingPath))
                    {
                        var upd = connection.CreateCommand();
                        upd.CommandText = $"UPDATE Games SET {dbColumn} = $path WHERE Id = $id;";
                        upd.Parameters.AddWithValue("$path", AppPaths.ToStoragePath(destPath));
                        upd.Parameters.AddWithValue("$id", match.id);
                        upd.ExecuteNonQuery();
                    }
                    moved++;
                }
                catch { }
            }
            tx.Commit();
            if (moved > 0)
                System.Diagnostics.Debug.WriteLine(
                    $"[Migration] Swept {moved} orphaned files from {folderName}/ into console subfolders");
        }

        /// <summary>
        /// If the file is in the flat root folder (not already in a console subfolder),
        /// moves it to root/console/ and returns the new path. Otherwise returns null.
        /// </summary>
        private static string? MoveFileToConsoleSubfolder(string filePath, string rootFolder, string console)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                return null;

            string? fileDir = System.IO.Path.GetDirectoryName(filePath);
            if (fileDir == null) return null;

            // Normalize for comparison
            string normalRoot = System.IO.Path.GetFullPath(rootFolder).TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            string normalDir = System.IO.Path.GetFullPath(fileDir).TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

            // Only move if file is directly in the root (not already in a subfolder)
            if (!string.Equals(normalDir, normalRoot, StringComparison.OrdinalIgnoreCase))
                return null;

            string destFolder = AppPaths.GetFolder(
                System.IO.Path.GetFileName(rootFolder), console);
            string destPath = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(filePath));

            try
            {
                System.IO.File.Move(filePath, destPath, overwrite: false);
                return destPath;
            }
            catch
            {
                // If the destination already exists, just update the path
                if (System.IO.File.Exists(destPath)) return destPath;
                return null;
            }
        }

        /// <summary>
        /// Moves snap .mp4 files from flat Snaps/ to Snaps/{Console}/ by matching
        /// the filename (romHash) to games in the database.
        /// </summary>
        private void MigrateSnapFiles(SqliteConnection connection)
        {
            string snapsRoot = AppPaths.GetFolder("Snaps");
            var flatFiles = System.IO.Directory.EnumerateFiles(snapsRoot, "*.mp4").ToList();
            if (flatFiles.Count == 0) return;

            // Build hash→console lookup from games
            var hashToConsole = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT RomHash, Console FROM Games WHERE RomHash != '' AND Console != '';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string hash = reader.GetString(0);
                string console = reader.GetString(1);
                hashToConsole.TryAdd(hash, console);
            }
            reader.Close();

            int moved = 0;
            foreach (string file in flatFiles)
            {
                string key = System.IO.Path.GetFileNameWithoutExtension(file);
                if (!hashToConsole.TryGetValue(key, out string? console)) continue;

                string destFolder = AppPaths.GetFolder("Snaps", console);
                string destPath = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(file));
                try
                {
                    System.IO.File.Move(file, destPath, overwrite: false);
                    moved++;
                }
                catch { if (!System.IO.File.Exists(destPath)) continue; }
            }
            if (moved > 0)
                System.Diagnostics.Debug.WriteLine($"[Migration] Moved {moved} snap files to console subfolders");
        }

        /// <summary>
        /// Moves screenshot .png files from flat Screenshots/ to Screenshots/{Console}/
        /// by parsing the console name from the filename.
        /// </summary>
        private static void MigrateScreenshotFiles()
        {
            string screenshotsRoot = AppPaths.GetFolder("Screenshots");
            var flatFiles = System.IO.Directory.EnumerateFiles(screenshotsRoot, "*.png").ToList();
            if (flatFiles.Count == 0) return;

            int moved = 0;
            foreach (string file in flatFiles)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(file);
                // Expected format: "yyyyMMdd_HHmmss Title (Console)"
                int parenOpen  = name.LastIndexOf('(');
                int parenClose = name.LastIndexOf(')');
                if (parenOpen < 0 || parenClose <= parenOpen) continue;

                string console = name[(parenOpen + 1)..parenClose].Trim();
                if (string.IsNullOrWhiteSpace(console)) continue;

                string destFolder = AppPaths.GetFolder("Screenshots", console);
                string destPath = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(file));
                try
                {
                    System.IO.File.Move(file, destPath, overwrite: false);
                    moved++;
                }
                catch { }
            }
            if (moved > 0)
                System.Diagnostics.Debug.WriteLine($"[Migration] Moved {moved} screenshot files to console subfolders");
        }

        /// <summary>
        /// One-time migration: removes duplicate game entries that share the same RomHash
        /// AND have identical titles. This safely handles No-Intro ~ alternate title ROMs
        /// (e.g., "Chaotix ~ Knuckles' Chaotix") without deleting genuinely different games
        /// that happen to share a hash (e.g., PS1 hash collisions from multi-disc CHDs).
        /// </summary>
        private void DeduplicateByRomHash(SqliteConnection connection)
        {
            // Find groups where the same hash+console+title appears more than once.
            // This only catches exact-title duplicates — safe by design.
            var findCmd = connection.CreateCommand();
            findCmd.CommandText = @"
                SELECT RomHash, Console, Title FROM Games
                WHERE RomHash != '' AND RomHash IS NOT NULL
                GROUP BY RomHash, Console, Title
                HAVING COUNT(*) > 1;";

            var dupeGroups = new List<(string hash, string console, string title)>();
            using (var reader = findCmd.ExecuteReader())
                while (reader.Read())
                    dupeGroups.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));

            if (dupeGroups.Count == 0) return;

            int removed = 0;
            using var tx = connection.BeginTransaction();

            foreach (var (hash, console, title) in dupeGroups)
            {
                var listCmd = connection.CreateCommand();
                listCmd.CommandText = @"
                    SELECT Id FROM Games WHERE RomHash = $hash AND Console = $console AND Title = $title
                    ORDER BY
                        CASE WHEN CoverArtPath IS NOT NULL AND CoverArtPath != '' THEN 0 ELSE 1 END,
                        PlayCount DESC,
                        IsFavorite DESC,
                        Id ASC;";
                listCmd.Parameters.AddWithValue("$hash", hash);
                listCmd.Parameters.AddWithValue("$console", console);
                listCmd.Parameters.AddWithValue("$title", title);

                var ids = new List<int>();
                using (var reader = listCmd.ExecuteReader())
                    while (reader.Read())
                        ids.Add(reader.GetInt32(0));

                if (ids.Count <= 1) continue;

                for (int i = 1; i < ids.Count; i++)
                {
                    var delCmd = connection.CreateCommand();
                    delCmd.CommandText = "DELETE FROM Games WHERE Id = $id;";
                    delCmd.Parameters.AddWithValue("$id", ids[i]);
                    delCmd.ExecuteNonQuery();
                    removed++;
                }
            }

            tx.Commit();

            if (removed > 0)
                System.Diagnostics.Debug.WriteLine($"[Migration] Removed {removed} exact-title duplicate game entries");
        }

        /// <summary>
        /// Clears BoxArt3DPath entries that contain "_2d" — these are 2D images that were
        /// incorrectly stored in the BoxArt3D folder by the old FetchBoxArt2DAsync.
        /// Moves them to ScreenScraperArtPath (if empty) so the 2D art isn't lost.
        /// </summary>
        private static void CleanupBogus3DPaths(SqliteConnection connection)
        {
            var cmd = connection.CreateCommand();

            // First, preserve the 2D art by moving to ScreenScraperArtPath where it's empty
            cmd.CommandText = @"UPDATE Games
                SET ScreenScraperArtPath = BoxArt3DPath
                WHERE BoxArt3DPath LIKE '%\_2d.%' ESCAPE '\'
                AND (ScreenScraperArtPath IS NULL OR ScreenScraperArtPath = '');";
            int moved = cmd.ExecuteNonQuery();

            // Clear the bogus 3D paths
            cmd.CommandText = @"UPDATE Games
                SET BoxArt3DPath = ''
                WHERE BoxArt3DPath LIKE '%\_2d.%' ESCAPE '\';";
            int cleared = cmd.ExecuteNonQuery();

            if (cleared > 0)
                System.Diagnostics.Debug.WriteLine(
                    $"[Migration] Cleared {cleared} bogus _2d entries from BoxArt3DPath ({moved} preserved as ScreenScraperArtPath)");
        }

        /// <summary>
        /// Portable mode v2 (v1.3.3): rewrite absolute paths under the current DataRoot
        /// as relative ("Roms\NES\zelda.smc") so the DB survives drive-letter changes
        /// when the data folder is moved between PCs. Covers Games (RomPath/CoverArtPath/
        /// BoxArt3DPath/ScreenScraperArtPath) and SaveStates (FilePath/Screenshot).
        /// Idempotent — paths already relative or outside DataRoot pass through unchanged.
        /// Per-row try/catch ensures one malformed path can't poison the whole migration.
        /// </summary>
        private static void RelativizePathsUnderDataRoot(SqliteConnection connection)
        {
            int gameRows = RelativizeGameRows(connection);
            int saveRows = RelativizeSaveStateRows(connection);

            if (gameRows > 0 || saveRows > 0)
                System.Diagnostics.Trace.WriteLine($"[Migration] Relativized {gameRows} game rows + {saveRows} save state rows under DataRoot");
        }

        private static int RelativizeGameRows(SqliteConnection connection)
        {
            int rewrote = 0, failures = 0;
            try
            {
                using var tx = connection.BeginTransaction();
                var rows = new List<(int id, string? rom, string? cover, string? bx3d, string? ss)>();
                var read = connection.CreateCommand();
                read.Transaction = tx;
                read.CommandText = "SELECT Id, RomPath, CoverArtPath, BoxArt3DPath, ScreenScraperArtPath FROM Games;";
                using (var reader = read.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add((
                            reader.GetInt32(0),
                            reader.IsDBNull(1) ? null : reader.GetString(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4)));
                    }
                }

                foreach (var row in rows)
                {
                    try
                    {
                        string? newRom   = row.rom   != null ? AppPaths.ToStoragePath(row.rom)   : null;
                        string? newCover = row.cover != null ? AppPaths.ToStoragePath(row.cover) : null;
                        string? new3D    = row.bx3d  != null ? AppPaths.ToStoragePath(row.bx3d)  : null;
                        string? newSS    = row.ss    != null ? AppPaths.ToStoragePath(row.ss)    : null;

                        bool changed =
                            !string.Equals(newRom, row.rom, StringComparison.Ordinal) ||
                            !string.Equals(newCover, row.cover, StringComparison.Ordinal) ||
                            !string.Equals(new3D, row.bx3d, StringComparison.Ordinal) ||
                            !string.Equals(newSS, row.ss, StringComparison.Ordinal);

                        if (!changed) continue;

                        var u = connection.CreateCommand();
                        u.Transaction = tx;
                        u.CommandText = @"UPDATE Games
                            SET RomPath = $rom,
                                CoverArtPath = $cover,
                                BoxArt3DPath = $bx3d,
                                ScreenScraperArtPath = $ss
                            WHERE Id = $id;";
                        u.Parameters.AddWithValue("$rom",   newRom   ?? "");
                        u.Parameters.AddWithValue("$cover", newCover ?? "");
                        u.Parameters.AddWithValue("$bx3d",  new3D    ?? "");
                        u.Parameters.AddWithValue("$ss",    newSS    ?? "");
                        u.Parameters.AddWithValue("$id",    row.id);
                        u.ExecuteNonQuery();
                        rewrote++;
                    }
                    catch (Exception rowEx)
                    {
                        // One bad row mustn't poison the rest of the migration.
                        failures++;
                        System.Diagnostics.Trace.WriteLine($"[Migration] Game row {row.id} skipped: {rowEx.Message}");
                    }
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Migration] RelativizeGameRows failed: {ex.Message}");
                return 0;
            }
            if (failures > 0)
                System.Diagnostics.Trace.WriteLine($"[Migration] {failures} game row(s) skipped due to errors");
            return rewrote;
        }

        private static int RelativizeSaveStateRows(SqliteConnection connection)
        {
            int rewrote = 0, failures = 0;
            try
            {
                using var tx = connection.BeginTransaction();
                var rows = new List<(int id, string? path, string? snap)>();
                var read = connection.CreateCommand();
                read.Transaction = tx;
                read.CommandText = "SELECT Id, FilePath, Screenshot FROM SaveStates;";
                using (var reader = read.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add((
                            reader.GetInt32(0),
                            reader.IsDBNull(1) ? null : reader.GetString(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2)));
                    }
                }

                foreach (var row in rows)
                {
                    try
                    {
                        string? newPath = row.path != null ? AppPaths.ToStoragePath(row.path) : null;
                        string? newSnap = row.snap != null ? AppPaths.ToStoragePath(row.snap) : null;

                        bool changed =
                            !string.Equals(newPath, row.path, StringComparison.Ordinal) ||
                            !string.Equals(newSnap, row.snap, StringComparison.Ordinal);
                        if (!changed) continue;

                        var u = connection.CreateCommand();
                        u.Transaction = tx;
                        u.CommandText = "UPDATE SaveStates SET FilePath = $path, Screenshot = $snap WHERE Id = $id;";
                        u.Parameters.AddWithValue("$path", newPath ?? "");
                        u.Parameters.AddWithValue("$snap", newSnap ?? "");
                        u.Parameters.AddWithValue("$id",   row.id);
                        u.ExecuteNonQuery();
                        rewrote++;
                    }
                    catch (Exception rowEx)
                    {
                        failures++;
                        System.Diagnostics.Trace.WriteLine($"[Migration] SaveState row {row.id} skipped: {rowEx.Message}");
                    }
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Migration] RelativizeSaveStateRows failed: {ex.Message}");
                return 0;
            }
            if (failures > 0)
                System.Diagnostics.Trace.WriteLine($"[Migration] {failures} save state row(s) skipped due to errors");
            return rewrote;
        }

        /// <summary>
        /// Returns the ID of an existing game with the given RomHash and Console, or null if none.
        /// Used during import to prevent creating duplicates for ~ alternate title ROMs.
        /// </summary>
        public int? GetExistingGameIdByHash(string hash, string console)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id FROM Games WHERE RomHash = $hash AND Console = $console LIMIT 1;";
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$console", console);
            var result = cmd.ExecuteScalar();
            return result != null ? Convert.ToInt32(result) : null;
        }

        private void TryAddColumn(SqliteConnection connection, string table, string column, string definition)
        {
            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $col;";
            checkCmd.Parameters.AddWithValue("$col", column);
            long exists = (long)checkCmd.ExecuteScalar()!;
            if (exists == 0)
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
                cmd.ExecuteNonQuery();
            }
        }

        public void InsertGame(Game game)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO Games
                    (Title, Console, Manufacturer, Year, RomPath, OriginalSourcePath, RomHash,
                     CoverArtPath, BoxArt3DPath, ScreenScraperArtPath,
                     BackgroundColor, AccentColor, Rating, Collection, DateAdded,
                     Developer, Publisher, Genre, Description, PreferredCore)
                VALUES
                    ($title, $console, $manufacturer, $year, $romPath, $origSourcePath, $romHash,
                     $coverArt, $boxArt3D, $ssArt,
                     $bgColor, $accentColor, 0, '', $dateAdded,
                     $developer, $publisher, $genre, $description, $preferredCore);";

            cmd.Parameters.AddWithValue("$title", game.Title);
            cmd.Parameters.AddWithValue("$console", game.Console);
            cmd.Parameters.AddWithValue("$manufacturer", game.Manufacturer);
            cmd.Parameters.AddWithValue("$year", game.Year);
            // All filesystem paths are relativized against DataRoot before storage so the DB
            // is portable across drive-letter changes (USB on PC1=E:, PC2=F:). Paths outside
            // DataRoot pass through unchanged.
            cmd.Parameters.AddWithValue("$romPath", AppPaths.ToStoragePath(game.RomPath));
            cmd.Parameters.AddWithValue("$origSourcePath", AppPaths.ToStoragePath(game.OriginalSourcePath ?? ""));
            cmd.Parameters.AddWithValue("$romHash", game.RomHash ?? "");
            cmd.Parameters.AddWithValue("$coverArt", AppPaths.ToStoragePath(game.CoverArtPath ?? ""));
            cmd.Parameters.AddWithValue("$boxArt3D", AppPaths.ToStoragePath(game.BoxArt3DPath ?? ""));
            cmd.Parameters.AddWithValue("$ssArt", AppPaths.ToStoragePath(game.ScreenScraperArtPath ?? ""));
            cmd.Parameters.AddWithValue("$bgColor", game.BackgroundColor);
            cmd.Parameters.AddWithValue("$accentColor", game.AccentColor);
            cmd.Parameters.AddWithValue("$dateAdded", DateTime.Now.ToString("o"));
            cmd.Parameters.AddWithValue("$developer", game.Developer ?? "");
            cmd.Parameters.AddWithValue("$publisher", game.Publisher ?? "");
            cmd.Parameters.AddWithValue("$genre", game.Genre ?? "");
            cmd.Parameters.AddWithValue("$description", game.Description ?? "");
            cmd.Parameters.AddWithValue("$preferredCore", game.PreferredCore ?? "");
            cmd.ExecuteNonQuery();

            var idCmd = connection.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid();";
            game.Id = (int)(long)idCmd.ExecuteScalar()!;
        }

        /// <summary>
        /// Persists the RA numeric game ID for a single game. Called once at
        /// game launch after rcheevos identifies the ROM, so subsequent Web
        /// API fetches (time-to-beat, achievement list, per-user progress)
        /// can skip the hash-resolve roundtrip.
        ///
        /// THREAD-AFFINITY: Call only from the emulator startup path or the
        /// UI thread. Never call from rcheevos event delegates
        /// (AchievementTriggered / GameCompleted / etc.) — those fire on the
        /// per-frame emu thread and a synchronous SQLite write there causes
        /// audio crackle and frame stalls.
        /// </summary>
        public void UpdateRAGameId(int gameId, int raGameId)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET RAGameId = $raId WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$raId", raGameId);
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Persists the cached GetGameProgression JSON response with a fetch
        /// timestamp (unix seconds). Pass empty/0 to invalidate.
        ///
        /// THREAD-AFFINITY: Call from background fetch tasks or the UI thread,
        /// never from rcheevos event delegates — see UpdateRAGameId comment.
        /// </summary>
        public void UpdateRAProgression(int gameId, string json, long fetchedAt)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET RAProgressionJson = $json, RAProgressionFetchedAt = $ts WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$json", json ?? "");
            cmd.Parameters.AddWithValue("$ts", fetchedAt);
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Persists the cached GetGameInfoAndUserProgress JSON response with
        /// a fetch timestamp (unix seconds). Pass empty/0 to invalidate so
        /// the next detail-card open refetches.
        ///
        /// THREAD-AFFINITY: Call from background fetch tasks or the UI thread,
        /// never from rcheevos event delegates — see UpdateRAGameId comment.
        /// </summary>
        public void UpdateRAUserProgress(int gameId, string json, long fetchedAt)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET RAUserProgressJson = $json, RAUserProgressFetchedAt = $ts WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$json", json ?? "");
            cmd.Parameters.AddWithValue("$ts", fetchedAt);
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Persists the live in-game progress snapshot collected during play.
        /// Called once at emulator close (on a background Task, not the emu
        /// thread) so the next detail-card open can read "you were 73% of
        /// the way to X last session" from disk.
        ///
        /// THREAD-AFFINITY: Call from a Task-pool thread launched at game
        /// exit, never from inside an rcheevos event callback.
        /// </summary>
        public void UpdateRALiveProgress(int gameId, string json, long fetchedAt)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET RALiveProgressJson = $json, RALiveProgressFetchedAt = $ts WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$json", json ?? "");
            cmd.Parameters.AddWithValue("$ts", fetchedAt);
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        // ── ra_cache CRUD ────────────────────────────────────────────────────
        // Generic web-API cache used by the Achievements tab. Access wrapped
        // through a single TTL-checking helper so UI code never touches the
        // raw rows. All methods are background-thread safe per the WAL setup
        // in OpenConnection.

        public sealed record RaCacheRow(string Payload, long FetchedAt, long TtlSeconds);

        /// <summary>
        /// Returns the cached payload and freshness data for a key, or null
        /// if no row exists. The caller decides whether the row is fresh
        /// based on fetched_at + ttl_seconds.
        /// </summary>
        public RaCacheRow? GetRaCache(string cacheKey)
        {
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT payload_json, fetched_at, ttl_seconds FROM ra_cache WHERE cache_key = $k;";
            cmd.Parameters.AddWithValue("$k", cacheKey);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new RaCacheRow(
                Payload:     reader.IsDBNull(0) ? "" : reader.GetString(0),
                FetchedAt:   reader.IsDBNull(1) ? 0L : reader.GetInt64(1),
                TtlSeconds:  reader.IsDBNull(2) ? 0L : reader.GetInt64(2));
        }

        /// <summary>
        /// Upserts a cached row. Owner groups per-user payloads for
        /// wipe-on-logout (e.g. <c>"user:foo"</c>, <c>"global"</c>,
        /// <c>"game:1234"</c>).
        /// </summary>
        public void SetRaCache(string cacheKey, string owner, string payload, long fetchedAt, long ttlSeconds)
        {
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ra_cache (cache_key, owner, payload_json, fetched_at, ttl_seconds)
                VALUES ($k, $o, $p, $f, $t)
                ON CONFLICT(cache_key) DO UPDATE SET
                    owner=excluded.owner,
                    payload_json=excluded.payload_json,
                    fetched_at=excluded.fetched_at,
                    ttl_seconds=excluded.ttl_seconds;";
            cmd.Parameters.AddWithValue("$k", cacheKey);
            cmd.Parameters.AddWithValue("$o", owner ?? "global");
            cmd.Parameters.AddWithValue("$p", payload ?? "");
            cmd.Parameters.AddWithValue("$f", fetchedAt);
            cmd.Parameters.AddWithValue("$t", ttlSeconds);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Marks every row with the given owner as stale (fetched_at=0)
        /// without deleting the payloads. PeekCached calls continue to
        /// serve the cached JSON for instant cold-paint, while
        /// GetCachedAsync's freshness check fires a refetch on next call.
        /// Use to force a session-start refresh without losing the
        /// last-known state as a fallback.
        /// </summary>
        public void MarkRaCacheStaleByOwner(string owner)
        {
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE ra_cache SET fetched_at = 0 WHERE owner = $o;";
            cmd.Parameters.AddWithValue("$o", owner ?? "");
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Wipes every cached row tagged with the given owner. Called on RA
        /// logout / API-key change so the next sign-in doesn't see the prior
        /// user's stats.
        /// </summary>
        public void DeleteRaCacheByOwner(string owner)
        {
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM ra_cache WHERE owner = $o;";
            cmd.Parameters.AddWithValue("$o", owner ?? "");
            cmd.ExecuteNonQuery();
        }

        // ── ra_heatmap_daily CRUD ────────────────────────────────────────────

        /// <summary>
        /// Upserts one day's unlock count for a user. Past days are written
        /// once and never refreshed; only today's row gets re-stamped as the
        /// user plays.
        /// </summary>
        public void SetRaHeatmapDay(string user, string isoDate, int count)
        {
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ra_heatmap_daily (user, date, count)
                VALUES ($u, $d, $c)
                ON CONFLICT(user, date) DO UPDATE SET count=excluded.count;";
            cmd.Parameters.AddWithValue("$u", user ?? "");
            cmd.Parameters.AddWithValue("$d", isoDate ?? "");
            cmd.Parameters.AddWithValue("$c", count);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Returns a date-keyed map of unlock counts for the given user
        /// across the range. Inclusive start/end. Used to render the heatmap
        /// grid from cached data only — no network call required for past days.
        /// </summary>
        public Dictionary<string, int> GetRaHeatmapRange(string user, string isoStart, string isoEnd)
        {
            var result = new Dictionary<string, int>();
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT date, count FROM ra_heatmap_daily WHERE user = $u AND date BETWEEN $s AND $e;";
            cmd.Parameters.AddWithValue("$u", user ?? "");
            cmd.Parameters.AddWithValue("$s", isoStart ?? "");
            cmd.Parameters.AddWithValue("$e", isoEnd ?? "");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = reader.GetInt32(1);
            return result;
        }

        /// <summary>
        /// Returns the local Games.Id for a given RA game ID, or null if no
        /// owned game maps to that RA ID. Uses the partial index
        /// idx_games_ra_game_id (RAGameId &gt; 0) so the lookup is O(log n)
        /// instead of a full table scan. Used by the Achievements tab when
        /// rendering tiles that need to launch the local game (e.g. AOTW's
        /// Play Now button).
        /// </summary>
        public int? GetLocalGameIdByRAGameId(int raGameId)
        {
            if (raGameId <= 0) return null;
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id FROM Games WHERE RAGameId = $ra LIMIT 1;";
            cmd.Parameters.AddWithValue("$ra", raGameId);
            var result = cmd.ExecuteScalar();
            if (result == null || result is DBNull) return null;
            return Convert.ToInt32(result);
        }

        /// <summary>
        /// Returns the RA game IDs of every game in the local library that
        /// has been launched at least once with RA enabled. Used by the
        /// Achievements tab to intersect library ownership with RA's
        /// per-user completion stream.
        /// </summary>
        public HashSet<int> GetOwnedRAGameIds()
        {
            var set = new HashSet<int>();
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT RAGameId FROM Games WHERE RAGameId > 0;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                set.Add(reader.GetInt32(0));
            return set;
        }

        /// <summary>
        /// Updates the RomPath for a single game. Used when the launcher transparently
        /// extracts a .zip whose path was stored as-is by the importer (pre-fix imports
        /// via the console-nav hint short-circuit), so the next launch is fast.
        /// </summary>
        public void UpdateRomPath(int gameId, string newRomPath)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET RomPath = $romPath WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$romPath", AppPaths.ToStoragePath(newRomPath));
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Returns the Game.Id for the row whose RomPath matches, or null if none.
        /// Used by the importer's M3U bundler to find stale single-disc rows that
        /// should be replaced when a multi-disc playlist is generated.
        /// </summary>
        public int? GetGameIdByRomPath(string romPath)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id FROM Games WHERE RomPath = $romPath LIMIT 1;";
            cmd.Parameters.AddWithValue("$romPath", AppPaths.ToStoragePath(romPath));
            var result = cmd.ExecuteScalar();
            return result == null || result is DBNull ? (int?)null : Convert.ToInt32(result);
        }

        public bool RomPathExists(string romPath)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            // Match against the storage form. Caller may hand us either an absolute path
            // (a freshly-resolved game ROM) or a value that's already been relativized;
            // ToStoragePath is idempotent on already-relative inputs.
            cmd.CommandText = "SELECT COUNT(*) FROM Games WHERE RomPath = $romPath;";
            cmd.Parameters.AddWithValue("$romPath", AppPaths.ToStoragePath(romPath));
            return (long)cmd.ExecuteScalar()! > 0;
        }

        public HashSet<string> GetAllRomPaths()
        {
            // Returns paths in absolute (resolved) form so callers can compare against
            // freshly-discovered files on disk without worrying about the storage convention.
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT RomPath FROM Games;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string path = reader.GetString(0);
                if (!string.IsNullOrEmpty(path)) set.Add(AppPaths.FromStoragePath(path));
            }
            return set;
        }

        public bool GameExists(int gameId)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Games WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", gameId);
            return (long)cmd.ExecuteScalar()! > 0;
        }

        public void UpdatePlayCount(int gameId)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE Games
                SET PlayCount  = PlayCount + 1,
                    LastPlayed = $lastPlayed
                WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$lastPlayed", DateTime.Now.ToString("o"));
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public void RecalcSaveCount(int gameId)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE Games
                SET SaveCount = (SELECT COUNT(*) FROM SaveStates WHERE GameId = $id)
                WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public void UpdateCoverArt(int gameId, string coverArtPath)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET CoverArtPath = $path WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$path", AppPaths.ToStoragePath(coverArtPath));
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public void UpdateBoxArt3D(int gameId, string path)
        {
            // Guard: never store 2D art paths as 3D box art
            if (!string.IsNullOrEmpty(path) && path.Contains("_2d", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[DB] Rejected 2D path for BoxArt3DPath: {path}");
                return;
            }
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET BoxArt3DPath = $path WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$path", AppPaths.ToStoragePath(path));
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public void UpdateScreenScraperArt(int gameId, string path)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET ScreenScraperArtPath = $path WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$path", AppPaths.ToStoragePath(path));
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public List<Game> GetGamesWithout3DBoxArtForConsole(string console)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT * FROM Games
                                WHERE Console = $console
                                AND   (BoxArt3DPath IS NULL OR BoxArt3DPath = '')
                                ORDER BY Title;";
            cmd.Parameters.AddWithValue("$console", console);
            using var reader = cmd.ExecuteReader();
            return ReadAllGames(reader);
        }

        public List<Game> GetGamesWithoutScreenScraperArt()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT * FROM Games
                                WHERE (ScreenScraperArtPath IS NULL OR ScreenScraperArtPath = '')
                                AND   RomHash != ''
                                ORDER BY Console, Title;";
            using var reader = cmd.ExecuteReader();
            return ReadAllGames(reader);
        }

        public void UpdateHash(int gameId, string hash)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET RomHash = $hash WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public void UpdateRating(int gameId, int rating)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET Rating = $rating WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$rating", rating);
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public void UpdateTitle(int gameId, string title)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET Title = $title WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public void UpdateMetadata(int gameId, string developer, string publisher, string genre, string description)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"UPDATE Games
                SET Developer = $dev, Publisher = $pub, Genre = $genre, Description = $desc
                WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$dev", developer);
            cmd.Parameters.AddWithValue("$pub", publisher);
            cmd.Parameters.AddWithValue("$genre", genre);
            cmd.Parameters.AddWithValue("$desc", description);
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public void UpdateYear(int gameId, int year)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET Year = $year WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$year", year);
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public List<Game> GetGamesWithoutMetadata()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT Id, Title, Console, RomHash, RomPath
                FROM Games
                WHERE (Developer IS NULL OR Developer = '')
                AND   (Genre IS NULL OR Genre = '')
                AND   (RomHash IS NOT NULL AND RomHash != '')
                AND   (MetadataAttempts IS NULL OR MetadataAttempts < 1)
                ORDER BY Console, Title;";
            using var reader = cmd.ExecuteReader();
            var games = new List<Game>();
            while (reader.Read())
            {
                games.Add(new Game
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    Console = reader.GetString(2),
                    RomHash = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    RomPath = AppPaths.FromStoragePath(reader.IsDBNull(4) ? "" : reader.GetString(4)),
                });
            }
            return games;
        }

        // ── Collection methods (join table) ────────────────────────────────

        public int CreateCollection(string name)
        {
            using var connection = OpenConnection();
            // INSERT OR IGNORE in case of duplicate, then SELECT to get the ID either way.
            var insCmd = connection.CreateCommand();
            insCmd.CommandText = "INSERT OR IGNORE INTO Collections (Name) VALUES ($name);";
            insCmd.Parameters.AddWithValue("$name", name);
            insCmd.ExecuteNonQuery();
            var idCmd = connection.CreateCommand();
            idCmd.CommandText = "SELECT Id FROM Collections WHERE Name = $name;";
            idCmd.Parameters.AddWithValue("$name", name);
            return (int)(long)idCmd.ExecuteScalar()!;
        }

        public void DeleteCollection(int collectionId)
        {
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Collections WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", collectionId);
            cmd.ExecuteNonQuery();
        }

        public void RenameCollection(int collectionId, string newName)
        {
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Collections SET Name = $name WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$name", newName);
            cmd.Parameters.AddWithValue("$id", collectionId);
            cmd.ExecuteNonQuery();
        }

        public void AddGameToCollection(int gameId, int collectionId)
        {
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO GameCollections (GameId, CollectionId) VALUES ($gid, $cid);";
            cmd.Parameters.AddWithValue("$gid", gameId);
            cmd.Parameters.AddWithValue("$cid", collectionId);
            cmd.ExecuteNonQuery();
        }

        public void RemoveGameFromCollection(int gameId, int collectionId)
        {
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM GameCollections WHERE GameId = $gid AND CollectionId = $cid;";
            cmd.Parameters.AddWithValue("$gid", gameId);
            cmd.Parameters.AddWithValue("$cid", collectionId);
            cmd.ExecuteNonQuery();
        }

        public List<(int Id, string Name)> GetCollectionsForGame(int gameId)
        {
            var list = new List<(int, string)>();
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT c.Id, c.Name
                FROM Collections c
                INNER JOIN GameCollections gc ON gc.CollectionId = c.Id
                WHERE gc.GameId = $gid
                ORDER BY c.Name;";
            cmd.Parameters.AddWithValue("$gid", gameId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetInt32(0), reader.GetString(1)));
            return list;
        }

        public List<(int Id, string Name)> GetAllCollections()
        {
            var list = new List<(int, string)>();
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, Name FROM Collections ORDER BY SortOrder, Name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetInt32(0), reader.GetString(1)));
            return list;
        }

        public List<Game> GetGamesByCollectionId(int collectionId)
        {
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT g.* FROM Games g
                INNER JOIN GameCollections gc ON gc.GameId = g.Id
                WHERE gc.CollectionId = $cid
                ORDER BY g.Title;";
            cmd.Parameters.AddWithValue("$cid", collectionId);
            using var reader = cmd.ExecuteReader();
            return ReadAllGames(reader);
        }

        public void VacuumDatabase()
        {
            using var connection = OpenConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "VACUUM;";
            cmd.ExecuteNonQuery();
        }

        public List<Game> GetRecentlyAdded(int limit = 25)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Games ORDER BY DateAdded DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            return ReadAllGames(reader);
        }

        public void ToggleFavorite(int gameId, bool isFavorite)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET IsFavorite = $fav WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$fav", isFavorite ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public void IncrementArtworkAttempts(int gameId)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET ArtworkAttempts = ArtworkAttempts + 1 WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Increments the per-game metadata-attempt counter. Used by the metadata
        /// refresh pipeline to mark a game as "we tried, fetch came back empty"
        /// so the auto-resume filter skips it on subsequent launches.
        /// </summary>
        public void IncrementMetadataAttempts(int gameId)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET MetadataAttempts = MetadataAttempts + 1 WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Resets MetadataAttempts to 0 for every game on the given console.
        /// Called when the user explicitly clicks Refresh Library on that
        /// console — they're asking for a re-try, so previously-attempted games
        /// re-enter the refresh queue.
        /// </summary>
        public void ResetMetadataAttemptsForConsole(string console)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET MetadataAttempts = 0 WHERE Console = $console;";
            cmd.Parameters.AddWithValue("$console", console);
            cmd.ExecuteNonQuery();
        }

        public int GetSaveStateCountForGame(int gameId)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM SaveStates WHERE GameId = $id;";
            cmd.Parameters.AddWithValue("$id", gameId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public int GetSaveStateCountForGames(IEnumerable<int> gameIds)
        {
            var ids = gameIds.ToList();
            if (ids.Count == 0) return 0;
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM SaveStates WHERE GameId IN ({string.Join(",", ids)});";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void DeleteGame(int gameId)
        {
            using var connection = OpenConnection();
            // Disable FK enforcement so save states are preserved when a game is removed from the library.
            using (var fk = connection.CreateCommand()) { fk.CommandText = "PRAGMA foreign_keys = OFF;"; fk.ExecuteNonQuery(); }
            // Clean up join table manually since FKs are disabled
            using (var gc = connection.CreateCommand()) { gc.CommandText = "DELETE FROM GameCollections WHERE GameId = $id;"; gc.Parameters.AddWithValue("$id", gameId); gc.ExecuteNonQuery(); }
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Games WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public void DeleteGames(IEnumerable<int> gameIds)
        {
            var ids = gameIds.ToList();
            if (ids.Count == 0) return;
            using var connection = OpenConnection();
            using (var fk = connection.CreateCommand()) { fk.CommandText = "PRAGMA foreign_keys = OFF;"; fk.ExecuteNonQuery(); }
            using var tx = connection.BeginTransaction();
            // Clean up join table manually since FKs are disabled
            var gcCmd = connection.CreateCommand();
            gcCmd.CommandText = "DELETE FROM GameCollections WHERE GameId = $id;";
            var gcParam = gcCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Games WHERE Id = $id;";
            var param = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (int id in ids)
            {
                gcParam.Value = id;
                gcCmd.ExecuteNonQuery();
                param.Value = id;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        public int GetGameCountForConsole(string console)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Games WHERE Console = $console;";
            cmd.Parameters.AddWithValue("$console", console);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void DeleteAllGamesForConsole(string console)
        {
            using var connection = OpenConnection();
            // Artwork files are preserved so they can be reused if the library is re-imported.
            using (var fk = connection.CreateCommand()) { fk.CommandText = "PRAGMA foreign_keys = OFF;"; fk.ExecuteNonQuery(); }
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Games WHERE Console = $console;";
            cmd.Parameters.AddWithValue("$console", console);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Deletes artwork files (CoverArtPath, BoxArt3DPath, ScreenScraperArtPath) for games matching the query.
        /// Called before DELETE so the files don't become orphans.
        /// </summary>
        private static void CleanupArtworkFiles(SqliteConnection connection, string query,
            (string name, object value)[] parameters)
        {
            try
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText = query;
                foreach (var (name, value) in parameters)
                    cmd.Parameters.AddWithValue(name, value);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        if (reader.IsDBNull(i)) continue;
                        string path = reader.GetString(i);
                        if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                        {
                            try { System.IO.File.Delete(path); }
                            catch { /* non-fatal */ }
                        }
                    }
                }
            }
            catch { /* non-fatal — don't block the delete */ }
        }

        public List<Game> GetGamesWithoutArtworkForConsole(string console)
        {
            var games = new List<Game>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            // Manual fetch — ignores attempt cap so user can force a retry for any game.
            cmd.CommandText = @"
                SELECT Id, Title, Console, RomHash, RomPath, BackgroundColor, AccentColor
                FROM Games
                WHERE Console = $console
                AND   (CoverArtPath IS NULL OR CoverArtPath = '')
                ORDER BY ArtworkAttempts ASC;";
            cmd.Parameters.AddWithValue("$console", console);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                games.Add(new Game
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    Console = reader.GetString(2),
                    RomHash = reader.GetString(3),
                    RomPath = AppPaths.FromStoragePath(reader.IsDBNull(4) ? "" : reader.GetString(4)),
                    BackgroundColor = reader.IsDBNull(5) ? "#1F1F21" : reader.GetString(5),
                    AccentColor = reader.IsDBNull(6) ? "#E03535" : reader.GetString(6),
                });
            }
            return games;
        }

        public List<Game> GetGamesWithoutArtwork()
        {
            var games = new List<Game>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, Title, Console, RomHash, RomPath, BackgroundColor, AccentColor, ArtworkAttempts
                FROM Games
                WHERE (CoverArtPath IS NULL OR CoverArtPath = '')
                AND   (RomHash IS NOT NULL AND RomHash != '')
                AND   ArtworkAttempts < 2
                ORDER BY ArtworkAttempts ASC;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                games.Add(new Game
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    Console = reader.GetString(2),
                    RomHash = reader.GetString(3),
                    RomPath = AppPaths.FromStoragePath(reader.IsDBNull(4) ? "" : reader.GetString(4)),
                    BackgroundColor = reader.IsDBNull(5) ? "#1F1F21" : reader.GetString(5),
                    AccentColor = reader.IsDBNull(6) ? "#E03535" : reader.GetString(6),
                    ArtworkAttempts = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                });
            }
            return games;
        }

        public List<Game> GetAllGames()
        {
            var games = new List<Game>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Games ORDER BY Title;";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return games;
            var o = new OrdinalMap(reader);
            do
            {
                try
                {
                    games.Add(ReadGame(reader, o));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"ReadGame failed: {ex.Message}");
                    for (int i = 0; i < reader.FieldCount; i++)
                        System.Diagnostics.Trace.WriteLine($"  Col {i}: {reader.GetName(i)} = {(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i))}");
                }
            } while (reader.Read());
            return games;
        }

        public List<Game> GetFavorites()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Games WHERE IsFavorite = 1 ORDER BY Console, Title;";
            using var reader = cmd.ExecuteReader();
            return ReadAllGames(reader);
        }

        public List<Game> GetRecentlyPlayed()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM Games
                WHERE LastPlayed IS NOT NULL
                ORDER BY LastPlayed DESC
                LIMIT 20;";
            using var reader = cmd.ExecuteReader();
            return ReadAllGames(reader);
        }

        // GetByCollection(string) removed — use GetGamesByCollectionId(int) instead.

        /// <summary>
        /// Resolves column ordinals once per reader, then reuses for every row.
        /// Returns -1 for columns that don't exist in the result set.
        /// </summary>
        private sealed class OrdinalMap
        {
            public readonly int Id, Title, Console, Manufacturer, Year, RomPath, OriginalSourcePath, RomHash,
                CoverArtPath, BackgroundColor, AccentColor, PlayCount, SaveCount,
                IsFavorite, Rating, Collection, LastPlayed, BoxArt3DPath,
                ScreenScraperArtPath, ArtworkAttempts, MetadataAttempts,
                Developer, Publisher, Genre, Description, PreferredCore,
                RAGameId, RAProgressionJson, RAProgressionFetchedAt,
                RAUserProgressJson, RAUserProgressFetchedAt,
                RALiveProgressJson, RALiveProgressFetchedAt;

            public OrdinalMap(SqliteDataReader reader)
            {
                Id       = TryOrd(reader, "Id");
                Title    = TryOrd(reader, "Title");
                Console  = TryOrd(reader, "Console");
                Manufacturer = TryOrd(reader, "Manufacturer");
                Year     = TryOrd(reader, "Year");
                RomPath  = TryOrd(reader, "RomPath");
                OriginalSourcePath = TryOrd(reader, "OriginalSourcePath");
                RomHash  = TryOrd(reader, "RomHash");
                CoverArtPath    = TryOrd(reader, "CoverArtPath");
                BackgroundColor = TryOrd(reader, "BackgroundColor");
                AccentColor     = TryOrd(reader, "AccentColor");
                PlayCount  = TryOrd(reader, "PlayCount");
                SaveCount  = TryOrd(reader, "SaveCount");
                IsFavorite = TryOrd(reader, "IsFavorite");
                Rating     = TryOrd(reader, "Rating");
                Collection = TryOrd(reader, "Collection");
                LastPlayed = TryOrd(reader, "LastPlayed");
                BoxArt3DPath = TryOrd(reader, "BoxArt3DPath");
                ScreenScraperArtPath = TryOrd(reader, "ScreenScraperArtPath");
                ArtworkAttempts = TryOrd(reader, "ArtworkAttempts");
                MetadataAttempts = TryOrd(reader, "MetadataAttempts");
                Developer   = TryOrd(reader, "Developer");
                Publisher   = TryOrd(reader, "Publisher");
                Genre       = TryOrd(reader, "Genre");
                Description = TryOrd(reader, "Description");
                PreferredCore = TryOrd(reader, "PreferredCore");
                RAGameId                = TryOrd(reader, "RAGameId");
                RAProgressionJson       = TryOrd(reader, "RAProgressionJson");
                RAProgressionFetchedAt  = TryOrd(reader, "RAProgressionFetchedAt");
                RAUserProgressJson      = TryOrd(reader, "RAUserProgressJson");
                RAUserProgressFetchedAt = TryOrd(reader, "RAUserProgressFetchedAt");
                RALiveProgressJson      = TryOrd(reader, "RALiveProgressJson");
                RALiveProgressFetchedAt = TryOrd(reader, "RALiveProgressFetchedAt");
            }

            private static int TryOrd(SqliteDataReader r, string col)
            { try { return r.GetOrdinal(col); } catch { return -1; } }
        }

        private static Game ReadGame(SqliteDataReader reader, OrdinalMap o)
        {
            return new Game
            {
                Id              = reader.GetInt32(o.Id),
                Title           = reader.GetString(o.Title),
                Console         = reader.GetString(o.Console),
                Manufacturer    = GetStr(reader, o.Manufacturer),
                Year            = GetInt(reader, o.Year),
                // Filesystem paths are stored relative to DataRoot when possible (portable mode
                // survives drive-letter changes). Resolve back to absolute on read so the rest
                // of the app sees fully-qualified paths.
                RomPath         = AppPaths.FromStoragePath(GetStr(reader, o.RomPath)),
                OriginalSourcePath = AppPaths.FromStoragePath(GetStr(reader, o.OriginalSourcePath)),
                RomHash         = GetStr(reader, o.RomHash),
                CoverArtPath    = AppPaths.FromStoragePath(GetStr(reader, o.CoverArtPath)),
                BackgroundColor = GetStr(reader, o.BackgroundColor, "#1F1F21"),
                AccentColor     = GetStr(reader, o.AccentColor, "#E03535"),
                PlayCount       = GetInt(reader, o.PlayCount),
                SaveCount       = GetInt(reader, o.SaveCount),
                IsFavorite      = GetInt(reader, o.IsFavorite) == 1,
                Rating          = GetInt(reader, o.Rating),
                Collection      = GetStr(reader, o.Collection),
                LastPlayed      = GetDate(reader, o.LastPlayed),
                BoxArt3DPath    = AppPaths.FromStoragePath(GetStr(reader, o.BoxArt3DPath)),
                ScreenScraperArtPath = AppPaths.FromStoragePath(GetStr(reader, o.ScreenScraperArtPath)),
                ArtworkAttempts = GetInt(reader, o.ArtworkAttempts),
                MetadataAttempts = GetInt(reader, o.MetadataAttempts),
                Developer   = GetStr(reader, o.Developer),
                Publisher   = GetStr(reader, o.Publisher),
                Genre       = GetStr(reader, o.Genre),
                Description = GetStr(reader, o.Description),
                PreferredCore = GetStr(reader, o.PreferredCore),
                RAGameId                = GetInt(reader, o.RAGameId),
                RAProgressionJson       = GetStr(reader, o.RAProgressionJson),
                RAProgressionFetchedAt  = GetLong(reader, o.RAProgressionFetchedAt),
                RAUserProgressJson      = GetStr(reader, o.RAUserProgressJson),
                RAUserProgressFetchedAt = GetLong(reader, o.RAUserProgressFetchedAt),
                RALiveProgressJson      = GetStr(reader, o.RALiveProgressJson),
                RALiveProgressFetchedAt = GetLong(reader, o.RALiveProgressFetchedAt),
            };
        }

        private static List<Game> ReadAllGames(SqliteDataReader reader)
        {
            var games = new List<Game>();
            if (!reader.Read()) return games;
            var o = new OrdinalMap(reader);
            do { games.Add(ReadGame(reader, o)); } while (reader.Read());
            return games;
        }

        private static Game? ReadSingleGame(SqliteDataReader reader)
        {
            if (!reader.Read()) return null;
            return ReadGame(reader, new OrdinalMap(reader));
        }

        private static string GetStr(SqliteDataReader r, int ord, string def = "")
            => ord >= 0 && !r.IsDBNull(ord) ? r.GetString(ord) : def;

        private static int GetInt(SqliteDataReader r, int ord)
            => ord >= 0 && !r.IsDBNull(ord) ? r.GetInt32(ord) : 0;

        private static long GetLong(SqliteDataReader r, int ord)
            => ord >= 0 && !r.IsDBNull(ord) ? r.GetInt64(ord) : 0L;

        private static DateTime? GetDate(SqliteDataReader r, int ord)
        {
            if (ord < 0 || r.IsDBNull(ord)) return null;
            string val = r.GetString(ord);
            return DateTime.TryParse(val, out var dt) ? dt : null;
        }

        // Save State methods

        public Game? GetGameById(int id)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Games WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return ReadSingleGame(reader);
        }

        /// <summary>
        /// Fires when a save state is inserted, deleted, renamed, or rediscovered.
        /// MainWindow's Save States tab subscribes so it can live-refresh while
        /// open — without this the user has to nav away and back to see a state
        /// they just made. Raised on whichever thread mutates the table; the
        /// subscriber must marshal to the UI thread before touching UI.
        /// Static so subscribers receive events from any DatabaseService
        /// instance — EmulatorWindow constructs its own DatabaseService for
        /// per-game work, separate from MainWindow's instance.
        /// </summary>
        public static event EventHandler? SaveStatesChanged;

        public int InsertSaveState(SaveState s)
        {
            int newId;
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO SaveStates (GameId, Slot, FilePath, Screenshot, CreatedAt, Name, GameTitle, ConsoleName, CoreName, RomHash)
                    VALUES ($gameId, 0, $filePath, $screenshot, $createdAt, $name, $gameTitle, $consoleName, $coreName, $romHash);";
                cmd.Parameters.AddWithValue("$gameId",      s.GameId);
                cmd.Parameters.AddWithValue("$filePath",    AppPaths.ToStoragePath(s.StatePath));
                cmd.Parameters.AddWithValue("$screenshot",  AppPaths.ToStoragePath(s.ScreenshotPath ?? ""));
                cmd.Parameters.AddWithValue("$createdAt",   s.CreatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("$name",        s.Name);
                cmd.Parameters.AddWithValue("$gameTitle",   s.GameTitle);
                cmd.Parameters.AddWithValue("$consoleName", s.ConsoleName);
                cmd.Parameters.AddWithValue("$coreName",    s.CoreName);
                cmd.Parameters.AddWithValue("$romHash",     s.RomHash);
                cmd.ExecuteNonQuery();

                var idCmd = connection.CreateCommand();
                idCmd.CommandText = "SELECT last_insert_rowid();";
                newId = (int)(long)idCmd.ExecuteScalar()!;
            }
            System.Diagnostics.Trace.WriteLine("[DB] SaveStatesChanged fired");
            SaveStatesChanged?.Invoke(this, EventArgs.Empty);
            return newId;
        }

        public void DeleteSaveState(int id)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM SaveStates WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            System.Diagnostics.Trace.WriteLine("[DB] SaveStatesChanged fired");
            SaveStatesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateSaveStateName(int id, string newName, string newStatePath, string newScreenshotPath)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE SaveStates
                    SET Name = $name, FilePath = $filePath, Screenshot = $screenshot
                    WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$name",       newName);
                cmd.Parameters.AddWithValue("$filePath",   AppPaths.ToStoragePath(newStatePath));
                cmd.Parameters.AddWithValue("$screenshot", AppPaths.ToStoragePath(newScreenshotPath ?? ""));
                cmd.Parameters.AddWithValue("$id",         id);
                cmd.ExecuteNonQuery();
            }
            System.Diagnostics.Trace.WriteLine("[DB] SaveStatesChanged fired");
            SaveStatesChanged?.Invoke(this, EventArgs.Empty);
        }

        public List<SaveState> GetSaveStatesByGame(int gameId)
        {
            var list = new List<SaveState>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, GameId, Slot, FilePath, Screenshot, CreatedAt, Name, GameTitle, ConsoleName, CoreName, RomHash
                FROM SaveStates WHERE GameId = $gameId ORDER BY CreatedAt DESC;";
            cmd.Parameters.AddWithValue("$gameId", gameId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadSaveState(reader));
            return list;
        }

        public List<SaveState> GetAllSaveStates()
        {
            var list = new List<SaveState>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, GameId, Slot, FilePath, Screenshot, CreatedAt, Name, GameTitle, ConsoleName, CoreName, RomHash
                FROM SaveStates ORDER BY CreatedAt DESC;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadSaveState(reader));
            return list;
        }

        /// <summary>
        /// Scans Save States/ on disk for .json metadata + .state files that aren't
        /// already registered in the database, and inserts them. Matches to games by RomHash.
        /// Called on startup so save states survive a database rebuild.
        /// </summary>
        public int DiscoverOrphanedSaveStates()
        {
            string root = Path.Combine(AppPaths.DataRoot, "Save States");
            if (!Directory.Exists(root)) return 0;

            // Build a set of known state file paths so we don't insert duplicates
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in GetAllSaveStates())
                if (!string.IsNullOrEmpty(s.StatePath)) known.Add(s.StatePath);

            // Build hash→gameId lookup from current games
            var hashToGame = new Dictionary<string, (int id, string title)>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in GetAllGames())
                if (!string.IsNullOrEmpty(g.RomHash) && !hashToGame.ContainsKey(g.RomHash))
                    hashToGame[g.RomHash] = (g.Id, g.Title);

            int count = 0;
            foreach (string consoleDir in Directory.EnumerateDirectories(root))
            {
                foreach (string gameDir in Directory.EnumerateDirectories(consoleDir))
                {
                    foreach (string jsonFile in Directory.EnumerateFiles(gameDir, "*.json"))
                    {
                        try
                        {
                            string stem = Path.GetFileNameWithoutExtension(jsonFile);
                            string statePath = Path.Combine(gameDir, stem + ".state");
                            if (!File.Exists(statePath)) continue;
                            if (known.Contains(statePath)) continue;

                            string json = File.ReadAllText(jsonFile);
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            var el = doc.RootElement;

                            string romHash = el.TryGetProperty("RomHash", out var h) ? h.GetString() ?? "" : "";
                            string name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? stem : stem;
                            string consoleName = el.TryGetProperty("ConsoleName", out var c) ? c.GetString() ?? "" : "";
                            string gameTitle = el.TryGetProperty("GameTitle", out var t) ? t.GetString() ?? "" : "";
                            string coreName = el.TryGetProperty("CoreName", out var cn) ? cn.GetString() ?? "" : "";
                            DateTime created = DateTime.Now;
                            if (el.TryGetProperty("CreatedAt", out var d) && d.GetString() is string ds
                                && DateTime.TryParse(ds, out var parsed))
                                created = parsed;

                            string pngPath = Path.Combine(gameDir, stem + ".png");

                            // Try to match to an existing game by hash
                            int gameId = 0;
                            if (!string.IsNullOrEmpty(romHash) && hashToGame.TryGetValue(romHash, out var match))
                            {
                                gameId = match.id;
                                gameTitle = match.title; // use current title
                            }

                            var ss = new SaveState
                            {
                                GameId = gameId,
                                Name = name,
                                GameTitle = gameTitle,
                                ConsoleName = consoleName,
                                CoreName = coreName,
                                RomHash = romHash,
                                StatePath = statePath,
                                ScreenshotPath = File.Exists(pngPath) ? pngPath : "",
                                CreatedAt = created,
                            };
                            InsertSaveState(ss);
                            known.Add(statePath);
                            count++;
                        }
                        catch { /* skip malformed json */ }
                    }
                }
            }

            // Update save counts for all affected games
            if (count > 0)
            {
                foreach (var g in GetAllGames())
                    RecalcSaveCount(g.Id);
            }

            return count;
        }

        public SaveState? GetSaveStateByGameAndName(int gameId, string name)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, GameId, Slot, FilePath, Screenshot, CreatedAt, Name, GameTitle, ConsoleName, CoreName, RomHash
                FROM SaveStates WHERE GameId = $gameId AND Name = $name LIMIT 1;";
            cmd.Parameters.AddWithValue("$gameId", gameId);
            cmd.Parameters.AddWithValue("$name",   name);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadSaveState(reader) : null;
        }

        private SaveState ReadSaveState(SqliteDataReader r)
        {
            // 0=Id 1=GameId 2=Slot(skip) 3=FilePath 4=Screenshot 5=CreatedAt 6=Name 7=GameTitle 8=ConsoleName 9=CoreName 10=RomHash
            return new SaveState
            {
                Id             = r.GetInt32(0),
                GameId         = r.GetInt32(1),
                StatePath      = AppPaths.FromStoragePath(r.IsDBNull(3) ? "" : r.GetString(3)),
                ScreenshotPath = AppPaths.FromStoragePath(r.IsDBNull(4) ? "" : r.GetString(4)),
                CreatedAt      = r.IsDBNull(5) ? DateTime.Now :
                                     DateTime.TryParse(r.GetString(5), out var dt) ? dt : DateTime.Now,
                Name           = r.IsDBNull(6) ? "" : r.GetString(6),
                GameTitle      = r.IsDBNull(7) ? "" : r.GetString(7),
                ConsoleName    = r.IsDBNull(8) ? "" : r.GetString(8),
                CoreName       = r.IsDBNull(9) ? "" : r.GetString(9),
                RomHash        = r.IsDBNull(10) ? "" : r.GetString(10),
            };
        }

        // Input Mapping methods
        public List<InputMapping> GetInputMappings()
        {
            var mappings = new List<InputMapping>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM InputMappings;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                mappings.Add(new InputMapping
                {
                    ConsoleName = reader.GetString(1),
                    ButtonName = reader.GetString(2),
                    InputType = Enum.Parse<InputType>(reader.GetString(3)),
                    Key = reader.IsDBNull(4) ? System.Windows.Input.Key.None : (System.Windows.Input.Key)reader.GetInt32(4),
                    ControllerButtonId = reader.IsDBNull(5) ? 0 : (uint)reader.GetInt32(5),
                    DisplayText = reader.IsDBNull(6) ? "" : reader.GetString(6)
                });
            }
            return mappings;
        }

        public void SaveInputMappings(List<InputMapping> mappings)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            
            // Clear existing mappings
            var clearCmd = connection.CreateCommand();
            clearCmd.CommandText = "DELETE FROM InputMappings;";
            clearCmd.ExecuteNonQuery();
            
            // Insert new mappings
            foreach (var mapping in mappings)
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO InputMappings (ConsoleName, ButtonName, InputType, KeyCode, ControllerButtonId, DisplayText)
                    VALUES ($console, $button, $type, $key, $controllerBtn, $display);";
                cmd.Parameters.AddWithValue("$console", mapping.ConsoleName);
                cmd.Parameters.AddWithValue("$button", mapping.ButtonName);
                cmd.Parameters.AddWithValue("$type", mapping.InputType.ToString());
                cmd.Parameters.AddWithValue("$key", (int)mapping.Key);
                cmd.Parameters.AddWithValue("$controllerBtn", (int)mapping.ControllerButtonId);
                cmd.Parameters.AddWithValue("$display", mapping.DisplayText);
                cmd.ExecuteNonQuery();
            }
            
            transaction.Commit();
        }
    }

    public class InputMapping
    {
        public string ConsoleName { get; set; } = "";
        public string ButtonName { get; set; } = "";
        public InputType InputType { get; set; }
        public System.Windows.Input.Key Key { get; set; }
        public uint ControllerButtonId { get; set; }
        public string DisplayText { get; set; } = "";
        public bool IsSelected { get; set; }
        // For chord mappings (e.g. "Disk Swap" = L3 + Start). Format: "A+B" where A
        // and B are either two key names (keyboard) or two controller-button ids.
        // Null/empty for single-input mappings.
        public string? ChordIdentifier { get; set; }
    }

    public enum InputType { Keyboard, Controller }
}