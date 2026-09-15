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

        // Every PC syncs to its own repository. Sharing one save set between PCs let a
        // newer save from one machine replace another machine's further-along save, and
        // let a new PC pull saves it never made. The shared emutastic-saves repository
        // that older builds wrote to is left as it is.
        private static string RepoName => PerPcRepoName;

        /// <summary>
        /// Stable per-machine token: the hostname squashed to repo/path-safe chars.
        /// Declared BEFORE the members that use it — static initializers run in
        /// textual order, so a forward reference would capture null and collapse
        /// every machine onto the same "library..db" filename, defeating the
        /// per-machine namespacing below.
        /// </summary>
        private static string MachineSuffix { get; } = BuildMachineSuffix();

        private static string BuildMachineSuffix()
        {
            // GitHub repo names + path segments allow letters, digits, '-', '_', '.';
            // squash anything else in the machine name to '-'.
            var sb = new StringBuilder();
            foreach (char c in Environment.MachineName.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
            string suffix = sb.ToString().Trim('-');
            return suffix.Length == 0 ? "pc" : suffix;
        }

        /// <summary>This machine's dedicated repo name (for UI display).</summary>
        public static string PerPcRepoName { get; } = $"{SharedRepoName}-{MachineSuffix}";

        /// <summary>The repo currently in use (for UI display).</summary>
        public static string EffectiveRepoName => RepoName;

        /// <summary>
        /// The library.db filename THIS machine reads/writes in its repository. The
        /// per-machine name predates per-PC repositories (it kept machines apart inside
        /// one shared repository) and stays so existing repositories keep their layout.
        /// library.db is non-portable anyway: it stores absolute, OS-specific ROM paths
        /// and back-/forward-slash art paths.
        /// </summary>
        public static string DbRepoFileName { get; } = $"library.{MachineSuffix}.db";

        // Not readonly: the offline self-test swaps in clients that answer from memory
        // (UseHttpHandler).
        private static HttpClient Http = CreateHttpClient(TimeSpan.FromSeconds(30));
        // Files over 1 MB have to be fetched as raw bytes (see DownloadFileAsync); those
        // transfers can be up to 100 MB, so they get a longer timeout of their own.
        private static HttpClient RawHttp = CreateHttpClient(TimeSpan.FromMinutes(10));
        private volatile string? _token;
        private string? _username;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _shaCache = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
            _gameLocks = new();

        private GitHubSyncService() { }

        // The Accept header is set per request (AuthedRequest), not on the client, so a
        // raw download can ask for raw bytes instead of JSON.
        private static HttpClient CreateHttpClient(TimeSpan timeout, HttpMessageHandler? handler = null)
        {
            var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = timeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Emutastic/cloud-sync");
            return http;
        }

        /// <summary>
        /// Test-only: sends every request through <paramref name="handler"/> (the offline
        /// self-test's in-memory GitHub) instead of the network.
        /// </summary>
        internal static void UseHttpHandler(HttpMessageHandler handler)
        {
            Http = CreateHttpClient(TimeSpan.FromSeconds(30), handler);
            RawHttp = CreateHttpClient(TimeSpan.FromMinutes(10), handler);
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
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
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

                CloudSyncLog.Write($"Upload failed {repoPath}: {FailureText(resp)}");
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

                CloudSyncLog.Write($"Delete failed {repoPath}: {FailureText(resp)}");
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

        /// <param name="quietIfMissing">A 404 is an expected answer for this caller (a manifest
        /// before the first sync, a game with no cloud save yet) rather than a failure worth
        /// logging.</param>
        public async Task<byte[]?> DownloadFileAsync(string repoPath,
            CancellationToken ct = default, bool quietIfMissing = false)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_username)) return null;

            try
            {
                string url = $"{ApiBase}/repos/{_username}/{RepoName}/contents/{repoPath}";
                using var req = AuthedRequest(HttpMethod.Get, url);
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    if (!(quietIfMissing && resp.StatusCode == HttpStatusCode.NotFound))
                        CloudSyncLog.Write($"Download failed {repoPath}: {FailureText(resp)}");
                    return null;
                }

                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string base64 = root.GetProperty("content").GetString() ?? "";
                base64 = base64.Replace("\n", "").Replace("\r", "");

                if (root.TryGetProperty("sha", out var shaProp))
                    _shaCache[repoPath] = shaProp.GetString() ?? "";

                // The Contents API inlines files only up to 1 MB. A bigger one (up to 100 MB)
                // comes back with an empty "content" and encoding "none" and has to be fetched
                // as raw bytes; decoding the empty string instead returned zero bytes, which
                // callers skipped silently.
                if (base64.Length == 0 && root.TryGetProperty("size", out var sizeProp) && sizeProp.GetInt64() > 0)
                    return await DownloadRawAsync(url, repoPath, ct).ConfigureAwait(false);
                return Convert.FromBase64String(base64);
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Download exception {repoPath}: {ex.Message}");
                return null;
            }
        }

        private async Task<byte[]?> DownloadRawAsync(string url, string repoPath, CancellationToken ct)
        {
            using var req = AuthedRequest(HttpMethod.Get, url);
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("application/vnd.github.raw");
            using var resp = await RawHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                CloudSyncLog.Write($"Download failed {repoPath} (raw): {FailureText(resp)}");
                return null;
            }
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        // Status of a failed response, plus GitHub's rate-limit headers when they are the reason.
        private static string FailureText(HttpResponseMessage resp)
        {
            var text = new StringBuilder($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
                    text.Append($", rate limit remaining {string.Join(",", remaining)}");
                if (resp.Headers.RetryAfter?.Delta is { } retry)
                    text.Append($", retry after {retry.TotalSeconds:0} s");
            }
            return text.ToString();
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

                byte[]? data = await DownloadFileAsync(path, ct, quietIfMissing: true);
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
        // Deliberately local (not in the manifest): it answers "did *I*
        // change since *my* last sync?", which is per-machine state. Lives in
        // DataRoot so portable installs carry it with their data.

        // Keyed by repo name, so the side-car written while this PC still synced to
        // the shared repository never passes for the state of its own repository.
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

        public record LocalSaveInfo(string RepoPath, string LocalPath, DateTime LastModifiedUtc, long SizeBytes, bool Compress = false);

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

        // ── Console-managed saves (memory cards, VMUs, save trees) ──────────────
        // The per-game ".srm" map above only covers frontend-managed SRAM. Cores
        // like PCSX2, PPSSPP, Dolphin and flycast write their OWN memory cards /
        // save trees into the save directory (= BatterySaves/{Console}). Those are
        // synced here, keyed by relative path (shared / console-level, not per-game),
        // gzip-compressed (mostly-empty cards shrink hugely), with caches, shader
        // caches, save-states and retired consoles excluded.

        private static readonly HashSet<string> UnsupportedSaveConsoles =
            new(StringComparer.OrdinalIgnoreCase) { "DOS" };

        private static bool IsUnsupportedConsole(string console)
            => UnsupportedSaveConsoles.Contains(console);

        // Path segments that are never battery saves: emulator caches, shader
        // caches, save-states (synced separately), dumps, logs, screenshots, and HD
        // texture packs — HdPackService installs PSP packs into
        // BatterySaves\PSP\PSP\TEXTURES, where they synced as if they were saves (a
        // single pack runs to thousands of files and hundreds of MB).
        private static readonly HashSet<string> ExcludedSaveSegments =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Cache", "Shaders", "ShaderCache", "StateSaves", "PPSSPP_STATE",
                "Dump", "Logs", "ScreenShots", "Screenshots", "Triforce", "WFS",
                "TEXTURES"
            };

        // A cloud path under BatterySaves/<Console>/ that the exclusions keep out of
        // sync (counted for the log, so skipped files are visible rather than silently
        // ignored).
        private static bool IsExcludedRepoPath(string repoPath)
        {
            string p = repoPath.EndsWith(".enc", StringComparison.Ordinal) ? repoPath[..^4] : repoPath;
            if (!p.StartsWith("BatterySaves/", StringComparison.Ordinal)
                || p.EndsWith(".srm", StringComparison.OrdinalIgnoreCase)) return false;
            string rest = p["BatterySaves/".Length..];
            int slash = rest.IndexOf('/');
            return slash > 0 && IsExcludedSavePath(rest[(slash + 1)..]);
        }

        // BIOS files, by the names System Files knows them under. They belong in the
        // System folder, but copies land in save trees too (GameCubeHandler mirrors
        // IPL.bin into Dolphin's User\GC\<region> at launch), and a backup of saves is
        // no place for any of them.
        private static readonly HashSet<string> BiosFileNames =
            Emutastic.Views.KnownBios.All.Select(b => System.IO.Path.GetFileName(b.Filename))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static bool IsExcludedSavePath(string rel)
        {
            if (rel.EndsWith(".srm", StringComparison.OrdinalIgnoreCase)) return true;
            string[] segs = rel.Split('/', '\\');
            if (BiosFileNames.Contains(segs[^1])) return true;
            bool underTitle = false;
            for (int i = 0; i < segs.Length; i++)
            {
                if (ExcludedSaveSegments.Contains(segs[i])) return true;
                if (i == segs.Length - 1) break;   // the rules below match folders, never a save's own name
                // Console system files: installed title content (3DS system archives and
                // installed titles in Azahar's nand\ and sdmc\, Wii channels in Dolphin's
                // User\Wii) and Azahar's ticket database. A title's saves sit beside its
                // content, under title\...\data.
                if (underTitle && segs[i].Equals("content", StringComparison.OrdinalIgnoreCase)) return true;
                if (segs[i].Equals("title", StringComparison.OrdinalIgnoreCase)) underTitle = true;
                if (i > 0 && segs[i].Equals("dbs", StringComparison.OrdinalIgnoreCase)
                    && segs[i - 1].Equals("nand", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Sprawling emulator trees where only specific subfolders are saves; the
        // rest (Dolphin's 160 MB+ User/Cache, Azahar's 40 MB+ shaders) must never
        // be uploaded. Null = sync the whole console folder minus the excludes.
        private static string[]? SaveAllowlist(string console) => console switch
        {
            "GameCube" => new[] { "User/GC", "User/Wii" },        // Dolphin memcards + Wii NAND
            "3DS"      => new[] { "Azahar/nand", "Azahar/sdmc" }, // 3DS save data
            _          => null,
        };

        /// <summary>
        /// Every console-managed save file on disk (memory cards, VMUs, PSP/3DS/GC
        /// save trees, arcade nvram, …) as repo-pathed, gzip-flagged entries. The
        /// per-game ".srm" files are handled by <see cref="BuildLocalSaveMap"/>.
        /// </summary>
        public static List<LocalSaveInfo> BuildExtraSaveMap()
        {
            var result = new List<LocalSaveInfo>();
            string root = AppPaths.GetFolder("BatterySaves");
            if (!System.IO.Directory.Exists(root)) return result;

            foreach (string consoleDir in System.IO.Directory.EnumerateDirectories(root))
            {
                string console = System.IO.Path.GetFileName(consoleDir);
                if (IsUnsupportedConsole(console)) continue;

                string[]? allow = SaveAllowlist(console);
                IEnumerable<string> bases = allow == null
                    ? new[] { consoleDir }
                    : allow.Select(a => System.IO.Path.Combine(
                               consoleDir, a.Replace('/', System.IO.Path.DirectorySeparatorChar)))
                           .Where(System.IO.Directory.Exists);

                foreach (string baseDir in bases)
                {
                    foreach (string full in System.IO.Directory.EnumerateFiles(
                                 baseDir, "*", System.IO.SearchOption.AllDirectories))
                    {
                        string rel = System.IO.Path.GetRelativePath(consoleDir, full);
                        if (IsExcludedSavePath(rel)) continue;

                        var fi = new System.IO.FileInfo(full);
                        string repoPath = $"BatterySaves/{console}/{rel.Replace('\\', '/')}";
                        result.Add(new LocalSaveInfo(
                            repoPath, full, fi.LastWriteTimeUtc, fi.Length, Compress: true));
                    }
                }
            }
            return result;
        }

        // Map a remote extra-save repo path back to its local path even when the
        // file doesn't exist on this PC yet (restoring this PC's backup after a
        // reinstall). Rejects the db, per-game ".srm" (handled elsewhere), retired
        // consoles, and excludes.
        private static bool TryResolveExtraSaveLocalPath(string repoPath, bool encrypted, out string localPath)
        {
            localPath = "";
            string p = repoPath;
            if (encrypted && p.EndsWith(".enc", StringComparison.Ordinal)) p = p[..^4];
            if (!p.StartsWith("BatterySaves/", StringComparison.Ordinal)) return false;
            if (p.EndsWith(".srm", StringComparison.OrdinalIgnoreCase)) return false;

            string rest = p["BatterySaves/".Length..];
            int slash = rest.IndexOf('/');
            if (slash <= 0) return false;
            string console = rest[..slash];
            string rel = rest[(slash + 1)..];
            if (rel.Length == 0 || IsUnsupportedConsole(console) || IsExcludedSavePath(rel)) return false;

            localPath = System.IO.Path.Combine(
                AppPaths.GetFolder("BatterySaves", console),
                rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return true;
        }

        private static byte[] GzipCompress(byte[] data)
        {
            using var ms = new System.IO.MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(
                       ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        private static byte[] GzipDecompress(byte[] data)
        {
            using var input = new System.IO.MemoryStream(data);
            using var gz = new System.IO.Compression.GZipStream(
                input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new System.IO.MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }

        /// <summary>
        /// Uploads this console's changed console-managed saves (memory cards, save
        /// trees). Lightweight per-console counterpart to <see cref="FullSyncAsync"/>,
        /// called on game close alongside the .srm upload. Fire-and-forget safe.
        /// </summary>
        public async Task<int> UploadConsoleExtraSavesAsync(string console, CancellationToken ct = default)
        {
            if (!IsAuthenticated || string.IsNullOrEmpty(console)) return 0;

            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            bool encrypted = cfg is { EncryptionEnabled: true }
                && !string.IsNullOrEmpty(cfg.PassphraseProtected);
            byte[]? key = encrypted
                ? DeriveKey(UnprotectString(cfg!.PassphraseProtected), _username ?? "") : null;
            string encSuffix = encrypted ? ".enc" : "";
            string prefix = $"BatterySaves/{console}/";

            int n = 0;
            foreach (var local in BuildExtraSaveMap())
            {
                if (ct.IsCancellationRequested) break;
                if (!local.RepoPath.StartsWith(prefix, StringComparison.Ordinal)) continue;

                string repoPath = local.RepoPath + encSuffix;
                if (_manifestCache.Files.TryGetValue(repoPath, out var entry)
                    && DateTime.TryParse(entry.LastModifiedUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var rm)
                    && local.LastModifiedUtc <= rm)
                    continue;

                try
                {
                    byte[] bytes = GzipCompress(System.IO.File.ReadAllBytes(local.LocalPath));
                    if (encrypted && key != null) bytes = Encrypt(bytes, key);
                    if (await UploadFileAsync(repoPath, bytes, ct).ConfigureAwait(false))
                    {
                        _manifestCache.Files[repoPath] = new SyncFileEntry
                        {
                            LastModifiedUtc = local.LastModifiedUtc.ToString("o"),
                            SizeBytes = local.SizeBytes
                        };
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    CloudSyncLog.Write($"Extra-save upload failed {repoPath}: {ex.Message}");
                }
            }
            if (n > 0) CloudSyncLog.Write($"Uploaded {n} {console} memory-card/save file(s)");
            return n;
        }

        /// <summary>
        /// Downloads this console's console-managed saves that are newer remotely
        /// than local (or missing locally). MUST complete before the core boots,
        /// since cores read memory cards / save trees from disk at init. Called on
        /// game launch alongside the .srm download (usually a fast no-op when the
        /// startup background sync already pulled them).
        /// </summary>
        public async Task<int> DownloadConsoleExtraSavesAsync(string console, CancellationToken ct = default)
        {
            if (!IsAuthenticated || string.IsNullOrEmpty(console)) return 0;

            var cfg = App.Configuration?.GetCloudSyncConfiguration();
            bool encrypted = cfg is { EncryptionEnabled: true }
                && !string.IsNullOrEmpty(cfg.PassphraseProtected);
            byte[]? key = encrypted
                ? DeriveKey(UnprotectString(cfg!.PassphraseProtected), _username ?? "") : null;
            string prefix = $"BatterySaves/{console}/";

            int n = 0;
            foreach (var (repoPath, entry) in _manifestCache.Files)
            {
                if (ct.IsCancellationRequested) break;
                if (!repoPath.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!TryResolveExtraSaveLocalPath(repoPath, encrypted, out var targetPath)) continue;

                bool hasMtime = DateTime.TryParse(entry.LastModifiedUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var remoteMtime);
                bool shouldDownload = !System.IO.File.Exists(targetPath)
                    || (hasMtime && remoteMtime > System.IO.File.GetLastWriteTimeUtc(targetPath));
                if (!shouldDownload) continue;

                try
                {
                    byte[]? data = await DownloadFileAsync(repoPath, ct).ConfigureAwait(false);
                    if (data != null && data.Length > 0)
                    {
                        if (encrypted && key != null) data = Decrypt(data, key);
                        data = GzipDecompress(data);
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targetPath)!);
                        System.IO.File.WriteAllBytes(targetPath, data);
                        if (hasMtime) System.IO.File.SetLastWriteTimeUtc(targetPath, remoteMtime);
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    CloudSyncLog.Write($"Extra-save download failed {repoPath}: {ex.Message}");
                }
            }
            if (n > 0) CloudSyncLog.Write($"Downloaded {n} {console} memory-card/save file(s)");
            return n;
        }

        // ── Full sync progress ──────────────────────────────────────────────

        public enum SyncPhase { Checking, Uploading, Downloading, Library, Finishing }

        /// <summary>One report from a running full sync. Done/Total count files within the
        /// current phase; Library and Finishing are single steps.</summary>
        public sealed record SyncProgress(SyncPhase Phase, int Done, int Total, int Uploaded, int Downloaded, int Errors);

        /// <summary>Raised from the sync's own thread: on every phase change and phase end,
        /// and at most every 100 ms in between.</summary>
        public event Action<SyncProgress>? SyncProgressChanged;

        /// <summary>The latest report of the running full sync, or null when none is running.</summary>
        public SyncProgress? CurrentProgress { get; private set; }

        /// <summary>The result of the last full sync that finished this session, or null.</summary>
        public SyncResult? LastResult { get; private set; }

        public static string DescribeProgress(SyncProgress p) => p.Phase switch
        {
            SyncPhase.Checking    => "checking what changed…",
            SyncPhase.Uploading   => $"uploading {p.Done:N0} of {p.Total:N0}",
            SyncPhase.Downloading => $"downloading {p.Done:N0} of {p.Total:N0}",
            SyncPhase.Library     => "syncing the library database…",
            _                     => "saving the sync manifest…",
        };

        public static string DescribeResult(SyncResult r) => r.Errors > 0
            ? $"{r.Uploaded:N0} up, {r.Downloaded:N0} down, {r.Errors:N0} failed (details in Logs\\cloudsync.log)"
            : $"{r.Uploaded:N0} up, {r.Downloaded:N0} down";

        private static string Megabytes(long bytes) =>
            bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.#} MB" : $"{bytes / 1000.0:0.#} KB";

        // Keeps progress events at a UI-friendly rate — every phase change and phase end goes
        // out, otherwise at most one report per 100 ms — and leaves a trail in cloudsync.log
        // every 250 files or 30 seconds, so a long sync is never silent.
        private sealed class ProgressThrottle
        {
            private readonly GitHubSyncService _owner;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private long _lastEventMs = -1000, _lastLogMs;
            private SyncPhase? _phase;
            private int _lastLoggedDone;

            public ProgressThrottle(GitHubSyncService owner) => _owner = owner;

            public void Report(SyncProgress p)
            {
                long now = _clock.ElapsedMilliseconds;
                bool phaseChanged = _phase != p.Phase;
                if (phaseChanged) { _phase = p.Phase; _lastLogMs = now; _lastLoggedDone = 0; }
                bool phaseEnd = p.Total > 0 && p.Done >= p.Total;
                _owner.CurrentProgress = p;
                if (phaseChanged || phaseEnd || now - _lastEventMs >= 100)
                {
                    _lastEventMs = now;
                    try { _owner.SyncProgressChanged?.Invoke(p); } catch { }
                }
                if (p.Phase is SyncPhase.Uploading or SyncPhase.Downloading && p.Done > _lastLoggedDone
                    && (phaseEnd || p.Done - _lastLoggedDone >= 250 || now - _lastLogMs >= 30_000))
                {
                    CloudSyncLog.Write($"{p.Phase}: {p.Done:N0} of {p.Total:N0} ({p.Errors:N0} failed so far)");
                    _lastLoggedDone = p.Done;
                    _lastLogMs = now;
                }
            }
        }

        private readonly object _fullSyncGate = new();
        private Task<SyncResult>? _fullSync;

        /// <summary>
        /// Runs a full two-way sync. While one is already running this returns THAT sync, so a
        /// second caller (Sync Now during the sync started at sign-in or launch) waits for its
        /// real result instead of starting a competing one.
        /// </summary>
        public Task<SyncResult> FullSyncAsync(DatabaseService db, CancellationToken ct = default)
        {
            if (!IsAuthenticated) return Task.FromResult(new SyncResult(0, 0, 0));
            lock (_fullSyncGate)
            {
                if (_fullSync is { IsCompleted: false }) return _fullSync;
                return _fullSync = Task.Run(() => RunFullSyncAsync(db, ct));
            }
        }

        private async Task<SyncResult> RunFullSyncAsync(DatabaseService db, CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            var progress = new ProgressThrottle(this);
            int uploaded = 0, downloaded = 0, errors = 0;
            try { SyncStateChanged?.Invoke(true); } catch { }
            progress.Report(new SyncProgress(SyncPhase.Checking, 0, 0, 0, 0, 0));
            try
            {
                var cfg = App.Configuration?.GetCloudSyncConfiguration();
                bool encrypted = cfg is { EncryptionEnabled: true }
                    && !string.IsNullOrEmpty(cfg.PassphraseProtected);
                byte[]? encKey = encrypted
                    ? DeriveKey(UnprotectString(cfg!.PassphraseProtected), _username ?? "")
                    : null;
                string encSuffix = encrypted ? ".enc" : "";
                CloudSyncLog.Write($"Full sync started: {_username}/{RepoName}{(encrypted ? " (encrypted)" : "")}");

                // This PC's repository may not exist yet: a sign-in from before every PC had
                // its own only ever created the shared one.
                await EnsureRepoExistsAsync(ct).ConfigureAwait(false);
                await RefreshShaCacheAsync(ct).ConfigureAwait(false);
                await LoadManifestAsync(ct).ConfigureAwait(false);
                CloudSyncLog.Write($"Cloud manifest lists {_manifestCache.Files.Count:N0} file(s)");

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
                    if (await DeleteFileAsync(stale, ct).ConfigureAwait(false))
                        CloudSyncLog.Write($"Removed stale variant: {stale}");
                }

                // UPLOAD: local files newer than the manifest.
                var localSaves = BuildLocalSaveMap(db);
                localSaves.AddRange(BuildExtraSaveMap());
                var toUpload = new List<LocalSaveInfo>();
                foreach (var local in localSaves)
                {
                    if (_manifestCache.Files.TryGetValue(local.RepoPath + encSuffix, out var entry)
                        && DateTime.TryParse(entry.LastModifiedUtc, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var remoteMtime)
                        && local.LastModifiedUtc <= remoteMtime)
                        continue;
                    toUpload.Add(local);
                }
                CloudSyncLog.Write($"Upload: {toUpload.Count:N0} of {localSaves.Count:N0} local save file(s) are new or newer here " +
                                   $"({Megabytes(toUpload.Sum(l => l.SizeBytes))})");

                int done = 0;
                progress.Report(new SyncProgress(SyncPhase.Uploading, 0, toUpload.Count, uploaded, downloaded, errors));
                foreach (var local in toUpload)
                {
                    if (ct.IsCancellationRequested) break;
                    string repoPath = local.RepoPath + encSuffix;
                    try
                    {
                        byte[] bytes = System.IO.File.ReadAllBytes(local.LocalPath);
                        if (local.Compress) bytes = GzipCompress(bytes);
                        if (encrypted && encKey != null) bytes = Encrypt(bytes, encKey);
                        if (await UploadFileAsync(repoPath, bytes, ct).ConfigureAwait(false))
                        {
                            _manifestCache.Files[repoPath] = new SyncFileEntry
                            {
                                LastModifiedUtc = local.LastModifiedUtc.ToString("o"),
                                SizeBytes = local.SizeBytes
                            };
                            uploaded++;
                        }
                        else errors++;   // UploadFileAsync logged why
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        CloudSyncLog.Write($"Upload failed {repoPath}: {ex.Message}");
                    }
                    progress.Report(new SyncProgress(SyncPhase.Uploading, ++done, toUpload.Count, uploaded, downloaded, errors));
                }

                // DOWNLOAD: build a lookup from repo path → (local path, gzip?) for ALL
                // games (including never-played), plus the console-managed extra saves.
                var allGames = db.GetGamesSyncMap();
                var repoToLocalPath = new Dictionary<string, (string LocalPath, bool Compressed)>();
                foreach (var g in allGames)
                {
                    if (string.IsNullOrEmpty(g.RomHash) || string.IsNullOrEmpty(g.Console)) continue;
                    string batteryDir = AppPaths.GetFolder("BatterySaves", g.Console);
                    string romStem = System.IO.Path.GetFileNameWithoutExtension(g.RomPath);
                    string localPath = System.IO.Path.Combine(batteryDir,
                        FileNameHelper.SanitizeFileName(romStem) + ".srm");
                    string rp = $"BatterySaves/{g.Console}/{g.RomHash}.srm" + encSuffix;
                    repoToLocalPath[rp] = (localPath, false);
                }
                foreach (var extra in BuildExtraSaveMap())
                    repoToLocalPath[extra.RepoPath + encSuffix] = (extra.LocalPath, true);

                // Remote files that are newer than local (or don't exist locally). Covers
                // per-game .srm (via repoToLocalPath) and console-managed extra saves —
                // including ones missing locally (restoring this PC's backup after a reinstall).
                var toDownload = new List<(string RepoPath, string TargetPath, bool Compressed,
                                           bool HasRemoteMtime, DateTime RemoteMtime, long SizeBytes)>();
                int skippedNonSave = 0;
                foreach (var (repoPath, entry) in _manifestCache.Files)
                {
                    if (!repoPath.StartsWith("BatterySaves/")) continue;

                    string targetPath;
                    bool compressed;
                    if (repoToLocalPath.TryGetValue(repoPath, out var mapped))
                    {
                        targetPath = mapped.LocalPath;
                        compressed = mapped.Compressed;
                    }
                    else if (TryResolveExtraSaveLocalPath(repoPath, encrypted, out var resolved))
                    {
                        targetPath = resolved;
                        compressed = true;
                    }
                    else
                    {
                        if (IsExcludedRepoPath(repoPath)) skippedNonSave++;
                        continue;
                    }

                    bool hasRemoteMtime = DateTime.TryParse(entry.LastModifiedUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var remoteMtime);
                    bool shouldDownload = !System.IO.File.Exists(targetPath)
                        || (hasRemoteMtime
                            && remoteMtime > System.IO.File.GetLastWriteTimeUtc(targetPath));
                    if (shouldDownload)
                        toDownload.Add((repoPath, targetPath, compressed, hasRemoteMtime, remoteMtime, entry.SizeBytes));
                }
                CloudSyncLog.Write($"Download: {toDownload.Count:N0} cloud file(s) are new or newer than this PC's copy " +
                                   $"({Megabytes(toDownload.Sum(d => d.SizeBytes))})" +
                                   (skippedNonSave > 0 ? $"; {skippedNonSave:N0} skipped as non-save data (texture packs, BIOS and system files, caches)" : ""));

                done = 0;
                progress.Report(new SyncProgress(SyncPhase.Downloading, 0, toDownload.Count, uploaded, downloaded, errors));
                foreach (var d in toDownload)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        byte[]? data = await DownloadFileAsync(d.RepoPath, ct).ConfigureAwait(false);
                        if (data == null)
                            errors++;   // listed in the manifest but not fetched; DownloadFileAsync logged why
                        else if (data.Length > 0)
                        {
                            if (encrypted && encKey != null) data = Decrypt(data, encKey);
                            if (d.Compressed) data = GzipDecompress(data);
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(d.TargetPath)!);
                            System.IO.File.WriteAllBytes(d.TargetPath, data);
                            // Stamp the manifest's mtime back onto the file — WriteAllBytes
                            // sets "now", which is newer than the manifest entry, so the NEXT
                            // full sync would see every save we just downloaded as locally
                            // modified and re-upload the lot (the "90 up with no changes" bug).
                            if (d.HasRemoteMtime) System.IO.File.SetLastWriteTimeUtc(d.TargetPath, d.RemoteMtime);
                            downloaded++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        CloudSyncLog.Write($"Download failed {d.RepoPath}: {ex.Message}");
                    }
                    progress.Report(new SyncProgress(SyncPhase.Downloading, ++done, toDownload.Count, uploaded, downloaded, errors));
                }

                progress.Report(new SyncProgress(SyncPhase.Library, 0, 1, uploaded, downloaded, errors));
                // Sync library database — use VACUUM INTO for a consistent snapshot
                // (raw File.ReadAllBytes on a WAL-mode DB risks partial checkpoint reads).
                //
                // The db needs a THREE-WAY decision, not a mine-vs-remote compare. Two
                // machines' databases legitimately differ (play history, caches), so
                // "is my content different from remote?" is always yes and alternating
                // syncs ping-pong uploads forever. Instead each machine remembers the
                // hash it last synced at (local side-car file, NOT the manifest):
                //   - my db changed since last sync            → upload (last-writer-wins)
                //   - only remote changed                      → download and adopt it
                //   - neither changed                          → quiet
                // mtime is useless here in all cases: the sync's own VACUUM connection
                // checkpoints the WAL on close, rewriting library.db's mtime every sync.
                try
                {
                    string dbPath = System.IO.Path.Combine(AppPaths.DataRoot, "library.db");
                    // Per-machine remote filename (library.<host>.db), kept from the shared
                    // repository so existing repositories keep their layout. The LOCAL path
                    // is always library.db.
                    string dbRepoPath = DbRepoFileName + encSuffix;
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
                                if (await UploadFileAsync(dbRepoPath, dbBytes, ct).ConfigureAwait(false))
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

                    // Download the remote DB when it changed and I didn't (restoring this
                    // PC's backup after a reinstall).
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
                            byte[]? remoteDb = await DownloadFileAsync(dbRepoPath, ct).ConfigureAwait(false);
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

                progress.Report(new SyncProgress(SyncPhase.Finishing, 0, 1, uploaded, downloaded, errors));
                await SaveManifestAsync(ct).ConfigureAwait(false);
                return Finish(new SyncResult(uploaded, downloaded, errors));
            }
            catch (Exception ex)
            {
                CloudSyncLog.Write($"Full sync failed: {ex.Message}");
                return Finish(new SyncResult(uploaded, downloaded, errors + 1));
            }
            finally
            {
                CurrentProgress = null;
                try { SyncStateChanged?.Invoke(false); } catch { }
            }

            SyncResult Finish(SyncResult result)
            {
                LastResult = result;
                CloudSyncLog.Write($"Full sync: {result.Uploaded} up, {result.Downloaded} down, {result.Errors} errors " +
                                   $"in {(int)clock.Elapsed.TotalMinutes}m {clock.Elapsed.Seconds:00}s");
                return result;
            }
        }

        // ── Background sync (app startup + initial login) ────────────────────
        // Runs a full sync OFF the UI thread so saves are already local by the
        // time a game launches — the per-game launch hook then just does a quick
        // local check instead of a multi-MB download. The sync raises
        // SyncStateChanged and SyncProgressChanged, so the main window can show
        // its phase and progress in the banner.

        /// <summary>True while a full sync is in flight (background or Sync Now).</summary>
        public bool IsSyncing => _fullSync is { IsCompleted: false };

        /// <summary>Raised with true when a full sync starts and false when it ends (after
        /// <see cref="LastResult"/> is set).</summary>
        public event Action<bool>? SyncStateChanged;

        /// <summary>
        /// Kicks off a full sync on a background thread (no-op if one is already
        /// running or the user isn't signed in). Called at app startup and right
        /// after device-flow login completes.
        /// </summary>
        public void StartBackgroundSync(DatabaseService db)
        {
            if (!IsAuthenticated) { CloudSyncLog.Write("Background sync skipped: not signed in"); return; }
            if (App.Configuration?.GetCloudSyncConfiguration() is not { Enabled: true })
            {
                CloudSyncLog.Write("Background sync skipped: sync is turned off");
                return;
            }
            if (IsSyncing) { CloudSyncLog.Write("Background sync skipped: a sync is already running"); return; }

            CloudSyncLog.Write("Background sync starting");
            _ = FullSyncAsync(db);   // reports its own progress, result and failures
        }

        /// <summary>
        /// Ensures this console's memory cards / save trees are on disk before the
        /// core boots. Prefers letting the in-flight background sync finish (bounded
        /// so a stalled sync never hangs launch) over starting a competing download;
        /// then does a targeted per-console pull, which is a fast no-op once the
        /// background sync has already fetched them. Call on the game-launch path.
        /// </summary>
        public async Task EnsureConsoleSavesReadyAsync(string console, CancellationToken ct = default)
        {
            if (!IsAuthenticated || string.IsNullOrEmpty(console)) return;

            var bg = _fullSync;
            if (bg is { IsCompleted: false })
            {
                try { await bg.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
                catch { /* timeout or fault — fall through to a targeted pull */ }
            }
            await DownloadConsoleExtraSavesAsync(console, ct).ConfigureAwait(false);
        }
    }
}

