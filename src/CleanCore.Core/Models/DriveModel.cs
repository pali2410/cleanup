using System;

namespace CleanCore.Core.Models;

public enum DriveAlertLevel
{
    Healthy,
    Warning,
    Critical
}

public class DriveModel
{
    public string Name { get; set; } = string.Empty; // e.g. "C:\"
    public string VolumeLabel { get; set; } = string.Empty;
    public string DriveType { get; set; } = string.Empty; // Fixed, Removable, Network, etc.
    public string FileSystem { get; set; } = string.Empty; // NTFS, FAT32, exFAT, ReFS
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long AvailableBytes { get; set; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercentage => TotalBytes > 0 ? (double)UsedBytes / TotalBytes * 100.0 : 0.0;
    public double FreePercentage => TotalBytes > 0 ? (double)FreeBytes / TotalBytes * 100.0 : 0.0;
    public DriveAlertLevel AlertLevel { get; set; } = DriveAlertLevel.Healthy;
    public string HealthStatus { get; set; } = "Normal";
    public string ScanStatus { get; set; } = "Ready";
    public DateTime? LastScanTime { get; set; }
    public bool IsSystemDrive { get; set; }
    public bool IsReady { get; set; } = true;

    public string TotalDisplay => FormatBytes(TotalBytes);
    public string FreeDisplay => FormatBytes(FreeBytes);
    public string UsedDisplay => FormatBytes(UsedBytes);
    public string FreePercentageDisplay => $"{FreePercentage:F1}% Free";
    public string UsedPercentageDisplay => $"{UsedPercentage:F1}% Used";
    public string DisplayTitle => string.IsNullOrWhiteSpace(VolumeLabel) 
        ? $"Local Disk ({Name.TrimEnd('\\')})" 
        : $"{VolumeLabel} ({Name.TrimEnd('\\')})";

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}
