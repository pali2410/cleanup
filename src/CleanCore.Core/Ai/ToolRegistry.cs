using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Applications;
using CleanCore.Core.Cache;
using CleanCore.Core.Cleanup;
using CleanCore.Core.Duplicates;
using CleanCore.Core.LargeFiles;
using CleanCore.Core.Models;
using CleanCore.Core.Reporting;
using CleanCore.Core.Security;
using CleanCore.Core.Storage;

namespace CleanCore.Core.Ai;

public record ToolExecutionOutcome(
    bool Success,
    string ToolName,
    string OutputJson,
    bool RequiresApproval,
    string? ApprovalToken = null,
    string? ErrorMessage = null);

public class ToolRegistry
{
    private readonly IDriveService _driveService;
    private readonly IDirectoryScanner _directoryScanner;
    private readonly ILargeFileManager _largeFileManager;
    private readonly IDuplicateFinderService _duplicateFinder;
    private readonly ICacheEngine _cacheEngine;
    private readonly IAppManagerService _appManager;
    private readonly ICleanupExecutionEngine _cleanupEngine;
    private readonly Dictionary<string, CleanupPlan> _stagedPlans = new();

    public ToolRegistry(
        IDriveService driveService,
        IDirectoryScanner directoryScanner,
        ILargeFileManager largeFileManager,
        IDuplicateFinderService duplicateFinder,
        ICacheEngine cacheEngine,
        IAppManagerService appManager,
        ICleanupExecutionEngine cleanupEngine)
    {
        _driveService = driveService;
        _directoryScanner = directoryScanner;
        _largeFileManager = largeFileManager;
        _duplicateFinder = duplicateFinder;
        _cacheEngine = cacheEngine;
        _appManager = appManager;
        _cleanupEngine = cleanupEngine;
    }

    public List<string> GetAvailableToolNames() => new()
    {
        "GetDriveInventory",
        "GetDriveUsage",
        "ScanDirectory",
        "GetDirectorySize",
        "SearchLargeFiles",
        "SearchByExtension",
        "FindDuplicateCandidates",
        "AnalyzeCacheProviders",
        "GetInstalledApplications",
        "GenerateCleanupPlan",
        "ValidateCleanupPlan",
        "PreviewCleanup",
        "ExecuteApprovedCleanup",
        "MoveApprovedFiles",
        "GetCleanupJobStatus",
        "VerifyRecoveredSpace",
        "ExportReport"
    };

    public async Task<ToolExecutionOutcome> ExecuteToolAsync(
        string toolName,
        Dictionary<string, object?> arguments,
        string? approvalToken,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (toolName)
            {
                case "GetDriveInventory":
                {
                    var drives = _driveService.GetDrives();
                    var summary = drives.Select(d => new
                    {
                        d.Name,
                        d.VolumeLabel,
                        d.DriveType,
                        d.FileSystem,
                        Total = d.TotalDisplay,
                        Free = d.FreeDisplay,
                        Used = d.UsedDisplay,
                        FreePercent = $"{d.FreePercentage:F1}%",
                        Status = d.HealthStatus
                    });
                    return new ToolExecutionOutcome(true, toolName, JsonSerializer.Serialize(summary), false);
                }

                case "GetDriveUsage":
                {
                    string drive = GetStringArg(arguments, "drivePath", "C:\\");
                    var driveInfo = _driveService.GetDrive(drive);
                    if (driveInfo == null)
                    {
                        return new ToolExecutionOutcome(false, toolName, "", false, null, $"Drive '{drive}' was not found.");
                    }
                    return new ToolExecutionOutcome(true, toolName, JsonSerializer.Serialize(new
                    {
                        driveInfo.Name,
                        driveInfo.VolumeLabel,
                        TotalBytes = driveInfo.TotalBytes,
                        FreeBytes = driveInfo.FreeBytes,
                        UsedBytes = driveInfo.UsedBytes,
                        driveInfo.FreePercentageDisplay,
                        driveInfo.UsedPercentageDisplay
                    }), false);
                }

                case "ScanDirectory":
                {
                    string path = GetStringArg(arguments, "path", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    bool deep = GetBoolArg(arguments, "deep", false);
                    var node = await _directoryScanner.ScanDirectoryAsync(path, deep, 4, null, cancellationToken);
                    var topDirs = node.Children
                        .OrderByDescending(c => c.SizeBytes)
                        .Take(15)
                        .Select(c => new
                        {
                            c.Name,
                            c.SizeBytes,
                            Size = c.SizeDisplay,
                            c.IsDirectory,
                            c.CategoryDisplay
                        });
                    return new ToolExecutionOutcome(true, toolName, JsonSerializer.Serialize(new
                    {
                        RootPath = node.FullPath,
                        TotalSize = node.SizeDisplay,
                        TopItems = topDirs
                    }), false);
                }

                case "SearchLargeFiles":
                {
                    string path = GetStringArg(arguments, "path", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    long minBytes = GetLongArg(arguments, "minSizeBytes", 500 * 1024 * 1024); // 500MB default
                    var files = await _directoryScanner.FindLargeFilesAsync(path, minBytes, 4, null, cancellationToken);
                    var summary = files.Take(25).Select(f => new
                    {
                        f.FileName,
                        f.FullPath,
                        f.SizeBytes,
                        Size = f.SizeDisplay,
                        Category = f.CategoryDisplay,
                        Modified = f.ModifiedDisplay
                    });
                    return new ToolExecutionOutcome(true, toolName, JsonSerializer.Serialize(summary), false);
                }

                case "FindDuplicateCandidates":
                {
                    string path = GetStringArg(arguments, "path", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
                    long minBytes = GetLongArg(arguments, "minSizeBytes", 10 * 1024 * 1024); // 10MB default
                    var dups = await _duplicateFinder.FindDuplicatesAsync(path, minBytes, 4, null, cancellationToken);
                    var summary = dups.Take(15).Select(d => new
                    {
                        d.GroupId,
                        SizePerCopy = d.SingleSizeDisplay,
                        Copies = d.TotalCount,
                        TotalReclaimable = d.ReclaimableDisplay,
                        Files = d.Items.Select(i => i.FullPath)
                    });
                    return new ToolExecutionOutcome(true, toolName, JsonSerializer.Serialize(summary), false);
                }

                case "AnalyzeCacheProviders":
                {
                    var results = await _cacheEngine.AnalyzeAllAsync(null, cancellationToken);
                    var summary = results.Select(r => new
                    {
                        r.ProviderId,
                        r.DisplayName,
                        r.Category,
                        TotalSize = r.SizeDisplay,
                        Items = r.TotalItemCount,
                        r.RiskBadge,
                        r.RequiresElevation,
                        r.RunningProcessWarning
                    });
                    return new ToolExecutionOutcome(true, toolName, JsonSerializer.Serialize(summary), false);
                }

                case "GetInstalledApplications":
                {
                    var apps = await _appManager.GetInstalledApplicationsAsync(cancellationToken);
                    var topApps = apps.Take(20).Select(a => new
                    {
                        a.DisplayName,
                        a.PublisherDisplay,
                        a.VersionDisplay,
                        Size = a.FootprintDisplay,
                        a.InstallLocation
                    });
                    return new ToolExecutionOutcome(true, toolName, JsonSerializer.Serialize(topApps), false);
                }

                case "GenerateCleanupPlan":
                {
                    // Scan cache providers to generate a proposal
                    var cacheResults = await _cacheEngine.AnalyzeAllAsync(null, cancellationToken);
                    var candidates = new List<CleanupCandidate>();

                    foreach (var provider in cacheResults)
                    {
                        foreach (var target in provider.Targets.Where(t => t.IsSelected && !t.IsLocked))
                        {
                            candidates.Add(new CleanupCandidate
                            {
                                Name = target.Name,
                                Path = target.Path,
                                SizeBytes = target.SizeBytes,
                                Category = provider.Category,
                                RiskLevel = provider.RiskLevel,
                                Reason = target.SideEffectDescription,
                                RequiresElevation = provider.RequiresElevation,
                                ExpectedSideEffects = target.SideEffectDescription,
                                IsReversible = true
                            });
                        }
                    }

                    var plan = _cleanupEngine.CreatePlanFromCandidates(candidates);
                    _stagedPlans[plan.PlanId] = plan;

                    var planSummary = new
                    {
                        plan.PlanId,
                        TotalSelected = plan.TotalSelectedDisplay,
                        SafeSelected = plan.SafeSelectedDisplay,
                        ReviewRequired = plan.ReviewRequiredDisplay,
                        CandidateCount = plan.Candidates.Count,
                        RequiresUserApproval = true,
                        ApprovalMessage = $"Review plan {plan.PlanId}. Approving will delete or quarantine {plan.CandidateCount} items ({plan.TotalSelectedDisplay})."
                    };

                    return new ToolExecutionOutcome(true, toolName, JsonSerializer.Serialize(planSummary), false);
                }

                case "ExecuteApprovedCleanup":
                {
                    // DESTRUCTIVE TOOL: Requires strict approval token matching the staged plan
                    string planId = GetStringArg(arguments, "planId", string.Empty);
                    if (string.IsNullOrWhiteSpace(planId) || !_stagedPlans.TryGetValue(planId, out var stagedPlan))
                    {
                        return new ToolExecutionOutcome(false, toolName, "", false, null, "Invalid or expired PlanId. A proposal must first be generated and approved.");
                    }

                    if (string.IsNullOrWhiteSpace(approvalToken) || !string.Equals(approvalToken, planId, StringComparison.OrdinalIgnoreCase))
                    {
                        // Return approval challenge
                        return new ToolExecutionOutcome(false, toolName, "", true, planId,
                            $"Destructive operation requires explicit confirmation. Please confirm approval for Plan ID '{planId}' to recover {stagedPlan.TotalSelectedDisplay}.");
                    }

                    bool quarantine = GetBoolArg(arguments, "enableQuarantine", true);
                    var jobResult = await _cleanupEngine.ExecutePlanAsync(stagedPlan, quarantine, null, cancellationToken);
                    _stagedPlans.Remove(planId);

                    return new ToolExecutionOutcome(true, toolName, JsonSerializer.Serialize(new
                    {
                        jobResult.JobId,
                        jobResult.Status,
                        TotalRecovered = jobResult.BytesRecoveredDisplay,
                        SuccessItems = jobResult.SuccessCount,
                        FailedItems = jobResult.FailureCount,
                        SkippedLocked = jobResult.SkippedLockedCount
                    }), false);
                }

                case "MoveApprovedFiles":
                {
                    string src = GetStringArg(arguments, "sourcePath", string.Empty);
                    string dst = GetStringArg(arguments, "destinationDir", string.Empty);

                    if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
                    {
                        return new ToolExecutionOutcome(false, toolName, "", false, null, "Both sourcePath and destinationDir must be specified.");
                    }

                    string opToken = $"move_{Path.GetFileName(src)}";
                    if (string.IsNullOrWhiteSpace(approvalToken) || approvalToken != opToken)
                    {
                        return new ToolExecutionOutcome(false, toolName, "", true, opToken,
                            $"Moving '{src}' to '{dst}' requires user confirmation.");
                    }

                    var (success, msg) = await _largeFileManager.SafeMoveFileAsync(src, dst, null, cancellationToken);
                    return new ToolExecutionOutcome(success, toolName, JsonSerializer.Serialize(new { success, message = msg }), false);
                }

                case "VerifyRecoveredSpace":
                {
                    string drive = GetStringArg(arguments, "drive", "C:\\");
                    var d = _driveService.GetDrive(drive);
                    return new ToolExecutionOutcome(true, toolName, JsonSerializer.Serialize(new
                    {
                        Drive = drive,
                        CurrentFreeSpace = d?.FreeDisplay,
                        FreePercentage = d?.FreePercentageDisplay
                    }), false);
                }

                default:
                    return new ToolExecutionOutcome(false, toolName, "", false, null, $"Unsupported tool '{toolName}'.");
            }
        }
        catch (Exception ex)
        {
            return new ToolExecutionOutcome(false, toolName, "", false, null, $"Tool error: {ex.Message}");
        }
    }

    private static string GetStringArg(Dictionary<string, object?> args, string key, string fallback)
    {
        if (args.TryGetValue(key, out var v) && v != null)
        {
            return v.ToString() ?? fallback;
        }
        return fallback;
    }

    private static long GetLongArg(Dictionary<string, object?> args, string key, long fallback)
    {
        if (args.TryGetValue(key, out var v) && v != null && long.TryParse(v.ToString(), out long l))
        {
            return l;
        }
        return fallback;
    }

    private static bool GetBoolArg(Dictionary<string, object?> args, string key, bool fallback)
    {
        if (args.TryGetValue(key, out var v) && v != null && bool.TryParse(v.ToString(), out bool b))
        {
            return b;
        }
        return fallback;
    }
}
