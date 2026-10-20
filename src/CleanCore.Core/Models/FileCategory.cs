namespace CleanCore.Core.Models;

public enum FileCategory
{
    OperatingSystem,
    InstalledApps,
    UserDocuments,
    Downloads,
    Images,
    Videos,
    Audio,
    Archives,
    Development,
    BuildOutputs,
    ApplicationCaches,
    TemporaryFiles,
    Logs,
    VirtualMachines,
    Backups,
    DuplicateCandidates,
    Unknown
}

public static class FileCategoryExtensions
{
    public static string GetDisplayName(this FileCategory category) => category switch
    {
        FileCategory.OperatingSystem => "Operating System",
        FileCategory.InstalledApps => "Installed Apps",
        FileCategory.UserDocuments => "Documents & Office",
        FileCategory.Downloads => "Downloads",
        FileCategory.Images => "Images & Photos",
        FileCategory.Videos => "Videos & Movies",
        FileCategory.Audio => "Audio & Music",
        FileCategory.Archives => "Archives & Compressed",
        FileCategory.Development => "Source Code & Repos",
        FileCategory.BuildOutputs => "Build Outputs (bin/obj/node_modules)",
        FileCategory.ApplicationCaches => "App Caches & Data",
        FileCategory.TemporaryFiles => "Temporary Files",
        FileCategory.Logs => "Log Files & Dumps",
        FileCategory.VirtualMachines => "VMs & Disk Images",
        FileCategory.Backups => "Backups & System Images",
        FileCategory.DuplicateCandidates => "Duplicate Candidates",
        FileCategory.Unknown => "Other / Needs Review",
        _ => "Other"
    };

    public static string GetAccentColor(this FileCategory category) => category switch
    {
        FileCategory.OperatingSystem => "#0078D4",       // Windows Blue
        FileCategory.InstalledApps => "#6B69D6",         // Purple
        FileCategory.UserDocuments => "#107C41",         // Green
        FileCategory.Downloads => "#00B7C3",             // Cyan
        FileCategory.Images => "#EA4335",                // Red/Coral
        FileCategory.Videos => "#8764B8",                // Violet
        FileCategory.Audio => "#E3008C",                 // Pink
        FileCategory.Archives => "#D83B01",              // Orange
        FileCategory.Development => "#008272",           // Teal
        FileCategory.BuildOutputs => "#F7630C",           // Bright Orange
        FileCategory.ApplicationCaches => "#E81123",      // Crimson
        FileCategory.TemporaryFiles => "#B146C2",        // Purple/Magenta
        FileCategory.Logs => "#7A7574",                  // Gray
        FileCategory.VirtualMachines => "#498205",       // Olive Green
        FileCategory.Backups => "#004E8C",               // Navy
        FileCategory.DuplicateCandidates => "#FF8C00",   // Dark Orange
        FileCategory.Unknown => "#8A8886",               // Neutral Gray
        _ => "#8A8886"
    };
}
