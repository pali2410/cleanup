using System;
using System.Collections.Generic;
using System.Linq;

namespace CleanCore.Core.Models;

public enum CleanupActionType
{
    DeleteFile,
    DeleteDirectory,
    MoveToQuarantine,
    EmptyRecycleBin,
    ExecuteSystemUtility
}

public class CleanupCandidate : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public CleanupActionType ActionType { get; set; } = CleanupActionType.DeleteFile;
    public long SizeBytes { get; set; }
    public string Category { get; set; } = "Cache";
    public CleanupRiskLevel RiskLevel { get; set; } = CleanupRiskLevel.Safe;
    public double ConfidenceScore { get; set; } = 1.0;
    public string Reason { get; set; } = string.Empty;
    public bool RequiresElevation { get; set; }
    public string ExpectedSideEffects { get; set; } = "None";
    public bool IsReversible { get; set; } = true;
    public string CleanupMethod { get; set; } = "Filesystem Safe Delete";
    public string VerificationMethod { get; set; } = "Post-check existence & drive free space";
    public bool IsLocked { get; set; }
    public string LockReason { get; set; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? SelectionChanged;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    public string SizeDisplay => DriveModel.FormatBytes(SizeBytes);
    public string ConfidencePercent => $"{ConfidenceScore * 100:F0}%";
    public string ReversibleDisplay => IsReversible ? "Reversible (Quarantine)" : "Permanent";
}

public class CleanupPlan
{
    public string PlanId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<CleanupCandidate> Candidates { get; set; } = new();

    public long TotalSelectedBytes => Candidates.Where(c => c.IsSelected).Sum(c => c.SizeBytes);
    public int SelectedCount => Candidates.Count(c => c.IsSelected);
    public int CandidateCount => Candidates.Count;
    public long SafeSelectedBytes => Candidates.Where(c => c.IsSelected && c.RiskLevel == CleanupRiskLevel.Safe).Sum(c => c.SizeBytes);
    public long ReviewRequiredBytes => Candidates.Where(c => c.IsSelected && c.RiskLevel != CleanupRiskLevel.Safe).Sum(c => c.SizeBytes);
    public long ProtectedSpaceExcludedBytes { get; set; }

    public string TotalSelectedDisplay => DriveModel.FormatBytes(TotalSelectedBytes);
    public string SafeSelectedDisplay => DriveModel.FormatBytes(SafeSelectedBytes);
    public string ReviewRequiredDisplay => DriveModel.FormatBytes(ReviewRequiredBytes);
    public string ExcludedDisplay => DriveModel.FormatBytes(ProtectedSpaceExcludedBytes);
}

public class CleanupTargetResult
{
    public string CandidateId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool Success { get; set; }
    public long BytesRecovered { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public bool WasLocked { get; set; }
    public bool WasQuarantined { get; set; }
    public string QuarantinedPath { get; set; } = string.Empty;
}

public class CleanupJobResult
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime CompletedAt { get; set; }
    public TimeSpan Duration => CompletedAt - StartedAt;
    public int TotalTargets { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int SkippedLockedCount { get; set; }
    public long TotalBytesRecovered { get; set; }
    public List<CleanupTargetResult> Results { get; set; } = new();
    public string Status { get; set; } = "Completed";

    public string BytesRecoveredDisplay => DriveModel.FormatBytes(TotalBytesRecovered);
    public string StartedAtDisplay => StartedAt.ToString("yyyy-MM-dd HH:mm:ss");
}

public class QuarantineRecord
{
    public string RecordId { get; set; } = Guid.NewGuid().ToString("N");
    public string JobId { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string QuarantinedPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Sha256Hash { get; set; } = string.Empty;
    public DateTime QuarantinedAt { get; set; } = DateTime.Now;
    public bool IsRestored { get; set; }
    public bool CanRestore => !IsRestored;

    public string SizeDisplay => DriveModel.FormatBytes(FileSizeBytes);
    public string TimeDisplay => QuarantinedAt.ToString("yyyy-MM-dd HH:mm:ss");
}

public class ScanProgressReport
{
    public string CurrentDrive { get; set; } = string.Empty;
    public string CurrentDirectory { get; set; } = string.Empty;
    public long FilesDiscovered { get; set; }
    public long DirectoriesScanned { get; set; }
    public long BytesScanned { get; set; }
    public long InaccessibleCount { get; set; }
    public string CurrentPhase { get; set; } = "Scanning...";
    public double? EstimatedPercent { get; set; }
    public TimeSpan Elapsed { get; set; }

    public string BytesDisplay => DriveModel.FormatBytes(BytesScanned);
    public string SpeedDisplay => Elapsed.TotalSeconds > 0 
        ? $"{FilesDiscovered / Math.Max(0.1, Elapsed.TotalSeconds):F0} files/s" 
        : "0 files/s";
}
