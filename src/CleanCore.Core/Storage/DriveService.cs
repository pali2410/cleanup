using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CleanCore.Core.Models;

namespace CleanCore.Core.Storage;

public interface IDriveService
{
    List<DriveModel> GetDrives(double criticalThresholdPercent = 5.0, double warningThresholdPercent = 10.0);
    DriveModel? GetDrive(string driveLetterOrPath, double criticalThresholdPercent = 5.0, double warningThresholdPercent = 10.0);
    bool HasCriticalSystemDrive(out DriveModel? systemDrive, double criticalThresholdPercent = 5.0);
}

public class DriveService : IDriveService
{
    public List<DriveModel> GetDrives(double criticalThresholdPercent = 5.0, double warningThresholdPercent = 10.0)
    {
        var result = new List<DriveModel>();
        var systemDriveLetter = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        try
        {
            var driveInfos = DriveInfo.GetDrives();
            foreach (var di in driveInfos)
            {
                if (!di.IsReady)
                {
                    result.Add(new DriveModel
                    {
                        Name = di.Name,
                        VolumeLabel = "Not Ready",
                        DriveType = di.DriveType.ToString(),
                        IsReady = false,
                        HealthStatus = "Offline / No Media"
                    });
                    continue;
                }

                var model = QueryDriveDetails(di.Name, systemDriveLetter, criticalThresholdPercent, warningThresholdPercent);
                result.Add(model);
            }
        }
        catch (Exception)
        {
            // Fallback to minimal C: query if enumerating all drives encounters security barriers
            var fallback = QueryDriveDetails("C:\\", systemDriveLetter, criticalThresholdPercent, warningThresholdPercent);
            result.Add(fallback);
        }

        return result;
    }

    public DriveModel? GetDrive(string driveLetterOrPath, double criticalThresholdPercent = 5.0, double warningThresholdPercent = 10.0)
    {
        var root = Path.GetPathRoot(driveLetterOrPath);
        if (string.IsNullOrWhiteSpace(root)) return null;

        var systemDriveLetter = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return QueryDriveDetails(root, systemDriveLetter, criticalThresholdPercent, warningThresholdPercent);
    }

    public bool HasCriticalSystemDrive(out DriveModel? systemDrive, double criticalThresholdPercent = 5.0)
    {
        var systemDriveLetter = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        systemDrive = GetDrive(systemDriveLetter, criticalThresholdPercent);
        return systemDrive != null && systemDrive.FreePercentage < criticalThresholdPercent;
    }

    private static DriveModel QueryDriveDetails(string rootPath, string systemDriveLetter, double criticalThresholdPercent, double warningThresholdPercent)
    {
        var model = new DriveModel
        {
            Name = rootPath.TrimEnd('\\') + "\\",
            IsSystemDrive = string.Equals(rootPath.TrimEnd('\\'), systemDriveLetter.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
            IsReady = true
        };

        // 1. Try Win32 GetDiskFreeSpaceExW for high-precision 64-bit cluster arithmetic
        if (NativeStorageMethods.GetDiskFreeSpaceExW(model.Name, out ulong freeCaller, out ulong totalBytes, out ulong totalFree))
        {
            model.TotalBytes = (long)totalBytes;
            model.FreeBytes = (long)totalFree;
            model.AvailableBytes = (long)freeCaller;
        }
        else
        {
            // Fallback to DriveInfo
            try
            {
                var di = new DriveInfo(model.Name);
                if (di.IsReady)
                {
                    model.TotalBytes = di.TotalSize;
                    model.FreeBytes = di.TotalFreeSpace;
                    model.AvailableBytes = di.AvailableFreeSpace;
                }
            }
            catch { }
        }

        // 2. Query Volume Information (Label and File System)
        var volName = new StringBuilder(261);
        var fsName = new StringBuilder(261);
        if (NativeStorageMethods.GetVolumeInformationW(
            model.Name,
            volName,
            (uint)volName.Capacity,
            out uint serial,
            out uint maxComp,
            out uint flags,
            fsName,
            (uint)fsName.Capacity))
        {
            model.VolumeLabel = volName.ToString();
            model.FileSystem = fsName.ToString();
        }
        else
        {
            try
            {
                var di = new DriveInfo(model.Name);
                model.VolumeLabel = di.VolumeLabel;
                model.FileSystem = di.DriveFormat;
            }
            catch { }
        }

        // 3. Query Drive Type
        uint typeCode = NativeStorageMethods.GetDriveTypeW(model.Name);
        model.DriveType = NativeStorageMethods.GetDriveTypeDescription(typeCode);

        // 4. Calculate Alert Levels based on thresholds
        if (model.FreePercentage < criticalThresholdPercent)
        {
            model.AlertLevel = DriveAlertLevel.Critical;
            model.HealthStatus = $"Critical Low Space (<{criticalThresholdPercent}%)";
        }
        else if (model.FreePercentage < warningThresholdPercent)
        {
            model.AlertLevel = DriveAlertLevel.Warning;
            model.HealthStatus = $"Warning: Low Space (<{warningThresholdPercent}%)";
        }
        else
        {
            model.AlertLevel = DriveAlertLevel.Healthy;
            model.HealthStatus = "Healthy";
        }

        return model;
    }
}
