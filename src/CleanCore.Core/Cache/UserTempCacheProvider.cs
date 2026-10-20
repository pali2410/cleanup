using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore.Core.Security;

namespace CleanCore.Core.Cache;

public class UserTempCacheProvider : ICacheProvider
{
    public string ProviderId => "provider_user_temp";
    public string DisplayName => "User Temporary Files & Crash Dumps";
    public string Category => "Windows & System";
    public string Description => "Temporary application scratch files, crash memory dumps, and Windows Error Reporting archives.";
    public CleanupRiskLevel RiskLevel => CleanupRiskLevel.Safe;
    public bool RequiresElevation => false;

    public bool IsInstalled() => true;

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
            IsInstalled = true
        };

        var searchDirs = new List<string>
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "WER", "ReportArchive")
        };

        long totalBytes = 0;

        foreach (var dir in searchDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir)) continue;

            try
            {
                var dirInfo = new DirectoryInfo(dir);
                var files = dirInfo.GetFiles("*", SearchOption.TopDirectoryOnly);

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        // Check if file is locked
                        bool isLocked = PathSafetyGuard.IsFileLocked(file.FullName, out string lockReason);
                        long length = file.Length;
                        totalBytes += length;

                        result.Targets.Add(new CacheTargetItem
                        {
                            Path = file.FullName,
                            Name = file.Name,
                            IsDirectory = false,
                            SizeBytes = length,
                            LastModified = file.LastWriteTime,
                            IsLocked = isLocked,
                            LockProcessName = isLocked ? lockReason : string.Empty,
                            IsSelected = !isLocked,
                            SideEffectDescription = "Temporary scratch file; safe to delete."
                        });
                    }
                    catch { }
                }

                // Also check subdirectories in Temp
                var subDirs = dirInfo.GetDirectories("*", SearchOption.TopDirectoryOnly);
                foreach (var subDir in subDirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        long dirSize = CalculateDirectorySize(subDir);
                        totalBytes += dirSize;

                        result.Targets.Add(new CacheTargetItem
                        {
                            Path = subDir.FullName,
                            Name = subDir.Name,
                            IsDirectory = true,
                            SizeBytes = dirSize,
                            LastModified = subDir.LastWriteTime,
                            IsLocked = false,
                            IsSelected = true,
                            SideEffectDescription = "Temporary folder; safe to delete."
                        });
                    }
                    catch { }
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

                if (!PathSafetyGuard.IsPathSafeForDeletion(target.Path, out string safetyError))
                {
                    targetResult.Success = false;
                    targetResult.ErrorMessage = safetyError;
                    job.FailureCount++;
                    job.Results.Add(targetResult);
                    continue;
                }

                if (PathSafetyGuard.IsFileLocked(target.Path, out string lockError))
                {
                    targetResult.Success = false;
                    targetResult.WasLocked = true;
                    targetResult.ErrorMessage = lockError;
                    job.SkippedLockedCount++;
                    job.Results.Add(targetResult);
                    continue;
                }

                try
                {
                    long reclaimed = target.SizeBytes;
                    if (target.IsDirectory && Directory.Exists(target.Path))
                    {
                        Directory.Delete(target.Path, true);
                    }
                    else if (File.Exists(target.Path))
                    {
                        File.Delete(target.Path);
                    }

                    targetResult.Success = true;
                    targetResult.BytesRecovered = reclaimed;
                    job.SuccessCount++;
                    job.TotalBytesRecovered += reclaimed;
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

    private static long CalculateDirectorySize(DirectoryInfo dir)
    {
        long size = 0;
        try
        {
            foreach (var f in dir.GetFiles("*", SearchOption.AllDirectories))
            {
                try { size += f.Length; } catch { }
            }
        }
        catch { }
        return size;
    }
}
