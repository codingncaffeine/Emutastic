using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emutastic.Configuration;

namespace Emutastic.Services
{
    /// <summary>
    /// <c>Emutastic.exe --selftest-cloudsync-offline [report.log] --portable</c>: a whole two-way
    /// sync against an in-memory fake of the GitHub Contents API, so the sync engine runs end to
    /// end with no account, no network and no risk to a real repository. Run it from a copy of the
    /// build output whose PortableData has never been used. Checks that this PC syncs to its own
    /// repository (created before the first upload), that saves go up and come down intact, that
    /// texture packs, BIOS files and console system files stay out in both directions, that files
    /// over 1 MB download through the raw fallback, that progress reports carry the right totals,
    /// that a second FullSyncAsync joins the running one, that a repeat sync transfers no saves,
    /// and that the log narrates it. Exit code 0 = pass, 1 = a check failed, 2 = incomplete.
    ///
    /// Port of the Linux build's CloudSyncOfflineSelfTest. The fake answers through an
    /// HttpMessageHandler rather than a loopback HttpListener, so it needs no URL reservation.
    /// Output goes to the report file and, when launched from a terminal, to that terminal too.
    /// </summary>
    internal static class CloudSyncOfflineSelfTest
    {
        public static int Run(string? reportPath)
        {
            string path = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportPath() : reportPath!;
            using var r = new Report(path);
            try
            {
                // Off the calling thread: App.OnStartup runs under the dispatcher's
                // synchronization context, and blocking on awaits that resume onto it deadlocks.
                return Task.Run(() => RunAsync(r)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                r.Line($"  [FAIL] unhandled: {ex}");
                r.Line("=== FAIL ===");
                return 1;
            }
        }

        private static string DefaultReportPath()
        {
            try { return Path.Combine(AppPaths.GetFolder("Logs"), "cloudsync-selftest.log"); }
            catch { return Path.Combine(AppContext.BaseDirectory, "cloudsync-selftest.log"); }
        }

        private static async Task<int> RunAsync(Report r)
        {
            r.Line("=== cloud sync offline self-test ===");
            string root = AppPaths.DataRoot;
            if (!AppPaths.IsPortable
                || File.Exists(Path.Combine(root, "config.json"))
                || File.Exists(Path.Combine(root, "library.db"))
                || Directory.Exists(Path.Combine(root, "BatterySaves")))
            {
                r.Line($"  [SKIP] needs --portable and an unused PortableData ({root}); run a fresh copy of the build output");
                r.Line("=== INCOMPLETE ===");
                return 2;
            }

            var config = new JsonConfigurationService(null);
            await config.LoadAsync();
            var cloud = config.GetCloudSyncConfiguration();
            cloud.Enabled = true;
            cloud.UsePerPcRepo = false;   // the retired per-PC toggle; must not matter any more
            config.SetCloudSyncConfiguration(cloud);
            App.Configuration = config;

            r.Line("--- one repository per PC");
            r.Check(GitHubSyncService.EffectiveRepoName == GitHubSyncService.PerPcRepoName
                    && GitHubSyncService.PerPcRepoName.StartsWith("emutastic-saves-", StringComparison.Ordinal)
                    && GitHubSyncService.PerPcRepoName.Length > "emutastic-saves-".Length,
                    $"this PC syncs to its own repository even with the old per-PC toggle off ({GitHubSyncService.EffectiveRepoName})");

            var fake = new FakeGitHub(GitHubSyncService.PerPcRepoName);
            GitHubSyncService.UseHttpHandler(fake);
            var svc = GitHubSyncService.Instance;
            typeof(GitHubSyncService).GetField("_token", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(svc, "offline-token");
            r.Check(await svc.ValidateTokenAsync() && svc.Username == "tester", "signed in against the fake API");

            // Cloud side: two saves (one over 1 MB, which GitHub does not inline), a texture pack, a
            // GameCube BIOS copy and a 3DS system archive.
            DateTime cloudTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            byte[] smallSave = RandomNumberGenerator.GetBytes(4096);
            byte[] bigSave = RandomNumberGenerator.GetBytes(1_500_000);   // random, so it stays over 1 MB gzipped
            byte[] cloudTexture = RandomNumberGenerator.GetBytes(2048);
            byte[] cloudBios = RandomNumberGenerator.GetBytes(2048);
            byte[] cloudSystem = RandomNumberGenerator.GetBytes(2048);
            const string SmallPath = "BatterySaves/PSP/PSP/SAVEDATA/ULUS00001/DATA.BIN";
            const string BigPath = "BatterySaves/PSP/PSP/SAVEDATA/ULUS00002/BIG.BIN";
            const string CloudTexturePath = "BatterySaves/PSP/PSP/TEXTURES/ULUS00001/tex.png";
            const string CloudBiosPath = "BatterySaves/GameCube/User/GC/EUR/IPL.bin";
            const string CloudSystemPath = "BatterySaves/3DS/Azahar/nand/title/0004009b/00010202/content/00000000.app";
            fake.Seed(SmallPath, Gzip(smallSave));
            fake.Seed(BigPath, Gzip(bigSave));
            fake.Seed(CloudTexturePath, Gzip(cloudTexture));
            fake.Seed(CloudBiosPath, Gzip(cloudBios));
            fake.Seed(CloudSystemPath, Gzip(cloudSystem));
            fake.SeedManifest(new Dictionary<string, (DateTime, long)>
            {
                [SmallPath] = (cloudTime, smallSave.Length),
                [BigPath] = (cloudTime, bigSave.Length),
                [CloudTexturePath] = (cloudTime, cloudTexture.Length),
                [CloudBiosPath] = (cloudTime, cloudBios.Length),
                [CloudSystemPath] = (cloudTime, cloudSystem.Length),
            });

            // This PC: one save of its own and a texture pack.
            string saves = AppPaths.GetFolder("BatterySaves");
            string localSave = Path.Combine(saves, "PSP", "PSP", "SAVEDATA", "ULUS00003", "LOCAL.BIN");
            string localTexture = Path.Combine(saves, "PSP", "PSP", "TEXTURES", "ULUS00003", "local.png");
            // GameCube: a memory card (a save, inside the allowlisted User\GC) beside a BIOS copy.
            string localCard = Path.Combine(saves, "GameCube", "User", "GC", "USA", "Card A", "01-TEST-save.gci");
            string localBios = Path.Combine(saves, "GameCube", "User", "GC", "USA", "IPL.bin");
            // A PlayStation BIOS in a console folder that syncs whole, and Azahar's NAND and SD card:
            // a system archive and its ticket stay out, while a game save under title\...\data and
            // the system settings under nand\data are saves.
            const string Id = "00000000000000000000000000000000";
            string localPs1Bios = Path.Combine(saves, "PS1", "scph5501.bin");
            string local3dsSystem = Path.Combine(saves, "3DS", "Azahar", "nand", "title", "0004009b", "00014002", "content", "00000000.app");
            string local3dsTicket = Path.Combine(saves, "3DS", "Azahar", "nand", "dbs", "ticket.db", "0004009B00014002.0000000000000000.tik");
            string local3dsSave = Path.Combine(saves, "3DS", "Azahar", "sdmc", "Nintendo 3DS", Id, Id, "title", "00040000", "00012300", "data", "00000001", "main");
            string local3dsSettings = Path.Combine(saves, "3DS", "Azahar", "nand", "data", Id, "sysdata", "00010017", "00000000", "config");
            foreach (string file in new[] { localSave, localTexture, localCard, localBios, localPs1Bios,
                                            local3dsSystem, local3dsTicket, local3dsSave, local3dsSettings })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(3000));
            }

            var reports = new ConcurrentQueue<GitHubSyncService.SyncProgress>();
            var states = new ConcurrentQueue<bool>();
            svc.SyncProgressChanged += reports.Enqueue;
            svc.SyncStateChanged += states.Enqueue;

            r.Line("--- first sync");
            var first = svc.FullSyncAsync(new DatabaseService());
            var joined = svc.FullSyncAsync(new DatabaseService());
            r.Check(ReferenceEquals(first, joined), "a second FullSyncAsync while one runs joins it instead of starting another");
            r.Check(svc.IsSyncing, "IsSyncing is true while it runs");
            var result = await first;

            r.Check(fake.RepoCreated && fake.RejectedBeforeCreate == 0, "this PC's repository was created before anything was uploaded");
            r.Check(result == new GitHubSyncService.SyncResult(5, 2, 0),
                    $"5 up (four saves and the library), 2 down, 0 errors — got {result.Uploaded} up, {result.Downloaded} down, {result.Errors} errors");
            r.Check(fake.Has("BatterySaves/PSP/PSP/SAVEDATA/ULUS00003/LOCAL.BIN"), "this PC's save was uploaded");
            r.Check(!fake.PutPaths.Any(p => p.Contains("/TEXTURES/")), "no texture pack was uploaded");
            r.Check(fake.Has("BatterySaves/GameCube/User/GC/USA/Card A/01-TEST-save.gci"), "a GameCube memory card was uploaded");
            r.Check(!fake.PutPaths.Any(p => p.EndsWith("/IPL.bin", StringComparison.OrdinalIgnoreCase)), "no GameCube BIOS copy was uploaded");
            r.Check(!fake.PutPaths.Any(p => p.EndsWith("/scph5501.bin", StringComparison.OrdinalIgnoreCase)), "no PlayStation BIOS was uploaded");
            r.Check(fake.Has($"BatterySaves/3DS/Azahar/sdmc/Nintendo 3DS/{Id}/{Id}/title/00040000/00012300/data/00000001/main")
                    && fake.Has($"BatterySaves/3DS/Azahar/nand/data/{Id}/sysdata/00010017/00000000/config"),
                    "a 3DS game save and the 3DS system settings were uploaded");
            r.Check(!fake.PutPaths.Any(p => p.Contains("/content/") || p.Contains("/nand/dbs/")), "no 3DS system archive or ticket was uploaded");
            string downloadedSmall = Path.Combine(saves, "PSP", "PSP", "SAVEDATA", "ULUS00001", "DATA.BIN");
            string downloadedBig = Path.Combine(saves, "PSP", "PSP", "SAVEDATA", "ULUS00002", "BIG.BIN");
            r.Check(File.Exists(downloadedSmall) && File.ReadAllBytes(downloadedSmall).SequenceEqual(smallSave), "a small cloud save came down intact");
            r.Check(File.Exists(downloadedBig) && File.ReadAllBytes(downloadedBig).SequenceEqual(bigSave), "a cloud save over 1 MB came down intact");
            r.Check(fake.RawRequests.Contains(BigPath), "…fetched raw, because the JSON response carried no content");
            r.Check(!File.Exists(Path.Combine(saves, "PSP", "PSP", "TEXTURES", "ULUS00001", "tex.png")), "the cloud texture pack was not downloaded");
            r.Check(!File.Exists(Path.Combine(saves, "GameCube", "User", "GC", "EUR", "IPL.bin")), "the cloud BIOS copy was not downloaded");
            r.Check(!File.Exists(Path.Combine(saves, "3DS", "Azahar", "nand", "title", "0004009b", "00010202", "content", "00000000.app")),
                    "the cloud 3DS system archive was not downloaded");
            r.Check(File.Exists(downloadedSmall) && File.GetLastWriteTimeUtc(downloadedSmall) == cloudTime, "a downloaded save carries the cloud's modified time");
            r.Check(fake.ManifestKeys().Contains("BatterySaves/PSP/PSP/SAVEDATA/ULUS00003/LOCAL.BIN"), "the manifest was saved with the upload in it");

            var list = reports.ToList();
            var up = list.Where(p => p.Phase == GitHubSyncService.SyncPhase.Uploading).ToList();
            var down = list.Where(p => p.Phase == GitHubSyncService.SyncPhase.Downloading).ToList();
            r.Check(list.Count > 0 && list[0].Phase == GitHubSyncService.SyncPhase.Checking, "progress starts in the checking phase");
            r.Check(up.Count > 0 && up.All(p => p.Total == 4) && up[0].Done == 0 && up[^1].Done == 4,
                    $"upload progress runs 0 → 4 of 4 ({up.Count} report(s))");
            r.Check(down.Count > 0 && down.All(p => p.Total == 2) && down[0].Done == 0 && down[^1].Done == 2,
                    $"download progress runs 0 → 2 of 2 ({down.Count} report(s))");
            r.Check(down.Zip(down.Skip(1)).All(z => z.Second.Done >= z.First.Done), "download progress never goes backwards");
            r.Check(list.Any(p => p.Phase == GitHubSyncService.SyncPhase.Library) && list.Any(p => p.Phase == GitHubSyncService.SyncPhase.Finishing),
                    "the library and manifest steps report too");
            r.Check(states.SequenceEqual(new[] { true, false }), $"SyncStateChanged fired true, then false ({string.Join(", ", states)})");
            r.Check(svc.LastResult == result && svc.CurrentProgress == null && !svc.IsSyncing, "afterwards LastResult is set and nothing reports as running");

            string logPath = Path.Combine(AppPaths.GetFolder("Logs"), "cloudsync.log");
            string log = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
            r.Check(log.Contains($"Full sync started: tester/{GitHubSyncService.PerPcRepoName}"), "the log records the start and the repository");
            r.Check(log.Contains("Upload: 4 of 4 local save file(s)"), "the log records the upload plan");
            r.Check(log.Contains("Download: 2 cloud file(s)") && log.Contains("3 skipped as non-save data"),
                    "the log records the download plan and the skipped texture pack, BIOS copy and 3DS system archive");
            r.Check(log.Contains("Full sync: 5 up, 2 down, 0 errors"), "the log records the result");

            r.Line("--- repeat sync");
            int putsBefore = fake.PutPaths.Count(p => p.StartsWith("BatterySaves/", StringComparison.Ordinal));
            var again = await svc.FullSyncAsync(new DatabaseService());
            int putsAfter = fake.PutPaths.Count(p => p.StartsWith("BatterySaves/", StringComparison.Ordinal));
            r.Check(again.Downloaded == 0 && again.Errors == 0 && putsAfter == putsBefore,
                    $"nothing changed, so no save moves either way (got {again.Downloaded} down, {putsAfter - putsBefore} save upload(s), {again.Errors} errors)");

            r.Line(r.Failures == 0 ? "=== PASS ===" : $"=== FAIL ({r.Failures} check(s)) ===");
            return r.Failures == 0 ? 0 : 1;
        }

        private static byte[] Gzip(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        // ── Output: the report file, plus the parent terminal when there is one ─────────────
        private sealed class Report : IDisposable
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool AttachConsole(uint dwProcessId);
            private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

            private readonly StreamWriter? _file;
            private readonly StreamWriter? _console;
            public int Failures;

            public Report(string path)
            {
                try
                {
                    // A WinExe launched from cmd / PowerShell can borrow the parent's console;
                    // Console.Out was bound before that console existed, so re-open stdout.
                    if (AttachConsole(ATTACH_PARENT_PROCESS))
                        _console = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                }
                catch { }
                try
                {
                    string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    _file = new StreamWriter(path, append: false) { AutoFlush = true };
                }
                catch { }
            }

            public void Line(string text)
            {
                try { _console?.WriteLine(text); } catch { }
                try { _file?.WriteLine(text); }    catch { }
            }

            public void Check(bool ok, string what)
            {
                Line($"  [{(ok ? "PASS" : "FAIL")}] {what}");
                if (!ok) Failures++;
            }

            public void Dispose()
            {
                try { _file?.Dispose(); } catch { }
                try { _console?.Dispose(); } catch { }
            }
        }

        /// <summary>The handful of GitHub endpoints the sync engine calls, answered in memory. The
        /// repository starts out missing, like a PC's repository before its first sync.</summary>
        private sealed class FakeGitHub : HttpMessageHandler
        {
            private readonly string _repoName;
            private readonly ConcurrentDictionary<string, byte[]> _files = new();
            private volatile bool _repoCreated;
            private int _rejectedBeforeCreate;

            public FakeGitHub(string repoName) => _repoName = repoName;

            public ConcurrentQueue<string> PutPaths { get; } = new();
            public ConcurrentQueue<string> RawRequests { get; } = new();
            public bool RepoCreated => _repoCreated;
            public int RejectedBeforeCreate => _rejectedBeforeCreate;

            public void Seed(string path, byte[] bytes) => _files[path] = bytes;
            public bool Has(string path) => _files.ContainsKey(path);

            public void SeedManifest(IDictionary<string, (DateTime Modified, long Size)> entries)
            {
                var manifest = new GitHubSyncService.SyncManifest();
                foreach (var (path, (modified, size)) in entries)
                    manifest.Files[path] = new GitHubSyncService.SyncFileEntry { LastModifiedUtc = modified.ToString("o"), SizeBytes = size };
                _files["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest);
            }

            public HashSet<string> ManifestKeys() =>
                _files.TryGetValue("manifest.json", out var bytes)
                    ? JsonSerializer.Deserialize<GitHubSyncService.SyncManifest>(bytes)!.Files.Keys.ToHashSet()
                    : new HashSet<string>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                await Task.Delay(15, ct);   // keeps a sync running long enough for a second caller to join it
                var uri = request.RequestUri!;
                if (!uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                    return Json(HttpStatusCode.NotFound, new { message = "Not Found" });

                string path = Uri.UnescapeDataString(uri.AbsolutePath);
                string repo = $"/repos/tester/{_repoName}";
                if (path == "/user") return Json(HttpStatusCode.OK, new { login = "tester" });
                if (path == "/user/repos" && request.Method == HttpMethod.Post)
                {
                    _repoCreated = true;
                    return Json(HttpStatusCode.Created, new { name = _repoName });
                }
                if (!_repoCreated)
                {
                    if (path.StartsWith(repo + "/contents/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
                        Interlocked.Increment(ref _rejectedBeforeCreate);
                    return Json(HttpStatusCode.NotFound, new { message = "Not Found" });
                }
                if (path == repo) return Json(HttpStatusCode.OK, new { name = _repoName });
                if (path == repo + "/git/trees/HEAD")
                {
                    var tree = _files.Select(kv => new { path = kv.Key, type = "blob", sha = Sha(kv.Value) }).ToArray();
                    return Json(HttpStatusCode.OK, new { tree });
                }

                string contents = repo + "/contents/";
                if (path.StartsWith(contents, StringComparison.Ordinal))
                {
                    string repoPath = path[contents.Length..];
                    if (request.Method == HttpMethod.Get)
                    {
                        if (!_files.TryGetValue(repoPath, out var bytes))
                            return Json(HttpStatusCode.NotFound, new { message = "Not Found" });
                        if (request.Headers.Accept.Any(a => a.MediaType?.Contains("raw", StringComparison.Ordinal) == true))
                        {
                            RawRequests.Enqueue(repoPath);
                            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                        }
                        bool inline = bytes.Length <= 1_000_000;   // GitHub inlines files up to 1 MB only
                        return Json(HttpStatusCode.OK, new
                        {
                            path = repoPath, sha = Sha(bytes), size = bytes.Length,
                            encoding = inline ? "base64" : "none",
                            content = inline ? Convert.ToBase64String(bytes) : "",
                        });
                    }
                    if (request.Method == HttpMethod.Put)
                    {
                        using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                        byte[] body = Convert.FromBase64String(doc.RootElement.GetProperty("content").GetString() ?? "");
                        _files[repoPath] = body;
                        PutPaths.Enqueue(repoPath);
                        return Json(HttpStatusCode.Created, new { content = new { sha = Sha(body) } });
                    }
                    if (request.Method == HttpMethod.Delete)
                    {
                        _files.TryRemove(repoPath, out _);
                        return Json(HttpStatusCode.OK, new { });
                    }
                }
                return Json(HttpStatusCode.NotFound, new { message = "Not Found" });
            }

            private static HttpResponseMessage Json(HttpStatusCode status, object body)
            {
                var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                return new HttpResponseMessage(status) { Content = content };
            }

            private static string Sha(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        }
    }
}
