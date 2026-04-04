using Microsoft.Data.Sqlite;
using Emutastic.Models;
using System;
using System.Collections.Generic;
using System.IO;

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
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string appFolder = Path.Combine(appData, "OpenEmuWindows");
                Directory.CreateDirectory(appFolder);
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
                );";
            cmd.ExecuteNonQuery();

            TryAddColumn(connection, "Games", "Rating", "INTEGER DEFAULT 0");
            TryAddColumn(connection, "Games", "Collection", "TEXT DEFAULT ''");

            TryAddColumn(connection, "SaveStates", "Name",        "TEXT NOT NULL DEFAULT ''");
            TryAddColumn(connection, "SaveStates", "GameTitle",   "TEXT NOT NULL DEFAULT ''");
            TryAddColumn(connection, "SaveStates", "ConsoleName", "TEXT NOT NULL DEFAULT ''");
            TryAddColumn(connection, "SaveStates", "CoreName",    "TEXT NOT NULL DEFAULT ''");
            TryAddColumn(connection, "SaveStates", "RomHash",     "TEXT NOT NULL DEFAULT ''");
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
                     CoverArtPath, BackgroundColor, AccentColor, Rating, Collection, DateAdded)
                VALUES
                    ($title, $console, $manufacturer, $year, $romPath, $romHash,
                     $coverArt, $bgColor, $accentColor, 0, '', $dateAdded);";

            cmd.Parameters.AddWithValue("$title", game.Title);
            cmd.Parameters.AddWithValue("$console", game.Console);
            cmd.Parameters.AddWithValue("$manufacturer", game.Manufacturer);
            cmd.Parameters.AddWithValue("$year", game.Year);
            cmd.Parameters.AddWithValue("$romPath", game.RomPath);
            cmd.Parameters.AddWithValue("$romHash", game.RomHash ?? "");
            cmd.Parameters.AddWithValue("$coverArt", game.CoverArtPath ?? "");
            cmd.Parameters.AddWithValue("$bgColor", game.BackgroundColor);
            cmd.Parameters.AddWithValue("$accentColor", game.AccentColor);
            cmd.Parameters.AddWithValue("$dateAdded", DateTime.Now.ToString("o"));
            cmd.ExecuteNonQuery();

            var idCmd = connection.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid();";
            game.Id = (int)(long)idCmd.ExecuteScalar()!;
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

        public void UpdateCollection(int gameId, string collection)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Games SET Collection = $collection WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$collection", collection);
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public List<string> GetAllCollections()
        {
            var collections = new List<string>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT Collection FROM Games WHERE Collection != '' ORDER BY Collection;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                collections.Add(reader.GetString(0));
            return collections;
        }

        public List<Game> GetRecentlyAdded(int limit = 25)
        {
            var games = new List<Game>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Games ORDER BY DateAdded DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) games.Add(ReadGame(reader));
            return games;
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

        public void DeleteGame(int gameId)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Games WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }

        public List<Game> GetGamesWithoutArtwork()
        {
            var games = new List<Game>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, Title, Console, RomHash, RomPath, BackgroundColor, AccentColor
                FROM Games
                WHERE (CoverArtPath IS NULL OR CoverArtPath = '')
                AND   (RomHash IS NOT NULL AND RomHash != '');";

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

        public List<Game> GetAllGames()
        {
            var games = new List<Game>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Games ORDER BY Title;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                games.Add(ReadGame(reader));
            return games;
        }

        public List<Game> GetFavorites()
        {
            var games = new List<Game>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Games WHERE IsFavorite = 1 ORDER BY Title;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                games.Add(ReadGame(reader));
            return games;
        }

        public List<Game> GetRecentlyPlayed()
        {
            var games = new List<Game>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM Games
                WHERE LastPlayed IS NOT NULL
                ORDER BY LastPlayed DESC
                LIMIT 20;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                games.Add(ReadGame(reader));
            return games;
        }

        public List<Game> GetByCollection(string collection)
        {
            var games = new List<Game>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Games WHERE Collection = $col ORDER BY Title;";
            cmd.Parameters.AddWithValue("$col", collection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                games.Add(ReadGame(reader));
            return games;
        }

        private Game ReadGame(SqliteDataReader reader)
        {
            return new Game
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                Console = reader.GetString(2),
                Manufacturer = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Year = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                RomPath = reader.IsDBNull(5) ? "" : reader.GetString(5),
                RomHash = reader.IsDBNull(6) ? "" : reader.GetString(6),
                CoverArtPath = reader.IsDBNull(7) ? "" : reader.GetString(7),
                BackgroundColor = reader.IsDBNull(8) ? "#1F1F21" : reader.GetString(8),
                AccentColor = reader.IsDBNull(9) ? "#E03535" : reader.GetString(9),
                PlayCount = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                SaveCount = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                IsFavorite = reader.IsDBNull(12) ? false : reader.GetInt32(12) == 1,
                Rating = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                Collection = reader.IsDBNull(14) ? "" : reader.GetString(14),
                LastPlayed = reader.IsDBNull(15) ? null :
                                  DateTime.TryParse(reader.GetString(15), out var dt) ? dt : null,
            };
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
            return reader.Read() ? ReadGame(reader) : null;
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