using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emutastic.Services
{
    /// <summary>
    /// Per-friend mutable cache snapshot persisted as a JSON payload in the
    /// existing <c>ra_cache</c> SQLite table under key
    /// <c>friend:{UserId}</c>. Owner column is set to
    /// <c>friends:{loggedInUsername}</c> so on-logout wipe still works.
    ///
    /// Lives outside <c>FriendsConfiguration</c> on purpose: this struct
    /// hot-updates every poll (every 5 min). Keeping it in the config file
    /// would race with PreferencesWindow saves and burn the config's
    /// LastModified field every cycle. SQLite is the right home.
    /// </summary>
    public sealed class FriendCacheSnapshot
    {
        [JsonPropertyName("avatarUrl")]      public string AvatarUrl { get; set; } = "";
        [JsonPropertyName("motto")]          public string Motto { get; set; } = "";
        [JsonPropertyName("pointsSoftcore")] public int PointsSoftcore { get; set; }
        [JsonPropertyName("pointsHardcore")] public int PointsHardcore { get; set; }
        [JsonPropertyName("truePoints")]     public int TruePoints { get; set; }
        [JsonPropertyName("memberSince")]    public string MemberSince { get; set; } = "";
        [JsonPropertyName("lastGameTitle")]  public string LastGameTitle { get; set; } = "";
        [JsonPropertyName("lastGameId")]     public int LastGameId { get; set; }
        // Relative path like "/Images/093469.png" — combine with the
        // media host at render time. Comes from GetUserRecentlyPlayedGames.ImageIcon.
        [JsonPropertyName("lastGameImageIcon")] public string LastGameImageIcon { get; set; } = "";
        // Number of unique achievements the friend has unlocked in the
        // last 24h. Surfaces in the brief card as "X unlocks today".
        [JsonPropertyName("recentUnlock24h")] public int RecentUnlockCount24h { get; set; }
        [JsonPropertyName("richPresence")]   public string RichPresence { get; set; } = "";
        // Counter of unlocks seen during polling that the user hasn't
        // viewed yet in the UI. Resets to 0 when the brief card opens.
        [JsonPropertyName("unseenCount")]    public int UnseenUnlockCount { get; set; }
        // ISO-8601 timestamp this snapshot was written. Used for "Last
        // checked X min ago" UI surfaces.
        [JsonPropertyName("updatedAt")]      public string UpdatedAt { get; set; } = "";

        // Phase 6b.2 — friend's last-seen rank+score per LB on their
        // most-recent-played game. Diffed against the next poll's data
        // to detect "friend just passed my score" events.
        // Keyed by leaderboard_id. System.Text.Json handles int keys on
        // net8+ (stringifies during serialize, round-trips clean).
        [JsonPropertyName("lastSeenLbScores")] public Dictionary<int, FriendLbScore> LastSeenLbScores { get; set; } = new();
        // Game id whose LBs we tracked in LastSeenLbScores. When this
        // changes, the whole dict gets invalidated — old game's LB
        // entries aren't relevant to the new game.
        [JsonPropertyName("lastSeenLbGameId")] public int LastSeenLbGameId { get; set; }
    }
}
