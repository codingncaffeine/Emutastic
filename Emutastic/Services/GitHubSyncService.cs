using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    public sealed class GitHubSyncService
    {
        public static GitHubSyncService Instance { get; } = new();

        private static string ClientId => Secrets.GitHubOAuthClientId;
        private const string SharedRepoName = "emutastic-saves";
        private const string ApiBase = "https://api.github.com";

        // Active repo: the shared one by default, or this machine's own when
        // the per-PC toggle is on. Read from config on every access so a
        // toggle flip takes effect on the very next operation.
        private static string RepoName =>
            App.Configuration?.GetCloudSyncConfiguration() is { UsePerPcRepo: true }
                ? PerPcRepoName
                : SharedRepoName;

        /// <summary>This machine's dedicated repo name (for UI display).</summary>
        public static string PerPcRepoName { get; } = BuildPerPcRepoName();

        /// <summary>The repo currently in use (for UI display).</summary>
        public static string EffectiveRepoName => RepoName;

        private static string BuildPerPcRepoName()
        {
            // GitHub repo names allow letters, digits, '-', '_', '.'; squash
            // anything else in the machine name to '-'.
            var sb = new StringBuilder();
            foreach (char c in Environment.MachineName.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
            string suffix = sb.ToString().Trim('-');
            if (suffix.Length == 0) suffix = "pc";
            return $"{SharedRepoName}-{suffix}";
        }

        /// <summary>
        /// Drops every piece of state bound to the previous repo (sha cache,
        /// manifest). Call when the per-PC toggle flips so the next sync
        /// starts clean against the newly selected repo. The db side-car is
        /// per-repo by filename and needs no reset.
        /// </summary>
        public void ResetRepoBinding()
        {
            _shaCache.Clear();
            _manifestCache = new SyncManifest();
        }

        private static readonly HttpClient Http = CreateHttpClient();
        private volatile string? _token;
        private string? _username;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _shaCache = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
            _gameLocks = new();

        private GitHubSyncService() { }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Emutastic/cloud-sync");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        // ── Initialization ──────────────────────────────────────────────────

        public bool IsAuthenticated => !string.IsNullOrEmpty(_token);
        public string? Username => _username;

        public void LoadFromConfig()
        {
            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            if (cfg == null) return;

            _token = UnprotectString(cfg.GitHubTokenProtected);
            _username = cfg.GitHubUsername;
        }

        private HttpRequestMessage AuthedRequest(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            if (!string.IsNullOrEmpty(_token))
                req.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
            return req;
        }

        public async Task<bool> ValidateTokenAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_token)) return false;
            try
            {
                using var req = AuthedRequest(HttpMethod.Get, $"{ApiBase}/user");
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _token = null;
                    return false;
                }
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    _username = doc.RootElement.GetProperty("login").GetString();
                }
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ── Device Flow ─────────────────────────────────────────────────────

        public sealed record DeviceFlowStart(
            string DeviceCode, string UserCode, string VerificationUri,
            int ExpiresIn, int Interval);

        public async Task<DeviceFlowStart> BeginDeviceFlowAsync(CancellationToken ct = default)
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("scope", "repo")
            });
            content.Headers.ContentType!.MediaType = "application/x-www-form-urlencoded";

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
            req.Content = content;
            req.Headers.Accept.ParseAdd("application/json");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new DeviceFlowStart(
                root.GetProperty("device_code").GetString()!,
                root.GetProperty("user_code").GetString()!,
                root.GetProperty("verification_uri").GetString()!,
                root.GetProperty("expires_in").GetInt32(),
                root.GetProperty("interval").GetInt32());
        }

        public async Task<bool> PollForTokenAsync(string deviceCode, int intervalSec,
            int expiresInSec, CancellationToken ct = default)
        {
            var deadline = DateTime.UtcNow.AddSeconds(expiresInSec);

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct).ConfigureAwait(false);

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", ClientId),
                    new KeyValuePair<string, string>("device_code", deviceCode),
                    new KeyValuePair<string, string>("grant_type",
                        "urn:ietf:params:oauth:grant-type:device_code")
                });

                using var req = new HttpRequestMessage(HttpMethod.Post,
                    "https://github.com/login/oauth/access_token");
                req.Content = content;
                req.Headers.Accept.ParseAdd("application/json");

                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("access_token", out var tokenProp))
                {
                    _token = tokenProp.GetString();
                    await ValidateTokenAsync(ct);
                    SaveTokenToConfig();
                    return true;
                }

                if (root.TryGetProperty("error", out var err))
                {
                    string error = err.GetString() ?? "";
                    if (error == "authorization_pending") continue;
                    if (error == "slow_down") { intervalSec += 5; continue; }
                    if (error == "expired_token" || error == "access_denied") return false;
                }
            }
            return false;
        }

        private void SaveTokenToConfig()
        {
            var cfg = App.Configuration?.GetCloudSyncConfiguration() ?? new Configuration.CloudSyncConfiguration();
            cfg.GitHubTokenProtected = ProtectString(_token ?? "");
            cfg.GitHubUsername = _username ?? "";
            cfg.Enabled = true;
            App.Configuration?.SetCloudSyncConfiguration(cfg);
            _ = App.Configuration?.SaveAsync();
        }

        public void SignOut()
        {
            _token = null;
            _username = null;
            _shaCache.Clear();

            var cfg = App.Configuration?.GetCloudSyncConfiguration() ?? new Configuration.CloudSyncConfiguration();
            cfg.GitHubTokenProtected = "";
            cfg.GitHubUsername = "";
            cfg.Enabled = false;
            App.Configuration?.SetCloudSyncConfiguration(cfg);
            _ = App.Configuration?.SaveAsync();
        }

        // ── Repo Management ─────────────────────────────────────────────────

        public async Task EnsureRepoExistsAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_username)) return;

            try
            {
                using var checkReq = AuthedRequest(HttpMethod.Get,
                    $"{ApiBase}/repos/{_username}/{RepoName}");
                using var check = await Http.SendAsync(checkReq, ct).ConfigureAwait(false);
                if (check.IsSuccessStatusCode) return;
            }
            catch { }

            try
            {
                string body = JsonSerializer.Serialize(new
                {
                    name = RepoName,
                    @private = true,
                    description = "Emutastic cloud saves",
                    auto_init = false
                });

                using var req = AuthedRequest(HttpMethod.Post, $"{ApiBase}/user/repos");
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.UnprocessableEntity)
                    CloudSyncLog.Write("Repo already exists (422)");
                else
                    resp.EnsureSuccessStatusCode();

                CloudSyncLog.Write($"Created repo {_username}/{RepoName}");
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Repo creation failed: {ex.Message}");
            }
        }

        // ── SHA Cache ───────────────────────────────────────────────────────

        public async Task RefreshShaCacheAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_username)) return;
            _shaCache.Clear();

            try
            {
                using var req = AuthedRequest(HttpMethod.Get,
                    $"{ApiBase}/repos/{_username}/{RepoName}/git/trees/HEAD?recursive=1");
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    CloudSyncLog.Write("Empty repo — no commits yet");
                    return;
                }
                if (!resp.IsSuccessStatusCode) return;

                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("tree", out var tree))
                {
                    foreach (var item in tree.EnumerateArray())
                    {
                        string path = item.GetProperty("path").GetString() ?? "";
                        string sha = item.GetProperty("sha").GetString() ?? "";
                        if (!string.IsNullOrEmpty(path))
                            _shaCache[path] = sha;
                    }
                }

                CloudSyncLog.Write($"SHA cache loaded: {_shaCache.Count} files");
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"SHA cache refresh failed: {ex.Message}");
            }
        }

        // ── File Upload ─────────────────────────────────────────────────────

        public async Task<bool> UploadFileAsync(string repoPath, byte[] fileBytes,
            CancellationToken ct = default, bool isRetry = false)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_username)) return false;

            try
            {
                string base64 = Convert.ToBase64String(fileBytes);
                _shaCache.TryGetValue(repoPath, out string? existingSha);

                var payload = new Dictionary<string, object>
                {
                    ["message"] = $"sync {repoPath}",
                    ["content"] = base64
                };
                if (!string.IsNullOrEmpty(existingSha))
                    payload["sha"] = existingSha;

                string body = JsonSerializer.Serialize(payload);
                using var req = AuthedRequest(HttpMethod.Put,
                    $"{ApiBase}/repos/{_username}/{RepoName}/contents/{repoPath}");
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

                // 409: sha we sent no longer matches HEAD. 422: file exists but we
                // sent no sha (stale/missing cache entry — seen when the same path
                // is queued twice in quick succession). Both mean "our sha cache
                // is wrong for this path" — refresh and retry once.
                if ((resp.StatusCode == HttpStatusCode.Conflict
                     || resp.StatusCode == HttpStatusCode.UnprocessableEntity) && !isRetry)
                {
                    await RefreshShaCacheAsync(ct);
                    return await UploadFileAsync(repoPath, fileBytes, ct, isRetry: true);
                }

                if (resp.IsSuccessStatusCode)
                {
                    string respJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(respJson);
                    if (doc.RootElement.TryGetProperty("content", out var c)
                        && c.TryGetProperty("sha", out var newSha))
                    {
                        _shaCache[repoPath] = newSha.GetString() ?? "";
                    }
                    // The freshly-uploaded variant is now canonical — remove its
                    // encryption-toggle counterpart so exactly one variant of each
                    // file ever exists remotely. Without this, toggling encryption
                    // leaves stale .enc/.srm shadows that a later toggle-back would
                    // resurrect over newer saves (silent rollback on fresh installs).
                    await DeleteCounterpartVariantAsync(repoPath, ct).ConfigureAwait(false);
                    return true;
                }

                CloudSyncLog.Write($"Upload failed {repoPath}: {resp.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Upload exception {repoPath}: {ex.Message}");
                return false;
            }
        }

        // ── File Delete ─────────────────────────────────────────────────────

        /// <summary>
        /// Deletes a file from the sync repo. Requires the blob sha, which is
        /// taken from the sha cache — returns false (no-op) when the path isn't
        /// cached. Git history retains the blob, so deletion is recoverable.
        /// </summary>
        public async Task<bool> DeleteFileAsync(string repoPath, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_username)) return false;
            if (!_shaCache.TryGetValue(repoPath, out string? sha) || string.IsNullOrEmpty(sha))
                return false;

            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["message"] = $"remove {repoPath}",
                    ["sha"] = sha
                };
                using var req = AuthedRequest(HttpMethod.Delete,
                    $"{ApiBase}/repos/{_username}/{RepoName}/contents/{repoPath}");
                req.Content = new StringContent(JsonSerializer.Serialize(payload),
                    Encoding.UTF8, "application/json");

                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _shaCache.TryRemove(repoPath, out _);
                    _manifestCache.Files.TryRemove(repoPath, out _);
                    return true;
                }

                CloudSyncLog.Write($"Delete failed {repoPath}: {resp.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Delete exception {repoPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes the encryption-toggle counterpart of a just-uploaded path
        /// ("X.srm" ↔ "X.srm.enc", "manifest.json" ↔ "manifest.json.enc") so the
        /// repo converges to a single variant per file. If the remote delete
        /// can't run (sha not cached), the manifest entry is still dropped so
        /// the stale variant stops being advertised to download passes; the
        /// blob itself gets cleaned up by a later sync once the sha cache
        /// knows it.
        /// </summary>
        private async Task DeleteCounterpartVariantAsync(string repoPath, CancellationToken ct)
        {
            string counterpart = repoPath.EndsWith(".enc", StringComparison.Ordinal)
                ? repoPath[..^4]
                : repoPath + ".enc";

            bool known = _shaCache.ContainsKey(counterpart)
                || _manifestCache.Files.ContainsKey(counterpart);
            if (!known) return;

            if (await DeleteFileAsync(counterpart, ct).ConfigureAwait(false))
                CloudSyncLog.Write($"Removed stale variant: {counterpart}");
            else
                _manifestCache.Files.TryRemove(counterpart, out _);
        }

        // ── File Download ───────────────────────────────────────────────────

        public async Task<byte[]?> DownloadFileAsync(string repoPath,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_username)) return null;

            try
            {
                using var req = AuthedRequest(HttpMethod.Get,
                    $"{ApiBase}/repos/{_username}/{RepoName}/contents/{repoPath}");
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode) return null;

                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string base64 = root.GetProperty("content").GetString() ?? "";
                base64 = base64.Replace("\n", "").Replace("\r", "");

                if (root.TryGetProperty("sha", out var shaProp))
                    _shaCache[repoPath] = shaProp.GetString() ?? "";

                return Convert.FromBase64String(base64);
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Download exception {repoPath}: {ex.Message}");
                return null;
            }
        }

        // ── Per-Game Locking ────────────────────────────────────────────────

        public SemaphoreSlim GetGameLock(string romHash)
            => _gameLocks.GetOrAdd(romHash, _ => new SemaphoreSlim(1, 1));

        // ── DPAPI Token Protection ──────────────────────────────────────────

        public static string ProtectString(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return "";
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
                byte[] encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            catch { return ""; }
        }

        public static string UnprotectString(string protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64)) return "";
            try
            {
                byte[] encrypted = Convert.FromBase64String(protectedBase64);
                byte[] bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }

        // ── Optional Encryption ─────────────────────────────────────────────

        public static byte[] Encrypt(byte[] plaintext, byte[] key)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[plaintext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
            var result = new byte[12 + 16 + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, 12);
            Buffer.BlockCopy(tag, 0, result, 12, 16);
            Buffer.BlockCopy(ciphertext, 0, result, 28, ciphertext.Length);
            return result;
        }

        public static byte[] Decrypt(byte[] blob, byte[] key)
        {
            if (blob.Length < 28) throw new CryptographicException("Invalid encrypted data");
            byte[] nonce = new byte[12];
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[blob.Length - 28];
            Buffer.BlockCopy(blob, 0, nonce, 0, 12);
            Buffer.BlockCopy(blob, 12, tag, 0, 16);
            Buffer.BlockCopy(blob, 28, ciphertext, 0, ciphertext.Length);
            byte[] plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }

        public static byte[] DeriveKey(string passphrase, string githubUsername)
        {
            byte[] salt = Encoding.UTF8.GetBytes($"emutastic-sync-{githubUsername}");
            return Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(passphrase), salt, 100_000,
                HashAlgorithmName.SHA256, 32);
        }

        // ── Manifest ────────────────────────────────────────────────────────

        public class SyncManifest
        {
            public System.Collections.Concurrent.ConcurrentDictionary<string, SyncFileEntry> Files { get; set; } = new();
            public int SchemaVersion { get; set; } = 1;
        }

        public class SyncFileEntry
        {
            public string LastModifiedUtc { get; set; } = "";
            public long SizeBytes { get; set; }
            // SHA-256 of the plaintext content; set for library.db so the
            // upload decision is content-based (see FullSyncAsync). Null on
            // entries written by older builds — treated as "unknown, upload".
            public string? Sha256 { get; set; }
        }

        private SyncManifest _manifestCache = new();

        public SyncManifest ManifestCache => _manifestCache;

        public async Task LoadManifestAsync(CancellationToken ct = default)
        {
            try
            {
                var cfg = App.Configuration?.GetCloudSyncConfiguration();
                bool encrypted = cfg is { EncryptionEnabled: true }
                    && !string.IsNullOrEmpty(cfg.PassphraseProtected);
                string path = encrypted ? "manifest.json.enc" : "manifest.json";

                byte[]? data = await DownloadFileAsync(path, ct);
                if (data == null || data.Length == 0)
                {
                    _manifestCache = new SyncManifest();
                    return;
                }

                if (encrypted)
                {
                    byte[] key = DeriveKey(
                        UnprotectString(cfg!.PassphraseProtected), _username ?? "");
                    data = Decrypt(data, key);
                }

                string json = Encoding.UTF8.GetString(data);
                _manifestCache = JsonSerializer.Deserialize<SyncManifest>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Manifest load failed: {ex.Message}");
                _manifestCache = new SyncManifest();
            }
        }

        public async Task SaveManifestAsync(CancellationToken ct = default)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(_manifestCache, new JsonSerializerOptions { WriteIndented = true }));

                var cfg = App.Configuration?.GetCloudSyncConfiguration();
                bool encrypted = cfg is { EncryptionEnabled: true }
                    && !string.IsNullOrEmpty(cfg.PassphraseProtected);

                if (encrypted)
                {
                    byte[] key = DeriveKey(
                        UnprotectString(cfg!.PassphraseProtected), _username ?? "");
                    data = Encrypt(data, key);
                }

                string path = encrypted ? "manifest.json.enc" : "manifest.json";
                await UploadFileAsync(path, data, ct);
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Manifest save failed: {ex.Message}");
            }
        }

        // ── Last-synced db hash (local side-car) ────────────────────────────
        // Hash of the library.db snapshot this MACHINE last uploaded or adopted.
        // Deliberately local (not in the shared manifest): it answers "did *I*
        // change since *my* last sync?", which is per-machine state. Lives in
        // DataRoot so portable installs carry it with their data.

        // Keyed by repo name so flipping the per-PC toggle back and forth
        // keeps an accurate "what did I last sync HERE" per repository.
        private static string DbStatePath
            => System.IO.Path.Combine(AppPaths.DataRoot, $"cloudsync_dbstate_{RepoName}.txt");

        private static string? LoadLastSyncedDbHash()
        {
            try
            {
                string p = DbStatePath;
                return System.IO.File.Exists(p)
                    ? System.IO.File.ReadAllText(p).Trim()
                    : null;
            }
            catch { return null; }
        }

        private static void SaveLastSyncedDbHash(string hash)
        {
            try { System.IO.File.WriteAllText(DbStatePath, hash); }
            catch { /* non-fatal — worst case one redundant upload next sync */ }
        }

        // ── Bidirectional Sync ──────────────────────────────────────────────

        public record SyncResult(int Uploaded, int Downloaded, int Errors);

        public record LocalSaveInfo(string RepoPath, string LocalPath, DateTime LastModifiedUtc, long SizeBytes);

        public static List<LocalSaveInfo> BuildLocalSaveMap(DatabaseService db)
        {
            var result = new List<LocalSaveInfo>();
            var games = db.GetGamesSyncMap();

            foreach (var g in games)
            {
                if (string.IsNullOrEmpty(g.RomHash) || string.IsNullOrEmpty(g.Console)
                    || string.IsNullOrEmpty(g.RomPath))
                    continue;

                string batteryDir = AppPaths.GetFolder("BatterySaves", g.Console);
                string romStem = System.IO.Path.GetFileNameWithoutExtension(g.RomPath);
                string localPath = System.IO.Path.Combine(batteryDir,
                    FileNameHelper.SanitizeFileName(romStem) + ".srm");

                if (!System.IO.File.Exists(localPath)) continue;

                var fi = new System.IO.FileInfo(localPath);
                string repoPath = $"BatterySaves/{g.Console}/{g.RomHash}.srm";

                result.Add(new LocalSaveInfo(repoPath, localPath, fi.LastWriteTimeUtc, fi.Length));
            }

            return result;
        }

        public async Task<SyncResult> FullSyncAsync(DatabaseService db,
            CancellationToken ct = default)
        {
            if (!IsAuthenticated) return new SyncResult(0, 0, 0);

            int uploaded = 0, downloaded = 0, errors = 0;

            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            bool encrypted = cfg is { EncryptionEnabled: true }
                && !string.IsNullOrEmpty(cfg.PassphraseProtected);
            byte[]? encKey = encrypted
                ? DeriveKey(UnprotectString(cfg!.PassphraseProtected), _username ?? "")
                : null;

            await RefreshShaCacheAsync(ct);
            await LoadManifestAsync(ct);

            // Converge the repo to one variant per file. An encryption toggle
            // re-uploads everything under the other suffix but historically left
            // the old variant behind; when BOTH X and X.enc exist remotely, drop
            // the one that doesn't match the current mode. Both-exist is required:
            // an opposite-variant file with no counterpart is the only surviving
            // copy of that save and must stay downloadable after a toggle-back.
            foreach (var stale in _shaCache.Keys.ToList())
            {
                if (ct.IsCancellationRequested) break;
                bool isEnc = stale.EndsWith(".enc", StringComparison.Ordinal);
                if (isEnc == encrypted) continue;              // matches current mode — keep
                string counterpart = isEnc ? stale[..^4] : stale + ".enc";
                if (!_shaCache.ContainsKey(counterpart)) continue; // lone copy — keep
                if (await DeleteFileAsync(stale, ct))
                    CloudSyncLog.Write($"Removed stale variant: {stale}");
            }

            var localSaves = BuildLocalSaveMap(db);
            string encSuffix = encrypted ? ".enc" : "";

            // Upload local files that are newer than the manifest
            foreach (var local in localSaves)
            {
                if (ct.IsCancellationRequested) break;

                string repoPath = local.RepoPath + encSuffix;
                bool shouldUpload = true;

                if (_manifestCache.Files.TryGetValue(repoPath, out var entry)
                    && DateTime.TryParse(entry.LastModifiedUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var remoteMtime))
                {
                    shouldUpload = local.LastModifiedUtc > remoteMtime;
                }

                if (shouldUpload)
                {
                    try
                    {
                        byte[] bytes = System.IO.File.ReadAllBytes(local.LocalPath);
                        if (encrypted && encKey != null) bytes = Encrypt(bytes, encKey);
                        if (await UploadFileAsync(repoPath, bytes, ct))
                        {
                            _manifestCache.Files[repoPath] = new SyncFileEntry
                            {
                                LastModifiedUtc = local.LastModifiedUtc.ToString("o"),
                                SizeBytes = local.SizeBytes
                            };
                            uploaded++;
                        }
                        else errors++;
                    }
                    catch { errors++; }
                }
            }

            // Build a lookup from repo path → local .srm path for ALL games (including never-played)
            var allGames = db.GetGamesSyncMap();
            var repoToLocalPath = new Dictionary<string, string>();
            foreach (var g in allGames)
            {
                if (string.IsNullOrEmpty(g.RomHash) || string.IsNullOrEmpty(g.Console)) continue;
                string batteryDir = AppPaths.GetFolder("BatterySaves", g.Console);
                string romStem = System.IO.Path.GetFileNameWithoutExtension(g.RomPath);
                string localPath = System.IO.Path.Combine(batteryDir,
                    FileNameHelper.SanitizeFileName(romStem) + ".srm");
                string rp = $"BatterySaves/{g.Console}/{g.RomHash}.srm" + encSuffix;
                repoToLocalPath[rp] = localPath;
            }

            // Download remote files that are newer than local (or don't exist locally)
            foreach (var (repoPath, entry) in _manifestCache.Files)
            {
                if (ct.IsCancellationRequested) break;
                if (!repoPath.StartsWith("BatterySaves/")) continue;
                if (!repoToLocalPath.TryGetValue(repoPath, out string? targetPath)) continue;

                bool hasRemoteMtime = DateTime.TryParse(entry.LastModifiedUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var remoteMtime);
                bool shouldDownload = !System.IO.File.Exists(targetPath)
                    || (hasRemoteMtime
                        && remoteMtime > System.IO.File.GetLastWriteTimeUtc(targetPath));

                if (shouldDownload)
                {
                    try
                    {
                        byte[]? data = await DownloadFileAsync(repoPath, ct);
                        if (data != null && data.Length > 0)
                        {
                            if (encrypted && encKey != null) data = Decrypt(data, encKey);
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targetPath)!);
                            System.IO.File.WriteAllBytes(targetPath, data);
                            // Stamp the manifest's mtime back onto the file — WriteAllBytes
                            // sets "now", which is newer than the manifest entry, so the NEXT
                            // full sync would see every save we just downloaded as locally
                            // modified and re-upload the lot (the "90 up with no changes" bug).
                            if (hasRemoteMtime) System.IO.File.SetLastWriteTimeUtc(targetPath, remoteMtime);
                            downloaded++;
                        }
                    }
                    catch { errors++; }
                }
            }

            // Sync library database — use VACUUM INTO for a consistent snapshot
            // (raw File.ReadAllBytes on a WAL-mode DB risks partial checkpoint reads).
            //
            // The db needs a THREE-WAY decision, not a mine-vs-remote compare. Two
            // machines' databases legitimately differ (play history, caches), so
            // "is my content different from remote?" is always yes and alternating
            // syncs ping-pong uploads forever. Instead each machine remembers the
            // hash it last synced at (local side-car file, NOT the shared manifest):
            //   - my db changed since last sync            → upload (last-writer-wins)
            //   - only remote changed                      → download and adopt it
            //   - neither changed                          → quiet
            // mtime is useless here in all cases: the sync's own VACUUM connection
            // checkpoints the WAL on close, rewriting library.db's mtime every sync.
            try
            {
                string dbPath = System.IO.Path.Combine(AppPaths.DataRoot, "library.db");
                string dbRepoPath = "library.db" + encSuffix;
                string? lastSyncedHash = LoadLastSyncedDbHash();
                string? myHash = null;

                if (System.IO.File.Exists(dbPath))
                {
                    string tempDb = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), $"emutastic_sync_{Guid.NewGuid():N}.db");
                    try
                    {
                        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
                        {
                            conn.Open();
                            var cmd = conn.CreateCommand();
                            cmd.CommandText = $"VACUUM INTO '{tempDb.Replace("'", "''")}'";
                            cmd.ExecuteNonQuery();
                        }

                        var snapInfo = new System.IO.FileInfo(tempDb);
                        byte[] dbBytes = System.IO.File.ReadAllBytes(tempDb);
                        // Hash the PLAINTEXT snapshot — encryption uses a random IV,
                        // so ciphertext never compares equal even for identical content.
                        myHash = Convert.ToHexString(SHA256.HashData(dbBytes));

                        _manifestCache.Files.TryGetValue(dbRepoPath, out var dbEntry);
                        string? remoteHash = dbEntry?.Sha256;

                        bool localChanged = !string.Equals(myHash, lastSyncedHash,
                            StringComparison.OrdinalIgnoreCase);
                        // Upload when I changed (and remote doesn't already have my
                        // exact content), or to seed the hash on a legacy manifest
                        // entry written by a pre-hash build.
                        bool dbNeedsUpload =
                            (localChanged || string.IsNullOrEmpty(remoteHash))
                            && !string.Equals(myHash, remoteHash, StringComparison.OrdinalIgnoreCase);

                        if (dbNeedsUpload)
                        {
                            if (encrypted && encKey != null) dbBytes = Encrypt(dbBytes, encKey);
                            if (await UploadFileAsync(dbRepoPath, dbBytes, ct))
                            {
                                _manifestCache.Files[dbRepoPath] = new SyncFileEntry
                                {
                                    LastModifiedUtc = DateTime.UtcNow.ToString("o"),
                                    SizeBytes = snapInfo.Length,
                                    Sha256 = myHash
                                };
                                SaveLastSyncedDbHash(myHash);
                                lastSyncedHash = myHash;
                                uploaded++;
                                CloudSyncLog.Write("Database uploaded");
                            }
                            else errors++;
                        }
                        else if (!localChanged && string.Equals(myHash, remoteHash, StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(myHash, lastSyncedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            // Remote already matches me but my side-car is stale
                            // (e.g. first run after updating) — just record it.
                            SaveLastSyncedDbHash(myHash);
                            lastSyncedHash = myHash;
                        }
                    }
                    finally
                    {
                        try { System.IO.File.Delete(tempDb); } catch { }
                    }
                }

                // Download the remote DB when it changed and I didn't (second-PC
                // restore + continuous adoption of the other machine's db).
                if (_manifestCache.Files.TryGetValue(dbRepoPath, out var remoteDbEntry)
                    && DateTime.TryParse(remoteDbEntry.LastModifiedUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var remoteDbMtime))
                {
                    var localDbInfo = System.IO.File.Exists(dbPath) ? new System.IO.FileInfo(dbPath) : null;
                    string? remoteHash = remoteDbEntry.Sha256;

                    bool shouldDownload;
                    if (localDbInfo == null)
                    {
                        shouldDownload = true;
                    }
                    else if (!string.IsNullOrEmpty(remoteHash) && myHash != null)
                    {
                        bool localChanged = !string.Equals(myHash, lastSyncedHash,
                            StringComparison.OrdinalIgnoreCase);
                        // Adopt remote only when I have no local edits of my own and
                        // remote genuinely differs from me. If BOTH sides changed,
                        // the upload above already won (last-writer-wins) and the
                        // manifest now carries my hash, so this stays false.
                        shouldDownload = !localChanged
                            && !string.Equals(remoteHash, myHash, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        // Legacy manifest entry without a hash — old mtime rule.
                        shouldDownload = remoteDbMtime > localDbInfo.LastWriteTimeUtc;
                    }

                    if (shouldDownload)
                    {
                        byte[]? remoteDb = await DownloadFileAsync(dbRepoPath, ct);
                        if (remoteDb != null && remoteDb.Length > 0)
                        {
                            if (encrypted && encKey != null) remoteDb = Decrypt(remoteDb, encKey);
                            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                            System.IO.File.WriteAllBytes(dbPath, remoteDb);
                            // Same mtime-echo fix as the save download above.
                            System.IO.File.SetLastWriteTimeUtc(dbPath, remoteDbMtime);
                            // Record what we adopted so the next sync sees "unchanged"
                            // (hash the bytes we wrote — covers legacy entries too).
                            SaveLastSyncedDbHash(Convert.ToHexString(SHA256.HashData(remoteDb)));
                            downloaded++;
                            CloudSyncLog.Write("Database downloaded from remote");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Database sync failed: {ex.Message}");
                errors++;
            }

            await SaveManifestAsync(ct);
            CloudSyncLog.Write($"Full sync: {uploaded} up, {downloaded} down, {errors} errors");

            return new SyncResult(uploaded, downloaded, errors);
        }
    }
}

