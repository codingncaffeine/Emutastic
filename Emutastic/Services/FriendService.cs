using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Emutastic.Configuration;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// Owns the Friends list, the SQLite-backed per-friend cache, and (in
    /// Phase 3) the periodic poll loop. Phase 1 ships add/remove/refresh
    /// with manual triggering — no DispatcherTimer yet.
    ///
    /// Concrete class (no interface) to match the rest of
    /// <c>Services/</c>; only <c>IConfigurationService</c> has an interface
    /// in this codebase. Single instance lives on <c>App.Friends</c>.
    ///
    /// Threading: every public method is awaitable. Internal awaits use
    /// ConfigureAwait(false). The Friends list is exposed as
    /// <see cref="Friends"/>; readers should treat it as a snapshot —
    /// AddAsync/RemoveAsync replace the underlying List reference rather
    /// than mutating in place, so a foreach over the snapshot stays
    /// stable across concurrent edits.
    /// </summary>
    public class FriendService
    {
        private readonly IConfigurationService _config;
        private readonly DatabaseService _db;
        private readonly RetroAchievementsService _api;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        // Serializes writes against the friends list so AddAsync /
        // RemoveAsync / polling can't step on each other. Snapshot reads
        // (the Friends property) are lock-free.
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        // ── Friend-rank pre-fetch (Phase 6b.1) ─────────────────────────────
        // Per-game cache of every friend's LB ranks for the currently-active
        // game. Populated by PrefetchFriendLbRanksAsync at game load;
        // consumed by RetroAchievementsClient.LeaderboardScoreboardReceived
        // subscribers to decide which triumph/proximity toasts to fire.
        //
        // Concurrency: ConcurrentDictionary lets the emu thread (toast
        // decision) read concurrently with the background prefetch task
        // populating values without locking.
        //
        // Lifecycle: cancellable per game. EndCurrentGameLbPrefetch()
        // cancels the previous fetch and drops the cache when the game
        // changes. A SemaphoreSlim(4) caps concurrency so a 20-friend
        // list doesn't burst 20 simultaneous HTTPS calls at the RA API
        // right when the emulator is also doing core init + disk reads.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Dictionary<int, FriendLbScore>> _friendLbRanks = new();
        // Mirror cache of MY OWN rank per leaderboard on the currently-
        // active game. Needed alongside friend ranks because triumph
        // detection requires knowing "where I WAS before this submission"
        // not just where I am after — the SCOREBOARD event gives the
        // new rank only.
        //
        // ConcurrentDictionary (not Dictionary) because the prefetch task
        // populates this on a background thread while UpdateMyLbRank
        // writes from the UI thread post-submission. A whole-dict
        // replacement would race with in-place mutations.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, FriendLbScore> _myLbRanks = new();
        private int _currentLbGameId;
        private CancellationTokenSource? _lbPrefetchCts;

        /// <summary>
        /// Returns the friend's known rank+score on the given leaderboard,
        /// or null if no entry is cached. Sub-microsecond read — safe to
        /// call from the emu thread inside the SCOREBOARD handler.
        /// </summary>
        public FriendLbScore? GetFriendLbScore(int friendUserId, int leaderboardId)
        {
            if (_friendLbRanks.TryGetValue(friendUserId, out var byLb)
                && byLb.TryGetValue(leaderboardId, out var score))
                return score;
            return null;
        }

        /// <summary>Snapshot of all cached friend ranks for the current game.</summary>
        public IReadOnlyDictionary<int, Dictionary<int, FriendLbScore>> AllFriendLbRanks => _friendLbRanks;

        /// <summary>
        /// MY cached rank on a leaderboard at game-load time. Used by the
        /// SCOREBOARD triumph check ("was I below friend X before this
        /// submission?"). After the toast logic decides, MY cached rank
        /// is bumped to the new value so subsequent submissions compare
        /// against the latest.
        /// </summary>
        public FriendLbScore? GetMyLbScore(int leaderboardId)
        {
            return _myLbRanks.TryGetValue(leaderboardId, out var s) ? s : null;
        }

        /// <summary>
        /// After a successful submission (SCOREBOARD event handled), bump
        /// my cached rank to the new value so the next submission's
        /// triumph check compares against the latest baseline rather
        /// than the game-load baseline.
        /// </summary>
        public void UpdateMyLbRank(int leaderboardId, int newRank, string formattedScore)
        {
            // Per-key write on ConcurrentDictionary — safe against the
            // background prefetch's per-key writes. (The whole-dict
            // replacement we used in an earlier draft would have raced.)
            _myLbRanks[leaderboardId] = new FriendLbScore
            {
                LeaderboardId = leaderboardId,
                Rank = newRank,
                FormattedScore = formattedScore,
            };
        }

        /// <summary>
        /// Fires when a game with LBs is loaded. Cancels any in-flight
        /// previous fetch, clears the rank cache, and kicks a background
        /// task that calls GetUserGameLeaderboardsAsync per friend with
        /// concurrency capped at 4 (polite to RA, doesn't burst).
        /// Fire-and-forget — the emulator doesn't wait on this.
        /// </summary>
        public void StartFriendLbPrefetch(int raGameId)
        {
            if (raGameId <= 0) return;
            if (_currentLbGameId == raGameId && _friendLbRanks.Count > 0) return; // same game, already cached

            try { _lbPrefetchCts?.Cancel(); _lbPrefetchCts?.Dispose(); } catch { }
            _lbPrefetchCts = new CancellationTokenSource();
            var token = _lbPrefetchCts.Token;
            _friendLbRanks.Clear();
            _myLbRanks.Clear();
            _currentLbGameId = raGameId;

            string? myUsername = null;
            try { myUsername = _config.GetRetroAchievementsConfiguration()?.Username; } catch { }
            var friends = Friends.Where(f => !f.IsInvalid).ToArray();
            if (friends.Length == 0 && string.IsNullOrWhiteSpace(myUsername)) return;

            _ = Task.Run(async () =>
            {
                using var sem = new SemaphoreSlim(4);

                // My own rank pre-fetch (parallel with friends).
                Task? myTask = null;
                if (!string.IsNullOrWhiteSpace(myUsername))
                {
                    myTask = Task.Run(async () =>
                    {
                        await sem.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            if (token.IsCancellationRequested) return;
                            var boards = await _api.GetUserGameLeaderboardsAsync(
                                myUsername!, raGameId, token).ConfigureAwait(false);
                            // Per-key writes so this can race-coexist with
                            // any UpdateMyLbRank UI-thread writes that
                            // sneak in mid-prefetch.
                            foreach (var b in boards)
                            {
                                if (b.UserEntry == null) continue;
                                _myLbRanks[b.Id] = new FriendLbScore
                                {
                                    LeaderboardId = b.Id,
                                    Rank          = b.UserEntry.Rank,
                                    Score         = b.UserEntry.Score,
                                    FormattedScore = b.UserEntry.FormattedScore,
                                };
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { RaLog.Write($"[LbPrefetch] me ({myUsername}): {ex.Message}"); }
                        finally { sem.Release(); }
                    }, token);
                }

                var tasks = friends.Select(async f =>
                {
                    await sem.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (token.IsCancellationRequested) return;
                        var boards = await _api.GetUserGameLeaderboardsAsync(
                            f.Username, raGameId, token).ConfigureAwait(false);
                        var byLb = new Dictionary<int, FriendLbScore>(boards.Count);
                        foreach (var b in boards)
                        {
                            if (b.UserEntry == null) continue;
                            byLb[b.Id] = new FriendLbScore
                            {
                                LeaderboardId = b.Id,
                                Rank          = b.UserEntry.Rank,
                                Score         = b.UserEntry.Score,
                                FormattedScore = b.UserEntry.FormattedScore,
                            };
                        }
                        _friendLbRanks[f.UserId] = byLb;
                    }
                    catch (OperationCanceledException) { /* game changed */ }
                    catch (Exception ex)
                    {
                        RaLog.Write($"[LbPrefetch] {f.Username}: {ex.Message}");
                    }
                    finally { sem.Release(); }
                }).ToArray();

                try
                {
                    if (myTask != null) await myTask.ConfigureAwait(false);
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }

                if (!token.IsCancellationRequested)
                    RaLog.Write($"[LbPrefetch] complete game={raGameId} friends={friends.Length} cachedCount={_friendLbRanks.Count} myLbs={_myLbRanks.Count}");
            }, token);
        }

        /// <summary>
        /// Called on game close or window close. Cancels the in-flight
        /// prefetch (if any) and clears the cache so we don't hold stale
        /// rank data for a game we're no longer playing.
        /// </summary>
        public void EndCurrentGameLbPrefetch()
        {
            try { _lbPrefetchCts?.Cancel(); _lbPrefetchCts?.Dispose(); } catch { }
            _lbPrefetchCts = null;
            _friendLbRanks.Clear();
            _myLbRanks.Clear();
            _currentLbGameId = 0;
        }

        // ── Polling (Phase 3) ──────────────────────────────────────────────
        // DispatcherTimer (UI thread) ticks at the configured interval;
        // the Tick handler does the network work on a Task.Run and then
        // applies state changes back via Dispatcher.BeginInvoke. Using
        // DispatcherTimer over System.Threading.Timer matches the
        // codebase's UI-affinity preference and gives a natural marshal
        // point for Tick → background → UI handoff.
        private DispatcherTimer? _pollTimer;
        private bool _polling;
        private int _pollCycleSeq;  // increments each cycle; backoff is keyed against this
        // Lets StopPolling cancel an in-flight RefreshAllAsync rather
        // than letting it run to completion on a stopped service.
        private CancellationTokenSource? _pollCts;

        // In-memory activity feed, capped at this many entries (oldest
        // dropped). Not persisted — rebuilds from next poll after restart.
        private const int RecentActivityCap = 100;
        private readonly List<FriendActivityEntry> _recentActivity = new();
        private readonly object _recentActivityLock = new();

        // Backoff: friends with ConsecutiveFailures > 0 are skipped for
        // (failures - 1) cycles, capped at 12 (≈ an hour at 5-min poll).
        // Reset to 0 on first successful poll.
        private const int MaxConsecutiveFailures = 8;
        private const int BackoffCap = 12;

        /// <summary>Read-only snapshot of the in-memory activity feed,
        /// newest first.</summary>
        public IReadOnlyList<FriendActivityEntry> RecentActivity
        {
            get
            {
                lock (_recentActivityLock) return _recentActivity.ToArray();
            }
        }

        /// <summary>
        /// Raised when one new unlock is appended to <see cref="RecentActivity"/>.
        /// Use for inline toast / pill notifications. Fires on whatever
        /// thread the poll completed on — subscribers must marshal.
        /// </summary>
        public event EventHandler<FriendActivityEntry>? ActivityReceived;

        /// <summary>
        /// Phase 6b.2 — raised when a friend's polling-side LB-rank diff
        /// shows improvement (they passed me or otherwise climbed). UI
        /// layer decides whether to fire "friend beat your score" toast.
        /// Fires on the poll thread; subscribers must marshal.
        /// </summary>
        public event EventHandler<FriendLbImprovementEvent>? FriendLbImproved;

        /// <summary>
        /// Snapshot of one friend's LB rank improvements detected on a
        /// polling cycle. Carries the friend identity + every LB where
        /// their rank improved against the last-seen snapshot.
        /// </summary>
        public sealed record FriendLbImprovementEvent(
            int FriendUserId,
            string FriendUsername,
            int GameId,
            List<FriendLbDiff.Improvement> Improvements);

        public FriendService(IConfigurationService config, DatabaseService db, RetroAchievementsService api)
        {
            _config = config;
            _db = db;
            _api = api;
        }

        /// <summary>
        /// Snapshot of the current friends list. Stable for foreach even
        /// when concurrent writes are in flight (writers replace the
        /// underlying List reference rather than mutating in place).
        ///
        /// CAVEAT: the FriendEntry instances themselves are mutated in
        /// place by <see cref="RefreshAsync"/> (Username refresh,
        /// LastSeenUnlockDate advance, failure counters). UI binding
        /// targets that observe individual entry properties can see
        /// half-written state. Phase 2 binds to immutable snapshots
        /// (DTOs) instead of the live entries for this reason.
        /// </summary>
        public IReadOnlyList<FriendEntry> Friends
        {
            get
            {
                var cfg = _config.GetFriendsConfiguration();
                return cfg.Friends.ToArray();
            }
        }

        /// <summary>
        /// Raised after a successful add/remove/refresh so UI can rebind.
        /// Fires on whatever thread the operation completed on — UI
        /// subscribers must Dispatcher.Invoke their own work.
        ///
        /// IMPORTANT: subscribers MUST unsubscribe in their Unloaded /
        /// Closed handler. <see cref="FriendService"/> outlives every
        /// window (lazy singleton on MainWindow), so a missed unsubscribe
        /// leaks the subscribing window via the delegate's target
        /// reference. Service itself isn't leaked — but the window is.
        /// </summary>
        public event EventHandler? FriendListChanged;

        // ── Lookup (for the Add Friend dialog preview) ──────────────────────

        public sealed record LookupResult(
            bool Success,
            string? Error,
            int UserId,
            string Username,
            string AvatarUrl,
            string Motto,
            int PointsSoftcore,
            int PointsHardcore,
            string MemberSince);

        /// <summary>
        /// Validates a username against RA's public profile endpoint.
        /// Returns a preview the UI can render in the Add Friend confirm
        /// step. Doesn't persist anything.
        /// </summary>
        public async Task<LookupResult> LookupAsync(string username, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username))
                return new LookupResult(false, "Username is required.", 0, "", "", "", 0, 0, "");

            try
            {
                var profile = await _api.GetUserProfileAsync(username, ct).ConfigureAwait(false);
                if (profile == null || profile.Id == 0)
                    return new LookupResult(false, "Username not found on RetroAchievements.", 0, "", "", "", 0, 0, "");

                return new LookupResult(
                    Success:        true,
                    Error:          null,
                    UserId:         profile.Id,
                    Username:       string.IsNullOrEmpty(profile.User) ? username : profile.User,
                    AvatarUrl:      BuildAvatarUrl(profile.UserPic),
                    Motto:          profile.Motto ?? "",
                    PointsSoftcore: profile.TotalSoftcorePoints,
                    PointsHardcore: profile.TotalPoints,
                    MemberSince:    profile.MemberSince ?? "");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new LookupResult(false, $"Lookup failed: {ex.Message}", 0, "", "", "", 0, 0, "");
            }
        }

        // ── Add / Remove ────────────────────────────────────────────────────

        /// <summary>
        /// Persists a friend entry and writes an initial cache snapshot
        /// from the lookup preview. The first poll cycle after this will
        /// seed LastSeenUnlockDate without firing notifications because
        /// JustAdded starts true.
        /// </summary>
        /// <returns>True on success; false when the friend already exists
        /// or the lookup wasn't usable.</returns>
        public async Task<bool> AddAsync(LookupResult preview, CancellationToken ct = default)
        {
            if (preview == null || !preview.Success || preview.UserId == 0) return false;

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var cfg = _config.GetFriendsConfiguration();
                if (cfg.Friends.Any(f => f.UserId == preview.UserId)) return false;

                var entry = new FriendEntry
                {
                    UserId   = preview.UserId,
                    Username = preview.Username,
                    LastSeenUnlockDate = "",  // seeded on first poll
                    JustAdded = true,
                };
                var updated = new List<FriendEntry>(cfg.Friends) { entry };
                cfg.Friends = updated;
                _config.SetFriendsConfiguration(cfg);
                await _config.SaveAsync().ConfigureAwait(false);

                // Seed cache snapshot from the lookup preview so the UI
                // can render immediately without a network round-trip.
                var snapshot = new FriendCacheSnapshot
                {
                    AvatarUrl       = preview.AvatarUrl,
                    Motto           = preview.Motto,
                    PointsSoftcore  = preview.PointsSoftcore,
                    PointsHardcore  = preview.PointsHardcore,
                    MemberSince     = preview.MemberSince,
                    UpdatedAt       = DateTimeOffset.UtcNow.ToString("o"),
                };
                RaLog.Write($"[AddAsync] SEED uid={preview.UserId} user=[{preview.Username}] avatar=[{preview.AvatarUrl}]");
                WriteCache(preview.UserId, snapshot);
            }
            finally { _writeLock.Release(); }

            FriendListChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>
        /// Removes a friend from the list and deletes their cached snapshot.
        /// </summary>
        public async Task<bool> RemoveAsync(int userId, CancellationToken ct = default)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var cfg = _config.GetFriendsConfiguration();
                int before = cfg.Friends.Count;
                cfg.Friends = cfg.Friends.Where(f => f.UserId != userId).ToList();
                if (cfg.Friends.Count == before) return false;

                _config.SetFriendsConfiguration(cfg);
                await _config.SaveAsync().ConfigureAwait(false);

                // Wipe cache row — friend may be re-added later but should
                // start fresh, not with stale unread counts / dates.
                DeleteCache(userId);
            }
            finally { _writeLock.Release(); }

            FriendListChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        // ── Refresh (manual; polling wires in Phase 3) ─────────────────────

        /// <summary>
        /// Pulls profile + recent achievements for one friend, runs the
        /// diff, advances the cursor, updates the cache snapshot.
        /// Returns the number of NEW unlocks discovered (0 for first poll
        /// when JustAdded is true — it just seeds).
        /// </summary>
        public async Task<int> RefreshAsync(int userId, CancellationToken ct = default)
        {
            FriendEntry? friend = Friends.FirstOrDefault(f => f.UserId == userId);
            if (friend == null) return 0;

            // Activity events accumulated under the lock, fired after release.
            var _pendingActivityToFire = new List<FriendActivityEntry>();

            try
            {
                // 1. Profile (cheap, updates cached fields)
                var profile = await _api.GetUserProfileAsync(friend.Username, ct).ConfigureAwait(false);
                if (profile == null || profile.Id == 0)
                {
                    await MarkFailureAsync(friend, "Profile unavailable").ConfigureAwait(false);
                    return 0;
                }

                // 2. Recent achievements within the lookback window. Widen
                // to (interval * 2) so a missed cycle still catches the
                // backlog on the next tick. Manual refresh callers get the
                // default 60 min — fine for one-off catch-up.
                var cfgForLookback = _config.GetFriendsConfiguration();
                int lookbackMin = Math.Max(60, cfgForLookback.PollIntervalMin * 2);
                var recent = await _api.GetUserRecentAchievementsAsync(
                    friend.Username, minutes: lookbackMin, ct).ConfigureAwait(false);

                // 2b. Most-recently-played game (for the snapshot's
                // LastGameTitle / icon fields). GetUserProfile returns
                // LastGameID but not the title or image; RecentlyPlayed
                // returns full game info including ImageIcon.
                string lastGameTitle = "";
                int lastGameIdFromRecent = 0;
                string lastGameImageIcon = "";
                try
                {
                    var played = await _api.GetUserRecentlyPlayedGamesAsync(
                        friend.Username, count: 1, ct).ConfigureAwait(false);
                    if (played != null && played.Count > 0)
                    {
                        lastGameTitle = played[0].Title;
                        lastGameIdFromRecent = played[0].GameId;
                        lastGameImageIcon = played[0].ImageIcon ?? "";
                    }
                }
                catch { /* best-effort — LastGameTitle just stays blank */ }

                // 2c. 24h unlock count. Cheap extra signal for the brief
                // card: "5 unlocks today" reads better than "no recent
                // activity" when the friend's been playing.
                int unlock24hCount = 0;
                try
                {
                    var recent24h = await _api.GetUserRecentAchievementsAsync(
                        friend.Username, minutes: 1440, ct).ConfigureAwait(false);
                    unlock24hCount = recent24h?.Count ?? 0;
                }
                catch { /* best-effort */ }

                // 2d. Phase 6b.2 — leaderboard diff. Fetch friend's LB
                // ranks ONLY when their most-recent game changed since
                // the last poll OR > 1h has elapsed (catches new
                // submissions on the same game between cycles). Without
                // this gate we'd burn an HTTP call per friend per cycle
                // even when nothing on their end could have changed.
                Dictionary<int, FriendLbScore>? friendCurrentLbScores = null;
                int lbDiffGameId = 0;
                // Cheap pre-read of the snapshot before the lock — used
                // ONLY to decide whether to issue the HTTP fetch. The
                // authoritative prevSnapshot read inside the lock below
                // is what the diff actually compares against.
                var preSnap = ReadCache(userId);
                bool gameChanged = preSnap == null
                    || preSnap.LastSeenLbGameId != lastGameIdFromRecent;
                bool staleSnapshot = preSnap != null
                    && DateTimeOffset.TryParse(preSnap.UpdatedAt, out var snapAt)
                    && (DateTimeOffset.UtcNow - snapAt) > TimeSpan.FromHours(1);
                if (lastGameIdFromRecent > 0 && (gameChanged || staleSnapshot))
                {
                    try
                    {
                        var boards = await _api.GetUserGameLeaderboardsAsync(
                            friend.Username, lastGameIdFromRecent, ct).ConfigureAwait(false);
                        if (boards != null && boards.Count > 0)
                        {
                            friendCurrentLbScores = new Dictionary<int, FriendLbScore>(boards.Count);
                            foreach (var b in boards)
                            {
                                if (b.UserEntry == null) continue;
                                friendCurrentLbScores[b.Id] = new FriendLbScore
                                {
                                    LeaderboardId = b.Id,
                                    Rank          = b.UserEntry.Rank,
                                    Score         = b.UserEntry.Score,
                                    FormattedScore = b.UserEntry.FormattedScore,
                                };
                            }
                            lbDiffGameId = lastGameIdFromRecent;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        RaLog.Write($"[Phase6b.2] {friend.Username} LB fetch: {ex.Message}");
                    }
                }

                // 3. Diff
                bool hardcoreFilter = cfgForLookback.HardcoreOnlyToast;
                var diff = FriendDiff.Compute(recent, friend.LastSeenUnlockDate, hardcoreFilter);

                int resultIncrement = 0;
                List<FriendLbDiff.Improvement>? lbImprovements = null;

                // 4. Update list state + cache snapshot
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var cfg = _config.GetFriendsConfiguration();
                    var f = cfg.Friends.FirstOrDefault(x => x.UserId == userId);
                    if (f == null) return 0;

                    // Username can drift over time — refresh from API
                    if (!string.IsNullOrEmpty(profile.User)) f.Username = profile.User;

                    f.LastSeenUnlockDate = diff.NewCursor;
                    f.ConsecutiveFailures = 0;
                    f.LastError = "";
                    f.IsPrivate = false;
                    f.IsInvalid = false;

                    bool wasJustAdded = f.JustAdded;
                    f.JustAdded = false;

                    _config.SetFriendsConfiguration(cfg);
                    await _config.SaveAsync().ConfigureAwait(false);

                    var prevSnapshot = ReadCache(userId) ?? new FriendCacheSnapshot();
                    int incrementBy = wasJustAdded ? 0 : diff.NewUnlocks.Count;

                    // Phase 3: push new unlocks onto the in-memory activity
                    // feed and notify subscribers. Suppress on first-poll
                    // (JustAdded) — seeding shouldn't replay an hour of
                    // history into the UI. Insert newest-first so the
                    // feed reads chronologically.
                    //
                    // Mutate the feed list inside the lock; defer
                    // ActivityReceived invocation until AFTER _writeLock
                    // is released. Foreign subscribers that synchronously
                    // do slow work would otherwise stall every other
                    // writer in this service.
                    if (!wasJustAdded && diff.NewUnlocks.Count > 0)
                    {
                        string avatarUrl = BuildAvatarUrl(profile.UserPic);
                        for (int i = diff.NewUnlocks.Count - 1; i >= 0; i--)
                        {
                            var entry = new FriendActivityEntry
                            {
                                FriendUserId    = userId,
                                FriendUsername  = profile.User,
                                FriendAvatarUrl = avatarUrl,
                                Unlock          = diff.NewUnlocks[i],
                            };
                            lock (_recentActivityLock)
                            {
                                _recentActivity.Insert(0, entry);
                                while (_recentActivity.Count > RecentActivityCap)
                                    _recentActivity.RemoveAt(_recentActivity.Count - 1);
                            }
                            _pendingActivityToFire.Add(entry);
                        }
                    }

                    // Build the new snapshot. Be defensive: if a profile
                    // field comes back blank/zero but the previous snapshot
                    // had a real value, KEEP the previous value. Without
                    // this, transient API hiccups (or partial profile
                    // responses) would wipe a friend's avatar / motto /
                    // points from the UI between polls.
                    string newAvatar = !string.IsNullOrEmpty(profile.UserPic)
                        ? BuildAvatarUrl(profile.UserPic)
                        : prevSnapshot.AvatarUrl;
                    string newMotto = !string.IsNullOrWhiteSpace(profile.Motto)
                        ? profile.Motto!
                        : prevSnapshot.Motto;
                    int newSoftcore = profile.TotalSoftcorePoints > 0
                        ? profile.TotalSoftcorePoints
                        : prevSnapshot.PointsSoftcore;
                    int newHardcore = profile.TotalPoints > 0
                        ? profile.TotalPoints
                        : prevSnapshot.PointsHardcore;
                    int newTrue = profile.TotalTruePoints > 0
                        ? profile.TotalTruePoints
                        : prevSnapshot.TruePoints;
                    string newMemberSince = !string.IsNullOrWhiteSpace(profile.MemberSince)
                        ? profile.MemberSince!
                        : prevSnapshot.MemberSince;
                    // LastGame comes from GetUserRecentlyPlayedGames (more
                    // reliable than profile.LastGameId which is sometimes
                    // 0 even for active users). Fall back to previous on miss.
                    int newLastGameId = lastGameIdFromRecent != 0
                        ? lastGameIdFromRecent
                        : (profile.LastGameId != 0 ? profile.LastGameId : prevSnapshot.LastGameId);
                    string newLastGameTitle = !string.IsNullOrEmpty(lastGameTitle)
                        ? lastGameTitle
                        : prevSnapshot.LastGameTitle;
                    string newLastGameIcon = !string.IsNullOrEmpty(lastGameImageIcon)
                        ? lastGameImageIcon
                        : prevSnapshot.LastGameImageIcon;

                    // Phase 6b.2 — LB diff. Compare the friend's
                    // current ranks against the snapshot's last-seen.
                    // Only compute if (a) we fetched current scores
                    // above and (b) the friend's most-recent game is
                    // the same as the snapshot's tracked game (different
                    // games mean we have nothing to compare against).
                    // First poll after add (JustAdded) seeds without
                    // toasting — same pattern as the achievement diff.
                    Dictionary<int, FriendLbScore> newLastSeenLbScores;
                    int newLastSeenLbGameId;
                    if (friendCurrentLbScores != null && lbDiffGameId > 0)
                    {
                        newLastSeenLbScores = friendCurrentLbScores;
                        newLastSeenLbGameId = lbDiffGameId;
                        if (!wasJustAdded
                            && prevSnapshot.LastSeenLbGameId == lbDiffGameId
                            && prevSnapshot.LastSeenLbScores != null
                            && prevSnapshot.LastSeenLbScores.Count > 0)
                        {
                            lbImprovements = FriendLbDiff.Compute(
                                prevSnapshot.LastSeenLbScores, friendCurrentLbScores);
                            if (lbImprovements.Count > 0)
                                RaLog.Write($"[Phase6b.2] {friend.Username} lb improvements={lbImprovements.Count} game={lbDiffGameId}");
                        }
                    }
                    else
                    {
                        // Keep the previous LB state if we didn't fetch
                        // new ones (friend hasn't played anything with
                        // LBs recently, or fetch failed transiently).
                        newLastSeenLbScores = prevSnapshot.LastSeenLbScores ?? new Dictionary<int, FriendLbScore>();
                        newLastSeenLbGameId = prevSnapshot.LastSeenLbGameId;
                    }

                    var snapshot = new FriendCacheSnapshot
                    {
                        AvatarUrl       = newAvatar,
                        Motto           = newMotto,
                        PointsSoftcore  = newSoftcore,
                        PointsHardcore  = newHardcore,
                        TruePoints      = newTrue,
                        MemberSince     = newMemberSince,
                        LastGameId      = newLastGameId,
                        LastGameTitle   = newLastGameTitle,
                        LastGameImageIcon = newLastGameIcon,
                        RichPresence    = profile.RichPresenceMsg ?? prevSnapshot.RichPresence,
                        RecentUnlockCount24h = unlock24hCount,
                        UnseenUnlockCount = prevSnapshot.UnseenUnlockCount + incrementBy,
                        LastSeenLbScores = newLastSeenLbScores,
                        LastSeenLbGameId = newLastSeenLbGameId,
                        UpdatedAt       = DateTimeOffset.UtcNow.ToString("o"),
                    };
                    // Skip the SQLite write when nothing materially
                    // changed. UpdatedAt always differs, so it's excluded
                    // from the equality check. Without this, every 5-min
                    // poll burns an INSERT OR REPLACE per friend even
                    // when the friend is offline and identical to last
                    // cycle.
                    bool snapshotDirty = incrementBy != 0
                        || snapshot.AvatarUrl       != prevSnapshot.AvatarUrl
                        || snapshot.Motto           != prevSnapshot.Motto
                        || snapshot.PointsSoftcore  != prevSnapshot.PointsSoftcore
                        || snapshot.PointsHardcore  != prevSnapshot.PointsHardcore
                        || snapshot.TruePoints      != prevSnapshot.TruePoints
                        || snapshot.MemberSince     != prevSnapshot.MemberSince
                        || snapshot.LastGameId      != prevSnapshot.LastGameId
                        || snapshot.LastGameTitle   != prevSnapshot.LastGameTitle
                        || snapshot.LastGameImageIcon != prevSnapshot.LastGameImageIcon
                        || snapshot.RichPresence    != prevSnapshot.RichPresence
                        || snapshot.RecentUnlockCount24h != prevSnapshot.RecentUnlockCount24h
                        || snapshot.LastSeenLbGameId != prevSnapshot.LastSeenLbGameId
                        || (friendCurrentLbScores != null);

                    if (snapshotDirty)
                    {
                        RaLog.Write($"[RefreshAsync] uid={userId} user=[{profile.User}] " +
                                    $"profile.UserPic=[{profile.UserPic ?? "<null>"}] " +
                                    $"prev.AvatarUrl=[{prevSnapshot.AvatarUrl}] " +
                                    $"WRITE avatar=[{snapshot.AvatarUrl}] lastGame=[{snapshot.LastGameTitle}] icon=[{snapshot.LastGameImageIcon}]");
                        WriteCache(userId, snapshot);
                    }

                    resultIncrement = incrementBy;
                }
                finally { _writeLock.Release(); }

                // Fire events AFTER the lock is released so foreign
                // subscribers can't stall other writers.
                FriendListChanged?.Invoke(this, EventArgs.Empty);
                foreach (var e in _pendingActivityToFire)
                {
                    try { ActivityReceived?.Invoke(this, e); } catch { }
                }
                if (lbImprovements != null && lbImprovements.Count > 0)
                {
                    try
                    {
                        FriendLbImproved?.Invoke(this, new FriendLbImprovementEvent(
                            FriendUserId: friend.UserId,
                            FriendUsername: friend.Username,
                            GameId: lbDiffGameId,
                            Improvements: lbImprovements));
                    }
                    catch { /* never propagate UI subscriber exceptions */ }
                }
                return resultIncrement;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await MarkFailureAsync(friend, ex.Message).ConfigureAwait(false);
                return 0;
            }
        }

        /// <summary>
        /// Iterates RefreshAsync across every friend. Sequential to keep
        /// in step with RetroAchievementsService's internal throttle and
        /// to surface errors deterministically. Honors per-friend
        /// exponential backoff so transiently-failing accounts don't
        /// burn the polling budget on every cycle.
        /// </summary>
        public async Task<int> RefreshAllAsync(CancellationToken ct = default)
        {
            int totalNew = 0;
            int cycle = Interlocked.Increment(ref _pollCycleSeq);
            foreach (var f in Friends)
            {
                if (ct.IsCancellationRequested) break;
                if (ShouldSkipForBackoff(f, cycle)) continue;
                totalNew += await RefreshAsync(f.UserId, ct).ConfigureAwait(false);
            }
            return totalNew;
        }

        /// <summary>
        /// Backoff schedule: a friend that's failed N times in a row
        /// gets skipped for min(N, cap) consecutive cycles. Reset on
        /// any successful refresh. Implementation: skip when
        /// (cycle % (failures + 1)) != 0 for failures in [1, cap].
        ///
        /// IsInvalid friends (failed past MaxConsecutiveFailures) still
        /// get probed periodically — at the BackoffCap cadence — so a
        /// transiently-private account that recovers can come back
        /// without a manual refresh. RefreshAsync on success resets
        /// both ConsecutiveFailures AND IsInvalid.
        /// </summary>
        private static bool ShouldSkipForBackoff(FriendEntry f, int cycle)
        {
            if (f.IsInvalid)
                return (cycle % (BackoffCap + 1)) != 0;
            if (f.ConsecutiveFailures <= 0) return false;
            int gap = Math.Min(f.ConsecutiveFailures, BackoffCap);
            return (cycle % (gap + 1)) != 0;
        }

        // ── Polling control (Phase 3) ─────────────────────────────────────

        /// <summary>
        /// Starts the DispatcherTimer poll loop. Idempotent — safe to
        /// call repeatedly. Reads <c>FriendsConfiguration.PollIntervalMin</c>
        /// for cadence; changes to that setting require StopPolling +
        /// StartPolling to take effect. MUST be called from the UI
        /// thread — DispatcherTimer captures CurrentDispatcher.
        /// </summary>
        public void StartPolling()
        {
            // Fail fast if a future caller invokes from a worker thread —
            // silently capturing the wrong dispatcher would make the
            // timer tick on a phantom dispatcher that never pumps.
            System.Windows.Application.Current?.Dispatcher.VerifyAccess();

            if (_polling) return;
            var cfg = _config.GetFriendsConfiguration();
            if (!cfg.PollingEnabled) return;

            int min = Math.Max(1, cfg.PollIntervalMin);
            _pollCts = new CancellationTokenSource();
            var token = _pollCts.Token;

            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(min),
            };
            _pollTimer.Tick += async (_, __) =>
            {
                try { await Task.Run(() => RefreshAllAsync(token), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* stopped */ }
                catch (Exception ex) { Trace.WriteLine($"[Friends] poll tick error: {ex.Message}"); }
            };
            _pollTimer.Start();
            _polling = true;

            // First poll fires after Interval — kick an immediate refresh
            // off-UI so the user sees state inside ~5s of Achievements
            // opening, not waiting a full Interval.
            _ = Task.Run(async () =>
            {
                try { await RefreshAllAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* stopped */ }
                catch (Exception ex) { Trace.WriteLine($"[Friends] initial poll error: {ex.Message}"); }
            }, token);
        }

        /// <summary>
        /// Stops the poll loop and cancels any in-flight RefreshAllAsync.
        /// Safe to call when not started. Call from MainWindow.Closed to
        /// release the DispatcherTimer's dispatcher reference and let
        /// the service get GC'd cleanly.
        /// </summary>
        public void StopPolling()
        {
            try { _pollTimer?.Stop(); } catch { }
            _pollTimer = null;
            try { _pollCts?.Cancel(); _pollCts?.Dispose(); } catch { }
            _pollCts = null;
            _polling = false;
            _pollCycleSeq = 0;
        }

        /// <summary>
        /// Resets the unread badge count for a friend. Call when the user
        /// opens the brief card or detail window for them.
        ///
        /// Holds <see cref="_writeLock"/> for the read-modify-write cycle
        /// so a concurrent <see cref="RefreshAsync"/> can't clobber the
        /// zero by reading the pre-clear snapshot. Sync API uses Wait()
        /// because UI callers are already off the layout-critical path
        /// when opening a popup; lock contention is microseconds.
        /// </summary>
        public void MarkSeen(int userId)
        {
            _writeLock.Wait();
            try
            {
                var snapshot = ReadCache(userId);
                if (snapshot == null || snapshot.UnseenUnlockCount == 0) return;
                snapshot.UnseenUnlockCount = 0;
                WriteCache(userId, snapshot);
            }
            finally { _writeLock.Release(); }
            FriendListChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Returns the cached snapshot for a friend (avatar, motto, points,
        /// unseen-count). Returns null if no row exists yet.
        /// </summary>
        public FriendCacheSnapshot? GetSnapshot(int userId) => ReadCache(userId);

        // ── Private helpers ─────────────────────────────────────────────────

        private async Task MarkFailureAsync(FriendEntry friend, string error)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var cfg = _config.GetFriendsConfiguration();
                var f = cfg.Friends.FirstOrDefault(x => x.UserId == friend.UserId);
                if (f == null) return;
                f.ConsecutiveFailures += 1;
                f.LastError = error;
                // After enough consecutive failures, mark the account
                // invalid so the row greys out and the polling loop
                // stops hammering it. Manual refresh (or success on
                // backoff probe) clears IsInvalid. We don't distinguish
                // 403 (private) from 404 (deleted) because RA's API
                // wrappers don't surface HTTP status — both show as
                // "Account unavailable" in the row UI.
                if (f.ConsecutiveFailures >= MaxConsecutiveFailures)
                    f.IsInvalid = true;
                _config.SetFriendsConfiguration(cfg);
                await _config.SaveAsync().ConfigureAwait(false);
                FriendListChanged?.Invoke(this, EventArgs.Empty);
            }
            finally { _writeLock.Release(); }
        }

        private string BuildAvatarUrl(string? userPic)
        {
            if (string.IsNullOrWhiteSpace(userPic))
            {
                RaLog.Write("[BuildAvatarUrl] input is empty/null — returning empty");
                return "";
            }
            // RA returns paths like "/UserPic/UserName.png". Compose against
            // the media host.
            string result = userPic.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? userPic
                : "https://media.retroachievements.org" + userPic;
            RaLog.Write($"[BuildAvatarUrl] in=[{userPic}] out=[{result}]");
            return result;
        }

        private string CacheKey(int userId) => $"friend:{userId}";

        private string CacheOwner()
        {
            var ra = _config.GetRetroAchievementsConfiguration();
            string user = string.IsNullOrWhiteSpace(ra?.Username) ? "unknown" : ra.Username;
            return $"friends:{user}";
        }

        private FriendCacheSnapshot? ReadCache(int userId)
        {
            try
            {
                var row = _db.GetRaCache(CacheKey(userId));
                if (row == null || string.IsNullOrEmpty(row.Payload))
                {
                    RaLog.Write($"[ReadCache] uid={userId} no row or empty payload");
                    return null;
                }
                var snap = JsonSerializer.Deserialize<FriendCacheSnapshot>(row.Payload, _jsonOpts);
                RaLog.Write($"[ReadCache] uid={userId} payloadLen={row.Payload.Length} " +
                            $"avatar=[{snap?.AvatarUrl}] lastGame=[{snap?.LastGameTitle}] icon=[{snap?.LastGameImageIcon}]");
                return snap;
            }
            catch (Exception ex)
            {
                RaLog.Write($"[ReadCache] uid={userId} EX: {ex.GetType().Name} {ex.Message}");
                return null;
            }
        }

        private void WriteCache(int userId, FriendCacheSnapshot snapshot)
        {
            try
            {
                string payload = JsonSerializer.Serialize(snapshot, _jsonOpts);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                // Long TTL — we treat the row as authoritative until next
                // manual refresh / poll cycle overwrites it. TTL is just
                // a metadata field; we never expire from cache.
                _db.SetRaCache(CacheKey(userId), CacheOwner(), payload, now, (long)TimeSpan.FromDays(365).TotalSeconds);
                RaLog.Write($"[WriteCache] uid={userId} payloadLen={payload.Length} " +
                            $"avatar=[{snapshot.AvatarUrl}] " +
                            $"jsonHead=[{payload.Substring(0, Math.Min(120, payload.Length))}]");
            }
            catch (Exception ex)
            {
                RaLog.Write($"[WriteCache] uid={userId} EX: {ex.GetType().Name} {ex.Message}");
            }
        }

        private void DeleteCache(int userId)
        {
            try
            {
                // ra_cache has no public delete API; overwrite with empty
                // payload + zero TTL so the row is effectively dead until
                // re-added. Keeps the table from growing unboundedly is a
                // Phase-5+ concern; not worth a schema migration now.
                _db.SetRaCache(CacheKey(userId), CacheOwner(), "", 0, 0);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Friends] cache delete failed for {userId}: {ex.Message}");
            }
        }
    }
}
