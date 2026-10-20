using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore.Core.Security;

namespace CleanCore.Core.LargeFiles;

public interface ILargeFileManager
{
    Task<(bool Success, string Message)> SafeMoveFileAsync(
        string sourceFilePath,
        string destinationDirectory,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    bool SafeDeleteFile(string filePath, out string statusMessage);
}

public class LargeFileManager : ILargeFileManager
{
    public async Task<(bool Success, string Message)> SafeMoveFileAsync(
        string sourceFilePath,
        string destinationDirectory,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // 1. Safety check source path
        if (!PathSafetyGuard.IsPathSafeForDeletion(sourceFilePath, out string safetyError))
        {
            return (false, $"Source file is protected: {safetyError}");
        }

        if (!File.Exists(sourceFilePath))
        {
            return (false, "Source file does not exist.");
        }

        // 2. Check if source file is locked
        if (PathSafetyGuard.IsFileLocked(sourceFilePath, out string lockError))
        {
            return (false, $"Source file is locked: {lockError}");
        }

        var sourceInfo = new FileInfo(sourceFilePath);
        long sourceLength = sourceInfo.Length;

        // 3. Check destination directory and drive free space
        try
        {
            if (!Directory.Exists(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
        }
        catch (Exception ex)
        {
            return (false, $"Failed to create destination directory: {ex.Message}");
        }

        var destDriveRoot = Path.GetPathRoot(Path.GetFullPath(destinationDirectory));
        if (!string.IsNullOrWhiteSpace(destDriveRoot))
        {
            try
            {
                var destDriveInfo = new DriveInfo(destDriveRoot);
                if (destDriveInfo.AvailableFreeSpace < sourceLength + (100 * 1024 * 1024))
                {
                    return (false, $"Destination drive {destDriveRoot} does not have sufficient free space ({DriveModel.FormatBytes(destDriveInfo.AvailableFreeSpace)} available, requires {DriveModel.FormatBytes(sourceLength)}).");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Could not verify destination drive space: {ex.Message}");
            }
        }

        var destinationFilePath = Path.Combine(destinationDirectory, sourceInfo.Name);
        if (File.Exists(destinationFilePath))
        {
            return (false, $"Destination file already exists: '{destinationFilePath}'. Transfer aborted to prevent overwrite.");
        }

        // 4. Copy-Verify-Delete pattern
        string tempDestPath = destinationFilePath + ".cleancore_tmp";

        try
        {
            // A. Copy with progress
            using (var srcStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous))
            using (var dstStream = new FileStream(tempDestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[512 * 1024]; // 512KB buffer
                long totalBytesCopied = 0;
                int bytesRead;

                while ((bytesRead = await srcStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await dstStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalBytesCopied += bytesRead;
                    if (sourceLength > 0)
                    {
                        progress?.Report((double)totalBytesCopied / sourceLength * 100.0);
                    }
                }
            }

            // B. Verify size and cryptographic hash
            var destInfo = new FileInfo(tempDestPath);
            if (destInfo.Length != sourceLength)
            {
                try { File.Delete(tempDestPath); } catch { }
                return (false, $"Size mismatch after copy. Source: {sourceLength}, Copied: {destInfo.Length}. Aborted.");
            }

            // Cryptographic SHA-256 verification
            string sourceHash = await ComputeSha256Async(sourceFilePath, cancellationToken);
            string destHash = await ComputeSha256Async(tempDestPath, cancellationToken);

            if (!string.Equals(sourceHash, destHash, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(tempDestPath); } catch { }
                return (false, "SHA-256 checksum verification failed. The copied file does not match the source byte-for-byte. Transfer aborted.");
            }

            // C. Rename temp file to final target
            File.Move(tempDestPath, destinationFilePath);

            // D. Only now safely delete original source file
            File.Delete(sourceFilePath);

            return (true, $"Successfully verified and moved '{sourceInfo.Name}' to '{destinationDirectory}'.");
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(tempDestPath)) File.Delete(tempDestPath); } catch { }
            return (false, "File move was cancelled by user.");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempDestPath)) File.Delete(tempDestPath); } catch { }
            return (false, $"Move failed: {ex.Message}");
        }
    }

    public bool SafeDeleteFile(string filePath, out string statusMessage)
    {
        statusMessage = string.Empty;

        if (!PathSafetyGuard.IsPathSafeForDeletion(filePath, out string safetyError))
        {
            statusMessage = $"Safety rejection: {safetyError}";
            return false;
        }

        if (PathSafetyGuard.IsFileLocked(filePath, out string lockError))
        {
            statusMessage = $"Cannot delete locked file: {lockError}";
            return false;
        }

        try
        {
            File.Delete(filePath);
            statusMessage = $"Successfully deleted file: {filePath}";
            return true;
        }
        catch (Exception ex)
        {
            statusMessage = $"Deletion failed: {ex.Message}";
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        byte[] hash = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash);
    }
}
