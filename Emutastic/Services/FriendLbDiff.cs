using System.Collections.Generic;

namespace Emutastic.Services
{
    /// <summary>
    /// Pure-static diff between a friend's previous and current LB rank
    /// states. Used by Phase 6b.2's polling-driven "friend beat your
    /// score" detection. Outputs only LBs where the friend's rank
    /// improved (smaller rank number).
    ///
    /// Mirrors <see cref="FriendDiff"/> for testability — same shape, no
    /// time-of-day side effects. Can be exercised from a debug menu
    /// without a test project.
    /// </summary>
    public static class FriendLbDiff
    {
        public sealed record Improvement(
            int LeaderboardId,
            int OldRank,
            int NewRank,
            long OldScore,
            long NewScore,
            string FormattedScore);

        /// <summary>
        /// Returns LBs where the friend's rank IMPROVED (newRank smaller
        /// than oldRank). LBs missing from <paramref name="previous"/>
        /// are NEW entries — also treated as improvements (was effectively
        /// not on the board, now is). LBs missing from <paramref name="current"/>
        /// are skipped (we don't toast disappearances).
        /// </summary>
        public static List<Improvement> Compute(
            IReadOnlyDictionary<int, FriendLbScore> previous,
            IReadOnlyDictionary<int, FriendLbScore> current)
        {
            var result = new List<Improvement>();
            if (current == null || current.Count == 0) return result;

            foreach (var kv in current)
            {
                var cur = kv.Value;
                if (cur == null || cur.Rank <= 0) continue;

                int oldRank;
                long oldScore;
                if (previous != null && previous.TryGetValue(kv.Key, out var prev))
                {
                    oldRank = prev.Rank;
                    oldScore = prev.Score;
                }
                else
                {
                    // No prior entry. Treat as a "new top-N appearance"
                    // by using rank=int.MaxValue as the prior, so any
                    // new rank counts as improvement.
                    oldRank = int.MaxValue;
                    oldScore = 0;
                }

                if (cur.Rank < oldRank)
                {
                    result.Add(new Improvement(
                        LeaderboardId: kv.Key,
                        OldRank: oldRank,
                        NewRank: cur.Rank,
                        OldScore: oldScore,
                        NewScore: cur.Score,
                        FormattedScore: cur.FormattedScore));
                }
            }
            return result;
        }
    }
}
