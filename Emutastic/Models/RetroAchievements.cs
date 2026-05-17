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
}
