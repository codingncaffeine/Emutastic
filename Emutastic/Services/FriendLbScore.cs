using System.Text.Json.Serialization;

namespace Emutastic.Services
{
    /// <summary>
    /// One friend's rank+score on a single leaderboard. Serializable as
    /// part of FriendCacheSnapshot for polling-side diff (Phase 6b.2),
    /// also used in-memory by the game-load pre-fetch cache for the
    /// SCOREBOARD-triggered triumph/proximity check (Phase 6b.1).
    /// </summary>
    public sealed class FriendLbScore
    {
        [JsonPropertyName("lbId")]    public int LeaderboardId { get; set; }
        [JsonPropertyName("rank")]    public int Rank { get; set; }
        [JsonPropertyName("score")]   public long Score { get; set; }
        [JsonPropertyName("display")] public string FormattedScore { get; set; } = "";
    }
}
