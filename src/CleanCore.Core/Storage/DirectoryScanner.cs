using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Classification;
using CleanCore.Core.Models;

namespace CleanCore.Core.Storage;

public interface IDirectoryScanner
{
    Task<StorageNode> ScanDirectoryAsync(
        string rootPath,
        bool deepScan,
        int maxDegreeOfParallelism,
        IProgress<ScanProgressReport>? progress,
        CancellationToken cancellationToken);

    Task<List<LargeFileEntry>> FindLargeFilesAsync(
        string rootPath,
        long minSizeBytes,
        int maxDegreeOfParallelism,
        IProgress<ScanProgressReport>? progress,
        CancellationToken cancellationToken);
}

public class DirectoryScanner : IDirectoryScanner
{
    public async Task<StorageNode> ScanDirectoryAsync(
        string rootPath,
        bool deepScan,
        int maxDegreeOfParallelism,
        IProgress<ScanProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var normalizedRoot = NativeStorageMethods.NormalizeLongPath(rootPath);
        var dirInfo = new DirectoryInfo(normalizedRoot);

        var rootNode = new StorageNode
        {
            FullPath = dirInfo.FullName,
            Name = string.IsNullOrEmpty(dirInfo.Name) ? dirInfo.FullName : dirInfo.Name,
            IsDirectory = true,
            LastModified = TryGetLastModified(dirInfo)
        };

        long filesDiscovered = 0;
        long dirsScanned = 0;
        long totalBytes = 0;
        long inaccessibleCount = 0;

        var visitedPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        visitedPaths.TryAdd(rootNode.FullPath, 0);

        var report = new ScanProgressReport
        {
            CurrentDrive = Path.GetPathRoot(rootNode.FullPath) ?? string.Empty,
            CurrentPhase = deepScan ? "Deep Scanning Directories..." : "Fast Scanning Top Directories..."
        };

        var throttledProgressTimer = Stopwatch.StartNew();

        void ReportProgress(string currentDir)
        {
            if (throttledProgressTimer.ElapsedMilliseconds > 120)
            {
                throttledProgressTimer.Restart();
                report.CurrentDirectory = currentDir;
                report.FilesDiscovered = Interlocked.Read(ref filesDiscovered);
                report.DirectoriesScanned = Interlocked.Read(ref dirsScanned);
                report.BytesScanned = Interlocked.Read(ref totalBytes);
                report.InaccessibleCount = Interlocked.Read(ref inaccessibleCount);
                report.Elapsed = stopwatch.Elapsed;
                progress?.Report(report);
            }
        }

        int maxDepth = deepScan ? 32 : 2;

        // 1. Process root-level files
        try
        {
            Interlocked.Increment(ref dirsScanned);
            ReportProgress(rootNode.FullPath);

            var rootFiles = dirInfo.GetFiles();
            long rootFilesSize = 0;
            foreach (var fi in rootFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    long length = fi.Length;
                    rootFilesSize += length;
                    Interlocked.Increment(ref filesDiscovered);
                    Interlocked.Add(ref totalBytes, length);

                    var cls = FileClassifier.Classify(fi.FullName, length, false);
                    rootNode.Children.Add(new StorageNode
                    {
                        FullPath = fi.FullName,
                        Name = fi.Name,
                        IsDirectory = false,
                        SizeBytes = length,
                        LastModified = TryGetLastModified(fi),
                        LastAccessed = TryGetLastAccessed(fi),
                        Extension = fi.Extension,
                        Category = cls.Category,
                        IsReparsePoint = (fi.Attributes & FileAttributes.ReparsePoint) != 0,
                        IsHidden = (fi.Attributes & FileAttributes.Hidden) != 0,
                        IsSystem = (fi.Attributes & FileAttributes.System) != 0
                    });
                }
                catch
                {
                    Interlocked.Increment(ref inaccessibleCount);
                }
            }
            rootNode.FileCount = rootFiles.Length;
            rootNode.SizeBytes = rootFilesSize;
        }
        catch
        {
            rootNode.IsPermissionDenied = true;
            Interlocked.Increment(ref inaccessibleCount);
        }

        // 2. Discover root-level subdirectories
        DirectoryInfo[] topSubDirs = [];
        try
        {
            topSubDirs = dirInfo.GetDirectories();
            rootNode.DirectoryCount = topSubDirs.Length;
        }
        catch
        {
            rootNode.IsPermissionDenied = true;
            Interlocked.Increment(ref inaccessibleCount);
        }

        // 3. Parallelize across top subdirectories with deadlock-free bounded concurrency
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(16, maxDegreeOfParallelism)),
            CancellationToken = cancellationToken
        };

        var topNodes = new ConcurrentBag<StorageNode>();

        await Parallel.ForEachAsync(topSubDirs, parallelOptions, (subDir, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (!visitedPaths.TryAdd(subDir.FullName, 0))
            {
                return ValueTask.CompletedTask;
            }

            bool isReparse = (subDir.Attributes & FileAttributes.ReparsePoint) != 0;
            var childNode = new StorageNode
            {
                FullPath = subDir.FullName,
                Name = subDir.Name,
                IsDirectory = true,
                LastModified = TryGetLastModified(subDir),
                IsReparsePoint = isReparse,
                IsHidden = (subDir.Attributes & FileAttributes.Hidden) != 0,
                IsSystem = (subDir.Attributes & FileAttributes.System) != 0
            };

            topNodes.Add(childNode);

            if (!isReparse)
            {
                ScanSubtree(childNode, deepScan, 1, maxDepth, ct);
            }

            return ValueTask.CompletedTask;
        });

        // 4. Combine top subdirectories into rootNode
        long subDirsSize = 0;
        foreach (var node in topNodes)
        {
            rootNode.Children.Add(node);
            subDirsSize += node.SizeBytes;
            rootNode.FileCount += node.FileCount;
            rootNode.DirectoryCount += node.DirectoryCount;
        }
        rootNode.SizeBytes += subDirsSize;
        rootNode.Category = FileCategory.OperatingSystem;

        stopwatch.Stop();
        report.CurrentPhase = "Scan Complete";
        report.FilesDiscovered = Interlocked.Read(ref filesDiscovered);
        report.DirectoriesScanned = Interlocked.Read(ref dirsScanned);
        report.BytesScanned = Interlocked.Read(ref totalBytes);
        report.InaccessibleCount = Interlocked.Read(ref inaccessibleCount);
        report.Elapsed = stopwatch.Elapsed;
        progress?.Report(report);

        return rootNode;

        void ScanSubtree(StorageNode currentNode, bool isDeep, int depth, int maxD, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > maxD) return;

            DirectoryInfo currentDirInfo;
            try
            {
                currentDirInfo = new DirectoryInfo(currentNode.FullPath);
                if (NativeStorageMethods.IsReparsePoint(currentNode.FullPath))
                {
                    currentNode.IsReparsePoint = true;
                    return;
                }
            }
            catch
            {
                currentNode.IsPermissionDenied = true;
                Interlocked.Increment(ref inaccessibleCount);
                return;
            }

            Interlocked.Increment(ref dirsScanned);
            ReportProgress(currentNode.FullPath);

            FileInfo[] files;
            DirectoryInfo[] subDirs;
            try
            {
                files = currentDirInfo.GetFiles();
                subDirs = currentDirInfo.GetDirectories();
            }
            catch
            {
                currentNode.IsPermissionDenied = true;
                Interlocked.Increment(ref inaccessibleCount);
                return;
            }

            long currentDirFilesSize = 0;
            var fileNodes = new List<StorageNode>();

            foreach (var fi in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long length = fi.Length;
                    currentDirFilesSize += length;
                    Interlocked.Increment(ref filesDiscovered);
                    Interlocked.Add(ref totalBytes, length);

                    var cls = FileClassifier.Classify(fi.FullName, length, false);

                    if (isDeep || depth <= 1)
                    {
                        fileNodes.Add(new StorageNode
                        {
                            FullPath = fi.FullName,
                            Name = fi.Name,
                            IsDirectory = false,
                            SizeBytes = length,
                            LastModified = TryGetLastModified(fi),
                            LastAccessed = TryGetLastAccessed(fi),
                            Extension = fi.Extension,
                            Category = cls.Category,
                            IsReparsePoint = (fi.Attributes & FileAttributes.ReparsePoint) != 0,
                            IsHidden = (fi.Attributes & FileAttributes.Hidden) != 0,
                            IsSystem = (fi.Attributes & FileAttributes.System) != 0
                        });
                    }
                }
                catch
                {
                    Interlocked.Increment(ref inaccessibleCount);
                }
            }

            currentNode.Children.AddRange(fileNodes);
            currentNode.FileCount = files.Length;
            currentNode.DirectoryCount = subDirs.Length;

            long subTotalSize = 0;
            foreach (var sd in subDirs)
            {
                ct.ThrowIfCancellationRequested();
                if (!visitedPaths.TryAdd(sd.FullName, 0)) continue;

                bool isRep = (sd.Attributes & FileAttributes.ReparsePoint) != 0;
                var subNode = new StorageNode
                {
                    FullPath = sd.FullName,
                    Name = sd.Name,
                    IsDirectory = true,
                    LastModified = TryGetLastModified(sd),
                    IsReparsePoint = isRep,
                    IsHidden = (sd.Attributes & FileAttributes.Hidden) != 0,
                    IsSystem = (sd.Attributes & FileAttributes.System) != 0
                };

                currentNode.Children.Add(subNode);

                if (!isRep && depth < maxD)
                {
                    ScanSubtree(subNode, isDeep, depth + 1, maxD, ct);
                }

                subTotalSize += subNode.SizeBytes;
                currentNode.FileCount += subNode.FileCount;
                currentNode.DirectoryCount += subNode.DirectoryCount;
            }

            currentNode.SizeBytes = currentDirFilesSize + subTotalSize;
            var dirClassification = FileClassifier.Classify(currentNode.FullPath, currentNode.SizeBytes, true);
            currentNode.Category = dirClassification.Category;
        }
    }

    public async Task<List<LargeFileEntry>> FindLargeFilesAsync(
        string rootPath,
        long minSizeBytes,
        int maxDegreeOfParallelism,
        IProgress<ScanProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new ConcurrentBag<LargeFileEntry>();
        long filesDiscovered = 0;
        long dirsScanned = 0;
        long totalBytes = 0;
        long inaccessibleCount = 0;

        var report = new ScanProgressReport
        {
            CurrentDrive = Path.GetPathRoot(rootPath) ?? string.Empty,
            CurrentPhase = $"Finding Files >= {DriveModel.FormatBytes(minSizeBytes)}..."
        };

        var throttledTimer = Stopwatch.StartNew();
        var visited = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        void ReportProgress(string currentPath)
        {
            if (throttledTimer.ElapsedMilliseconds > 120)
            {
                throttledTimer.Restart();
                report.CurrentDirectory = currentPath;
                report.FilesDiscovered = Interlocked.Read(ref filesDiscovered);
                report.DirectoriesScanned = Interlocked.Read(ref dirsScanned);
                report.BytesScanned = Interlocked.Read(ref totalBytes);
                report.InaccessibleCount = Interlocked.Read(ref inaccessibleCount);
                report.Elapsed = stopwatch.Elapsed;
                progress?.Report(report);
            }
        }

        var dirInfo = new DirectoryInfo(rootPath);
        if (visited.TryAdd(dirInfo.FullName, 0))
        {
            // Process root files
            try
            {
                Interlocked.Increment(ref dirsScanned);
                ReportProgress(dirInfo.FullName);

                foreach (var fi in dirInfo.GetFiles())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        long len = fi.Length;
                        Interlocked.Increment(ref filesDiscovered);
                        Interlocked.Add(ref totalBytes, len);
                        if (len >= minSizeBytes)
                        {
                            var cls = FileClassifier.Classify(fi.FullName, len, false);
                            results.Add(new LargeFileEntry
                            {
                                FullPath = fi.FullName,
                                FileName = fi.Name,
                                DirectoryPath = fi.DirectoryName ?? string.Empty,
                                SizeBytes = len,
                                Extension = fi.Extension,
                                Category = cls.Category,
                                LastModified = TryGetLastModified(fi),
                                LastAccessed = TryGetLastAccessed(fi),
                                IsTemporaryCandidate = cls.IsSafeToDeleteCandidate,
                                Confidence = cls.Confidence
                            });
                        }
                    }
                    catch { Interlocked.Increment(ref inaccessibleCount); }
                }
            }
            catch { Interlocked.Increment(ref inaccessibleCount); }

            // Parallelize top subdirectories
            DirectoryInfo[] topSubDirs = [];
            try { topSubDirs = dirInfo.GetDirectories(); } catch { }

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Math.Min(16, maxDegreeOfParallelism)),
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(topSubDirs, parallelOptions, (sd, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if ((sd.Attributes & FileAttributes.ReparsePoint) == 0 && visited.TryAdd(sd.FullName, 0))
                {
                    TraverseSubtree(sd.FullName, 1, 16, ct);
                }
                return ValueTask.CompletedTask;
            });
        }

        stopwatch.Stop();
        report.CurrentPhase = "Large File Search Complete";
        report.FilesDiscovered = Interlocked.Read(ref filesDiscovered);
        report.DirectoriesScanned = Interlocked.Read(ref dirsScanned);
        report.BytesScanned = Interlocked.Read(ref totalBytes);
        report.InaccessibleCount = Interlocked.Read(ref inaccessibleCount);
        report.Elapsed = stopwatch.Elapsed;
        progress?.Report(report);

        return results.OrderByDescending(f => f.SizeBytes).ToList();

        void TraverseSubtree(string currentPath, int depth, int maxDepth, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > maxDepth || !visited.TryAdd(currentPath, 0)) return;

            DirectoryInfo curDir;
            try
            {
                curDir = new DirectoryInfo(currentPath);
                if ((curDir.Attributes & FileAttributes.ReparsePoint) != 0) return;
            }
            catch
            {
                Interlocked.Increment(ref inaccessibleCount);
                return;
            }

            Interlocked.Increment(ref dirsScanned);
            ReportProgress(currentPath);

            FileInfo[] files;
            DirectoryInfo[] subDirs;
            try
            {
                files = curDir.GetFiles();
                subDirs = curDir.GetDirectories();
            }
            catch
            {
                Interlocked.Increment(ref inaccessibleCount);
                return;
            }

            foreach (var fi in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long length = fi.Length;
                    Interlocked.Increment(ref filesDiscovered);
                    Interlocked.Add(ref totalBytes, length);

                    if (length >= minSizeBytes)
                    {
                        var cls = FileClassifier.Classify(fi.FullName, length, false);
                        results.Add(new LargeFileEntry
                        {
                            FullPath = fi.FullName,
                            FileName = fi.Name,
                            DirectoryPath = fi.DirectoryName ?? string.Empty,
                            SizeBytes = length,
                            Extension = fi.Extension,
                            Category = cls.Category,
                            LastModified = TryGetLastModified(fi),
                            LastAccessed = TryGetLastAccessed(fi),
                            IsTemporaryCandidate = cls.IsSafeToDeleteCandidate,
                            Confidence = cls.Confidence
                        });
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
                TraverseSubtree(sd.FullName, depth + 1, maxDepth, ct);
            }
        }
    }

    private static DateTime? TryGetLastModified(FileSystemInfo fsi)
    {
        try { return fsi.LastWriteTime; } catch { return null; }
    }

    private static DateTime? TryGetLastAccessed(FileSystemInfo fsi)
    {
        try { return fsi.LastAccessTime; } catch { return null; }
    }
}
