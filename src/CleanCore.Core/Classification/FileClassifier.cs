using System;
using System.Collections.Generic;
using System.IO;
using CleanCore.Core.Models;

namespace CleanCore.Core.Classification;

public class ClassificationResult
{
    public FileCategory Category { get; set; } = FileCategory.Unknown;
    public double Confidence { get; set; } = 0.5;
    public string Explanation { get; set; } = string.Empty;
    public bool IsSafeToDeleteCandidate { get; set; }
}

public static class FileClassifier
{
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
        ".txt", ".rtf", ".csv", ".tsv", ".md", ".epub"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".svg", ".ico", ".raw", ".heic", ".psd"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".3gp", ".ts"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".alac"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".img", ".cab"
    };

    private static readonly HashSet<string> SourceCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".cpp", ".c", ".h", ".hpp", ".py", ".js", ".ts", ".jsx", ".tsx",
        ".java", ".kt", ".go", ".rs", ".rb", ".php", ".html", ".css", ".scss", ".json", ".xml", ".yaml", ".yml", ".sql"
    };

    private static readonly HashSet<string> VirtualMachineExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vmdk", ".vdi", ".vhd", ".vhdx", ".qcow2", ".ova", ".ovf"
    };

    private static readonly HashSet<string> BackupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bak", ".backup", ".bkp", ".old", ".gho", ".tib", ".wim"
    };

    private static readonly HashSet<string> LogExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".dmp", ".etl", ".evtx", ".cbs", ".wer"
    };

    private static readonly HashSet<string> TempExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".chk", ".part", ".crdownload"
    };

    public static ClassificationResult Classify(string fullPath, long sizeBytes, bool isDirectory)
    {
        var result = new ClassificationResult();
        string normalized = fullPath.Replace('/', '\\');

        // Check Windows Operating System locations
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (normalized.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.IndexOf(@"\Temp", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Category = FileCategory.TemporaryFiles;
                result.Confidence = 0.90;
                result.Explanation = "Located in Windows system temporary directory.";
                result.IsSafeToDeleteCandidate = true;
                return result;
            }
            if (normalized.IndexOf(@"\Logs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\SoftwareDistribution\Download", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Category = FileCategory.Logs;
                result.Confidence = 0.85;
                result.Explanation = "Windows system servicing or telemetry log file.";
                result.IsSafeToDeleteCandidate = false; // requires system provider
                return result;
            }

            result.Category = FileCategory.OperatingSystem;
            result.Confidence = 0.98;
            result.Explanation = "Located in protected Windows operating system directory.";
            result.IsSafeToDeleteCandidate = false;
            return result;
        }

        // Check Program Files (Installed Apps)
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (normalized.StartsWith(progFiles, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(progFilesX86, StringComparison.OrdinalIgnoreCase))
        {
            result.Category = FileCategory.InstalledApps;
            result.Confidence = 0.95;
            result.Explanation = "Installed application binary/resource directory.";
            result.IsSafeToDeleteCandidate = false;
            return result;
        }

        // Check User Profile paths
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (normalized.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            // AppData Caches & Temp
            if (normalized.IndexOf(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Category = FileCategory.TemporaryFiles;
                result.Confidence = 0.95;
                result.Explanation = "Located in user temporary folder (%TEMP%).";
                result.IsSafeToDeleteCandidate = true;
                return result;
            }

            if (normalized.IndexOf(@"\Cache", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\CachedData", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\GPUCache", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\Code Cache", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Category = FileCategory.ApplicationCaches;
                result.Confidence = 0.90;
                result.Explanation = "Identified as an application cache storage location.";
                result.IsSafeToDeleteCandidate = true;
                return result;
            }

            // Developer Build Outputs
            if (normalized.IndexOf(@"\node_modules", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\bin\Debug", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\bin\Release", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\obj\Debug", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\obj\Release", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\.gradle\caches", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\.nuget\packages", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Category = FileCategory.BuildOutputs;
                result.Confidence = 0.92;
                result.Explanation = "Developer build output or local package cache repository.";
                result.IsSafeToDeleteCandidate = false; // requires explicit user confirmation
                return result;
            }

            // Downloads folder
            var downloadsDir = Path.Combine(userProfile, "Downloads");
            if (normalized.StartsWith(downloadsDir, StringComparison.OrdinalIgnoreCase))
            {
                result.Category = FileCategory.Downloads;
                result.Confidence = 0.85;
                result.Explanation = "Located in user Downloads folder.";
                result.IsSafeToDeleteCandidate = false; // user data, needs review
                return result;
            }
        }

        // Check File Extension for standalone files
        string ext = Path.GetExtension(fullPath);
        if (!string.IsNullOrWhiteSpace(ext))
        {
            if (VirtualMachineExtensions.Contains(ext))
            {
                result.Category = FileCategory.VirtualMachines;
                result.Confidence = 0.95;
                result.Explanation = $"Virtual machine disk or image container ({ext}).";
                return result;
            }
            if (TempExtensions.Contains(ext))
            {
                result.Category = FileCategory.TemporaryFiles;
                result.Confidence = 0.88;
                result.Explanation = $"Temporary scratch or download in-progress file ({ext}).";
                result.IsSafeToDeleteCandidate = true;
                return result;
            }
            if (LogExtensions.Contains(ext))
            {
                result.Category = FileCategory.Logs;
                result.Confidence = 0.85;
                result.Explanation = $"Diagnostic log or crash memory dump ({ext}).";
                result.IsSafeToDeleteCandidate = true;
                return result;
            }
            if (BackupExtensions.Contains(ext))
            {
                result.Category = FileCategory.Backups;
                result.Confidence = 0.90;
                result.Explanation = $"System or application backup file ({ext}).";
                return result;
            }
            if (VideoExtensions.Contains(ext))
            {
                result.Category = FileCategory.Videos;
                result.Confidence = 0.95;
                result.Explanation = $"Media video file ({ext}).";
                return result;
            }
            if (AudioExtensions.Contains(ext))
            {
                result.Category = FileCategory.Audio;
                result.Confidence = 0.95;
                result.Explanation = $"Audio recording or music file ({ext}).";
                return result;
            }
            if (ImageExtensions.Contains(ext))
            {
                result.Category = FileCategory.Images;
                result.Confidence = 0.95;
                result.Explanation = $"Image or photo asset ({ext}).";
                return result;
            }
            if (ArchiveExtensions.Contains(ext))
            {
                result.Category = FileCategory.Archives;
                result.Confidence = 0.90;
                result.Explanation = $"Compressed archive container ({ext}).";
                return result;
            }
            if (DocumentExtensions.Contains(ext))
            {
                result.Category = FileCategory.UserDocuments;
                result.Confidence = 0.92;
                result.Explanation = $"Office or personal document ({ext}).";
                return result;
            }
            if (SourceCodeExtensions.Contains(ext))
            {
                result.Category = FileCategory.Development;
                result.Confidence = 0.90;
                result.Explanation = $"Software development source file ({ext}).";
                return result;
            }
        }

        // Unclassified / Needs Review
        result.Category = FileCategory.Unknown;
        result.Confidence = 0.30;
        result.Explanation = "Unrecognized file structure or purpose. Review carefully before cleanup.";
        result.IsSafeToDeleteCandidate = false;
        return result;
    }
}
