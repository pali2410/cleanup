using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore.Core.Quarantine;
using CleanCore.Core.Security;
using CleanCore.Core.Storage;

namespace CleanCore.Core.Cleanup;

public interface ICleanupExecutionEngine
{
    CleanupPlan CreatePlanFromCandidates(IEnumerable<CleanupCandidate> candidates);
    Task<CleanupJobResult> ExecutePlanAsync(
        CleanupPlan plan,
        bool enableQuarantine,
        IProgress<ScanProgressReport>? progress,
        CancellationToken cancellationToken);
}

public class CleanupExecutionEngine : ICleanupExecutionEngine
{
    private readonly IQuarantineManager _quarantineManager;
    private readonly IDriveService _driveService;

    public CleanupExecutionEngine(IQuarantineManager quarantineManager, IDriveService driveService)
    {
        _quarantineManager = quarantineManager;
        _driveService = driveService;
    }

    public CleanupPlan CreatePlanFromCandidates(IEnumerable<CleanupCandidate> candidates)
    {
        var plan = new CleanupPlan();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long excludedBytes = 0;

        foreach (var c in candidates)
        {
            // Re-check safety guard
            if (!PathSafetyGuard.IsPathSafeForDeletion(c.Path, out _))
            {
                excludedBytes += c.SizeBytes;
                continue;
            }

            // Prevent double counting exact paths or sub-targets if parent directory is already targeted
            if (seenPaths.Contains(c.Path))
            {
                continue;
            }

            seenPaths.Add(c.Path);
            plan.Candidates.Add(c);
        }

        plan.ProtectedSpaceExcludedBytes = excludedBytes;
        return plan;
    }

    public async Task<CleanupJobResult> ExecutePlanAsync(
        CleanupPlan plan,
        bool enableQuarantine,
        IProgress<ScanProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var selectedCandidates = plan.Candidates.Where(c => c.IsSelected).ToList();
        var job = new CleanupJobResult
        {
            TotalTargets = selectedCandidates.Count,
            StartedAt = DateTime.Now
        };

        var report = new ScanProgressReport
        {
            CurrentPhase = "Executing Cleanup Plan..."
        };

        int processed = 0;

        await Task.Run(async () =>
        {
            foreach (var candidate in selectedCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;

                report.CurrentDirectory = candidate.Path;
                report.FilesDiscovered = processed;
                report.EstimatedPercent = (double)processed / Math.Max(1, selectedCandidates.Count) * 100.0;
                progress?.Report(report);

                var targetResult = new CleanupTargetResult
                {
                    CandidateId = candidate.Id,
                    Path = candidate.Path
                };

                // 1. Re-validate path safety
                if (!PathSafetyGuard.IsPathSafeForDeletion(candidate.Path, out string safetyError))
                {
                    targetResult.Success = false;
                    targetResult.ErrorMessage = $"Safety rejection: {safetyError}";
                    job.FailureCount++;
                    job.Results.Add(targetResult);
                    continue;
                }

                // 2. Check if file exists and lock status
                bool isDir = Directory.Exists(candidate.Path);
                bool isFile = File.Exists(candidate.Path);

                if (!isDir && !isFile)
                {
                    // Already gone
                    targetResult.Success = true;
                    targetResult.ErrorMessage = "Item was already removed.";
                    job.SuccessCount++;
                    job.Results.Add(targetResult);
                    continue;
                }

                if (isFile && PathSafetyGuard.IsFileLocked(candidate.Path, out string lockError))
                {
                    targetResult.Success = false;
                    targetResult.WasLocked = true;
                    targetResult.ErrorMessage = lockError;
                    job.SkippedLockedCount++;
                    job.Results.Add(targetResult);
                    continue;
                }

                // 3. Execution (Quarantine or Delete)
                try
                {
                    long itemSize = candidate.SizeBytes;

                    if (enableQuarantine && isFile && candidate.IsReversible)
                    {
                        var quRec = await _quarantineManager.QuarantineFileAsync(candidate.Path, job.JobId, cancellationToken);
                        if (quRec != null)
                        {
                            targetResult.Success = true;
                            targetResult.WasQuarantined = true;
                            targetResult.QuarantinedPath = quRec.QuarantinedPath;
                            targetResult.BytesRecovered = itemSize;
                            job.SuccessCount++;
                            job.TotalBytesRecovered += itemSize;
                            job.Results.Add(targetResult);
                            continue;
                        }
                    }

                    // Direct deletion if quarantine disabled or directory
                    if (isDir)
                    {
                        long recovered = CleanDirectorySafely(candidate.Path, cancellationToken);
                        targetResult.Success = true;
                        targetResult.BytesRecovered = recovered;
                        job.SuccessCount++;
                        job.TotalBytesRecovered += recovered;
                    }
                    else
                    {
                        File.Delete(candidate.Path);
                        targetResult.Success = true;
                        targetResult.BytesRecovered = itemSize;
                        job.SuccessCount++;
                        job.TotalBytesRecovered += itemSize;
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

        report.CurrentPhase = $"Cleanup Completed. Recovered {DriveModel.FormatBytes(job.TotalBytesRecovered)}.";
        progress?.Report(report);

        return job;
    }

    private static long CleanDirectorySafely(string dirPath, CancellationToken ct)
    {
        long recovered = 0;
        try
        {
            var dir = new DirectoryInfo(dirPath);
            if (!dir.Exists) return 0;

            foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long len = f.Length;
                    f.Delete();
                    recovered += len;
                }
                catch { }
            }

            foreach (var d in dir.EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    d.Delete(false);
                }
                catch { }
            }

            try
            {
                if (!dir.EnumerateFileSystemInfos().Any())
                {
                    dir.Delete(false);
                }
            }
            catch { }
        }
        catch { }
        return recovered;
    }
}
