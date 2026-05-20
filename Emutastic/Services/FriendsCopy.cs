using System;
using System.Globalization;

namespace Emutastic.Services
{
    /// <summary>
    /// Parameterized strings for the Friends feature. Action-oriented
    /// framing — every comparison phrase names the friend first and
    /// frames the gap as something the player can act on. Stored here
    /// instead of as XAML literals because (a) the codebase has no
    /// localization infrastructure (no .resx) and (b) most strings need
    /// formatting at runtime with friend names + numbers.
    /// </summary>
    public static class FriendsCopy
    {
        // ── Compare summaries (Phase 5 will consume) ──────────────────

        public static string PointsAhead(string friend, int gap) =>
            $"{friend} is {gap:N0} pts ahead of you";

        public static string PointsBehind(string friend, int gap) =>
            $"You're {gap:N0} pts ahead of {friend}";

        public static string AchievementsAheadInGame(string friend, int gap, string game) =>
            $"{friend} is {gap} achievements ahead of you in {game}";

        // Compact summary phrases for the Compare-pane delta header. No
        // username interpolation — used when the friend's name is
        // already displayed nearby.
        public static string TheyreAheadByAch(int gap) =>
            $"They're {gap:N0} achievements ahead";

        public static string YoureAheadByAch(int gap) =>
            $"You're {gap:N0} achievements ahead";

        public static string CloseToCatchingUp(string friend, int gap, string game) =>
            $"Catch up to {friend} in {game} — only {gap} more to go";

        public static string YoureAheadInGame(string friend, int gap, string game) =>
            $"You're {gap} achievements ahead of {friend} in {game}";

        public static string FriendMasteredGame(string friend, string game) =>
            $"{friend} mastered {game}";

        // ── Activity feed (Phase 3 already consumes — kept here for reuse) ─

        public static string FriendUnlocked(string friend, string achievement, bool hardcore) =>
            hardcore
                ? $"{friend} unlocked [HC] {achievement}"
                : $"{friend} unlocked {achievement}";

        // ── Profile + status ──────────────────────────────────────────

        public static string PointsAndSoftcore(int hardcore, int softcore) =>
            $"{hardcore:N0} pts · {softcore:N0} softcore";

        public static string MemberSinceDisplay(string isoDate)
        {
            if (string.IsNullOrWhiteSpace(isoDate)) return "";
            if (DateTime.TryParseExact(isoDate, "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
                return $"Joined {dt.ToLocalTime():MMMM yyyy}";
            return "";
        }

        // ── Leaderboard (Phase 6 will consume) ────────────────────────

        public static string LbScoreToBeat(string friend, string score) =>
            $"Next: {friend} — {score}";

        public static string LbPositionInPack(int rank, int total) =>
            total == 0 ? "" : $"You're #{rank} of {total} friends";

        // Phase 6b toast formatters. Two-line: bold first line, muted
        // second line. ShowLbToast lays out separately; these are just
        // the text content.
        public static (string headline, string subline) LbTriumphYou(string friend, string lbTitle, string game, string console)
            => ($"You beat {friend}'s score on {lbTitle}", $"{game} · {console}");

        public static (string headline, string subline) LbProximity(string friend, string gap, string lbTitle, string game, string console)
            => ($"Close to {friend} — {gap} to beat on {lbTitle}", $"{game} · {console}");

        public static (string headline, string subline) LbFriendBeatYou(string friend, string lbTitle, string game, string console)
            => ($"{friend} just beat your score on {lbTitle}", $"{game} · {console}");
    }
}
