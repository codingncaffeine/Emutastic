using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emutastic.Models
{
    // POCOs matching the RetroAchievements web API response shapes from
    // https://api-docs.retroachievements.org/. The API accepts both PascalCase
    // and camelCase property names; deserialization uses
    // PropertyNameCaseInsensitive=true so either survives. JsonPropertyName
    // attributes pin the canonical PascalCase form for write paths.

    /// <summary>
    /// Response of GET API_GetGameProgression.php — no user required. Carries
    /// the new "time to beat / master" medians plus per-achievement community
    /// stats (median time to unlock, true ratio = rarity-weighted points,
    /// unlock counts). Sample-size fields let callers gate display on
    /// statistical confidence.
    /// </summary>
    public sealed class RAProgression
    {
        [JsonPropertyName("ID")]                          public int Id { get; set; }
        [JsonPropertyName("Title")]                       public string Title { get; set; } = "";
        [JsonPropertyName("ConsoleID")]                   public int ConsoleId { get; set; }
        [JsonPropertyName("ConsoleName")]                 public string ConsoleName { get; set; } = "";
        [JsonPropertyName("ImageIcon")]                   public string ImageIcon { get; set; } = "";
        [JsonPropertyName("NumDistinctPlayers")]          public int NumDistinctPlayers { get; set; }
        [JsonPropertyName("NumAchievements")]             public int NumAchievements { get; set; }
        // Seconds. Nullable because low-coverage games omit them.
        [JsonPropertyName("MedianTimeToBeat")]            public int? MedianTimeToBeat { get; set; }
        [JsonPropertyName("MedianTimeToBeatHardcore")]    public int? MedianTimeToBeatHardcore { get; set; }
        [JsonPropertyName("MedianTimeToComplete")]        public int? MedianTimeToComplete { get; set; }
        [JsonPropertyName("MedianTimeToMaster")]          public int? MedianTimeToMaster { get; set; }
        // Sample sizes — gate display on >= 20 to avoid n=2 medians.
        [JsonPropertyName("TimesUsedInBeatMedian")]       public int TimesUsedInBeatMedian { get; set; }
        [JsonPropertyName("TimesUsedInCompletionMedian")] public int TimesUsedInCompletionMedian { get; set; }
        [JsonPropertyName("TimesUsedInMasteryMedian")]    public int TimesUsedInMasteryMedian { get; set; }
        [JsonPropertyName("Achievements")]                public List<RAAchievement> Achievements { get; set; } = new();
    }

    public sealed class RAAchievement
    {
        [JsonPropertyName("ID")]                       public int Id { get; set; }
        [JsonPropertyName("Title")]                    public string Title { get; set; } = "";
        [JsonPropertyName("Description")]              public string Description { get; set; } = "";
        [JsonPropertyName("Points")]                   public int Points { get; set; }
        [JsonPropertyName("TrueRatio")]                public int TrueRatio { get; set; }
        // "progression" | "win_condition" | "missable" | null
        [JsonPropertyName("Type")]                     public string? Type { get; set; }
        [JsonPropertyName("BadgeName")]                public string BadgeName { get; set; } = "";
        [JsonPropertyName("NumAwarded")]               public int NumAwarded { get; set; }
        [JsonPropertyName("NumAwardedHardcore")]       public int NumAwardedHardcore { get; set; }
        [JsonPropertyName("MedianTimeToUnlock")]       public int? MedianTimeToUnlock { get; set; }
        [JsonPropertyName("MedianTimeToUnlockHardcore")] public int? MedianTimeToUnlockHardcore { get; set; }
        [JsonPropertyName("TimesUsedInUnlockMedian")]  public int TimesUsedInUnlockMedian { get; set; }
    }

    /// <summary>
    /// Response of GET API_GetGameInfoAndUserProgress.php — the user's
    /// per-achievement unlock state. DateEarned / DateEarnedHardcore are only
    /// present when the user has the achievement; absence means not earned.
    /// </summary>
    public sealed class RAUserProgress
    {
        [JsonPropertyName("ID")]                       public int Id { get; set; }
        [JsonPropertyName("Title")]                    public string Title { get; set; } = "";
        [JsonPropertyName("NumAchievements")]          public int NumAchievements { get; set; }
        [JsonPropertyName("NumAwardedToUser")]         public int NumAwardedToUser { get; set; }
        [JsonPropertyName("NumAwardedToUserHardcore")] public int NumAwardedToUserHardcore { get; set; }
        [JsonPropertyName("UserCompletion")]           public string UserCompletion { get; set; } = "";          // "12.34%"
        [JsonPropertyName("UserCompletionHardcore")]   public string UserCompletionHardcore { get; set; } = "";
        [JsonPropertyName("UserTotalPlaytime")]        public int UserTotalPlaytime { get; set; }                // seconds
        [JsonPropertyName("HighestAwardKind")]         public string? HighestAwardKind { get; set; }             // "beaten" / "completed" / "mastered"
        [JsonPropertyName("HighestAwardDate")]         public string? HighestAwardDate { get; set; }
        // Keyed by achievement ID (stringified).
        [JsonPropertyName("Achievements")]             public Dictionary<string, RAUserAchievement> Achievements { get; set; } = new();
    }

    /// <summary>
    /// Snapshot of live in-game progress collected from rcheevos's
    /// ACHIEVEMENT_PROGRESS_INDICATOR_UPDATE events during a play session.
    /// Persisted once at emulator close so the detail card can show actual
    /// "you're 73% of the way there" progress instead of community-median
    /// proxies. Keyed by achievement ID.
    /// </summary>
    public sealed class RALiveProgress
    {
        [JsonPropertyName("Hardcore")]    public bool Hardcore { get; set; }
        [JsonPropertyName("Achievements")] public Dictionary<int, RALiveAchievementProgress> Achievements { get; set; } = new();
    }

    public sealed class RALiveAchievementProgress
    {
        [JsonPropertyName("Percent")]      public float Percent { get; set; }       // 0..100
        [JsonPropertyName("ProgressText")] public string ProgressText { get; set; } = "";  // e.g. "3 of 5"
    }

    public sealed class RAUserAchievement
    {
        [JsonPropertyName("ID")]                  public int Id { get; set; }
        [JsonPropertyName("Title")]               public string Title { get; set; } = "";
        [JsonPropertyName("Description")]         public string Description { get; set; } = "";
        [JsonPropertyName("Points")]              public int Points { get; set; }
        [JsonPropertyName("TrueRatio")]           public int TrueRatio { get; set; }
        [JsonPropertyName("Type")]                public string? Type { get; set; }
        [JsonPropertyName("NumAwarded")]          public int NumAwarded { get; set; }
        [JsonPropertyName("NumAwardedHardcore")]  public int NumAwardedHardcore { get; set; }
        [JsonPropertyName("BadgeName")]           public string BadgeName { get; set; } = "";
        [JsonPropertyName("DateEarned")]          public string? DateEarned { get; set; }
        [JsonPropertyName("DateEarnedHardcore")]  public string? DateEarnedHardcore { get; set; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Achievements-tab response shapes
    // POCOs for the 24 web-API endpoints called by the dedicated Achievements
    // tab. Field types follow what the API actually returns; everything is
    // case-insensitive at deserialize time. Optional fields are nullable.
    // ═════════════════════════════════════════════════════════════════════════

    // ── #29 GetUserProfile ───────────────────────────────────────────────────
    public sealed class RAUserProfile
    {
        [JsonPropertyName("User")]                public string User { get; set; } = "";
        [JsonPropertyName("UserPic")]             public string? UserPic { get; set; }
        [JsonPropertyName("MemberSince")]         public string? MemberSince { get; set; }
        [JsonPropertyName("RichPresenceMsg")]     public string? RichPresenceMsg { get; set; }
        [JsonPropertyName("LastGameID")]          public int LastGameId { get; set; }
        [JsonPropertyName("ContribCount")]        public int ContribCount { get; set; }
        [JsonPropertyName("ContribYield")]        public int ContribYield { get; set; }
        [JsonPropertyName("TotalPoints")]         public int TotalPoints { get; set; }
        [JsonPropertyName("TotalSoftcorePoints")] public int TotalSoftcorePoints { get; set; }
        [JsonPropertyName("TotalTruePoints")]     public int TotalTruePoints { get; set; }
        [JsonPropertyName("Permissions")]         public int Permissions { get; set; }
        [JsonPropertyName("Untracked")]           public int Untracked { get; set; }
        [JsonPropertyName("ID")]                  public int Id { get; set; }
        [JsonPropertyName("UserWallActive")]      public int UserWallActive { get; set; }
        [JsonPropertyName("Motto")]               public string? Motto { get; set; }
    }

    // ── #28 GetUserPoints ────────────────────────────────────────────────────
    public sealed class RAUserPoints
    {
        [JsonPropertyName("Points")]          public int Points { get; set; }
        [JsonPropertyName("SoftcorePoints")]  public int SoftcorePoints { get; set; }
    }

    // ── #31 GetUserRecentAchievements (returns array) ────────────────────────
    public sealed class RAUserRecentAchievement
    {
        [JsonPropertyName("Date")]            public string Date { get; set; } = "";
        [JsonPropertyName("HardcoreMode")]    public int HardcoreMode { get; set; }
        [JsonPropertyName("AchievementID")]   public int AchievementId { get; set; }
        [JsonPropertyName("Title")]           public string Title { get; set; } = "";
        [JsonPropertyName("Description")]     public string Description { get; set; } = "";
        [JsonPropertyName("BadgeName")]       public string BadgeName { get; set; } = "";
        [JsonPropertyName("Points")]          public int Points { get; set; }
        [JsonPropertyName("TrueRatio")]       public int TrueRatio { get; set; }
        [JsonPropertyName("Type")]            public string? Type { get; set; }
        [JsonPropertyName("Author")]          public string? Author { get; set; }
        [JsonPropertyName("GameTitle")]       public string GameTitle { get; set; } = "";
        [JsonPropertyName("GameIcon")]        public string? GameIcon { get; set; }
        [JsonPropertyName("GameID")]          public int GameId { get; set; }
        [JsonPropertyName("ConsoleName")]     public string ConsoleName { get; set; } = "";
        [JsonPropertyName("BadgeURL")]        public string? BadgeUrl { get; set; }
        [JsonPropertyName("GameURL")]         public string? GameUrl { get; set; }
    }

    // ── #22 GetUserAwards ────────────────────────────────────────────────────
    public sealed class RAUserAwards
    {
        [JsonPropertyName("TotalAwardsCount")]               public int TotalAwardsCount { get; set; }
        [JsonPropertyName("HiddenAwardsCount")]              public int HiddenAwardsCount { get; set; }
        [JsonPropertyName("MasteryAwardsCount")]             public int MasteryAwardsCount { get; set; }
        [JsonPropertyName("CompletionAwardsCount")]          public int CompletionAwardsCount { get; set; }
        [JsonPropertyName("BeatenHardcoreAwardsCount")]      public int BeatenHardcoreAwardsCount { get; set; }
        [JsonPropertyName("BeatenSoftcoreAwardsCount")]      public int BeatenSoftcoreAwardsCount { get; set; }
        [JsonPropertyName("EventAwardsCount")]               public int EventAwardsCount { get; set; }
        [JsonPropertyName("SiteAwardsCount")]                public int SiteAwardsCount { get; set; }
        [JsonPropertyName("VisibleUserAwards")]              public List<RAVisibleAward> VisibleUserAwards { get; set; } = new();
    }

    public sealed class RAVisibleAward
    {
        [JsonPropertyName("AwardedAt")]      public string AwardedAt { get; set; } = "";
        [JsonPropertyName("AwardType")]      public string AwardType { get; set; } = "";   // "Mastery/Completion", "Game Beaten", etc.
        [JsonPropertyName("AwardData")]      public int AwardData { get; set; }            // game ID for game awards
        [JsonPropertyName("AwardDataExtra")] public int AwardDataExtra { get; set; }       // 1 = hardcore, 0 = softcore
        [JsonPropertyName("DisplayOrder")]   public int DisplayOrder { get; set; }
        [JsonPropertyName("Title")]          public string? Title { get; set; }
        [JsonPropertyName("ConsoleName")]    public string? ConsoleName { get; set; }
        [JsonPropertyName("Flags")]          public int? Flags { get; set; }
        [JsonPropertyName("ImageIcon")]      public string? ImageIcon { get; set; }
    }

    // ── #25 GetUserCompletionProgress (paginated) ───────────────────────────
    public sealed class RAUserCompletionProgressResponse
    {
        [JsonPropertyName("Count")]    public int Count { get; set; }
        [JsonPropertyName("Total")]    public int Total { get; set; }
        [JsonPropertyName("Results")]  public List<RAUserCompletionProgressItem> Results { get; set; } = new();
    }

    public sealed class RAUserCompletionProgressItem
    {
        [JsonPropertyName("GameID")]                public int GameId { get; set; }
        [JsonPropertyName("Title")]                 public string Title { get; set; } = "";
        [JsonPropertyName("ImageIcon")]             public string? ImageIcon { get; set; }
        [JsonPropertyName("ConsoleID")]             public int ConsoleId { get; set; }
        [JsonPropertyName("ConsoleName")]           public string ConsoleName { get; set; } = "";
        [JsonPropertyName("MaxPossible")]           public int MaxPossible { get; set; }
        [JsonPropertyName("NumAwarded")]            public int NumAwarded { get; set; }
        [JsonPropertyName("NumAwardedHardcore")]    public int NumAwardedHardcore { get; set; }
        [JsonPropertyName("MostRecentAwardedDate")] public string? MostRecentAwardedDate { get; set; }
        [JsonPropertyName("HighestAwardKind")]      public string? HighestAwardKind { get; set; }  // "mastered", "completed", "beaten-hardcore", "beaten-softcore"
        [JsonPropertyName("HighestAwardDate")]      public string? HighestAwardDate { get; set; }
    }

    // ── API_GetUsersIFollow / API_GetUsersFollowingMe ────────────────────
    // RA's follow-graph endpoints. Both are paginated (Count + Total +
    // Results) and self-only (you can only query YOUR own follow lists
    // via the public web API key). Returns sparse identity data —
    // avatar / motto / last-online require separate per-user calls.
    //
    // Per feedback_ra_model_strictness.md: RA returns 0/1 for booleans, so
    // IsFollowingMe / AmIFollowing are modeled as int (NOT bool) — a
    // mistyped field nukes the whole deserialize via System.Text.Json
    // strictness. Points fields are int? in case RA ever returns null
    // for unranked users.
    public sealed class RAUsersIFollowResponse
    {
        [JsonPropertyName("Count")]   public int Count { get; set; }
        [JsonPropertyName("Total")]   public int Total { get; set; }
        [JsonPropertyName("Results")] public List<RAUsersIFollowEntry> Results { get; set; } = new();
    }

    public sealed class RAUsersIFollowEntry
    {
        [JsonPropertyName("User")]           public string  User           { get; set; } = "";
        [JsonPropertyName("ULID")]           public string? Ulid           { get; set; }
        [JsonPropertyName("Points")]         public int?    Points         { get; set; }
        [JsonPropertyName("PointsSoftcore")] public int?    PointsSoftcore { get; set; }
        // True when this user (whom YOU follow) also follows YOU back.
        // NOTE: this endpoint returns a JSON boolean (false/true), NOT the
        // 0/1 int that some other RA endpoints use. Verified from live
        // response payload — modeling as bool. Don't blindly trust
        // feedback_ra_model_strictness.md for this field.
        [JsonPropertyName("IsFollowingMe")]  public bool    IsFollowingMe  { get; set; }
    }

    public sealed class RAUsersFollowingMeResponse
    {
        [JsonPropertyName("Count")]   public int Count { get; set; }
        [JsonPropertyName("Total")]   public int Total { get; set; }
        [JsonPropertyName("Results")] public List<RAUsersFollowingMeEntry> Results { get; set; } = new();
    }

    public sealed class RAUsersFollowingMeEntry
    {
        [JsonPropertyName("User")]           public string  User           { get; set; } = "";
        [JsonPropertyName("ULID")]           public string? Ulid           { get; set; }
        [JsonPropertyName("Points")]         public int?    Points         { get; set; }
        [JsonPropertyName("PointsSoftcore")] public int?    PointsSoftcore { get; set; }
        // True when YOU follow this user back (the mirror of
        // IsFollowingMe on the IFollow endpoint). JSON boolean, NOT 0/1 —
        // same as the sibling endpoint.
        [JsonPropertyName("AmIFollowing")]   public bool    AmIFollowing   { get; set; }
    }

    // ── #32 GetUserRecentlyPlayedGames ──────────────────────────────────────
    public sealed class RARecentlyPlayedGame
    {
        [JsonPropertyName("GameID")]              public int GameId { get; set; }
        [JsonPropertyName("ConsoleID")]           public int ConsoleId { get; set; }
        [JsonPropertyName("ConsoleName")]         public string ConsoleName { get; set; } = "";
        [JsonPropertyName("Title")]               public string Title { get; set; } = "";
        [JsonPropertyName("ImageIcon")]           public string? ImageIcon { get; set; }
        [JsonPropertyName("ImageTitle")]          public string? ImageTitle { get; set; }
        [JsonPropertyName("ImageIngame")]         public string? ImageIngame { get; set; }
        [JsonPropertyName("ImageBoxArt")]         public string? ImageBoxArt { get; set; }
        [JsonPropertyName("LastPlayed")]          public string? LastPlayed { get; set; }
        [JsonPropertyName("NumPossibleAchievements")] public int NumPossibleAchievements { get; set; }
        [JsonPropertyName("PossibleScore")]       public int PossibleScore { get; set; }
        [JsonPropertyName("NumAchieved")]         public int NumAchieved { get; set; }
        [JsonPropertyName("ScoreAchieved")]       public int ScoreAchieved { get; set; }
        [JsonPropertyName("NumAchievedHardcore")] public int NumAchievedHardcore { get; set; }
        [JsonPropertyName("ScoreAchievedHardcore")] public int ScoreAchievedHardcore { get; set; }
    }

    // ── #35 GetUserWantToPlayList (paginated) ───────────────────────────────
    public sealed class RAWantToPlayResponse
    {
        [JsonPropertyName("Count")]   public int Count { get; set; }
        [JsonPropertyName("Total")]   public int Total { get; set; }
        [JsonPropertyName("Results")] public List<RAWantToPlayItem> Results { get; set; } = new();
    }

    public sealed class RAWantToPlayItem
    {
        [JsonPropertyName("ID")]                     public int Id { get; set; }
        [JsonPropertyName("Title")]                  public string Title { get; set; } = "";
        [JsonPropertyName("ConsoleID")]              public int ConsoleId { get; set; }
        [JsonPropertyName("ConsoleName")]            public string ConsoleName { get; set; } = "";
        [JsonPropertyName("ImageIcon")]              public string? ImageIcon { get; set; }
        [JsonPropertyName("PointsTotal")]            public int PointsTotal { get; set; }
        [JsonPropertyName("AchievementsPublished")]  public int AchievementsPublished { get; set; }
    }

    // ── #3 GetAchievementOfTheWeek ──────────────────────────────────────────
    public sealed class RAAchievementOfTheWeek
    {
        [JsonPropertyName("Achievement")]         public RAAchievementSummary? Achievement { get; set; }
        [JsonPropertyName("Console")]             public RAConsoleSummary? Console { get; set; }
        [JsonPropertyName("ForumTopic")]          public RAForumTopicSummary? ForumTopic { get; set; }
        [JsonPropertyName("Game")]                public RAGameSummary? Game { get; set; }
        [JsonPropertyName("StartAt")]             public string? StartAt { get; set; }
        [JsonPropertyName("TotalPlayers")]        public int TotalPlayers { get; set; }
        [JsonPropertyName("Unlocks")]             public List<RAAchievementOfTheWeekUnlock> Unlocks { get; set; } = new();
        [JsonPropertyName("UnlocksCount")]        public int UnlocksCount { get; set; }
        [JsonPropertyName("UnlocksHardcoreCount")] public int UnlocksHardcoreCount { get; set; }
    }

    public sealed class RAAchievementSummary
    {
        [JsonPropertyName("ID")]          public int Id { get; set; }
        [JsonPropertyName("Title")]       public string Title { get; set; } = "";
        [JsonPropertyName("Description")] public string Description { get; set; } = "";
        [JsonPropertyName("Points")]      public int Points { get; set; }
        [JsonPropertyName("TrueRatio")]   public int TrueRatio { get; set; }
        [JsonPropertyName("Type")]        public string? Type { get; set; }
        [JsonPropertyName("Author")]      public string? Author { get; set; }
        [JsonPropertyName("BadgeName")]   public string BadgeName { get; set; } = "";
        [JsonPropertyName("BadgeURL")]    public string? BadgeUrl { get; set; }
    }

    public sealed class RAConsoleSummary
    {
        [JsonPropertyName("ID")]    public int Id { get; set; }
        [JsonPropertyName("Title")] public string Title { get; set; } = "";
    }

    public sealed class RAForumTopicSummary
    {
        [JsonPropertyName("ID")]  public int Id { get; set; }
    }

    public sealed class RAGameSummary
    {
        [JsonPropertyName("ID")]        public int Id { get; set; }
        [JsonPropertyName("Title")]     public string Title { get; set; } = "";
        [JsonPropertyName("ImageIcon")] public string? ImageIcon { get; set; }
    }

    public sealed class RAAchievementOfTheWeekUnlock
    {
        [JsonPropertyName("User")]         public string User { get; set; } = "";
        [JsonPropertyName("RAPoints")]     public int RaPoints { get; set; }
        [JsonPropertyName("DateAwarded")]  public string DateAwarded { get; set; } = "";
        [JsonPropertyName("HardcoreMode")] public int HardcoreMode { get; set; }
    }

    // ── #20 GetRecentGameAwards (paginated) ─────────────────────────────────
    public sealed class RARecentGameAwardsResponse
    {
        [JsonPropertyName("Count")]   public int Count { get; set; }
        [JsonPropertyName("Total")]   public int Total { get; set; }
        [JsonPropertyName("Results")] public List<RARecentGameAward> Results { get; set; } = new();
    }

    public sealed class RARecentGameAward
    {
        [JsonPropertyName("User")]            public string User { get; set; } = "";
        [JsonPropertyName("AwardKind")]       public string AwardKind { get; set; } = "";  // "beaten-softcore", "beaten-hardcore", "completed", "mastered"
        [JsonPropertyName("AwardDate")]       public string AwardDate { get; set; } = "";
        [JsonPropertyName("GameID")]          public int GameId { get; set; }
        [JsonPropertyName("GameTitle")]       public string GameTitle { get; set; } = "";
        [JsonPropertyName("ConsoleID")]       public int ConsoleId { get; set; }
        [JsonPropertyName("ConsoleName")]     public string ConsoleName { get; set; } = "";
    }

    // ── #21 GetTopTenUsers (array) ──────────────────────────────────────────
    public sealed class RATopTenUser
    {
        [JsonPropertyName("1")]    public string User { get; set; } = "";
        [JsonPropertyName("2")]    public int Points { get; set; }
        [JsonPropertyName("3")]    public int RetroPoints { get; set; }
        // The API returns this as a positional array per row; deserializer
        // wires "1"/"2"/"3" because the inner shape is JSON-array-style.
    }

    // ── #5 GetAchievementsEarnedBetween & #6 GetAchievementsEarnedOnDay
    //     (both return arrays of the same shape) ───────────────────────────
    public sealed class RAEarnedAchievement
    {
        [JsonPropertyName("Date")]          public string Date { get; set; } = "";
        [JsonPropertyName("HardcoreMode")]  public int HardcoreMode { get; set; }
        [JsonPropertyName("AchievementID")] public int AchievementId { get; set; }
        [JsonPropertyName("Title")]         public string Title { get; set; } = "";
        [JsonPropertyName("Description")]   public string Description { get; set; } = "";
        [JsonPropertyName("BadgeName")]     public string BadgeName { get; set; } = "";
        [JsonPropertyName("Points")]        public int Points { get; set; }
        [JsonPropertyName("TrueRatio")]     public int TrueRatio { get; set; }
        [JsonPropertyName("Author")]        public string? Author { get; set; }
        [JsonPropertyName("GameTitle")]     public string GameTitle { get; set; } = "";
        [JsonPropertyName("GameID")]        public int GameId { get; set; }
        [JsonPropertyName("ConsoleName")]   public string ConsoleName { get; set; } = "";
    }

    // ── Support: #1, #10, #12, #15, #18 ─────────────────────────────────────

    // #1 GetAchievementCount — just an array of achievement IDs
    public sealed class RAAchievementCount
    {
        [JsonPropertyName("GameID")]         public int GameId { get; set; }
        [JsonPropertyName("AchievementIDs")] public List<int> AchievementIds { get; set; } = new();
    }

    // #10 GetConsoleIDs — list
    public sealed class RAConsole
    {
        [JsonPropertyName("ID")]      public int Id { get; set; }
        [JsonPropertyName("Name")]    public string Name { get; set; } = "";
        [JsonPropertyName("IconURL")] public string? IconUrl { get; set; }
        [JsonPropertyName("Active")]  public bool Active { get; set; }
        [JsonPropertyName("IsGameSystem")] public bool IsGameSystem { get; set; }
    }

    // #12 GetGameHashes
    public sealed class RAGameHashesResponse
    {
        [JsonPropertyName("Results")] public List<RAGameHash> Results { get; set; } = new();
    }

    public sealed class RAGameHash
    {
        [JsonPropertyName("MD5")]     public string Md5 { get; set; } = "";
        [JsonPropertyName("Name")]    public string Name { get; set; } = "";
        [JsonPropertyName("Labels")]  public List<string> Labels { get; set; } = new();
        [JsonPropertyName("PatchUrl")] public string? PatchUrl { get; set; }
    }

    // #15 GetGameList (paginated, per console)
    public sealed class RAGameListItem
    {
        [JsonPropertyName("Title")]              public string Title { get; set; } = "";
        [JsonPropertyName("ID")]                 public int Id { get; set; }
        [JsonPropertyName("ConsoleID")]          public int ConsoleId { get; set; }
        [JsonPropertyName("ConsoleName")]        public string ConsoleName { get; set; } = "";
        [JsonPropertyName("ImageIcon")]          public string? ImageIcon { get; set; }
        [JsonPropertyName("NumAchievements")]    public int NumAchievements { get; set; }
        [JsonPropertyName("NumLeaderboards")]    public int NumLeaderboards { get; set; }
        [JsonPropertyName("Points")]             public int Points { get; set; }
        [JsonPropertyName("DateModified")]       public string? DateModified { get; set; }
        [JsonPropertyName("ForumTopicID")]       public int? ForumTopicId { get; set; }
        [JsonPropertyName("Hashes")]             public List<string> Hashes { get; set; } = new();
    }

    // #18 GetGame — minimal game record
    public sealed class RAGameMinimal
    {
        [JsonPropertyName("Title")]            public string Title { get; set; } = "";
        [JsonPropertyName("ForumTopicID")]     public int? ForumTopicId { get; set; }
        [JsonPropertyName("ConsoleID")]        public int ConsoleId { get; set; }
        [JsonPropertyName("ConsoleName")]      public string ConsoleName { get; set; } = "";
        [JsonPropertyName("Flags")]            public int Flags { get; set; }
        [JsonPropertyName("ImageIcon")]        public string? ImageIcon { get; set; }
        [JsonPropertyName("GameIcon")]         public string? GameIcon { get; set; }
        [JsonPropertyName("ImageTitle")]       public string? ImageTitle { get; set; }
        [JsonPropertyName("ImageIngame")]      public string? ImageIngame { get; set; }
        [JsonPropertyName("ImageBoxArt")]      public string? ImageBoxArt { get; set; }
        [JsonPropertyName("Publisher")]        public string? Publisher { get; set; }
        [JsonPropertyName("Developer")]        public string? Developer { get; set; }
        [JsonPropertyName("Genre")]            public string? Genre { get; set; }
        [JsonPropertyName("Released")]         public string? Released { get; set; }
        [JsonPropertyName("GameTitle")]        public string? GameTitle { get; set; }
        [JsonPropertyName("Console")]          public string? Console { get; set; }
    }

    // ── #2 GetAchievementDistribution (small dict) ──────────────────────────
    // Returns { "1": 12000, "2": 8000, ... } where key = num achievements
    // and value = players with that count. Stored as Dictionary<string,int>
    // for ordered iteration by the consuming UI.
    public sealed class RAAchievementDistribution
    {
        public Dictionary<string, int> Buckets { get; set; } = new();
    }

    // ── Leaderboards (Phase 6) ──────────────────────────────────────────

    // API_GetGameLeaderboards.php — paginated list of LBs for a game.
    // Response wraps Results+Total like other paginated endpoints.
    public sealed class RAGameLeaderboardsResponse
    {
        [JsonPropertyName("Count")]   public int Count { get; set; }
        [JsonPropertyName("Total")]   public int Total { get; set; }
        [JsonPropertyName("Results")] public List<RAGameLeaderboard> Results { get; set; } = new();
    }

    public sealed class RAGameLeaderboard
    {
        [JsonPropertyName("ID")]          public int Id { get; set; }
        [JsonPropertyName("RankAsc")]     public bool RankAsc { get; set; }
        [JsonPropertyName("Title")]       public string Title { get; set; } = "";
        [JsonPropertyName("Description")] public string Description { get; set; } = "";
        [JsonPropertyName("Format")]      public string Format { get; set; } = "";    // SCORE, TIME, MILLISECS, ...
        [JsonPropertyName("TopEntry")]    public RALeaderboardEntry? TopEntry { get; set; }
    }

    // API_GetLeaderboardEntries.php — entry rows on one LB.
    public sealed class RALeaderboardEntriesResponse
    {
        [JsonPropertyName("Count")]   public int Count { get; set; }
        [JsonPropertyName("Total")]   public int Total { get; set; }
        [JsonPropertyName("Results")] public List<RALeaderboardEntry> Results { get; set; } = new();
    }

    public sealed class RALeaderboardEntry
    {
        [JsonPropertyName("User")]          public string User { get; set; } = "";
        [JsonPropertyName("Rank")]          public int Rank { get; set; }
        [JsonPropertyName("Score")]         public long Score { get; set; }
        [JsonPropertyName("FormattedScore")]public string FormattedScore { get; set; } = "";
        [JsonPropertyName("DateSubmitted")] public string DateSubmitted { get; set; } = "";
    }

    // API_GetUserGameLeaderboards.php — a user's ranks across every LB
    // for one game. The friend-rank-across-all-LBs primitive that
    // lets us avoid paging full LB entry lists.
    public sealed class RAUserGameLeaderboardsResponse
    {
        [JsonPropertyName("Count")]   public int Count { get; set; }
        [JsonPropertyName("Total")]   public int Total { get; set; }
        [JsonPropertyName("Results")] public List<RAUserGameLeaderboard> Results { get; set; } = new();
    }

    public sealed class RAUserGameLeaderboard
    {
        [JsonPropertyName("ID")]              public int Id { get; set; }
        [JsonPropertyName("Title")]           public string Title { get; set; } = "";
        [JsonPropertyName("Description")]     public string Description { get; set; } = "";
        [JsonPropertyName("Format")]          public string Format { get; set; } = "";
        [JsonPropertyName("UserEntry")]       public RALeaderboardEntry? UserEntry { get; set; }
    }

    // ── Library Spotlight (materialized) ────────────────────────────────────
    // Pre-computed cross-reference of RA Web API data with the local library
    // for the Achievements tab's "across all my games" panels. Materialized
    // once per refresh on the background thread; UI binds to it directly.

    public sealed class RALibrarySpotlight
    {
        public List<RASpotlightGame> ClosestToMastering   { get; set; } = new();
        public List<RASpotlightQuickWin> QuickWins        { get; set; } = new();
        public List<RASpotlightGame> ContinueWhereLeftOff { get; set; } = new();
        public List<RASpotlightGame> NeverStarted         { get; set; } = new();
        public List<RASpotlightGame> WishlistOwned        { get; set; } = new();
    }

    public sealed class RASpotlightGame
    {
        public int RAGameId      { get; set; }
        public int LocalGameId   { get; set; }
        public string Title      { get; set; } = "";
        public string Console    { get; set; } = "";
        public string? ImageIcon { get; set; }       // RA image path; UI prepends host
        public string? LocalArtPath { get; set; }    // Fallback when RA path is missing; used by "Never started" panel for tiles where we never pulled the RA icon
        public int NumAchieved   { get; set; }
        public int MaxPossible   { get; set; }
        public string Subtitle   { get; set; } = "";  // panel-specific blurb
    }

    public sealed class RASpotlightQuickWin
    {
        public int RAGameId           { get; set; }
        public int LocalGameId        { get; set; }
        public string GameTitle       { get; set; } = "";
        public string Console         { get; set; } = "";
        public int AchievementId      { get; set; }
        public string AchievementTitle { get; set; } = "";
        public string Description     { get; set; } = "";
        public string BadgeName       { get; set; } = "";
        public int Points             { get; set; }
        public int MedianSeconds      { get; set; }  // typical time-to-unlock from community medians
    }
}
