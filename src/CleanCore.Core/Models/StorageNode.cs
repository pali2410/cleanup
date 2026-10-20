using System;
using System.Collections.Generic;
using System.IO;

namespace CleanCore.Core.Models;

public class StorageNode
{
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public long FileCount { get; set; }
    public long DirectoryCount { get; set; }
    public DateTime? LastModified { get; set; }
    public DateTime? LastAccessed { get; set; }
    public string Extension { get; set; } = string.Empty;
    public FileCategory Category { get; set; } = FileCategory.Unknown;
    public bool IsReparsePoint { get; set; } // Junction or symlink
    public bool IsPermissionDenied { get; set; }
    public bool IsHidden { get; set; }
    public bool IsSystem { get; set; }
    public List<StorageNode> Children { get; set; } = new();

    public string SizeDisplay => DriveModel.FormatBytes(SizeBytes);
    public string ModifiedDisplay => LastModified?.ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string TypeDisplay => IsDirectory ? "Folder" : (string.IsNullOrWhiteSpace(Extension) ? "File" : Extension.ToUpperInvariant());
    public string CategoryDisplay => Category.GetDisplayName();
    public string CategoryColor => Category.GetAccentColor();
    public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public bool HasPermissionDenied => IsPermissionDenied;
    public bool HasReparsePoint => IsReparsePoint;

    public string StatusBadge => IsPermissionDenied 
        ? "Access Denied" 
        : (IsReparsePoint ? "Link/Junction" : (IsDirectory ? $"{FileCount} files" : SizeDisplay));
}
