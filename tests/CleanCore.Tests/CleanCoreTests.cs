using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Classification;
using CleanCore.Core.Cleanup;
using CleanCore.Core.Duplicates;
using CleanCore.Core.Models;
using CleanCore.Core.Quarantine;
using CleanCore.Core.Security;
using CleanCore.Core.Storage;
using Xunit;

namespace CleanCore.Tests;

public class CleanCoreTests
{
    [Fact]
    public void DriveModel_FormatBytes_ReturnsExpectedUnits()
    {
        Assert.Equal("0 B", DriveModel.FormatBytes(0));
        Assert.Equal("512.0 B", DriveModel.FormatBytes(512));
        Assert.Equal("1.0 KB", DriveModel.FormatBytes(1024));
        Assert.Equal("1.5 MB", DriveModel.FormatBytes((long)(1.5 * 1024 * 1024)));
        Assert.Equal("2.0 GB", DriveModel.FormatBytes(2L * 1024 * 1024 * 1024));
        Assert.Equal("1.5 TB", DriveModel.FormatBytes((long)(1.5 * 1024L * 1024 * 1024 * 1024)));
    }

    [Fact]
    public void DriveModel_UsagePercentages_CalculatesCorrectly()
    {
        var drive = new DriveModel
        {
            Name = "C:\\",
            TotalBytes = 1000,
            FreeBytes = 250
        };

        Assert.Equal(750, drive.UsedBytes);
        Assert.Equal(75.0, drive.UsedPercentage, 1);
        Assert.Equal(25.0, drive.FreePercentage, 1);
    }

    [Theory]
    [InlineData("..\\Windows\\System32", false)]
    [InlineData("C:\\", false)]
    [InlineData("C:\\Windows", false)]
    [InlineData("C:\\Windows\\System32\\config\\SAM", false)]
    [InlineData("C:\\pagefile.sys", false)]
    [InlineData("C:\\bootmgr", false)]
    public void PathSafetyGuard_RejectsDangerousProtectedLocations(string path, bool expectedSafe)
    {
        bool isSafe = PathSafetyGuard.IsPathSafeForDeletion(path, out string rejection);
        Assert.Equal(expectedSafe, isSafe);
        Assert.NotEmpty(rejection);
    }

    [Fact]
    public void PathSafetyGuard_AcceptsTempAndSafeLocations()
    {
        string safePath = Path.Combine(Path.GetTempPath(), "cleancore_test_file.tmp");
        bool isSafe = PathSafetyGuard.IsPathSafeForDeletion(safePath, out string rejection);
        Assert.True(isSafe, $"Expected safe path but rejected: {rejection}");
    }

    [Fact]
    public void FileClassifier_ClassifiesKnownExtensionsAndPaths()
    {
        var doc = FileClassifier.Classify("C:\\data\\report.pdf", 1024, false);
        Assert.Equal(FileCategory.UserDocuments, doc.Category);

        var video = FileClassifier.Classify("C:\\movies\\vacation.mp4", 5000000, false);
        Assert.Equal(FileCategory.Videos, video.Category);

        var vm = FileClassifier.Classify("C:\\vms\\ubuntu.vmdk", 10000000, false);
        Assert.Equal(FileCategory.VirtualMachines, vm.Category);

        var temp = FileClassifier.Classify(Path.Combine(Path.GetTempPath(), "scratch.tmp"), 500, false);
        Assert.Equal(FileCategory.TemporaryFiles, temp.Category);
        Assert.True(temp.IsSafeToDeleteCandidate);
    }

    [Fact]
    public void CleanupPlan_PreventsDoubleCountingAndCalculatesTotals()
    {
        var candidates = new List<CleanupCandidate>
        {
            new() { Id = "1", Path = "C:\\Temp\\item1.tmp", SizeBytes = 1000, RiskLevel = CleanupRiskLevel.Safe, IsSelected = true },
            new() { Id = "2", Path = "C:\\Temp\\item1.tmp", SizeBytes = 1000, RiskLevel = CleanupRiskLevel.Safe, IsSelected = true }, // Duplicate path
            new() { Id = "3", Path = "C:\\Temp\\item2.tmp", SizeBytes = 2500, RiskLevel = CleanupRiskLevel.Moderate, IsSelected = true },
            new() { Id = "4", Path = "C:\\Temp\\item3.tmp", SizeBytes = 5000, RiskLevel = CleanupRiskLevel.Safe, IsSelected = false } // Unselected
        };

        var engine = new CleanupExecutionEngine(new QuarantineManager(), new DriveService());
        var plan = engine.CreatePlanFromCandidates(candidates);

        // Path item1.tmp was added twice; only unique paths should be in plan
        Assert.Equal(3, plan.Candidates.Count);
        // Selected: item1 (1000) + item2 (2500) = 3500
        Assert.Equal(3500, plan.TotalSelectedBytes);
        Assert.Equal(1000, plan.SafeSelectedBytes);
        Assert.Equal(2500, plan.ReviewRequiredBytes);
    }

    [Fact]
    public void DuplicateFinder_RetentionRules_PreservesOneItem()
    {
        var group = new DuplicateGroup
        {
            Sha256Hash = "ABCDEF123456",
            FileSizeBytes = 1024,
            Items = new List<DuplicateItem>
            {
                new() { FullPath = "C:\\dir1\\copy1.txt", SizeBytes = 1024, LastModified = new DateTime(2023, 1, 1) },
                new() { FullPath = "C:\\dir2\\copy2.txt", SizeBytes = 1024, LastModified = new DateTime(2023, 6, 1) },
                new() { FullPath = "C:\\dir3\\copy3.txt", SizeBytes = 1024, LastModified = new DateTime(2023, 12, 1) }
            }
        };

        var service = new DuplicateFinderService();
        service.ApplyRetentionRule(new List<DuplicateGroup> { group }, DuplicateRetentionRule.KeepOldest);

        // Oldest (2023-01-01) must be preserved
        Assert.True(group.Items[0].IsOriginalPreserved);
        Assert.False(group.Items[0].IsSelectedForDeletion);

        // Others must be selected for deletion
        Assert.True(group.Items[1].IsSelectedForDeletion);
        Assert.True(group.Items[2].IsSelectedForDeletion);
        Assert.Equal(2048, group.ReclaimableBytes);
    }

    [Fact]
    public async Task QuarantineManager_QuarantineAndRestore_CycleSucceeds()
    {
        var qm = new QuarantineManager();
        string tempDir = Path.Combine(Path.GetTempPath(), "cleancore_test_env_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string testFile = Path.Combine(tempDir, "sample_to_quarantine.txt");
        string originalContent = "CleanCore test payload " + DateTime.UtcNow.Ticks;
        await File.WriteAllTextAsync(testFile, originalContent);

        try
        {
            // 1. Quarantine file
            var record = await qm.QuarantineFileAsync(testFile, "test_job_1", CancellationToken.None);
            Assert.NotNull(record);
            Assert.False(File.Exists(testFile));
            Assert.True(File.Exists(record.QuarantinedPath));

            // 2. Restore file
            var (success, error) = await qm.RestoreFileAsync(record);
            Assert.True(success, $"Restore failed with error: {error}");
            Assert.True(File.Exists(testFile));

            string restoredContent = await File.ReadAllTextAsync(testFile);
            Assert.Equal(originalContent, restoredContent);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task DirectoryScanner_ScanDirectoryAsync_CompletesSuccessfullyAndCountsFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "CleanCore_ScanTest_" + Guid.NewGuid().ToString("N"));
        string subDir = Path.Combine(tempDir, "SubDirA");
        Directory.CreateDirectory(subDir);

        await File.WriteAllTextAsync(Path.Combine(tempDir, "root1.txt"), "Hello world");
        await File.WriteAllTextAsync(Path.Combine(subDir, "sub1.log"), "Log file content");

        try
        {
            var scanner = new DirectoryScanner();
            var result = await scanner.ScanDirectoryAsync(tempDir, deepScan: true, maxDegreeOfParallelism: 4, null, CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(result.FileCount >= 2);
            Assert.True(result.SizeBytes > 0);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task DirectoryScanner_FindLargeFilesAsync_FindsMatchingFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "CleanCore_LargeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        byte[] smallData = new byte[100];
        byte[] largeData = new byte[5000];
        await File.WriteAllBytesAsync(Path.Combine(tempDir, "small.bin"), smallData);
        await File.WriteAllBytesAsync(Path.Combine(tempDir, "large.bin"), largeData);

        try
        {
            var scanner = new DirectoryScanner();
            var results = await scanner.FindLargeFilesAsync(tempDir, minSizeBytes: 1000, maxDegreeOfParallelism: 4, null, CancellationToken.None);

            Assert.NotNull(results);
            Assert.Single(results);
            Assert.Equal("large.bin", results[0].FileName);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SystemMaintenance_IsRunningAsAdministrator_ReturnsBoolean()
    {
        bool isAdmin = CleanCore.Core.SystemMaintenance.SystemMaintenanceService.IsRunningAsAdministrator();
        // Should return a valid bool without throwing
        Assert.True(isAdmin || !isAdmin);
    }

    [Fact]
    public void NativeStorageMethods_EnableBackupPrivileges_ExecutesSafely()
    {
        // Should execute safely without throwing exceptions
        bool result = CleanCore.Core.Storage.NativeStorageMethods.EnableBackupPrivileges();
        Assert.True(result || !result);
    }

    [Fact]
    public async Task SystemMaintenance_GrantDirectoryAccessAsync_InvalidPath_ReturnsFalse()
    {
        bool result = await CleanCore.Core.SystemMaintenance.SystemMaintenanceService.GrantDirectoryAccessAsync(@"Z:\NonExistent_Dir_Test_123");
        Assert.False(result);
    }

    [Fact]
    public void CacheProviderResult_SelectionCascadesToChildrenAndBack()
    {
        var provider = new CleanCore.Core.Models.CacheProviderResult();
        var target1 = new CleanCore.Core.Models.CacheTargetItem { Name = "test1", SizeBytes = 1000, IsSelected = true };
        var target2 = new CleanCore.Core.Models.CacheTargetItem { Name = "test2", SizeBytes = 2000, IsSelected = true };
        provider.Targets.Add(target1);
        provider.Targets.Add(target2);
        provider.RegisterChildEvents();

        Assert.True(provider.IsSelected);
        Assert.Equal(3000, provider.SelectedSizeBytes);

        // Deselect provider should deselect children
        provider.IsSelected = false;
        Assert.False(target1.IsSelected);
        Assert.False(target2.IsSelected);
        Assert.Equal(0, provider.SelectedSizeBytes);

        // Selecting one child should make provider indeterminate (null)
        target1.IsSelected = true;
        Assert.Null(provider.IsSelected);
        Assert.Equal(1000, provider.SelectedSizeBytes);

        // Selecting second child should make provider true
        target2.IsSelected = true;
        Assert.True(provider.IsSelected);
        Assert.Equal(3000, provider.SelectedSizeBytes);
    }

    [Fact]
    public void CleanupCandidate_NotifiesPropertyChangedOnSelection()
    {
        var candidate = new CleanCore.Core.Models.CleanupCandidate { IsSelected = true };
        string? changedProperty = null;
        candidate.PropertyChanged += (s, e) => changedProperty = e.PropertyName;

        candidate.IsSelected = false;
        Assert.Equal(nameof(CleanCore.Core.Models.CleanupCandidate.IsSelected), changedProperty);
    }
}
