using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore.Core.Security;

namespace CleanCore.Core.Cache;

public class BrowserCacheProvider : ICacheProvider
{
    public string ProviderId => "provider_browser_cache";
    public string DisplayName => "Web Browser Cache (Chrome, Edge, Firefox, Brave)";
    public string Category => "Browsers";
    public string Description => "Web page assets, cached images, and compiled script bytecode. Preserves cookies, passwords, history, and logins.";
    public CleanupRiskLevel RiskLevel => CleanupRiskLevel.Safe;
    public bool RequiresElevation => false;

    private record BrowserSpec(string Name, string ProcessName, string BaseDir, string[] CacheSubPaths);

    private readonly List<BrowserSpec> _specs;

    public BrowserCacheProvider()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        _specs = new List<BrowserSpec>
        {
            // Google Chrome
            new("Google Chrome", "chrome",
                Path.Combine(localAppData, "Google", "Chrome", "User Data"),
                [@"Default\Cache", @"Default\Code Cache", @"Default\GPUCache"]),

            // Microsoft Edge
            new("Microsoft Edge", "msedge",
                Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
                [@"Default\Cache", @"Default\Code Cache", @"Default\GPUCache"]),

            // Brave Browser
            new("Brave Browser", "brave",
                Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"),
                [@"Default\Cache", @"Default\Code Cache", @"Default\GPUCache"]),

            // Mozilla Firefox
            new("Mozilla Firefox", "firefox",
                Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles"),
                [@"cache2\entries"])
        };
    }

    public bool IsInstalled()
    {
        return _specs.Any(s => Directory.Exists(s.BaseDir));
    }

    public Task<CacheProviderResult> AnalyzeAsync(CancellationToken cancellationToken)
    {
        var result = new CacheProviderResult
        {
            ProviderId = ProviderId,
            DisplayName = DisplayName,
            Category = Category,
            Description = Description,
            RiskLevel = RiskLevel,
            RequiresElevation = RequiresElevation,
            IsInstalled = IsInstalled()
        };

        long totalBytes = 0;
        var runningBrowsers = new List<string>();

        foreach (var spec in _specs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(spec.BaseDir)) continue;

            bool isRunning = Process.GetProcessesByName(spec.ProcessName).Length > 0;
            if (isRunning)
            {
                runningBrowsers.Add(spec.Name);
            }

            if (spec.Name == "Mozilla Firefox")
            {
                // Enumerate Firefox profile directories
                try
                {
                    var profiles = Directory.GetDirectories(spec.BaseDir);
                    foreach (var profile in profiles)
                    {
                        var cacheDir = Path.Combine(profile, "cache2", "entries");
                        if (Directory.Exists(cacheDir))
                        {
                            long size = CalculateDirectorySize(cacheDir);
                            totalBytes += size;
                            result.Targets.Add(new CacheTargetItem
                            {
                                Path = cacheDir,
                                Name = $"{spec.Name} Cache ({Path.GetFileName(profile)})",
                                IsDirectory = true,
                                SizeBytes = size,
                                IsLocked = isRunning,
                                LockProcessName = isRunning ? $"{spec.Name} is running" : string.Empty,
                                IsSelected = !isRunning,
                                SideEffectDescription = "Cached web images and script entries. No cookies or passwords deleted."
                            });
                        }
                    }
                }
                catch { }
            }
            else
            {
                // Chromium-based browsers
                foreach (var relCache in spec.CacheSubPaths)
                {
                    var fullCachePath = Path.Combine(spec.BaseDir, relCache);
                    if (Directory.Exists(fullCachePath))
                    {
                        long size = CalculateDirectorySize(fullCachePath);
                        totalBytes += size;
                        result.Targets.Add(new CacheTargetItem
                        {
                            Path = fullCachePath,
                            Name = $"{spec.Name} ({Path.GetFileName(relCache)})",
                            IsDirectory = true,
                            SizeBytes = size,
                            IsLocked = isRunning,
                            LockProcessName = isRunning ? $"{spec.Name} is running" : string.Empty,
                            IsSelected = !isRunning,
                            SideEffectDescription = "Web page cached elements and shaders. Preserves cookies and user logins."
                        });
                    }
                }
            }
        }

        result.TotalSizeBytes = totalBytes;
        if (runningBrowsers.Count > 0)
        {
            result.IsRunning = true;
            result.RunningProcessWarning = $"Browsers currently open: {string.Join(", ", runningBrowsers)}. Close them to clean locked cache files.";
        }

        return Task.FromResult(result);
    }

    public async Task<CleanupJobResult> CleanAsync(List<CacheTargetItem> targets, bool moveToQuarantine, CancellationToken cancellationToken)
    {
        var job = new CleanupJobResult { TotalTargets = targets.Count };

        await Task.Run(() =>
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetResult = new CleanupTargetResult { Path = target.Path };

                if (!Directory.Exists(target.Path))
                {
                    targetResult.Success = true;
                    job.SuccessCount++;
                    job.Results.Add(targetResult);
                    continue;
                }

                if (!PathSafetyGuard.IsPathSafeForDeletion(target.Path, out string safetyError))
                {
                    targetResult.Success = false;
                    targetResult.ErrorMessage = safetyError;
                    job.FailureCount++;
                    job.Results.Add(targetResult);
                    continue;
                }

                long recoveredInTarget = 0;
                int filesDeleted = 0;
                int lockedCount = 0;

                try
                {
                    var dir = new DirectoryInfo(target.Path);
                    foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (PathSafetyGuard.IsFileLocked(file.FullName, out _))
                        {
                            lockedCount++;
                            continue;
                        }

                        try
                        {
                            long len = file.Length;
                            file.Delete();
                            recoveredInTarget += len;
                            filesDeleted++;
                        }
                        catch
                        {
                            lockedCount++;
                        }
                    }

                    targetResult.Success = filesDeleted > 0 || lockedCount == 0;
                    targetResult.BytesRecovered = recoveredInTarget;
                    targetResult.WasLocked = lockedCount > 0;
                    if (lockedCount > 0)
                    {
                        targetResult.ErrorMessage = $"{lockedCount} files were locked by running browser.";
                        job.SkippedLockedCount += lockedCount;
                    }

                    job.TotalBytesRecovered += recoveredInTarget;
                    job.SuccessCount++;
                }
                catch (Exception ex)
                {
                    targetResult.Success = false;
                    targetResult.ErrorMessage = ex.Message;
                    job.FailureCount++;
                }

                job.Results.Add(targetResult);
            }

            job.CompletedAt = DateTime.Now;
        }, cancellationToken);

        return job;
    }

    private static long CalculateDirectorySize(string path)
    {
        long size = 0;
        try
        {
            var dir = new DirectoryInfo(path);
            foreach (var f in dir.GetFiles("*", SearchOption.AllDirectories))
            {
                try { size += f.Length; } catch { }
            }
        }
        catch { }
        return size;
    }
}
