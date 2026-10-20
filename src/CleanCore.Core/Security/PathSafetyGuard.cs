using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CleanCore.Core.Security;

public static class PathSafetyGuard
{
    private static readonly HashSet<string> ProtectedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys",
        "swapfile.sys",
        "hiberfil.sys",
        "dumpstack.log",
        "dumpstack.log.tmp",
        "bootmgr",
        "bootnxt",
        "BOOTSECT.BAK",
        "ntldr",
        "NTDETECT.COM"
    };

    private static readonly string[] ProtectedDirectoryPatterns =
    [
        @"\Windows\System32\config",
        @"\Windows\WinSxS",
        @"\Windows\System32\drivers",
        @"\Windows\System32\DriverStore",
        @"\Windows\Boot",
        @"\Recovery",
        @"\System Volume Information",
        @"\$Recycle.Bin" // Protected against blanket deletion; must use specific recycle bin API
    ];

    public static bool IsPathSafeForDeletion(string targetPath, out string rejectionReason)
    {
        rejectionReason = string.Empty;

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            rejectionReason = "Path is empty or null.";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(targetPath);
        }
        catch (Exception ex)
        {
            rejectionReason = $"Invalid path format: {ex.Message}";
            return false;
        }

        // 1. Check for directory traversal tricks
        if (targetPath.Contains(".."))
        {
            rejectionReason = "Path traversal sequences ('..') are strictly prohibited.";
            return false;
        }

        // 2. Reject drive roots (e.g. C:\, D:\)
        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(fullPath.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = "Cannot delete volume root.";
            return false;
        }

        // 3. Reject critical system root files
        var fileName = Path.GetFileName(fullPath);
        if (ProtectedRootFiles.Contains(fileName))
        {
            rejectionReason = $"'{fileName}' is a critical protected Windows system file.";
            return false;
        }

        // 4. Reject critical Windows and System directories directly
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.Equals(fullPath, winDir, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, sysDir, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, progFiles, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, progFilesX86, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, userProfile, StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = "Target is a top-level system or user profile root directory.";
            return false;
        }

        // 5. Reject specific protected subdirectories
        foreach (var pattern in ProtectedDirectoryPatterns)
        {
            if (fullPath.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                rejectionReason = $"Path resides inside protected Windows subsystem location ('{pattern}').";
                return false;
            }
        }

        return true;
    }

    public static bool IsFileLocked(string filePath, out string lockingReason)
    {
        lockingReason = string.Empty;
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException ex)
        {
            lockingReason = $"File is in use by another process: {ex.Message}";
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            lockingReason = $"Permission denied / Access restricted: {ex.Message}";
            return true;
        }
        catch (Exception ex)
        {
            lockingReason = ex.Message;
            return true;
        }
    }

    public static bool RevalidateFile(string filePath, long expectedSize, out string error)
    {
        error = string.Empty;
        if (!File.Exists(filePath))
        {
            error = "File no longer exists on disk (time-of-check changed).";
            return false;
        }

        try
        {
            var info = new FileInfo(filePath);
            if (info.Length != expectedSize)
            {
                error = $"File size changed from {expectedSize} to {info.Length} bytes since scan.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not revalidate file: {ex.Message}";
            return false;
        }
    }
}
