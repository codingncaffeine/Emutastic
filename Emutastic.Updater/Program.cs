using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Emutastic.Updater;

internal static class Program
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "emutastic-update.log");
    private static readonly string UpdaterExeName = "Emutastic.Updater.exe";

    static int Main(string[] args)
    {
        try
        {
            string instructionsPath = Path.Combine(Path.GetTempPath(), "emutastic-update.json");
            if (!File.Exists(instructionsPath))
            {
                Log("No instructions file found. Nothing to do.");
                return 1;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(instructionsPath));
            var root = doc.RootElement;
            string? stagingDir = root.GetProperty("stagingDir").GetString();
            string? targetDir = root.GetProperty("targetDir").GetString();
            int mainPid = root.GetProperty("mainPid").GetInt32();

            if (string.IsNullOrEmpty(stagingDir) || string.IsNullOrEmpty(targetDir))
            {
                Log("Invalid instructions: stagingDir or targetDir is null/empty.");
                return 1;
            }

            if (!Directory.Exists(stagingDir))
            {
                Log($"Staging directory does not exist: {stagingDir}");
                return 1;
            }

            Log($"Updater started. PID to wait for: {mainPid}");
            Log($"Staging: {stagingDir}");
            Log($"Target:  {targetDir}");

            WaitForProcessExit(mainPid);

            using var mutex = new Mutex(false, "Emutastic_Updater_v1");
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                Log("Another updater instance is running. Aborting.");
                return 1;
            }

            var copiedFiles = new List<(string target, string backup)>();
            var createdFiles = new List<string>();

            try
            {
                var stagingFiles = new List<string>(Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories));
                if (stagingFiles.Count == 0)
                {
                    Log("Staging directory is empty. Aborting.");
                    return 1;
                }

                foreach (string sourceFile in stagingFiles)
                {
                    string relativePath = Path.GetRelativePath(stagingDir, sourceFile);

                    if (relativePath.Equals(UpdaterExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"  Skipped: {relativePath} (self)");
                        continue;
                    }

                    string targetFile = Path.Combine(targetDir, relativePath);
                    string? targetSubDir = Path.GetDirectoryName(targetFile);

                    if (targetSubDir != null)
                        Directory.CreateDirectory(targetSubDir);

                    string backupFile = targetFile + ".old";
                    if (File.Exists(targetFile))
                    {
                        if (File.Exists(backupFile))
                            File.Delete(backupFile);
                        File.Move(targetFile, backupFile);
                        copiedFiles.Add((targetFile, backupFile));
                    }
                    else
                    {
                        createdFiles.Add(targetFile);
                    }

                    File.Copy(sourceFile, targetFile, overwrite: true);
                    Log($"  Copied: {relativePath}");
                }

                Log($"Update complete: {copiedFiles.Count} replaced, {createdFiles.Count} new.");

                foreach (var entry in copiedFiles)
                {
                    try { File.Delete(entry.backup); } catch { }
                }

                try { Directory.Delete(stagingDir, true); } catch { }
                try { File.Delete(instructionsPath); } catch { }

                Log("Cleanup complete. Launching Emutastic...");
            }
            catch (Exception ex)
            {
                Log($"COPY FAILED: {ex.Message}");
                Log("Rolling back...");

                foreach (var entry in copiedFiles)
                {
                    try
                    {
                        if (File.Exists(entry.backup))
                        {
                            if (File.Exists(entry.target)) File.Delete(entry.target);
                            File.Move(entry.backup, entry.target);
                            Log($"  Restored: {Path.GetFileName(entry.target)}");
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        Log($"  Rollback failed for {Path.GetFileName(entry.target)}: {rollbackEx.Message}");
                    }
                }

                foreach (string created in createdFiles)
                {
                    try { if (File.Exists(created)) File.Delete(created); } catch { }
                }

                Log("Rollback complete. Launching original version...");
            }

            string exePath = Path.Combine(targetDir, "Emutastic.exe");
            if (File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--post-update",
                    WorkingDirectory = targetDir,
                    UseShellExecute = true,
                });
            }
            else
            {
                Log($"Cannot find {exePath} to relaunch.");
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            return 1;
        }
    }

    private static void WaitForProcessExit(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            Log($"Waiting for process {pid} ({proc.ProcessName}) to exit...");
            if (!proc.WaitForExit(10_000))
            {
                Log($"Process {pid} did not exit in 10s. Force-killing.");
                try { proc.Kill(); proc.WaitForExit(5_000); } catch { }
            }
            Log($"Process {pid} exited.");
        }
        catch (ArgumentException)
        {
            Log($"Process {pid} already exited.");
        }
    }

    private static void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }
}
