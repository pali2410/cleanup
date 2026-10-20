using System;

namespace CleanCore.Core.Models;

public class LargeFileEntry
{
    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Extension { get; set; } = string.Empty;
    public FileCategory Category { get; set; } = FileCategory.Unknown;
    public DateTime? LastModified { get; set; }
    public DateTime? LastAccessed { get; set; }
    public bool IsTemporaryCandidate { get; set; }
    public bool IsDuplicateCandidate { get; set; }
    public double Confidence { get; set; } = 1.0;
    public bool IsExcluded { get; set; }

    public string SizeDisplay => DriveModel.FormatBytes(SizeBytes);
    public string ModifiedDisplay => LastModified?.ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string CategoryDisplay => Category.GetDisplayName();
    public string ConfidencePercent => $"{Confidence * 100:F0}%";
}
