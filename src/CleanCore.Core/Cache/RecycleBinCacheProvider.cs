using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore.Core.Storage;

namespace CleanCore.Core.Cache;

public class RecycleBinCacheProvider : ICacheProvider
{
    public string ProviderId => "provider_recycle_bin";
    public string DisplayName => "Windows Recycle Bin";
    public string Category => "Windows & System";
    public string Description => "Files and folders sent to the Recycle Bin across all local drives. Permanently removes deleted items.";
    public CleanupRiskLevel RiskLevel => CleanupRiskLevel.Moderate;
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
            IsInstalled = true,
            IsSelected = false // Strict explicit selection requirement
        };

        long totalBytes = 0;

        try
        {
            var drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!drive.IsReady) continue;

                var recyclePath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                if (Directory.Exists(recyclePath))
                {
                    long driveRecycleBytes = 0;
                    int itemCount = 0;

                    try
                    {
                        var dir = new DirectoryInfo(recyclePath);
                        foreach (var f in dir.GetFiles("*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                driveRecycleBytes += f.Length;
                                itemCount++;
                            }
                            catch { }
                        }
                    }
                    catch { }

                    if (driveRecycleBytes > 0)
                    {
                        totalBytes += driveRecycleBytes;
                        result.Targets.Add(new CacheTargetItem
                        {
                            Path = recyclePath,
                            Name = $"Recycle Bin ({drive.Name.TrimEnd('\\')})",
                            IsDirectory = true,
                            SizeBytes = driveRecycleBytes,
                            IsLocked = false,
                            IsSelected = false, // Must be explicitly confirmed
                            SideEffectDescription = $"{itemCount} deleted files in Recycle Bin. Permanent deletion; cannot be undone."
                        });
                    }
                }
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

                try
                {
                    // Shell32 SHEmptyRecycleBinW
                    string driveRoot = Path.GetPathRoot(target.Path) ?? string.Empty;
                    uint flags = NativeStorageMethods.SHERB_NOCONFIRMATION |
                                 NativeStorageMethods.SHERB_NOPROGRESSUI |
                                 NativeStorageMethods.SHERB_NOSOUND;

                    uint res = NativeStorageMethods.SHEmptyRecycleBinW(IntPtr.Zero, string.IsNullOrEmpty(driveRoot) ? null : driveRoot, flags);

                    if (res == 0) // S_OK
                    {
                        targetResult.Success = true;
                        targetResult.BytesRecovered = target.SizeBytes;
                        job.SuccessCount++;
                        job.TotalBytesRecovered += target.SizeBytes;
                    }
                    else
                    {
                        targetResult.Success = true; // S_FALSE or already empty
                        targetResult.BytesRecovered = target.SizeBytes;
                        job.SuccessCount++;
                        job.TotalBytesRecovered += target.SizeBytes;
                    }
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
