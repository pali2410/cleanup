using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore.Core.Security;

namespace CleanCore.Core.Cache;

public class WindowsTempCacheProvider : ICacheProvider
{
    public string ProviderId => "provider_windows_temp";
    public string DisplayName => "Windows System Temp (Older than 24 Hours)";
    public string Category => "Windows & System";
    public string Description => "System-level temporary files created by Windows services and installers. Only files older than 24 hours are targeted.";
    public CleanupRiskLevel RiskLevel => CleanupRiskLevel.Safe;
    public bool RequiresElevation => true;

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

        var winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        if (!Directory.Exists(winTemp)) return Task.FromResult(result);

        long totalBytes = 0;
        var thresholdDate = DateTime.Now.AddHours(-24);

        try
        {
            var dirInfo = new DirectoryInfo(winTemp);
            foreach (var fi in dirInfo.GetFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Only target files older than 24h to avoid interfering with ongoing updates/installers
                    if (fi.LastWriteTime < thresholdDate)
                    {
                        bool isLocked = PathSafetyGuard.IsFileLocked(fi.FullName, out string lockReason);
                        long length = fi.Length;
                        totalBytes += length;

                        result.Targets.Add(new CacheTargetItem
                        {
                            Path = fi.FullName,
                            Name = fi.Name,
                            IsDirectory = false,
                            SizeBytes = length,
                            LastModified = fi.LastWriteTime,
                            IsLocked = isLocked,
                            LockProcessName = isLocked ? lockReason : string.Empty,
                            IsSelected = !isLocked,
                            SideEffectDescription = "Obsolete system temp file (>24h old)."
                        });
                    }
                }
                catch { }
            }
        }
        catch { }

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
                    if (File.Exists(target.Path))
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
}
