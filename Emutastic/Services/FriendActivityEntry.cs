using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// One row of the in-memory friend-activity feed: a friend's recent
    /// achievement unlock with the friend's identity attached. Lives only
    /// in <see cref="FriendService.RecentActivity"/>, capped to N entries,
    /// oldest dropped. Not persisted — feed rebuilds on next poll cycle
    /// after a restart.
    /// </summary>
    public sealed class FriendActivityEntry
    {
        public int    FriendUserId { get; set; }
        public string FriendUsername { get; set; } = "";
        public string FriendAvatarUrl { get; set; } = "";
        public RAUserRecentAchievement Unlock { get; set; } = new();
    }
}
