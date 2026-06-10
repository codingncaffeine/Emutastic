using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    public sealed record AppUpdate(string Tag, string DownloadUrl, string ReleaseNotes, string? Digest = null);

    public static class UpdateService
    {
        private const string GitHubApiUrl =
            "https://api.github.com/repos/codingncaffeine/Emutastic/releases/latest";

        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Emutastic/updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        public static async Task<AppUpdate?> CheckAsync(CancellationToken ct)
        {
            try
            {
                var prefs = App.Configuration?.GetUserPreferences();
                if (prefs?.CheckForUpdates == false) return null;

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                using var resp = await Http.GetAsync(GitHubApiUrl, linked.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                string json = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(tag)) return null;

                if (!IsNewer(tag)) return null;

                string downloadUrl = "";
                string? digest = null;
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (name != null && name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                            && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.TryGetProperty("browser_download_url", out var u)
                                ? u.GetString() ?? "" : "";
                            digest = asset.TryGetProperty("digest", out var d)
                                ? d.GetString() : null;   // "sha256:…" once GitHub has computed it
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(downloadUrl)) return null;

                string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

                return new AppUpdate(tag, downloadUrl, notes, digest);
            }
            catch
            {
                return null;
            }
        }

        public static async Task ApplyAsync(AppUpdate update, IProgress<string>? status, CancellationToken ct)
        {
            string exeDir = AppPaths.GetExeFolder();

            // Pre-flight write test
            status?.Report("Checking permissions…");
            string testFile = Path.Combine(exeDir, ".update-writetest");
            try
            {
                File.WriteAllText(testFile, "");
                File.Delete(testFile);
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "Cannot update from this location — the directory is read-only or protected. " +
                    "Download the latest release manually from the releases page.");
            }

            // Download with progress
            status?.Report($"Downloading {update.Tag}…");
            string zipPath = Path.Combine(Path.GetTempPath(), $"Emutastic-update-{update.Tag}.zip");
            using (var resp = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dest = File.Create(zipPath);
                byte[] buf = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dest.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                        status?.Report($"Downloading {update.Tag}… {downloaded * 100 / total}%");
                }
            }

            // Integrity gate: verify the downloaded zip against GitHub's published
            // SHA-256 digest BEFORE we extract it over our own install. A mismatch
            // means the download was corrupted or tampered with — abort rather than
            // stage and run it. Falls back gracefully when no digest is published yet.
            if (!string.IsNullOrEmpty(update.Digest))
            {
                status?.Report("Verifying…");
                string expected = update.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? update.Digest[7..] : update.Digest;
                string actual = await Sha256HexAsync(zipPath, ct);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    Trace.WriteLine($"[Update] digest mismatch: expected {expected}, got {actual}");
                    try { File.Delete(zipPath); } catch { }
                    throw new InvalidOperationException(
                        "Update integrity check failed — the download didn't match the expected "
                        + "checksum, so nothing was installed. Try again, or update from the releases page.");
                }
                Trace.WriteLine("[Update] SHA-256 digest verified");
            }
            else
            {
                Trace.WriteLine("[Update] no SHA-256 digest published for this asset — skipping verification");
            }

            // Extract to staging (off UI thread)
            status?.Report("Extracting…");
            string stagingDir = Path.Combine(Path.GetTempPath(), $"Emutastic-staging-{update.Tag}");
            await Task.Run(() =>
            {
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, true);
                ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);
            }, ct);

            // Validate staging
            string stagedExe = Path.Combine(stagingDir, "Emutastic.exe");
            if (!File.Exists(stagedExe))
                throw new FileNotFoundException("Downloaded update is missing Emutastic.exe — aborting.");

            // Write instructions
            status?.Report("Preparing update…");
            string instructionsPath = Path.Combine(Path.GetTempPath(), "emutastic-update.json");
            int pid = Environment.ProcessId;
            string instructionsJson = JsonSerializer.Serialize(new
            {
                stagingDir,
                targetDir = exeDir,
                mainPid = pid,
            });
            File.WriteAllText(instructionsPath, instructionsJson);

            // Launch updater from staging
            string updaterPath = Path.Combine(stagingDir, "Emutastic.Updater.exe");
            if (!File.Exists(updaterPath))
            {
                // Fallback: use the updater from the current install
                string localUpdater = Path.Combine(exeDir, "Emutastic.Updater.exe");
                if (File.Exists(localUpdater))
                    updaterPath = localUpdater;
                else
                    throw new FileNotFoundException("Emutastic.Updater.exe not found in update or current install.");
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = updaterPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to launch updater: {ex.Message}. Download the latest release manually.");
            }

            status?.Report("Restarting…");
            System.Windows.Application.Current.Shutdown();
        }

        private static async Task<string> Sha256HexAsync(string path, CancellationToken ct)
        {
            await using var fs = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static bool IsNewer(string remoteTag)
        {
            string trimmed = remoteTag.TrimStart('v', 'V').Trim();
            if (!Version.TryParse(trimmed, out var remote)) return false;
            var local = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (local == null) return false;
            var localTrimmed = new Version(local.Major, local.Minor, local.Build);
            var remoteTrimmed = new Version(remote.Major, remote.Minor, remote.Build);
            return remoteTrimmed.CompareTo(localTrimmed) > 0;
        }

        public static void CleanupOldFiles()
        {
            try
            {
                string exeDir = AppPaths.GetExeFolder();
                foreach (var f in Directory.EnumerateFiles(exeDir, "*.old", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { }
                }

                foreach (var d in Directory.EnumerateDirectories(Path.GetTempPath(), "Emutastic-staging-*"))
                {
                    try { Directory.Delete(d, true); } catch { }
                }

                string zipPattern = "Emutastic-update-*.zip";
                foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), zipPattern))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }
    }
}
