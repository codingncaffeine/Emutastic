using System;
using System.Collections.Generic;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// Pure-static diff logic for friend activity polling. Takes a list of
    /// recent achievements from RA's API and a cursor (the last-seen ISO-8601
    /// date string) and returns the subset that's strictly newer than the
    /// cursor, plus the new cursor value.
    ///
    /// Lifted to a static class so it can be exercised from a debug menu
    /// without a test project (the codebase doesn't have one). Pure inputs,
    /// pure outputs, no time-of-day side effects — feed it canned data.
    ///
    /// Date comparison is string-lex on ISO-8601 (`yyyy-MM-dd HH:mm:ss`).
    /// RA returns timestamps in that exact format so lex ordering matches
    /// chronological ordering. No DateTime.Now anywhere — local clock skew
    /// can't affect the diff.
    /// </summary>
    internal static class FriendDiff
    {
        public sealed record Result(
            List<RAUserRecentAchievement> NewUnlocks,
            string NewCursor);

        /// <summary>
        /// Returns unlocks strictly newer than <paramref name="lastSeenIso"/>
        /// (lex compare on ISO-8601). The result's <c>NewCursor</c> is the
        /// max Date across <paramref name="recent"/> — advance LastSeenUnlockDate
        /// to this even when the returned NewUnlocks list is filtered down
        /// to zero (e.g. hardcore-only mode), so we don't re-evaluate
        /// already-seen entries forever.
        ///
        /// Empty cursor input acts as a seed: returns empty NewUnlocks but
        /// still computes NewCursor from the input. Callers in "JustAdded"
        /// mode use this to baseline without firing notifications.
        /// </summary>
        public static Result Compute(
            IReadOnlyList<RAUserRecentAchievement> recent,
            string lastSeenIso,
            bool hardcoreOnly = false)
        {
            if (recent == null || recent.Count == 0)
                return new Result(new List<RAUserRecentAchievement>(), lastSeenIso ?? "");

            string cursor = lastSeenIso ?? "";
            var newUnlocks = new List<RAUserRecentAchievement>();
            string maxDate = cursor;

            foreach (var a in recent)
            {
                if (string.IsNullOrEmpty(a.Date)) continue;

                // Track the max Date for cursor advancement, regardless of
                // hardcore filter — see method comment.
                if (string.Compare(a.Date, maxDate, StringComparison.Ordinal) > 0)
                    maxDate = a.Date;

                // Cursor compare for diff inclusion
                if (string.Compare(a.Date, cursor, StringComparison.Ordinal) <= 0)
                    continue;

                if (hardcoreOnly && a.HardcoreMode == 0)
                    continue;

                newUnlocks.Add(a);
            }

            return new Result(newUnlocks, maxDate);
        }
    }
}
