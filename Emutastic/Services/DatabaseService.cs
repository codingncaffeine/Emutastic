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
            pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
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

            // One-time cleanup: remove Arcade-tagged entries that aren't .zip files.
            // FBNeo arcade ROMs are always .zip; anything else was misidentified on import.
            var cleanCmd = connection.CreateCommand();
            cleanCmd.CommandText = "DELETE FROM Games WHERE Console = 'Arcade' AND RomPath NOT LIKE '%.zip';";
            cleanCmd.ExecuteNonQuery();

            // One-time path migration: fix paths that still reference the old AppData folder name.
            var pathFixCmd = connection.CreateCommand();
            pathFixCmd.CommandText =
                "UPDATE Games SET CoverArtPath = REPLACE(CoverArtPath, '\\OpenEmuWindows\\', '\\Emutastic\\') " +
                "WHERE CoverArtPath LIKE '%OpenEmuWindows%';" +
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
                    string coverPath = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    string art3DPath = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    string ssArtPath = reader.IsDBNull(4) ? "" : reader.GetString(4);

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
                            u.Parameters.AddWithValue("$path", newCover);
                            u.Parameters.AddWithValue("$id", id);
                            u.ExecuteNonQuery();
                        }
                        if (new3D != null)
                        {
                            var u = connection.CreateCommand();
                            u.CommandText = "UPDATE Games SET BoxArt3DPath = $path WHERE Id = $id;";
                            u.Parameters.AddWithValue("$path", new3D);
                            u.Parameters.AddWithValue("$id", id);
                            u.ExecuteNonQuery();
                        }
                        if (newSS != null)
                        {
                            var u = connection.CreateCommand();
                            u.CommandText = "UPDATE Games SET ScreenScraperArtPath = $path WHERE Id = $id;";
                            u.Parameters.AddWithValue("$path", newSS);
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
                        upd.Parameters.AddWithValue("$path", destPath);
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
                    (Title, Console, Manufacturer, Year, RomPath, RomHash,
                     CoverArtPath, BoxArt3DPath, ScreenScraperArtPath,
                     BackgroundColor, AccentColor, Rating, Collection, DateAdded,
                     Developer, Publisher, Genre, Description)
                VALUES
                    ($title, $console, $manufacturer, $year, $romPath, $romHash,
                     $coverArt, $boxArt3D, $ssArt,
                     $bgColor, $accentColor, 0, '', $dateAdded,
                     $developer, $publisher, $genre, $description);";

            cmd.Parameters.AddWithValue("$title", game.Title);
            cmd.Parameters.AddWithValue("$console", game.Console);
            cmd.Parameters.AddWithValue("$manufacturer", game.Manufacturer);
            cmd.Parameters.AddWithValue("$year", game.Year);
            cmd.Parameters.AddWithValue("$romPath", game.RomPath);
            cmd.Parameters.AddWithValue("$romHash", game.RomHash ?? "");
            cmd.Parameters.AddWithValue("$coverArt", game.CoverArtPath ?? "");
            cmd.Parameters.AddWithValue("$boxArt3D", game.BoxArt3DPath ?? "");
            cmd.Parameters.AddWithValue("$ssArt", game.ScreenScraperArtPath ?? "");
            cmd.Parameters.AddWithValue("$bgColor", game.BackgroundColor);
            cmd.Parameters.AddWithValue("$accentColor", game.AccentColor);
            cmd.Parameters.AddWithValue("$dateAdded", DateTime.Now.ToString("o"));
            cmd.Parameters.AddWithValue("$developer", game.Developer ?? "");
            cmd.Parameters.AddWithValue("$publisher", game.Publisher ?? "");
            cmd.Parameters.AddWithValue("$genre", game.Genre ?? "");
            cmd.Parameters.AddWithValue("$description", game.Description ?? "");
            cmd.ExecuteNonQuery();

            var idCmd = connection.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid();";
            game.Id = (int)(long)idCmd.ExecuteScalar()!;
        }

        public bool RomPathExists(string romPath)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Games WHERE RomPath = $romPath;";
            cmd.Parameters.AddWithValue("$romPath", romPath);
            return (long)cmd.ExecuteScalar()! > 0;
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
            cmd.Parameters.AddWithValue("$path", coverArtPath);
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
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public void UpdateScreenScraperArt(int gameId, string path)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET ScreenScraperArtPath = $path WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$path", path);
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
                AND   (RomHash IS NOT NULL AND RomHash != '')
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
                    RomPath = reader.IsDBNull(4) ? "" : reader.GetString(4),
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
            // Clean up artwork files before deleting the DB record
            CleanupArtworkFiles(connection, "SELECT CoverArtPath, BoxArt3DPath, ScreenScraperArtPath FROM Games WHERE Id = $id;",
                new[] { ("$id", (object)gameId) });
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
            // Clean up artwork files before deleting DB records
            foreach (int id in ids)
                CleanupArtworkFiles(connection, "SELECT CoverArtPath, BoxArt3DPath, ScreenScraperArtPath FROM Games WHERE Id = $id;",
                    new[] { ("$id", (object)id) });
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
                    RomPath = reader.IsDBNull(4) ? "" : reader.GetString(4),
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
                AND   ArtworkAttempts < 3
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
                    RomPath = reader.IsDBNull(4) ? "" : reader.GetString(4),
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
            public readonly int Id, Title, Console, Manufacturer, Year, RomPath, RomHash,
                CoverArtPath, BackgroundColor, AccentColor, PlayCount, SaveCount,
                IsFavorite, Rating, Collection, LastPlayed, BoxArt3DPath,
                ScreenScraperArtPath, ArtworkAttempts,
                Developer, Publisher, Genre, Description;

            public OrdinalMap(SqliteDataReader reader)
            {
                Id       = TryOrd(reader, "Id");
                Title    = TryOrd(reader, "Title");
                Console  = TryOrd(reader, "Console");
                Manufacturer = TryOrd(reader, "Manufacturer");
                Year     = TryOrd(reader, "Year");
                RomPath  = TryOrd(reader, "RomPath");
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
                Developer   = TryOrd(reader, "Developer");
                Publisher   = TryOrd(reader, "Publisher");
                Genre       = TryOrd(reader, "Genre");
                Description = TryOrd(reader, "Description");
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
                RomPath         = GetStr(reader, o.RomPath),
                RomHash         = GetStr(reader, o.RomHash),
                CoverArtPath    = GetStr(reader, o.CoverArtPath),
                BackgroundColor = GetStr(reader, o.BackgroundColor, "#1F1F21"),
                AccentColor     = GetStr(reader, o.AccentColor, "#E03535"),
                PlayCount       = GetInt(reader, o.PlayCount),
                SaveCount       = GetInt(reader, o.SaveCount),
                IsFavorite      = GetInt(reader, o.IsFavorite) == 1,
                Rating          = GetInt(reader, o.Rating),
                Collection      = GetStr(reader, o.Collection),
                LastPlayed      = GetDate(reader, o.LastPlayed),
                BoxArt3DPath    = GetStr(reader, o.BoxArt3DPath),
                ScreenScraperArtPath = GetStr(reader, o.ScreenScraperArtPath),
                ArtworkAttempts = GetInt(reader, o.ArtworkAttempts),
                Developer   = GetStr(reader, o.Developer),
                Publisher   = GetStr(reader, o.Publisher),
                Genre       = GetStr(reader, o.Genre),
                Description = GetStr(reader, o.Description),
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

        public int InsertSaveState(SaveState s)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO SaveStates (GameId, Slot, FilePath, Screenshot, CreatedAt, Name, GameTitle, ConsoleName, CoreName, RomHash)
                VALUES ($gameId, 0, $filePath, $screenshot, $createdAt, $name, $gameTitle, $consoleName, $coreName, $romHash);";
            cmd.Parameters.AddWithValue("$gameId",      s.GameId);
            cmd.Parameters.AddWithValue("$filePath",    s.StatePath);
            cmd.Parameters.AddWithValue("$screenshot",  s.ScreenshotPath);
            cmd.Parameters.AddWithValue("$createdAt",   s.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$name",        s.Name);
            cmd.Parameters.AddWithValue("$gameTitle",   s.GameTitle);
            cmd.Parameters.AddWithValue("$consoleName", s.ConsoleName);
            cmd.Parameters.AddWithValue("$coreName",    s.CoreName);
            cmd.Parameters.AddWithValue("$romHash",     s.RomHash);
            cmd.ExecuteNonQuery();

            var idCmd = connection.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid();";
            return (int)(long)idCmd.ExecuteScalar()!;
        }

        public void DeleteSaveState(int id)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM SaveStates WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        public void UpdateSaveStateName(int id, string newName, string newStatePath, string newScreenshotPath)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE SaveStates
                SET Name = $name, FilePath = $filePath, Screenshot = $screenshot
                WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$name",       newName);
            cmd.Parameters.AddWithValue("$filePath",   newStatePath);
            cmd.Parameters.AddWithValue("$screenshot", newScreenshotPath);
            cmd.Parameters.AddWithValue("$id",         id);
            cmd.ExecuteNonQuery();
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
                StatePath      = r.IsDBNull(3) ? "" : r.GetString(3),
                ScreenshotPath = r.IsDBNull(4) ? "" : r.GetString(4),
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
    }

    public enum InputType { Keyboard, Controller }
}