using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore.Core.Security;

namespace CleanCore.Core.Cache;

public class DeveloperCacheProvider : ICacheProvider
{
    public string ProviderId => "provider_developer_cache";
    public string DisplayName => "Developer Tool & Package Caches";
    public string Category => "Developer";
    public string Description => "Local package caches for npm, NuGet, pip, Gradle, and Maven. Packages will be redownloaded on future builds if cleaned.";
    public CleanupRiskLevel RiskLevel => CleanupRiskLevel.Moderate;
    public bool RequiresElevation => false;

    private record DevCacheSpec(string Name, string Path, string ToolDescription);

    private readonly List<DevCacheSpec> _specs;

    public DeveloperCacheProvider()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        _specs = new List<DevCacheSpec>
        {
            new("npm Cache", Path.Combine(localAppData, "npm-cache"), "Node.js npm downloaded tarballs and metadata."),
            new("npm Roaming Cache", Path.Combine(appData, "npm-cache"), "npm global download store."),
            new("NuGet Package Cache", Path.Combine(userProfile, ".nuget", "packages"), ".NET NuGet global-packages folder."),
            new("pip Cache", Path.Combine(localAppData, "pip", "cache"), "Python pip downloaded wheels and sdist packages."),
            new("Gradle Build Cache", Path.Combine(userProfile, ".gradle", "caches"), "Java/Kotlin Gradle compiled build caches."),
            new("Maven Repository", Path.Combine(userProfile, ".m2", "repository"), "Apache Maven downloaded jars and pom files."),
            new("pnpm Store", Path.Combine(localAppData, "pnpm", "store"), "pnpm hard-link content-addressable store."),
            new("Yarn Cache", Path.Combine(localAppData, "Yarn", "Cache"), "Yarn dependency cache archives.")
        };
    }

    public bool IsInstalled()
    {
        return _specs.Exists(s => Directory.Exists(s.Path));
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

        foreach (var spec in _specs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(spec.Path)) continue;

            try
            {
                long size = CalculateDirectorySize(spec.Path);
                if (size > 0)
                {
                    totalBytes += size;
                    result.Targets.Add(new CacheTargetItem
                    {
                        Path = spec.Path,
                        Name = spec.Name,
                        IsDirectory = true,
                        SizeBytes = size,
                        IsLocked = false,
                        IsSelected = false, // Require explicit selection due to re-download side effect
                        SideEffectDescription = $"{spec.ToolDescription} Warning: Cleaning will require tools to re-download dependencies when rebuilding."
                    });
                }
            }
            catch { }
        }

        result.TotalSizeBytes = totalBytes;
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
                        targetResult.ErrorMessage = $"{lockedCount} files were in use.";
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
