using System;

namespace CleanCore.Core.Models;

public class AppInventoryItem
{
    public string DisplayName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string InstallDate { get; set; } = string.Empty;
    public string UninstallString { get; set; } = string.Empty;
    public string QuietUninstallString { get; set; } = string.Empty;
    public string RegistryKeyPath { get; set; } = string.Empty;
    public long RegisteredSizeBytes { get; set; }
    public long MeasuredFolderSizeBytes { get; set; }
    public long AssociatedCacheSizeBytes { get; set; }
    public bool IsMeasured { get; set; }
    public bool IsSystemComponent { get; set; }
    public string IconPath { get; set; } = string.Empty;

    public long TotalFootprintBytes => Math.Max(RegisteredSizeBytes, MeasuredFolderSizeBytes) + AssociatedCacheSizeBytes;
    public string FootprintDisplay => DriveModel.FormatBytes(TotalFootprintBytes);
    public string FolderSizeDisplay => MeasuredFolderSizeBytes > 0 ? DriveModel.FormatBytes(MeasuredFolderSizeBytes) : (RegisteredSizeBytes > 0 ? DriveModel.FormatBytes(RegisteredSizeBytes) : "Unknown");
    public string CacheSizeDisplay => AssociatedCacheSizeBytes > 0 ? DriveModel.FormatBytes(AssociatedCacheSizeBytes) : "0 B";
    public string PublisherDisplay => string.IsNullOrWhiteSpace(Publisher) ? "Unknown Publisher" : Publisher;
    public string VersionDisplay => string.IsNullOrWhiteSpace(DisplayVersion) ? "-" : DisplayVersion;
}
