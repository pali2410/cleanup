using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore.Core.Security;

namespace CleanCore.Core.Duplicates;

public interface IDuplicateFinderService
{
    Task<List<DuplicateGroup>> FindDuplicatesAsync(
        string rootPath,
        long minSizeBytes,
        int maxDegreeOfParallelism,
        IProgress<ScanProgressReport>? progress,
        CancellationToken cancellationToken);

    void ApplyRetentionRule(List<DuplicateGroup> groups, DuplicateRetentionRule rule);
}

public enum DuplicateRetentionRule
{
    KeepOldest,
    KeepNewest,
    KeepShortestPath,
    KeepLongestPath,
    DeselectAll
}

public class DuplicateFinderService : IDuplicateFinderService
{
    public async Task<List<DuplicateGroup>> FindDuplicatesAsync(
        string rootPath,
        long minSizeBytes,
        int maxDegreeOfParallelism,
        IProgress<ScanProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var report = new ScanProgressReport
        {
            CurrentDrive = Path.GetPathRoot(rootPath) ?? string.Empty,
            CurrentPhase = "Stage 1: Discovering files and grouping by size..."
        };

        // STAGE 1: Collect all eligible files and group by exact size
        var sizeMap = new ConcurrentDictionary<long, ConcurrentBag<string>>();
        long totalFilesChecked = 0;
        long totalDirsChecked = 0;
        long inaccessibleCount = 0;

        await TraverseDirectoryAsync(rootPath, cancellationToken);

        // Filter for sizes that have at least 2 files
        var potentialCandidates = sizeMap.Where(kvp => kvp.Value.Count > 1).ToList();
        int candidateGroupCount = potentialCandidates.Count;

        report.CurrentPhase = $"Stage 2 & 3: Computing cryptographic hashes for {candidateGroupCount} candidate size groups...";
        progress?.Report(report);

        // STAGE 2 & 3: For each candidate size group, compute partial hash then full SHA-256
        var finalDuplicateGroups = new ConcurrentBag<DuplicateGroup>();
        using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(16, maxDegreeOfParallelism)));

        var hashTasks = potentialCandidates.Select(async group =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileList = group.Value.ToList();
                long fileSize = group.Key;

                // Stage 2: Quick partial hash (first 4KB + last 4KB)
                var partialGroups = new Dictionary<string, List<string>>();
                foreach (var file in fileList)
                {
                    string? partialHash = await TryComputePartialHashAsync(file, cancellationToken);
                    if (partialHash == null) continue;

                    if (!partialGroups.TryGetValue(partialHash, out var list))
                    {
                        list = new List<string>();
                        partialGroups[partialHash] = list;
                    }
                    list.Add(file);
                }

                // Stage 3: Full SHA-256 for groups that matched partial hash
                foreach (var pGroup in partialGroups.Values.Where(l => l.Count > 1))
                {
                    var fullHashGroups = new Dictionary<string, List<string>>();
                    foreach (var file in pGroup)
                    {
                        string? fullHash = await TryComputeFullSha256Async(file, fileSize, cancellationToken);
                        if (fullHash == null) continue;

                        if (!fullHashGroups.TryGetValue(fullHash, out var list))
                        {
                            list = new List<string>();
                            fullHashGroups[fullHash] = list;
                        }
                        list.Add(file);
                    }

                    // For each confirmed duplicate group
                    foreach (var confirmed in fullHashGroups)
                    {
                        if (confirmed.Value.Count > 1)
                        {
                            var dupGroup = new DuplicateGroup
                            {
                                Sha256Hash = confirmed.Key,
                                FileSizeBytes = fileSize
                            };

                            foreach (var filePath in confirmed.Value)
                            {
                                var fi = new FileInfo(filePath);
                                dupGroup.Items.Add(new DuplicateItem
                                {
                                    FullPath = fi.FullName,
                                    FileName = fi.Name,
                                    DirectoryPath = fi.DirectoryName ?? string.Empty,
                                    SizeBytes = fi.Length,
                                    LastModified = fi.LastWriteTime,
                                    IsSelectedForDeletion = false,
                                    IsOriginalPreserved = false
                                });
                            }

                            finalDuplicateGroups.Add(dupGroup);
                        }
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(hashTasks);

        var result = finalDuplicateGroups.OrderByDescending(g => g.FileSizeBytes * g.TotalCount).ToList();

        // Default to keeping the oldest file intact
        ApplyRetentionRule(result, DuplicateRetentionRule.KeepOldest);

        report.CurrentPhase = $"Duplicate Search Complete: {result.Count} duplicate groups identified.";
        progress?.Report(report);

        return result;

        async Task TraverseDirectoryAsync(string dirPath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (!PathSafetyGuard.IsPathSafeForDeletion(dirPath, out _))
            {
                // Still allow reading unless it's critical system internals
            }

            DirectoryInfo dir;
            try
            {
                dir = new DirectoryInfo(dirPath);
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return;
            }
            catch
            {
                Interlocked.Increment(ref inaccessibleCount);
                return;
            }

            Interlocked.Increment(ref totalDirsChecked);

            FileInfo[] files;
            DirectoryInfo[] subDirs;

            try
            {
                files = dir.GetFiles();
                subDirs = dir.GetDirectories();
            }
            catch
            {
                Interlocked.Increment(ref inaccessibleCount);
                return;
            }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long length = f.Length;
                    Interlocked.Increment(ref totalFilesChecked);

                    if (length >= minSizeBytes && length > 0)
                    {
                        var bag = sizeMap.GetOrAdd(length, _ => new ConcurrentBag<string>());
                        bag.Add(f.FullName);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref inaccessibleCount);
                }
            }

            foreach (var sd in subDirs)
            {
                ct.ThrowIfCancellationRequested();
                if ((sd.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                await TraverseDirectoryAsync(sd.FullName, ct);
            }
        }
    }

    public void ApplyRetentionRule(List<DuplicateGroup> groups, DuplicateRetentionRule rule)
    {
        foreach (var group in groups)
        {
            if (group.Items.Count <= 1) continue;

            // Reset flags
            foreach (var item in group.Items)
            {
                item.IsSelectedForDeletion = false;
                item.IsOriginalPreserved = false;
            }

            if (rule == DuplicateRetentionRule.DeselectAll)
            {
                continue;
            }

            DuplicateItem preserved = rule switch
            {
                DuplicateRetentionRule.KeepOldest => group.Items.OrderBy(i => i.LastModified ?? DateTime.MaxValue).First(),
                DuplicateRetentionRule.KeepNewest => group.Items.OrderByDescending(i => i.LastModified ?? DateTime.MinValue).First(),
                DuplicateRetentionRule.KeepShortestPath => group.Items.OrderBy(i => i.FullPath.Length).First(),
                DuplicateRetentionRule.KeepLongestPath => group.Items.OrderByDescending(i => i.FullPath.Length).First(),
                _ => group.Items.First()
            };

            preserved.IsOriginalPreserved = true;
            preserved.IsSelectedForDeletion = false;

            // Mark all other duplicates for deletion
            foreach (var item in group.Items)
            {
                if (!ReferenceEquals(item, preserved))
                {
                    item.IsSelectedForDeletion = true;
                }
            }
        }
    }

    private static async Task<string?> TryComputePartialHashAsync(string filePath, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            long length = stream.Length;
            if (length == 0) return "empty";

            int sampleSize = (int)Math.Min(4096, length);
            byte[] buffer = new byte[sampleSize * 2];

            // Read first 4KB
            int readHead = await stream.ReadAsync(buffer.AsMemory(0, sampleSize), ct);

            // Read last 4KB
            if (length > sampleSize)
            {
                stream.Seek(-sampleSize, SeekOrigin.End);
                int readTail = await stream.ReadAsync(buffer.AsMemory(sampleSize, sampleSize), ct);
            }

            using var sha1 = SHA256.Create();
            byte[] hash = sha1.ComputeHash(buffer);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return null; // Inaccessible or locked
        }
    }

    private static async Task<string?> TryComputeFullSha256Async(string filePath, long expectedSize, CancellationToken ct)
    {
        try
        {
            using var sha256 = SHA256.Create();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 512, FileOptions.Asynchronous);

            if (stream.Length != expectedSize)
            {
                return null; // File changed during hashing
            }

            byte[] hash = await sha256.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return null; // Inaccessible or locked
        }
    }
}
