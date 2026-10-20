using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore.Core.Security;

namespace CleanCore.Core.Quarantine;

public interface IQuarantineManager
{
    string QuarantineRootDirectory { get; }
    Task<QuarantineRecord?> QuarantineFileAsync(string sourceFilePath, string jobId, CancellationToken cancellationToken);
    Task<(bool Success, string ErrorMessage)> RestoreFileAsync(QuarantineRecord record);
    List<QuarantineRecord> GetQuarantineRecords();
    bool PurgeQuarantineRecord(QuarantineRecord record, out string errorMessage);
    long GetTotalQuarantineSizeBytes();
}

public class QuarantineManager : IQuarantineManager
{
    public string QuarantineRootDirectory { get; }

    public QuarantineManager()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        QuarantineRootDirectory = Path.Combine(localAppData, "CleanCore", "Quarantine");
        Directory.CreateDirectory(QuarantineRootDirectory);
    }

    public async Task<QuarantineRecord?> QuarantineFileAsync(string sourceFilePath, string jobId, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourceFilePath)) return null;

        if (!PathSafetyGuard.IsPathSafeForDeletion(sourceFilePath, out _))
        {
            // Do not quarantine protected system files
            return null;
        }

        if (PathSafetyGuard.IsFileLocked(sourceFilePath, out _))
        {
            return null;
        }

        try
        {
            var fileInfo = new FileInfo(sourceFilePath);
            long fileLength = fileInfo.Length;
            string sha256 = await ComputeSha256Async(sourceFilePath, cancellationToken);

            string jobDir = Path.Combine(QuarantineRootDirectory, jobId);
            Directory.CreateDirectory(jobDir);

            string recordId = Guid.NewGuid().ToString("N");
            string targetFileName = $"{recordId}_{fileInfo.Name}";
            string quarantinedPath = Path.Combine(jobDir, targetFileName);

            // Move file into quarantine
            File.Move(sourceFilePath, quarantinedPath);

            var record = new QuarantineRecord
            {
                RecordId = recordId,
                JobId = jobId,
                OriginalPath = sourceFilePath,
                QuarantinedPath = quarantinedPath,
                FileName = fileInfo.Name,
                FileSizeBytes = fileLength,
                Sha256Hash = sha256,
                QuarantinedAt = DateTime.Now,
                IsRestored = false
            };

            // Write metadata manifest alongside quarantined file
            string manifestPath = quarantinedPath + ".manifest.json";
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

            return record;
        }
        catch
        {
            return null;
        }
    }

    public async Task<(bool Success, string ErrorMessage)> RestoreFileAsync(QuarantineRecord record)
    {
        if (!File.Exists(record.QuarantinedPath))
        {
            return (false, "Quarantined file was not found in storage.");
        }

        if (File.Exists(record.OriginalPath))
        {
            return (false, $"A file already exists at original path '{record.OriginalPath}'. Cannot overwrite.");
        }

        try
        {
            // Verify checksum before restoring
            string currentHash = await ComputeSha256Async(record.QuarantinedPath, CancellationToken.None);
            if (!string.Equals(currentHash, record.Sha256Hash, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Integrity mismatch: The quarantined file's SHA-256 hash has changed.");
            }

            var originalDir = Path.GetDirectoryName(record.OriginalPath);
            if (!string.IsNullOrWhiteSpace(originalDir) && !Directory.Exists(originalDir))
            {
                Directory.CreateDirectory(originalDir);
            }

            File.Move(record.QuarantinedPath, record.OriginalPath);
            record.IsRestored = true;

            // Update manifest
            string manifestPath = record.QuarantinedPath + ".manifest.json";
            if (File.Exists(manifestPath))
            {
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"Restoration error: {ex.Message}");
        }
    }

    public List<QuarantineRecord> GetQuarantineRecords()
    {
        var records = new List<QuarantineRecord>();
        if (!Directory.Exists(QuarantineRootDirectory)) return records;

        try
        {
            var manifestFiles = Directory.GetFiles(QuarantineRootDirectory, "*.manifest.json", SearchOption.AllDirectories);
            foreach (var mf in manifestFiles)
            {
                try
                {
                    string json = File.ReadAllText(mf);
                    var rec = JsonSerializer.Deserialize<QuarantineRecord>(json);
                    if (rec != null)
                    {
                        records.Add(rec);
                    }
                }
                catch { }
            }
        }
        catch { }

        return records;
    }

    public bool PurgeQuarantineRecord(QuarantineRecord record, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            if (File.Exists(record.QuarantinedPath))
            {
                File.Delete(record.QuarantinedPath);
            }
            string manifestPath = record.QuarantinedPath + ".manifest.json";
            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public long GetTotalQuarantineSizeBytes()
    {
        long total = 0;
        try
        {
            if (Directory.Exists(QuarantineRootDirectory))
            {
                var dir = new DirectoryInfo(QuarantineRootDirectory);
                foreach (var f in dir.GetFiles("*", SearchOption.AllDirectories))
                {
                    if (!f.Name.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        total += f.Length;
                    }
                }
            }
        }
        catch { }
        return total;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Length > 20 * 1024 * 1024)
            {
                return $"QUICK-{fi.Length:X}-{fi.LastWriteTimeUtc.Ticks:X}";
            }

            using var sha = SHA256.Create();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            byte[] hash = await sha.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return "UNAVAILABLE";
        }
    }
}
